using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using QuickPane.Interop;
using QuickPane.Services;
using QuickPane.UI;
using NM = QuickPane.Interop.NativeMethods;

namespace QuickPane.Explorer
{
    /// <summary>
    /// Optional desktop dock. A borderless top-level window at the left edge of the screen. Every
    /// folder click opens a new Explorer window. In reserved mode it registers as an AppBar so the
    /// desktop keeps that strip clear. In auto-hide mode it is a 1px trigger that slides out on hover
    /// and back when the cursor leaves, overlaying whatever is beneath it.
    /// </summary>
    internal sealed class AppBarHost : IDisposable
    {
        private readonly bool _autoHide;
        private readonly bool _allDesktops;
        private Window _window;
        private IntPtr _handle;
        private uint _callbackMsg;
        private bool _registered;
        private SidebarControl _sidebar;
        private DispatcherTimer _collapseTimer;
        private DispatcherTimer _pinTimer;
        private WinEventHook _foregroundHook;
        private IntPtr _lastExplorer;
        private bool _expanded;
        private bool _disposed;

        public AppBarHost(bool autoHide, bool allDesktops) { _autoHide = autoHide; _allDesktops = allDesktops; }

        public void Start()
        {
            bool dark = App.Theme != null && App.Theme.IsDark;
            _sidebar = new SidebarControl();
            _sidebar.Attach(NavigateOrOpen); // navigate the active Explorer, or open a new one
            _sidebar.TitleToggle = () =>
            {
                // For the dock, the title toggle flips auto-hide on or off.
                var st = App.Settings.Current;
                st.DesktopDockAutoHide = !st.DesktopDockAutoHide;
                App.Settings.Save();
            };

            var wa = SystemParameters.WorkArea;
            _window = new Window
            {
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                Topmost = _autoHide, // overlay when auto-hiding; reserved mode does not need topmost
                Title = "QuickPane",
                Background = new SolidColorBrush(dark ? Color.FromRgb(0x20, 0x20, 0x20) : Color.FromRgb(0xF3, 0xF3, 0xF3)),
                Content = _sidebar,
                Left = wa.Left,
                Top = wa.Top,
                Height = wa.Height,
                Width = _autoHide ? 1 : WidthDip()
            };
            App.Theme?.AttachWindow(_window);
            _window.SourceInitialized += OnSourceInitialized;
            _window.Closed += (s, e) => RemoveAppBar();
            _window.Show();

            if (_allDesktops) PinAllDesktops();

            if (_autoHide)
            {
                _collapseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
                _collapseTimer.Tick += (s, e) =>
                {
                    _collapseTimer.Stop();
                    // Do not collapse while a drag or a context menu is in progress, or while the
                    // cursor is still over the pane (a menu popup sits outside the window bounds).
                    if (SidebarControl.SuppressAutoHide || CursorOverWindow()) { _collapseTimer.Start(); return; }
                    Collapse();
                };
                _window.MouseEnter += (s, e) => { _collapseTimer.Stop(); Expand(); };
                _window.MouseLeave += (s, e) => { _collapseTimer.Stop(); _collapseTimer.Start(); };
            }

            if (App.Settings != null)
            {
                App.Settings.Changed += OnSettingsChanged;
                App.Settings.WidthChangedLive += OnSettingsChanged;
            }

            // Track the last active Explorer window so a dock click navigates it instead of always
            // opening a new one.
            _foregroundHook = new WinEventHook(NM.EVENT_SYSTEM_FOREGROUND, NM.EVENT_SYSTEM_FOREGROUND);
            _foregroundHook.Event += OnForeground;
            _foregroundHook.Install();
        }

