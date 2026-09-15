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
        private readonly string _hostApp;
        private readonly Dictionary<IntPtr, NM.RECT> _orig = new Dictionary<IntPtr, NM.RECT>();
        private HwndSource _host;
        private SidebarControl _sidebar;
        private int _width;
        private int _origWinW, _origWinH;
        private int _origWinX, _origWinY;
        private bool _moved;       // the dialog was nudged to keep the pane on screen
        private bool _attached, _disposed;
        private bool _applying, _shiftedOnce;
        private int _relayoutCount, _windowStart;
        private int _lastW, _lastH;      // dialog outer size at the previous relayout pass
        private int _attachTick;         // settle window for controls that are built lazily
        private int _lastFullPass;       // throttles full passes triggered by pure moves/sweeps

        /// <summary>Set when the dialog fought the inside shift (reset its own size), so the watcher can
        /// drop this dialog to the non-invasive beside follower instead.</summary>
        public bool Failed { get; private set; }

        public bool Attached { get { return _attached; } }

        /// <summary>The hosted pane window, for moving keyboard focus into it.</summary>
        public IntPtr PaneHandle { get { return _host != null ? _host.Handle : IntPtr.Zero; } }

        public SidebarControl Sidebar { get { return _sidebar; } }

        public DialogInsidePane(IntPtr dlg, Action<string> navigate, string hostApp)
        {
            _dlg = dlg;
            _navigate = navigate;
            _hostApp = hostApp;
            _width = ClampWidth(App.Settings != null ? App.Settings.Current.SidebarWidthPx : 220);
        }

        public bool TryAttach()
        {
            if (_disposed || _attached) return _attached;
            if (!NM.IsWindow(_dlg)) return false;

            NM.RECT win;
            if (!NM.GetWindowRect(_dlg, out win)) return false;
            _origWinW = win.Width; _origWinH = win.Height;
            _origWinX = win.Left; _origWinY = win.Top;

            // The pane occupies a strip at the dialog's left, so it is only reachable when the dialog
            // sits inside the monitor's work area. A dialog opened hard against the left edge left that
            // strip off screen, which reads as the pane simply never appearing.
            var wa = NM.WorkAreaFor(_dlg);
            if (wa.Width <= 0 || wa.Height <= 0) return false;
            int room = wa.Width - _origWinW;
            if (_width > room) _width = room;
            if (_width > wa.Width / 3) _width = wa.Width / 3;
            if (_width < 160)
            {
                // Widening would push the dialog's own buttons off the screen. The beside follower moves
                // the dialog aside instead of reshaping it, so hand it over.
                Failed = true;
                Log.Info("inside pane does not fit beside the " + QuickPane.Interop.DialogNavigator.Owner(_dlg) +
                    " dialog " + _dlg.ToString("X") + " on a " + wa.Width + "px work area, so it goes beside.");
                return false;
            }

            CaptureChildren();
            if (_orig.Count == 0) return false; // controls not built yet; caller retries

            // Reparenting a WPF host into another process's window is the least forgiving thing the app
            // does, so the breadcrumb names it and the app it is doing it to. A termination that never
            // reaches a managed handler still leaves this in the heartbeat.
            Log.Activity("attaching inside pane to the " + QuickPane.Interop.DialogNavigator.Owner(_dlg) +
                " dialog " + _dlg.ToString("X"));

            try
            {
                // Widen the dialog by the pane width (grow the right edge), then push every child right so
                // the left strip is free. The full-width file view ends up filling the wider area exactly.
                int newW = _origWinW + _width;
                int left = win.Left, top = win.Top;
                if (left + newW > wa.Right) left = wa.Right - newW;
                if (left < wa.Left) left = wa.Left;
                if (top + _origWinH > wa.Bottom) top = wa.Bottom - _origWinH;
                if (top < wa.Top) top = wa.Top;
                _moved = left != win.Left || top != win.Top;

                NM.SetWindowPos(_dlg, IntPtr.Zero, left, top, newW, _origWinH,
                    NM.SWP_NOZORDER | NM.SWP_NOACTIVATE);

                ShiftChildren();
                Repaint();

                NM.RECT client; NM.GetClientRect(_dlg, out client);
                var p = new HwndSourceParameters("QuickPaneSidebar")
                {
                    ParentWindow = _dlg,
                    WindowStyle = unchecked((int)(NM.WS_CHILD | NM.WS_VISIBLE | NM.WS_CLIPSIBLINGS | NM.WS_CLIPCHILDREN)),
                    ExtendedWindowStyle = unchecked((int)NM.WS_EX_NOACTIVATE),
                    PositionX = 0, PositionY = 0, Width = _width, Height = client.Height
                };
                _sidebar = new SidebarControl();
                _sidebar.HostApp = _hostApp;   // set before Attach, which is what builds the sections
                _sidebar.Attach(_navigate);
                _host = new HwndSource(p);
                // HwndSource swallows a failed CreateWindowEx and disposes itself, so the object can come
                // back already dead. Assigning RootVisual to that throws, which is every caught exception
                // this method has ever logged.
                if (_host.IsDisposed || _host.Handle == IntPtr.Zero)
                {
                    Log.Info("dialog inside pane host window was not created for " + _dlg.ToString("X") + "; retrying later.");
                    Restore();
                    return false;
                }
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
                Log.Activity("idle");
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

        /// <summary>True for the pane's own host window. HwndSourceParameters takes a window NAME, not a
        /// class name, so the host's class is WPF's generated HwndWrapper[...] and comparing the class
        /// against "QuickPaneSidebar" never matched. The pane was therefore captured as one of the
        /// dialog's controls and shifted right along with them every time the layout was reapplied.</summary>
        private bool IsOurPane(IntPtr h)
        {
            if (_host != null && h == _host.Handle) return true;
            return NM.TextOf(h) == "QuickPaneSidebar";
        }

        /// <summary>Ask the dialog to repaint itself and all its children.
        ///
        /// Moving another process's controls with SetWindowPos does not invalidate the area they came
        /// from, so whatever was drawn there stays on screen until something else paints over it. After
        /// a resize that leaves pieces of the pane stranded across the dialog's file list, which is the
        /// ghost text that appears over the rows.</summary>
        private void Repaint()
        {
            if (!NM.IsWindow(_dlg)) return;
            NM.RedrawWindow(_dlg, IntPtr.Zero, IntPtr.Zero,
                NM.RDW_INVALIDATE | NM.RDW_ERASE | NM.RDW_ALLCHILDREN | NM.RDW_UPDATENOW);
        }

        private void CaptureChildren()
        {
            _orig.Clear();
            NM.EnumChildWindows(_dlg, (h, l) =>
            {
                if (NM.GetParent(h) != _dlg) return true;       // direct children only
                if (IsOurPane(h)) return true;
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
                if (_orig.ContainsKey(h) || IsOurPane(h)) return true;
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
                // Any width other than the one set at attach means the dialog is no longer the size the
                // captured positions were recorded against, whether the dialog reset it or the user
                // dragged the corner. Those positions then describe nothing, and forcing the controls
                // back to them is what drew the buttons twice and left the bottom row misplaced until
                // the dialog was resized again. Only a narrower dialog used to be caught here.
                bool widthOff = Math.Abs(wr.Width - (_origWinW + _width)) > 4;
                if ((total > 0 && off > total / 2) || widthOff)
                {
                    // Give up on the first pass and hand the dialog to the beside follower, which never
                    // touches its controls. Retrying holds the dialog in a broken state for as long as
                    // the fight lasts; the follower costs a pane position and nothing else.
                    Failed = true;
                    Restore();
                    return;
                }
            }

            // A dialog that keeps putting its controls back owns its own layout, and no number of further
            // passes will change that. Pausing maintenance here left the pane sitting inside the dialog
            // while the dialog's content returned to where it wanted it, so the pane covered the file
            // list's first column and the controls beneath it. Hand it over instead, which is the same
            // outcome the position test above reaches for dialogs that move their controls back in one
            // go rather than by oscillating.
            if (now - _windowStart > 1500) { _windowStart = now; _relayoutCount = 0; }
            if (++_relayoutCount > 12)
            {
                Failed = true;
                Restore();
                Log.Event("glitch", "the " + QuickPane.Interop.DialogNavigator.Owner(_dlg) + " dialog " +
                    _dlg.ToString("X") + " moved its controls back " + _relayoutCount +
                    " times in under 1.5 s, so it was handed to the beside follower.");
                return;
            }

            _applying = true;
            try { CaptureNewChildren(); ShiftChildren(); }
            finally { _applying = false; }

            NM.RECT client;
            if (NM.GetClientRect(_dlg, out client))
                NM.SetWindowPos(_host.Handle, NM.HWND_TOP, 0, 0, _width, client.Height,
                    NM.SWP_NOACTIVATE);

            Repaint();
        }

        // Put every control back, return the dialog to its original size, and remove the pane host. Used
        // both on teardown and when the dialog is handed off to the beside follower.
        private void Restore()
        {
            Log.Activity("restoring the " + QuickPane.Interop.DialogNavigator.Owner(_dlg) + " dialog " +
                _dlg.ToString("X") + " to its own layout");
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
                    uint flags = NM.SWP_NOZORDER | NM.SWP_NOACTIVATE | (_moved ? 0 : NM.SWP_NOMOVE);
                    NM.SetWindowPos(_dlg, IntPtr.Zero, _origWinX, _origWinY, _origWinW, _origWinH, flags);
                }
            }
            catch (Exception ex) { Log.Error("DialogInsidePane restore", ex); }
            try { Repaint(); } catch { }
            try { _sidebar?.Detach(); } catch (Exception ex) { Log.Error("sidebar detach", ex); }
            try { _host?.Dispose(); } catch { }
            _host = null; _sidebar = null;
            Log.Activity("idle");
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
