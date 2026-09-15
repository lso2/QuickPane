using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using QuickPane.Interop;
using QuickPane.Services;
using QuickPane.UI;
using NM = QuickPane.Interop.NativeMethods;

namespace QuickPane.Explorer
{
    /// <summary>
    /// "Beside Explorer window" mode. For each Explorer window a separate borderless pane window is
    /// placed flush against the window's visible left edge and follows it. Nothing inside Explorer is
    /// touched, so its content is never shifted and there is no flicker. Only the active Explorer
    /// window shows its pane, and our own pane windows never count as losing focus.
    /// </summary>
    internal sealed class ExplorerFollowerWatcher : IDisposable
    {
        private readonly Dictionary<IntPtr, FollowerPane> _panes = new Dictionary<IntPtr, FollowerPane>();
        private WinEventHook _life, _loc, _fg;
        private DispatcherTimer _sweep;
        private IntPtr _active;

        public void Start()
        {
            _life = new WinEventHook(NM.EVENT_OBJECT_CREATE, NM.EVENT_OBJECT_SHOW);
            _life.Event += OnLife; _life.Install();
            _loc = new WinEventHook(NM.EVENT_OBJECT_LOCATIONCHANGE, NM.EVENT_OBJECT_LOCATIONCHANGE);
            _loc.Event += OnLoc; _loc.Install();
            _fg = new WinEventHook(NM.EVENT_SYSTEM_FOREGROUND, NM.EVENT_SYSTEM_FOREGROUND);
            _fg.Event += OnFg; _fg.Install();

            _sweep = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            _sweep.Tick += (s, e) => Rescan();
            _sweep.Start();

            if (App.Settings != null)
            {
                App.Settings.Changed += OnSettings;
                App.Settings.WidthChangedLive += OnSettings;
            }
            Rescan();
        }

        private static bool IsFolder(IntPtr h)
        {
            if (!NM.IsWindow(h)) return false;
            var c = NM.ClassOf(h);
            return c == "CabinetWClass" || c == "ExploreWClass";
        }

        private bool IsOwnPane(IntPtr h)
        {
            foreach (var kv in _panes) if (kv.Value.Handle == h) return true;
            return false;
        }

        private void OnLife(uint evt, IntPtr hwnd, int idObject, int idChild, uint thread)
        {
            if (idObject != NM.OBJID_WINDOW || idChild != 0) return;
            if (evt == NM.EVENT_OBJECT_DESTROY)
            {
                if (_panes.TryGetValue(hwnd, out var p)) { _panes.Remove(hwnd); p.Close(); }
                return;
            }
            if (IsFolder(hwnd) && !_panes.ContainsKey(hwnd)) EnsurePane(hwnd);
        }

        private void OnLoc(uint evt, IntPtr hwnd, int idObject, int idChild, uint thread)
        {
            if (idObject != NM.OBJID_WINDOW) return;
            if (_panes.TryGetValue(hwnd, out var p)) Position(hwnd, p);
        }

        private void OnFg(uint evt, IntPtr hwnd, int idObject, int idChild, uint thread)
        {
            if (IsOwnPane(hwnd)) return; // clicking our own pane must not move it
            if (IsFolder(hwnd)) { _active = hwnd; EnsurePane(hwnd); }
            // Any foreground change can reorder windows. Re-apply every pane's z-order so each one
            // tracks its own Explorer window: visible beside it when that window is up, tucked behind
            // it when another app covers it. The pane is no longer hidden just because Explorer lost
            // focus, so it persists while you click around.
            PositionAll();
        }

        private void PositionAll()
        {
            foreach (var kv in _panes) Position(kv.Key, kv.Value);
        }

        private void EnsurePane(IntPtr hwnd)
        {
            if (_panes.ContainsKey(hwnd)) return;
            try { _panes[hwnd] = new FollowerPane(hwnd); }
            catch (Exception ex) { Log.Error("create follower", ex); }
        }

        private void Position(IntPtr hwnd, FollowerPane pane)
        {
            // Hide the pane only when its window is gone, minimized, or hidden. The owner relationship
            // takes care of virtual-desktop visibility on its own.
            if (!NM.IsWindow(hwnd) || !NM.IsWindowVisible(hwnd) || NM.IsIconic(hwnd)) { pane.Hide(); return; }
            NM.RECT r;
            // Visible bounds (no invisible DWM border) so the pane sits flush with no gap.
            if (NM.DwmGetWindowAttribute(hwnd, NM.DWMWA_EXTENDED_FRAME_BOUNDS, out r, Marshal.SizeOf(typeof(NM.RECT))) != 0)
            {
                if (!NM.GetWindowRect(hwnd, out r)) return;
            }
            pane.PositionBeside(r, WidthPx(hwnd));
        }

