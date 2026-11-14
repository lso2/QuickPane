// GroupSection.Menus.cs
// Purpose: right-click menus and the prompts they open for groups, tabs and pins in the Groups section.
// Reason: menu commands are one responsibility, separate from building rows and from drag and drop.
// Defines: GroupSection (partial): BuildGroupMenu, BuildTabMenu, BuildPinMenu and their helpers.
// Doc: docs/notes/QuickPane/UI/GroupSection.Menus.cs.md
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using WinForms = System.Windows.Forms;
using QuickPane.Interop;
using QuickPane.Models;
using QuickPane.Services;

namespace QuickPane.UI
{
    public partial class GroupSection
    {
        // ---- menus ----
        private ContextMenu BuildGroupMenu(PinnedGroup group, PinnedTab activeTab)
        {
            var menu = new ContextMenu();
            menu.Items.Add(Item("Rename", () => RenameGroupOrTab(group, group.Tabs.Count == 1 ? group.Tabs[0] : activeTab)));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Add tab", () => { var v = TextPrompt.Ask("New tab name", ""); if (!string.IsNullOrWhiteSpace(v)) App.Groups.AddTab(group, v); }));
            menu.Items.Add(Item("Add group below", () => { var v = TextPrompt.Ask("New group name", ""); if (!string.IsNullOrWhiteSpace(v)) App.Groups.CreateGroupAfter(group, v); }));
            menu.Items.Add(Item("Add folder...", () => { var p = PickFolder(); if (!string.IsNullOrEmpty(p)) App.Groups.AddPin(group, p); }));
            menu.Items.Add(Item("Add file...", () => { var p = PickFile(); if (!string.IsNullOrEmpty(p)) App.Groups.AddPin(group, p); }));
            menu.Items.Add(Item("Move up", () => App.Groups.MoveGroup(group, -1)));
            menu.Items.Add(Item("Move down", () => App.Groups.MoveGroup(group, +1)));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Delete group", () => ConfirmDeleteGroup(group)));
            return menu;
        }

        private ContextMenu BuildTabMenu(PinnedGroup group, PinnedTab tab)
        {
            var menu = new ContextMenu();
            menu.Items.Add(Item("Rename tab", () => RenameTab(tab)));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Move tab left", () => App.Groups.MoveTab(group, tab, -1)));
            menu.Items.Add(Item("Move tab right", () => App.Groups.MoveTab(group, tab, +1)));
            menu.Items.Add(Item("Make tab its own group", () => App.Groups.MoveTabToNewGroup(group, tab)));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Delete tab", () => App.Groups.DeleteTab(group, tab)));
            return menu;
        }

        /// <summary>
        /// Point a pin somewhere else without removing and re-pinning it. A pin whose target moved, or
        /// was captured wrongly in the first place, keeps its name and its place in the group and only
        /// its destination changes. The text is taken as typed, because a share that is offline right
        /// now is still the path that was meant.
        /// </summary>
        private void EditPinPath(PinnedFolder pin)
        {
            try
            {
                if (pin == null || string.IsNullOrEmpty(pin.LinkPath)) return;
                var typed = TextPrompt.Ask("Edit path", pin.TargetPath ?? "");
                if (typed == null) return;
                typed = typed.Trim().Trim('"');
                if (typed.Length == 0) return;
                if (string.Equals(typed, pin.TargetPath, StringComparison.OrdinalIgnoreCase)) return;

                ShellLink.Create(pin.LinkPath, typed);
                Log.Info("pin \"" + pin.DisplayName + "\" now points at " + typed + ".");
                if (App.Groups != null) App.Groups.Reload();
            }
            catch (Exception ex) { Log.Error("edit the path of '" + (pin == null ? "" : pin.DisplayName) + "'", ex); }
        }

