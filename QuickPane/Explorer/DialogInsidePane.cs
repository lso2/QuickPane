using System;
using System.Collections.Generic;
using System.Windows.Interop;
using System.Windows.Media;
using QuickPane.Interop;
using QuickPane.Services;
using QuickPane.UI;
using NM = QuickPane.Interop.NativeMethods;

namespace QuickPane.Explorer
{
    /// <summary>
    /// Inside-mode pane for a file dialog. A dialog is a flat set of controls (address bar, nav pane,
    /// file view, File name box, buttons) rather than one content host, so shifting a single child the
    /// way Explorer does leaves the rest overlapping the pane. This widens the dialog by the pane width
    /// and shifts every direct child right by that amount, clearing a strip on the left for a reparented
    /// pane. Original positions are stored so the shift is reapplied idempotently and fully restored on
    /// teardown.
    /// </summary>
    internal sealed class DialogInsidePane : IDisposable
    {
        private readonly IntPtr _dlg;
        private readonly Action<string> _navigate;
        private readonly Dictionary<IntPtr, NM.RECT> _orig = new Dictionary<IntPtr, NM.RECT>();
        private HwndSource _host;
        private SidebarControl _sidebar;
        private int _width;
        private int _origWinW, _origWinH;
        private bool _attached, _disposed;
        private bool _applying, _disabled, _shiftedOnce;
        private int _relayoutCount, _windowStart;
        private int _lastW, _lastH;      // dialog outer size at the previous relayout pass
        private int _attachTick;         // settle window for controls that are built lazily
        private int _lastFullPass;       // throttles full passes triggered by pure moves/sweeps

        /// <summary>Set when the dialog fought the inside shift (reset its own size), so the watcher can
        /// drop this dialog to the non-invasive beside follower instead.</summary>
        public bool Failed { get; private set; }

        public bool Attached { get { return _attached; } }

        public DialogInsidePane(IntPtr dlg, Action<string> navigate)
        {
            _dlg = dlg;
            _navigate = navigate;
            _width = ClampWidth(App.Settings != null ? App.Settings.Current.SidebarWidthPx : 220);
        }

        public bool TryAttach()
        {
            if (_disposed || _attached) return _attached;
            if (!NM.IsWindow(_dlg)) return false;

            NM.RECT win;
            if (!NM.GetWindowRect(_dlg, out win)) return false;
            _origWinW = win.Width; _origWinH = win.Height;

            CaptureChildren();
            if (_orig.Count == 0) return false; // controls not built yet; caller retries

            try
            {
                // Widen the dialog by the pane width (grow the right edge), then push every child right so
                // the left strip is free. The full-width file view ends up filling the wider area exactly.
                NM.SetWindowPos(_dlg, IntPtr.Zero, 0, 0, _origWinW + _width, _origWinH,
                    NM.SWP_NOMOVE | NM.SWP_NOZORDER | NM.SWP_NOACTIVATE);
                ShiftChildren();

                NM.RECT client; NM.GetClientRect(_dlg, out client);
                var p = new HwndSourceParameters("QuickPaneSidebar")
                {
                    ParentWindow = _dlg,
                    WindowStyle = unchecked((int)(NM.WS_CHILD | NM.WS_VISIBLE | NM.WS_CLIPSIBLINGS | NM.WS_CLIPCHILDREN)),
                    ExtendedWindowStyle = unchecked((int)NM.WS_EX_NOACTIVATE),
                    PositionX = 0, PositionY = 0, Width = _width, Height = client.Height
                };
                _sidebar = new SidebarControl();
                _sidebar.Attach(_navigate);
                _host = new HwndSource(p);
                _host.RootVisual = _sidebar;
                bool dark = App.Theme != null && App.Theme.IsDark;
                _host.CompositionTarget.BackgroundColor = dark ? Color.FromRgb(0x20, 0x20, 0x20) : Color.FromRgb(0xF3, 0xF3, 0xF3);
                NM.ShowWindow(_host.Handle, NM.SW_SHOWNA);

                _attached = true;
                _shiftedOnce = true;
                _attachTick = Environment.TickCount;
                _lastW = _origWinW + _width;
                _lastH = _origWinH;
                Log.Info("dialog inside pane attached to " + _dlg.ToString("X"));
                return true;
            }
            catch (Exception ex)
            {
                // The dialog can close (or its HwndSource die) between widening and hosting; put the
                // children and the dialog size back so a failed attach never leaves a shifted wreck.
                Log.Error("DialogInsidePane attach", ex);
                Restore();
                return false;
            }
        }

