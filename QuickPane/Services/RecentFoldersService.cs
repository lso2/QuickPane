using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using QuickPane.Models;

namespace QuickPane.Services
{
    /// <summary>
    /// Surfaces recently used items from %APPDATA%\Microsoft\Windows\Recent plus folders navigated
    /// through QuickPane. All scanning (shortcut resolution, existence checks) runs on the background
    /// worker: the Recent folder routinely holds hundreds of .lnk files, some pointing at dead
    /// network targets, and resolving those on the UI thread stalled the input queue shared with
    /// Explorer. Shortcuts are resolved newest-first and resolution stops once the list is full, so
    /// the cost is bounded by the visible count rather than the folder size. RecentsChanged is always
    /// raised on the UI thread.
    /// </summary>
    public sealed class RecentFoldersService : IDisposable
    {
        public event Action RecentsChanged;

        public IReadOnlyList<PinnedFolder> Items { get { return _items; } }

        private readonly SettingsStore _settings;
        private readonly string _recentDir;
        private readonly string _appRecentFile;
        private readonly List<string> _appRecent = new List<string>(); // folders navigated via QuickPane
        private List<PinnedFolder> _items = new List<PinnedFolder>();
        private FileSystemWatcher _watcher;
        private Timer _debounce;
        private readonly object _gate = new object();
        private int _rescanQueued; // coalesces queued background rescans

        public RecentFoldersService(SettingsStore settings)
        {
            _settings = settings;
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            _recentDir = Path.Combine(appData, @"Microsoft\Windows\Recent");
            _appRecentFile = Path.Combine(appData, @"QuickPane\recent.txt");
        }

        /// <summary>Record a folder the user just opened through QuickPane. Safe to call from any
        /// thread; the existence check and file write happen on the worker.</summary>
        public void RecordNavigation(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            WorkQueue.Post(() => RecordCore(path));
        }

        private void RecordCore(string path)
        {
            try
            {
                if (!Directory.Exists(path)) return;
                bool changed;
                lock (_gate)
                {
                    if (_appRecent.Count > 0 && string.Equals(_appRecent[0], path, StringComparison.OrdinalIgnoreCase))
                    {
                        changed = false; // already the most recent; nothing to do
                    }
                    else
                    {
                        _appRecent.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
                        _appRecent.Insert(0, path);
                        while (_appRecent.Count > 50) _appRecent.RemoveAt(_appRecent.Count - 1);
                        SaveAppRecent();
                        changed = true;
                    }
                }
                if (changed) RescanCore();
            }
            catch (Exception ex) { Log.Error("record navigation", ex); }
        }

        private void LoadAppRecent()
        {
            try
            {
                if (!File.Exists(_appRecentFile)) return;
                lock (_gate)
                {
                    _appRecent.Clear();
                    foreach (var line in File.ReadAllLines(_appRecentFile))
                        if (!string.IsNullOrWhiteSpace(line)) _appRecent.Add(line.Trim());
                }
            }
            catch (Exception ex) { Log.Error("load app recent", ex); }
        }

