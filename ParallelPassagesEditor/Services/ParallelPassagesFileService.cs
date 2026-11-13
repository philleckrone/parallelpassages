using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using ParallelPassagesEditor.Models;

namespace ParallelPassagesEditor.Services
{
    /// <summary>
    /// An open ParallelPassages.xml, held as raw text lines so that everything except the
    /// &lt;Verse&gt; lines whose hidden state changed is written back byte-for-byte.
    /// </summary>
    public class ParallelPassagesDocument
    {
        public string Path;
        public Encoding Encoding;          // carries the BOM decision via GetPreamble()
        public List<string> Lines = new List<string>();
        public List<string> Terminators = new List<string>();   // "\r\n", "\n", "\r" or "" per line
        public List<VerseLine> Verses = new List<VerseLine>();
        public List<BookNode> Books = new List<BookNode>();
        public List<PassageGroup> Passages = new List<PassageGroup>();
        public long FileLength;
        public DateTime LastWriteUtc;

        /// <summary>Passages were added, changed or removed since the last load or save.</summary>
        public bool StructureChanged;

        /// <summary>Passages added with this editor (see PassageGroup.IsUserAdded).</summary>
        public int UserAddedCount
        {
            get
            {
                int n = 0;
                foreach (PassageGroup p in Passages)
                    if (p.IsUserAdded) n++;
                return n;
            }
        }

        /// <summary>Entries hidden in the file as it will be written.</summary>
        public int HiddenCount
        {
            get
            {
                int n = 0;
                foreach (VerseLine v in Verses)
                    if (v.IsEffectivelyHidden) n++;
                return n;
            }
        }

        /// <summary>Entries the user asked to hide (including kept ones).</summary>
        public int RequestedHiddenCount
        {
            get
            {
                int n = 0;
                foreach (VerseLine v in Verses)
                    if (v.IsHidden) n++;
                return n;
            }
        }

        public int WithPassageCount
        {
            get
            {
                int n = 0;
                foreach (VerseLine v in Verses)
                    if (v.IsHiddenWithPassage) n++;
                return n;
            }
        }

        public int PassagesHiddenEntirelyCount
        {
            get
            {
                int n = 0;
                foreach (PassageGroup p in Passages)
                    if (p.IsHiddenEntirely) n++;
                return n;
            }
        }

        /// <summary>
        /// Passages that, as last read from or written to disk, are still open (their &lt;Passage&gt;
        /// line is not commented out) but have something hidden and fewer than 2 visible entries,
        /// for example a file saved by the first version of this tool.
        /// </summary>
        public int BrokenPassageCountOnDisk
        {
            get
            {
                int n = 0;
                foreach (PassageGroup p in Passages)
                {
                    if (p.OpenLine != null && p.OpenLine.SavedHidden) continue;
                    int savedVisible = 0;
                    bool anySavedHidden = false;
                    foreach (VerseLine v in p.Lines)
                    {
                        if (v.SavedHidden) anySavedHidden = true;
                        else savedVisible++;
                    }
                    if (anySavedHidden && savedVisible < PassageGroup.MinVisible) n++;
                }
                return n;
            }
        }

        public bool IsDirty
        {
            get
            {
                if (StructureChanged) return true;
                foreach (VerseLine v in Verses)
                    if (v.IsChanged) return true;
                foreach (PassageGroup p in Passages)
                    if ((p.OpenLine != null && p.OpenLine.IsChanged) || (p.CloseLine != null && p.CloseLine.IsChanged))
                        return true;
                return false;
            }
        }
    }

    public static class ParallelPassagesFileService
    {
        // <Verse HEB="0101">GEN 1:27</Verse>, <Verse GRK="...">MRK 15:42-45</Verse>, <Verse ...>2CH 8:13,16</Verse>
        private static readonly Regex VisibleRe = new Regex(
            @"^\s*<Verse\b(?<attrs>[^>]*)>\s*(?<ref>(?<book>[1-4A-Z]{3})\s+(?<ch>\d+)\s*:[^<]*?)\s*</Verse>\s*$",
            RegexOptions.CultureInvariant);

