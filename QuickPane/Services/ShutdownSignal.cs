using System;
using System.Diagnostics;
using System.Threading;

namespace QuickPane.Services
{
    /// <summary>
    /// Lets one copy of the executable ask the running tray instance to shut down in an orderly way.
    ///
    /// Before the watchdog existed, an installer or uninstaller could force-kill QuickPane and nothing
    /// noticed. Now a force kill is indistinguishable from a crash, so the watchdog answers it with a
    /// relaunch in the middle of the install it was killed for. A named event gives the outside world a
    /// way to reach the same exit path the tray menu uses, which stamps the heartbeat clean and leaves
    /// the watchdog with nothing to recover.
    ///
    /// The waiting half is deliberately synchronous so the installer can block on the process until the
    /// app and its watchdog are both gone and the files are free.
    /// </summary>
    internal static class ShutdownSignal
    {
        public const string Verb = "--exit";

        /// <summary>Auto-reset so a single Set wakes exactly one waiter, which is all there ever is.</summary>
        private const string EventName = "QuickPane.ExitSignal.{3D9A2B1C}";

        private static EventWaitHandle _handle;
        private static Thread _listener;
        private static volatile bool _stop;

        /// <summary>Create the event and wait on it. The callback is invoked on a background thread, so a
        /// caller that needs the UI thread has to marshal there itself.</summary>
        public static void Listen(Action onRequested)
        {
            if (onRequested == null || _listener != null) return;
            try
            {
                bool created;
                _handle = new EventWaitHandle(false, EventResetMode.AutoReset, EventName, out created);
            }
            catch (Exception ex)
            {
                Log.Error("create the shutdown signal", ex);
                return;
            }

            _stop = false;
            _listener = new Thread(() =>
            {
                try
                {
                    // A bounded wait rather than an infinite one, so Stop is never left racing a thread
                    // parked forever on a handle it is about to close.
                    while (!_stop)
                    {
                        if (!_handle.WaitOne(500)) continue;
                        if (_stop) return;
                        Log.Event("session", "another copy of the executable asked for a clean shutdown.");
                        onRequested();
                        return;
                    }
                }
                catch (Exception ex) { Log.Error("shutdown signal listener", ex); }
            })
            { IsBackground = true, Name = "QuickPaneShutdownSignal" };
            _listener.Start();
        }

        public static void Stop()
        {
            _stop = true;
            try { if (_handle != null) _handle.Set(); } catch { }
            try { if (_handle != null) _handle.Close(); } catch { }
            _handle = null;
            _listener = null;
        }

        /// <summary>Ask the running instance to exit and wait for it, and for the watchdog it leaves
        /// behind, to be gone. True when nothing is left running, which is what tells an installer the
        /// files are free.</summary>
        public static bool RequestExit(TimeSpan wait)
        {
            EventWaitHandle h;
            try
            {
                h = EventWaitHandle.OpenExisting(EventName);
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                Log.Info("no running QuickPane to shut down.");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("open the shutdown signal", ex);
                return false;
            }

            var deadline = DateTime.UtcNow + wait;
            try
            {
                h.Set();
                Log.Info("clean shutdown requested of the running instance.");
                while (DateTime.UtcNow < deadline && CrashGuard.InstanceHeld()) Thread.Sleep(150);
            }
            finally { try { h.Close(); } catch { } }

            if (CrashGuard.InstanceHeld())
            {
                Log.Event("session", "a shutdown was requested but the tray instance still held the " +
                    "single-instance lock after " + Math.Round(wait.TotalSeconds) + " s.");
                return false;
            }

            // The watchdog outlives the app it watches by a moment while it reads the heartbeat, and an
            // installer that copies over the executable in that moment fails on a locked file.
            while (DateTime.UtcNow < deadline && OtherInstances() > 0) Thread.Sleep(150);

            int left = OtherInstances();
            if (left > 0)
            {
                Log.Event("session", left + " QuickPane process(es) were still running " +
                    Math.Round(wait.TotalSeconds) + " s after a clean shutdown was requested.");
                return false;
            }

            Log.Info("the running QuickPane shut down on request.");
            return true;
        }

        /// <summary>QuickPane processes other than this one, which during an exit request means the tray
        /// app, its watchdog, or both.</summary>
        private static int OtherInstances()
        {
            try
            {
                int self = Process.GetCurrentProcess().Id;
                int n = 0;
                foreach (var p in Process.GetProcessesByName("QuickPane"))
                {
                    try { if (p.Id != self && !p.HasExited) n++; } catch { }
                    finally { try { p.Dispose(); } catch { } }
                }
                return n;
            }
            catch { return 0; }
        }
    }
}
