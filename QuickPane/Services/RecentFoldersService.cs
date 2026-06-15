using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using QuickPane.Models;

namespace QuickPane.Services
{
    /// <summary>
    /// Surfaces recently used folders from %APPDATA%\Microsoft\Windows\Recent. Only .lnk files whose
    /// target is a directory are kept, newest first, capped at settings.RecentsMaxCount. A
    /// FileSystemWatcher keeps the list live with no polling.
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

        public RecentFoldersService(SettingsStore settings)
        {
            _settings = settings;
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            _recentDir = Path.Combine(appData, @"Microsoft\Windows\Recent");
            _appRecentFile = Path.Combine(appData, @"QuickPane\recent.txt");
        }

        /// <summary>Record a folder the user just opened through QuickPane, so Recent is reliable even
        /// when the Windows Recent folder is sparse. Newest first, deduped, persisted.</summary>
        public void RecordNavigation(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return;
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
                if (changed) { Rescan(); RecentsChanged?.Invoke(); }
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
            LoadAppRecent();
            Rescan();
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

        public void Rescan()
        {
            var result = new List<PinnedFolder>();
            try
            {
                if (Directory.Exists(_recentDir))
                {
                    int cap = _settings.Current.RecentsMaxCount;
                    var candidates = new List<Tuple<DateTime, PinnedFolder>>();

                    foreach (var lnk in Directory.GetFiles(_recentDir, "*.lnk"))
                    {
                        string target;
                        try { target = ShellLink.ResolveTarget(lnk); }
                        catch { continue; }
                        if (string.IsNullOrEmpty(target)) continue;

                        // Show the actual recent items, files and folders alike, just like the native
                        // Recent list. A file keeps its own path and is flagged so the UI can open it or
                        // jump to its folder; a folder is shown as itself.
                        bool isDir = false, isFile = false;
                        try { isDir = Directory.Exists(target); if (!isDir) isFile = File.Exists(target); }
                        catch { continue; }
                        if (!isDir && !isFile) continue;

                        DateTime when;
                        try { when = File.GetLastWriteTimeUtc(lnk); }
                        catch { when = DateTime.MinValue; }

                        candidates.Add(Tuple.Create(when, new PinnedFolder
                        {
                            DisplayName = Leaf(target),
                            TargetPath = target,
                            LinkPath = lnk,
                            Exists = true,
                            IsFile = isFile
                        }));
                    }

                    var recentItems = candidates.OrderByDescending(t => t.Item1).Select(t => t.Item2);

                    List<string> appSnapshot;
                    lock (_gate) appSnapshot = _appRecent.ToList();
                    var appItems = appSnapshot
                        .Where(SafeDirExists)
                        .Select(p => new PinnedFolder { DisplayName = Leaf(p), TargetPath = p, LinkPath = null, Exists = true, IsFile = false });

                    // Folders opened through QuickPane come first, then the Windows Recent items.
                    result = appItems.Concat(recentItems)
                        .GroupBy(p => p.TargetPath, StringComparer.OrdinalIgnoreCase)
                        .Select(g => g.First())
                        .Take(cap)
                        .ToList();
                }
                else
                {
                    List<string> appSnapshot;
                    lock (_gate) appSnapshot = _appRecent.ToList();
                    result = appSnapshot.Where(SafeDirExists)
                        .Select(p => new PinnedFolder { DisplayName = Leaf(p), TargetPath = p, LinkPath = null, Exists = true })
                        .Take(_settings.Current.RecentsMaxCount)
                        .ToList();
                }
            }
            catch (Exception ex)
            {
                Log.Error("recents rescan failed", ex);
            }

            lock (_gate) { _items = result; }
        }

        public void RemoveFromRecents(PinnedFolder item)
        {
            try
            {
                if (!string.IsNullOrEmpty(item.LinkPath) && File.Exists(item.LinkPath)) File.Delete(item.LinkPath);
                lock (_gate)
                {
                    if (_appRecent.RemoveAll(p => string.Equals(p, item.TargetPath, StringComparison.OrdinalIgnoreCase)) > 0)
                        SaveAppRecent();
                }
                Rescan();
                RecentsChanged?.Invoke();
            }
            catch (Exception ex) { Log.Error("remove recent failed", ex); }
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
            // Resolve shortcuts on the UI (STA) thread, where the shell COM behaves predictably.
            if (_debounce == null)
                _debounce = new Timer(_ =>
                {
                    var disp = System.Windows.Application.Current?.Dispatcher;
                    if (disp != null) disp.BeginInvoke(new Action(() => { Rescan(); RecentsChanged?.Invoke(); }));
                    else { Rescan(); RecentsChanged?.Invoke(); }
                }, null, Timeout.Infinite, Timeout.Infinite);
            _debounce.Change(300, Timeout.Infinite);
        }

        public void Dispose()
        {
            try { _watcher?.Dispose(); } catch { }
            try { _debounce?.Dispose(); } catch { }
        }
    }
}
