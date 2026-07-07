using System;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using QuickPane.Models;
using QuickPane.Services;

namespace QuickPane.UI
{
    /// <summary>
    /// Root of the embedded sidebar. Builds the three sections in the order from settings, hosts the
    /// settings slide-in, and rebuilds when groups, recents, theme, or settings change. One instance
    /// lives per Explorer window.
    /// </summary>
    public partial class SidebarControl : UserControl
    {
        private Action<string> _navigate;
        private bool _wired;

        /// <summary>Set while a drag or a context menu is active anywhere in a pane, so the desktop
        /// dock does not auto-collapse out from under the user mid-interaction.</summary>
        public static bool SuppressAutoHide;

        public SidebarControl()
        {
            InitializeComponent();
            // Guaranteed-opaque base layer, so the sidebar is visible even if a themed brush
            // ever fails to resolve. The themed Border paints on top of this when it resolves.
            bool dark = App.Theme != null && App.Theme.IsDark;
            Background = new SolidColorBrush(dark
                ? Color.FromRgb(0x20, 0x20, 0x20)
                : Color.FromRgb(0xF3, 0xF3, 0xF3));

            AddHandler(ContextMenuOpeningEvent, new ContextMenuEventHandler((s, e) => SuppressAutoHide = true), true);
            AddHandler(ContextMenuClosingEvent, new ContextMenuEventHandler((s, e) => SuppressAutoHide = false), true);

            Loaded += (s, e) => { Wire(); HookHWheel(); };
            Unloaded += (s, e) => Unwire();
        }

        public void Attach(Action<string> navigate)
        {
            _navigate = navigate;
            Wire();
            BuildSections();
        }

        // Groups/recents/drive changes are handled inside their own sections, so a navigation that
        // touches Recent no longer rebuilds every section of every open sidebar. Only theme and
        // settings changes rebuild the whole pane, and those rebuilds are coalesced: a save fires
        // once per control but the pane is rebuilt once per pump.
        private bool _rebuildQueued;

        private void Wire()
        {
            if (_wired) return;
            _wired = true;
            if (App.Theme != null) App.Theme.ThemeChanged += OnDataChanged;
            if (App.Settings != null) App.Settings.Changed += OnSettingsChanged;
        }

        private void Unwire()
        {
            if (!_wired) return;
            _wired = false;
            if (App.Theme != null) App.Theme.ThemeChanged -= OnDataChanged;
            if (App.Settings != null) App.Settings.Changed -= OnSettingsChanged;
        }

        private void OnDataChanged()
        {
            QueueBuild();
        }

        private void OnSettingsChanged(object sender, EventArgs e)
        {
            QueueBuild();
        }

