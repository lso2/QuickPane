using System;
using System.Text;
using System.Windows.Interop;
using System.Windows.Media;
using QuickPane.Interop;
using QuickPane.Services;
using QuickPane.UI;
using NM = QuickPane.Interop.NativeMethods;

namespace QuickPane.Explorer
{
    /// <summary>
    /// Owns the embedded sidebar for a single Explorer window. It reparents a WPF HwndSource as a
    /// child of the CabinetWClass window and shifts Explorer's content host (ShellTabWindowClass) to
    /// the right by the sidebar width. Layout is reapplied whenever Explorer relays out, because
    /// Explorer restores its content host to full width on every resize.
    /// </summary>
    internal sealed class ExplorerWindow : IDisposable
    {
        private readonly IntPtr _cabinet;
        private readonly Action<string> _navOverride;
        private IntPtr _contentHost;
        private HwndSource _host;
        private IntPtr _hostHandle; // cached so the background enforcer never touches WPF objects
        private SidebarControl _sidebar;
        private volatile int _width;
        private volatile int _contentTop;
        private volatile bool _applying;
        private volatile bool _disposed;
        private volatile bool _collapsed;
        private volatile bool _ready;     // false until the window is presented and its toolbar laid out
        private int _attachTick;          // tick recorded at attach, for the readiness grace fallback
        public Action OnFirstShown;       // watcher hooks this to run a brief hold-burst when the strip first shows
        private const int CollapsedPx = 36;
        private int _logCount;

        private int EffWidth() { return _collapsed ? CollapsedPx : _width; }

        public void ToggleCollapsed()
        {
            _collapsed = !_collapsed;
            _sidebar?.SetCollapsed(_collapsed);
            ApplyLayout();
        }

        public IntPtr Cabinet { get { return _cabinet; } }
        public IntPtr ContentHost { get { return _contentHost; } }
        public bool Attached { get { return _host != null; } }

        public ExplorerWindow(IntPtr cabinet, Action<string> navigate = null)
        {
            _cabinet = cabinet;
            _navOverride = navigate; // null -> drive a real Explorer window; set -> e.g. a file dialog
            _width = ClampWidth(App.Settings != null ? App.Settings.Current.SidebarWidthPx : 220);
        }

        public bool TryAttach()
        {
            if (_disposed || _host != null) return _host != null;
            if (!NM.IsWindow(_cabinet)) return false;

            _contentHost = FindContentHost(_cabinet);
            if (_contentHost == IntPtr.Zero) return false; // children not ready yet, caller retries

            try
            {
                _sidebar = new SidebarControl();
                _sidebar.Attach(NavigateInThisWindow);
                _sidebar.TitleToggle = ToggleCollapsed;

                NM.RECT client;
                NM.GetClientRect(_cabinet, out client);
                _contentTop = MeasureContentTop(client);

                var p = new HwndSourceParameters("QuickPaneSidebar")
                {
                    ParentWindow = _cabinet,
                    WindowStyle = unchecked((int)(NM.WS_CHILD | NM.WS_CLIPSIBLINGS | NM.WS_CLIPCHILDREN)),
                    ExtendedWindowStyle = unchecked((int)NM.WS_EX_NOACTIVATE),
                    PositionX = 0,
                    PositionY = _contentTop,
                    Width = _width,
                    Height = Math.Max(0, client.Height - _contentTop)
                };
                _host = new HwndSource(p);
                _hostHandle = _host.Handle;
                _host.RootVisual = _sidebar;
                // Clear the child HWND to an opaque theme color, so even before the WPF visual tree
                // paints there is a solid strip. This both removes any see-through and acts as a
                // diagnostic: a blank colored strip would mean the HWND presents but the tree does not.
                bool dark = App.Theme != null && App.Theme.IsDark;
                _host.CompositionTarget.BackgroundColor = dark
                    ? Color.FromRgb(0x20, 0x20, 0x20)
                    : Color.FromRgb(0xF3, 0xF3, 0xF3);

                // Created hidden. ApplyLayout reveals the strip only once the window is presented and its
                // toolbar has laid out, so it never flashes blank or sits over the toolbar during load.
                _attachTick = Environment.TickCount;
                ApplyLayout();

                if (App.Settings != null)
                {
                    App.Settings.Changed += OnSettingsChanged;
                    App.Settings.WidthChangedLive += OnSettingsChanged;
                }
                Log.Info("Attached sidebar to Explorer window " + _cabinet.ToString("X") +
                         " (content host " + _contentHost.ToString("X") + ")");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("TryAttach failed for " + _cabinet.ToString("X"), ex);
                SafeTeardownHost();
                return false;
            }
        }

        private void NavigateInThisWindow(string path)
        {
            App.Recents?.RecordNavigation(path);
            if (_navOverride != null) _navOverride(path);
            else ExplorerNavigator.NavigateAsync(_cabinet, path); // never block the input-attached UI thread
        }

