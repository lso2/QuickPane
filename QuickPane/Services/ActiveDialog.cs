using System;

namespace QuickPane.Services
{
    /// <summary>
    /// Which application's file dialog the pane is currently attached to. The per-app sections need to
    /// know whose dialog they are sitting in, and only the dialog watcher knows that, so it publishes
    /// the name here rather than every section reaching into the watcher.
    /// </summary>
    internal static class ActiveDialog
    {
        private static string _app;

        public static event Action Changed;

        /// <summary>Process name of the app whose dialog has a pane, or null when none does.</summary>
        public static string App
        {
            get { return _app; }
            set
            {
                if (string.Equals(_app, value, StringComparison.OrdinalIgnoreCase)) return;
                _app = value;
                var h = Changed;
                if (h != null) h();
            }
        }
    }
}
