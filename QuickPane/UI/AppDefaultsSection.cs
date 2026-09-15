using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using QuickPane.Services;

namespace QuickPane.UI
{
    /// <summary>
    /// The folder you chose for the app whose dialog this pane is sitting in, offered before anything
    /// else so the place that app always works in is the first thing in reach. Set it from any pane
    /// folder's context menu. The pane only builds this section inside a dialog, because an Explorer
    /// window is nobody's app.
    /// </summary>
    internal sealed class AppDefaultsSection : UserControl
    {
        private readonly StackPanel _root = new StackPanel();
        private Action<string> _navigate;
        private string _app;
        private bool _expanded = true;
        private bool _wired;

        public AppDefaultsSection()
        {
            Focusable = false;
            Content = _root;
            Unloaded += (s, e) => Detach();
        }

        private void Wire()
        {
            if (_wired) return;
            _wired = true;
            if (App.AppDefaults != null) App.AppDefaults.Changed += OnChanged;
        }

        /// <summary>Release subscriptions. Called by the pane, because a pane hosted in an HwndSource
        /// never raises Unloaded.</summary>
        public void Detach()
        {
            if (!_wired) return;
            _wired = false;
            if (App.AppDefaults != null) App.AppDefaults.Changed -= OnChanged;
        }

        private void OnChanged()
        {
            if (_navigate == null) return;
            Dispatcher.BeginInvoke(new Action(() => Build(_navigate, _app)),
                System.Windows.Threading.DispatcherPriority.Background);
        }

        public void Build(Action<string> navigate, string app)
        {
            _navigate = navigate;
            _app = app;
            Wire();
            _root.Children.Clear();
            if (App.AppDefaults == null || string.IsNullOrEmpty(app)) return;

            var items = new StackPanel();
            RotateTransform chevron = null;
            TextBlock label;
            _expanded = UiState.GetExpanded("section:appdefaults");
            var header = UiHelpers.BuildHeader(
                RecentsSection.SectionTitle("appdefaults", "App Folders"),
                () =>
                {
                    _expanded = !_expanded;
                    UiState.SetExpanded("section:appdefaults", _expanded);
                    UiHelpers.ToggleExpand(items, chevron, _expanded);
                },
                () => RecentsSection.RenameSectionPrompt("appdefaults", "App Folders"),
                out chevron, out label);
            header.ContextMenu = RecentsSection.SectionMenu("appdefaults", "App Folders");

            var folder = App.AppDefaults.For(app);
            if (!string.IsNullOrEmpty(folder)) items.Children.Add(Row(app, folder, true));
            else items.Children.Add(Hint("Right-click a folder and choose \"Always start " + app + " here\""));

            if (_expanded) chevron.Angle = 90;
            else { items.Visibility = Visibility.Collapsed; items.MaxHeight = 0; }

            _root.Children.Add(header);
            _root.Children.Add(items);
        }

        private FolderItem Row(string app, string folder, bool emphasize)
        {
            var fi = new FolderItem();
            fi.Bind(Leaf(folder), folder, true);
            fi.ToolTip = folder;
            if (emphasize) fi.FontWeight = FontWeights.SemiBold;
            var captured = folder;
            fi.Clicked += () => { if (_navigate != null) _navigate(captured); };

            var menu = new ContextMenu();
            var clear = new MenuItem { Header = "Forget this folder for " + app };
            clear.Click += (s, e) => App.AppDefaults.Remove(app);
            menu.Items.Add(clear);
            fi.ContextMenu = menu;
            return fi;
        }

        private static TextBlock Hint(string text)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = 11,
                Opacity = 0.6,
                Margin = new Thickness(14, 2, 8, 4),
                TextWrapping = TextWrapping.Wrap,
                Foreground = UiHelpers.AppBrush("TextSecondary")
            };
        }

        private static string Leaf(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            var t = path.TrimEnd('\\');
            int i = t.LastIndexOf('\\');
            return i >= 0 && i < t.Length - 1 ? t.Substring(i + 1) : t;
        }
    }
}