        private void OnSettingsChanged(object sender, EventArgs e)
        {
            int w = ClampWidth(App.Settings.Current.SidebarWidthPx);
            if (w != _width)
            {
                _width = w;
                ApplyLayout();
            }
        }

        /// <summary>Reposition the content host and the sidebar. Called on attach and on relayout.</summary>
        public void ApplyLayout()
        {
            if (_applying || _host == null) return;
            if (!NM.IsWindow(_cabinet) || !NM.IsWindow(_contentHost)) return;

            NM.RECT client;
            if (!NM.GetClientRect(_cabinet, out client)) return;
            int w = client.Width;
            int h = client.Height;
            int width = EffWidth();
            if (w <= width + 40 || h <= 0) return; // too small to split sanely

            int top = MeasureContentTop(client);

            // Hold everything until the window is actually presented and its toolbar has laid out.
            // Attaching mid-load measured contentTop as 0, which placed the strip over the toolbar and
            // shifted the content away while the strip's own window was still hidden, leaving the blank
            // gap on the left. Require the cabinet visible and a real content top, with a time fallback
            // so a window that never reports a toolbar still attaches.
            if (!_ready)
            {
                if (!NM.IsWindowVisible(_cabinet)) return;
                if (top <= 0 && Environment.TickCount - _attachTick < 5000) return;
                _ready = true;
                // Place the strip before revealing it, so it never flashes at its creation spot.
                NM.SetWindowPos(_host.Handle, NM.HWND_TOP, 0, top, width, Math.Max(0, h - top), NM.SWP_NOACTIVATE);
                NM.ShowWindow(_host.Handle, NM.SW_SHOWNA);
                try { OnFirstShown?.Invoke(); } catch { } // brief hold-burst while Explorer settles the new window
            }

            _contentTop = top;
            int contentH = h - top;

            int sidebarX = 0, sidebarY = top, sidebarW = width, sidebarH = contentH;
            int contentX = width, contentY = top, contentW = w - width, contentH2 = contentH;

            // Skip if already where we want it (within tolerance) so we do not fight Explorer's own
            // identical relayout and create a feedback loop.
            if (ContentAlreadyPlaced(contentX, contentY, contentW, contentH2)) return;

            bool verbose = _logCount < 3;
            if (verbose)
            {
                _logCount++;
                Log.Info("ApplyLayout cabinet=" + _cabinet.ToString("X") + " client=" + w + "x" + h +
                         " contentTop=" + top + " sidebar=(" + sidebarX + "," + sidebarY + " " + sidebarW + "x" + sidebarH +
                         ") content=(" + contentX + "," + contentY + " " + contentW + "x" + contentH2 + ")");
            }

            _applying = true;
            try
            {
                // Two independent SetWindowPos calls, NOT a DeferWindowPos batch. DeferWindowPos
                // cannot combine windows from different threads, and Explorer's content host lives in
                // another process, so a batch silently fails and nothing moves. Separate calls each
                // succeed: the content host shifts right, and our sidebar is forced to the TOP so
                // Explorer's content can never paint over it (SetParent leaves it at the bottom).
                NM.SetWindowPos(_contentHost, IntPtr.Zero, contentX, contentY, contentW, contentH2,
                    NM.SWP_NOZORDER | NM.SWP_NOACTIVATE | NM.SWP_DEFERERASE);
                NM.SetWindowPos(_host.Handle, NM.HWND_TOP, sidebarX, sidebarY, sidebarW, sidebarH,
                    NM.SWP_NOACTIVATE | NM.SWP_DEFERERASE);
            }
            catch (Exception ex)
            {
                Log.Error("ApplyLayout failed", ex);
            }
            finally
            {
                _applying = false;
            }

            if (verbose)
            {
                NM.RECT hr, cr;
                NM.GetWindowRect(_host.Handle, out hr);
                NM.GetWindowRect(_contentHost, out cr);
                Log.Info("  post-layout hostHwnd=" + _host.Handle.ToString("X") +
                         " hostScreenRect=(" + hr.Left + "," + hr.Top + " " + hr.Width + "x" + hr.Height + ")" +
                         " hostVisible=" + NM.IsWindowVisible(_host.Handle) +
                         " hostParentIsCabinet=" + (NM.GetParent(_host.Handle) == _cabinet) +
                         " | contentScreenRect=(" + cr.Left + "," + cr.Top + " " + cr.Width + "x" + cr.Height + ")");
            }
        }

        public bool IsApplying { get { return _applying; } }

