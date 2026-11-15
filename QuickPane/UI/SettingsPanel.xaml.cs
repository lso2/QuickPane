// SettingsPanel.xaml.cs
// Purpose: the Settings window's frame: tabs, wheel routing, footer and the small builders every block uses.
// Reason: the blocks for each area are in SettingsPanel.Blocks, .Reorder, .Ssh and .Profiles.
// Defines: SettingsPanel (partial): constructor, Bind, BuildUI, Tab, BuildFooter, small builders.
// Doc: docs/notes/QuickPane/UI/SettingsPanel.xaml.cs.md
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using WinForms = System.Windows.Forms;
using QuickPane.Models;
using QuickPane.Services;

namespace QuickPane.UI
{
    /// <summary>Settings UI. Every change is written to settings.json immediately (no Apply button).
    /// Sections and groups reorder by dragging their grip handle rather than with up/down buttons.</summary>
    public partial class SettingsPanel : UserControl
    {
        public event EventHandler CloseRequested;

        private SettingsStore _settings;
        private GroupStore _groups;
        private Point _dragStart;
        private string _openTab = "Groups";   // survives a rebuild, so reordering does not throw you back
        private bool _maybeDrag;
        // Every row in a list carries the same pair of move handlers, so without recording which one was
        // pressed the drag belongs to whichever row the pointer happens to be over when it passes the
        // threshold, and the neighbour below the one you grabbed is the row that moves.
        private object _pressedHandle;
        private const string ProfileGroupFormat = "QpProfileGroup";

        /// <summary>Remember the element a press landed on and start watching for a drag.</summary>
        private void BeginMaybeDrag(object handle, MouseButtonEventArgs e)
        {
            _pressedHandle = handle;
            _dragStart = e.GetPosition(null);
            _maybeDrag = true;
        }

        /// <summary>True once the pointer has travelled far enough for the press on this element to be a drag.</summary>
        private bool DragStarted(object handle, MouseEventArgs e, bool horizontalCounts)
        {
            if (!_maybeDrag || !ReferenceEquals(_pressedHandle, handle)) return false;
            if (e.LeftButton != MouseButtonState.Pressed) { _maybeDrag = false; return false; }
            var pt = e.GetPosition(null);
            bool far = Math.Abs(pt.Y - _dragStart.Y) >= SystemParameters.MinimumVerticalDragDistance ||
                       (horizontalCounts && Math.Abs(pt.X - _dragStart.X) >= SystemParameters.MinimumHorizontalDragDistance);
            if (!far) return false;
            _maybeDrag = false;
            return true;
        }

        private void EndMaybeDrag() { _maybeDrag = false; _pressedHandle = null; }

        public SettingsPanel()
        {
            InitializeComponent();
            Focusable = true;
            KeyDown += (s, e) => { if (e.Key == Key.Escape) Raise(); };
        }

        public void Bind(SettingsStore settings, GroupStore groups)
        {
            _settings = settings;
            _groups = groups;
            BuildUI();
        }

        private void BuildUI()
        {
            Host.Children.Clear();
            if (_settings == null) return;
            var s = _settings.Current;

            // Title + close
            var title = new Grid();
            title.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            title.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var t = Heading("Settings");
            t.Margin = new Thickness(0);
            Grid.SetColumn(t, 0);
            title.Children.Add(t);
            var close = new Button
            {
                Content = "", // ChromeClose
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 12,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = UiHelpers.AppBrush("TextSecondary"),
                Cursor = Cursors.Hand,
                Padding = new Thickness(6)
            };
            close.Click += (a, b) => Raise();
            Grid.SetColumn(close, 1);
            title.Children.Add(close);
            Host.Children.Add(title);

            // Grouped into tabs. Everything used to sit in one column, so finding a single option meant
            // scrolling past every other one.
            var tabs = new TabControl
            {
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Margin = new Thickness(0, 4, 0, 0)
            };

            // Groups: the profiles and the pinned folders inside them. First, because it is the tab
            // that gets opened to do actual work rather than to change a setting once.
            var groups = new StackPanel();
            var scroller = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Margin = new Thickness(0, 0, 0, 6)
            };
            // Without this the columns could only be reached by dragging the scrollbar.
            TrackStrip(scroller);
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            for (int i = 0; i < s.Profiles.Count; i++) row.Children.Add(BuildProfileColumn(i));
            row.Children.Add(BuildAddProfileColumn());
            scroller.Content = row;
            groups.Children.Add(scroller);
            tabs.Items.Add(Tab("Groups", groups, false));

