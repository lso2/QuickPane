using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace QuickPane.Services
{
    /// <summary>
    /// A folder each application should start in, chosen by the user, independent of whatever Windows
    /// last remembered for that app's dialog. Set from a pane folder's context menu while that app's
    /// dialog is open, and offered at the top of the pane whenever that app asks for a file again.
    /// </summary>
    public sealed class AppDefaultsService
    {
        private readonly Dictionary<string, string> _map =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly string _file;
        private readonly object _gate = new object();

        public event Action Changed;

        public AppDefaultsService()
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QuickPane");
            _file = Path.Combine(dir, "appdefaults.txt");
            Load();
        }

        /// <summary>The folder chosen for an app, or null when it has none.</summary>
        public string For(string app)
        {
            if (string.IsNullOrWhiteSpace(app)) return null;
            lock (_gate)
            {
                string v;
                return _map.TryGetValue(app, out v) ? v : null;
            }
        }

        public IReadOnlyList<KeyValuePair<string, string>> All
        {
            get { lock (_gate) return _map.OrderBy(k => k.Key).ToList(); }
        }

        public void Set(string app, string folder)
        {
            if (string.IsNullOrWhiteSpace(app) || string.IsNullOrWhiteSpace(folder)) return;
            lock (_gate) _map[app] = folder.TrimEnd('\\');
            Save();
            var h = Changed; if (h != null) h();
        }

        public void Remove(string app)
        {
            if (string.IsNullOrWhiteSpace(app)) return;
            lock (_gate) { if (!_map.Remove(app)) return; }
            Save();
            var h = Changed; if (h != null) h();
        }

        private void Load()
        {
            try
            {
                if (!File.Exists(_file)) return;
                foreach (var line in File.ReadAllLines(_file))
                {
                    var parts = line.Split(new[] { '|' }, 2);
                    if (parts.Length == 2 && parts[0].Length > 0) _map[parts[0]] = parts[1];
                }
            }
            catch (Exception ex) { Log.Error("app defaults load", ex); }
        }

        private void Save()
        {
            try
            {
                List<KeyValuePair<string, string>> snapshot;
                lock (_gate) snapshot = _map.ToList();
                Directory.CreateDirectory(Path.GetDirectoryName(_file));
                File.WriteAllLines(_file, snapshot.Select(kv => kv.Key + "|" + kv.Value));
            }
            catch (Exception ex) { Log.Error("app defaults save", ex); }
        }
    }
}
