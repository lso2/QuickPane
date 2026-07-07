using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;
using QuickPane.Interop;
using QuickPane.Services;
using NM = QuickPane.Interop.NativeMethods;

namespace QuickPane.Explorer
{
    /// <summary>
    /// Attaches a QuickPane to file Open/Save common dialogs, honouring the same Pane Mode as real
    /// Explorer windows. Inside mode reparents the pane into the dialog and shifts its content; Beside
    /// mode places a follower pane against the dialog's edge; Off attaches nothing. Detection works on
    /// both the classic and the newer dialogs (a #32770 carrying a File name ComboBoxEx32), and the
    /// dialog is driven through DialogNavigator rather than the Explorer Shell automation.
    /// </summary>
    internal sealed class DialogPaneWatcher : IDisposable
    {
        private readonly Dictionary<IntPtr, FollowerPane> _beside = new Dictionary<IntPtr, FollowerPane>();
        private readonly Dictionary<IntPtr, DialogInsidePane> _inside = new Dictionary<IntPtr, DialogInsidePane>();
        private WinEventHook _life, _loc, _fg;
        private DispatcherTimer _sweep;
        private string _mode = "inside";

        // Per-HWND detection results. Without this, every foreground change and every sweep pass
        // re-enumerated the child tree of every open dialog in every app (Photoshop's dialogs have
        // dozens of children), on the UI thread. "Not a dialog" verdicts are re-checked a few times
        // shortly after, because dialogs build their controls lazily, then trusted; a "dialog"
        // verdict is stable. Entries drop when the window is destroyed, which also covers HWND reuse.
        private sealed class Verdict { public bool IsDialog; public int Tick; public int Rechecks; }
        private readonly Dictionary<IntPtr, Verdict> _verdicts = new Dictionary<IntPtr, Verdict>();
        private readonly HashSet<IntPtr> _loggedUnmatched = new HashSet<IntPtr>();

        private bool IsFileDialogCached(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;
            Verdict v;
            if (_verdicts.TryGetValue(hwnd, out v))
            {
                if (v.IsDialog) return true;
                // Lazily built dialogs: give a "no" up to 6 more chances over the first seconds.
                if (v.Rechecks >= 6 || Environment.TickCount - v.Tick < 400) return false;
                v.Rechecks++;
                v.Tick = Environment.TickCount;
                v.IsDialog = DialogNavigator.IsFileDialog(hwnd);
                return v.IsDialog;
            }
            if (NM.ClassOf(hwnd) != "#32770")
            {
                // Cheap class miss: cache as final so plain windows are never enumerated at all.
                _verdicts[hwnd] = new Verdict { IsDialog = false, Tick = Environment.TickCount, Rechecks = 6 };
                return false;
            }
            var isDialog = DialogNavigator.IsFileDialog(hwnd);
            _verdicts[hwnd] = new Verdict { IsDialog = isDialog, Tick = Environment.TickCount };
            return isDialog;
        }

        private void ForgetWindow(IntPtr hwnd)
        {
            _verdicts.Remove(hwnd);
            _loggedUnmatched.Remove(hwnd);
        }

        public void Start()
        {
            _mode = CurrentMode();

            _life = new WinEventHook(NM.EVENT_OBJECT_CREATE, NM.EVENT_OBJECT_SHOW);
            _life.Event += OnLife; _life.Install();
            _loc = new WinEventHook(NM.EVENT_OBJECT_LOCATIONCHANGE, NM.EVENT_OBJECT_LOCATIONCHANGE);
            _loc.Event += OnLoc; _loc.Install();
            _fg = new WinEventHook(NM.EVENT_SYSTEM_FOREGROUND, NM.EVENT_SYSTEM_FOREGROUND);
            _fg.Event += OnFg; _fg.Install();

            // A dialog's controls are not all present at creation, so a light sweep catches the ones the
            // create/show events miss, reapplies inside-mode layout, and prunes closed dialogs. The
            // verdict cache keeps each pass cheap, so the interval mostly affects late attachment of a
            // dialog whose SHOW event was missed; the FOREGROUND hook already covers the common case.
            _sweep = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
            _sweep.Tick += (s, e) => Rescan();
            _sweep.Start();

            if (App.Settings != null) App.Settings.Changed += OnSettings;

            Log.Info("DialogPaneWatcher started. mode=" + _mode);
            Rescan();
        }

        private static string CurrentMode()
        {
            string m = App.Settings != null ? (App.Settings.Current.Mode ?? "inside") : "inside";
            m = m.Trim().ToLowerInvariant();
            if (m != "beside" && m != "off") m = "inside";
            return m;
        }

