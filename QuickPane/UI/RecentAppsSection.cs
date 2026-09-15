using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using QuickPane.Services;

namespace QuickPane.UI
{
    /// <summary>
    /// The applications you have been using. A row opens its app; the folder that app's dialog was last
    /// sent to is on the row's right-click menu, because reopening the program and going back to where it
    /// was working are two different intentions and only one of them is a click away in Windows already.
    /// </summary>
    internal sealed class RecentAppsSection : UserControl
    {
        private readonly StackPanel _root = new StackPanel();
        private Action<string> _navigate;
        private bool _expanded = true;
        private bool _wired;
        private bool _pendingRebuild;

        public RecentAppsSection()
        {
            Focusable = false;
            Content = _root;
            Unloaded += (s, e) => Detach();

            // This list reorders itself every time another program comes to the front. Rebuilding it
            // under the pointer would move a row out from under a click already in progress, so a
            // rebuild that arrives while the pointer is here waits until the pointer leaves.
            MouseLeave += (s, e) =>
            {
                if (!_pendingRebuild || _navigate == null) return;
                _pendingRebuild = false;
                Build(_navigate);
            };
        }

        private void Wire()
        {
            if (_wired) return;
            _wired = true;
            if (App.RecentApps != null) App.RecentApps.Changed += OnChanged;
            AppShortcuts.Loaded += OnIconsLoaded;   // icons improve once the Start menu has been read
            AppShortcuts.BeginLoad();
        }

        /// <summary>Release subscriptions. Called by the pane, because a pane hosted in an HwndSource
        /// never raises Unloaded.</summary>
        public void Detach()
        {
            if (!_wired) return;
            _wired = false;
            if (App.RecentApps != null) App.RecentApps.Changed -= OnChanged;
            AppShortcuts.Loaded -= OnIconsLoaded;
        }

        private void OnIconsLoaded() { OnChanged(false); }

        /// <param name="asked">True when a person acted on the list, which redraws it at once. A change
        /// that came from another program reaching the front waits until the pointer is away, so a row
        /// never moves out from under a click already in progress.</param>
        private void OnChanged(bool asked)
        {
            if (_navigate == null) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!asked && IsMouseOver) { _pendingRebuild = true; return; }
                _pendingRebuild = false;
                Build(_navigate);
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        public void Build(Action<string> navigate)
        {
            _navigate = navigate;
            Wire();
            _root.Children.Clear();
            if (App.RecentApps == null) return;

            var items = new StackPanel();
            RotateTransform chevron = null;
            TextBlock label;
            _expanded = UiState.GetExpanded("section:recentapps");
            var header = UiHelpers.BuildHeader(
                RecentsSection.SectionTitle("recentapps", "Recent Apps"),
                () =>
                {
                    _expanded = !_expanded;
                    UiState.SetExpanded("section:recentapps", _expanded);
                    UiHelpers.ToggleExpand(items, chevron, _expanded);
                },
                () => RecentsSection.RenameSectionPrompt("recentapps", "Recent Apps"),
                out chevron, out label);
            header.ContextMenu = RecentsSection.SectionMenu("recentapps", "Recent Apps");

            var source = App.Settings != null ? App.Settings.Current.RecentAppsSource : "both";
            foreach (var entry in App.RecentApps.ItemsFor(source))
                items.Children.Add(BuildRow(entry));

            if (_expanded) chevron.Angle = 90;
            else { items.Visibility = Visibility.Collapsed; items.MaxHeight = 0; }

            _root.Children.Add(header);
            _root.Children.Add(items);
        }

        private FrameworkElement BuildRow(RecentAppsService.Entry entry)
        {
            var captured = entry;
            var icon = IconHelper.GetAppIcon(entry.ExePath);
            var profiles = BrowserProfiles.For(entry.ExePath);

            // A browser is several signed-in identities behind one program, so its row opens out into
            // them rather than pretending there is only one thing behind the icon. Every row is built the
            // same way whether or not it has any, so they line up down one edge.
            bool hasProfiles = profiles.Count > 1;
            var node = new FolderTreeNode(entry.Name, entry.ExePath, true, _navigate, 0,
                isFile: !hasProfiles, overrideIcon: icon, pinContext: null,
                childProvider: hasProfiles ? (Func<int, List<FrameworkElement>>)
                    (level => ProfileRows(captured, profiles, icon, level)) : null,
                onClick: () => Launch(captured.ExePath, null));
            node.Row.ToolTip = Tip(entry);
            node.Row.ContextMenu = BuildMenu(captured);
            return node;
        }

        private List<FrameworkElement> ProfileRows(RecentAppsService.Entry entry,
            List<BrowserProfiles.Profile> profiles, ImageSource icon, int level)
        {
            var rows = new List<FrameworkElement>();
            foreach (var p in profiles)
            {
                var captured = p;
                var row = new FolderTreeNode(p.Name, entry.ExePath, true, _navigate, level,
                    isFile: true, overrideIcon: icon, pinContext: null, childProvider: null,
                    onClick: () => Launch(entry.ExePath, captured.Arguments));
                row.Row.ToolTip = p.Name + Environment.NewLine + entry.Name;
                rows.Add(row);
            }
            return rows;
        }

        private static string Tip(RecentAppsService.Entry entry)
        {
            return entry.ExePath +
                (string.IsNullOrEmpty(entry.Folder) ? "" : Environment.NewLine + "Last folder: " + entry.Folder);
        }

        private ContextMenu BuildMenu(RecentAppsService.Entry entry)
        {
            var menu = new ContextMenu();
            menu.Items.Add(Item("Open " + entry.Name, () => Launch(entry.ExePath, null)));

            if (!string.IsNullOrEmpty(entry.Folder))
                menu.Items.Add(Item("Open last folder", () => { if (_navigate != null) _navigate(entry.Folder); }));

            menu.Items.Add(Item("Open file location", () =>
            {
                var dir = Path.GetDirectoryName(entry.ExePath);
                if (!string.IsNullOrEmpty(dir) && _navigate != null) _navigate(dir);
            }));

            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Forget " + entry.Name, () => App.RecentApps.Forget(entry.ExePath)));
            menu.Items.Add(Item("Forget all", () => App.RecentApps.Clear()));
            return menu;
        }

        private static void Launch(string exePath, string arguments)
        {
            try
            {
                if (string.IsNullOrEmpty(exePath)) { Log.Info("a recent app row carried no path."); return; }
                if (!File.Exists(exePath))
                {
                    Log.Info("\"" + exePath + "\" is no longer on disk, so the row did not open it.");
                    return;
                }
                Log.Info("opening " + exePath + " " + (arguments ?? "") + "from the recent apps list.");
                Process.Start(new ProcessStartInfo(exePath)
                {
                    Arguments = arguments ?? "",
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(exePath) ?? ""
                });
            }
            catch (Exception ex) { Log.Error("open '" + exePath + "'", ex); }
        }

        private static MenuItem Item(string header, Action action)
        {
            var mi = new MenuItem { Header = header };
            mi.Click += (s, e) => { try { action(); } catch (Exception ex) { Log.Error("recent apps menu", ex); } };
            return mi;
        }
    }
}