        private void QueueBuild()
        {
            if (_rebuildQueued) return;
            _rebuildQueued = true;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _rebuildQueued = false;
                BuildSections();
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        private void BuildSections()
        {
            if (App.Settings == null) return;
            SectionsPanel.Children.Clear();

            var ordered = App.Settings.Current.Sections
                .Where(s => s.Visible)
                .OrderBy(s => s.Order)
                .ToList();

            bool first = true;
            foreach (var s in ordered)
            {
                FrameworkElement section = BuildSection(s.Type);
                if (section == null) continue; // e.g. Linux when WSL is not installed

                if (!first) SectionsPanel.Children.Add(UiHelpers.MakeSeparator());
                first = false;
                SectionsPanel.Children.Add(section);
            }

            RefreshProfileTabs();
            WireTabHover();
            ApplyProfileTabsState();
        }

        // ---- profile tabs ----------------------------------------------------
        private bool _tabsHover;
        private bool _hoverWired;
        private bool _paneCollapsed;

        private void WireTabHover()
        {
            if (_hoverWired || HeaderPanel == null) return;
            _hoverWired = true;
            HeaderPanel.MouseEnter += (s, e) => { _tabsHover = true; ApplyProfileTabsState(); };
            HeaderPanel.MouseLeave += (s, e) => { _tabsHover = false; ApplyProfileTabsState(); };
        }

        private void RefreshProfileTabs()
        {
            if (ProfileTabsBar == null || App.Settings == null) return;
            ProfileTabsBar.Children.Clear();
            var st = App.Settings.Current;
            var profiles = st.Profiles;
            if (profiles == null) return;

            for (int i = 0; i < profiles.Count; i++)
            {
                int idx = i;
                bool active = i == st.ActiveProfileIndex;
                var tab = new Border
                {
                    Padding = new Thickness(8, 2, 8, 3),
                    Margin = new Thickness(0, 0, 4, 0),
                    CornerRadius = new CornerRadius(3),
                    Cursor = System.Windows.Input.Cursors.Hand,
                    Background = active ? UiHelpers.AppBrush("ItemHoverBackground") : Brushes.Transparent,
                    Child = new TextBlock
                    {
                        Text = profiles[i].Name,
                        FontSize = 12,
                        FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal,
                        Foreground = active ? UiHelpers.AppBrush("TextPrimary") : UiHelpers.AppBrush("TextSecondary")
                    }
                };
                tab.MouseLeftButtonUp += (s, e) => { App.Settings.SwitchProfile(idx); };
                ProfileTabsBar.Children.Add(tab);
            }
        }

        private void ApplyProfileTabsState()
        {
            if (ProfileTabsHost == null) return;
            if (App.Settings == null) { ProfileTabsHost.Visibility = Visibility.Collapsed; return; }
            var st = App.Settings.Current;
            bool show = st.ShowProfileTabs && !_paneCollapsed && (st.Profiles != null && st.Profiles.Count > 0);
            if (!show) { ProfileTabsHost.Visibility = Visibility.Collapsed; return; }
            // Auto-hide collapses the row until the cursor is over the header.
            ProfileTabsHost.Visibility = (st.ProfileTabsAutoHide && !_tabsHover)
                ? Visibility.Collapsed : Visibility.Visible;
        }

        private FrameworkElement BuildSection(string type)
        {
            switch (type)
            {
                case "groups":
                    var g = new GroupSection(); g.Build(_navigate); return g;
                case "recents":
                    var r = new RecentsSection(); r.Build(_navigate); return r;
                case "computer":
                    var c = new ComputerSection(); c.Build(_navigate); return c;
                case "network":
                    var n = new ShellRootSection("network", "Network", "::{F02C1A0D-BE21-4350-88B0-7367FC96EF3C}", false);
                    n.Build(_navigate); return n;
                case "linux":
                    if (!ShellRootSection.WslPresent()) return null;
                    var lx = new ShellRootSection("linux", "Linux", "\\\\wsl$", true);
                    lx.Build(_navigate); return lx;
                default:
                    return null;
            }
        }

        // ---- settings slide-in ----------------------------------------------

        private void OnGearClick(object sender, RoutedEventArgs e)
        {
            var panel = new SettingsPanel();
            panel.Compact = true; // narrow embedded pane: stack everything vertically
            panel.Bind(App.Settings, App.Groups);
            panel.CloseRequested += (s2, e2) => HideSettings();
            SettingsHost.Child = panel;

            double w = ActualWidth > 0 ? ActualWidth : 220;
            SettingsSlide.X = w;
            SettingsHost.Visibility = Visibility.Visible;

            var anim = new DoubleAnimation(w, 0, TimeSpan.FromMilliseconds(200))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            SettingsSlide.BeginAnimation(TranslateTransform.XProperty, anim);
        }

        // Slow the wheel to roughly match Explorer's nav pane, which scrolled about half as fast.
        // If the cursor is over a tab row, scroll that horizontally instead of the whole pane, because
        // this handler tunnels first and would otherwise eat the wheel before the tab row sees it.
        private void OnScrollWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
        {
            // The vertical wheel only ever scrolls the pane. Tabs are scrolled solely by horizontal
            // wheel input, handled separately in OnMouseHWheel, so a normal wheel never touches them.
            e.Handled = true;
            double delta = (e.Delta / 120.0) * 48; // pixels per wheel notch
            Scroller.ScrollToVerticalOffset(Scroller.VerticalOffset - delta);
        }

        // Horizontal wheel (tilt wheel or trackpad sideways): always scroll the tab row under the
        // cursor left and right. WPF does not route WM_MOUSEHWHEEL, so we hook it from the window.
        private const int WM_MOUSEHWHEEL = 0x020E;

        private HwndSource _hwheelSrc;

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
            int delta = (short)((wParam.ToInt64() >> 16) & 0xFFFF); // tilt right is positive
            var over = System.Windows.Input.Mouse.DirectlyOver as DependencyObject;
            var tab = FindTabScroller(over);
            if (tab != null && tab.ScrollableWidth > 0)
            {
                double target = tab.HorizontalOffset + Math.Sign(delta) * 48;
                if (target < 0) target = 0;
                if (target > tab.ScrollableWidth) target = tab.ScrollableWidth;
                tab.ScrollToHorizontalOffset(target);
                handled = true;
            }
            return IntPtr.Zero;
        }

        private static ScrollViewer FindTabScroller(DependencyObject d)
        {
            while (d != null)
            {
                var sv = d as ScrollViewer;
                if (sv != null && (sv.Tag as string) == "tabscroll") return sv;
                DependencyObject parent = null;
                try { parent = VisualTreeHelper.GetParent(d); } catch { }
                if (parent == null && d is FrameworkElement fe) parent = fe.Parent;
                d = parent;
            }
            return null;
        }

        // ---- support + resize ----