        private void CaptureChildren()
        {
            _orig.Clear();
            NM.EnumChildWindows(_dlg, (h, l) =>
            {
                if (NM.GetParent(h) != _dlg) return true;       // direct children only
                if (NM.ClassOf(h) == "QuickPaneSidebar") return true;
                NM.RECT r;
                if (NM.GetWindowRect(h, out r))
                {
                    var tl = new NM.POINT { X = r.Left, Y = r.Top };
                    NM.ScreenToClient(_dlg, ref tl);
                    _orig[h] = new NM.RECT { Left = tl.X, Top = tl.Y, Right = tl.X + r.Width, Bottom = tl.Y + r.Height };
                }
                return true;
            }, IntPtr.Zero);
        }

        // Capture controls that appeared after attach (some dialogs build controls lazily, so they were
        // not in the first pass and would otherwise sit unshifted, peeking out from under the pane).
        private void CaptureNewChildren()
        {
            NM.EnumChildWindows(_dlg, (h, l) =>
            {
                if (NM.GetParent(h) != _dlg) return true;
                if (_orig.ContainsKey(h) || NM.ClassOf(h) == "QuickPaneSidebar") return true;
                NM.RECT r;
                if (NM.GetWindowRect(h, out r))
                {
                    var tl = new NM.POINT { X = r.Left, Y = r.Top };
                    NM.ScreenToClient(_dlg, ref tl);
                    _orig[h] = new NM.RECT { Left = tl.X, Top = tl.Y, Right = tl.X + r.Width, Bottom = tl.Y + r.Height };
                }
                return true;
            }, IntPtr.Zero);
        }

        // Only move a control that is not already at its shifted position. Re-setting a window to the
        // place it already occupies still emits a location-change event, and that event is what fed the
        // oscillation; skipping no-op moves stops the feedback once the layout has settled.
        private void ShiftChildren()
        {
            foreach (var kv in _orig)
            {
                if (!NM.IsWindow(kv.Key)) continue;
                var r = kv.Value;
                int tx = r.Left + _width, ty = r.Top;
                NM.RECT cur;
                if (NM.GetWindowRect(kv.Key, out cur))
                {
                    var tl = new NM.POINT { X = cur.Left, Y = cur.Top };
                    NM.ScreenToClient(_dlg, ref tl);
                    if (Math.Abs(tl.X - tx) <= 2 && Math.Abs(tl.Y - ty) <= 2 &&
                        Math.Abs(cur.Width - r.Width) <= 2 && Math.Abs(cur.Height - r.Height) <= 2)
                        continue; // already placed
                }
                NM.SetWindowPos(kv.Key, IntPtr.Zero, tx, ty, r.Width, r.Height,
                    NM.SWP_NOZORDER | NM.SWP_NOACTIVATE);
            }
        }

