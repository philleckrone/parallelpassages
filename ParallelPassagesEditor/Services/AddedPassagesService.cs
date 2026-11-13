using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using ParallelPassagesEditor.Models;

namespace ParallelPassagesEditor.Services
{
    /// <summary>
    /// Reads and checks the references the user types for a new passage. Paratext reads each
    /// entry as a reference in the Original versification, so that is what is checked.
    /// </summary>
    public static class PassageReferences
    {
        // GEN 2:24, MRK 10:7-8, 2CH 8:13,16, MAT 1:2-6,16 (the shapes used in the stock file).
        // A full stop is accepted for the colon, as Paratext's own reader accepts it.
        private static readonly Regex RefRe = new Regex(
            @"^\s*(?<book>[1-4A-Za-z][A-Za-z0-9]{2})\s+(?<ch>\d{1,3})\s*[:.]\s*(?<v>\d{1,3})(?:\s*-\s*(?<to>\d{1,3}))?(?:\s*,\s*(?<and>\d{1,3}))?\s*$",
            RegexOptions.CultureInvariant);

        public const int OldAndNewTestamentBooks = 66;

        /// <summary>
        /// Splits the text into references (one per line, or separated by ';') and checks them.
        /// Returns the normalized references; errors gets one message per problem (empty = OK).
        /// The passage itself (excluded, may be null) is not counted as a duplicate when editing.
        /// </summary>
        public static List<string> Parse(string text, ParallelPassagesDocument doc, PassageGroup excluded,
            List<string> errors)
        {
            var refs = new List<string>();
            var seen = new Dictionary<string, int>(StringComparer.Ordinal);
            string[] lines = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            int number = 0;
            foreach (string rawLine in lines)
            {
                foreach (string part in rawLine.Split(';'))
                {
                    string item = part.Trim();
                    if (item.Length == 0) continue;
                    number++;
                    string normalized;
                    string error = ParseOne(item, out normalized);
                    if (error != null)
                    {
                        errors.Add(string.Format(Loc.Get("Err_LineFormat"), number, error));
                        continue;
                    }
                    int first;
                    if (seen.TryGetValue(normalized, out first))
                    {
                        errors.Add(string.Format(Loc.Get("Err_LineFormat"), number,
                            string.Format(Loc.Get("Err_Duplicate"), normalized, first)));
                        continue;
                    }
                    seen[normalized] = number;
                    refs.Add(normalized);
                }
            }

            if (errors.Count == 0 && refs.Count < PassageGroup.MinVisible)
                errors.Add(Loc.Get("Err_TooFew"));

            if (errors.Count == 0 && doc != null)
            {
                PassageGroup same = FindSamePassage(doc, refs, excluded);
                if (same != null)
                    errors.Add(string.Format(Loc.Get("Err_Exists"), same.Lines[0].LineIndex + 1,
                        string.Join(", ", same.References)));
            }
            return refs;
        }

        /// <summary>A passage of the file with the same references (in any order), or null.</summary>
        public static PassageGroup FindSamePassage(ParallelPassagesDocument doc, IList<string> refs, PassageGroup excluded)
        {
            string key = SortedKey(refs);
            foreach (PassageGroup p in doc.Passages)
                if (p != excluded && p.Lines.Count > 0 && SortedKey(p.References) == key) return p;
            return null;
        }

        private static string SortedKey(IEnumerable<string> refs)
        {
            var list = refs.Select(r => Regex.Replace(r.Trim(), @"\s+", " ")).ToList();
            list.Sort(StringComparer.Ordinal);
            return string.Join("|", list);
        }

        /// <summary>Null and the normalized reference, or an error message.</summary>
        public static string ParseOne(string item, out string normalized)
        {
            normalized = null;
            Match m = RefRe.Match(item);
            if (!m.Success)
                return string.Format(Loc.Get("Err_BadRef"), item);

            string book = m.Groups["book"].Value.ToUpperInvariant();
            int bookIndex = BookOrderService.GetSortIndex(book);
            if (bookIndex < 0 || bookIndex >= OldAndNewTestamentBooks)
                return string.Format(Loc.Get("Err_Book"), book);

            int ch = int.Parse(m.Groups["ch"].Value, CultureInfo.InvariantCulture);
            int v = int.Parse(m.Groups["v"].Value, CultureInfo.InvariantCulture);
            int to = m.Groups["to"].Success ? int.Parse(m.Groups["to"].Value, CultureInfo.InvariantCulture) : 0;
            int and = m.Groups["and"].Success ? int.Parse(m.Groups["and"].Value, CultureInfo.InvariantCulture) : 0;

            string chv = book + " " + ch.ToString(CultureInfo.InvariantCulture);
            if (ch < 1 || v < 1)
                return string.Format(Loc.Get("Err_BadRef"), item);
            if (m.Groups["to"].Success && to <= v)
                return string.Format(Loc.Get("Err_Order"), item);
            if (m.Groups["and"].Success && and <= Math.Max(v, to))
                return string.Format(Loc.Get("Err_Order"), item);

            int lastChapter = Versification.LastChapter(bookIndex + 1);
            if (lastChapter > 0 && ch > lastChapter)
                return string.Format(Loc.Get("Err_Chapter"), chv, book, lastChapter);
            int lastVerse = Versification.LastVerse(bookIndex + 1, ch);
            int highest = Math.Max(v, Math.Max(to, and));
            if (lastVerse > 0 && highest > lastVerse)
                return string.Format(Loc.Get("Err_Verse"), item, chv, lastVerse);

            var sb = new StringBuilder();
            sb.Append(chv).Append(':').Append(v.ToString(CultureInfo.InvariantCulture));
            if (m.Groups["to"].Success) sb.Append('-').Append(to.ToString(CultureInfo.InvariantCulture));
            if (m.Groups["and"].Success) sb.Append(',').Append(and.ToString(CultureInfo.InvariantCulture));
            normalized = sb.ToString();
            return null;
        }
    }

