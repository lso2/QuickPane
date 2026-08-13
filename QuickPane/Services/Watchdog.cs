using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace QuickPane.Services
{
    /// <summary>
    /// Out-of-process crash recovery. The tray app launches a second copy of its own executable with
    /// "--watchdog &lt;pid&gt;", and that copy does nothing but wait for the first to exit.
    ///
    /// It lives outside the app because the terminations recorded in the journal never reached a managed
    /// handler, so anything hosted inside the process would die with it. The watchdog owns no windows,
    /// no tray icon, and no hooks, which keeps it to a few megabytes and gives the fault almost nothing
    /// to land on.
    ///
    /// On the parent's exit it reads the heartbeat CrashGuard maintains. A clean stamp means the user
    /// asked the app to close, so the watchdog exits quietly. Anything else is a crash: it is written to
    /// events.log with the exit code and the app is relaunched, subject to the restart budget that stops
    /// a fault which reappears on every launch from cycling forever.
    /// </summary>
    internal static class Watchdog
    {
        public const string Verb = "--watchdog";
        private const int RestartDelayMs = 2500;

        /// <summary>Start the companion process that watches this one. Failure is logged and ignored,
        /// because the app is fully usable without recovery.</summary>
        public static void Launch()
        {
            try
            {
                var exe = Log.ExePath();
                if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
                {
                    Log.Event("crash", "watchdog not started because the executable path could not be resolved.");
                    return;
                }

                var psi = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = Verb + " " + Process.GetCurrentProcess().Id,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    WorkingDirectory = Path.GetDirectoryName(exe) ?? Environment.CurrentDirectory
                };
                var p = Process.Start(psi);
                Log.Info("watchdog started as pid " + (p != null ? p.Id.ToString() : "unknown") + ".");
            }
            catch (Exception ex) { Log.Event("crash", "watchdog could not be started", ex); }
        }

        /// <summary>Body of the "--watchdog" run mode. Blocks on a worker until the watched process is
        /// gone, then either exits quietly or restarts the app, and finally calls onDone so the hosting
        /// Application can shut itself down.</summary>
        public static void Run(int watchedPid, Action onDone)
        {
            var t = new Thread(() =>
            {
                try { Watch(watchedPid); }
                catch (Exception ex) { Log.Event("crash", "watchdog failed", ex); }
                finally { try { onDone(); } catch { } }
            })
            { IsBackground = true, Name = "QuickPaneWatchdog" };
            t.Start();
        }

        private static void Watch(int watchedPid)
        {
            Process watched = null;
            try { watched = Process.GetProcessById(watchedPid); }
            catch
            {
                Log.Info("watchdog found no process " + watchedPid + " to watch, so it is exiting.");
                return;
            }

            Log.Info("watchdog is watching pid " + watchedPid + ".");
            watched.WaitForExit();

            int exitCode = 0;
            try { exitCode = watched.ExitCode; } catch { }

            // Give the exit path a moment to finish stamping the heartbeat, so an orderly shutdown that
            // is still writing is never read as a crash.
            Thread.Sleep(600);

            var state = CrashGuard.ReadState();
            if (state.Valid && state.Clean)
            {
                Log.Info("watched pid " + watchedPid + " exited cleanly, so the watchdog is exiting.");
                return;
            }

            var detail = state.Valid
                ? "last heartbeat " + state.Beat.ToString("yyyy-MM-dd HH:mm:ss.fff") +
                  ", last activity \"" + state.Activity + "\""
                : "no readable heartbeat";
            Log.Event("crash", "watched pid " + watchedPid + " terminated with exit code " + exitCode +
                " and no clean-exit stamp. " + detail + ".");

            Restart();
        }

        private static void Restart()
        {
            var exe = Log.ExePath();
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
            {
                Log.Event("restart", "skipped because the executable is gone, which is what an uninstall or an update in progress looks like.");
                return;
            }

            int recent;
            if (!CrashGuard.RestartAllowed(out recent))
            {
                Log.Event("restart", "suppressed after " + recent + " restarts within " +
                    CrashGuard.RestartWindow.TotalMinutes + " minutes, because a fault that returns on every launch would otherwise cycle without end. " +
                    "Start QuickPane from the Start menu once the cause is addressed.");
                return;
            }

            Thread.Sleep(RestartDelayMs);

            // A restart is pointless if something already brought the app back, which happens when the
            // user relaunches it during the delay above.
            if (CrashGuard.InstanceHeld())
            {
                Log.Info("restart skipped because QuickPane is running again already.");
                return;
            }

            try
            {
                CrashGuard.NoteRestart();
                Process.Start(new ProcessStartInfo
                {
                    FileName = exe,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(exe) ?? Environment.CurrentDirectory
                });
                Log.Event("restart", "QuickPane relaunched after a crash. This was restart " + (recent + 1) +
                    " of " + CrashGuard.MaxRestarts + " allowed in the current window.");
            }
            catch (Exception ex) { Log.Event("restart", "relaunch failed", ex); }
        }
    }
}
