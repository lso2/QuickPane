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
using NM = QuickPane.Interop.NativeMethods;

namespace QuickPane.UI
{
    /// <summary>
    /// Root of the embedded sidebar. Builds the three sections in the order from settings, opens the
    /// real Settings window from its gear button, and rebuilds when groups, recents, theme, or settings
    /// change. One instance lives per Explorer window.
    /// </summary>
    public partial class SidebarControl : UserControl
    {
        private Action<string> _navigate;

        /// <summary>Process name of the app whose file dialog hosts this pane, or null in an Explorer
        /// window. The per-app sections exist only to answer "where does this app save", which an
        /// Explorer window has no answer to, so they are built only when this is set.</summary>
        public string HostApp { get; set; }

        /// <summary>The pane a row belongs to, found by walking up from the row. A row is built before it
        /// is in the tree, so anything that depends on the host app has to ask at the moment it is
        /// needed rather than at construction.</summary>
        internal static SidebarControl Owning(DependencyObject d)
        {
            while (d != null)
            {
                var sb = d as SidebarControl;
                if (sb != null) return sb;
                d = System.Windows.Media.VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d);
            }
            return null;
        }
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

            PreviewMouseWheel += OnWheel;
            Loaded += (s, e) => { Wire(); HookHWheel(); };
            Unloaded += (s, e) => { Unwire(); UnhookHWheel(); };
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
            SshfsService.MountStatusChanged += OnSshMountChanged;
            PaneFilter.Changed += OnFilterChanged;
        }

        private void Unwire()
        {
            if (!_wired) return;
            _wired = false;
            if (App.Theme != null) App.Theme.ThemeChanged -= OnDataChanged;
            if (App.Settings != null) App.Settings.Changed -= OnSettingsChanged;
            SshfsService.MountStatusChanged -= OnSshMountChanged;
            PaneFilter.Changed -= OnFilterChanged;
        }

        /// <summary>Release every subscription this pane and its sections hold.
        ///
        /// A pane is hosted in an HwndSource, and disposing that host destroys the window without WPF
        /// ever raising Unloaded on the root visual, so the Unloaded handler above never runs for a pane
        /// torn down that way. The theme, the settings store and the static PathStatus, DrivesChanged and
        /// MountStatusChanged events all outlive the pane, so a pane left subscribed to them is held in
        /// memory for the life of the process and goes on rebuilding itself every time one of them
        /// fires. Every host therefore calls this before disposing its host.</summary>
        public void Detach()
        {
            if (_detached) return;
            _detached = true;
            Unwire();
            UnhookHWheel();
            DetachSections();
        }

        private bool _detached;

        /// <summary>Each section holds subscriptions of its own, and clearing the panel only raises
        /// Unloaded on a pane that was loaded, so they are told directly.</summary>
        private void DetachSections()
        {
            if (SectionsPanel == null) return;
            foreach (var child in SectionsPanel.Children)
            {
                var g = child as GroupSection; if (g != null) { g.Detach(); continue; }
                var r = child as RecentsSection; if (r != null) { r.Detach(); continue; }
                var c = child as ComputerSection; if (c != null) { c.Detach(); continue; }
                var ra = child as RecentAppsSection; if (ra != null) { ra.Detach(); continue; }
                var ad = child as AppDefaultsSection; if (ad != null) { ad.Detach(); continue; }
            }
        }

        private void OnSshMountChanged(object sender, EventArgs e)
        {
            QueueBuild();
        }

        // Typing only changes which rows are shown, so the rows are re-filtered rather than rebuilt.
        private void OnFilterChanged()
        {
            if (_detached) return;
            Dispatcher.BeginInvoke(new Action(ApplyFilter),
                System.Windows.Threading.DispatcherPriority.Background);
        }

        /// <summary>Put the caret in the search box, for the keyboard shortcut.</summary>
        public bool FocusSearch()
        {
            if (SectionsPanel == null) return false;
            foreach (var child in SectionsPanel.Children)
            {
                var box = child as SearchSection;
                if (box != null) { box.FocusBox(); return true; }
            }
            return false;
        }

        /// <summary>
        /// Give the pane's own window the keyboard, so a text box inside it receives what is typed.
        ///
        /// A pane lives inside another application's window and is created without activation, which is
        /// what stops it stealing focus from Explorer every time it appears. The cost is that clicking
        /// into it moves nothing: the host window's thread still owns the input queue that decides where
        /// keystrokes land, so focus has to be taken by borrowing that queue for the length of the call.
        /// </summary>
        internal bool FocusHostWindow()
        {
            try
            {
                var src = PresentationSource.FromVisual(this) as HwndSource;
                if (src == null || src.Handle == IntPtr.Zero) return false;

                IntPtr fg = NM.GetForegroundWindow();
                uint pid;
                uint theirs = fg == IntPtr.Zero ? 0 : NM.GetWindowThreadProcessId(fg, out pid);
                uint ours = NM.GetCurrentThreadId();

                bool attached = theirs != 0 && theirs != ours && NM.AttachThreadInput(ours, theirs, true);
                try { NM.SetFocus(src.Handle); }
                finally { if (attached) NM.AttachThreadInput(ours, theirs, false); }
                return true;
            }
            catch (Exception ex) { Log.Error("move the keyboard into the pane", ex); return false; }
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
            ApplyFilter();
        }

        /// <summary>Hide the rows that do not match what was typed in the search box.
        ///
        /// The filter runs over the rows the sections have already built rather than being threaded
        /// through each section's construction, so a section never has to know a filter exists and the
        /// behavior cannot drift between them. A section whose rows all disappear is hidden with them,
        /// so the pane does not fill with empty headers while typing.</summary>
        private void ApplyFilter()
        {
            if (SectionsPanel == null) return;
            foreach (var child in SectionsPanel.Children)
            {
                var fe = child as FrameworkElement;
                if (fe == null || fe is SearchSection) continue;
                int shown = FilterWithin(fe);
                // With no filter every section stands; with one, only those with a surviving row.
                fe.Visibility = (!PaneFilter.Active || shown > 0) ? Visibility.Visible : Visibility.Collapsed;
            }
        }



        /// <summary>
        /// Apply the filter to every folder row beneath an element, returning how many stayed.
        ///
        /// The pane has two kinds of row and the filter has to know both: a flat row, and a tree row
        /// that can hold more rows under it. A tree row survives when its own label matches or when
        /// anything already expanded beneath it does, so filtering never hides the way to a match.
        /// </summary>
        private static int FilterWithin(DependencyObject root)
        {
            int shown = 0;
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);

                var item = child as FolderItem;
                if (item != null)
                {
                    bool keepItem = PaneFilter.Matches(item.LabelText);
                    item.Visibility = keepItem ? Visibility.Visible : Visibility.Collapsed;
                    if (keepItem) shown++;
                    continue;
                }

                var node = child as FolderTreeNode;
                if (node != null)
                {
                    int beneath = node.ChildrenHost != null ? FilterWithin(node.ChildrenHost) : 0;
                    bool keepNode = beneath > 0 || PaneFilter.Matches(node.LabelText);
                    node.Visibility = keepNode ? Visibility.Visible : Visibility.Collapsed;
                    if (keepNode) shown++;
                    continue;
                }

                var fe = child as FrameworkElement;
                var tag = fe == null ? null : fe.Tag as string;

                if (tag == UiHelpers.SeparatorTag)
                {
                    fe.Visibility = PaneFilter.Active ? Visibility.Collapsed : Visibility.Visible;
                    continue;
                }

                int inside = FilterWithin(child);

                if (tag == UiHelpers.FilterBlockTag)
                {
                    fe.Visibility = (!PaneFilter.Active || inside > 0) ? Visibility.Visible : Visibility.Collapsed;
                }

                shown += inside;
            }
            return shown;
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
                    // A neutral lift off the pane rather than an accent tint, which reads as a colored
                    // highlight over a tinted surface.
                    Background = active ? UiHelpers.AppBrush("BadgeBackground") : Brushes.Transparent,
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
                case "search":
                    var sb = new SearchSection(); sb.Build(_navigate); return sb;
                case "appdefaults":
                    // Only inside a file dialog: an Explorer window is not an app asking for a folder.
                    if (string.IsNullOrEmpty(HostApp)) return null;
                    var ad = new AppDefaultsSection(); ad.Build(_navigate, HostApp); return ad;
                case "recentapps":
                    var ra = new RecentAppsSection(); ra.Build(_navigate); return ra;
                case "computer":
                    var c = new ComputerSection(); c.Build(_navigate); return c;
                case "network":
                    // shell: form rather than the raw CLSID. Navigate2 rejects "::{GUID}" outright with
                    // "value does not fall within the expected range", so clicking Network did nothing.
                    var n = new ShellRootSection("network", "Network", "shell:NetworkPlacesFolder", false);
                    n.Build(_navigate); return n;
                case "linux":
                    if (!ShellRootSection.WslPresent()) return null;
                    var lx = new ShellRootSection("linux", "Linux", "\\\\wsl$", true);
                    lx.Build(_navigate); return lx;
                case "ssh":
                    var profiles = App.Settings.Current.SshProfiles;
                    if (profiles == null || profiles.Count == 0) return null;
                    var ssh = new SshSection();
                    ssh.Build(_navigate); return ssh;
                default:
                    return null;
            }
        }

        // ---- settings ----------------------------------------------

        // Opens the real Settings window instead of embedding a second, cramped copy of the settings
        // UI in the sidebar itself, so there is exactly one settings surface to keep in sync.
        private void OnGearClick(object sender, RoutedEventArgs e)
        {
            App.ShowSettings();
        }

        // Slow the wheel to roughly match Explorer's nav pane, which scrolled about half as fast.
        // If the cursor is over a tab row, scroll that horizontally instead of the whole pane, because
        // this handler tunnels first and would otherwise eat the wheel before the tab row sees it.
        // ---- wheel routing --------------------------------------------
        //
        // Every wheel event in the pane arrives here, whichever device produced it, so there is one
        // place that decides what moves and one rule per direction:
        //
        //   vertical wheel              -> the pane scrolls up and down, always
        //   Shift + vertical wheel      -> the tab strip under the cursor moves sideways
        //   horizontal wheel            -> the tab strip under the cursor moves sideways
        //
        // A tab strip is reached by sideways input only. Mapping a plain vertical wheel onto sideways
        // movement means inventing a direction the system has no convention for, and whichever way it
        // is chosen it reads as backwards to half the people using it.

        private const double WheelStep = 48;   // pixels per notch, matching Explorer's nav pane

        private void OnWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
        {
            if (e.Delta == 0) return;

            bool shift = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) != 0;
            if (shift)
            {
                // Shift + wheel away from you goes left, the same as every browser and file manager.
                if (ScrollStripBy(StripUnderCursor(), -StepOf(e.Delta))) e.Handled = true;
                return;
            }

            e.Handled = true;
            Scroller.ScrollToVerticalOffset(Scroller.VerticalOffset - StepOf(e.Delta));
        }

        // Horizontal wheel: a trackpad's two-finger sideways swipe and a tilt wheel both arrive as
        // WM_MOUSEHWHEEL, which WPF does not route at all, so the pane's own window has to read it.
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

        private void UnhookHWheel()
        {
            if (_hwheelSrc == null) return;
            try { _hwheelSrc.RemoveHook(HWheelProc); } catch { }
            _hwheelSrc = null;
        }

        private IntPtr HWheelProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg != WM_MOUSEHWHEEL) return IntPtr.Zero;
            // Positive means the wheel went right, and Windows has already applied whatever scrolling
            // direction the person set, so following the sign as given is what makes a swipe land the
            // way their system says it should.
            int delta = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
            if (ScrollStripBy(StripUnderCursor(), StepOf(delta))) handled = true;
            return IntPtr.Zero;
        }

        private static double StepOf(int wheelDelta) { return (wheelDelta / 120.0) * WheelStep; }

        /// <summary>The tab strip the pointer is inside, or null when it is over anything else.</summary>
        private static ScrollViewer StripUnderCursor()
        {
            var d = System.Windows.Input.Mouse.DirectlyOver as DependencyObject;
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

        /// <summary>Move a strip by a pixel amount. False when it has nowhere left to go that way, so
        /// the event carries on to whatever would have handled it otherwise.</summary>
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
    }

    /// <summary>Shared UI helpers: section separators, headers, and expand/collapse animation.</summary>
    internal static class UiHelpers
    {
        public const string SeparatorTag = "qp-separator";

        /// <summary>Marks a header and the rows under it as one thing, so filtering never leaves a
        /// heading standing over nothing.</summary>
        public const string FilterBlockTag = "qp-block";

        public static Border MakeSeparator()
        {
            return new Border
            {
                Height = 1,
                Margin = new Thickness(8, 6, 8, 6),
                Background = AppBrush("SeparatorColor"),
                Tag = SeparatorTag   // a rule between rows means nothing once the rows are filtered out
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
                Opacity = 1,   // always shown, so a section reads as collapsible before it is hovered
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
