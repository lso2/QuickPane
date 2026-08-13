using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;

namespace QuickPane.Services
{
    /// <summary>
    /// Records what the running session is doing and decides, on the next start, whether the previous
    /// session ended cleanly.
    ///
    /// The app already logs unhandled managed exceptions, yet the journal shows sessions that simply
    /// stop with no such record, which means the terminations that matter are not reaching a managed
    /// handler at all. Detection therefore cannot live only inside the process: a heartbeat file carries
    /// the last known state to disk every few seconds, the exit path stamps it clean, and whatever reads
    /// it afterwards can tell an orderly exit from a hard kill without needing the dead process.
    ///
    /// Two readers use that file. The next start reports the previous session into events.log, and the
    /// Watchdog companion process reacts the moment the app disappears.
    /// </summary>
    internal static class CrashGuard
    {
        private const string StateFileName = "session.state";
        private const string RestartFileName = "restarts.state";
        private const int HeartbeatMs = 4000;

        /// <summary>Name of the mutex the tray instance claims. Held here rather than in App because the
        /// watchdog also tests it to tell a live tray app from its own kind.</summary>
        public const string InstanceMutexName = "QuickPane.SingleInstance.{3D9A2B1C}";

        /// <summary>An auto restart is allowed only if fewer than this many happened in the window
        /// below, so a fault that reappears immediately on launch cannot spin the machine.</summary>
        public const int MaxRestarts = 3;
        public static readonly TimeSpan RestartWindow = TimeSpan.FromMinutes(10);

        private static Thread _beat;
        private static volatile bool _stop;
        private static DateTime _started;
        private static string _stateDir;

        // Only the process that opened the session may stamp the heartbeat clean. Pin mode and the
        // watchdog both reach the same exit path without ever calling BeginSession, and a stamp from
        // either would mark the live tray app's heartbeat as an orderly exit, which is exactly the
        // signal the watchdog reads to decide a crash needs no recovery.
        private static bool _owned;
        private static bool _marked;

        public static string StateDir
        {
            get
            {
                if (_stateDir != null) return _stateDir;
                _stateDir = Log.Folder ?? Path.GetTempPath();
                return _stateDir;
            }
        }

        public static string StatePath { get { return Path.Combine(StateDir, StateFileName); } }
        private static string RestartPath { get { return Path.Combine(StateDir, RestartFileName); } }

        /// <summary>One session's last known state, as read back from the heartbeat file.</summary>
        public sealed class SessionRecord
        {
            public int Pid;
            public string Session = "";
            public string Version = "";
            public DateTime Started;
            public DateTime Beat;
            public string Activity = "";
            public DateTime ActivityAt;
            public bool Clean;
            public bool Valid;
        }

        // ---- startup -------------------------------------------------------

        /// <summary>Read the heartbeat left by the previous run, report it, then take the file over for
        /// this run. Called once, before any other service starts.</summary>
        public static void BeginSession()
        {
            _started = DateTime.Now;
            _owned = true;
            _marked = false;

            var prev = ReadState();
            if (prev.Valid && !prev.Clean && !IsStillRunning(prev))
            {
                var gap = prev.Beat > DateTime.MinValue ? (DateTime.Now - prev.Beat) : TimeSpan.Zero;
                Log.Event("crash",
                    "previous session " + prev.Session + " (pid " + prev.Pid + ", version " + prev.Version +
                    ") ended without a clean exit. started " + Stamp(prev.Started) +
                    ", last heartbeat " + Stamp(prev.Beat) + " (" + Math.Round(gap.TotalMinutes, 1) + " min ago)" +
                    ", last activity \"" + prev.Activity + "\" at " + Stamp(prev.ActivityAt) +
                    ", uptime " + Describe(prev.Beat - prev.Started) + ".");
            }
            else if (prev.Valid && prev.Clean)
            {
                Log.Info("previous session " + prev.Session + " exited cleanly at " + Stamp(prev.Beat) + ".");
            }

            Log.Event("session", "started. version " + Log.AppVersion() + ", pid " +
                Process.GetCurrentProcess().Id + ", exe " + (Log.ExePath() ?? "unknown") + ".");

            WriteState(false);
            StartHeartbeat();
        }

        private static void StartHeartbeat()
        {
            if (_beat != null) return;
            _stop = false;
            _beat = new Thread(HeartbeatLoop) { IsBackground = true, Name = "QuickPaneHeartbeat" };
            _beat.Start();
        }

        private static void HeartbeatLoop()
        {
            while (!_stop)
            {
                try { WriteState(false); } catch { }
                for (int i = 0; i < HeartbeatMs / 250 && !_stop; i++) Thread.Sleep(250);
            }
        }

        /// <summary>Stamp the heartbeat clean so neither the watchdog nor the next start treats this exit
        /// as a crash. Called from the app's exit path, and again from the tray menu's Restart, so the
        /// second call is a no-op rather than a duplicate journal line.</summary>
        public static void MarkCleanExit()
        {
            if (!_owned || _marked) return;
            _marked = true;
            _stop = true;
            try { WriteState(true); } catch { }
            Log.Event("session", "exited cleanly after " + Describe(DateTime.Now - _started) + ".");
        }

