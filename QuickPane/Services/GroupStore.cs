using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using QuickPane.Models;

namespace QuickPane.Services
{
    /// <summary>
    /// Reads and writes the groups hierarchy. A group is a folder NN_Name. Inside it, each tab is a
    /// subfolder TT_TabName, and a tab's pins are NNN_Name.lnk files in that subfolder. A legacy group
    /// that still holds loose .lnk files is migrated to a single tab on first scan, so a one-tab group
    /// looks exactly like a plain group while groups with several tabs show a tab row.
    /// </summary>
    public sealed class GroupStore : IDisposable
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
                var target = ShellLink.ResolveTarget(lnk) ?? string.Empty;
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

        // ---- group display name ----------------------------------------------
        public static string DisplayName(PinnedGroup g)
        {
            if (g == null) return "";
            if (g.Tabs.Count == 1) return g.Tabs[0].Name;
            return string.Join(" / ", g.Tabs.Select(t => t.Name));
        }

        // ---- active tab ------------------------------------------------------
        public void SetActiveTab(PinnedGroup group, int index)
        {
            if (group == null) return;
            if (index < 0) index = 0;
            if (index >= group.Tabs.Count) index = group.Tabs.Count - 1;
            group.ActiveTab = index;
            _activeTabs[group.FolderPath] = index;
        }

        // ---- groups ----------------------------------------------------------
        public PinnedGroup CreateGroup(string name)
        {
            name = Sanitize(name);
            if (string.IsNullOrWhiteSpace(name)) name = "Group";
            int order = NextGroupOrder();
            var folder = Path.Combine(Root, order.ToString("00") + "_" + name);
            Directory.CreateDirectory(folder);
            Directory.CreateDirectory(Path.Combine(folder, "01_" + name)); // first tab
            ScanOnce();
            RaiseChanged();
            return _groups.FirstOrDefault(g => string.Equals(g.FolderPath, folder, StringComparison.OrdinalIgnoreCase));
        }

        public PinnedGroup CreateGroupAfter(PinnedGroup reference, string name)
        {
            var created = CreateGroup(name);
            if (created == null || reference == null) return created;
            var ordered = _groups.Where(g => !ReferenceEquals(g, created)).ToList();
            int idx = ordered.FindIndex(g => string.Equals(g.FolderPath, reference.FolderPath, StringComparison.OrdinalIgnoreCase));
            if (idx < 0) return created;
            ordered.Insert(idx + 1, created);
            ReorderGroups(ordered);
            return _groups.FirstOrDefault(g => string.Equals(DisplayName(g), Sanitize(name), StringComparison.OrdinalIgnoreCase)) ?? created;
        }

        public void DeleteGroup(PinnedGroup group)
        {
            Try(() => { if (Directory.Exists(group.FolderPath)) Directory.Delete(group.FolderPath, true); });
        }

        public void RenameGroup(PinnedGroup group, string newName)
        {
            // A single-tab group renames its tab; the folder label is kept in sync.
            if (group.Tabs.Count == 1)
            {
                RenameTab(group.Tabs[0], newName);
                RenameGroupFolder(group, newName);
            }
            else RenameGroupFolder(group, newName);
        }

        private void RenameGroupFolder(PinnedGroup group, string newName)
        {
            Try(() =>
            {
                newName = Sanitize(newName);
                if (string.IsNullOrWhiteSpace(newName)) return;
                var parent = Path.GetDirectoryName(group.FolderPath);
                var dest = Path.Combine(parent, group.Order.ToString("00") + "_" + newName);
                if (!string.Equals(dest, group.FolderPath, StringComparison.OrdinalIgnoreCase))
                    Directory.Move(group.FolderPath, dest);
            });
        }

        public void MoveGroup(PinnedGroup group, int delta)
        {
            var ordered = _groups.ToList();
            int idx = ordered.FindIndex(g => ReferenceEquals(g, group));
            int dest = idx + delta;
            if (idx < 0 || dest < 0 || dest >= ordered.Count) return;
            ordered.RemoveAt(idx); ordered.Insert(dest, group);
            ReorderGroups(ordered);
        }

