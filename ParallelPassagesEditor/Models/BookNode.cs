using System.Collections.Generic;
using System.ComponentModel;

namespace ParallelPassagesEditor.Models
{
    public class BookNode : INotifyPropertyChanged
    {
        private readonly List<ChapterNode> _chapters = new List<ChapterNode>();

        public string Book { get; set; }

        public IList<ChapterNode> Chapters
        {
            get { return _chapters; }
        }

        public void AddChapter(ChapterNode chapter)
        {
            _chapters.Add(chapter);
        }

        /// <summary>The user's choice: true = all asked hidden, false = none, null = some.</summary>
        public bool? IsHidden
        {
            get
            {
                bool any = false, all = true;
                foreach (ChapterNode c in _chapters)
                {
                    bool? s = c.IsHidden;
                    if (s != false) any = true;
                    if (s != true) all = false;
                }
                if (!any) return false;
                if (all) return true;
                return null;
            }
        }

        public int HiddenCount
        {
            get
            {
                int n = 0;
                foreach (ChapterNode c in _chapters)
                    n += c.HiddenCount;
                return n;
            }
        }

        public int WithPassageCount
        {
            get
            {
                int n = 0;
                foreach (ChapterNode c in _chapters)
                    n += c.WithPassageCount;
                return n;
            }
        }

        public int TotalCount
        {
            get
            {
                int n = 0;
                foreach (ChapterNode c in _chapters)
                    n += c.TotalCount;
                return n;
            }
        }

        public string CountText
        {
            get { return ChapterNode.FormatCount(HiddenCount, TotalCount, WithPassageCount); }
        }

        /// <summary>Sets the requested state; call RaiseAll (via MainWindow.RefreshAll) afterwards.</summary>
        public void SetHidden(bool hidden)
        {
            foreach (ChapterNode c in _chapters)
                c.SetHidden(hidden);
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
