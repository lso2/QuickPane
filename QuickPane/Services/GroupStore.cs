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
            GroupsChanged?.Invoke();
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

        public void ScanOnce()
        {
            var result = new List<PinnedGroup>();
            try
            {
                var root = Root;
                Directory.CreateDirectory(root);

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
                bool exists = target.Length > 0 && SafeDirExists(target);
                tab.Items.Add(new PinnedFolder { DisplayName = disp, TargetPath = target, LinkPath = lnk, Order = o, Exists = exists });
            }
            tab.Items = tab.Items.OrderBy(i => i.Order)
                .ThenBy(i => i.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
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

        private static bool SafeDirExists(string path) { try { return Directory.Exists(path); } catch { return false; } }

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
            GroupsChanged?.Invoke();
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
                var temp = new List<Tuple<string, string>>();
                for (int i = 0; i < ordered.Count; i++)
                {
                    var g = ordered[i];
                    if (!Directory.Exists(g.FolderPath)) continue;
                    var tmp = Path.Combine(parent, "~ord~" + Guid.NewGuid().ToString("N"));
                    Directory.Move(g.FolderPath, tmp);
                    temp.Add(Tuple.Create(tmp, (i + 1).ToString("00") + "_" + g.Name));
                }
                foreach (var t in temp) Directory.Move(t.Item1, Path.Combine(parent, t.Item2));
                ScanOnce(); GroupsChanged?.Invoke();
            }
            catch (Exception ex) { Log.Error("ReorderGroups failed", ex); }
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
            ScanOnce(); GroupsChanged?.Invoke();
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
                var temp = new List<Tuple<string, string>>();
                for (int i = 0; i < ordered.Count; i++)
                {
                    var t = ordered[i];
                    if (!Directory.Exists(t.FolderPath)) continue;
                    var tmp = Path.Combine(parent, "~ord~" + Guid.NewGuid().ToString("N"));
                    Directory.Move(t.FolderPath, tmp);
                    temp.Add(Tuple.Create(tmp, (i + 1).ToString("00") + "_" + t.Name));
                }
                foreach (var x in temp) Directory.Move(x.Item1, Path.Combine(parent, x.Item2));
                ScanOnce(); GroupsChanged?.Invoke();
            }
            catch (Exception ex) { Log.Error("ReorderTabs failed", ex); }
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
        public void AddPin(PinnedGroup group, string targetPath)
        {
            if (group == null) return;
            AddPin(group.Active, targetPath);
        }

        public void AddPin(PinnedTab tab, string targetPath)
        {
            try
            {
                if (tab == null || string.IsNullOrEmpty(targetPath)) return;
                var dir = tab.FolderPath;
                if (IsGroupFolder(dir)) // implicit empty tab, materialize a real subfolder
                {
                    dir = Path.Combine(dir, "01_" + (string.IsNullOrWhiteSpace(tab.Name) ? "Main" : tab.Name));
                    Directory.CreateDirectory(dir);
                }
                int order = NextPinOrder(dir);
                string leaf = SafeLeaf(targetPath);
                string lnk = UniqueLink(dir, order, leaf);
                ShellLink.Create(lnk, targetPath, leaf);
                ScanOnce(); GroupsChanged?.Invoke();
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

                var temps = new List<Tuple<string, string>>();
                for (int i = 0; i < order.Count; i++)
                {
                    var o = order[i];
                    if (!File.Exists(o.Path)) continue;
                    var t = Path.Combine(dir, "~ord~" + Guid.NewGuid().ToString("N") + ".lnk");
                    File.Move(o.Path, t);
                    temps.Add(Tuple.Create(t, (i + 1).ToString("000") + "_" + o.Name + ".lnk"));
                }
                foreach (var t in temps) File.Move(t.Item1, Path.Combine(dir, t.Item2));

                ScanOnce(); GroupsChanged?.Invoke();
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
                var temp = new List<Tuple<string, string>>();
                for (int i = 0; i < ordered.Count; i++)
                {
                    var p = ordered[i];
                    if (!File.Exists(p.LinkPath)) continue;
                    var tmp = Path.Combine(dir, "~ord~" + Guid.NewGuid().ToString("N") + ".lnk");
                    File.Move(p.LinkPath, tmp);
                    temp.Add(Tuple.Create(tmp, (i + 1).ToString("000") + "_" + p.DisplayName + ".lnk"));
                }
                foreach (var t in temp) File.Move(t.Item1, Path.Combine(dir, t.Item2));
                ScanOnce(); GroupsChanged?.Invoke();
            }
            catch (Exception ex) { Log.Error("ReorderPins failed", ex); }
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
            try { action(); ScanOnce(); GroupsChanged?.Invoke(); }
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
            if (_debounce == null)
                _debounce = new Timer(_ => { ScanOnce(); GroupsChanged?.Invoke(); }, null, Timeout.Infinite, Timeout.Infinite);
            _debounce.Change(250, Timeout.Infinite);
        }

        public void Dispose()
        {
            try { _watcher?.Dispose(); } catch { }
            try { _debounce?.Dispose(); } catch { }
        }
    }
}
