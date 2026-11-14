// BesideApps.cs
// Purpose: the stored beside list: apps whose Save and Open dialogs put their controls back over the inside pane.
// Reason: without a stored list, the first dialog of each such app flashed the inside pane and jumped in every session.
// Defines: BesideApps.
// Doc: docs/notes/QuickPane/Services/BesideApps.cs.md
using System;
using System.Collections.Generic;
using System.IO;

namespace QuickPane.Services
{
    /// <summary>Process names whose file dialogs open with a beside pane, one per line in
    /// %APPDATA%\QuickPane\besideapps.txt. Only an app whose dialog put its controls back at the same size
    /// is added; a width change under the pane stays a session-only entry in DialogPaneWatcher.</summary>
    internal static class BesideApps
    {
        private static readonly object Gate = new object();
        private static readonly HashSet<string> Names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static string _file;
        private static bool _loaded;

        private static void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QuickPane");
                _file = Path.Combine(dir, "besideapps.txt");
                if (!File.Exists(_file)) return;
                foreach (var line in File.ReadAllLines(_file))
                {
                    var name = line.Trim();
                    if (name.Length > 0) Names.Add(name);
                }
                Log.Info("beside list loaded: " + (Names.Count == 0 ? "empty" : string.Join(", ", Names)) + ".");
            }
            catch (Exception ex) { Log.Info("reading besideapps.txt failed: " + ex.Message); }
        }

        public static bool Contains(string processName)
        {
            if (string.IsNullOrEmpty(processName)) return false;
            lock (Gate)
            {
                EnsureLoaded();
                return Names.Contains(processName);
            }
        }

        public static void Add(string processName)
        {
            if (string.IsNullOrEmpty(processName) || processName == "unknown") return;
            lock (Gate)
            {
                EnsureLoaded();
                if (!Names.Add(processName)) return;
                Save();
            }
            Log.Info(processName + " added to the stored beside list.");
        }

        /// <summary>Empty the stored list. Returns how many apps it held.</summary>
        public static int Clear()
        {
            int n;
            lock (Gate)
            {
                EnsureLoaded();
                n = Names.Count;
                Names.Clear();
                Save();
            }
            Log.Info("stored beside list cleared (" + n + " app(s)).");
            return n;
        }

        // Temp file then replace, the same pattern SettingsStore uses for settings.json.
        private static void Save()
        {
            try
            {
                if (_file == null) return;
                Directory.CreateDirectory(Path.GetDirectoryName(_file));
                var tmp = _file + ".tmp";
                File.WriteAllLines(tmp, Names);
                if (File.Exists(_file)) File.Replace(tmp, _file, null);
                else File.Move(tmp, _file);
            }
            catch (Exception ex) { Log.Info("writing besideapps.txt failed: " + ex.Message); }
        }
    }
}
