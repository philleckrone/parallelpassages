using System.Collections.Generic;

namespace ParallelPassagesEditor.Models
{
    /// <summary>
    /// The &lt;Verse&gt; entries of one &lt;Passage&gt;, plus its &lt;Passage&gt; / &lt;/Passage&gt; lines.
    /// A parallel with fewer than 2 entries left makes no sense (and an empty or one-entry
    /// passage can stop Paratext from loading the file), so when the requested hiding would
    /// leave fewer than 2 visible entries, the whole passage is commented out.
    /// </summary>
    public class PassageGroup
    {
        public const int MinVisible = 2;

        private readonly List<VerseLine> _lines = new List<VerseLine>();

        public int Index { get; set; }
        public MarkerLine OpenLine { get; set; }    // <Passage>, null if not found
        public MarkerLine CloseLine { get; set; }   // </Passage>, null if not found

        /// <summary>
        /// Line index of the "Added by Parallel Passages Editor" comment right before &lt;Passage&gt;,
        /// or -1. Only such passages may be edited or removed; the stock ones can only be hidden.
        /// </summary>
        public int AddedMarkerLineIndex { get; set; }

        public PassageGroup()
        {
            AddedMarkerLineIndex = -1;
        }

        public bool IsUserAdded
        {
            get { return AddedMarkerLineIndex >= 0 && OpenLine != null && CloseLine != null; }
        }

        /// <summary>The references of the entries, in file order.</summary>
        public List<string> References
        {
            get
            {
                var refs = new List<string>();
                foreach (VerseLine l in _lines)
                    refs.Add(l.Reference);
                return refs;
            }
        }

        public IList<VerseLine> Lines
        {
            get { return _lines; }
        }

        public int RequestedVisibleCount
        {
            get
            {
                int n = 0;
                foreach (VerseLine l in _lines)
                    if (!l.IsHidden) n++;
                return n;
            }
        }

        /// <summary>Something is asked to be hidden and fewer than 2 entries would be left.</summary>
        public bool IsHiddenEntirely
        {
            get
            {
                // Only possible when both marker lines are known, so every line can be commented out.
                if (OpenLine == null || CloseLine == null) return false;
                bool any = false;
                foreach (VerseLine l in _lines)
                    if (l.IsHidden) { any = true; break; }
                return any && RequestedVisibleCount < MinVisible;
            }
        }

        public int EffectiveVisibleCount
        {
            get
            {
                int n = 0;
                foreach (VerseLine l in _lines)
                    if (!l.IsEffectivelyHidden) n++;
                return n;
            }
        }
    }

    /// <summary>A &lt;Passage&gt; or &lt;/Passage&gt; line; commented out when its passage is hidden entirely.</summary>
    public class MarkerLine
    {
        public int LineIndex { get; set; }
        public string VisibleText { get; set; }   // the line as it reads when not commented out
        public string SavedText { get; set; }     // exact text read from (or last written to) disk
        public bool SavedHidden { get; set; }
        public PassageGroup Passage { get; set; }

        public bool IsEffectivelyHidden
        {
            get { return Passage != null && Passage.IsHiddenEntirely; }
        }

        public bool IsChanged
        {
            get { return IsEffectivelyHidden != SavedHidden; }
        }

        public string Render()
        {
            bool hidden = IsEffectivelyHidden;
            if (hidden == SavedHidden) return SavedText;
            return hidden ? "<!--" + VisibleText + " -->" : VisibleText;
        }

        public void CommitSaved()
        {
            SavedText = Render();
            SavedHidden = IsEffectivelyHidden;
        }
    }
}
