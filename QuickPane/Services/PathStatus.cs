using System;
using System.Collections.Generic;
using System.IO;

namespace QuickPane.Services
{
    /// <summary>
    /// Cached, background-probed path facts (exists / file-vs-folder). The UI reads only this cache
    /// and never touches the disk itself, because probing a dead network target can block for many
    /// seconds and the UI thread shares Explorer's input queue. A path that has never been probed is
    /// reported optimistically (exists, folder unless the leaf has an extension); the probe runs on
    /// the worker and, whenever a result differs from what is cached, a coalesced Changed event is
    /// raised on the UI thread so the sections can re-render with the truth.
    /// </summary>
    internal static class PathStatus
    {
        /// <summary>Raised on the UI thread, coalesced to at most one call per ~300 ms, after any
        /// probe result changed (or a slow icon finished extracting). Sections rebuild on it.</summary>
        public static event Action Changed;

        private sealed class Entry
        {
            public bool Exists;
            public bool IsFile;
            public bool Probed;
            public bool InFlight;
            public int Tick;      // completion time of the last probe
        }

        private const int TtlMs = 15000;
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, Entry> Map =
            new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        private static bool _raiseQueued;

        /// <summary>Cached existence; optimistic true for never-probed paths. Queues a probe.</summary>
        public static bool IsAlive(string path)
        {
            var e = Lookup(path);
            return e == null || e.Exists;
        }

        /// <summary>Cached file-vs-folder; extension guess for never-probed paths. Queues a probe.</summary>
        public static bool IsFile(string path)
        {
            var e = Lookup(path);
            if (e == null) return false;
            return e.Probed ? e.IsFile : GuessIsFile(path);
        }

        /// <summary>True only when a probe has completed and found the path alive. Used to gate
        /// operations that would themselves hit the disk (real shell icon extraction).</summary>
        public static bool ConfirmedAlive(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            lock (Gate)
            {
                Entry e;
                return Map.TryGetValue(path, out e) && e.Probed && e.Exists;
            }
        }

        /// <summary>Ask for a (re-)probe without reading anything.</summary>
        public static void Touch(string path) { Lookup(path); }

        /// <summary>Fire the coalesced Changed event (used by slow icon extraction as well, so all
        /// visual refreshes funnel through one throttle).</summary>
        public static void NotifyVisualRefresh() { QueueRaise(); }

        // Returns the entry (never probes on the calling thread) and queues a probe when the entry
        // is new or stale. Null for empty paths.
        private static Entry Lookup(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            Entry e;
            bool probe = false;
            lock (Gate)
            {
                if (!Map.TryGetValue(path, out e))
                {
                    e = new Entry { Exists = true, IsFile = GuessIsFile(path) };
                    Map[path] = e;
                    probe = true;
                }
                else if (!e.InFlight && (!e.Probed || Environment.TickCount - e.Tick > TtlMs))
                {
                    probe = true;
                }
                if (probe) e.InFlight = true;
            }
            if (probe)
            {
                var p = path;
                WorkQueue.Post(() => Probe(p));
            }
            return e;
        }

        private static void Probe(string path)
        {
            bool exists = false, isFile = false;
            try
            {
                var attr = File.GetAttributes(path); // one syscall answers both questions
                exists = true;
                isFile = (attr & FileAttributes.Directory) == 0;
            }
            catch { exists = false; }

            bool changed = false;
            lock (Gate)
            {
                Entry e;
                if (Map.TryGetValue(path, out e))
                {
                    changed = !e.Probed || e.Exists != exists || (exists && e.IsFile != isFile);
                    e.Exists = exists;
                    if (exists) e.IsFile = isFile;
                    e.Probed = true;
                    e.InFlight = false;
                    e.Tick = Environment.TickCount;
                }
            }
            if (changed) QueueRaise();
        }

        private static void QueueRaise()
        {
            lock (Gate)
            {
                if (_raiseQueued) return;
                _raiseQueued = true;
            }
            WorkQueue.PostUI(() =>
            {
                // Small settle delay so a burst of probe results turns into one rebuild.
                var t = new System.Windows.Threading.DispatcherTimer
                { Interval = TimeSpan.FromMilliseconds(300) };
                t.Tick += (s, e) =>
                {
                    t.Stop();
                    lock (Gate) { _raiseQueued = false; }
                    var h = Changed;
                    if (h != null) h();
                };
                t.Start();
            });
        }

        private static bool GuessIsFile(string path)
        {
            try
            {
                var trimmed = path.TrimEnd('\\', '/');
                return !string.IsNullOrEmpty(Path.GetExtension(trimmed));
            }
            catch { return false; }
        }
    }
}