        private void OnSettings(object sender, EventArgs e)
        {
            string m = CurrentMode();
            if (m == _mode) return; // width and other changes are handled by the panes themselves
            _mode = m;
            TeardownAll();          // the next sweep rebuilds every open dialog under the new mode
            Rescan();
        }

        private bool IsOwnPane(IntPtr h)
        {
            foreach (var kv in _beside) if (kv.Value.Handle == h) return true;
            return false;
        }

        private bool Attached(IntPtr hwnd) { return _beside.ContainsKey(hwnd) || _inside.ContainsKey(hwnd); }

        private void OnLife(uint evt, IntPtr hwnd, int idObject, int idChild, uint thread)
        {
            if (idObject != NM.OBJID_WINDOW || idChild != 0) return;
            if (evt == NM.EVENT_OBJECT_DESTROY) { ForgetWindow(hwnd); Detach(hwnd); return; }
            // Probe on SHOW only: at CREATE the children are not built yet, so every #32770 anywhere
            // cost a full (and useless) child enumeration just to say "not yet".
            if (evt != NM.EVENT_OBJECT_SHOW) return;
            if (!IsOwnPane(hwnd) && IsFileDialogCached(hwnd)) Attach(hwnd);
        }

        private void OnLoc(uint evt, IntPtr hwnd, int idObject, int idChild, uint thread)
        {
            if (idObject != NM.OBJID_WINDOW) return;
            if (_inside.TryGetValue(hwnd, out var win)) { win.Relayout(); if (win.Failed) DemoteToBeside(hwnd); }
            else if (_beside.TryGetValue(hwnd, out var pane)) PositionBeside(hwnd, pane);
        }

        // Attach a file dialog immediately when it gains focus. Log an unmatched #32770 once (with its
        // child tree) so any unfamiliar picker is easy to diagnose, without flooding the log.
        private void OnFg(uint evt, IntPtr hwnd, int idObject, int idChild, uint thread)
        {
            if (idObject != NM.OBJID_WINDOW || idChild != 0 || hwnd == IntPtr.Zero) return;
            if (IsOwnPane(hwnd)) return;
            try
            {
                if (IsFileDialogCached(hwnd)) { Attach(hwnd); return; }
                // Diagnose unfamiliar pickers once per window, not on every focus change: the same
                // Photoshop dialog used to dump its child tree into the log a dozen times a session.
                if (NM.ClassOf(hwnd) == "#32770" && _loggedUnmatched.Add(hwnd))
                    Log.Info("FG #32770 not matched as file dialog; children: " + DumpChildren(hwnd));
            }
            catch (Exception ex) { Log.Error("dialog fg", ex); }
        }

        // Apps whose dialogs fought the inside shift once. Their future dialogs go straight to beside, so
        // a known-hostile app (Photoshop) never flashes the inside attempt again this session.
        private static readonly System.Collections.Generic.HashSet<uint> _besideProcs = new System.Collections.Generic.HashSet<uint>();

        private void Attach(IntPtr hwnd)
        {
            if (_mode == "off" || Attached(hwnd)) return;
            try
            {
                uint pid; NM.GetWindowThreadProcessId(hwnd, out pid);
                bool forceBeside = pid != 0 && (_besideProcs.Contains(pid) || IsKnownHostile(pid));

                // Classic comdlg32 dialogs are laid out by comdlg32 and break if reparented or shifted,
                // and apps that fought the shift before are remembered, so both use the beside follower.
                // Everything else honours the pane mode.
                // Navigation drives the foreign dialog with window messages; run it on the worker so
                // a busy host app (Photoshop mid-save) can never stall our input-attached UI thread.
                Action<string> nav = p => WorkQueue.Post(() => DialogNavigator.Navigate(hwnd, p));
                if (_mode == "beside" || forceBeside || DialogNavigator.IsClassicDialog(hwnd))
                {
                    var pane = new FollowerPane(hwnd, nav);
                    _beside[hwnd] = pane;
                    PositionBeside(hwnd, pane);
                }
                else
                {
                    var win = new DialogInsidePane(hwnd, nav);
                    if (win.TryAttach()) _inside[hwnd] = win;
                    else win.Dispose(); // controls not ready yet; the sweep retries
                }
            }
            catch (Exception ex) { Log.Error("attach dialog pane", ex); }
        }

        private void Detach(IntPtr hwnd)
        {
            if (_beside.TryGetValue(hwnd, out var pane)) { _beside.Remove(hwnd); try { pane.Close(); } catch { } }
            if (_inside.TryGetValue(hwnd, out var win)) { _inside.Remove(hwnd); try { win.Dispose(); } catch { } }
        }

