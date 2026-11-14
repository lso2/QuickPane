// GroupStore.cs
// Purpose: loads the groups hierarchy (group folders, tab subfolders, .lnk pins) and rescans it when the disk changes.
// Reason: loading, scanning and watching the groups folder is one responsibility; edits and cross-profile moves are separate files.
// Defines: GroupStore (owner of the partial set).
// Set: GroupStore.cs, GroupStore.Edits.cs, GroupStore.Profiles.cs.
// Doc: docs/notes/QuickPane/Services/GroupStore.cs.md
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using QuickPane.Models;
using NM = QuickPane.Interop.NativeMethods;

namespace QuickPane.Services
{
    /// <summary>
    /// Reads and writes the groups hierarchy. A group is a folder NN_Name. Inside it, each tab is a
    /// subfolder TT_TabName, and a tab's pins are NNN_Name.lnk files in that subfolder. A legacy group
    /// that still holds loose .lnk files is migrated to a single tab on first scan, so a one-tab group
    /// looks exactly like a plain group while groups with several tabs show a tab row.
    /// </summary>
    public sealed partial class GroupStore : IDisposable
    {
        public event Action GroupsChanged;

        public IReadOnlyList<PinnedGroup> Groups { get { return _groups; } }

        private static readonly Regex Prefix = new Regex(@"^(\d+)_(.*)$", RegexOptions.Compiled);

        private readonly SettingsStore _settings;
        private readonly Dictionary<string, int> _activeTabs = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private List<PinnedGroup> _groups = new List<PinnedGroup>();
        private FileSystemWatcher _watcher;
        private Timer _debounce;
        private readonly object _gate = new object();

        public GroupStore(SettingsStore settings) { _settings = settings; }

        public string Root { get { return _settings.ExpandedGroupsPath; } }

        public void Start()
        {
            ScanOnce();
            SeedDefaultsIfEmpty();
            InstallWatcher();
        }

        public void Reload()
        {
            InstallWatcher();
            ScanOnce();
            RaiseChanged();
        }

        // GroupsChanged always fires on the UI thread, because scans can complete on the
        // FileSystemWatcher debounce (worker) and every subscriber is a WPF control.
        private void RaiseChanged()
        {
            WorkQueue.PostUI(() => { GroupsChanged?.Invoke(); });
        }

