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
    /// <summary>The Groups section. Each group holds one or more tabs; a single-tab group looks like a
    /// plain group, several tabs show a scrollable tab row. Pins, tabs, and groups all drag to reorder,
    /// dragging a group onto another merges its tabs in, and dragging a tab to empty space makes it a
    /// group. Naming uses a top-level prompt because the embedded pane cannot host an inline text box.</summary>
    public partial class GroupSection : UserControl
    {
        private const string PinFormat = "QuickPanePin";
        private const string GroupFormat = "QuickPaneGroup";
        private const string TabFormat = "QuickPaneTab";

        private Action<string> _navigate;
        private Point _dragStart;
        private bool _maybeDrag;
        private readonly Dictionary<FrameworkElement, DropLineAdorner> _adorners = new Dictionary<FrameworkElement, DropLineAdorner>();
        private DragGhostAdorner _ghost;
        private AdornerLayer _ghostLayer;
        private Vector _grab;

        public GroupSection()
        {
            InitializeComponent();
            // Empty space in the section is a drop target so a tab dropped here becomes its own group.
            Root.Background = Brushes.Transparent;
            Root.AllowDrop = true;
            Root.DragOver += OnRootDragOver;
            Root.Drop += OnRootDrop;
        }

        private void OnRootDragOver(object sender, DragEventArgs e)
        {
            UpdateGhost(e);
            e.Effects = e.Data.GetDataPresent(TabFormat) ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
        }

        private void OnRootDrop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(TabFormat)) return;
            e.Handled = true;
            var tr = e.Data.GetData(TabFormat) as TabRef;
            if (tr == null) return;
            var src = FindTab(tr.TabFolder);
            if (src.Item2 != null) App.Groups.MoveTabToNewGroup(src.Item1, src.Item2);
        }

        public void Build(Action<string> navigate)
        {
            _navigate = navigate;
            Root.Children.Clear();
            _adorners.Clear();
            if (App.Groups == null) return;

            var groups = App.Groups.Groups;
            for (int i = 0; i < groups.Count; i++)
            {
                AddGroup(groups[i]);
                if (i < groups.Count - 1) Root.Children.Add(UiHelpers.MakeSeparator());
            }
            if (groups.Count == 0) Root.Children.Add(BuildAddGroupRow());
        }

        private void Rebuild() { Build(_navigate); }

        private void AddGroup(PinnedGroup group)
        {
            group.Expanded = UiState.GetExpanded("group:" + group.FolderPath, group.Expanded);
            var items = new StackPanel();
            RotateTransform chevron;
            FrameworkElement header = group.Tabs.Count > 1
                ? BuildTabRow(group, items, out chevron)
                : BuildSingleHeader(group, items, out chevron);

            // active tab pins
            var active = group.Active;
            if (active != null)
            {
                foreach (var pin in active.Items)
                {
                    var captured = pin;
                    var node = new FolderTreeNode(pin.DisplayName, pin.TargetPath, pin.Exists, _navigate, 0);
                    node.Row.ContextMenu = BuildPinMenu(group, active, captured);
                    WirePinDragSource(node.Row, captured);
                    items.Children.Add(node);
                }
            }
            EnableItemsDrop(items, group);

            if (!group.Expanded) { items.Visibility = Visibility.Collapsed; items.MaxHeight = 0; }
            else chevron.Angle = 90;

            Root.Children.Add(header);
            Root.Children.Add(items);
        }

        // ---- single-tab header ----
        private FrameworkElement BuildSingleHeader(PinnedGroup group, StackPanel items, out RotateTransform chevron)
        {
            var tab = group.Tabs[0];
            TextBlock label;
            Grid header = null;
            header = UiHelpers.BuildHeader(tab.Name,
                () => { group.Expanded = !group.Expanded; UiState.SetExpanded("group:" + group.FolderPath, group.Expanded); UiHelpers.ToggleExpand(items, ChevronOf(header), group.Expanded); },
                () => RenameGroupOrTab(group, tab),
                out chevron, out label);
            header.ContextMenu = BuildGroupMenu(group, tab);
            AddPlusButton(header, group);
            WireGroupDrag(header, group);
            EnableGroupDrop(header, group);
            _chevrons[header] = chevron;
            return header;
        }

        private readonly Dictionary<FrameworkElement, RotateTransform> _chevrons = new Dictionary<FrameworkElement, RotateTransform>();
        private RotateTransform ChevronOf(FrameworkElement header) { RotateTransform r; return _chevrons.TryGetValue(header, out r) ? r : null; }

        // ---- multi-tab row ----
        private FrameworkElement BuildTabRow(PinnedGroup group, StackPanel items, out RotateTransform chevron)
        {
            var grid = new Grid { Margin = new Thickness(6, 6, 8, 2) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            chevron = new RotateTransform(0);
            var glyph = new TextBlock
            {
                Text = "",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 10,
                Foreground = UiHelpers.AppBrush("ChevronColor"),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = chevron
            };
            var rt = chevron;
            var chevHit = new Border { Width = 18, Background = Brushes.Transparent, Cursor = Cursors.Hand, Child = glyph };
            chevHit.MouseLeftButtonUp += (s, e) => { group.Expanded = !group.Expanded; UiState.SetExpanded("group:" + group.FolderPath, group.Expanded); UiHelpers.ToggleExpand(items, rt, group.Expanded); };
            WireGroupDrag(chevHit, group); // drag the chevron to move the whole tab group
            Grid.SetColumn(chevHit, 0);
            grid.Children.Add(chevHit);

            var bar = new StackPanel { Orientation = Orientation.Horizontal };
            for (int i = 0; i < group.Tabs.Count; i++)
                bar.Children.Add(BuildTabButton(group, group.Tabs[i], i));

            var scroller = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = bar,
                Tag = "tabscroll" // SidebarControl's wheel handler routes horizontal scroll here
            };
            scroller.AllowDrop = true;
            scroller.DragOver += (s, e) => OnTabBarDragOver(bar, group, e);
            scroller.DragLeave += (s, e) => RemoveTabPlaceholder();
            scroller.Drop += (s, e) => OnTabBarDrop(bar, group, e);
            Grid.SetColumn(scroller, 1);
            grid.Children.Add(scroller);

            var addTab = new Button
            {
                Content = "+",
                FontSize = 13,
                FontWeight = FontWeights.Bold,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = UiHelpers.AppBrush("ChevronColor"),
                Cursor = Cursors.Hand,
                Padding = new Thickness(6, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "Add tab"
            };
            addTab.Click += (s, e) =>
            {
                var v = TextPrompt.Ask("New tab name", "");
                if (!string.IsNullOrWhiteSpace(v)) App.Groups.AddTab(group, v);
            };
            Grid.SetColumn(addTab, 2);
            grid.Children.Add(addTab);

            grid.ContextMenu = BuildGroupMenu(group, group.Active);
            // The row is a drop target for groups (merge) and tabs (move in).
            EnableGroupDrop(grid, group);
            return grid;
        }

        private FrameworkElement BuildTabButton(PinnedGroup group, PinnedTab tab, int index)
        {
            bool activeTab = index == group.ActiveTab;
            var text = new TextBlock
            {
                Text = tab.Name,
                FontSize = 12,
                FontWeight = activeTab ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = activeTab ? UiHelpers.AppBrush("TextPrimary") : UiHelpers.AppBrush("TextSecondary"),
                VerticalAlignment = VerticalAlignment.Center
            };
            var underline = new Border
            {
                Height = 2,
                Margin = new Thickness(0, 2, 0, 0),
                Background = activeTab ? UiHelpers.AppBrush("AccentBrush") : Brushes.Transparent,
                CornerRadius = new CornerRadius(1)
            };
            var stack = new StackPanel { Margin = new Thickness(6, 0, 6, 0) };
            stack.Children.Add(text);
            stack.Children.Add(underline);

            var btn = new Border { Background = Brushes.Transparent, Cursor = Cursors.Hand, Child = stack, Tag = "tab" };
            btn.ContextMenu = BuildTabMenu(group, tab);

            btn.PreviewMouseLeftButtonDown += (s, e) =>
            {
                if (e.ClickCount == 2) { e.Handled = true; RenameTab(tab); return; }
                _dragStart = e.GetPosition(null); _maybeDrag = true;
            };
            btn.PreviewMouseLeftButtonUp += (s, e) =>
            {
                if (_maybeDrag)
                {
                    App.Groups.SetActiveTab(group, index);
                    group.Expanded = true; // clicking a tab while collapsed opens the group on that tab
                    UiState.SetExpanded("group:" + group.FolderPath, true);
                    Rebuild();
                }
                _maybeDrag = false;
            };
            btn.MouseMove += (s, e) =>
            {
                if (!_maybeDrag || e.LeftButton != MouseButtonState.Pressed) return;
                var p = e.GetPosition(null);
                if (Math.Abs(p.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
                    Math.Abs(p.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                _maybeDrag = false;
                try
                {
                    var data = new DataObject();
                    data.SetData(TabFormat, new TabRef { GroupFolder = group.FolderPath, TabFolder = tab.FolderPath });
                    SidebarControl.SuppressAutoHide = true;
                    // No floating ghost for tabs; the inline placeholder is the preview.
                    DragDrop.DoDragDrop(btn, data, DragDropEffects.Move);
                }
                catch (Exception ex) { Log.Error("tab drag", ex); }
                finally { SidebarControl.SuppressAutoHide = false; RemoveTabPlaceholder(); }
            };

            return btn;
        }

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
            if (e.Data.GetDataPresent(GroupFormat) || e.Data.GetDataPresent(PinFormat)) e.Effects = DragDropEffects.Move;
            else if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effects = DragDropEffects.Copy;
            else e.Effects = DragDropEffects.None;
        }

        private void OnTabBarDrop(StackPanel bar, PinnedGroup group, DragEventArgs e)
        {
            e.Handled = true;
            RemoveTabPlaceholder();
            int index = ComputeTabIndex(bar, e);
            if (e.Data.GetDataPresent(TabFormat))
            {
                var tr = e.Data.GetData(TabFormat) as TabRef;
                if (tr != null) DropTabAtIndex(group, index, tr);
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
                _dragStart = e.GetPosition(null); _maybeDrag = true;
            };
            source.PreviewMouseLeftButtonUp += (s, e) => _maybeDrag = false;
            source.MouseMove += (s, e) =>
            {
                if (!_maybeDrag || e.LeftButton != MouseButtonState.Pressed) return;
                var p = e.GetPosition(null);
                if (Math.Abs(p.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
                    Math.Abs(p.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                _maybeDrag = false;
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
                    else if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effects = DragDropEffects.Copy;
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
                    foreach (var path in (string[])e.Data.GetData(DataFormats.FileDrop))
                        if (System.IO.Directory.Exists(path)) App.Groups.AddPin(group, path);
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

        // ---- pins ----
        private void WirePinDragSource(FrameworkElement source, PinnedFolder pin)
        {
            source.PreviewMouseLeftButtonDown += (s, e) => { _dragStart = e.GetPosition(null); _maybeDrag = true; };
            source.PreviewMouseLeftButtonUp += (s, e) => _maybeDrag = false;
            source.MouseMove += (s, e) =>
            {
                if (!_maybeDrag || e.LeftButton != MouseButtonState.Pressed) return;
                var p = e.GetPosition(null);
                if (Math.Abs(p.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
                    Math.Abs(p.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                _maybeDrag = false;
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
                e.Effects = e.Data.GetDataPresent(PinFormat) ? DragDropEffects.Move : DragDropEffects.Copy;
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
                    int at = index;
                    foreach (var path in (string[])e.Data.GetData(DataFormats.FileDrop))
                        if (System.IO.Directory.Exists(path)) { App.Groups.AddPinAtIndex(group.Active, path, at); at++; }
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

        // ---- menus ----
        private ContextMenu BuildGroupMenu(PinnedGroup group, PinnedTab activeTab)
        {
            var menu = new ContextMenu();
            menu.Items.Add(Item("Rename", () => RenameGroupOrTab(group, group.Tabs.Count == 1 ? group.Tabs[0] : activeTab)));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Add tab", () => { var v = TextPrompt.Ask("New tab name", ""); if (!string.IsNullOrWhiteSpace(v)) App.Groups.AddTab(group, v); }));
            menu.Items.Add(Item("Add group below", () => { var v = TextPrompt.Ask("New group name", ""); if (!string.IsNullOrWhiteSpace(v)) App.Groups.CreateGroupAfter(group, v); }));
            menu.Items.Add(Item("Add folder...", () => { var p = PickFolder(); if (!string.IsNullOrEmpty(p)) App.Groups.AddPin(group, p); }));
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

        private ContextMenu BuildPinMenu(PinnedGroup group, PinnedTab tab, PinnedFolder pin)
        {
            var menu = new ContextMenu();
            menu.Items.Add(Item("Open", () => { if (pin.Exists) _navigate(pin.TargetPath); }));
            menu.Items.Add(Item("Open in new window", () => ExplorerNavigator.OpenNewWindow(pin.TargetPath)));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Rename", () => { var v = TextPrompt.Ask("Rename pin", pin.DisplayName); if (v != null) App.Groups.RenamePin(pin, v); }));
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

        // ---- add group affordances ----
        private void AddPlusButton(Grid header, PinnedGroup group)
        {
            var plus = new Button
            {
                Content = "+",
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                Opacity = 0,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = UiHelpers.AppBrush("ChevronColor"),
                Cursor = Cursors.Hand,
                Padding = new Thickness(6, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "Add group below"
            };
            plus.Click += (s, e) => { var v = TextPrompt.Ask("New group name", ""); if (!string.IsNullOrWhiteSpace(v)) App.Groups.CreateGroupAfter(group, v); };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(plus, header.ColumnDefinitions.Count - 1);
            header.Children.Add(plus);
            header.MouseEnter += (s, e) => plus.Opacity = 1;
            header.MouseLeave += (s, e) => plus.Opacity = 0;
        }

        private FrameworkElement BuildAddGroupRow()
        {
            var btn = new Button
            {
                Content = "+  Add group",
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Foreground = UiHelpers.AppBrush("TextSecondary"),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                Padding = new Thickness(8, 6, 8, 6),
                FontSize = 12
            };
            btn.Click += (s, e) => { var v = TextPrompt.Ask("New group name", ""); if (!string.IsNullOrWhiteSpace(v)) App.Groups.CreateGroup(v); };
            return btn;
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

        private static void SafeClipboard(string text)
        {
            try { if (!string.IsNullOrEmpty(text)) Clipboard.SetText(text); }
            catch (Exception ex) { Log.Error("clipboard set", ex); }
        }

        private sealed class PinRef { public string LinkPath { get; set; } }
        private sealed class GroupRef { public string FolderPath { get; set; } }
        private sealed class TabRef { public string GroupFolder { get; set; } public string TabFolder { get; set; } }
    }

    internal static class PinnedFolderExtensions
    {
        public static bool IsBrokenSafe(this PinnedFolder f) { return f == null || !f.Exists; }
    }
}
