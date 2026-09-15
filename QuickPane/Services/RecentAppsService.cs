using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace QuickPane.Services
{
    /// <summary>
    /// The applications you have been using, so reopening one is a click in the pane. Two things put an
    /// app on the list: bringing it to the front, and using its Save or Open dialog. Which of those the
    /// pane shows is a setting, because "the apps I work in" and "the apps I save from" are different
    /// lists to different people.
    ///
    /// Windows keeps its own tally of program launches in UserAssist, but it is empty on machines where
    /// activity history is turned off or a cleaner has been through, so the list is built here instead of
    /// read from there.
    /// </summary>
    public sealed class RecentAppsService
    {
        public sealed class Entry
        {
            public string Name;        // what the program calls itself, e.g. "Microsoft Excel"
            public string ExePath;     // what a click launches
            public string Folder;      // the folder its dialog was last sent to, when there is one
            public DateTime When;
            public bool FromDialog;    // seen owning a file dialog
            public bool FromOpen;      // seen brought to the front
        }

        private const int MaxEntries = 20;

        private readonly List<Entry> _items = new List<Entry>();
        private readonly string _file;
        private readonly object _gate = new object();

        /// <summary>Raised when the list changes. The flag is true when a person asked for it, which is
        /// what tells the pane to redraw at once rather than waiting for a quiet moment.</summary>
        public event Action<bool> Changed;

        private void Raise(bool asked)
        {
            var h = Changed;
            if (h != null) h(asked);
        }

        public RecentAppsService()
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QuickPane");
            _file = Path.Combine(dir, "recentapps.txt");
            Load();
        }

        /// <summary>Everything recorded, most recent first.</summary>
        public IReadOnlyList<Entry> Items { get { lock (_gate) return _items.ToList(); } }

        /// <summary>The entries a given setting asks for, most recent first. An app can have been seen
        /// both ways, or only one: a program that raises a save dialog without ever being switched to is
        /// known only from the dialog, and one you work in but never save from only from the front.</summary>
        public IReadOnlyList<Entry> ItemsFor(string source)
        {
            var mode = (source ?? "both").Trim().ToLowerInvariant();
            lock (_gate)
            {
                if (mode == "dialogs") return _items.Where(e => e.FromDialog).ToList();
                if (mode == "open") return _items.Where(e => e.FromOpen).ToList();
                return _items.ToList();
            }
        }

        /// <summary>An app whose file dialog the pane was sent to a folder from.</summary>
        public void RecordDialog(string exePath, string folder)
        {
            Record(exePath, folder, true, false);
        }

        /// <summary>An app brought to the front.</summary>
        public void RecordRunning(string exePath)
        {
            Record(exePath, null, false, true);
        }

        private void Record(string exePath, string folder, bool fromDialog, bool fromOpen)
        {
            if (string.IsNullOrWhiteSpace(exePath)) return;
            if (!IsWorthListing(exePath)) return;

            string name = FriendlyName(exePath);
            if (string.IsNullOrWhiteSpace(name)) return;

            lock (_gate)
            {
                var existing = _items.FirstOrDefault(
                    e => string.Equals(e.ExePath, exePath, StringComparison.OrdinalIgnoreCase));
                if (existing != null) _items.Remove(existing);

                _items.Insert(0, new Entry
                {
                    Name = name,
                    ExePath = exePath,
                    // A later sighting that carries no folder must not erase the folder already known.
                    Folder = !string.IsNullOrWhiteSpace(folder)
                        ? folder.TrimEnd('\\')
                        : (existing != null ? existing.Folder : null),
                    When = DateTime.Now,
                    FromDialog = fromDialog || (existing != null && existing.FromDialog),
                    FromOpen = fromOpen || (existing != null && existing.FromOpen)
                });

                while (_items.Count > MaxEntries) _items.RemoveAt(_items.Count - 1);
            }

            Save();
            Raise(false);
        }

        public void Forget(string exePath)
        {
            if (string.IsNullOrWhiteSpace(exePath)) return;
            lock (_gate)
                _items.RemoveAll(e => string.Equals(e.ExePath, exePath, StringComparison.OrdinalIgnoreCase));
            Save();
            Raise(true);
        }

        public void Clear()
        {
            lock (_gate) _items.Clear();
            Save();
            Raise(true);
        }

        // ---- what counts as an application -----------------------------

        /// <summary>The shell's own pieces and QuickPane itself are always on screen and are never what
        /// somebody means by a recent app, so they never reach the list.</summary>
        private static readonly string[] NeverList =
        {
            "explorer.exe", "quickpane.exe", "applicationframehost.exe", "shellexperiencehost.exe",
            "textinputhost.exe", "searchhost.exe", "searchapp.exe", "startmenuexperiencehost.exe",
            "systemsettings.exe", "lockapp.exe", "dwm.exe", "sihost.exe", "runtimebroker.exe",
            "widgets.exe", "widgetboard.exe", "peopleexperiencehost.exe", "rundll32.exe",
            "openwith.exe", "dllhost.exe", "taskhostw.exe", "ctfmon.exe", "backgroundtaskhost.exe"
        };

        private static bool IsWorthListing(string exePath)
        {
            try
            {
                if (!File.Exists(exePath)) return false;

                var leaf = Path.GetFileName(exePath);
                if (NeverList.Any(n => string.Equals(n, leaf, StringComparison.OrdinalIgnoreCase))) return false;

                // Everything under SystemApps is shell furniture rather than something anyone launches.
                var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                if (!string.IsNullOrEmpty(windows) &&
                    exePath.StartsWith(Path.Combine(windows, "SystemApps"), StringComparison.OrdinalIgnoreCase))
                    return false;

                return true;
            }
            catch (Exception ex) { Log.Error("check '" + exePath + "'", ex); return false; }
        }

        /// <summary>
        /// What the program calls itself, cut back to the name. Some installers put a whole sentence in
        /// the description field ("WinSCP: SFTP, FTP, WebDAV, S3 and SCP client"), and a pane column is
        /// not wide enough to spend on the rest of it, so the name is taken up to the first punctuation
        /// that starts a description and the file name is used when nothing short is left.
        /// </summary>
        private static string FriendlyName(string exePath)
        {
            try
            {
                var d = (FileVersionInfo.GetVersionInfo(exePath).FileDescription ?? "").Trim();
                foreach (var cut in new[] { ":", " - ", " \u2013 ", ", " })
                {
                    int i = d.IndexOf(cut, StringComparison.Ordinal);
                    if (i > 0) d = d.Substring(0, i).Trim();
                }
                if (d.Length > 0 && d.Length <= 40) return d + Channel(exePath, d);
            }
            catch (Exception ex) { Log.Error("read the name of '" + exePath + "'", ex); }

            try { return Path.GetFileNameWithoutExtension(exePath); }
            catch { return null; }
        }

        /// <summary>
        /// Which side-by-side build this is, when the program does not say so itself. Chrome Canary and
        /// Chrome are separate installs that both describe themselves as "Google Chrome", so without
        /// this the two are one indistinguishable row.
        /// </summary>
        private static readonly Tuple<string, string>[] Channels =
        {
            Tuple.Create(@"Chrome SxS", "Canary"),
            Tuple.Create(@"Chrome Beta", "Beta"),
            Tuple.Create(@"Chrome Dev", "Dev"),
            Tuple.Create(@"Edge SxS", "Canary"),
            Tuple.Create(@"Edge Beta", "Beta"),
            Tuple.Create(@"Edge Dev", "Dev"),
            Tuple.Create(@"Brave-Browser-Nightly", "Nightly"),
            Tuple.Create(@"Brave-Browser-Beta", "Beta"),
            Tuple.Create(@"Firefox Nightly", "Nightly"),
            Tuple.Create(@"Firefox Developer Edition", "Developer Edition")
        };

        private static string Channel(string exePath, string name)
        {
            foreach (var c in Channels)
            {
                if (exePath.IndexOf(c.Item1, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (name.IndexOf(c.Item2, StringComparison.OrdinalIgnoreCase) >= 0) return "";
                return " " + c.Item2;
            }
            return "";
        }

        // ---- storage ---------------------------------------------------

        private void Load()
        {
            try
            {
                if (!File.Exists(_file)) return;
                foreach (var line in File.ReadAllLines(_file))
                {
                    var parts = line.Split(new[] { '|' }, 5);
                    if (parts.Length < 4) continue;   // an entry from before apps had paths

                    DateTime when;
                    if (!DateTime.TryParse(parts[3], out when)) when = DateTime.MinValue;

                    var flags = parts.Length > 4 ? parts[4] : "";
                    _items.Add(new Entry
                    {
                        Name = parts[0],
                        ExePath = parts[1],
                        Folder = parts[2].Length == 0 ? null : parts[2],
                        When = when,
                        FromDialog = flags.IndexOf("dialog", StringComparison.OrdinalIgnoreCase) >= 0,
                        FromOpen = flags.IndexOf("open", StringComparison.OrdinalIgnoreCase) >= 0
                    });
                }
                while (_items.Count > MaxEntries) _items.RemoveAt(_items.Count - 1);
            }
            catch (Exception ex) { Log.Error("recent apps load", ex); }
        }

        private void Save()
        {
            try
            {
                List<Entry> snapshot;
                lock (_gate) snapshot = _items.ToList();
                Directory.CreateDirectory(Path.GetDirectoryName(_file));
                File.WriteAllLines(_file, snapshot.Select(e =>
                    e.Name + "|" + e.ExePath + "|" + (e.Folder ?? "") + "|" + e.When.ToString("o") + "|" +
                    ((e.FromDialog ? "dialog" : "") + (e.FromOpen ? ",open" : "")).Trim(',')));
            }
            catch (Exception ex) { Log.Error("recent apps save", ex); }
        }
    }
}
