// ExplorerWindow.cs
// Purpose: one Explorer window's embedded pane: a WPF HwndSource reparented into CabinetWClass, with every content host shifted right.
// Reason: inside mode draws the pane in Explorer's own window, and Explorer resets its content hosts to full width on every relayout.
// Defines: ExplorerWindow.
// Origin: the Windows 11 tab handling is ported from jprisant's fork, commits 8842577 and 05ef6f3 (MIT, see NOTICE.md).
// Doc: docs/notes/QuickPane/Explorer/ExplorerWindow.cs.md
using System;
using System.Collections.Generic;
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
    /// The embedded sidebar for a single Explorer window. A WPF HwndSource is reparented as a child of
    /// the CabinetWClass window, and Explorer's content hosts (one ShellTabWindowClass per tab on
    /// Windows 11) are shifted right by the sidebar width. Explorer puts its content hosts back at full
    /// width on every resize, and the layout is reapplied each time.
    /// </summary>
    internal sealed class ExplorerWindow : IDisposable
    {
        private const string TabClass = "ShellTabWindowClass";

        private readonly IntPtr _cabinet;
        private readonly Action<string> _navOverride;
        private IntPtr _contentHost;      // the host found at attach, and the only host in the fallback layout
        private bool _tabbed;             // true when the content is in ShellTabWindowClass children
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
        public bool Attached { get { return _host != null; } }

        /// <summary>True when hwnd is one of this window's content hosts, including a tab opened after
        /// attach. The watcher calls this only for children of this cabinet, and the class lookup is not
        /// run for location events from other windows.</summary>
        public bool IsContentHost(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;
            if (hwnd == _contentHost) return true;
            return _tabbed && NM.GetParent(hwnd) == _cabinet && NM.ClassOf(hwnd) == TabClass;
        }

        /// <summary>
        /// Walk this window's content hosts: IntPtr.Zero for the first, then the previous host. Windows 11
        /// gives every Explorer tab its own ShellTabWindowClass child, all visible, with the active tab on
        /// top. Shifting only the one found at attach left every other tab at full width, over or under
        /// the pane (lso2/QuickPane#3). Tabs open and close during the window's lifetime, and the walk
        /// runs live on every pass. FindWindowEx allocates nothing, and the enforcer thread walks at its burst
        /// rate. A tab closed mid-walk ends the walk early, and every tab is shifted on the next pass.
        /// </summary>
        private IntPtr NextContentHost(IntPtr after)
        {
            if (_tabbed) return NM.FindWindowEx(_cabinet, after, TabClass, null);
            return after == IntPtr.Zero && NM.IsWindow(_contentHost) ? _contentHost : IntPtr.Zero;
        }

        // The hosts of one ApplyLayout pass, walked once and reused to measure, check and move.
        // Used on the UI thread only. EnforceFast on the enforcer thread walks the hosts live.
        private readonly List<IntPtr> _passHosts = new List<IntPtr>(4);
        private bool _inPass;

        private void CollectContentHosts(List<IntPtr> into)
        {
            into.Clear();
            for (var c = NextContentHost(IntPtr.Zero); c != IntPtr.Zero; c = NextContentHost(c)) into.Add(c);
        }

        /// <summary>Put the keyboard in this window's pane, for the keyboard navigation shortcut. A
        /// collapsed strip opens first.</summary>
        public bool FocusPane()
        {
            if (_sidebar == null || !_ready) return false;
            if (_collapsed) ToggleCollapsed();
            return _sidebar.FocusPane();
        }

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
            _tabbed = NM.ClassOf(_contentHost) == TabClass;

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

        /// <summary>Reposition the content hosts and the sidebar. Called on attach and on relayout.</summary>
        public void ApplyLayout()
        {
            // A re-entered pass would refill _passHosts while the first pass still reads it. The next
            // event or sweep catches up.
            if (_inPass) return;
            _inPass = true;
            try { ApplyLayoutPass(); }
            finally { _inPass = false; }
        }

        private void ApplyLayoutPass()
        {
            if (_applying || _host == null) return;
            if (!NM.IsWindow(_cabinet)) return;
            // A tab that closes after this walk makes only its own SetWindowPos fail, and the next pass
            // walks again.
            CollectContentHosts(_passHosts);
            if (_passHosts.Count == 0) return;

            NM.RECT client;
            if (!NM.GetClientRect(_cabinet, out client)) return;
            int w = client.Width;
            int h = client.Height;
            int width = EffWidth();
            if (w <= width + 40 || h <= 0) return; // too small to split sanely

            int top = MeasureContentTop(client, _passHosts);

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

            // Skip when every host is already within tolerance. Moving a host that is already placed
            // starts a feedback loop with Explorer's own identical relayout. Each of our own moves raises
            // a location event, and the ApplyLayout call from that event returns here.
            if (AllContentPlaced(_passHosts, contentX, contentY, contentW, contentH2)) return;

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
                // Independent SetWindowPos calls, not a DeferWindowPos batch. DeferWindowPos cannot
                // combine windows from different threads. Explorer's content hosts belong to another
                // process, and a batch fails silently with nothing moved. With separate calls, every host out
                // of place is moved right, and the sidebar is placed at the top of the z-order above
                // Explorer's content. SetParent puts the sidebar at the bottom of the z-order.
                foreach (var c in _passHosts)
                {
                    if (!NeedsMove(c, contentX, contentY, contentW, contentH2)) continue;
                    NM.SetWindowPos(c, IntPtr.Zero, contentX, contentY, contentW, contentH2,
                        NM.SWP_NOZORDER | NM.SWP_NOACTIVATE | NM.SWP_DEFERERASE);
                }
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
                NM.GetWindowRect(_passHosts[0], out cr);
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
            if (!NM.IsWindow(_cabinet)) return;

            NM.RECT client;
            if (!NM.GetClientRect(_cabinet, out client)) return;
            int w = client.Width, h = client.Height;
            int width = EffWidth(), top = _contentTop;
            if (w <= width + 40 || h <= 0) return;
            int contentH = h - top;

            for (var c = NextContentHost(IntPtr.Zero); c != IntPtr.Zero; c = NextContentHost(c))
            {
                if (!NeedsMove(c, width, top, w - width, contentH)) continue;
                NM.SetWindowPos(c, IntPtr.Zero, width, top, w - width, contentH,
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

        // True when a content host is somewhere other than (x,y,cx,cy). A tab Explorer has not sized yet
        // is skipped. Its first layout raises a location event, and ApplyLayout runs again from that event.
        private bool NeedsMove(IntPtr hwnd, int x, int y, int cx, int cy)
        {
            NM.RECT r;
            if (!NM.GetWindowRect(hwnd, out r) || r.Width <= 0 || r.Height <= 0) return false;
            var tl = new NM.POINT { X = r.Left, Y = r.Top };
            NM.ScreenToClient(_cabinet, ref tl);
            return !(Near(tl.X, x) && Near(tl.Y, y) && Near(r.Width, cx) && Near(r.Height, cy));
        }

        private bool AllContentPlaced(List<IntPtr> hosts, int x, int y, int cx, int cy)
        {
            foreach (var c in hosts)
            {
                if (NeedsMove(c, x, y, cx, cy)) return false;
            }
            return true;
        }

        private static bool Near(int a, int b) { return Math.Abs(a - b) <= 2; }

        // Content top in cabinet client coordinates (below the toolbar or ribbon), shared by every tab.
        // The largest reading is used. A tab Explorer is still creating can be at the client's top edge
        // for a moment, and using a 0 read from it would place the pane and every tab over the toolbar.
        private int MeasureContentTop(NM.RECT client)
        {
            var hosts = new List<IntPtr>(4);
            CollectContentHosts(hosts);
            return MeasureContentTop(client, hosts);
        }

        private int MeasureContentTop(NM.RECT client, List<IntPtr> hosts)
        {
            int best = 0;
            try
            {
                foreach (var c in hosts)
                {
                    NM.RECT r;
                    if (!NM.GetWindowRect(c, out r) || r.Width <= 0 || r.Height <= 0) continue;
                    var tl = new NM.POINT { X = r.Left, Y = r.Top };
                    NM.ScreenToClient(_cabinet, ref tl);
                    if (tl.Y >= 0 && tl.Y < client.Height && tl.Y > best) best = tl.Y;
                }
            }
            catch { }
            return best;
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

            // Every content host goes back to full width, and the window looks as it did before the pane.
            try
            {
                if (_ready && NM.IsWindow(_cabinet))
                {
                    NM.RECT client;
                    if (NM.GetClientRect(_cabinet, out client))
                    {
                        int top = _contentTop;
                        for (var c = NextContentHost(IntPtr.Zero); c != IntPtr.Zero; c = NextContentHost(c))
                        {
                            NM.SetWindowPos(c, IntPtr.Zero, 0, top, client.Width, client.Height - top,
                                NM.SWP_NOZORDER | NM.SWP_NOACTIVATE);
                        }
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
            try { _sidebar?.Detach(); } catch (Exception ex) { Log.Error("sidebar detach", ex); }
            try { _host?.Dispose(); } catch (Exception ex) { Log.Error("host dispose", ex); }
            _host = null;
            _sidebar = null;
        }
    }
}