        /// <summary>Pane width in device pixels, as the user set it. The setting is already measured on
        /// their own screen, so it is used as written; converting it by the monitor's scale inflated it
        /// and left the pane and the space made for it disagreeing about how wide it was.</summary>
        private static int WidthPx(IntPtr forWindow)
        {
            int w = App.Settings != null ? App.Settings.Current.SidebarWidthPx : 220;
            if (w < 160) w = 160; if (w > 400) w = 400;
            return w;
        }

        private void OnSettings(object sender, EventArgs e)
        {
            PositionAll();
        }

        private void Rescan()
        {
            NM.EnumWindows((hwnd, l) =>
            {
                if (NM.IsWindowVisible(hwnd) && IsFolder(hwnd) && !_panes.ContainsKey(hwnd)) EnsurePane(hwnd);
                return true;
            }, IntPtr.Zero);

            var dead = new List<IntPtr>();
            foreach (var kv in _panes) if (!NM.IsWindow(kv.Key)) dead.Add(kv.Key);
            foreach (var h in dead) { _panes[h].Close(); _panes.Remove(h); }

            // Keep every pane glued beside its Explorer window, regardless of which is in front.
            var fg = NM.GetForegroundWindow();
            if (IsFolder(fg)) { _active = fg; EnsurePane(fg); }
            PositionAll();
        }

        public void Dispose()
        {
            try { _sweep?.Stop(); } catch { }
            if (App.Settings != null)
            {
                App.Settings.Changed -= OnSettings;
                App.Settings.WidthChangedLive -= OnSettings;
            }
            try { _life?.Dispose(); } catch { }
            try { _loc?.Dispose(); } catch { }
            try { _fg?.Dispose(); } catch { }
            foreach (var kv in _panes) { try { kv.Value.Close(); } catch { } }
            _panes.Clear();
        }
    }

    /// <summary>A single pane window glued to the left of one Explorer window.</summary>
    internal sealed class FollowerPane
    {
        private const int CollapsedPx = 36;
        private readonly IntPtr _cab;
        private readonly Action<string> _navOverride;
        private Window _window;
        private SidebarControl _sidebar;
        private IntPtr _handle;
        private bool _collapsed;
        private bool _havePos;
        private NM.RECT _lastVisible;

        public IntPtr Handle { get { return _handle; } }

        /// <summary>The pane content, for moving keyboard focus into it.</summary>
        public SidebarControl Sidebar { get { return _sidebar; } }

        public FollowerPane(IntPtr cab, Action<string> navigate = null, string hostApp = null)
        {
            _cab = cab;
            _navOverride = navigate; // null -> drive an Explorer window; set -> drive e.g. a file dialog
            bool dark = App.Theme != null && App.Theme.IsDark;
            var sidebar = new SidebarControl();
            sidebar.HostApp = hostApp;  // set before Attach, which is what builds the sections
            _sidebar = sidebar;
            sidebar.SetResizeFromLeft(true); // outer (left) edge resizes; right stays flush to the window
            sidebar.Attach(NavigateThis);
            var sb = sidebar;
            sidebar.TitleToggle = () => ToggleCollapsed(sb);

            _window = new Window
            {
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                ShowActivated = false,   // do not steal focus when it appears
                Topmost = false,         // sit at its Explorer window's z-level, never forced above other windows
                Title = "QuickPane",
                Background = new SolidColorBrush(dark ? Color.FromRgb(0x20, 0x20, 0x20) : Color.FromRgb(0xF3, 0xF3, 0xF3)),
                Content = sidebar,
                Width = 220,
                Height = 400,
                Visibility = Visibility.Hidden
            };
            App.Theme?.AttachWindow(_window);
            _window.SourceInitialized += (s, e) =>
            {
                _handle = new WindowInteropHelper(_window).Handle;
                // Tool window keeps it off the taskbar. It stays activatable so right-click menus work.
                var ex = NM.GetWindowLongPtr(_handle, NM.GWL_EXSTYLE).ToInt64();
                NM.SetWindowLongPtr(_handle, NM.GWL_EXSTYLE, new IntPtr(ex | NM.WS_EX_TOOLWINDOW));
                // Make the Explorer window the pane's OWNER. Windows then keeps the pane at that window's
                // z-level (just above it, never topmost), on that window's virtual desktop, and hides it
                // automatically when the window is minimized. No manual z-order or desktop tracking.
                NM.SetWindowLongPtr(_handle, NM.GWLP_HWNDPARENT, _cab);
            };
            _window.Show();
        }