        // Pin the dock to every virtual desktop. This uses the same private shell interface that Task
        // View's "Show this window on all desktops" uses; there is no documented API for it. The window's
        // application view only exists after it has rendered, so the first attempts right after Show can
        // no-op. Retry a few times until it takes (or give up quietly on builds that do not support it).
        private void PinAllDesktops()
        {
            if (_pinTimer != null) return;
            int attempts = 0;
            _pinTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _pinTimer.Tick += (s, e) =>
            {
                attempts++;
                bool ok = _handle != IntPtr.Zero && VirtualDesktop.PinWindow(_handle);
                if (ok || attempts >= 8) { _pinTimer.Stop(); _pinTimer = null; }
            };
            _pinTimer.Start();
        }

        private void OnForeground(uint evt, IntPtr hwnd, int idObject, int idChild, uint thread)
        {
            var c = NM.ClassOf(hwnd);
            if (c == "CabinetWClass" || c == "ExploreWClass") _lastExplorer = hwnd;
        }

        private static int WidthPx()
        {
            int w = App.Settings != null ? App.Settings.Current.SidebarWidthPx : 220;
            if (w < 160) w = 160; if (w > 400) w = 400;
            return w;
        }

        private double WidthDip()
        {
            double scale = 1.0;
            try { if (_window != null) scale = VisualTreeHelper.GetDpi(_window).DpiScaleX; } catch { }
            if (scale <= 0) scale = 1.0;
            return WidthPx() / scale;
        }

        private void OnSourceInitialized(object sender, EventArgs e)
        {
            _handle = new WindowInteropHelper(_window).Handle;
            HwndSource.FromHwnd(_handle)?.AddHook(WndProc);

            // Tool window keeps the dock out of the Alt+Tab switcher, but a tool window has no shell
            // application view and so cannot be pinned to all virtual desktops. When the user wants it on
            // every desktop, leave it as a normal (pinnable) window; otherwise hide it from Alt+Tab.
            var ex = NM.GetWindowLongPtr(_handle, NM.GWL_EXSTYLE).ToInt64();
            if (!_allDesktops)
                NM.SetWindowLongPtr(_handle, NM.GWL_EXSTYLE, new IntPtr(ex | NM.WS_EX_TOOLWINDOW));

            if (_autoHide)
            {
                SetAutoHide(false); // start collapsed at the left edge
            }
            else
            {
                _callbackMsg = NM.RegisterWindowMessage("QuickPaneAppBarMessage");
                var abd = new NM.APPBARDATA { cbSize = Marshal.SizeOf(typeof(NM.APPBARDATA)), hWnd = _handle, uCallbackMessage = _callbackMsg };
                NM.SHAppBarMessage(NM.ABM_NEW, ref abd);
                _registered = true;
                SetReservedPosition();
            }
        }

        private static bool GetWorkArea(out NM.RECT rc)
        {
            rc = new NM.RECT();
            if (NM.SystemParametersInfo(NM.SPI_GETWORKAREA, 0, ref rc, 0)) return true;
            rc.Left = 0; rc.Top = 0;
            rc.Right = NM.GetSystemMetrics(NM.SM_CXSCREEN);
            rc.Bottom = NM.GetSystemMetrics(NM.SM_CYSCREEN);
            return true;
        }

        // Position the auto-hide dock on the LEFT screen edge, full work-area height. Collapsed it is a
        // thin trigger strip; expanded it grows rightward keeping its left edge at the screen edge.
        private void SetAutoHide(bool expanded)
        {
            if (_handle == IntPtr.Zero) return;
            NM.RECT wa; GetWorkArea(out wa);
            int width = expanded ? WidthPx() : 4;
            int top = wa.Top;
            int height = wa.Bottom - wa.Top;
            if (!expanded)
            {
                // Leave the top and bottom corners out of the collapsed trigger so the maximized-window
                // menu (top-left) and the taskbar/Start corner (bottom-left) stay reachable without the
                // pane sliding out by accident. The expanded pane still spans the full height.
                const int dead = 46;
                if (height > 2 * dead + 10) { top = wa.Top + dead; height = (wa.Bottom - wa.Top) - 2 * dead; }
            }
            NM.SetWindowPos(_handle, NM.HWND_TOP, wa.Left, top, width, height,
                NM.SWP_NOACTIVATE | NM.SWP_SHOWWINDOW);
        }

