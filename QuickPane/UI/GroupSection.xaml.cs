// GroupSection.xaml.cs
// Purpose: builds the Groups section of the pane: group headers, tab strips, tab buttons and pin rows.
// Reason: row building is one responsibility, separate from drag and drop and from menus.
// Defines: GroupSection (owner of the partial set), PinnedFolderExtensions.
// Set: GroupSection.xaml.cs, GroupSection.Drop.cs, GroupSection.Menus.cs.
// Doc: docs/notes/QuickPane/UI/GroupSection.xaml.cs.md
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
    /// dragging a group onto another merges its tabs in, dragging a tab to empty space makes it a
    /// group, and a pin or file dropped on a tab button is placed in that tab. The embedded pane cannot
    /// host an inline text box, and naming uses a top-level prompt.</summary>
    public partial class GroupSection : UserControl
    {
        private Action<string> _navigate;

        public GroupSection()
        {
            InitializeComponent();
            // Empty space in the section is a drop target: a tab dropped here becomes its own group,
            // and files/folders dropped here are pinned to the last group.
            Root.Background = Brushes.Transparent;
            Root.AllowDrop = true;
            Root.DragOver += OnRootDragOver;
            Root.Drop += OnRootDrop;
            Root.DragLeave += (s, e) => FolderTreeNode.ClearDropHighlight();
            Unloaded += (s, e) => Unwire();
        }

        // The section listens for its own data so a groups change repaints just this section, and
        // probe/icon results arriving from the worker repaint it with the corrected state.
        private bool _sectionWired;
        private bool _rebuildQueued;

        private void Wire()
        {
            if (_sectionWired) return;
            _sectionWired = true;
            if (App.Groups != null) App.Groups.GroupsChanged += QueueRebuild;
            PathStatus.Changed += QueueRebuild;
        }

        private void Unwire()
        {
            if (!_sectionWired) return;
            _sectionWired = false;
            if (App.Groups != null) App.Groups.GroupsChanged -= QueueRebuild;
            PathStatus.Changed -= QueueRebuild;
        }

        /// <summary>Release this section's subscriptions. Called by the pane, because a pane hosted in
        /// an HwndSource never raises Unloaded.</summary>
        public void Detach() { Unwire(); }

        private void QueueRebuild()
        {
            if (_rebuildQueued) return;
            _rebuildQueued = true;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _rebuildQueued = false;
                if (_navigate != null) Build(_navigate);
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        public void Build(Action<string> navigate)
        {
            _navigate = navigate;
            Wire();
            Root.Children.Clear();
            _adorners.Clear();
            _tabOfButton.Clear();
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

            // active tab pins (folders and files alike; files open, folders navigate)
            var active = group.Active;
            if (active != null)
            {
                var capturedTab = active;
                var pinCtx = new PinDropContext
                {
                    GroupName = GroupStore.DisplayName(group),
                    PinHere = paths => PinPathsToTab(capturedTab, paths)
                };
                foreach (var pin in active.Items)
                {
                    var captured = pin;
                    var node = new FolderTreeNode(pin.DisplayName, pin.TargetPath, pin.Exists, _navigate, 0,
                        captured.IsFile, null, pinCtx);
                    node.Row.ContextMenu = BuildPinMenu(group, active, captured);
                    WirePinDragSource(node.Row, captured);
                    items.Children.Add(node);
                }
            }
            EnableItemsDrop(items, group);

            if (!group.Expanded) { items.Visibility = Visibility.Collapsed; items.MaxHeight = 0; }
            else chevron.Angle = 90;

            // Header and rows go in together, so a group with nothing left after filtering goes away
            // instead of leaving its name over an empty gap.
            var block = new StackPanel { Tag = UiHelpers.FilterBlockTag };
            block.Children.Add(header);
            block.Children.Add(items);
            Root.Children.Add(block);
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
            scroller.DragLeave += (s, e) => OnTabBarDragLeave(scroller, e);
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
            _tabOfButton[btn] = tab;
            btn.ContextMenu = BuildTabMenu(group, tab);

            btn.PreviewMouseLeftButtonDown += (s, e) =>
            {
                if (e.ClickCount == 2) { e.Handled = true; RenameTab(tab); return; }
                BeginMaybeDrag(btn, e);
            };
            btn.PreviewMouseLeftButtonUp += (s, e) =>
            {
                // Only the tab the press landed on counts as a click, so releasing over a tab after
                // pressing somewhere else does not switch to it.
                if (_maybeDrag && ReferenceEquals(_pressedSource, btn))
                {
                    App.Groups.SetActiveTab(group, index);
                    group.Expanded = true; // clicking a tab while collapsed opens the group on that tab
                    UiState.SetExpanded("group:" + group.FolderPath, true);
                    Rebuild();
                }
                EndMaybeDrag();
            };
            btn.MouseMove += (s, e) =>
            {
                if (!DragStarted(btn, e)) return;
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
    }

    internal static class PinnedFolderExtensions
    {
        public static bool IsBrokenSafe(this PinnedFolder f) { return f == null || !f.Exists; }
    }
}
