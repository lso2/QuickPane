using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using QuickPane.Explorer;
using QuickPane.Interop;
using QuickPane.Services;
using NM = QuickPane.Interop.NativeMethods;

namespace QuickPane.UI
{
    /// <summary>The "This PC" section: fixed, removable, and network drives with a used/free bar.</summary>
    public partial class ComputerSection : UserControl
    {
        private Action<string> _navigate;
        private bool _expanded = true;
        private StackPanel _items;
        private RotateTransform _chevron;
        private bool _subscribed;
        private FolderTreeNode _pcNode;

        /// <summary>The shell name for This PC. Navigate2 rejects the raw "::{CLSID}" form.</summary>
        private const string ThisPcPath = "shell:MyComputerFolder";

        public ComputerSection()
        {
            InitializeComponent();
            Unloaded += (s, e) => Unsubscribe();
        }

        public void Build(Action<string> navigate)
        {
            _navigate = navigate;
            Root.Children.Clear();

            _expanded = UiState.GetExpanded("section:computer");
            _items = new StackPanel();
            var header = UiHelpers.BuildHeader(RecentsSection.SectionTitle("computer", "This PC"), () =>
            {
                _expanded = !_expanded;
                UiState.SetExpanded("section:computer", _expanded);
                UiHelpers.ToggleExpand(_items, _chevron, _expanded);
            }, () => RecentsSection.RenameSectionPrompt("computer", "This PC"), out _chevron, out _);
            header.ContextMenu = RecentsSection.SectionMenu("computer", "This PC");

            PopulateDrives();

            if (_expanded) _chevron.Angle = 90;
            else { _items.Visibility = Visibility.Collapsed; _items.MaxHeight = 0; }

            Root.Children.Add(header);
            Root.Children.Add(_items);

            Subscribe();
        }

        private void Subscribe()
        {
            if (_subscribed) return;
            _subscribed = true;
            ExplorerWatcher.DrivesChanged += OnDrivesChanged;
            if (App.Drives != null) App.Drives.Refreshed += OnDrivesRefreshed;
        }

        private void Unsubscribe()
        {
            if (!_subscribed) return;
            _subscribed = false;
            ExplorerWatcher.DrivesChanged -= OnDrivesChanged;
            if (App.Drives != null) App.Drives.Refreshed -= OnDrivesRefreshed;
        }

        /// <summary>Release this section's subscriptions. Called by the pane, because a pane hosted in
        /// an HwndSource never raises Unloaded.</summary>
        public void Detach() { Unsubscribe(); }

        // Device arrival/removal: requery on the worker; the Refreshed event repaints when done.
        private void OnDrivesChanged()
        {
            App.Drives?.RefreshAsync();
        }

        // Raised on the UI thread after the background snapshot replaced the cache. Reloading in place
        // keeps This PC open across a device arrival instead of collapsing the tree the user is in.
        private void OnDrivesRefreshed()
        {
            if (_items == null) return;
            if (_pcNode != null) _pcNode.ReloadChildren();
            else PopulateDrives();
        }

        // One This PC node holds the drives, so the whole computer is a single row that opens This PC
        // on click and expands to its drives, matching how Network and Linux already read.
        private void PopulateDrives()
        {
            _items.Children.Clear();
            var title = RecentsSection.SectionTitle("computer", "This PC");
            var icon = IconHelper.GetSpecialFolderIcon(NM.CSIDL_DRIVES);
            _pcNode = new FolderTreeNode(title, ThisPcPath, true, _navigate, 0, false, icon, null, BuildDriveNodes);
            _pcNode.Row.ContextMenu = BuildComputerMenu(title);
            _pcNode.Row.ToolTip = title;
            _items.Children.Add(_pcNode);
        }

        // Supplied to the This PC node, which calls it on first expand and on every drive change.
        private List<FrameworkElement> BuildDriveNodes(int level)
        {
            var list = new List<FrameworkElement>();
            if (App.Drives == null) return list;
            foreach (var d in App.Drives.GetDrives())
                list.Add(BuildDriveNode(d, level));
            return list;
        }

        private ContextMenu BuildComputerMenu(string title)
        {
            var menu = new ContextMenu();
            menu.Items.Add(Item("Open", () => _navigate(ThisPcPath)));
            menu.Items.Add(Item("Open in new window", () => ExplorerNavigator.OpenNewWindow(ThisPcPath)));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Rename", () => RecentsSection.RenameSectionPrompt("computer", "This PC")));
            return menu;
        }

        // A drive as an expandable tree node, like a pinned folder, with its free/total in the tooltip.
        private FrameworkElement BuildDriveNode(DriveItem d, int level)
        {
            var label = d.Label + "  (" + d.Letter + ")";
            var icon = IconHelper.GetFolderIcon(d.RootPath, d.Ready);
            var node = new FolderTreeNode(label, d.RootPath, d.Ready, _navigate, level, false, icon);
            node.Row.ContextMenu = BuildDriveMenu(d);
            if (d.Ready && d.TotalBytes > 0)
            {
                const long gb = 1024L * 1024L * 1024L;
                node.Row.ToolTip = label + Environment.NewLine + (d.FreeBytes / gb) + " GB free of " + (d.TotalBytes / gb) + " GB";
            }
            return node;
        }

        private ContextMenu BuildDriveMenu(DriveItem d)
        {
            var menu = new ContextMenu();
            menu.Items.Add(Item("Open", () => _navigate(d.RootPath)));
            menu.Items.Add(Item("Open in new window", () => ExplorerNavigator.OpenNewWindow(d.RootPath)));
            menu.Items.Add(Item("Copy path", () =>
            {
                try { Clipboard.SetText(d.RootPath); } catch (Exception ex) { Log.Error("clipboard", ex); }
            }));
            return menu;
        }

        private static MenuItem Item(string header, Action action)
        {
            var mi = new MenuItem { Header = header };
            mi.Click += (s, e) => { try { action(); } catch (Exception ex) { Log.Error("drive menu action", ex); } };
            return mi;
        }
    }
}