        public void ReorderGroups(List<PinnedGroup> ordered)
        {
            try
            {
                var parent = Root;
                var plan = new List<Tuple<string, string, string>>();
                for (int i = 0; i < ordered.Count; i++)
                {
                    var g = ordered[i];
                    plan.Add(Tuple.Create(g.FolderPath,
                        Path.Combine(parent, "~ord~" + Guid.NewGuid().ToString("N")),
                        Path.Combine(parent, (i + 1).ToString("00") + "_" + g.Name)));
                }
                TwoPhaseRename(plan, isDir: true);
            }
            catch (Exception ex) { Log.Error("ReorderGroups failed", ex); }
            ScanOnce(); RaiseChanged();
        }

        // Move every tab of source into dest, then delete source (drag a group onto a group).
        public void MergeGroups(PinnedGroup source, PinnedGroup dest)
        {
            Try(() =>
            {
                if (source == null || dest == null || ReferenceEquals(source, dest)) return;
                int order = NextTabOrder(dest.FolderPath);
                foreach (var tab in source.Tabs.ToList())
                {
                    var destPath = UniqueDir(dest.FolderPath, order++, tab.Name);
                    if (Directory.Exists(tab.FolderPath)) Directory.Move(tab.FolderPath, destPath);
                }
                if (Directory.Exists(source.FolderPath)) Directory.Delete(source.FolderPath, true);
            });
        }

        // ---- tabs ------------------------------------------------------------
        public PinnedTab AddTab(PinnedGroup group, string name)
        {
            name = Sanitize(name);
            if (string.IsNullOrWhiteSpace(name)) name = "Tab";
            int order = NextTabOrder(group.FolderPath);
            var dir = UniqueDir(group.FolderPath, order, name);
            Directory.CreateDirectory(dir);
            ScanOnce(); RaiseChanged();
            return _groups.SelectMany(g => g.Tabs).FirstOrDefault(t => string.Equals(t.FolderPath, dir, StringComparison.OrdinalIgnoreCase));
        }

        public void RenameTab(PinnedTab tab, string newName)
        {
            Try(() =>
            {
                newName = Sanitize(newName);
                if (string.IsNullOrWhiteSpace(newName) || tab == null) return;
                if (IsGroupFolder(tab.FolderPath)) return; // implicit empty tab, nothing on disk yet
                var parent = Path.GetDirectoryName(tab.FolderPath);
                var dest = Path.Combine(parent, tab.Order.ToString("00") + "_" + newName);
                if (!string.Equals(dest, tab.FolderPath, StringComparison.OrdinalIgnoreCase))
                    Directory.Move(tab.FolderPath, dest);
            });
        }

        public void DeleteTab(PinnedGroup group, PinnedTab tab)
        {
            Try(() =>
            {
                if (group.Tabs.Count <= 1) { DeleteGroup(group); return; } // last tab: drop the group
                if (Directory.Exists(tab.FolderPath)) Directory.Delete(tab.FolderPath, true);
            });
        }

        public void MoveTab(PinnedGroup group, PinnedTab tab, int delta)
        {
            var ordered = group.Tabs.ToList();
            int idx = ordered.FindIndex(t => ReferenceEquals(t, tab));
            int dest = idx + delta;
            if (idx < 0 || dest < 0 || dest >= ordered.Count) return;
            ordered.RemoveAt(idx); ordered.Insert(dest, tab);
            ReorderTabs(group, ordered);
        }