            // Pane: how and where the pane appears, and which sections it shows in what order. Two
            // columns rather than three: the short option blocks stack down one side and the long list
            // of sections fills the other, instead of three short columns leaving a hole under them.
            var pane = new Grid();
            pane.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            pane.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var options = new StackPanel { Margin = new Thickness(0, 0, 24, 0) };
            options.Children.Add(PaneModeBlock(s));
            options.Children.Add(DockBlock(s));
            options.Children.Add(ProfileTabsBlock(s));
            options.Children.Add(RecentAppsBlock(s));
            AddCol(pane, 0, options);

            AddCol(pane, 1, SectionsBlock(s));
            tabs.Items.Add(Tab("Pane", pane, true));

            // Hotkeys: keyboard navigation of the pane.
            tabs.Items.Add(Tab("Hotkeys", HotkeysBlock(s), true));

            // Connections: SSH mounts.
            var conn = new StackPanel();
            conn.Children.Add(SshBlock(s));
            tabs.Items.Add(Tab("Connections", conn, true));

            // Backup: settings import and export.
            var backup = new StackPanel();
            backup.Children.Add(BackupBlock());
            tabs.Items.Add(Tab("Backup", backup, true));

            // Restore whichever tab was open. Selecting it before the handler is attached keeps the
            // restore from being mistaken for the person choosing a tab.
            int open = 0;
            for (int i = 0; i < tabs.Items.Count; i++)
            {
                var item = tabs.Items[i] as TabItem;
                if (item != null && string.Equals(item.Header as string, _openTab, StringComparison.Ordinal))
                { open = i; break; }
            }
            tabs.SelectedIndex = open;
            tabs.SelectionChanged += (a, b) =>
            {
                if (!ReferenceEquals(b.OriginalSource, tabs)) return;   // ignore inner selectors
                var item = tabs.SelectedItem as TabItem;
                if (item != null) _openTab = item.Header as string;
            };
            Host.Children.Add(tabs);
            FooterHost.Content = BuildFooter();
        }

        // ---- wheel routing --------------------------------------------
        //
        // The profile columns are a sideways strip inside a page that scrolls up and down, so the two
        // have to be told apart by the direction of the input rather than by where the pointer is:
        //
        //   vertical wheel          -> the settings page scrolls up and down
        //   Shift + vertical wheel  -> the profile columns move sideways
        //   horizontal wheel        -> the profile columns move sideways
        //
        // Sideways movement is never driven by a plain vertical wheel, because the system has no
        // convention for which way that should go and either choice reads as backwards.

        private const double WheelStep = 64;
        private const int WM_MOUSEHWHEEL = 0x020E;

        private ScrollViewer _strip;
        private HwndSource _hwheelSrc;

        /// <summary>Register the sideways strip and start reading horizontal wheel input for it.</summary>
        private void TrackStrip(ScrollViewer sv)
        {
            _strip = sv;
            sv.PreviewMouseWheel += (s, e) =>
            {
                if (e.Delta == 0) return;
                if ((Keyboard.Modifiers & ModifierKeys.Shift) == 0) return;  // plain wheel scrolls the page
                if (ScrollStripBy(_strip, -(e.Delta / 120.0) * WheelStep)) e.Handled = true;
            };
            HookHWheel();
        }