        // <!--    <Verse ...>JHN 20:1</Verse> -->   (one hidden entry on one line)
        private static readonly Regex HiddenRe = new Regex(
            @"^\s*<!--(?<inner>.*?)\s*-->\s*$",
            RegexOptions.CultureInvariant);

        private static readonly Regex LanguageRe = new Regex(@"^\s*(?<lang>[A-Za-z_][\w.-]*)\s*=");

        private static readonly Regex PassageOpenRe = new Regex(@"<Passage[\s>]");

        // A <Passage> or </Passage> line on its own (also found inside a single-line comment).
        private static readonly Regex PassageMarkerRe = new Regex(@"^\s*<(?<close>/)?Passage\s*>\s*$", RegexOptions.CultureInvariant);

        private static readonly Regex PassagesCloseRe = new Regex(@"^\s*</Passages\s*>\s*$", RegexOptions.CultureInvariant);

        /// <summary>The comment written before each passage added with this editor.</summary>
        public const string AddedMarker = "<!-- Added by Parallel Passages Editor -->";

        // -- Load ---------------------------------------------------------------

        public static ParallelPassagesDocument Load(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            var doc = new ParallelPassagesDocument();
            doc.Path = path;

            int skip;
            doc.Encoding = DetectEncoding(bytes, out skip);
            string text = doc.Encoding.GetString(bytes, skip, bytes.Length - skip);
            SplitLines(text, doc.Lines, doc.Terminators);

            var info = new FileInfo(path);
            doc.FileLength = info.Length;
            doc.LastWriteUtc = info.LastWriteTimeUtc;

            ParseVerses(doc);
            BuildTree(doc);
            return doc;
        }