        // ---- heartbeat file -------------------------------------------------

        private static void WriteState(bool clean)
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("pid=" + Process.GetCurrentProcess().Id);
                sb.AppendLine("session=" + Log.SessionId);
                sb.AppendLine("version=" + Log.AppVersion());
                sb.AppendLine("started=" + Stamp(_started));
                sb.AppendLine("beat=" + Stamp(DateTime.Now));
                sb.AppendLine("activity=" + Sanitize(Log.LastActivity));
                sb.AppendLine("activityAt=" + Stamp(Log.LastActivityAt));
                sb.AppendLine("clean=" + (clean ? "1" : "0"));

                // Write beside the target then replace, so a termination during the write cannot leave a
                // half-written file that reads as a corrupt session.
                var tmp = StatePath + ".tmp";
                File.WriteAllText(tmp, sb.ToString());
                if (File.Exists(StatePath)) File.Delete(StatePath);
                File.Move(tmp, StatePath);
            }
            catch { }
        }

        public static SessionRecord ReadState()
        {
            var r = new SessionRecord();
            try
            {
                if (!File.Exists(StatePath)) return r;
                foreach (var raw in File.ReadAllLines(StatePath))
                {
                    int eq = raw.IndexOf('=');
                    if (eq <= 0) continue;
                    var key = raw.Substring(0, eq);
                    var val = raw.Substring(eq + 1);
                    switch (key)
                    {
                        case "pid": int.TryParse(val, out r.Pid); break;
                        case "session": r.Session = val; break;
                        case "version": r.Version = val; break;
                        case "started": r.Started = Parse(val); break;
                        case "beat": r.Beat = Parse(val); break;
                        case "activity": r.Activity = val; break;
                        case "activityAt": r.ActivityAt = Parse(val); break;
                        case "clean": r.Clean = val == "1"; break;
                    }
                }
                r.Valid = r.Pid != 0;
            }
            catch (Exception ex) { Log.Error("read session state", ex); }
            return r;
        }

        /// <summary>True while a tray instance holds the single-instance lock. Counting processes named
        /// QuickPane would also count watchdogs and one-shot verbs, whereas only the tray app ever claims
        /// this mutex, so this is the one honest answer to "is QuickPane running".</summary>
        public static bool InstanceHeld()
        {
            try
            {
                using (var m = Mutex.OpenExisting(InstanceMutexName)) { return m != null; }
            }
            catch (WaitHandleCannotBeOpenedException) { return false; }
            catch { return false; }
        }

        /// <summary>True when a live process still holds the recorded pid and looks like QuickPane, which
        /// keeps a recycled pid from being mistaken for the app that wrote the heartbeat.</summary>
        private static bool IsStillRunning(SessionRecord r)
        {
            try
            {
                if (r.Pid == 0) return false;
                var p = Process.GetProcessById(r.Pid);
                return p != null && !p.HasExited &&
                       string.Equals(p.ProcessName, "QuickPane", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        // ---- restart budget --------------------------------------------------

        /// <summary>True when another automatic restart is within budget. Recording it is the caller's
        /// job through NoteRestart, so a caller that decides not to restart does not spend the budget.</summary>
        public static bool RestartAllowed(out int recent)
        {
            recent = RecentRestarts().Count;
            return recent < MaxRestarts;
        }

        public static void NoteRestart()
        {
            try
            {
                var list = RecentRestarts();
                list.Add(DateTime.Now);
                var sb = new System.Text.StringBuilder();
                foreach (var t in list) sb.AppendLine(Stamp(t));
                File.WriteAllText(RestartPath, sb.ToString());
            }
            catch (Exception ex) { Log.Error("note restart", ex); }
        }

        private static List<DateTime> RecentRestarts()
        {
            var list = new List<DateTime>();
            try
            {
                if (!File.Exists(RestartPath)) return list;
                var cutoff = DateTime.Now - RestartWindow;
                foreach (var line in File.ReadAllLines(RestartPath))
                {
                    var t = Parse(line);
                    if (t > cutoff) list.Add(t);
                }
            }
            catch { }
            return list;
        }

        // ---- helpers ---------------------------------------------------------

        private static string Stamp(DateTime t)
        {
            return t == DateTime.MinValue ? "" : t.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
        }

        private static DateTime Parse(string s)
        {
            DateTime t;
            if (DateTime.TryParseExact((s ?? "").Trim(), "yyyy-MM-dd HH:mm:ss.fff",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out t)) return t;
            return DateTime.MinValue;
        }

        public static string Describe(TimeSpan span)
        {
            if (span <= TimeSpan.Zero) return "unknown";
            if (span.TotalMinutes < 1) return Math.Round(span.TotalSeconds) + "s";
            if (span.TotalHours < 1) return Math.Round(span.TotalMinutes) + "m";
            return Math.Round(span.TotalHours, 1) + "h";
        }

        private static string Sanitize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace('\r', ' ').Replace('\n', ' ');
        }
    }
}