        /// <summary>Reapply the shift and keep the pane full height. The dialog is widened once at attach
        /// and never re-widened here, because re-widening fought dialogs (such as Photoshop's Save) that
        /// reset their own size, which oscillated many times a second.</summary>
        public void Relayout()
        {
            if (!_attached || _disposed || _applying || _host == null || !NM.IsWindow(_dlg)) return;

            NM.RECT wr;
            if (!NM.GetWindowRect(_dlg, out wr)) return;
            bool sizeChanged = wr.Width != _lastW || wr.Height != _lastH;
            _lastW = wr.Width; _lastH = wr.Height;

            int now = Environment.TickCount;
            bool settling = now - _attachTick < 2000; // lazily-built controls appear shortly after attach

            // Dragging the dialog fires a location event per pixel, but its controls are positioned
            // in client coordinates and move with it, so a pure move needs no work at all. Running a
            // full pass anyway used to trip the back-off after ~1.5 s of dragging, permanently
            // disabling maintenance for that dialog, which is what glitched Save dialogs afterwards.
            // Pure moves and sweep ticks get at most one full pass per 1.2 s as a safety net.
            if (!sizeChanged && !settling && now - _lastFullPass < 1200) return;
            _lastFullPass = now;

            // A real size change is new information, so a backed-off dialog gets another chance.
            if (_disabled)
            {
                if (!sizeChanged) return;
                _disabled = false;
                _relayoutCount = 0;
            }

            // Detect the dialog re-laying out the controls we shifted. Photoshop's Save rebuilds itself on
            // navigation and moves its controls back over our pane (it does not change the window width).
            // If most controls are no longer where we put them, the dialog runs its own layout and inside
            // mode cannot hold, so restore it and let the watcher hand it to the beside follower.
            if (_shiftedOnce)
            {
                int off = 0, total = 0;
                foreach (var kv in _orig)
                {
                    if (!NM.IsWindow(kv.Key)) continue;
                    NM.RECT cr;
                    if (!NM.GetWindowRect(kv.Key, out cr)) continue;
                    var pt = new NM.POINT { X = cr.Left, Y = cr.Top };
                    NM.ScreenToClient(_dlg, ref pt);
                    total++;
                    if (Math.Abs(pt.X - (kv.Value.Left + _width)) > 3) off++; // not at the shifted target
                }
                bool widthReset = wr.Width < _origWinW + _width - 10;
                if ((total > 0 && off > total / 2) || widthReset)
                {
                    Failed = true;
                    Restore();
                    return;
                }
            }

            // Back off if a dialog keeps fighting the layout, so a stubborn dialog can never loop forever.
            if (now - _windowStart > 1500) { _windowStart = now; _relayoutCount = 0; }
            if (++_relayoutCount > 12) { _disabled = true; Log.Info("DialogInsidePane backed off " + _dlg.ToString("X")); return; }

            _applying = true;
            try { CaptureNewChildren(); ShiftChildren(); }
            finally { _applying = false; }

            NM.RECT client;
            if (NM.GetClientRect(_dlg, out client))
                NM.SetWindowPos(_host.Handle, NM.HWND_TOP, 0, 0, _width, client.Height,
                    NM.SWP_NOACTIVATE);
        }

        // Put every control back, return the dialog to its original size, and remove the pane host. Used
        // both on teardown and when the dialog is handed off to the beside follower.
        private void Restore()
        {
            try
            {
                if (NM.IsWindow(_dlg))
                {
                    foreach (var kv in _orig)
                    {
                        if (!NM.IsWindow(kv.Key)) continue;
                        var r = kv.Value;
                        NM.SetWindowPos(kv.Key, IntPtr.Zero, r.Left, r.Top, r.Width, r.Height,
                            NM.SWP_NOZORDER | NM.SWP_NOACTIVATE);
                    }
                    NM.SetWindowPos(_dlg, IntPtr.Zero, 0, 0, _origWinW, _origWinH,
                        NM.SWP_NOMOVE | NM.SWP_NOZORDER | NM.SWP_NOACTIVATE);
                }
            }
            catch (Exception ex) { Log.Error("DialogInsidePane restore", ex); }
            try { _host?.Dispose(); } catch { }
            _host = null; _sidebar = null;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Restore();
            _orig.Clear();
        }

        private static int ClampWidth(int w) { return w < 160 ? 160 : (w > 400 ? 400 : w); }
    }
}