        private void SetReservedPosition()
        {
            if (_handle == IntPtr.Zero) return;
            int width = WidthPx();
            int screenH = NM.GetSystemMetrics(NM.SM_CYSCREEN);

            var abd = new NM.APPBARDATA
            {
                cbSize = Marshal.SizeOf(typeof(NM.APPBARDATA)),
                hWnd = _handle,
                uEdge = NM.ABE_LEFT
            };
            abd.rc.Left = 0; abd.rc.Top = 0; abd.rc.Right = width; abd.rc.Bottom = screenH;
            NM.SHAppBarMessage(NM.ABM_QUERYPOS, ref abd);
            abd.rc.Right = abd.rc.Left + width;
            NM.SHAppBarMessage(NM.ABM_SETPOS, ref abd);
            NM.MoveWindow(_handle, abd.rc.Left, abd.rc.Top, abd.rc.Right - abd.rc.Left, abd.rc.Bottom - abd.rc.Top, true);
        }

        private bool CursorOverWindow()
        {
            if (_handle == IntPtr.Zero) return false;
            NM.POINT pt;
            if (!NM.GetCursorPos(out pt)) return false;
            NM.RECT r;
            if (!NM.GetWindowRect(_handle, out r)) return false;
            return pt.X >= r.Left && pt.X < r.Right && pt.Y >= r.Top && pt.Y < r.Bottom;
        }

        private void Expand()
        {
            if (_expanded) return;
            _expanded = true;
            SetAutoHide(true);
        }

        private void Collapse()
        {
            if (!_expanded) return;
            _expanded = false;
            SetAutoHide(false);
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (!_autoHide && _callbackMsg != 0 && (uint)msg == _callbackMsg && wParam.ToInt32() == NM.ABN_POSCHANGED)
            {
                SetReservedPosition();
                handled = true;
            }
            return IntPtr.Zero;
        }

        private void OnSettingsChanged(object sender, EventArgs e)
        {
            try
            {
                if (_autoHide) SetAutoHide(_expanded);
                else SetReservedPosition();
            }
            catch (Exception ex) { Log.Error("appbar reposition", ex); }
        }

        private void NavigateOrOpen(string path)
        {
            App.Recents?.RecordNavigation(path);
            // If an Explorer window is open, change its location; otherwise open a new one. After the
            // new window opens it becomes the active Explorer, so the next click navigates it.
            if (_lastExplorer != IntPtr.Zero && NM.IsWindow(_lastExplorer) &&
                ExplorerNavigator.Navigate(_lastExplorer, path))
            {
                NM.SetForegroundWindow(_lastExplorer); // bring the navigated window to the front
                return;
            }
            ExplorerNavigator.OpenNewWindow(path);
        }

        private void RemoveAppBar()
        {
            if (!_registered || _handle == IntPtr.Zero) return;
            try
            {
                var abd = new NM.APPBARDATA { cbSize = Marshal.SizeOf(typeof(NM.APPBARDATA)), hWnd = _handle };
                NM.SHAppBarMessage(NM.ABM_REMOVE, ref abd);
            }
            catch (Exception ex) { Log.Error("appbar remove", ex); }
            _registered = false;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (App.Settings != null)
            {
                App.Settings.Changed -= OnSettingsChanged;
                App.Settings.WidthChangedLive -= OnSettingsChanged;
            }
            try { _collapseTimer?.Stop(); } catch { }
            try { _pinTimer?.Stop(); } catch { }
            try { _foregroundHook?.Dispose(); } catch { }
            RemoveAppBar();
            try { _window?.Close(); } catch (Exception ex) { Log.Error("appbar window close", ex); }
            _window = null;
            _sidebar = null;
        }
    }
}