        private void OnSupportClick(object sender, RoutedEventArgs e)
        {
            OpenSupportLink();
        }

        /// <summary>Action for the title-row toggle. The host decides what it does: collapse the pane
        /// for the window modes, or toggle auto-hide for the desktop dock.</summary>
        public Action TitleToggle;

        private void OnTitleToggle(object sender, RoutedEventArgs e)
        {
            var t = TitleToggle;
            if (t != null) t();
        }

        /// <summary>Collapse to just the title strip (only the expand chevron stays), or restore.</summary>
        public void SetCollapsed(bool collapsed)
        {
            _paneCollapsed = collapsed;
            ApplyProfileTabsState();
            var v = collapsed ? Visibility.Collapsed : Visibility.Visible;
            if (TitleContent != null) TitleContent.Visibility = v;
            if (Scroller != null) Scroller.Visibility = v;
            if (SupportButton != null) SupportButton.Visibility = v;
            if (GearButton != null) GearButton.Visibility = v;
            if (TitleToggleGlyph != null) TitleToggleGlyph.Text = collapsed ? "" : ""; // expand / collapse
            if (TitleToggleGlyph != null) TitleToggleGlyph.Text = collapsed ? "" : ""; // ChevronRight / ChevronLeft
            if (TitleToggleButton != null) TitleToggleButton.ToolTip = collapsed ? "Expand" : "Collapse";
        }

        internal static void OpenSupportLink()
        {
            try
            {
                Process.Start(new ProcessStartInfo("https://plexpixel.com/donate") { UseShellExecute = true });
            }
            catch (Exception ex) { Log.Error("open support link", ex); }
        }

        private int _resizeStartPx;
        private double _resizeAccum;
        private bool _resizeFromLeft;

        /// <summary>Place the resize grip on the left edge (used in beside mode, where the right edge is
        /// held flush against the Explorer window). Dragging the left edge outward widens the pane.</summary>
        public void SetResizeFromLeft(bool fromLeft)
        {
            _resizeFromLeft = fromLeft;
            if (ResizeThumb != null)
                ResizeThumb.HorizontalAlignment = fromLeft ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        }

        private void OnResizeStarted(object sender, DragStartedEventArgs e)
        {
            _resizeStartPx = App.Settings != null ? App.Settings.Current.SidebarWidthPx : 220;
            _resizeAccum = 0;
        }

        private void OnResizeDelta(object sender, DragDeltaEventArgs e)
        {
            if (App.Settings == null) return;
            _resizeAccum += e.HorizontalChange;
            double scale = 1.0;
            try { scale = VisualTreeHelper.GetDpi(this).DpiScaleX; } catch { }
            int sign = _resizeFromLeft ? -1 : 1; // dragging the left grip outward (left) widens
            int px = _resizeStartPx + sign * (int)Math.Round(_resizeAccum * scale);
            App.Settings.NotifyWidthLive(px);
        }

        private void OnResizeCompleted(object sender, DragCompletedEventArgs e)
        {
            if (App.Settings != null) App.Settings.Save(); // persist the final width
        }

        private void HideSettings()
        {
            double w = ActualWidth > 0 ? ActualWidth : 220;
            var anim = new DoubleAnimation(0, w, TimeSpan.FromMilliseconds(180))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
            anim.Completed += (s, e) =>
            {
                SettingsHost.Visibility = Visibility.Collapsed;
                SettingsHost.Child = null;
            };
            SettingsSlide.BeginAnimation(TranslateTransform.XProperty, anim);
        }
    }

    /// <summary>Shared UI helpers: section separators, headers, and expand/collapse animation.</summary>
    internal static class UiHelpers
    {
        public static Border MakeSeparator()
        {
            return new Border
            {
                Height = 1,
                Margin = new Thickness(8, 6, 8, 6),
                Background = AppBrush("SeparatorColor")
            };
        }

        public static Brush AppBrush(string key)
        {
            var b = Application.Current != null ? Application.Current.TryFindResource(key) as Brush : null;
            return b ?? Brushes.Gray;
        }

