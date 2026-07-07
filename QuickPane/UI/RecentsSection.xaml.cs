using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using QuickPane.Interop;
using QuickPane.Models;
using QuickPane.Services;

namespace QuickPane.UI
{
    /// <summary>The Recent section: recently used folders, live from the Windows Recent folder.</summary>
    public partial class RecentsSection : UserControl
    {
        private Action<string> _navigate;
        private bool _expanded = true;
        private bool _wired;
        private bool _rebuildQueued;

        public RecentsSection()
        {
            InitializeComponent();
            Unloaded += (s, e) => Unwire();
        }

        // The section listens for its own data (recents list, probe/icon results) so a navigation
        // repaints just this section instead of every section of every sidebar.
        private void Wire()
        {
            if (_wired) return;
            _wired = true;
            if (App.Recents != null) App.Recents.RecentsChanged += QueueRebuild;
            PathStatus.Changed += QueueRebuild;
        }

        private void Unwire()
        {
            if (!_wired) return;
            _wired = false;
            if (App.Recents != null) App.Recents.RecentsChanged -= QueueRebuild;
            PathStatus.Changed -= QueueRebuild;
        }

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
            if (App.Recents == null) return;

            var holder = new RotateTransformHolder();
            var items = new StackPanel();

            _expanded = UiState.GetExpanded("section:recents");
            var header = UiHelpers.BuildHeader(SectionTitle("recents", "Recent"), () =>
            {
                _expanded = !_expanded;
                UiState.SetExpanded("section:recents", _expanded);
                UiHelpers.ToggleExpand(items, holder.Rotate, _expanded);
            }, () => RenameSectionPrompt("recents", "Recent"), out holder.Rotate, out _);
            header.ContextMenu = SectionMenu("recents", "Recent");

            foreach (var rec in App.Recents.Items)
            {
                var fi = new FolderItem();
                fi.Bind(rec.DisplayName, rec.TargetPath, rec.Exists, rec.IsFile);
                var captured = rec;
                fi.Clicked += () => OpenRecent(captured);
                fi.ContextMenu = BuildRecentMenu(captured);
                items.Children.Add(fi);
            }

            if (_expanded) holder.Rotate.Angle = 90;
            else { items.Visibility = Visibility.Collapsed; items.MaxHeight = 0; }

            Root.Children.Add(header);
            Root.Children.Add(items);
        }

        // Clicking a recent folder navigates there; clicking a recent file opens it.
        private void OpenRecent(PinnedFolder rec)
        {
            try
            {
                if (rec.IsFile) Process.Start(new ProcessStartInfo(rec.TargetPath) { UseShellExecute = true });
                else _navigate(rec.TargetPath);
            }
            catch (Exception ex) { Log.Error("open recent", ex); }
        }

        private static string FolderOf(PinnedFolder rec)
        {
            try { return rec.IsFile ? Path.GetDirectoryName(rec.TargetPath) : rec.TargetPath; }
            catch { return rec.TargetPath; }
        }

        private ContextMenu BuildRecentMenu(PinnedFolder rec)
        {
            var menu = new ContextMenu();
            if (rec.IsFile)
            {
                menu.Items.Add(Item("Open file", () => OpenRecent(rec)));
                menu.Items.Add(Item("Open containing folder", () => _navigate(FolderOf(rec))));
            }
            else
            {
                menu.Items.Add(Item("Open", () => _navigate(rec.TargetPath)));
                menu.Items.Add(Item("Open in new window", () => ExplorerNavigator.OpenNewWindow(rec.TargetPath)));
            }

            // Files pin as file shortcuts now, so a recent file pins itself, not just its folder.
            string pinTarget = rec.TargetPath;
            var pinTo = new MenuItem { Header = rec.IsFile ? "Pin file to group" : "Pin folder to group" };
            if (App.Groups != null && !string.IsNullOrEmpty(pinTarget))
            {
                foreach (var g in App.Groups.Groups)
                {
                    var dest = g;
                    pinTo.Items.Add(Item(g.Name, () => App.Groups.AddPin(dest, pinTarget)));
                }
                if (pinTo.Items.Count > 0) pinTo.Items.Add(new Separator());
                pinTo.Items.Add(Item("New group...", () =>
                {
                    var name = TextPrompt.Ask("New group name", "");
                    if (string.IsNullOrWhiteSpace(name)) return;
                    var g = App.Groups.CreateGroup(name);
                    if (g != null) App.Groups.AddPin(g, pinTarget);
                }));
            }
            menu.Items.Add(pinTo);

            menu.Items.Add(Item("Remove from recents", () => App.Recents.RemoveFromRecents(rec)));
            return menu;
        }

        internal static void RenameSectionPrompt(string type, string fallback)
        {
            try
            {
                var sec = App.Settings?.Current.Sections.FirstOrDefault(x => x.Type == type);
                if (sec == null) return;
                var v = TextPrompt.Ask("Rename section", string.IsNullOrWhiteSpace(sec.Title) ? fallback : sec.Title);
                if (v == null) return;
                sec.Title = v;
                App.Settings.Save();
            }
            catch (Exception ex) { Log.Error("rename section", ex); }
        }

        // ---- section header rename ----
        internal static string SectionTitle(string type, string fallback)
        {
            var sec = App.Settings?.Current.Sections.FirstOrDefault(s => s.Type == type);
            return sec != null && !string.IsNullOrWhiteSpace(sec.Title) ? sec.Title : fallback;
        }

        internal static ContextMenu SectionMenu(string type, string fallback)
        {
            var menu = new ContextMenu();
            var mi = new MenuItem { Header = "Rename section" };
            mi.Click += (s, e) => RenameSectionPrompt(type, fallback);
            menu.Items.Add(mi);
            var reset = new MenuItem { Header = "Reset name" };
            reset.Click += (s, e) =>
            {
                var sec = App.Settings?.Current.Sections.FirstOrDefault(x => x.Type == type);
                if (sec == null) return;
                sec.Title = null;
                App.Settings.Save();
            };
            menu.Items.Add(reset);
            return menu;
        }

        private static MenuItem Item(string header, Action action)
        {
            var mi = new MenuItem { Header = header };
            mi.Click += (s, e) => { try { action(); } catch (Exception ex) { Log.Error("recent menu action", ex); } };
            return mi;
        }

        private sealed class RotateTransformHolder
        {
            public System.Windows.Media.RotateTransform Rotate;
        }
    }
}
