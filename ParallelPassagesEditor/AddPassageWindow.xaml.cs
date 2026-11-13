using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using ParallelPassagesEditor.Models;
using ParallelPassagesEditor.Services;

namespace ParallelPassagesEditor
{
    /// <summary>
    /// Asks for the references of a new passage (or of one added earlier, when editing): one per
    /// line. OK is enabled only when every reference passes PassageReferences.Parse.
    /// </summary>
    public partial class AddPassageWindow : Window
    {
        private readonly ParallelPassagesDocument _doc;
        private readonly PassageGroup _editing;

        /// <summary>The checked references, set when the dialog closes with OK.</summary>
        public List<string> References { get; private set; }

        public AddPassageWindow(ParallelPassagesDocument doc, PassageGroup editing)
        {
            InitializeComponent();
            _doc = doc;
            _editing = editing;
            if (editing != null)
            {
                Title = Loc.Get("Dlg_EditPassageTitle");
                RefsBox.Text = string.Join("\r\n", editing.References);
            }
            NoteText.Text = Versification.IsAvailable ? "" : Loc.Get("Dlg_AddPassageNoVrs");
            NoteText.Visibility = NoteText.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            Loaded += delegate
            {
                RefsBox.Focus();
                RefsBox.CaretIndex = RefsBox.Text.Length;
                Check(false);
            };
        }

        private List<string> Check(bool showTooFew)
        {
            var errors = new List<string>();
            List<string> refs = PassageReferences.Parse(RefsBox.Text, _doc, _editing, errors);
            // While typing, "needs at least 2" is only noise; it is shown when OK is pressed.
            if (!showTooFew)
                errors.Remove(Loc.Get("Err_TooFew"));
            ErrorText.Text = string.Join("\n", errors);
            OkBtn.IsEnabled = errors.Count == 0;
            return errors.Count == 0 && refs.Count >= PassageGroup.MinVisible ? refs : null;
        }

        private void RefsBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (IsLoaded) Check(false);
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            List<string> refs = Check(true);
            if (refs == null) return;
            References = refs;
            DialogResult = true;
        }
    }
}