        /// <summary>
        /// Build a section/group header: a left chevron (revealed on hover) plus an uppercase label.
        /// Single click anywhere on the header toggles expand/collapse. Double click invokes
        /// onDoubleClick (used to rename), and a short timer keeps the two from firing together.
        /// </summary>
        public static Grid BuildHeader(string text, Action toggleClicked, Action onDoubleClick, out RotateTransform chevron, out TextBlock label)
        {
            var grid = new Grid { Margin = new Thickness(0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            chevron = new RotateTransform(0);
            var glyph = new TextBlock
            {
                Text = "", // Segoe MDL2 ChevronRight, rotates to point down when expanded
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 10,
                Opacity = 0,
                Foreground = AppBrush("ChevronColor"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = chevron
            };
            var chevronHit = new Border
            {
                Width = 22,
                Background = Brushes.Transparent,
                Cursor = System.Windows.Input.Cursors.Hand,
                Child = glyph
            };
            Grid.SetColumn(chevronHit, 0);
            grid.Children.Add(chevronHit);

            label = new TextBlock
            {
                Text = (text ?? string.Empty).ToUpperInvariant(),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = AppBrush("TextSecondary"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2, 0, 8, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetColumn(label, 1);
            grid.Children.Add(label);

            grid.Margin = new Thickness(6, 6, 8, 4);
            grid.MouseEnter += (s, e) => glyph.Opacity = 1;
            grid.MouseLeave += (s, e) => glyph.Opacity = 0;
            grid.Background = Brushes.Transparent;
            grid.Cursor = System.Windows.Input.Cursors.Hand;

            // Single click toggles; double click renames. The timer lets a single click settle so the
            // first click of a double-click does not also toggle.
            var dcTimer = new System.Windows.Threading.DispatcherTimer
            { Interval = TimeSpan.FromMilliseconds(220) };
            dcTimer.Tick += (s, e) => { dcTimer.Stop(); if (toggleClicked != null) toggleClicked(); };

            grid.PreviewMouseLeftButtonDown += (s, e) =>
            {
                if (e.ClickCount == 2 && onDoubleClick != null)
                {
                    dcTimer.Stop();
                    e.Handled = true;
                    onDoubleClick();
                }
            };
            grid.MouseLeftButtonUp += (s, e) =>
            {
                if (onDoubleClick == null) { if (toggleClicked != null) toggleClicked(); return; }
                if (!dcTimer.IsEnabled) dcTimer.Start();
            };
            return grid;
        }

        /// <summary>Swap an element for a TextBox to rename it in place. Enter commits, Escape cancels.</summary>
        public static void InlineRename(Panel parent, UIElement target, string initial, Action<string> onCommit, Action onCancel)
        {
            int idx = parent.Children.IndexOf(target);
            if (idx < 0) return;

            var tb = new TextBox
            {
                Text = initial ?? string.Empty,
                FontSize = 12,
                Margin = new Thickness(8, 2, 8, 2)
            };
            bool finished = false;

            Action cancel = () => { if (finished) return; finished = true; onCancel(); };
            Action commit = () =>
            {
                if (finished) return;
                finished = true;
                var v = tb.Text;
                onCommit(v);
            };

            tb.KeyDown += (s, e) =>
            {
                if (e.Key == System.Windows.Input.Key.Enter) { e.Handled = true; commit(); }
                else if (e.Key == System.Windows.Input.Key.Escape) { e.Handled = true; cancel(); }
            };
            tb.LostKeyboardFocus += (s, e) => cancel();

            parent.Children.Remove(target);
            parent.Children.Insert(idx, tb);
            tb.Focus();
            tb.SelectAll();
        }

        /// <summary>Animate a panel open/closed via MaxHeight, and rotate the matching chevron.</summary>
        public static void ToggleExpand(FrameworkElement panel, RotateTransform chevron, bool expand)
        {
            var dur = TimeSpan.FromMilliseconds(150);
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

            if (chevron != null)
            {
                var rot = new DoubleAnimation(chevron.Angle, expand ? 90 : 0, dur) { EasingFunction = ease };
                chevron.BeginAnimation(RotateTransform.AngleProperty, rot);
            }

            // Use FillBehavior.Stop so the animated value reverts to the base MaxHeight we set
            // explicitly. Relying on a Completed handler to clear MaxHeight is fragile, because a
            // replaced animation may never raise Completed, which left a section stuck closed.
            if (expand)
            {
                panel.Visibility = Visibility.Visible;
                panel.MaxHeight = double.PositiveInfinity; // base value: fully open after the animation
                panel.Measure(new Size(panel.ActualWidth > 0 ? panel.ActualWidth : double.PositiveInfinity, double.PositiveInfinity));
                double target = panel.DesiredSize.Height;
                if (target <= 0) { panel.BeginAnimation(FrameworkElement.MaxHeightProperty, null); return; }
                var anim = new DoubleAnimation(0, target, dur) { EasingFunction = ease, FillBehavior = FillBehavior.Stop };
                panel.BeginAnimation(FrameworkElement.MaxHeightProperty, anim);
            }
            else
            {
                double from = panel.ActualHeight;
                panel.MaxHeight = 0; // base value: closed after the animation
                var anim = new DoubleAnimation(from, 0, dur) { EasingFunction = ease, FillBehavior = FillBehavior.Stop };
                anim.Completed += (s, e) => { if (panel.MaxHeight == 0) panel.Visibility = Visibility.Collapsed; };
                panel.BeginAnimation(FrameworkElement.MaxHeightProperty, anim);
            }
        }
    }
}
