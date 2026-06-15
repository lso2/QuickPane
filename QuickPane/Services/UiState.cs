using System;
using System.Collections.Generic;
using System.IO;

namespace QuickPane.Services
{
    /// <summary>
    /// Remembers expand/collapse choices so they survive pane rebuilds, profile switches, window
    /// switches, and restarts. Keyed by a stable id (section type, group folder, tree path) and stored
    /// as "key|0/1" lines in %APPDATA%\QuickPane\uistate.txt.
    /// </summary>
    public static class UiState
    {
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, bool> Map = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private static string _file;
        private static bool _loaded;

        private static void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QuickPane");
                _file = Path.Combine(dir, "uistate.txt");
                if (File.Exists(_file))
                {
                    foreach (var line in File.ReadAllLines(_file))
                    {
                        int bar = line.LastIndexOf('|');
                        if (bar <= 0) continue;
                        Map[line.Substring(0, bar)] = line.Substring(bar + 1).Trim() == "1";
                    }
                }
            }
            catch (Exception ex) { Log.Error("uistate load", ex); }
        }

        public static bool GetExpanded(string key, bool dflt = true)
        {
            if (string.IsNullOrEmpty(key)) return dflt;
            lock (Gate)
            {
                EnsureLoaded();
                bool v;
                return Map.TryGetValue(key, out v) ? v : dflt;
            }
        }

        public static void SetExpanded(string key, bool value)
        {
            if (string.IsNullOrEmpty(key)) return;
            lock (Gate)
            {
                EnsureLoaded();
                Map[key] = value;
                Save();
            }
        }

        private static void Save()
        {
            try
            {
                if (_file == null) return;
                Directory.CreateDirectory(Path.GetDirectoryName(_file));
                var lines = new List<string>();
                foreach (var kv in Map) lines.Add(kv.Key + "|" + (kv.Value ? "1" : "0"));
                File.WriteAllLines(_file, lines);
            }
            catch (Exception ex) { Log.Error("uistate save", ex); }
        }
    }
}
