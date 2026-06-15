using System;
using System.Windows.Threading;
using QuickPane.Interop;
using QuickPane.Services;
using NM = QuickPane.Interop.NativeMethods;

namespace QuickPane.Explorer
{
    /// <summary>
    /// Records the folder of whatever Explorer window the user is browsing, so Recent reflects real
    /// usage and not only folders opened through QuickPane. It listens for window activation and title
    /// changes (which fire when an Explorer window navigates) and records the current path.
    /// </summary>
    internal sealed class RecentTracker : IDisposable
    {
        private WinEventHook _fg, _name;
        private DispatcherTimer _poll;

        public void Start()
        {
            _fg = new WinEventHook(NM.EVENT_SYSTEM_FOREGROUND, NM.EVENT_SYSTEM_FOREGROUND);
            _fg.Event += OnEvt; _fg.Install();
            _name = new WinEventHook(NM.EVENT_OBJECT_NAMECHANGE, NM.EVENT_OBJECT_NAMECHANGE);
            _name.Event += OnEvt; _name.Install();

            // The activation and title-change hooks miss some in-window navigations, so a light poll of
            // the foreground Explorer window catches those. RecordNavigation already skips repeats.
            _poll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
            _poll.Tick += (s, e) => OnEvt(0, NM.GetForegroundWindow(), NM.OBJID_WINDOW, 0, 0);
            _poll.Start();

            var fg = NM.GetForegroundWindow();
            OnEvt(0, fg, NM.OBJID_WINDOW, 0, 0); // record whatever is already in front
        }

        private void OnEvt(uint evt, IntPtr hwnd, int idObject, int idChild, uint thread)
        {
            if (idObject != NM.OBJID_WINDOW || idChild != 0 || hwnd == IntPtr.Zero) return;
            var c = NM.ClassOf(hwnd);
            if (c != "CabinetWClass" && c != "ExploreWClass") return;
            try
            {
                var path = ExplorerNavigator.GetCurrentPath(hwnd);
                if (!string.IsNullOrEmpty(path)) App.Recents?.RecordNavigation(path);
            }
            catch (Exception ex) { Log.Error("recent track", ex); }
        }

        public void Dispose()
        {
            try { _poll?.Stop(); } catch { }
            try { _fg?.Dispose(); } catch { }
            try { _name?.Dispose(); } catch { }
        }
    }
}