        private void NavigateThis(string path)
        {
            App.Recents?.RecordNavigation(path);
            if (_navOverride != null) _navOverride(path);
            else ExplorerNavigator.NavigateAsync(_cab, path); // COM navigation stays off the UI thread
        }

        public void PositionBeside(NM.RECT visible, int widthPx)
        {
            if (_window == null || _handle == IntPtr.Zero) return;
            _lastVisible = visible; _havePos = true;
            int effW = _collapsed ? CollapsedPx : widthPx;
            int height = visible.Bottom - visible.Top;
            if (height < 50) return;
            // Sit flush to the left of the window without overlapping, so the window's left resize
            // border stays reachable. Clamping the pane to zero when there was no room put it on top of
            // the window it belongs to, and on a monitor left of the primary one it threw the pane onto
            // a different screen, so the window is pushed right to make room instead.
            var wa = NM.WorkAreaFor(_cab);
            int left = visible.Left - effW;
            if (left < wa.Left)
            {
                MakeRoom(wa.Left + effW - visible.Left, wa);
                left = wa.Left;
            }
            int top = visible.Top;

            // Owner relationship handles z-order, so only move, size and show the pane here.
            //
            // SWP_SHOWWINDOW matters: the host window can be sitting with WS_VISIBLE clear while WPF
            // still believes the Window is Visible, so the managed check below is a no-op and the pane
            // is placed correctly and never drawn. That is every pane that goes beside rather than
            // inside, which is the classic comdlg32 dialogs, Photoshop, and any app handed over after
            // its dialog fought the inside layout.
            NM.SetWindowPos(_handle, IntPtr.Zero, left, top, effW, height,
                NM.SWP_NOACTIVATE | NM.SWP_NOZORDER | NM.SWP_SHOWWINDOW);
            if (_window.Visibility != Visibility.Visible) _window.Visibility = Visibility.Visible;
        }

        /// <summary>Slide the window this pane belongs to rightward by the amount the pane is short,
        /// narrowing it only when the work area cannot hold both. Maximized and minimized windows are
        /// left alone, because Windows owns those placements. SetWindowPos works in real window
        /// coordinates while the pane is placed against the DWM visible bounds, so the true rect is read
        /// back here rather than reusing the visible one, which would shrink the window by its invisible
        /// border on every pass.</summary>
        private void MakeRoom(int shortfall, NM.RECT wa)
        {
            if (shortfall <= 0 || !NM.IsWindow(_cab)) return;
            if (NM.IsZoomed(_cab) || NM.IsIconic(_cab)) return;

            NM.RECT wr;
            if (!NM.GetWindowRect(_cab, out wr)) return;

            int w = wr.Width, h = wr.Height;
            int newLeft = wr.Left + shortfall;
            if (newLeft + w > wa.Right)
            {
                w = wa.Right - newLeft;
                if (w < 320) return; // too little left to be worth reshaping the window
            }
            NM.SetWindowPos(_cab, IntPtr.Zero, newLeft, wr.Top, w, h,
                NM.SWP_NOZORDER | NM.SWP_NOACTIVATE);
        }

        private void ToggleCollapsed(SidebarControl sb)
        {
            _collapsed = !_collapsed;
            sb.SetCollapsed(_collapsed);
            if (!_havePos) return;
            int w = App.Settings != null ? App.Settings.Current.SidebarWidthPx : 220;
            if (w < 160) w = 160; if (w > 400) w = 400;
            PositionBeside(_lastVisible, w);
        }

        public void Hide()
        {
            // Hide the window itself as well as telling WPF, so the two cannot disagree about whether
            // the pane is on screen.
            if (_handle != IntPtr.Zero) NM.ShowWindow(_handle, NM.SW_HIDE);
            if (_window != null && _window.Visibility == Visibility.Visible)
                _window.Visibility = Visibility.Hidden;
        }

        public void Close()
        {
            try { _sidebar?.Detach(); } catch (Exception ex) { Log.Error("sidebar detach", ex); }
            try { _window?.Close(); } catch (Exception ex) { Log.Error("follower close", ex); }
            _window = null;
            _sidebar = null;
            _handle = IntPtr.Zero;
        }
    }
}