        private static Encoding DetectEncoding(byte[] b, out int skip)
        {
            if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF)
            {
                skip = 3;
                return new UTF8Encoding(true);
            }
            if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE)
            {
                skip = 2;
                return new UnicodeEncoding(false, true);
            }
            if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF)
            {
                skip = 2;
                return new UnicodeEncoding(true, true);
            }
            skip = 0;
            return new UTF8Encoding(false);
        }

        private static void SplitLines(string text, List<string> lines, List<string> terms)
        {
            int start = 0;
            int i = 0;
            while (i < text.Length)
            {
                char c = text[i];
                if (c == '\r' || c == '\n')
                {
                    lines.Add(text.Substring(start, i - start));
                    if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                    {
                        terms.Add("\r\n");
                        i += 2;
                    }
                    else
                    {
                        terms.Add(c.ToString());
                        i += 1;
                    }
                    start = i;
                }
                else
                {
                    i++;
                }
            }
            if (start < text.Length)
            {
                lines.Add(text.Substring(start));
                terms.Add("");
            }
        }

        private static void ParseVerses(ParallelPassagesDocument doc)
        {
            bool inComment = false;
            int passage = -1;
            var openLines = new Dictionary<int, MarkerLine>();
            var closeLines = new Dictionary<int, MarkerLine>();
            var addedMarkers = new Dictionary<int, int>();

            for (int i = 0; i < doc.Lines.Count; i++)
            {
                string line = doc.Lines[i];

                if (!inComment)
                {
                    // <Passage> / </Passage>, visible or commented out on its own line.
                    bool isClose;
                    MarkerLine marker = TryParseMarker(line, out isClose);
                    if (marker != null)
                    {
                        marker.LineIndex = i;
                        if (!isClose)
                        {
                            passage++;
                            openLines[passage] = marker;
                            if (i > 0 && string.Equals(doc.Lines[i - 1].Trim(), AddedMarker, StringComparison.Ordinal))
                                addedMarkers[passage] = i - 1;
                        }
                        else if (passage >= 0 && !closeLines.ContainsKey(passage))
                        {
                            closeLines[passage] = marker;
                        }
                        continue;
                    }

                    VerseLine v = TryParseVerse(line);
                    if (v != null)
                    {
                        v.LineIndex = i;
                        v.PassageIndex = passage;
                        doc.Verses.Add(v);
                        continue;
                    }
                }

                // Any other line: track multi-line comments (verse lines inside one are left alone)
                // and count <Passage> openings outside comments.
                inComment = ScanLine(line, inComment, ref passage);
            }

            // Group the entries of each passage and fill in "parallel with" text.
            var byPassage = doc.Verses.GroupBy(v => v.PassageIndex);
            foreach (var group in byPassage)
            {
                List<VerseLine> members = group.ToList();
                PassageGroup passageGroup = null;
                if (group.Key >= 0)
                {
                    passageGroup = new PassageGroup { Index = group.Key };
                    MarkerLine m;
                    if (openLines.TryGetValue(group.Key, out m)) { passageGroup.OpenLine = m; m.Passage = passageGroup; }
                    if (closeLines.TryGetValue(group.Key, out m)) { passageGroup.CloseLine = m; m.Passage = passageGroup; }
                    int addedLine;
                    if (addedMarkers.TryGetValue(group.Key, out addedLine)) passageGroup.AddedMarkerLineIndex = addedLine;
                    doc.Passages.Add(passageGroup);
                }
                foreach (VerseLine v in members)
                {
                    v.Passage = passageGroup;
                    if (passageGroup != null) passageGroup.Lines.Add(v);
                    var others = members.Where(o => o != v).Select(o => o.Reference).ToArray();
                    v.ParallelWith = string.Join(", ", others);
                }
            }
        }

        private static bool ScanLine(string line, bool inComment, ref int passage)
        {
            int pos = 0;
            while (pos < line.Length)
            {
                if (inComment)
                {
                    int end = line.IndexOf("-->", pos, StringComparison.Ordinal);
                    if (end < 0) return true;
                    inComment = false;
                    pos = end + 3;
                }
                else
                {
                    int open = line.IndexOf("<!--", pos, StringComparison.Ordinal);
                    string segment = open < 0 ? line.Substring(pos) : line.Substring(pos, open - pos);
                    passage += PassageOpenRe.Matches(segment).Count;
                    if (open < 0) return false;
                    inComment = true;
                    pos = open + 4;
                }
            }
            return inComment;
        }

        /// <summary>A &lt;Passage&gt; or &lt;/Passage&gt; line (visible or commented out on its own), or null.</summary>
        public static MarkerLine TryParseMarker(string line, out bool isClose)
        {
            isClose = false;
            bool hidden = false;
            string visible = line;
            Match h = HiddenRe.Match(line);
            if (h.Success)
            {
                visible = h.Groups["inner"].Value;
                hidden = true;
            }
            Match m = PassageMarkerRe.Match(visible);
            if (!m.Success) return null;
            isClose = m.Groups["close"].Success;
            var marker = new MarkerLine();
            marker.VisibleText = visible.TrimEnd();
            marker.SavedText = line;
            marker.SavedHidden = hidden;
            return marker;
        }

        /// <summary>Returns a VerseLine (without LineIndex/PassageIndex) or null if the line is not a Verse entry.</summary>
        public static VerseLine TryParseVerse(string line)
        {
            bool hidden = false;
            bool withPassage = false;
            string visible = line;

            Match h = HiddenRe.Match(line);
            if (h.Success)
            {
                visible = h.Groups["inner"].Value;
                hidden = true;
                // "(with passage)": hidden only because its whole passage is, not asked for by the user.
                string trimmed = visible.TrimEnd();
                if (trimmed.EndsWith(VerseLine.WithPassageTag, StringComparison.Ordinal))
                {
                    visible = trimmed.Substring(0, trimmed.Length - VerseLine.WithPassageTag.Length);
                    withPassage = true;
                }
            }

            Match m = VisibleRe.Match(visible);
            if (!m.Success) return null;

            var v = new VerseLine();
            v.Book = m.Groups["book"].Value;
            v.Chapter = int.Parse(m.Groups["ch"].Value, CultureInfo.InvariantCulture);
            v.Reference = Regex.Replace(m.Groups["ref"].Value, @"\s+", " ");
            Match lang = LanguageRe.Match(m.Groups["attrs"].Value);
            v.Language = lang.Success ? lang.Groups["lang"].Value : "";
            v.VerseElement = visible.Trim();
            v.VisibleText = visible.TrimEnd();
            v.SavedText = line;
            v.SavedHidden = hidden;
            v.SavedWithPassage = withPassage;
            v.IsHidden = hidden && !withPassage;
            return v;
        }

        private static void BuildTree(ParallelPassagesDocument doc)
        {
            var books = new Dictionary<string, BookNode>();
            var chapters = new Dictionary<string, ChapterNode>();

            foreach (VerseLine v in doc.Verses)
            {
                BookNode book;
                if (!books.TryGetValue(v.Book, out book))
                {
                    book = new BookNode { Book = v.Book };
                    books[v.Book] = book;
                }
                string key = v.Book + " " + v.Chapter.ToString(CultureInfo.InvariantCulture);
                ChapterNode ch;
                if (!chapters.TryGetValue(key, out ch))
                {
                    ch = new ChapterNode { Book = v.Book, Chapter = v.Chapter };
                    chapters[key] = ch;
                }
                ch.AddLine(v);
            }

            foreach (ChapterNode ch in chapters.Values.OrderBy(c => c.Chapter))
                books[ch.Book].AddChapter(ch);

            doc.Books.AddRange(books.Values.OrderBy(b => b.Book, BookOrderService.BookComparer));
        }

        // -- Save ---------------------------------------------------------------

        public static string BuildText(ParallelPassagesDocument doc)
        {
            string[] rendered = RenderLines(doc);
            var sb = new StringBuilder();
            for (int i = 0; i < rendered.Length; i++)
            {
                sb.Append(rendered[i]);
                sb.Append(doc.Terminators[i]);
            }
            return sb.ToString();
        }

        /// <summary>Every line as it will be written (without line terminators).</summary>
        private static string[] RenderLines(ParallelPassagesDocument doc)
        {
            var rendered = new string[doc.Lines.Count];
            for (int i = 0; i < doc.Lines.Count; i++)
                rendered[i] = doc.Lines[i];
            foreach (VerseLine v in doc.Verses)
                rendered[v.LineIndex] = v.Render();
            foreach (PassageGroup p in doc.Passages)
            {
                if (p.OpenLine != null) rendered[p.OpenLine.LineIndex] = p.OpenLine.Render();
                if (p.CloseLine != null) rendered[p.CloseLine.LineIndex] = p.CloseLine.Render();
            }
            return rendered;
        }

        /// <summary>Throws XmlException if the text is not well-formed XML.</summary>
        public static void ValidateXml(string text)
        {
            var xml = new XmlDocument();
            xml.XmlResolver = null;
            xml.LoadXml(text);
        }

        /// <summary>
        /// Throws InvalidOperationException if any passage would be written with fewer visible
        /// entries than min(2, its entry count). Such a file can stop Paratext from loading.
        /// </summary>
        public static void ValidatePassages(ParallelPassagesDocument doc)
        {
            foreach (PassageGroup p in doc.Passages)
            {
                int line = p.Lines[0].LineIndex + 1;
                if (p.IsHiddenEntirely)
                {
                    // Hidden entirely: every line of it, including <Passage> and </Passage>, is commented out.
                    if (p.EffectiveVisibleCount != 0 || !p.OpenLine.IsEffectivelyHidden || !p.CloseLine.IsEffectivelyHidden)
                        throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture,
                            "Passage starting at line {0} is hidden but {1} of its entries would still be visible.",
                            line, p.EffectiveVisibleCount));
                    continue;
                }
                int required = Math.Min(PassageGroup.MinVisible, p.Lines.Count);
                if (p.EffectiveVisibleCount < required)
                    throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture,
                        "Passage starting at line {0} would have only {1} visible entries.",
                        line, p.EffectiveVisibleCount));
            }
        }

        /// <summary>
        /// The one copy of the original file kept beside it: ParallelPassages.xmlorg.
        /// </summary>
        public static string OriginalCopyPath(string targetPath)
        {
            return targetPath + "org";
        }

        /// <summary>
        /// Writes the document to targetPath. Throws XmlException or InvalidOperationException
        /// (nothing written) if the result would be invalid, and UnauthorizedAccessException if
        /// Windows refuses the write. The first time a file is saved, the file as it was is
        /// copied beside it as &lt;name&gt;org (ParallelPassages.xmlorg); once that copy exists it
        /// is never touched again. Returns the path of the copy if this save created it, else null.
        /// </summary>
        public static string Save(ParallelPassagesDocument doc, string targetPath)
        {
            ValidatePassages(doc);
            string[] rendered = RenderLines(doc);
            string text = BuildText(doc);
            ValidateXml(text);

            byte[] preamble = doc.Encoding.GetPreamble();
            byte[] body = doc.Encoding.GetBytes(text);
            byte[] bytes = new byte[preamble.Length + body.Length];
            Buffer.BlockCopy(preamble, 0, bytes, 0, preamble.Length);
            Buffer.BlockCopy(body, 0, bytes, preamble.Length, body.Length);

            // Write the temp file first: in Program Files this is what fails without admin rights,
            // before any backup is made.
            string tmp = targetPath + ".tmp";
            File.WriteAllBytes(tmp, bytes);

            string originalCopy = null;
            try
            {
                if (File.Exists(targetPath))
                {
                    // Keep the original once; an existing .xmlorg (also one made by hand) is left alone.
                    string orig = OriginalCopyPath(targetPath);
                    if (!File.Exists(orig))
                    {
                        File.Copy(targetPath, orig, false);
                        originalCopy = orig;
                    }

                    try
                    {
                        File.Replace(tmp, targetPath, null);
                    }
                    catch (IOException)
                    {
                        File.Copy(tmp, targetPath, true);
                        File.Delete(tmp);
                    }
                    catch (PlatformNotSupportedException)
                    {
                        File.Copy(tmp, targetPath, true);
                        File.Delete(tmp);
                    }
                }
                else
                {
                    File.Move(tmp, targetPath);
                }
            }
            finally
            {
                if (File.Exists(tmp))
                {
                    try { File.Delete(tmp); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }

            foreach (VerseLine v in doc.Verses)
                v.CommitSaved();
            foreach (PassageGroup p in doc.Passages)
            {
                if (p.OpenLine != null) p.OpenLine.CommitSaved();
                if (p.CloseLine != null) p.CloseLine.CommitSaved();
            }
            // Lines mirror the file on disk again, so a later re-parse (adding a passage) starts from it.
            for (int i = 0; i < rendered.Length; i++)
                doc.Lines[i] = rendered[i];
            doc.StructureChanged = false;

            doc.Path = targetPath;
            var info = new FileInfo(targetPath);
            doc.FileLength = info.Length;
            doc.LastWriteUtc = info.LastWriteTimeUtc;
            return originalCopy;
        }

        // -- Passages added with this editor ------------------------------------

        /// <summary>
        /// Adds a passage before &lt;/Passages&gt;, marked as added by this editor. The references
        /// must already be checked (see PassageReferences.Parse). Returns the new passage.
        /// </summary>
        public static PassageGroup AddPassage(ParallelPassagesDocument doc, IList<string> refs)
        {
            int at = -1;
            for (int i = doc.Lines.Count - 1; i >= 0; i--)
                if (PassagesCloseRe.IsMatch(doc.Lines[i])) { at = i; break; }
            if (at < 0)
                throw new InvalidOperationException("The file has no </Passages> line.");
            List<string> block = BuildBlock(doc, refs);
            Restructure(doc, at, 0, block, null);
            return FindPassageAtLine(doc, at);
        }

        /// <summary>Replaces the references of a passage added with this editor, in place.</summary>
        public static PassageGroup ReplacePassage(ParallelPassagesDocument doc, PassageGroup passage, IList<string> refs)
        {
            CheckUserAdded(passage);
            int start = passage.AddedMarkerLineIndex;
            int count = passage.CloseLine.LineIndex - start + 1;
            // An entry that keeps its reference keeps its hidden state.
            var hidden = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (VerseLine v in passage.Lines)
                hidden[v.Reference] = v.IsHidden;
            Restructure(doc, start, count, BuildBlock(doc, refs), hidden);
            return FindPassageAtLine(doc, start);
        }

        /// <summary>Removes a passage added with this editor (marker comment through &lt;/Passage&gt;).</summary>
        public static void RemovePassage(ParallelPassagesDocument doc, PassageGroup passage)
        {
            CheckUserAdded(passage);
            int start = passage.AddedMarkerLineIndex;
            Restructure(doc, start, passage.CloseLine.LineIndex - start + 1, new List<string>(), null);
        }

        private static void CheckUserAdded(PassageGroup passage)
        {
            if (passage == null || !passage.IsUserAdded)
                throw new InvalidOperationException("Only passages added with this editor can be changed or removed.");
            if (passage.CloseLine.LineIndex < passage.AddedMarkerLineIndex)
                throw new InvalidOperationException("The passage's lines are out of order.");
        }

        private static PassageGroup FindPassageAtLine(ParallelPassagesDocument doc, int markerLine)
        {
            foreach (PassageGroup p in doc.Passages)
                if (p.AddedMarkerLineIndex == markerLine) return p;
            return null;
        }

        /// <summary>The lines of a new passage, indented like the file's first visible passage.</summary>
        private static List<string> BuildBlock(ParallelPassagesDocument doc, IList<string> refs)
        {
            string passageIndent = "  ", verseIndent = "    ";
            foreach (PassageGroup p in doc.Passages)
            {
                if (p.OpenLine == null || p.OpenLine.SavedHidden || p.Lines.Count == 0 || p.Lines[0].SavedHidden) continue;
                passageIndent = Indent(p.OpenLine.SavedText);
                verseIndent = Indent(p.Lines[0].SavedText);
                break;
            }
            var block = new List<string>();
            block.Add(passageIndent + AddedMarker);
            block.Add(passageIndent + "<Passage>");
            foreach (string r in refs)
                block.Add(verseIndent + "<Verse " + LanguageFor(r) + "=\"\">" + r + "</Verse>");
            block.Add(passageIndent + "</Passage>");
            return block;
        }

        private static string Indent(string line)
        {
            int n = 0;
            while (n < line.Length && (line[n] == ' ' || line[n] == '\t')) n++;
            return line.Substring(0, n);
        }

        /// <summary>HEB for Old Testament books, GRK for New Testament books (as in the stock file).</summary>
        public static string LanguageFor(string reference)
        {
            string book = reference.Length >= 3 ? reference.Substring(0, 3) : reference;
            int idx = BookOrderService.GetSortIndex(book);
            return idx >= 39 ? "GRK" : "HEB";
        }

        /// <summary>
        /// Replaces lines [start, start+removeCount) with newLines, parses the file again and keeps
        /// the hidden state the user chose for every entry outside the replaced lines. Entries in
        /// the new lines take their state from newHidden (by reference), else visible.
        /// </summary>
        private static void Restructure(ParallelPassagesDocument doc, int start, int removeCount,
            List<string> newLines, Dictionary<string, bool> newHidden)
        {
            var oldHidden = new Dictionary<int, bool>();
            foreach (VerseLine v in doc.Verses)
                oldHidden[v.LineIndex] = v.IsHidden;

            string term = "\r\n";
            for (int i = start - 1; i >= 0; i--)
                if (doc.Terminators[i].Length > 0) { term = doc.Terminators[i]; break; }

            doc.Lines.RemoveRange(start, removeCount);
            doc.Terminators.RemoveRange(start, removeCount);
            doc.Lines.InsertRange(start, newLines);
            var terms = new List<string>();
            for (int i = 0; i < newLines.Count; i++) terms.Add(term);
            doc.Terminators.InsertRange(start, terms);

            doc.Verses.Clear();
            doc.Books.Clear();
            doc.Passages.Clear();
            ParseVerses(doc);
            BuildTree(doc);

            int added = newLines.Count;
            foreach (VerseLine v in doc.Verses)
            {
                int i = v.LineIndex;
                bool h;
                if (i < start)
                {
                    if (oldHidden.TryGetValue(i, out h)) v.IsHidden = h;
                }
                else if (i >= start + added)
                {
                    if (oldHidden.TryGetValue(i - added + removeCount, out h)) v.IsHidden = h;
                }
                else
                {
                    v.IsHidden = newHidden != null && newHidden.TryGetValue(v.Reference, out h) && h;
                }
            }
            doc.StructureChanged = true;
        }

        /// <summary>A passage's identity for comparisons: its references, in file order.</summary>
        public static string PassageKey(IEnumerable<string> refs)
        {
            return string.Join(" ; ", refs);
        }

        // -- Pending changes (carried over when restarting as administrator) ----
        // Line 0: file length, line 1: last write time (the file must be unchanged on disk).
        // "P\t<refs>": a passage added with this editor, in file order.
        // "H\t<n>\t<entry>\t<refs>": entry <entry> of the n-th passage with these references is hidden.
        // "L\t<line>": an entry outside any passage is hidden.

        public static void WritePending(ParallelPassagesDocument doc, string pendingPath)
        {
            var lines = new List<string>();
            lines.Add(doc.FileLength.ToString(CultureInfo.InvariantCulture));
            lines.Add(doc.LastWriteUtc.Ticks.ToString(CultureInfo.InvariantCulture));
            foreach (PassageGroup p in doc.Passages)
                if (p.IsUserAdded)
                    lines.Add("P\t" + PassageKey(p.References));
            var seen = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (PassageGroup p in doc.Passages)
            {
                string key = PassageKey(p.References);
                int n;
                seen.TryGetValue(key, out n);
                seen[key] = n + 1;
                for (int e = 0; e < p.Lines.Count; e++)
                    if (p.Lines[e].IsHidden)
                        lines.Add("H\t" + n.ToString(CultureInfo.InvariantCulture) + "\t" + e.ToString(CultureInfo.InvariantCulture) + "\t" + key);
            }
            foreach (VerseLine v in doc.Verses)
                if (v.Passage == null && v.IsHidden)
                    lines.Add("L\t" + v.LineIndex.ToString(CultureInfo.InvariantCulture));
            File.WriteAllLines(pendingPath, lines.ToArray(), new UTF8Encoding(false));
        }

        /// <summary>
        /// Applies a pending file written by WritePending. Returns false (and changes nothing)
        /// if the file on disk changed in the meantime.
        /// </summary>
        public static bool ApplyPending(ParallelPassagesDocument doc, string pendingPath)
        {
            string[] lines = File.ReadAllLines(pendingPath);
            if (lines.Length < 2) return false;

            long length;
            long ticks;
            if (!long.TryParse(lines[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out length)) return false;
            if (!long.TryParse(lines[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out ticks)) return false;
            if (length != doc.FileLength || ticks != doc.LastWriteUtc.Ticks) return false;

            var addedWanted = new List<string>();
            var hiddenKeys = new HashSet<string>(StringComparer.Ordinal);
            var hiddenLines = new HashSet<int>();
            for (int i = 2; i < lines.Length; i++)
            {
                string[] f = lines[i].Split('\t');
                if (f.Length == 2 && f[0] == "P") addedWanted.Add(f[1]);
                else if (f.Length == 4 && f[0] == "H") hiddenKeys.Add(f[1] + "\t" + f[2] + "\t" + f[3]);
                else if (f.Length == 2 && f[0] == "L")
                {
                    int idx;
                    if (int.TryParse(f[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out idx)) hiddenLines.Add(idx);
                }
            }

            // Added passages are rebuilt only when they differ from the file, so a carry-over of
            // hiding alone moves nothing.
            var addedNow = new List<string>();
            foreach (PassageGroup p in doc.Passages)
                if (p.IsUserAdded) addedNow.Add(PassageKey(p.References));
            if (!addedNow.SequenceEqual(addedWanted))
            {
                for (int i = doc.Passages.Count - 1; i >= 0; i--)
                    if (doc.Passages[i].IsUserAdded) RemovePassage(doc, doc.Passages[i]);
                foreach (string key in addedWanted)
                    AddPassage(doc, key.Split(new[] { " ; " }, StringSplitOptions.None));
            }

            var seen = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (PassageGroup p in doc.Passages)
            {
                string key = PassageKey(p.References);
                int n;
                seen.TryGetValue(key, out n);
                seen[key] = n + 1;
                for (int e = 0; e < p.Lines.Count; e++)
                    p.Lines[e].IsHidden = hiddenKeys.Contains(n.ToString(CultureInfo.InvariantCulture) + "\t" + e.ToString(CultureInfo.InvariantCulture) + "\t" + key);
            }
            foreach (VerseLine v in doc.Verses)
                if (v.Passage == null) v.IsHidden = hiddenLines.Contains(v.LineIndex);
            return true;
        }
    }
}
