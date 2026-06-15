using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using QuickPane.Explorer;
using QuickPane.Interop;
using QuickPane.Services;

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
        }

        private void Unsubscribe()
        {
            if (!_subscribed) return;
            _subscribed = false;
            ExplorerWatcher.DrivesChanged -= OnDrivesChanged;
        }

        private void OnDrivesChanged()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_items == null) return;
                PopulateDrives();
            }));
        }

        private void PopulateDrives()
        {
            _items.Children.Clear();
            if (App.Drives == null) return;
            foreach (var d in App.Drives.GetDrives())
                _items.Children.Add(BuildDriveNode(d));
        }

        // A drive as an expandable tree node, like a pinned folder, with its free/total in the tooltip.
        private FrameworkElement BuildDriveNode(DriveItem d)
        {
            var label = d.Label + "  (" + d.Letter + ")";
            var icon = IconHelper.GetFolderIcon(d.RootPath, d.Ready);
            var node = new FolderTreeNode(label, d.RootPath, d.Ready, _navigate, 0, false, icon);
            node.Row.ContextMenu = BuildDriveMenu(d);
            if (d.Ready && d.TotalBytes > 0)
            {
                const long gb = 1024L * 1024L * 1024L;
                node.Row.ToolTip = label + "  -  " + (d.FreeBytes / gb) + " GB free of " + (d.TotalBytes / gb) + " GB";
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