        public void ReorderTabs(PinnedGroup group, List<PinnedTab> ordered)
        {
            try
            {
                var parent = group.FolderPath;
                var plan = new List<Tuple<string, string, string>>();
                for (int i = 0; i < ordered.Count; i++)
                {
                    var t = ordered[i];
                    plan.Add(Tuple.Create(t.FolderPath,
                        Path.Combine(parent, "~ord~" + Guid.NewGuid().ToString("N")),
                        Path.Combine(parent, (i + 1).ToString("00") + "_" + t.Name)));
                }
                TwoPhaseRename(plan, isDir: true);
            }
            catch (Exception ex) { Log.Error("ReorderTabs failed", ex); }
            ScanOnce(); RaiseChanged();
        }

        // Move a tab into another group as a tab (optionally at an index).
        public void MoveTabToGroup(PinnedTab tab, PinnedGroup dest, int index = -1)
        {
            Try(() =>
            {
                if (tab == null || dest == null) return;
                int order = NextTabOrder(dest.FolderPath);
                var destPath = UniqueDir(dest.FolderPath, order, tab.Name);
                if (Directory.Exists(tab.FolderPath)) Directory.Move(tab.FolderPath, destPath);
            });
        }

        // Pull a tab out to become its own group at the end of the list (drag a tab to empty space).
        public PinnedGroup MoveTabToNewGroup(PinnedGroup source, PinnedTab tab)
        {
            PinnedGroup created = null;
            Try(() =>
            {
                if (tab == null) return;
                int order = NextGroupOrder();
                var groupDir = Path.Combine(Root, order.ToString("00") + "_" + tab.Name);
                Directory.CreateDirectory(groupDir);
                var tabDir = Path.Combine(groupDir, "01_" + tab.Name);
                if (Directory.Exists(tab.FolderPath)) Directory.Move(tab.FolderPath, tabDir);
            });
            return created;
        }

        // ---- pins ------------------------------------------------------------
        // Pins may target folders or files; both are stored as ordinary .lnk shortcuts.
        public void AddPin(PinnedGroup group, string targetPath)
        {
            if (group == null) return;
            AddPin(group.Active, targetPath);
        }

        public void AddPin(PinnedTab tab, string targetPath)
        {
            AddPins(tab, new[] { targetPath });
        }

        /// <summary>Pin several targets at once with a single rescan at the end.</summary>
        public void AddPins(PinnedTab tab, System.Collections.Generic.IEnumerable<string> targetPaths)
        {
            try
            {
                if (tab == null || targetPaths == null) return;
                var dir = tab.FolderPath;
                if (IsGroupFolder(dir)) // implicit empty tab, materialize a real subfolder
                {
                    dir = Path.Combine(dir, "01_" + (string.IsNullOrWhiteSpace(tab.Name) ? "Main" : tab.Name));
                    Directory.CreateDirectory(dir);
                }
                int order = NextPinOrder(dir);
                bool any = false;
                foreach (var targetPath in targetPaths)
                {
                    if (string.IsNullOrEmpty(targetPath)) continue;
                    string leaf = SafeLeaf(targetPath);
                    string lnk = UniqueLink(dir, order++, leaf);
                    ShellLink.Create(lnk, targetPath, leaf);
                    any = true;
                }
                if (any) { ScanOnce(); RaiseChanged(); }
            }
            catch (Exception ex) { Log.Error("AddPin failed", ex); }
        }

