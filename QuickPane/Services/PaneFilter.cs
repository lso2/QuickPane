using System;

namespace QuickPane.Services
{
    /// <summary>
    /// The live filter text typed into the pane's search box. One value shared by every pane, so typing
    /// in one Explorer window's box filters that pane and the setting is not duplicated per section.
    /// Sections do not read it while building; the pane applies it to the rows it has already built,
    /// which keeps the filter out of every section's construction path.
    /// </summary>
    internal static class PaneFilter
    {
        private static string _text = "";

        public static event Action Changed;

        public static string Text
        {
            get { return _text; }
            set
            {
                var v = (value ?? "").Trim();
                if (string.Equals(v, _text, StringComparison.Ordinal)) return;
                _text = v;
                var h = Changed;
                if (h != null) h();
            }
        }

        public static bool Active { get { return _text.Length > 0; } }

        /// <summary>True when a row's label should stay visible. An empty filter matches everything.</summary>
        public static bool Matches(string label)
        {
            if (_text.Length == 0) return true;
            if (string.IsNullOrEmpty(label)) return false;
            return label.IndexOf(_text, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