        private ContextMenu BuildPinMenu(PinnedGroup group, PinnedTab tab, PinnedFolder pin)
        {
            var menu = new ContextMenu();
            if (pin.IsFile)
            {
                menu.Items.Add(Item("Open file", () => OpenPinFile(pin)));
                menu.Items.Add(Item("Open containing folder", () =>
                {
                    try
                    {
                        var d = System.IO.Path.GetDirectoryName(pin.TargetPath);
                        if (!string.IsNullOrEmpty(d)) _navigate(d);
                    }
                    catch (Exception ex) { Log.Error("open containing folder", ex); }
                }));
            }
            else
            {
                menu.Items.Add(Item("Open", () => { if (pin.Exists) _navigate(pin.TargetPath); }));
                menu.Items.Add(Item("Open in new window", () => ExplorerNavigator.OpenNewWindow(pin.TargetPath)));
            }
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Rename", () => { var v = TextPrompt.Ask("Rename pin", pin.DisplayName); if (v != null) App.Groups.RenamePin(pin, v); }));
            menu.Items.Add(Item("Edit path", () => EditPinPath(pin)));
            menu.Items.Add(new Separator());

            var moveTo = new MenuItem { Header = "Move to group" };
            foreach (var g in App.Groups.Groups.Where(x => !ReferenceEquals(x, group)))
            {
                var dest = g;
                moveTo.Items.Add(Item(GroupStore.DisplayName(g), () => App.Groups.MovePinToGroup(pin, dest)));
            }
            if (moveTo.Items.Count == 0) moveTo.IsEnabled = false;
            menu.Items.Add(moveTo);

            menu.Items.Add(Item("Move up", () => App.Groups.MovePin(tab, pin, -1)));
            menu.Items.Add(Item("Move down", () => App.Groups.MovePin(tab, pin, +1)));
            menu.Items.Add(Item("Copy path", () => SafeClipboard(pin.TargetPath)));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Remove Shortcut", () => App.Groups.RemovePin(pin)));
            return menu;
        }

        private void ConfirmDeleteGroup(PinnedGroup group)
        {
            int count = group.Tabs.Sum(t => t.Items.Count);
            if (count > 0)
            {
                var r = WinForms.MessageBox.Show(
                    "Delete this group and its " + count + " pin(s)?", "QuickPane",
                    WinForms.MessageBoxButtons.YesNo, WinForms.MessageBoxIcon.Warning);
                if (r != WinForms.DialogResult.Yes) return;
            }
            App.Groups.DeleteGroup(group);
        }

        private void RenameGroupOrTab(PinnedGroup group, PinnedTab tab)
        {
            if (group.Tabs.Count == 1) RenameGroupSingle(group);
            else RenameTab(tab);
        }

        private void RenameGroupSingle(PinnedGroup group)
        {
            var v = TextPrompt.Ask("Rename", group.Tabs.Count == 1 ? group.Tabs[0].Name : GroupStore.DisplayName(group));
            if (v != null) App.Groups.RenameGroup(group, v);
        }

        private void RenameTab(PinnedTab tab)
        {
            var v = TextPrompt.Ask("Rename tab", tab.Name);
            if (v != null) App.Groups.RenameTab(tab, v);
        }

        // ---- helpers ----
        private static MenuItem Item(string header, Action action)
        {
            var mi = new MenuItem { Header = header };
            mi.Click += (s, e) => { try { action(); } catch (Exception ex) { Log.Error("menu action", ex); } };
            return mi;
        }

        private static string PickFolder()
        {
            using (var dlg = new WinForms.FolderBrowserDialog())
            {
                dlg.Description = "Pick a folder to pin";
                return dlg.ShowDialog() == WinForms.DialogResult.OK ? dlg.SelectedPath : null;
            }
        }

        private static string PickFile()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Pick a file to pin" };
            return dlg.ShowDialog() == true ? dlg.FileName : null;
        }

        private static void OpenPinFile(PinnedFolder pin)
        {
            try
            {
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(pin.TargetPath) { UseShellExecute = true });
            }
            catch (Exception ex) { Log.Error("open pinned file", ex); }
        }

        private static void SafeClipboard(string text)
        {
            try { if (!string.IsNullOrEmpty(text)) Clipboard.SetText(text); }
            catch (Exception ex) { Log.Error("clipboard set", ex); }
        }
    }
}