        // Pin a target into a tab at a specific position, so a folder dropped on a line lands there.
        public void AddPinAtIndex(PinnedTab tab, string targetPath, int index)
        {
            try
            {
                if (tab == null || string.IsNullOrEmpty(targetPath)) return;
                var dir = tab.FolderPath;
                if (IsGroupFolder(dir))
                {
                    dir = Path.Combine(dir, "01_" + (string.IsNullOrWhiteSpace(tab.Name) ? "Main" : tab.Name));
                    Directory.CreateDirectory(dir);
                }

                var existing = Directory.GetFiles(dir, "*.lnk")
                    .Select(p => { int o; string n; Split(Path.GetFileNameWithoutExtension(p), out o, out n); return new PinSort { Path = p, Order = o, Name = n }; })
                    .OrderBy(x => x.Order).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();

                string leaf = SafeLeaf(targetPath);
                string tmpNew = Path.Combine(dir, "~new~" + Guid.NewGuid().ToString("N") + ".lnk");
                ShellLink.Create(tmpNew, targetPath, leaf);

                var order = existing.Select(x => new PinSort { Path = x.Path, Name = x.Name }).ToList();
                if (index < 0) index = 0;
                if (index > order.Count) index = order.Count;
                order.Insert(index, new PinSort { Path = tmpNew, Name = leaf });

                var plan = new List<Tuple<string, string, string>>();
                for (int i = 0; i < order.Count; i++)
                {
                    var o = order[i];
                    plan.Add(Tuple.Create(o.Path,
                        Path.Combine(dir, "~ord~" + Guid.NewGuid().ToString("N") + ".lnk"),
                        Path.Combine(dir, (i + 1).ToString("000") + "_" + o.Name + ".lnk")));
                }
                TwoPhaseRename(plan, isDir: false);

                ScanOnce(); RaiseChanged();
            }
            catch (Exception ex) { Log.Error("AddPinAtIndex failed", ex); }
        }

        private sealed class PinSort { public string Path; public int Order; public string Name; }

        public void RemovePin(PinnedFolder folder)
        {
            Try(() => { if (File.Exists(folder.LinkPath)) File.Delete(folder.LinkPath); });
        }

        public void RenamePin(PinnedFolder folder, string newName)
        {
            Try(() =>
            {
                newName = Sanitize(newName);
                if (string.IsNullOrWhiteSpace(newName)) return;
                var dir = Path.GetDirectoryName(folder.LinkPath);
                var dest = Path.Combine(dir, folder.Order.ToString("000") + "_" + newName + ".lnk");
                if (!string.Equals(dest, folder.LinkPath, StringComparison.OrdinalIgnoreCase))
                    File.Move(folder.LinkPath, dest);
            });
        }

        public void MovePinToTab(PinnedFolder folder, PinnedTab dest)
        {
            Try(() =>
            {
                if (dest == null) return;
                var dir = dest.FolderPath;
                if (IsGroupFolder(dir)) { dir = Path.Combine(dir, "01_" + (string.IsNullOrWhiteSpace(dest.Name) ? "Main" : dest.Name)); Directory.CreateDirectory(dir); }
                int order = NextPinOrder(dir);
                var target = UniqueLink(dir, order, folder.DisplayName);
                File.Move(folder.LinkPath, target);
            });
        }

        public void MovePinToGroup(PinnedFolder folder, PinnedGroup dest)
        {
            if (dest != null) MovePinToTab(folder, dest.Active);
        }

        public void MovePin(PinnedTab tab, PinnedFolder folder, int delta)
        {
            var ordered = tab.Items.ToList();
            int idx = ordered.FindIndex(p => ReferenceEquals(p, folder));
            int dest = idx + delta;
            if (idx < 0 || dest < 0 || dest >= ordered.Count) return;
            ordered.RemoveAt(idx); ordered.Insert(dest, folder);
            ReorderPins(tab, ordered);
        }

        public void ReorderPins(PinnedTab tab, List<PinnedFolder> ordered)
        {
            try
            {
                var dir = tab.FolderPath;
                var plan = new List<Tuple<string, string, string>>();
                for (int i = 0; i < ordered.Count; i++)
                {
                    var p = ordered[i];
                    plan.Add(Tuple.Create(p.LinkPath,
                        Path.Combine(dir, "~ord~" + Guid.NewGuid().ToString("N") + ".lnk"),
                        Path.Combine(dir, (i + 1).ToString("000") + "_" + p.DisplayName + ".lnk")));
                }
                TwoPhaseRename(plan, isDir: false);
            }
            catch (Exception ex) { Log.Error("ReorderPins failed", ex); }
            ScanOnce(); RaiseChanged();
        }

        // ---- cross-profile group operations ----------------------------------
        // The settings page shows one column per profile. These read and move/copy group folders between
        // each profile's own groups root, identified by its expanded folder path.

