using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Xml;
using Microsoft.Win32;
using ParallelPassagesEditor.Models;
using ParallelPassagesEditor.Services;

namespace ParallelPassagesEditor
{
    public partial class MainWindow : Window
    {
        public static readonly RoutedUICommand SaveAsCommand = new RoutedUICommand(
            "Save As", "SaveAs", typeof(MainWindow),
            new InputGestureCollection { new KeyGesture(Key.S, ModifierKeys.Control | ModifierKeys.Shift) });

        private ParallelPassagesDocument _doc;
        private bool _closingForRelaunch;
        private string _projectDir;   // set when opened from a project (--project); enables the per-project list
        private string _launchPath;   // the file named on the command line (from the plugin: Paratext's own)

        public MainWindow()
        {
            InitializeComponent();
            Versification.ProgramFilesRoots = ProgramFilesRoots();
            PopulateLanguageCombo();
            UpdateLocalizedColumnHeaders();
            UpdateHeaderAndStatus();
            UpdateButtonStates();
        }

        private static string GetStr(string key)
        {
            return Loc.Get(key);
        }

        // -- Startup ------------------------------------------------------------

        public void Startup(string argPath, string pendingPath, string projectDir)
        {
            _projectDir = projectDir;
            _launchPath = argPath;
            UpdateProjectOption();

            string path = argPath;
            if (path == null)
                path = FindDefaultFile();
            if (path == null)
            {
                MessageBox.Show(this, GetStr("Dlg_NotFoundBody"), GetStr("Dlg_NotFoundTitle"),
                    MessageBoxButton.OK, MessageBoxImage.Information);
                path = AskOpenPath();
            }
            if (path == null) return;
            if (!LoadFile(path)) return;

            // This window runs as administrator: only our own pending file (see
            // RelaunchElevated) is read and then deleted, never any other path.
            if (pendingPath != null && !IsOwnPendingFile(pendingPath))
                pendingPath = null;
            if (pendingPath != null)
            {
                bool applied = false;
                try
                {
                    applied = ParallelPassagesFileService.ApplyPending(_doc, pendingPath);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                try { File.Delete(pendingPath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }

                RefreshAll();
                MessageBox.Show(this,
                    GetStr(applied ? "Dlg_PendingAppliedBody" : "Dlg_PendingStaleBody"),
                    GetStr("Dlg_PendingTitle"),
                    MessageBoxButton.OK,
                    applied ? MessageBoxImage.Information : MessageBoxImage.Warning);
                return;
            }

            // Passages the user added come back first, so the hidden lists can find their entries.
            OfferAddedIfMissing();
            // A project's own saved list takes the place of the global "reapply after update" offer.
            if (!OfferProjectList())
                OfferReapplyIfNothingHidden();
        }

        /// <summary>
        /// If passages the user added earlier (added.txt) are not in the file, for example after
        /// a Paratext update replaced it, offers to add them again.
        /// </summary>
        private void OfferAddedIfMissing()
        {
            if (_doc == null) return;
            List<List<string>> missing = MissingAddedPassages();
            if (missing == null || missing.Count == 0) return;
            MessageBoxResult r = MessageBox.Show(this,
                string.Format(GetStr("Dlg_ReaddBody"), missing.Count, DescribePassages(missing)),
                GetStr("Dlg_ReaddTitle"), MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r == MessageBoxResult.Yes)
                AddPassages(missing);
        }

        private List<List<string>> MissingAddedPassages()
        {
            try
            {
                return AddedPassagesService.MissingFrom(_doc, AddedPassagesService.Load(AppPaths.AddedListPath));
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        private static string DescribePassages(List<List<string>> passages)
        {
            var lines = new List<string>();
            foreach (List<string> refs in passages.Take(10))
                lines.Add("    " + string.Join(", ", refs));
            if (passages.Count > 10)
                lines.Add("    ...");
            return string.Join("\n", lines);
        }

        private void AddPassages(List<List<string>> passages)
        {
            PassageGroup last = null;
            try
            {
                foreach (List<string> refs in passages)
                    last = ParallelPassagesFileService.AddPassage(_doc, refs);
            }
            catch (InvalidOperationException ex)
            {
                ShowPassageError(ex);
            }
            AfterStructureChange(last);
        }

        private string ProjectName
        {
            get { return _projectDir == null ? null : Path.GetFileName(_projectDir); }
        }

        /// <summary>Shows the per-project checkbox when opened from a project; ticked if the project has a saved list.</summary>
        private void UpdateProjectOption()
        {
            if (_projectDir == null)
            {
                ProjectOptionCheck.Visibility = Visibility.Collapsed;
                return;
            }
            ProjectOptionCheck.Visibility = Visibility.Visible;
            ProjectOptionCheck.Content = string.Format(GetStr("Check_SaveForProject"), ProjectName);
            ProjectOptionCheck.IsChecked = File.Exists(HiddenListService.ProjectListPath(_projectDir));
        }

        /// <summary>
        /// If the project has a saved list that differs from the file, offers to use it.
        /// Returns true when the project list was considered (shown, or already matching), so the
        /// global reapply offer is skipped.
        /// </summary>
        private bool OfferProjectList()
        {
            if (_doc == null || _projectDir == null) return false;
            HiddenList list;
            try
            {
                list = HiddenListService.Load(HiddenListService.ProjectListPath(_projectDir));
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            if (list.IsEmpty) return false;
            if (!HiddenListService.DiffersFrom(_doc, list)) return true;

            MessageBoxResult r = MessageBox.Show(this,
                string.Format(GetStr("Dlg_ProjectListBody"), ProjectName, list.Books.Count, list.Chapters.Count, list.Entries.Count),
                GetStr("Dlg_ProjectListTitle"),
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r == MessageBoxResult.Yes)
            {
                HiddenListService.Apply(_doc, list);
                RefreshAll();
            }
            return true;
        }

        /// <summary>
        /// The Paratext 9 install folder first (that is the file Paratext reads), then the last
        /// file used, but only if that is under Program Files too: this window runs as
        /// administrator, and the settings file holding LastPath can be changed by any program
        /// the user runs.
        /// </summary>
        private static string FindDefaultFile()
        {
            List<string> roots = ProgramFilesRoots();
            var candidates = new List<string>();
            foreach (string dir in roots)
                candidates.Add(Path.Combine(dir, @"Paratext 9\ParallelPassages\ParallelPassages.xml"));
            string last = SettingsService.Get("LastPath");
            if (!string.IsNullOrEmpty(last) && IsUnder(last, roots))
                candidates.Add(last);

            foreach (string c in candidates)
                if (File.Exists(c)) return c;
            return null;
        }

        /// <summary>Program Files folders from the machine registry (environment variables can be set per user).</summary>
        private static List<string> ProgramFilesRoots()
        {
            var roots = new List<string>();
            foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using (RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                    using (RegistryKey key = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion"))
                    {
                        if (key == null) continue;
                        foreach (string name in new[] { "ProgramW6432Dir", "ProgramFilesDir", "ProgramFilesDir (x86)" })
                        {
                            string dir = key.GetValue(name) as string;
                            if (!string.IsNullOrEmpty(dir) && !roots.Contains(dir, StringComparer.OrdinalIgnoreCase))
                                roots.Add(dir);
                        }
                    }
                }
                catch (Exception) { }
            }
            return roots;
        }

        private static bool IsUnder(string path, List<string> roots)
        {
            string full;
            try { full = Path.GetFullPath(path); }
            catch (Exception) { return false; }
            foreach (string root in roots)
                if (full.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>A pending file this editor wrote: its own name pattern, and a plain file (not a link).</summary>
        private static bool IsOwnPendingFile(string path)
        {
            try
            {
                if (!System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(path),
                        "^ParallelPassagesEditor_pending_[0-9a-f]{32}\\.txt$")) return false;
                return (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0;
            }
            catch (Exception) { return false; }
        }

        private void OfferReapplyIfNothingHidden()
        {
            if (_doc == null || _doc.RequestedHiddenCount > 0) return;
            HiddenList list = LoadHiddenList();
            if (list == null || list.IsEmpty) return;

            MessageBoxResult r = MessageBox.Show(this,
                string.Format(GetStr("Dlg_ReapplyBody"), list.Books.Count, list.Chapters.Count, list.Entries.Count),
                GetStr("Dlg_ReapplyTitle"),
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r == MessageBoxResult.Yes)
            {
                HiddenListService.Apply(_doc, list);
                RefreshAll();
            }
        }

        private static HiddenList LoadHiddenList()
        {
            try
            {
                return HiddenListService.Load(AppPaths.HiddenListPath);
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        // -- Language -----------------------------------------------------------

        private void PopulateLanguageCombo()
        {
            var languages = new List<LanguageOption>
            {
                new LanguageOption { DisplayName = "English", Culture = "en" },
                new LanguageOption { DisplayName = "Espa" + (char)0x00F1 + "ol", Culture = "es" },
                new LanguageOption { DisplayName = "Fran" + (char)0x00E7 + "ais", Culture = "fr" },
                new LanguageOption { DisplayName = "Portugu" + (char)0x00EA + "s", Culture = "pt" },
                new LanguageOption { DisplayName = new string(new[] { (char)0x4E2D, (char)0x6587, ' ', '(', (char)0x7B80, (char)0x4F53, ')' }), Culture = "zh-Hans" }
            };
            LanguageCombo.ItemsSource = languages;
            LanguageCombo.DisplayMemberPath = "DisplayName";

            string current = SettingsService.Get("Language");
            int idx = current == null ? 0 : Array.IndexOf(App.Cultures, current);
            LanguageCombo.SelectionChanged -= LanguageCombo_SelectionChanged;
            LanguageCombo.SelectedIndex = idx < 0 ? 0 : idx;
            LanguageCombo.SelectionChanged += LanguageCombo_SelectionChanged;
        }

        private void LanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            LanguageOption opt = LanguageCombo.SelectedItem as LanguageOption;
            if (opt == null) return;
            App.SwitchLanguage(opt.Culture);
            SettingsService.Set("Language", opt.Culture);
            UpdateLocalizedColumnHeaders();
            if (_projectDir != null)   // relabel only; keep the user's tick
                ProjectOptionCheck.Content = string.Format(GetStr("Check_SaveForProject"), ProjectName);
            RefreshAll();
        }

        private void UpdateLocalizedColumnHeaders()
        {
            if (EntryGrid.Columns.Count >= 5)
            {
                EntryGrid.Columns[0].Header = GetStr("Column_Hidden");
                EntryGrid.Columns[1].Header = GetStr("Column_Reference");
                EntryGrid.Columns[2].Header = GetStr("Column_ParallelWith");
                EntryGrid.Columns[3].Header = GetStr("Column_Text");
                EntryGrid.Columns[4].Header = GetStr("Column_Note");
            }
        }

        // -- Loading and binding ------------------------------------------------

        private bool LoadFile(string path)
        {
            ParallelPassagesDocument doc;
            try
            {
                doc = ParallelPassagesFileService.Load(path);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, GetStr("Dlg_OpenErrorBody") + "\n" + path + "\n\n" + ex.Message,
                    GetStr("Dlg_OpenErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }

            if (doc.Verses.Count == 0)
            {
                MessageBox.Show(this, GetStr("Dlg_NoVersesBody") + "\n" + path,
                    GetStr("Dlg_OpenErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            _doc = doc;
            SettingsService.Set("LastPath", path);

            // Nothing selected until the user clicks a book: a default selection would be swept
            // into Hide book along with whatever the user picks later.
            BookList.ItemsSource = _doc.Books;
            BookList.UnselectAll();
            ChapterList.ItemsSource = null;
            EntryGrid.ItemsSource = null;
            UpdateButtonStates();

            UpdateHeaderAndStatus();
            CommandManager.InvalidateRequerySuggested();

            // A file saved by the first version may have passages with fewer than 2 visible
            // entries. The 2-entry rule already shows them again (so the file is dirty); say why.
            int broken = _doc.BrokenPassageCountOnDisk;
            if (broken > 0)
                MessageBox.Show(this, string.Format(GetStr("Dlg_RepairBody"), broken),
                    GetStr("Dlg_RepairTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return true;
        }

        private void BookList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Chapters only for a single selected book, and none of them pre-selected.
            List<BookNode> books = SelectedBooks();
            IList<ChapterNode> chapters = books.Count == 1 ? books[0].Chapters : null;
            if (!ReferenceEquals(ChapterList.ItemsSource, chapters))
                ChapterList.ItemsSource = chapters;
            ChapterList.UnselectAll();
            ShowEntries();
            UpdateButtonStates();
        }

        private void ChapterList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ShowEntries();
            UpdateButtonStates();
        }

        /// <summary>Entries of the selected chapters, else of the selected books, else nothing.</summary>
        private void ShowEntries()
        {
            var lines = new List<VerseLine>();
            List<ChapterNode> chapters = SelectedChapters();
            if (chapters.Count > 0)
            {
                chapters.Sort(delegate(ChapterNode a, ChapterNode b) { return a.Chapter.CompareTo(b.Chapter); });
                foreach (ChapterNode c in chapters)
                    lines.AddRange(c.Lines);
            }
            else
            {
                foreach (BookNode b in SelectedBooks())
                    foreach (ChapterNode c in b.Chapters)
                        lines.AddRange(c.Lines);
            }
            EntryGrid.ItemsSource = lines.Count > 0 ? lines : null;
        }

        /// <summary>Buttons act only on what the user selected, so they are disabled without a selection.</summary>
        private void UpdateButtonStates()
        {
            bool anyBook = BookList.SelectedItems.Count > 0;
            bool anyChapter = ChapterList.SelectedItems.Count > 0;
            HideBookBtn.IsEnabled = anyBook;
            UnhideBookBtn.IsEnabled = anyBook;
            HideChaptersBtn.IsEnabled = anyChapter;
            UnhideChaptersBtn.IsEnabled = anyChapter;
            UnhideAllBtn.IsEnabled = _doc != null;
            AddPassageBtn.IsEnabled = _doc != null;
            bool added = SelectedAddedPassage() != null;
            EditPassageBtn.IsEnabled = added;
            RemovePassageBtn.IsEnabled = added;
            ReaddMenuItem.IsEnabled = _doc != null;
        }

        /// <summary>The passage of the selected entries when they all belong to one passage added with this editor.</summary>
        private PassageGroup SelectedAddedPassage()
        {
            PassageGroup found = null;
            foreach (object item in EntryGrid.SelectedItems)
            {
                VerseLine v = item as VerseLine;
                if (v == null || !v.IsUserAdded) return null;
                if (found != null && found != v.Passage) return null;
                found = v.Passage;
            }
            return found;
        }

        private void EntryGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateButtonStates();
        }

        private void EntryGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (SelectedAddedPassage() != null)
                EditPassage_Click(sender, e);
        }

        // -- Passages added with this editor ------------------------------------

        private void AddPassage_Click(object sender, RoutedEventArgs e)
        {
            if (_doc == null) return;
            var dlg = new AddPassageWindow(_doc, null);
            dlg.Owner = this;
            if (dlg.ShowDialog() != true) return;
            PassageGroup added = null;
            try
            {
                added = ParallelPassagesFileService.AddPassage(_doc, dlg.References);
            }
            catch (InvalidOperationException ex)
            {
                ShowPassageError(ex);
                return;
            }
            AfterStructureChange(added);
        }

        private void EditPassage_Click(object sender, RoutedEventArgs e)
        {
            PassageGroup passage = SelectedAddedPassage();
            if (_doc == null || passage == null) return;
            var dlg = new AddPassageWindow(_doc, passage);
            dlg.Owner = this;
            if (dlg.ShowDialog() != true) return;
            PassageGroup changed = null;
            try
            {
                changed = ParallelPassagesFileService.ReplacePassage(_doc, passage, dlg.References);
            }
            catch (InvalidOperationException ex)
            {
                ShowPassageError(ex);
                return;
            }
            AfterStructureChange(changed);
        }

        private void RemovePassage_Click(object sender, RoutedEventArgs e)
        {
            PassageGroup passage = SelectedAddedPassage();
            if (_doc == null || passage == null) return;
            MessageBoxResult r = MessageBox.Show(this,
                string.Format(GetStr("Dlg_RemovePassageBody"), string.Join(", ", passage.References)),
                GetStr("Dlg_RemovePassageTitle"), MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r != MessageBoxResult.Yes) return;

            // Keep showing the same book and chapters.
            string book = null;
            var chapters = new List<int>();
            List<BookNode> books = SelectedBooks();
            if (books.Count == 1) book = books[0].Book;
            foreach (ChapterNode c in SelectedChapters()) chapters.Add(c.Chapter);
            try
            {
                ParallelPassagesFileService.RemovePassage(_doc, passage);
            }
            catch (InvalidOperationException ex)
            {
                ShowPassageError(ex);
                return;
            }
            Rebind(book, chapters, null);
        }

        private void Readd_Click(object sender, RoutedEventArgs e)
        {
            if (_doc == null) return;
            List<List<string>> missing = MissingAddedPassages();
            if (missing == null || missing.Count == 0)
            {
                MessageBox.Show(this, GetStr("Dlg_ReaddNone"), GetStr("Dlg_ReaddTitle"),
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            OfferAddedIfMissing();
        }

        private void ShowPassageError(Exception ex)
        {
            MessageBox.Show(this, ex.Message, GetStr("Dlg_PassageErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
        }

        /// <summary>The books were rebuilt: show the passage's first entry (its book and chapter) selected.</summary>
        private void AfterStructureChange(PassageGroup passage)
        {
            if (passage == null || passage.Lines.Count == 0)
            {
                Rebind(null, null, null);
                return;
            }
            VerseLine first = passage.Lines[0];
            Rebind(first.Book, new List<int> { first.Chapter }, first);
        }

        private void Rebind(string book, List<int> chapters, VerseLine select)
        {
            BookList.ItemsSource = _doc.Books;
            BookList.UnselectAll();
            ChapterList.ItemsSource = null;
            EntryGrid.ItemsSource = null;
            BookNode bookNode = book == null ? null : _doc.Books.FirstOrDefault(b => b.Book == book);
            if (bookNode != null)
            {
                BookList.SelectedItem = bookNode;
                BookList.ScrollIntoView(bookNode);
                if (chapters != null)
                    foreach (ChapterNode c in bookNode.Chapters)
                        if (chapters.Contains(c.Chapter))
                        {
                            ChapterList.SelectedItems.Add(c);
                            ChapterList.ScrollIntoView(c);
                        }
            }
            if (select != null && EntryGrid.ItemsSource != null)
            {
                EntryGrid.SelectedItem = select;
                EntryGrid.ScrollIntoView(select);
            }
            RefreshAll();
            UpdateButtonStates();
            CommandManager.InvalidateRequerySuggested();
        }

        /// <summary>Selected books in Bible order (SelectedItems is in click order).</summary>
        private List<BookNode> SelectedBooks()
        {
            List<BookNode> books = BookList.SelectedItems.OfType<BookNode>().ToList();
            if (_doc != null)
                books.Sort(delegate(BookNode a, BookNode b) { return _doc.Books.IndexOf(a).CompareTo(_doc.Books.IndexOf(b)); });
            return books;
        }

        private List<ChapterNode> SelectedChapters()
        {
            return ChapterList.SelectedItems.OfType<ChapterNode>().ToList();
        }

        // -- Hiding and unhiding ------------------------------------------------

        private void BookCheck_Click(object sender, RoutedEventArgs e)
        {
            BookNode book = ((FrameworkElement)sender).DataContext as BookNode;
            if (book == null) return;
            // Ticking (or clicking a partly hidden book) hides everything; unticking unhides.
            book.SetHidden(book.IsHidden != true);
            SelectClickedRow(BookList, book);
            RefreshAll();
        }

        private void ChapterCheck_Click(object sender, RoutedEventArgs e)
        {
            ChapterNode ch = ((FrameworkElement)sender).DataContext as ChapterNode;
            if (ch == null) return;
            ch.SetHidden(ch.IsHidden != true);
            SelectClickedRow(ChapterList, ch);
            RefreshAll();
        }

        /// <summary>
        /// The checkbox swallows the mouse click, so its row would not be selected; select it
        /// as a click on the row would (Ctrl adds to the selection, otherwise it replaces it).
        /// </summary>
        private static void SelectClickedRow(ListBox list, object item)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
            {
                if (!list.SelectedItems.Contains(item))
                    list.SelectedItems.Add(item);
                return;
            }
            if (list.SelectedItems.Count == 1 && list.SelectedItem == item) return;
            list.SelectedItems.Clear();
            list.SelectedItem = item;
        }

        private void HideBook_Click(object sender, RoutedEventArgs e)
        {
            foreach (BookNode b in SelectedBooks())
                b.SetHidden(true);
            RefreshAll();
        }

        private void UnhideBook_Click(object sender, RoutedEventArgs e)
        {
            foreach (BookNode b in SelectedBooks())
                b.SetHidden(false);
            RefreshAll();
        }

        private void HideChapters_Click(object sender, RoutedEventArgs e)
        {
            foreach (ChapterNode c in SelectedChapters())
                c.SetHidden(true);
            RefreshAll();
        }

        private void UnhideChapters_Click(object sender, RoutedEventArgs e)
        {
            foreach (ChapterNode c in SelectedChapters())
                c.SetHidden(false);
            RefreshAll();
        }

        private void UnhideAll_Click(object sender, RoutedEventArgs e)
        {
            if (_doc == null) return;
            foreach (BookNode b in _doc.Books)
                b.SetHidden(false);
            RefreshAll();
        }

        private void Reapply_Click(object sender, RoutedEventArgs e)
        {
            if (_doc == null) return;
            HiddenList list = LoadHiddenList();
            if (list == null || list.IsEmpty)
            {
                MessageBox.Show(this, GetStr("Dlg_ReapplyNone"), GetStr("Dlg_ReapplyTitle"),
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            MessageBoxResult r = MessageBox.Show(this,
                string.Format(GetStr("Dlg_ReapplyConfirmBody"), list.Books.Count, list.Chapters.Count, list.Entries.Count),
                GetStr("Dlg_ReapplyTitle"), MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r != MessageBoxResult.Yes) return;
            HiddenListService.Apply(_doc, list);
            RefreshAll();
        }

        /// <summary>
        /// Re-raises every count and state. Needed after any change because a passage can span
        /// books: hiding LUK can keep a JHN entry visible (its passage would drop below 2).
        /// </summary>
        private void RefreshAll()
        {
            if (_doc != null)
            {
                foreach (VerseLine v in _doc.Verses)
                    v.RaiseEffective();
                foreach (BookNode b in _doc.Books)
                {
                    foreach (ChapterNode c in b.Chapters)
                        c.RaiseAll();
                    b.RaiseAll();
                }
            }
            UpdateHeaderAndStatus();
        }

        // -- Header / status ----------------------------------------------------

        private void UpdateHeaderAndStatus()
        {
            if (_doc == null)
            {
                FileHeaderText.Text = GetStr("FileBar_None");
                StatusText.Text = "";
                Title = GetStr("App_Title");
                return;
            }

            bool dirty = _doc.IsDirty;
            FileHeaderText.Text = string.Format(GetStr("FileBar_Format"), _doc.Path)
                + (_projectDir != null ? "      " + string.Format(GetStr("ProjectBar_Format"), ProjectName) : "");
            FileHeaderText.ToolTip = _doc.Path;
            int wholePassages = _doc.PassagesHiddenEntirelyCount;
            string status = wholePassages > 0
                ? string.Format(GetStr("Status_SummaryKeptFormat"), _doc.HiddenCount, _doc.Verses.Count,
                    _doc.WithPassageCount, wholePassages)
                : string.Format(GetStr("Status_SummaryFormat"), _doc.HiddenCount, _doc.Verses.Count);
            int added = _doc.UserAddedCount;
            if (added > 0)
                status += "; " + string.Format(GetStr("Status_AddedFormat"), added);
            if (dirty) status += "  " + GetStr("Status_Unsaved");
            StatusText.Text = status;
            Title = GetStr("App_Title") + " - " + Path.GetFileName(_doc.Path) + (dirty ? " *" : "");
        }

        // -- File commands ------------------------------------------------------

        private void Always_CanExecute(object sender, CanExecuteRoutedEventArgs e)
        {
            e.CanExecute = true;
        }

        private void HasDocument_CanExecute(object sender, CanExecuteRoutedEventArgs e)
        {
            e.CanExecute = _doc != null;
        }

        private void Open_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            if (!ConfirmDiscardChanges()) return;
            string path = AskOpenPath();
            if (path == null) return;
            if (!LoadFile(path)) return;
            OfferAddedIfMissing();
            if (!OfferProjectList())
                OfferReapplyIfNothingHidden();
        }

        private string AskOpenPath()
        {
            var dlg = new OpenFileDialog();
            dlg.Title = GetStr("Dlg_OpenTitle");
            dlg.Filter = "ParallelPassages.xml|ParallelPassages.xml|XML (*.xml)|*.xml|*.*|*.*";
            if (_doc != null)
                dlg.InitialDirectory = Path.GetDirectoryName(_doc.Path);
            return dlg.ShowDialog(this) == true ? dlg.FileName : null;
        }

        private void Save_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            if (_doc != null) DoSave(_doc.Path);
        }

        private void SaveAs_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            if (_doc == null) return;
            var dlg = new SaveFileDialog();
            dlg.Title = GetStr("Dlg_SaveTitle");
            dlg.Filter = "XML (*.xml)|*.xml|*.*|*.*";
            dlg.FileName = Path.GetFileName(_doc.Path);
            dlg.InitialDirectory = Path.GetDirectoryName(_doc.Path);
            if (dlg.ShowDialog(this) == true)
                DoSave(dlg.FileName);
        }

        private bool DoSave(string targetPath)
        {
            string backup;
            try
            {
                backup = ParallelPassagesFileService.Save(_doc, targetPath);
            }
            catch (XmlException ex)
            {
                MessageBox.Show(this, GetStr("Dlg_InvalidXmlBody") + "\n\n" + ex.Message,
                    GetStr("Dlg_SaveErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
            catch (UnauthorizedAccessException ex)
            {
                bool samePath = string.Compare(Path.GetFullPath(targetPath), Path.GetFullPath(_doc.Path),
                    StringComparison.OrdinalIgnoreCase) == 0;
                if (samePath && !IsElevated())
                {
                    MessageBoxResult r = MessageBox.Show(this,
                        string.Format(GetStr("Dlg_AdminBody"), targetPath),
                        GetStr("Dlg_AdminTitle"), MessageBoxButton.YesNo, MessageBoxImage.Warning);
                    if (r == MessageBoxResult.Yes)
                        RelaunchElevated();
                    return false;
                }
                MessageBox.Show(this, GetStr("Dlg_SaveErrorBody") + "\n" + targetPath + "\n\n" + ex.Message,
                    GetStr("Dlg_SaveErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, GetStr("Dlg_SaveErrorBody") + "\n" + targetPath + "\n\n" + ex.Message,
                    GetStr("Dlg_SaveErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }

            try
            {
                HiddenListService.Save(HiddenListService.Build(_doc), AppPaths.HiddenListPath);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            try
            {
                AddedPassagesService.Save(AddedPassagesService.Build(_doc), AppPaths.AddedListPath);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            SettingsService.Set("LastPath", targetPath);

            // Per-project list: kept (ticked) or removed (unticked), as of the last save of the
            // file the plugin passed. This window runs as administrator and the project folder
            // is writable by every user, so it never writes there: the exit code tells the
            // plugin (running without administrator rights) what to do when this window closes.
            string projectNote = null;
            if (_projectDir != null && _launchPath != null && string.Compare(Path.GetFullPath(targetPath),
                    Path.GetFullPath(_launchPath), StringComparison.OrdinalIgnoreCase) == 0)
            {
                if (ProjectOptionCheck.IsChecked == true)
                {
                    App.PluginExitCode = App.ExitKeepProjectList;
                    projectNote = string.Format(GetStr("Status_SavedForProject"), ProjectName);
                }
                else
                {
                    App.PluginExitCode = App.ExitRemoveProjectList;
                    if (File.Exists(HiddenListService.ProjectListPath(_projectDir)))
                        projectNote = string.Format(GetStr("Status_RemovedForProject"), ProjectName);
                }
            }

            RefreshAll();
            StatusText.Text += "  " + (backup == null
                ? GetStr("Status_Saved")
                : string.Format(GetStr("Status_SavedBackupFormat"), Path.GetFileName(backup)));
            if (projectNote != null)
                StatusText.Text += "  " + projectNote;
            return true;
        }

        private static bool IsElevated()
        {
            try
            {
                var principal = new WindowsPrincipal(WindowsIdentity.GetCurrent());
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>Restarts this exe as administrator, carrying the unsaved hidden state over in a temp file.</summary>
        private void RelaunchElevated()
        {
            string pending = Path.Combine(Path.GetTempPath(),
                "ParallelPassagesEditor_pending_" + Guid.NewGuid().ToString("N") + ".txt");
            try
            {
                ParallelPassagesFileService.WritePending(_doc, pending);
                string args = Quote(_doc.Path) + " --pending " + Quote(pending);
                if (_projectDir != null)
                    args += " --project " + Quote(_projectDir);
                var psi = new ProcessStartInfo(Assembly.GetExecutingAssembly().Location, args);
                psi.UseShellExecute = true;
                psi.Verb = "runas";
                Process.Start(psi);
            }
            catch (Win32Exception)
            {
                // The user cancelled the UAC prompt: stay open with the changes intact.
                TryDelete(pending);
                return;
            }
            catch (Exception ex)
            {
                TryDelete(pending);
                MessageBox.Show(this, GetStr("Dlg_SaveErrorBody") + "\n\n" + ex.Message,
                    GetStr("Dlg_SaveErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            // Deferred: this can run from inside Window_Closing (save prompt), where Close() would throw.
            _closingForRelaunch = true;
            Dispatcher.BeginInvoke(new Action(delegate() { Close(); }));
        }

        private static string Quote(string s)
        {
            return "\"" + s + "\"";
        }

        private static void TryDelete(string path)
        {
            try { File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private bool ConfirmDiscardChanges()
        {
            if (_doc == null || !_doc.IsDirty) return true;
            MessageBoxResult result = MessageBox.Show(this,
                GetStr("Dlg_UnsavedBody"),
                GetStr("Dlg_UnsavedTitle"),
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Warning);
            if (result == MessageBoxResult.Yes) return DoSave(_doc.Path);
            if (result == MessageBoxResult.No) return true;
            return false;
        }

        private void Exit_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            if (_closingForRelaunch) return;
            if (!ConfirmDiscardChanges())
                e.Cancel = true;
        }
    }

    public class LanguageOption
    {
        public string DisplayName { get; set; }
        public string Culture { get; set; }
    }
}
