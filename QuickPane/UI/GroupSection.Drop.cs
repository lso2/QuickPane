// GroupSection.Drop.cs
// Purpose: routes every drag and drop in the Groups section: pins, tabs, groups and files from Explorer.
// Reason: drop routing is one responsibility, separate from building rows (GroupSection.xaml.cs) and menus.
// Defines: GroupSection (partial): drag formats, drag state, drop handlers, drag ghost and drop line.
// Doc: docs/notes/QuickPane/UI/GroupSection.Drop.cs.md
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
        private const string PinFormat = "QuickPanePin";
        private const string GroupFormat = "QuickPaneGroup";
        private const string TabFormat = "QuickPaneTab";

        private Point _dragStart;
        private bool _maybeDrag;
        // Every pin, group header and tab shares these fields, so the press has to be tied to the element
        // it landed on; otherwise the drag is claimed by whichever one the pointer has reached by the time
        // it passes the threshold, and the row that moves is not the row that was grabbed.
        private object _pressedSource;
        private readonly Dictionary<FrameworkElement, DropLineAdorner> _adorners = new Dictionary<FrameworkElement, DropLineAdorner>();
        private DragGhostAdorner _ghost;
        private AdornerLayer _ghostLayer;
        private Vector _grab;

        private void OnRootDragOver(object sender, DragEventArgs e)
        {
            UpdateGhost(e);
            if (e.Data.GetDataPresent(TabFormat)) e.Effects = DragDropEffects.Move;
            // The Link cursor tells the truth here: dropping on empty space creates a shortcut pin.
            else if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effects = DragDropEffects.Link;
            else e.Effects = DragDropEffects.None;
            e.Handled = true;
        }

        private void OnRootDrop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(TabFormat))
            {
                e.Handled = true;
                var tr = e.Data.GetData(TabFormat) as TabRef;
                if (tr == null) return;
                var src = FindTab(tr.TabFolder);
                if (src.Item2 != null) App.Groups.MoveTabToNewGroup(src.Item1, src.Item2);
                return;
            }
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Handled = true;
                FolderTreeNode.ClearDropHighlight();
                PinToLastGroup(e.Data.GetData(DataFormats.FileDrop) as string[]);
            }
        }

        // Pin dropped paths to the last group's active tab; with no groups yet, create one.
        private static void PinToLastGroup(string[] paths)
        {
            if (paths == null || paths.Length == 0 || App.Groups == null) return;
            WorkQueue.Post(() =>
            {
                try
                {
                    var groups = App.Groups.Groups;
                    var last = groups.Count > 0 ? groups[groups.Count - 1] : App.Groups.CreateGroup("Pinned");
                    if (last == null) return;
                    App.Groups.AddPins(last.Active, paths.Where(PathIsReal).ToList());
                }
                catch (Exception ex) { Log.Error("pin to last group", ex); }
            });
        }

        // Pin dropped paths (files or folders) to one tab. The worker runs the path checks and the .lnk writes.
        private static void PinPathsToTab(PinnedTab tab, string[] paths)
        {
            if (tab == null || paths == null || paths.Length == 0 || App.Groups == null) return;
            WorkQueue.Post(() =>
            {
                try { App.Groups.AddPins(tab, paths.Where(PathIsReal).ToList()); }
                catch (Exception ex) { Log.Info("pin paths to tab '" + tab.Name + "' failed: " + ex.Message); }
            });
        }

        private static bool PathIsReal(string path)
        {
            try { return !string.IsNullOrEmpty(path) && (System.IO.Directory.Exists(path) || System.IO.File.Exists(path)); }
            catch { return false; }
        }

        // Tab button to the tab it shows, rebuilt with the rows.
        private readonly Dictionary<FrameworkElement, PinnedTab> _tabOfButton = new Dictionary<FrameworkElement, PinnedTab>();

        private FrameworkElement _tabPlaceholder;
        private System.Windows.Controls.Panel _tabPlaceholderBar;
        private int _tabPlaceholderIndex = -1;

        private void OnTabBarDragOver(StackPanel bar, PinnedGroup group, DragEventArgs e)
        {
            e.Handled = true;
            if (e.Data.GetDataPresent(TabFormat))
            {
                int index = ComputeTabIndex(bar, e);
                // Only move the placeholder when the target index actually changes, so it does not jitter.
                if (index != _tabPlaceholderIndex || !ReferenceEquals(_tabPlaceholderBar, bar))
                {
                    var tr = e.Data.GetData(TabFormat) as TabRef;
                    var src = tr != null ? FindTab(tr.TabFolder) : Tuple.Create<PinnedGroup, PinnedTab>(null, null);
                    ShowTabPlaceholder(bar, index, src.Item2 != null ? src.Item2.Name : "Tab");
                }
                e.Effects = DragDropEffects.Move;
                return;
            }
            UpdateGhost(e);
            RemoveTabPlaceholder();
            bool pin = e.Data.GetDataPresent(PinFormat), files = e.Data.GetDataPresent(DataFormats.FileDrop);
            if (pin || files)
            {
                // The lit tab receives the drop. Over the strip's empty end nothing is lit and the active tab receives it.
                Border over;
                TabAt(bar, e, out over);
                if (over != null) FolderTreeNode.HighlightDropRow(over);
                else FolderTreeNode.ClearDropHighlight();
                e.Effects = pin ? DragDropEffects.Move : DragDropEffects.Link; // a file dropped here is pinned as a shortcut
                return;
            }
            FolderTreeNode.ClearDropHighlight();
            e.Effects = e.Data.GetDataPresent(GroupFormat) ? DragDropEffects.Move : DragDropEffects.None;
        }

        private void OnTabBarDragLeave(FrameworkElement strip, DragEventArgs e)
        {
            RemoveTabPlaceholder();
            // WPF also raises DragLeave when the pointer crosses from one tab button to the next inside the strip.
            var p = e.GetPosition(strip);
            if (p.X < 0 || p.Y < 0 || p.X >= strip.ActualWidth || p.Y >= strip.ActualHeight) FolderTreeNode.ClearDropHighlight();
        }

        /// <summary>The tab whose button is under the pointer, or null over the strip's empty end.</summary>
        private PinnedTab TabAt(System.Windows.Controls.Panel bar, DragEventArgs e, out Border button)
        {
            button = null;
            double x = e.GetPosition(bar).X;
            foreach (var child in bar.Children)
            {
                var b = child as Border;
                PinnedTab tab;
                if (b == null || !_tabOfButton.TryGetValue(b, out tab)) continue;
                double left = b.TranslatePoint(new Point(0, 0), bar).X;
                if (x >= left && x < left + b.ActualWidth) { button = b; return tab; }
            }
            return null;
        }

        /// <summary>Moves a dropped pin to the end of the tab, or pins dropped files to it.</summary>
        private void DropOnTab(PinnedTab tab, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                PinPathsToTab(tab, e.Data.GetData(DataFormats.FileDrop) as string[]);
                return;
            }
            var pr = e.Data.GetData(PinFormat) as PinRef;
            if (pr == null) return;
            var hit = FindPin(pr.LinkPath);
            if (hit.Item2 == null || ReferenceEquals(hit.Item1, tab)) return; // dropped on the tab it is already in
            App.Groups.MovePinToTab(hit.Item2, tab);
        }

        private void OnTabBarDrop(StackPanel bar, PinnedGroup group, DragEventArgs e)
        {
            e.Handled = true;
            RemoveTabPlaceholder();
            FolderTreeNode.ClearDropHighlight();
            if (e.Data.GetDataPresent(TabFormat))
            {
                var tr = e.Data.GetData(TabFormat) as TabRef;
                if (tr != null) DropTabAtIndex(group, ComputeTabIndex(bar, e), tr);
                return;
            }
            Border over;
            var target = TabAt(bar, e, out over);
            if (target != null && (e.Data.GetDataPresent(PinFormat) || e.Data.GetDataPresent(DataFormats.FileDrop)))
            {
                try { DropOnTab(target, e); }
                catch (Exception ex) { Log.Info("drop on tab '" + target.Name + "' failed: " + ex.Message); }
                return;
            }
            OnGroupDrop(group, e);
        }

        private int ComputeTabIndex(System.Windows.Controls.Panel bar, DragEventArgs e)
        {
            double x = e.GetPosition(bar).X;
            int idx = 0;
            foreach (var child in bar.Children)
            {
                var fe = child as FrameworkElement;
                if (fe == null) continue;
                var t = fe.Tag as string;
                if (t != "tab") continue; // skip the placeholder
                double left = fe.TranslatePoint(new Point(0, 0), bar).X;
                if (x > left + fe.ActualWidth / 2) idx++; else break;
            }
            return idx;
        }

        private void ShowTabPlaceholder(StackPanel bar, int index, string name)
        {
            RemoveTabPlaceholder();
            var ph = new Border
            {
                Tag = "ph",
                Background = UiHelpers.AppBrush("ItemHoverBackground"),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(6, 0, 6, 0),
                Margin = new Thickness(2, 0, 2, 0),
                Child = new TextBlock
                {
                    Text = name,
                    FontSize = 12,
                    Opacity = 0.6,
                    Foreground = UiHelpers.AppBrush("TextPrimary"),
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            int insertAt = Math.Min(index, bar.Children.Count);
            bar.Children.Insert(insertAt, ph);
            _tabPlaceholder = ph;
            _tabPlaceholderBar = bar;
            _tabPlaceholderIndex = index;
        }

        private void RemoveTabPlaceholder()
        {
            if (_tabPlaceholder != null && _tabPlaceholderBar != null && _tabPlaceholderBar.Children.Contains(_tabPlaceholder))
                _tabPlaceholderBar.Children.Remove(_tabPlaceholder);
            _tabPlaceholder = null;
            _tabPlaceholderBar = null;
            _tabPlaceholderIndex = -1;
        }

        private void DropTabAtIndex(PinnedGroup destGroup, int index, TabRef tr)
        {
            var src = FindTab(tr.TabFolder);
            if (src.Item2 == null) return;
            if (ReferenceEquals(src.Item1, destGroup))
            {
                var ordered = destGroup.Tabs.Where(t => !ReferenceEquals(t, src.Item2)).ToList();
                if (index < 0) index = 0;
                if (index > ordered.Count) index = ordered.Count;
                ordered.Insert(index, src.Item2);
                App.Groups.ReorderTabs(destGroup, ordered);
            }
            else
            {
                App.Groups.MoveTabToGroup(src.Item2, destGroup);
            }
        }

        // ---- group drag (header / row) ----
        private void WireGroupDrag(FrameworkElement source, PinnedGroup group)
        {
            source.PreviewMouseLeftButtonDown += (s, e) =>
            {
                if (e.ClickCount == 2) return;
                BeginMaybeDrag(source, e);
            };
            source.PreviewMouseLeftButtonUp += (s, e) => EndMaybeDrag();
            source.MouseMove += (s, e) =>
            {
                if (!DragStarted(source, e)) return;
                try
                {
                    var data = new DataObject();
                    data.SetData(GroupFormat, new GroupRef { FolderPath = group.FolderPath });
                    StartGhost(source, e);
                    DragDrop.DoDragDrop(source, data, DragDropEffects.Move);
                }
                catch (Exception ex) { Log.Error("group drag", ex); }
                finally { EndGhost(); }
            };
        }

        private void EnableGroupDrop(FrameworkElement target, PinnedGroup group)
        {
            target.AllowDrop = true;
            target.DragOver += (s, e) =>
            {
                UpdateGhost(e);
                if (e.Data.GetDataPresent(GroupFormat))
                {
                    // Top third inserts before, bottom third inserts after (with a line), the middle
                    // merges the dragged group in as tabs.
                    double y = e.GetPosition(target).Y, h = Math.Max(1, target.ActualHeight);
                    if (y < h * 0.33) Adorner(target).Update(1);
                    else if (y > h * 0.66) Adorner(target).Update(h - 1);
                    else HideAdorner(target);
                    e.Effects = DragDropEffects.Move;
                }
                else
                {
                    HideAdorner(target);
                    if (e.Data.GetDataPresent(TabFormat) || e.Data.GetDataPresent(PinFormat)) e.Effects = DragDropEffects.Move;
                    else if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effects = DragDropEffects.Link; // dropping here pins (creates a shortcut)
                    else e.Effects = DragDropEffects.None;
                }
                e.Handled = true;
            };
            target.DragLeave += (s, e) => HideAdorner(target);
            target.Drop += (s, e) =>
            {
                e.Handled = true;
                HideAdorner(target);
                if (e.Data.GetDataPresent(GroupFormat))
                {
                    var gr = e.Data.GetData(GroupFormat) as GroupRef;
                    var src = App.Groups.Groups.FirstOrDefault(g => gr != null && string.Equals(g.FolderPath, gr.FolderPath, StringComparison.OrdinalIgnoreCase));
                    if (src == null || ReferenceEquals(src, group)) return;
                    double y = e.GetPosition(target).Y, h = Math.Max(1, target.ActualHeight);
                    if (y >= h * 0.33 && y <= h * 0.66) App.Groups.MergeGroups(src, group);
                    else ReorderGroupRelative(src, group, y > h * 0.66);
                    return;
                }
                OnGroupDrop(group, e);
            };
        }

        private void ReorderGroupRelative(PinnedGroup dragged, PinnedGroup target, bool after)
        {
            var ordered = App.Groups.Groups.ToList();
            ordered.Remove(dragged);
            int idx = ordered.FindIndex(g => ReferenceEquals(g, target));
            if (idx < 0) return;
            ordered.Insert(after ? idx + 1 : idx, dragged);
            App.Groups.ReorderGroups(ordered);
        }

        private void OnGroupDrop(PinnedGroup group, DragEventArgs e)
        {
            try
            {
                if (e.Data.GetDataPresent(GroupFormat))
                {
                    var gr = e.Data.GetData(GroupFormat) as GroupRef;
                    var src = App.Groups.Groups.FirstOrDefault(g => string.Equals(g.FolderPath, gr.FolderPath, StringComparison.OrdinalIgnoreCase));
                    if (src != null && !ReferenceEquals(src, group)) App.Groups.MergeGroups(src, group);
                    return;
                }
                if (e.Data.GetDataPresent(TabFormat))
                {
                    var tr = e.Data.GetData(TabFormat) as TabRef;
                    var src = FindTab(tr.TabFolder);
                    if (src.Item2 != null && !ReferenceEquals(src.Item1, group)) App.Groups.MoveTabToGroup(src.Item2, group);
                    return;
                }
                if (e.Data.GetDataPresent(DataFormats.FileDrop))
                {
                    // Dropping on a group header or the tab row outside a tab button pins to the active tab.
                    PinPathsToTab(group.Active, e.Data.GetData(DataFormats.FileDrop) as string[]);
                    return;
                }
                if (e.Data.GetDataPresent(PinFormat))
                {
                    var pr = e.Data.GetData(PinFormat) as PinRef;
                    if (pr != null) MovePinToTabEnd(pr.LinkPath, group.Active);
                }
            }
            catch (Exception ex) { Log.Error("group drop", ex); }
        }

        /// <summary>Remember the element a press landed on and start watching for a drag.</summary>
        private void BeginMaybeDrag(object source, MouseButtonEventArgs e)
        {
            _pressedSource = source;
            _dragStart = e.GetPosition(null);
            _maybeDrag = true;
        }

        /// <summary>True once the pointer has travelled far enough for the press on this element to be a drag.</summary>
        private bool DragStarted(object source, MouseEventArgs e)
        {
            if (!_maybeDrag || !ReferenceEquals(_pressedSource, source)) return false;
            if (e.LeftButton != MouseButtonState.Pressed) { _maybeDrag = false; return false; }
            var p = e.GetPosition(null);
            if (Math.Abs(p.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(p.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return false;
            _maybeDrag = false;
            return true;
        }

        private void EndMaybeDrag() { _maybeDrag = false; _pressedSource = null; }

        // ---- pins ----
        private void WirePinDragSource(FrameworkElement source, PinnedFolder pin)
        {
            source.PreviewMouseLeftButtonDown += (s, e) => BeginMaybeDrag(source, e);
            source.PreviewMouseLeftButtonUp += (s, e) => EndMaybeDrag();
            source.MouseMove += (s, e) =>
            {
                if (!DragStarted(source, e)) return;
                try
                {
                    var data = new DataObject();
                    data.SetData(PinFormat, new PinRef { LinkPath = pin.LinkPath });
                    StartGhost(source, e);
                    DragDrop.DoDragDrop(source, data, DragDropEffects.Move);
                }
                catch (Exception ex) { Log.Error("pin drag", ex); }
                finally { EndGhost(); }
            };
        }

        private void EnableItemsDrop(StackPanel items, PinnedGroup group)
        {
            items.AllowDrop = true;
            items.DragOver += (s, e) =>
            {
                e.Handled = true;
                UpdateGhost(e);
                if (e.Data.GetDataPresent(GroupFormat))
                {
                    // Dropping a group over the body of another group reorders it just after.
                    Adorner(items).Update(items.ActualHeight > 0 ? items.ActualHeight - 1 : 0);
                    e.Effects = DragDropEffects.Move;
                    return;
                }
                if (e.Data.GetDataPresent(TabFormat)) { e.Effects = DragDropEffects.None; return; }
                int index; double lineY;
                ComputeItemDrop(items, e, out index, out lineY);
                Adorner(items).Update(lineY);
                // Reordering a pin is a move; a FileDrop between rows pins there (creates a shortcut).
                e.Effects = e.Data.GetDataPresent(PinFormat) ? DragDropEffects.Move : DragDropEffects.Link;
            };
            items.DragLeave += (s, e) => HideAdorner(items);
            items.Drop += (s, e) =>
            {
                HideAdorner(items);
                e.Handled = true;
                if (e.Data.GetDataPresent(GroupFormat))
                {
                    var gr = e.Data.GetData(GroupFormat) as GroupRef;
                    var src = App.Groups.Groups.FirstOrDefault(g => gr != null && string.Equals(g.FolderPath, gr.FolderPath, StringComparison.OrdinalIgnoreCase));
                    if (src != null && !ReferenceEquals(src, group)) ReorderGroupRelative(src, group, true);
                    return;
                }
                int index; double lineY;
                ComputeItemDrop(items, e, out index, out lineY);
                if (e.Data.GetDataPresent(DataFormats.FileDrop))
                {
                    // Pin files or folders at the drop line. Verification and .lnk writes run on the
                    // worker so a slow source volume cannot stall the input-attached UI thread.
                    FolderTreeNode.ClearDropHighlight();
                    var dropped = e.Data.GetData(DataFormats.FileDrop) as string[];
                    var tab = group.Active;
                    int at = index;
                    WorkQueue.Post(() =>
                    {
                        try
                        {
                            foreach (var path in dropped ?? new string[0])
                                if (PathIsReal(path)) { App.Groups.AddPinAtIndex(tab, path, at); at++; }
                        }
                        catch (Exception ex) { Log.Error("pin at index", ex); }
                    });
                    return;
                }
                if (e.Data.GetDataPresent(PinFormat))
                {
                    var pr = e.Data.GetData(PinFormat) as PinRef;
                    if (pr != null) MovePinToIndex(pr.LinkPath, group.Active, index);
                }
            };
        }

        private void ComputeItemDrop(StackPanel items, DragEventArgs e, out int index, out double lineY)
        {
            double y = e.GetPosition(items).Y;
            index = items.Children.Count;
            lineY = items.ActualHeight > 0 ? items.ActualHeight - 1 : 0;
            for (int i = 0; i < items.Children.Count; i++)
            {
                var child = items.Children[i] as FrameworkElement;
                if (child == null) continue;
                double top = child.TranslatePoint(new Point(0, 0), items).Y;
                double mid = top + child.ActualHeight / 2;
                if (y < mid) { index = i; lineY = top; return; }
            }
        }

        private void MovePinToIndex(string linkPath, PinnedTab destTab, int index)
        {
            var hit = FindPin(linkPath);
            if (hit.Item2 == null || destTab == null) return;
            if (!ReferenceEquals(hit.Item1, destTab)) { App.Groups.MovePinToTab(hit.Item2, destTab); return; }
            var ordered = destTab.Items.Where(p => !ReferenceEquals(p, hit.Item2)).ToList();
            if (index < 0) index = 0;
            if (index > ordered.Count) index = ordered.Count;
            ordered.Insert(index, hit.Item2);
            App.Groups.ReorderPins(destTab, ordered);
        }

        private void MovePinToTabEnd(string linkPath, PinnedTab destTab)
        {
            var hit = FindPin(linkPath);
            if (hit.Item2 != null && destTab != null) App.Groups.MovePinToTab(hit.Item2, destTab);
        }

        private static Tuple<PinnedTab, PinnedFolder> FindPin(string linkPath)
        {
            foreach (var g in App.Groups.Groups)
                foreach (var t in g.Tabs)
                {
                    var hit = t.Items.FirstOrDefault(p => string.Equals(p.LinkPath, linkPath, StringComparison.OrdinalIgnoreCase));
                    if (hit != null) return Tuple.Create(t, hit);
                }
            return Tuple.Create<PinnedTab, PinnedFolder>(null, null);
        }

        private static Tuple<PinnedGroup, PinnedTab> FindTab(string tabFolder)
        {
            foreach (var g in App.Groups.Groups)
            {
                var t = g.Tabs.FirstOrDefault(x => string.Equals(x.FolderPath, tabFolder, StringComparison.OrdinalIgnoreCase));
                if (t != null) return Tuple.Create(g, t);
            }
            return Tuple.Create<PinnedGroup, PinnedTab>(null, null);
        }

        // ---- drag ghost / drop line ----
        private void StartGhost(FrameworkElement source, MouseEventArgs e)
        {
            SidebarControl.SuppressAutoHide = true;
            try
            {
                var layer = AdornerLayer.GetAdornerLayer(Root);
                if (layer == null) return;
                _grab = (Vector)e.GetPosition(source);
                _ghost = new DragGhostAdorner(Root, source, source.RenderSize);
                layer.Add(_ghost);
                _ghostLayer = layer;
            }
            catch (Exception ex) { Log.Error("start ghost", ex); }
        }

        private void UpdateGhost(DragEventArgs e)
        {
            if (_ghost == null) return;
            var p = e.GetPosition(Root);
            _ghost.SetPosition(new Point(p.X - _grab.X, p.Y - _grab.Y));
        }

        private void EndGhost()
        {
            SidebarControl.SuppressAutoHide = false;
            try { if (_ghost != null && _ghostLayer != null) _ghostLayer.Remove(_ghost); } catch { }
            _ghost = null; _ghostLayer = null;
            FolderTreeNode.ClearDropHighlight(); // no row keeps a drag highlight after any drag ends
        }

        private DropLineAdorner Adorner(FrameworkElement host)
        {
            DropLineAdorner a;
            if (_adorners.TryGetValue(host, out a)) return a;
            var layer = AdornerLayer.GetAdornerLayer(host);
            a = new DropLineAdorner(host);
            if (layer != null) layer.Add(a);
            _adorners[host] = a;
            return a;
        }

        private void HideAdorner(FrameworkElement host)
        {
            DropLineAdorner a;
            if (_adorners.TryGetValue(host, out a)) a.Hide();
        }

        private sealed class PinRef { public string LinkPath { get; set; } }
        private sealed class GroupRef { public string FolderPath { get; set; } }
        private sealed class TabRef { public string GroupFolder { get; set; } public string TabFolder { get; set; } }
    }
}