        private void HookHWheel()
        {
            var src = PresentationSource.FromVisual(this) as HwndSource;
            if (src == null || ReferenceEquals(src, _hwheelSrc)) return;
            if (_hwheelSrc != null) { try { _hwheelSrc.RemoveHook(HWheelProc); } catch { } }
            _hwheelSrc = src;
            src.AddHook(HWheelProc);
        }

        private IntPtr HWheelProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg != WM_MOUSEHWHEEL) return IntPtr.Zero;
            int delta = (short)((wParam.ToInt64() >> 16) & 0xFFFF);   // positive is rightward
            if (ScrollStripBy(_strip, (delta / 120.0) * WheelStep)) handled = true;
            return IntPtr.Zero;
        }

        /// <summary>Move the strip by a pixel amount. False when it has nowhere left to go that way.</summary>
        private static bool ScrollStripBy(ScrollViewer sv, double pixels)
        {
            if (sv == null || sv.ScrollableWidth <= 0) return false;
            double target = sv.HorizontalOffset + pixels;
            if (target < 0) target = 0;
            if (target > sv.ScrollableWidth) target = sv.ScrollableWidth;
            if (Math.Abs(target - sv.HorizontalOffset) < 0.5) return false;
            sv.ScrollToHorizontalOffset(target);
            return true;
        }

        /// <summary>One settings tab, themed to match the pane rather than the system default. The
        /// option tabs hold a handful of controls each and read better with room around them; Groups is
        /// a dense working surface and keeps its tighter spacing.</summary>
        private TabItem Tab(string header, UIElement content, bool roomy)
        {
            return new TabItem
            {
                Header = header,
                Content = new ScrollViewer
                {
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    Padding = roomy ? new Thickness(6, 14, 6, 14) : new Thickness(0, 8, 0, 0),
                    Content = content
                }
            };
        }

        // ---- footer -------------------------------------------------

        /// <summary>
        /// The bar under every tab: what this is and which build it is on the left, support on the
        /// right. The version is read from the assembly, so it can never disagree with the binary, and
        /// GitHub is asked in the background whether a newer release exists.
        /// </summary>
        private FrameworkElement BuildFooter()
        {
            var bar = new Grid { Margin = new Thickness(10, 0, 10, 10) };
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var rule = new Border
            {
                Height = 1,
                Background = UiHelpers.AppBrush("SeparatorColor"),
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 0, 0, 10)
            };
            Grid.SetColumnSpan(rule, 2);
            bar.Children.Add(rule);

            var left = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 12, 0, 0)
            };
            left.Children.Add(new TextBlock
            {
                Text = "QuickPane",
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = UiHelpers.AppBrush("TextPrimary")
            });
            left.Children.Add(new TextBlock
            {
                Text = UpdateService.CurrentDisplay,
                FontSize = 13,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = UiHelpers.AppBrush("TextSecondary")
            });

            var status = new TextBlock
            {
                Text = "Checking for updates...",
                FontSize = 12,
                Margin = new Thickness(12, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = UiHelpers.AppBrush("TextSecondary")
            };
            left.Children.Add(status);

            var download = new Button
            {
                Visibility = Visibility.Collapsed,
                Cursor = Cursors.Hand,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
                Background = UiHelpers.AppBrush("AccentBrush"),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(14, 6, 14, 6),
                Margin = new Thickness(12, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            left.Children.Add(download);

            Grid.SetColumn(left, 0);
            bar.Children.Add(left);

            var support = BuildSupportButton();
            support.Margin = new Thickness(12, 12, 0, 0);
            support.HorizontalAlignment = HorizontalAlignment.Right;
            support.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(support, 1);
            bar.Children.Add(support);

            UpdateService.CheckAsync(r =>
            {
                if (r == null) return;
                if (!string.IsNullOrEmpty(r.Error)) { status.Text = "Update check failed"; status.ToolTip = r.Error; return; }
                if (!r.Available) { status.Text = "Up to date"; return; }

                status.Text = "Version " + r.Latest.ToString(3) + " is available";
                download.Content = "Download " + r.Latest.ToString(3);
                download.ToolTip = r.DownloadUrl;
                download.Visibility = Visibility.Visible;
                var url = r.DownloadUrl;
                download.Click += (a, b) => OpenUrl(url);
            });

            return bar;
        }

        private Button BuildSupportButton()
        {
            var btn = new Button
            {
                Cursor = Cursors.Hand,
                Background = Brushes.Transparent,
                BorderBrush = UiHelpers.AppBrush("SeparatorColor"),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(12, 6, 12, 6),
                Margin = new Thickness(0, 18, 0, 4)
            };
            var sp = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            sp.Children.Add(new TextBlock
            {
                Text = "☕",
                Foreground = UiHelpers.AppBrush("AccentBrush"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            });
            sp.Children.Add(new TextBlock
            {
                Text = "Buy me a coffee",
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = UiHelpers.AppBrush("TextPrimary")
            });
            btn.Content = sp;
            btn.Click += (a, b) => SidebarControl.OpenSupportLink();
            return btn;
        }

        // ---- small builders ----
        private FrameworkElement ModeRadio(string label, bool isChecked, Action onSelect)
        {
            var rb = new RadioButton
            {
                Content = label,
                IsChecked = isChecked,
                GroupName = "qpPaneMode",
                Foreground = UiHelpers.AppBrush("TextPrimary"),
                FontSize = 13,
                Margin = new Thickness(0, 6, 0, 0)
            };
            rb.Checked += (a, b) => onSelect();
            return rb;
        }

        private FrameworkElement ModeCheck(string label, bool isChecked, Action<bool> onChange)
        {
            var cb = new CheckBox
            {
                Content = label,
                IsChecked = isChecked,
                Foreground = UiHelpers.AppBrush("TextPrimary"),
                FontSize = 13,
                Margin = new Thickness(0, 6, 0, 0)
            };
            cb.Checked += (a, b) => onChange(true);
            cb.Unchecked += (a, b) => onChange(false);
            return cb;
        }

        private TextBlock Grip()
        {
            return new TextBlock
            {
                Text = "☰", // trigram, drag handle
                FontSize = 12,
                Foreground = UiHelpers.AppBrush("TextSecondary"),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "Drag to reorder"
            };
        }

        private TextBlock Heading(string text)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Foreground = UiHelpers.AppBrush("TextPrimary"),
                Margin = new Thickness(0, 0, 0, 8)
            };
        }

        /// <summary>The heading that opens a column, with no gap above it.</summary>
        private TextBlock FirstSubHeading(string text)
        {
            var t = SubHeading(text);
            t.Margin = new Thickness(0, 0, 0, 8);
            return t;
        }

        private TextBlock SubHeading(string text)
        {
            return new TextBlock
            {
                Text = text.ToUpperInvariant(),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = UiHelpers.AppBrush("TextSecondary"),
                Margin = new Thickness(0, 16, 0, 8)
            };
        }

        private Button TextButton(string content)
        {
            return new Button
            {
                Content = content,
                FontSize = 12,
                Padding = new Thickness(8, 3, 8, 3),
                Margin = new Thickness(2, 0, 0, 0),
                Background = Brushes.Transparent,
                BorderBrush = UiHelpers.AppBrush("SeparatorColor"),
                BorderThickness = new Thickness(1),
                Foreground = UiHelpers.AppBrush("TextPrimary"),
                Cursor = Cursors.Hand
            };
        }

        private static string Friendly(string type)
        {
            switch (type)
            {
                case "groups": return "Groups";
                case "recents": return "Recent";
                case "computer": return "This PC";
                case "network": return "Network";
                case "linux": return "Linux";
                case "ssh": return "SSH";
                case "search": return "Search box";
                case "appdefaults": return "App Folders";
                case "recentapps": return "Recent Apps";
                default: return type;
            }
        }

        private void Raise()
        {
            var h = CloseRequested;
            if (h != null) h(this, EventArgs.Empty);
        }
    }
}
