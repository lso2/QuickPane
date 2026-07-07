using System;
using System.Collections.Concurrent;
using System.Threading;

namespace QuickPane.Services
{
    /// <summary>
    /// One shared background STA worker for everything that can touch a slow disk, the network, or
    /// cross-process COM: shortcut resolution, Shell.Application navigation, icon extraction, and
    /// existence probes. This matters because in Inside mode the UI thread is input-attached to
    /// Explorer (and to any app hosting a file dialog we embedded in), so a single blocking call on
    /// the UI thread stalls input for the whole shell even though CPU stays idle. Posting here means
    /// the only thread that ever waits on I/O is this one.
    /// </summary>
    internal static class WorkQueue
    {
        private static readonly BlockingCollection<Action> Queue = new BlockingCollection<Action>();
        private static Thread _thread;
        private static readonly object Gate = new object();

        /// <summary>Queue work on the worker thread. Never blocks the caller.</summary>
        public static void Post(Action work)
        {
            if (work == null) return;
            EnsureThread();
            try { Queue.Add(work); }
            catch (InvalidOperationException) { /* shutting down */ }
        }

        /// <summary>Run on the WPF UI thread without waiting for it.</summary>
        public static void PostUI(Action work)
        {
            if (work == null) return;
            var app = System.Windows.Application.Current;
            var disp = app != null ? app.Dispatcher : null;
            if (disp == null || disp.HasShutdownStarted) return;
            disp.BeginInvoke(work);
        }

        private static void EnsureThread()
        {
            if (_thread != null) return;
            lock (Gate)
            {
                if (_thread != null) return;
                var t = new Thread(Loop) { IsBackground = true, Name = "QuickPaneWorker" };
                // STA so the shell COM used here (IShellLink, Shell.Application, SHGetFileInfo)
                // behaves the way it does on the UI thread.
                t.SetApartmentState(ApartmentState.STA);
                t.Start();
                _thread = t;
            }
        }

        private static void Loop()
        {
            foreach (var work in Queue.GetConsumingEnumerable())
            {
                try { work(); }
                catch (Exception ex) { Log.Error("background work failed", ex); }
            }
        }
    }
}
