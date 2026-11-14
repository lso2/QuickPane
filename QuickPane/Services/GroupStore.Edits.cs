// GroupStore.Edits.cs
// Purpose: edits to the active profile's groups, tabs and pins: create, rename, move, reorder, delete.
// Reason: every edit writes folders or .lnk files and then rescans; loading and cross-profile moves are separate files.
// Defines: GroupStore (partial): group, tab and pin edits.
// Doc: docs/notes/QuickPane/Services/GroupStore.Edits.cs.md
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
    public partial class GroupStore
    {
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
            Try(() => Recycle(group.FolderPath));
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
    }
}
