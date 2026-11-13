using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using ParallelPassagesEditor.Models;

namespace ParallelPassagesEditor.Services
{
    /// <summary>
    /// Remembers which books/chapters/entries were hidden, so the list can be reapplied after
    /// a Paratext update replaces ParallelPassages.xml. One item per line:
    ///   JHN                          whole book hidden
    ///   MRK 16                       whole chapter hidden
    ///   LINE &lt;Verse ...&gt;...&lt;/Verse&gt;  single entry hidden in a partly hidden chapter
    /// </summary>
    public class HiddenList
    {
        public HashSet<string> Books = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Chapters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Entries = new HashSet<string>(StringComparer.Ordinal);

        public bool IsEmpty
        {
            get { return Books.Count == 0 && Chapters.Count == 0 && Entries.Count == 0; }
        }
    }

    public static class HiddenListService
    {
        private const string LinePrefix = "LINE ";

        public static HiddenList Build(ParallelPassagesDocument doc)
        {
            var list = new HiddenList();
            foreach (BookNode book in doc.Books)
            {
                if (book.IsHidden == true)
                {
                    list.Books.Add(book.Book);
                    continue;
                }
                foreach (ChapterNode ch in book.Chapters)
                {
                    bool? state = ch.IsHidden;
                    if (state == true)
                        list.Chapters.Add(ChapterKey(ch.Book, ch.Chapter));
                    else if (state == null)
                        foreach (VerseLine v in ch.Lines)
                            if (v.IsHidden)
                                list.Entries.Add(v.VerseElement);
                }
            }
            return list;
        }

        public static void Save(HiddenList list, string path)
        {
            var lines = new List<string>();
            lines.Add("# Parallel Passages Editor - hidden list (rewritten on every save)");
            var books = new List<string>(list.Books);
            books.Sort(BookOrderService.BookComparer);
            lines.AddRange(books);
            var chapters = new List<string>(list.Chapters);
            chapters.Sort(CompareChapterKeys);
            lines.AddRange(chapters);
            foreach (string e in list.Entries)
                lines.Add(LinePrefix + e);

            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllLines(path, lines.ToArray(), new UTF8Encoding(false));
        }

        public static HiddenList Load(string path)
        {
            var list = new HiddenList();
            if (!File.Exists(path)) return list;
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                if (line.StartsWith(LinePrefix, StringComparison.Ordinal))
                    list.Entries.Add(line.Substring(LinePrefix.Length).Trim());
                else if (line.IndexOf(' ') > 0)
                    list.Chapters.Add(NormalizeChapterKey(line));
                else
                    list.Books.Add(line);
            }
            return list;
        }

        /// <summary>Makes the document's hidden state match the list exactly.</summary>
        public static void Apply(ParallelPassagesDocument doc, HiddenList list)
        {
            foreach (VerseLine v in doc.Verses)
                v.IsHidden = Matches(list, v);
        }

        /// <summary>Whether the list asks for this entry to be hidden.</summary>
        public static bool Matches(HiddenList list, VerseLine v)
        {
            return list.Books.Contains(v.Book)
                || list.Chapters.Contains(ChapterKey(v.Book, v.Chapter))
                || list.Entries.Contains(v.VerseElement);
        }

        /// <summary>True if applying the list would change what the user has asked to hide.</summary>
        public static bool DiffersFrom(ParallelPassagesDocument doc, HiddenList list)
        {
            foreach (VerseLine v in doc.Verses)
                if (Matches(list, v) != v.IsHidden) return true;
            return false;
        }

        // -- Per-project list: <project>\local\ParallelPassagesEditor\hidden.txt ---------
        // The project's local folder is not synced by Send/Receive, so the list stays on this computer.
        // The editor (administrator) only reads it; the plugin writes or removes it after the
        // editor closes, without administrator rights (see App.ExitKeepProjectList).

        public static string ProjectListPath(string projectDir)
        {
            return Path.Combine(Path.Combine(Path.Combine(projectDir, "local"), "ParallelPassagesEditor"), "hidden.txt");
        }

        public static string ChapterKey(string book, int chapter)
        {
            return book + " " + chapter.ToString(CultureInfo.InvariantCulture);
        }

        private static string NormalizeChapterKey(string s)
        {
            string[] parts = s.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            int ch;
            if (parts.Length == 2 && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out ch))
                return ChapterKey(parts[0], ch);
            return s;
        }

        private static int CompareChapterKeys(string a, string b)
        {
            string[] pa = a.Split(' ');
            string[] pb = b.Split(' ');
            int c = BookOrderService.BookComparer.Compare(pa[0], pb[0]);
            if (c != 0) return c;
            int ca, cb;
            int.TryParse(pa[pa.Length - 1], out ca);
            int.TryParse(pb[pb.Length - 1], out cb);
            return ca.CompareTo(cb);
        }
    }
}
