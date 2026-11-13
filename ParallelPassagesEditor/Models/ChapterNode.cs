using System.Collections.Generic;
using System.ComponentModel;
using ParallelPassagesEditor.Services;

namespace ParallelPassagesEditor.Models
{
    public class ChapterNode : INotifyPropertyChanged
    {
        private readonly List<VerseLine> _lines = new List<VerseLine>();

        public string Book { get; set; }
        public int Chapter { get; set; }

        public IList<VerseLine> Lines
        {
            get { return _lines; }
        }

        public void AddLine(VerseLine line)
        {
            _lines.Add(line);
        }

        /// <summary>The user's choice: true = all asked hidden, false = none, null = some.</summary>
        public bool? IsHidden
        {
            get
            {
                int requested = 0;
                foreach (VerseLine l in _lines)
                    if (l.IsHidden) requested++;
                if (requested == 0) return false;
                if (requested == _lines.Count) return true;
                return null;
            }
        }

        /// <summary>Entries actually hidden in the file.</summary>
        public int HiddenCount
        {
            get
            {
                int n = 0;
                foreach (VerseLine l in _lines)
                    if (l.IsEffectivelyHidden) n++;
                return n;
            }
        }

        /// <summary>Entries not asked to be hidden, but hidden because their whole passage is.</summary>
        public int WithPassageCount
        {
            get
            {
                int n = 0;
                foreach (VerseLine l in _lines)
                    if (l.IsHiddenWithPassage) n++;
                return n;
            }
        }

        public int TotalCount
        {
            get { return _lines.Count; }
        }

        public string Label
        {
            get { return Book + " " + Chapter; }
        }

        public string CountText
        {
            get { return FormatCount(HiddenCount, TotalCount, WithPassageCount); }
        }

        public static string FormatCount(int hidden, int total, int kept)
        {
            if (kept > 0)
                return string.Format(Loc.Get("Count_KeptFormat"), hidden, total, kept);
            return string.Format(Loc.Get("Count_Format"), hidden, total);
        }

        /// <summary>Sets the requested state; call RaiseAll (via MainWindow.RefreshAll) afterwards.</summary>
        public void SetHidden(bool hidden)
        {
            foreach (VerseLine l in _lines)
                l.IsHidden = hidden;
        }

        public void RaiseAll()
        {
            OnPropertyChanged("IsHidden");
            OnPropertyChanged("HiddenCount");
            OnPropertyChanged("WithPassageCount");
            OnPropertyChanged("CountText");
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged(string name)
        {
            PropertyChangedEventHandler handler = PropertyChanged;
            if (handler != null) handler(this, new PropertyChangedEventArgs(name));
        }
    }
}