        /// <summary>List (folderPath, displayName) of the groups under an expanded root, in order.</summary>
        public static List<Tuple<string, string>> ListGroups(string expandedRoot)
        {
            var list = new List<Tuple<string, string>>();
            try
            {
                if (!Directory.Exists(expandedRoot)) return list;
                var items = new List<Tuple<int, string, string>>();
                foreach (var d in Directory.GetDirectories(expandedRoot))
                {
                    int o; string n; Split(Path.GetFileName(d), out o, out n);
                    items.Add(Tuple.Create(o, d, n));
                }
                foreach (var it in items.OrderBy(x => x.Item1).ThenBy(x => x.Item3, StringComparer.CurrentCultureIgnoreCase))
                    list.Add(Tuple.Create(it.Item2, it.Item3));
            }
            catch (Exception ex) { Log.Error("ListGroups failed", ex); }
            return list;
        }

        /// <summary>Create a new group folder (with a first tab) under any expanded root.</summary>
        public static void CreateGroupFolder(string expandedRoot, string name)
        {
            try
            {
                name = Sanitize(name);
                if (string.IsNullOrWhiteSpace(name)) name = "Group";
                Directory.CreateDirectory(expandedRoot);
                var folder = UniqueDir(expandedRoot, NextOrderInRoot(expandedRoot), name);
                Directory.CreateDirectory(folder);
                Directory.CreateDirectory(Path.Combine(folder, "01_" + name)); // first tab
            }
            catch (Exception ex) { Log.Error("CreateGroupFolder failed", ex); }
        }

        /// <summary>Delete a group folder and its contents.</summary>
        public static void DeleteGroupFolder(string folder)
        {
            try { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
            catch (Exception ex) { Log.Error("DeleteGroupFolder failed", ex); }
        }

        /// <summary>Move a group folder into another profile's groups root (drag between columns).</summary>
        public static void MoveGroupFolder(string groupFolder, string destExpandedRoot)
        {
            try
            {
                if (string.IsNullOrEmpty(groupFolder) || !Directory.Exists(groupFolder)) return;
                Directory.CreateDirectory(destExpandedRoot);
                if (string.Equals(Path.GetDirectoryName(groupFolder), destExpandedRoot.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return;
                int o; string name; Split(Path.GetFileName(groupFolder), out o, out name);
                var dest = UniqueDir(destExpandedRoot, NextOrderInRoot(destExpandedRoot), name);
                Directory.Move(groupFolder, dest);
            }
            catch (Exception ex) { Log.Error("MoveGroupFolder failed", ex); }
        }

        /// <summary>Copy a group folder into another profile's groups root (Ctrl-drag between columns).</summary>
        public static void CopyGroupFolder(string groupFolder, string destExpandedRoot)
        {
            try
            {
                if (string.IsNullOrEmpty(groupFolder) || !Directory.Exists(groupFolder)) return;
                Directory.CreateDirectory(destExpandedRoot);
                int o; string name; Split(Path.GetFileName(groupFolder), out o, out name);
                var dest = UniqueDir(destExpandedRoot, NextOrderInRoot(destExpandedRoot), name);
                CopyDir(groupFolder, dest);
            }
            catch (Exception ex) { Log.Error("CopyGroupFolder failed", ex); }
        }

        private static int NextOrderInRoot(string root)
        {
            int max = 0;
            try
            {
                foreach (var d in Directory.GetDirectories(root))
                {
                    int o; string n; Split(Path.GetFileName(d), out o, out n);
                    if (o != int.MaxValue && o > max) max = o;
                }
            }
            catch { }
            return max + 1;
        }

        private static void CopyDir(string src, string dst)
        {
            Directory.CreateDirectory(dst);
            foreach (var f in Directory.GetFiles(src)) File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true);
            foreach (var d in Directory.GetDirectories(src)) CopyDir(d, Path.Combine(dst, Path.GetFileName(d)));
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