        private void SaveAppRecent()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_appRecentFile));
                File.WriteAllLines(_appRecentFile, _appRecent);
            }
            catch (Exception ex) { Log.Error("save app recent", ex); }
        }

        public void Start()
        {
            WorkQueue.Post(() =>
            {
                LoadAppRecent();
                RescanCore();
            });
            try
            {
                if (!Directory.Exists(_recentDir)) return;
                _watcher = new FileSystemWatcher(_recentDir, "*.lnk")
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite
                };
                _watcher.Created += OnFsEvent;
                _watcher.Changed += OnFsEvent;
                _watcher.Deleted += OnFsEvent;
                _watcher.Renamed += OnFsEvent;
                _watcher.EnableRaisingEvents = true;
            }
            catch (Exception ex)
            {
                Log.Error("recents watcher failed", ex);
            }
        }

        /// <summary>Queue a rescan on the worker. Multiple queued requests coalesce into one.</summary>
        public void Rescan()
        {
            if (Interlocked.Exchange(ref _rescanQueued, 1) == 1) return;
            WorkQueue.Post(() =>
            {
                Interlocked.Exchange(ref _rescanQueued, 0);
                RescanCore();
            });
        }

        // Runs on the worker only.
        private void RescanCore()
        {
            var result = new List<PinnedFolder>();
            try
            {
                int cap = _settings.Current.RecentsMaxCount;

                List<string> appSnapshot;
                lock (_gate) appSnapshot = _appRecent.ToList();
                var appItems = new List<PinnedFolder>();
                foreach (var p in appSnapshot)
                {
                    if (!SafeDirExists(p)) continue;
                    appItems.Add(new PinnedFolder { DisplayName = Leaf(p), TargetPath = p, LinkPath = null, Exists = true, IsFile = false });
                    if (appItems.Count >= cap) break;
                }

                var recentItems = new List<PinnedFolder>();
                if (Directory.Exists(_recentDir))
                {
                    // Newest first by link write time (cheap metadata), then resolve only until the
                    // list is full. This keeps a 500-link Recent folder from costing 500 COM loads.
                    var byTime = new List<Tuple<DateTime, string>>();
                    foreach (var lnk in Directory.GetFiles(_recentDir, "*.lnk"))
                    {
                        DateTime when;
                        try { when = File.GetLastWriteTimeUtc(lnk); }
                        catch { when = DateTime.MinValue; }
                        byTime.Add(Tuple.Create(when, lnk));
                    }
                    byTime.Sort((a, b) => b.Item1.CompareTo(a.Item1));

                    int budget = Math.Max(cap * 3, 60); // resolution attempts, not results
                    foreach (var entry in byTime)
                    {
                        if (recentItems.Count >= cap || budget-- <= 0) break;

                        string target;
                        try { target = ShellLink.ResolveTarget(entry.Item2); }
                        catch { continue; }
                        if (string.IsNullOrEmpty(target)) continue;

                        bool isDir = false, isFile = false;
                        try { isDir = Directory.Exists(target); if (!isDir) isFile = File.Exists(target); }
                        catch { continue; }
                        if (!isDir && !isFile) continue;

                        recentItems.Add(new PinnedFolder
                        {
                            DisplayName = Leaf(target),
                            TargetPath = target,
                            LinkPath = entry.Item2,
                            Exists = true,
                            IsFile = isFile
                        });
                    }
                }

                // Folders opened through QuickPane come first, then the Windows Recent items.
                result = appItems.Concat(recentItems)
                    .GroupBy(p => p.TargetPath, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First())
                    .Take(cap)
                    .ToList();
            }
            catch (Exception ex)
            {
                Log.Error("recents rescan failed", ex);
            }

            lock (_gate) { _items = result; }
            WorkQueue.PostUI(() => { RecentsChanged?.Invoke(); });
        }

        public void RemoveFromRecents(PinnedFolder item)
        {
            WorkQueue.Post(() =>
            {
                try
                {
                    if (!string.IsNullOrEmpty(item.LinkPath) && File.Exists(item.LinkPath)) File.Delete(item.LinkPath);
                    lock (_gate)
                    {
                        if (_appRecent.RemoveAll(p => string.Equals(p, item.TargetPath, StringComparison.OrdinalIgnoreCase)) > 0)
                            SaveAppRecent();
                    }
                    RescanCore();
                }
                catch (Exception ex) { Log.Error("remove recent failed", ex); }
            });
        }

        private static bool SafeDirExists(string path)
        {
            try { return Directory.Exists(path); } catch { return false; }
        }

        private static string Leaf(string path)
        {
            try
            {
                var trimmed = path.TrimEnd('\\', '/');
                var leaf = Path.GetFileName(trimmed);
                return string.IsNullOrEmpty(leaf) ? trimmed : leaf;
            }
            catch { return path; }
        }

        private void OnFsEvent(object sender, FileSystemEventArgs e)
        {
            if (_debounce == null)
                _debounce = new Timer(_ => Rescan(), null, Timeout.Infinite, Timeout.Infinite);
            _debounce.Change(300, Timeout.Infinite);
        }

        public void Dispose()
        {
            try { _watcher?.Dispose(); } catch { }
            try { _debounce?.Dispose(); } catch { }
        }
    }
}