        /// <summary>
        /// Hold the content pane and sidebar in place using only Win32 calls, safe to invoke from the
        /// background enforcer thread. It reads nothing from WPF, acts only when something has drifted,
        /// and corrects Explorer's activation reset within a couple milliseconds so the move is never
        /// presented to the screen.
        /// </summary>
        public void EnforceFast()
        {
            var host = _hostHandle;
            if (_disposed || host == IntPtr.Zero) return;
            if (!_ready) return; // nothing is shifted or shown until the window is presented
            if (_applying) return;
            if (!NM.IsWindow(_cabinet) || !NM.IsWindow(_contentHost)) return;

            NM.RECT client;
            if (!NM.GetClientRect(_cabinet, out client)) return;
            int w = client.Width, h = client.Height;
            int width = EffWidth(), top = _contentTop;
            if (w <= width + 40 || h <= 0) return;
            int contentH = h - top;

            if (!RectMatches(_contentHost, width, top, w - width, contentH))
            {
                NM.SetWindowPos(_contentHost, IntPtr.Zero, width, top, w - width, contentH,
                    NM.SWP_NOZORDER | NM.SWP_NOACTIVATE | NM.SWP_DEFERERASE);
            }
            if (!RectMatches(host, 0, top, width, contentH))
            {
                NM.SetWindowPos(host, NM.HWND_TOP, 0, top, width, contentH,
                    NM.SWP_NOACTIVATE | NM.SWP_DEFERERASE);
            }
        }

        // True if hwnd already sits at (x,y,cx,cy) in cabinet client coordinates, within a 2px slop.
        private bool RectMatches(IntPtr hwnd, int x, int y, int cx, int cy)
        {
            NM.RECT r;
            if (!NM.GetWindowRect(hwnd, out r)) return false;
            var tl = new NM.POINT { X = r.Left, Y = r.Top };
            NM.ScreenToClient(_cabinet, ref tl);
            return Near(tl.X, x) && Near(tl.Y, y) && Near(r.Width, cx) && Near(r.Height, cy);
        }

        private bool ContentAlreadyPlaced(int x, int y, int cx, int cy)
        {
            NM.RECT r;
            if (!NM.GetWindowRect(_contentHost, out r)) return false;
            var tl = new NM.POINT { X = r.Left, Y = r.Top };
            NM.ScreenToClient(_cabinet, ref tl);
            return Near(tl.X, x) && Near(tl.Y, y) && Near(r.Width, cx) && Near(r.Height, cy);
        }

        private static bool Near(int a, int b) { return Math.Abs(a - b) <= 2; }

        // Content host top in cabinet client coordinates (below the toolbar/ribbon band).
        private int MeasureContentTop(NM.RECT client)
        {
            try
            {
                NM.RECT r;
                if (NM.GetWindowRect(_contentHost, out r))
                {
                    var tl = new NM.POINT { X = r.Left, Y = r.Top };
                    NM.ScreenToClient(_cabinet, ref tl);
                    if (tl.Y >= 0 && tl.Y < client.Height) return tl.Y;
                }
            }
            catch { }
            return 0;
        }

        /// <summary>Find the child window that hosts the folder content, so we can shift it right.</summary>
        private static IntPtr FindContentHost(IntPtr cabinet)
        {
            // Win10 folder windows nest the content in ShellTabWindowClass.
            var tab = NM.FindWindowEx(cabinet, IntPtr.Zero, "ShellTabWindowClass", null);
            if (tab != IntPtr.Zero) return tab;

            // Fallback: the largest direct child that is not our own sidebar.
            IntPtr best = IntPtr.Zero;
            long bestArea = 0;
            NM.EnumChildWindows(cabinet, (h, l) =>
            {
                if (NM.GetParent(h) != cabinet) return true; // direct children only
                var cls = NM.ClassOf(h);
                if (cls == "QuickPaneSidebar") return true;
                NM.RECT rr;
                if (NM.GetWindowRect(h, out rr))
                {
                    long area = (long)Math.Max(0, rr.Width) * Math.Max(0, rr.Height);
                    if (area > bestArea) { bestArea = area; best = h; }
                }
                return true;
            }, IntPtr.Zero);
            return best;
        }

        private static int ClampWidth(int w)
        {
            if (w < 160) return 160;
            if (w > 400) return 400;
            return w;
        }

        // ---- teardown --------------------------------------------------------

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (App.Settings != null)
            {
                App.Settings.Changed -= OnSettingsChanged;
                App.Settings.WidthChangedLive -= OnSettingsChanged;
            }

            // Restore Explorer's content host to full width so the window looks normal afterwards.
            try
            {
                if (_ready && NM.IsWindow(_cabinet) && NM.IsWindow(_contentHost))
                {
                    NM.RECT client;
                    if (NM.GetClientRect(_cabinet, out client))
                    {
                        int top = _contentTop;
                        NM.SetWindowPos(_contentHost, IntPtr.Zero, 0, top, client.Width, client.Height - top,
                            NM.SWP_NOZORDER | NM.SWP_NOACTIVATE);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("restore content host failed", ex);
            }

            SafeTeardownHost();
        }

        private void SafeTeardownHost()
        {
            _hostHandle = IntPtr.Zero; // stop the enforcer touching this window first
            try { _host?.Dispose(); } catch (Exception ex) { Log.Error("host dispose", ex); }
            _host = null;
            _sidebar = null;
        }
    }
}
