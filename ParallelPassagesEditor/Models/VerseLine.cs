using System.ComponentModel;
using ParallelPassagesEditor.Services;

namespace ParallelPassagesEditor.Models
{
    /// <summary>
    /// One &lt;Verse&gt; line of ParallelPassages.xml. A line is "hidden" when it is wrapped
    /// in a single-line XML comment: &lt;!--    &lt;Verse ...&gt;JHN 20:1&lt;/Verse&gt; --&gt;
    /// IsHidden is what the user asked for; IsEffectivelyHidden is what gets written. They differ
    /// when the whole passage is hidden (fewer than 2 entries would be left): the other entries
    /// are then hidden too, tagged "(with passage)" so they come back when the passage does.
    /// </summary>
    public class VerseLine : INotifyPropertyChanged
    {
        public const string WithPassageTag = "(with passage)";

        private bool _isHidden;

        public int LineIndex { get; set; }
        public int PassageIndex { get; set; }
        public PassageGroup Passage { get; set; }
        public string Book { get; set; }
        public int Chapter { get; set; }
        public string Reference { get; set; }      // e.g. "MRK 16:1-2"
        public string Language { get; set; }       // HEB or GRK attribute name
        public string VerseElement { get; set; }   // trimmed <Verse ...>...</Verse>
        public string VisibleText { get; set; }    // the line as it reads when not commented out

        // The exact text read from (or last written to) disk, so unchanged lines round-trip byte-for-byte.
        public string SavedText { get; set; }
        public bool SavedHidden { get; set; }
        public bool SavedWithPassage { get; set; }

        public string ParallelWith { get; set; }   // other references in the same <Passage>

        /// <summary>What the user asked for.</summary>
        public bool IsHidden
        {
            get { return _isHidden; }
            set
            {
                if (_isHidden == value) return;
                _isHidden = value;
                OnPropertyChanged("IsHidden");
            }
        }

        private bool PassageHiddenEntirely
        {
            get { return Passage != null && Passage.IsHiddenEntirely; }
        }

        /// <summary>What is written to the file.</summary>
        public bool IsEffectivelyHidden
        {
            get { return _isHidden || PassageHiddenEntirely; }
        }

        /// <summary>Not asked to be hidden, but hidden because its whole passage is.</summary>
        public bool IsHiddenWithPassage
        {
            get { return !_isHidden && PassageHiddenEntirely; }
        }

        /// <summary>In a passage added with this editor (which can be edited or removed).</summary>
        public bool IsUserAdded
        {
            get { return Passage != null && Passage.IsUserAdded; }
        }

        public string Note
        {
            get
            {
                string note = IsHiddenWithPassage ? Loc.Get("Note_Kept") : "";
                if (IsUserAdded)
                    note = note.Length == 0 ? Loc.Get("Note_Added") : Loc.Get("Note_Added") + "; " + note;
                return note;
            }
        }

        public bool IsChanged
        {
            get { return IsEffectivelyHidden != SavedHidden || IsHiddenWithPassage != SavedWithPassage; }
        }

        public string Render()
        {
            bool hidden = IsEffectivelyHidden;
            bool withPassage = IsHiddenWithPassage;
            if (hidden == SavedHidden && withPassage == SavedWithPassage) return SavedText;
            if (hidden) return "<!--" + VisibleText + (withPassage ? " " + WithPassageTag : "") + " -->";
            return VisibleText;
        }

        public void CommitSaved()
        {
            SavedText = Render();
            SavedHidden = IsEffectivelyHidden;
            SavedWithPassage = IsHiddenWithPassage;
        }

        public void RaiseEffective()
        {
            OnPropertyChanged("IsEffectivelyHidden");
            OnPropertyChanged("IsHiddenWithPassage");
            OnPropertyChanged("Note");
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged(string name)
        {
            PropertyChangedEventHandler handler = PropertyChanged;
            if (handler != null) handler(this, new PropertyChangedEventArgs(name));
        }
    }
}