        private void PositionBeside(IntPtr hwnd, FollowerPane pane)
        {
            if (!NM.IsWindow(hwnd) || !NM.IsWindowVisible(hwnd) || NM.IsIconic(hwnd)) { pane.Hide(); return; }
            NM.RECT r;
            if (NM.DwmGetWindowAttribute(hwnd, NM.DWMWA_EXTENDED_FRAME_BOUNDS, out r, Marshal.SizeOf(typeof(NM.RECT))) != 0)
            {
                if (!NM.GetWindowRect(hwnd, out r)) return;
            }
            pane.PositionBeside(r, WidthPx());
        }

        private static int WidthPx()
        {
            int w = App.Settings != null ? App.Settings.Current.SidebarWidthPx : 220;
            if (w < 160) w = 160; if (w > 400) w = 400;
            return w;
        }

        private void Rescan()
        {
            if (_mode != "off")
            {
                NM.EnumWindows((hwnd, l) =>
                {
                    if (NM.IsWindowVisible(hwnd) && !Attached(hwnd) && IsFileDialogCached(hwnd))
                        Attach(hwnd);
                    return true;
                }, IntPtr.Zero);
            }

            var dead = new List<IntPtr>();
            foreach (var kv in _beside) if (!NM.IsWindow(kv.Key)) dead.Add(kv.Key);
            foreach (var kv in _inside) if (!NM.IsWindow(kv.Key)) dead.Add(kv.Key);
            foreach (var h in dead) Detach(h);

            // Drop cached verdicts for windows that no longer exist, so the map cannot grow without
            // bound and a recycled HWND value is never trusted with a stale answer.
            if (_verdicts.Count > 0)
            {
                List<IntPtr> gone = null;
                foreach (var kv in _verdicts)
                    if (!NM.IsWindow(kv.Key)) (gone ?? (gone = new List<IntPtr>())).Add(kv.Key);
                if (gone != null) foreach (var h in gone) ForgetWindow(h);
            }

            foreach (var kv in _beside) PositionBeside(kv.Key, kv.Value);
            foreach (var kv in _inside) kv.Value.Relayout();

            // Hand any dialog that fought the inside shift over to the non-invasive beside follower.
            List<IntPtr> failed = null;
            foreach (var kv in _inside) if (kv.Value.Failed) (failed ?? (failed = new List<IntPtr>())).Add(kv.Key);
            if (failed != null) foreach (var h in failed) DemoteToBeside(h);
        }

        // Apps known to run their own dialog layout, which fights the inside shift. Their dialogs skip
        // the inside attempt entirely so there is never a flash, on top of the learned set above.
        private static bool IsKnownHostile(uint pid)
        {
            try
            {
                var name = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName;
                return name != null && name.IndexOf("photoshop", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch { return false; }
        }

        private void DemoteToBeside(IntPtr hwnd)
        {
            if (_inside.TryGetValue(hwnd, out var win)) { _inside.Remove(hwnd); try { win.Dispose(); } catch { } }
            // Remember the app so its later dialogs skip the inside attempt entirely.
            uint pid; NM.GetWindowThreadProcessId(hwnd, out pid);
            if (pid != 0) _besideProcs.Add(pid);
            if (!NM.IsWindow(hwnd) || _beside.ContainsKey(hwnd)) return;
            var pane = new FollowerPane(hwnd, p => WorkQueue.Post(() => DialogNavigator.Navigate(hwnd, p)));
            _beside[hwnd] = pane;
            PositionBeside(hwnd, pane);
            Log.Info("dialog moved to beside (inside reparent fought back) " + hwnd.ToString("X"));
        }

        private static string DumpChildren(IntPtr root)
        {
            var sb = new StringBuilder();
            int count = 0;
            NM.EnumChildWindows(root, (h, l) =>
            {
                sb.Append(NM.ClassOf(h)).Append(" | ");
                return ++count < 80;
            }, IntPtr.Zero);
            return sb.ToString();
        }

        private void TeardownAll()
        {
            foreach (var kv in _beside) { try { kv.Value.Close(); } catch { } }
            foreach (var kv in _inside) { try { kv.Value.Dispose(); } catch { } }
            _beside.Clear();
            _inside.Clear();
        }

        public void Dispose()
        {
            try { _sweep?.Stop(); } catch { }
            if (App.Settings != null) App.Settings.Changed -= OnSettings;
            try { _life?.Dispose(); } catch { }
            try { _loc?.Dispose(); } catch { }
            try { _fg?.Dispose(); } catch { }
            TeardownAll();
        }
    }
}