        private void SeedDefaultsIfEmpty()
        {
            try
            {
                if (_groups.Count > 0) return;
                var group = CreateGroup("Library");
                if (group == null) return;

                // First tab is "Library", created with the group.
                var library = group.Tabs.FirstOrDefault();
                if (library != null)
                {
                    AddPin(library, Folder(Environment.SpecialFolder.DesktopDirectory));
                    AddPin(library, Down(Profile(), "Downloads"));
                    AddPin(library, Folder(Environment.SpecialFolder.MyDocuments));
                    AddPin(library, Folder(Environment.SpecialFolder.MyPictures));
                    AddPin(library, Folder(Environment.SpecialFolder.MyMusic));
                    AddPin(library, Folder(Environment.SpecialFolder.MyVideos));
                }

                var system = AddTab(group, "System");
                if (system != null)
                {
                    AddPin(system, Profile());
                    AddPin(system, "C:\\");
                    AddPin(system, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
                    AddPin(system, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
                    AddPin(system, Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
                }
            }
            catch (Exception ex) { Log.Error("seed defaults failed", ex); }
        }

        private static string Profile() { return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile); }
        private static string Folder(Environment.SpecialFolder f) { return Environment.GetFolderPath(f); }
        private static string Down(string b, string s) { try { return string.IsNullOrEmpty(b) ? null : Path.Combine(b, s); } catch { return null; } }

        // ---- scanning --------------------------------------------------------

        private readonly object _scanLock = new object();

        public void ScanOnce()
        {
            // Serialize scans: they can run from the UI (mutations) and the watcher debounce
            // (worker) at the same time, and Materialize/recovery move things on disk.
            lock (_scanLock) { ScanCore(); }
        }

        private void ScanCore()
        {
            var result = new List<PinnedGroup>();
            try
            {
                var root = Root;
                Directory.CreateDirectory(root);
                RecoverTemps(root);

                foreach (var dir in Directory.GetDirectories(root))
                {
                    int order; string name;
                    Split(Path.GetFileName(dir), out order, out name);
                    var group = new PinnedGroup { Name = name, FolderPath = dir, Order = order };

                    var subdirs = Directory.GetDirectories(dir);
                    var loose = Directory.GetFiles(dir, "*.lnk");

                    if (subdirs.Length == 0 && loose.Length > 0)
                    {
                        Materialize(dir, name, loose);          // legacy -> single tab
                        subdirs = Directory.GetDirectories(dir);
                        loose = new string[0];
                    }

                    if (subdirs.Length == 0)
                    {
                        // Empty group: present one implicit tab pointing at the group folder.
                        group.Tabs.Add(new PinnedTab { Name = name, FolderPath = dir, Order = 0 });
                    }
                    else
                    {
                        foreach (var sub in subdirs)
                        {
                            int torder; string tname;
                            Split(Path.GetFileName(sub), out torder, out tname);
                            var tab = new PinnedTab { Name = tname, FolderPath = sub, Order = torder };
                            LoadPins(sub, tab);
                            group.Tabs.Add(tab);
                        }
                        group.Tabs = group.Tabs.OrderBy(t => t.Order)
                            .ThenBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

                        if (loose.Length > 0)
                        {
                            // Loose files alongside tabs: fold them into a new leading tab.
                            Materialize(dir, name, loose);
                            // simplest: rescan this group next pass; for now skip the loose set
                        }
                    }

                    int active;
                    _activeTabs.TryGetValue(dir, out active);
                    group.ActiveTab = Math.Max(0, Math.Min(active, group.Tabs.Count - 1));
                    result.Add(group);
                }

                result = result.OrderBy(g => g.Order)
                    .ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            }
            catch (Exception ex) { Log.Error("ScanOnce failed", ex); }

            lock (_gate) { _groups = result; }
        }

        private static void LoadPins(string tabDir, PinnedTab tab)
        {
            foreach (var lnk in Directory.GetFiles(tabDir, "*.lnk"))
            {
                int o; string disp;
                Split(Path.GetFileNameWithoutExtension(lnk), out o, out disp);
                bool? storedIsFile;
                var target = ShellLink.ResolveTarget(lnk, out storedIsFile) ?? string.Empty;
                if (target.Length > 0 && storedIsFile.HasValue) PathStatus.SeedKind(target, storedIsFile.Value);
                // Existence and file-vs-folder come from the background probe cache, never from a
                // direct disk hit here: scans run on the UI thread after mutations, and probing a
                // dead network target inline froze the input queue shared with Explorer.
                bool exists = target.Length > 0 && PathStatus.IsAlive(target);
                bool isFile = target.Length > 0 && PathStatus.IsFile(target);
                tab.Items.Add(new PinnedFolder { DisplayName = disp, TargetPath = target, LinkPath = lnk, Order = o, Exists = exists, IsFile = isFile });
            }
            tab.Items = tab.Items.OrderBy(i => i.Order)
                .ThenBy(i => i.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        // Bring back anything stranded under a temp name by a reorder that died halfway (crash,
        // locked folder, antivirus). Recovered entries sort to the end but keep their contents.
        private static void RecoverTemps(string root)
        {
            try
            {
                foreach (var dir in Directory.GetDirectories(root, "~ord~*"))
                    TryRename(dir, Path.Combine(root, "99_Recovered"), isDir: true);
                foreach (var group in Directory.GetDirectories(root))
                {
                    foreach (var sub in Directory.GetDirectories(group, "~ord~*"))
                        TryRename(sub, Path.Combine(group, "99_Recovered"), isDir: true);
                    foreach (var tabDir in Directory.GetDirectories(group))
                    {
                        foreach (var f in Directory.GetFiles(tabDir, "~ord~*.lnk"))
                            TryRename(f, Path.Combine(tabDir, "999_Recovered.lnk"), isDir: false);
                        foreach (var f in Directory.GetFiles(tabDir, "~new~*.lnk"))
                            TryRename(f, Path.Combine(tabDir, "999_Recovered.lnk"), isDir: false);
                    }
                }
            }
            catch (Exception ex) { Log.Error("recover temps", ex); }
        }

        private static void TryRename(string src, string destBase, bool isDir)
        {
            try
            {
                string dest = destBase;
                int n = 1;
                string dir = Path.GetDirectoryName(destBase);
                string name = Path.GetFileNameWithoutExtension(destBase);
                string ext = Path.GetExtension(destBase);
                while (isDir ? Directory.Exists(dest) : File.Exists(dest))
                    dest = Path.Combine(dir, name + " (" + (++n) + ")" + ext);
                if (isDir) Directory.Move(src, dest); else File.Move(src, dest);
                Log.Info("recovered stranded temp '" + src + "' -> '" + dest + "'");
            }
            catch (Exception ex) { Log.Error("recover temp '" + src + "'", ex); }
        }

        private static void Materialize(string groupDir, string name, string[] looseLnks)
        {
            try
            {
                var tabDir = Path.Combine(groupDir, "01_" + (string.IsNullOrWhiteSpace(name) ? "Main" : name));
                Directory.CreateDirectory(tabDir);
                foreach (var lnk in looseLnks)
                {
                    var dest = Path.Combine(tabDir, Path.GetFileName(lnk));
                    if (!File.Exists(dest)) File.Move(lnk, dest);
                }
            }
            catch (Exception ex) { Log.Error("materialize failed", ex); }
        }

        // Two-phase rename used by every reorder: move to temp names, then to final names. If any
        // step throws (locked folder, antivirus hold), roll the temps back so nothing is left
        // stranded under a ~ord~ name. ScanCore's RecoverTemps is the second net for a hard crash.
        private static void TwoPhaseRename(List<Tuple<string, string, string>> plan, bool isDir)
        {
            var done = new List<Tuple<string, string, string>>(); // original -> temp completed
            try
            {
                foreach (var step in plan)
                {
                    if (isDir ? !Directory.Exists(step.Item1) : !File.Exists(step.Item1)) continue;
                    if (isDir) Directory.Move(step.Item1, step.Item2); else File.Move(step.Item1, step.Item2);
                    done.Add(step);
                }
                foreach (var step in done)
                {
                    if (isDir) Directory.Move(step.Item2, step.Item3); else File.Move(step.Item2, step.Item3);
                }
            }
            catch
            {
                // Put whatever is still sitting at a temp name back where it came from.
                foreach (var step in done)
                {
                    try
                    {
                        if (isDir ? Directory.Exists(step.Item2) : File.Exists(step.Item2))
                        {
                            if (isDir) Directory.Move(step.Item2, step.Item1); else File.Move(step.Item2, step.Item1);
                        }
                    }
                    catch { /* recovery of stragglers happens in RecoverTemps */ }
                }
                throw;
            }
        }

        // ---- helpers ---------------------------------------------------------
        private bool IsGroupFolder(string path)
        {
            try { return string.Equals(Path.GetDirectoryName(path), Root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        private int NextGroupOrder()
        {
            int max = 0; foreach (var g in _groups) if (g.Order > max && g.Order != int.MaxValue) max = g.Order; return max + 1;
        }

        private static int NextTabOrder(string groupDir)
        {
            int max = 0;
            foreach (var sub in Directory.GetDirectories(groupDir))
            {
                int o; string n; Split(Path.GetFileName(sub), out o, out n);
                if (o != int.MaxValue && o > max) max = o;
            }
            return max + 1;
        }

        private static int NextPinOrder(string tabDir)
        {
            int max = 0;
            foreach (var lnk in Directory.GetFiles(tabDir, "*.lnk"))
            {
                int o; string n; Split(Path.GetFileNameWithoutExtension(lnk), out o, out n);
                if (o != int.MaxValue && o > max) max = o;
            }
            return max + 1;
        }

        private static string UniqueDir(string parent, int order, string name)
        {
            name = Sanitize(name);
            var baseName = order.ToString("00") + "_" + name;
            var path = Path.Combine(parent, baseName);
            int n = 1;
            while (Directory.Exists(path)) path = Path.Combine(parent, baseName + " (" + (++n) + ")");
            return path;
        }

        private static string UniqueLink(string folder, int order, string leaf)
        {
            leaf = Sanitize(leaf);
            var baseName = order.ToString("000") + "_" + leaf;
            var path = Path.Combine(folder, baseName + ".lnk");
            int n = 1;
            while (File.Exists(path)) path = Path.Combine(folder, baseName + " (" + (++n) + ").lnk");
            return path;
        }

        private static string SafeLeaf(string targetPath)
        {
            try
            {
                var trimmed = targetPath.TrimEnd('\\', '/');
                var leaf = Path.GetFileName(trimmed);
                if (string.IsNullOrEmpty(leaf)) leaf = trimmed;
                return string.IsNullOrEmpty(leaf) ? "Folder" : leaf;
            }
            catch { return "Folder"; }
        }

        private static void Split(string raw, out int order, out string name)
        {
            var m = Prefix.Match(raw);
            if (m.Success && int.TryParse(m.Groups[1].Value, out order)) { name = m.Groups[2].Value; return; }
            order = int.MaxValue; name = raw;
        }

        private static string Sanitize(string name)
        {
            if (name == null) return null;
            foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c.ToString(), string.Empty);
            return name.Trim();
        }

        private void Try(Action action)
        {
            try { action(); ScanOnce(); RaiseChanged(); }
            catch (Exception ex) { Log.Error("group mutation failed", ex); }
        }

        // ---- watcher ---------------------------------------------------------
        private void InstallWatcher()
        {
            try
            {
                _watcher?.Dispose();
                Directory.CreateDirectory(Root);
                _watcher = new FileSystemWatcher(Root)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite
                };
                _watcher.Created += OnFsEvent; _watcher.Deleted += OnFsEvent;
                _watcher.Renamed += OnFsEvent; _watcher.Changed += OnFsEvent;
                _watcher.EnableRaisingEvents = true;
            }
            catch (Exception ex) { Log.Error("group watcher install failed", ex); }
        }

        private void OnFsEvent(object sender, FileSystemEventArgs e)
        {
            // Scan on the worker, not on the watcher's threadpool callback, so the timer thread is
            // never tied up by a slow disk and the scan is serialized with everything else.
            if (_debounce == null)
                _debounce = new Timer(_ => WorkQueue.Post(() => { ScanOnce(); RaiseChanged(); }), null, Timeout.Infinite, Timeout.Infinite);
            _debounce.Change(250, Timeout.Infinite);
        }

        public void Dispose()
        {
            try { _watcher?.Dispose(); } catch { }
            try { _debounce?.Dispose(); } catch { }
        }
    }
}