    /// <summary>
    /// Chapter and verse counts of the Original versification, from the SIL.Scripture.dll of the
    /// Paratext 9 installed in Program Files (never from a folder a user can write to, because
    /// this program runs as administrator). Without it, only the form of a reference is checked.
    /// </summary>
    public static class Versification
    {
        private static bool _tried;
        private static object _original;
        private static MethodInfo _lastChapter;
        private static MethodInfo _lastVerse;

        /// <summary>Set by MainWindow: the Program Files folders read from the machine registry.</summary>
        public static IList<string> ProgramFilesRoots;

        private static void Load()
        {
            if (_tried) return;
            _tried = true;
            if (ProgramFilesRoots == null) return;
            foreach (string root in ProgramFilesRoots)
            {
                string dll = Path.Combine(Path.Combine(root, "Paratext 9"), "SIL.Scripture.dll");
                if (!File.Exists(dll)) continue;
                try
                {
                    Assembly a = Assembly.LoadFrom(dll);
                    Type t = a.GetType("SIL.Scripture.ScrVers", false);
                    if (t == null) continue;
                    FieldInfo f = t.GetField("Original", BindingFlags.Public | BindingFlags.Static);
                    MethodInfo c = t.GetMethod("GetLastChapter", new[] { typeof(int) });
                    MethodInfo v = t.GetMethod("GetLastVerse", new[] { typeof(int), typeof(int) });
                    object o = f == null ? null : f.GetValue(null);
                    if (o == null || c == null || v == null) continue;
                    _original = o;
                    _lastChapter = c;
                    _lastVerse = v;
                    return;
                }
                catch (Exception) { }
            }
        }

        public static bool IsAvailable
        {
            get { Load(); return _original != null; }
        }

        /// <summary>0 when unknown.</summary>
        public static int LastChapter(int bookNum)
        {
            Load();
            if (_original == null) return 0;
            try { return (int)_lastChapter.Invoke(_original, new object[] { bookNum }); }
            catch (Exception) { return 0; }
        }

        /// <summary>0 when unknown.</summary>
        public static int LastVerse(int bookNum, int chapter)
        {
            Load();
            if (_original == null) return 0;
            try { return (int)_lastVerse.Invoke(_original, new object[] { bookNum, chapter }); }
            catch (Exception) { return 0; }
        }
    }

    /// <summary>
    /// Remembers the passages added with this editor, so they can be added again after a
    /// Paratext update replaces ParallelPassages.xml. One passage per line, its references
    /// joined by " ; ". Rewritten on every save, like hidden.txt.
    /// </summary>
    public static class AddedPassagesService
    {
        public static List<List<string>> Build(ParallelPassagesDocument doc)
        {
            var list = new List<List<string>>();
            foreach (PassageGroup p in doc.Passages)
                if (p.IsUserAdded)
                    list.Add(p.References);
            return list;
        }

        public static void Save(List<List<string>> list, string path)
        {
            var lines = new List<string>();
            lines.Add("# Parallel Passages Editor - passages you added (rewritten on every save)");
            foreach (List<string> refs in list)
                lines.Add(ParallelPassagesFileService.PassageKey(refs));
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllLines(path, lines.ToArray(), new UTF8Encoding(false));
        }

        /// <summary>Passages from the file; lines that no longer pass the checks are skipped.</summary>
        public static List<List<string>> Load(string path)
        {
            var list = new List<List<string>>();
            if (!File.Exists(path)) return list;
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                var errors = new List<string>();
                List<string> refs = PassageReferences.Parse(line, null, null, errors);
                if (errors.Count == 0) list.Add(refs);
            }
            return list;
        }

        /// <summary>The saved passages the file does not have (as an added passage or a stock one).</summary>
        public static List<List<string>> MissingFrom(ParallelPassagesDocument doc, List<List<string>> list)
        {
            var missing = new List<List<string>>();
            foreach (List<string> refs in list)
                if (PassageReferences.FindSamePassage(doc, refs, null) == null)
                    missing.Add(refs);
            return missing;
        }
    }
}
