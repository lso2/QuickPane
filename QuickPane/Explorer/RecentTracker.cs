using System;
using System.Windows.Threading;
using QuickPane.Interop;
using QuickPane.Services;
using NM = QuickPane.Interop.NativeMethods;

namespace QuickPane.Explorer
{
    /// <summary>
    /// Records the folder of whatever Explorer window the user is browsing, so Recent reflects real
    /// usage and not only folders opened through QuickPane. It reacts to window activation and runs a
    /// slow safety poll. There is deliberately no NAMECHANGE hook: that event fires for nearly every
    /// control text change on the desktop, and answering each one with a cross-process Shell COM
    /// enumeration stalled the input queue the UI thread shares with Explorer. The COM query itself
    /// always runs on the background worker, never on the UI thread.
    /// </summary>
    internal sealed class RecentTracker : IDisposable
    {
        private WinEventHook _fg;
        private DispatcherTimer _poll;
        private volatile bool _busy;    // one COM query in flight at a time
        private IntPtr _lastHwnd;
        private string _lastPath;

        public void Start()
        {
            _fg = new WinEventHook(NM.EVENT_SYSTEM_FOREGROUND, NM.EVENT_SYSTEM_FOREGROUND);
            _fg.Event += OnEvt; _fg.Install();

            // In-window navigations do not change the foreground window, so a light poll of the
            // foreground Explorer window catches those. RecordNavigation already skips repeats.
            _poll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2000) };
            _poll.Tick += (s, e) => Track(NM.GetForegroundWindow());
            _poll.Start();

            Track(NM.GetForegroundWindow()); // record whatever is already in front
        }

        private void OnEvt(uint evt, IntPtr hwnd, int idObject, int idChild, uint thread)
        {
            if (idObject != NM.OBJID_WINDOW || idChild != 0 || hwnd == IntPtr.Zero) return;
            NoteApp(hwnd);
            Track(hwnd);
        }

        // Which program was just switched to. Reading the path is a pair of handle calls, so it happens
        // inline; deciding whether it belongs on the list, and writing the file, happens on the worker.
        private void NoteApp(IntPtr hwnd)
        {
            if (App.RecentApps == null) return;
            if (hwnd == _lastAppHwnd) return;
            _lastAppHwnd = hwnd;
            try
            {
                var exe = NM.ExePathOf(hwnd);
                if (string.IsNullOrEmpty(exe)) return;
                WorkQueue.Post(() =>
                {
                    try { App.RecentApps.RecordRunning(exe); }
                    catch (Exception ex) { Log.Error("note the app in front", ex); }
                });
            }
            catch (Exception ex) { Log.Error("read the app in front", ex); }
        }

        private IntPtr _lastAppHwnd;

        private void Track(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || _busy) return;
            var c = NM.ClassOf(hwnd);
            if (c != "CabinetWClass" && c != "ExploreWClass") return;

            _busy = true;
            WorkQueue.Post(() =>
            {
                try
                {
                    var path = ExplorerNavigator.GetCurrentPath(hwnd);
                    if (!string.IsNullOrEmpty(path) &&
                        !(hwnd == _lastHwnd && string.Equals(path, _lastPath, StringComparison.OrdinalIgnoreCase)))
                    {
                        _lastHwnd = hwnd;
                        _lastPath = path;
                        App.Recents?.RecordNavigation(path);
                    }
                }
                catch (Exception ex) { Log.Error("recent track", ex); }
                finally { _busy = false; }
            });
        }

        public void Dispose()
        {
            try { _poll?.Stop(); } catch { }
            try { _fg?.Dispose(); } catch { }
        }
    }
}
