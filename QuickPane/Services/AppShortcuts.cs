using System;
using System.Collections.Generic;
using System.IO;

namespace QuickPane.Services
{
    /// <summary>
    /// How Windows itself pictures each installed program, read from the Start menu.
    ///
    /// An executable can carry several icons and the first one is not always the one people know the
    /// program by. Chrome Canary and Chrome are both chrome.exe and both answer an icon request with the
    /// same plain Chrome icon; the gold one Canary is known by is further inside the file, and the
    /// only thing that says so is Canary's own Start menu shortcut. So the shortcuts are read once and
    /// consulted whenever a program needs a picture.
    /// </summary>
    internal static class AppShortcuts
    {
        public sealed class IconRef
        {
            public string File;
            public int Index;
        }

        private static readonly object Gate = new object();
        private static Dictionary<string, IconRef> _map;
        private static bool _loading;

        /// <summary>Read the Start menu on the worker. Safe to call repeatedly; it loads once.</summary>
        public static void BeginLoad()
        {
            lock (Gate)
            {
                if (_map != null || _loading) return;
                _loading = true;
            }
            WorkQueue.Post(() =>
            {
                var built = Scan();
                lock (Gate) { _map = built; _loading = false; }
                var h = Loaded;
                if (h != null) WorkQueue.PostUI(() => h());
            });
        }

        /// <summary>Raised on the UI thread once the Start menu has been read.</summary>
        public static event Action Loaded;

        /// <summary>The icon Windows shows for a program, or null when nothing better than the file's own
        /// first icon is known.</summary>
        public static IconRef IconFor(string exePath)
        {
            if (string.IsNullOrEmpty(exePath)) return null;
            Dictionary<string, IconRef> map;
            lock (Gate) map = _map;
            if (map == null) { BeginLoad(); return null; }

            IconRef found;
            return map.TryGetValue(exePath, out found) ? found : null;
        }

        private static Dictionary<string, IconRef> Scan()
        {
            var map = new Dictionary<string, IconRef>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in Roots())
            {
                try
                {
                    if (!Directory.Exists(root)) continue;
                    foreach (var lnk in Directory.GetFiles(root, "*.lnk", SearchOption.AllDirectories))
                        Record(map, lnk);
                }
                catch (Exception ex) { Log.Error("read the Start menu at '" + root + "'", ex); }
            }
            Log.Info("read " + map.Count + " program icon(s) from the Start menu.");
            return map;
        }

        private static void Record(Dictionary<string, IconRef> map, string lnk)
        {
            try
            {
                string target, iconFile;
                int index;
                if (!ShellLink.ReadTargetAndIcon(lnk, out target, out iconFile, out index)) return;
                if (string.IsNullOrEmpty(target)) return;
                if (string.IsNullOrEmpty(iconFile)) return;

                // A shortcut that just points at its target adds nothing over asking the file itself.
                if (index == 0 && string.Equals(iconFile, target, StringComparison.OrdinalIgnoreCase)) return;
                if (!File.Exists(iconFile)) return;

                // The first shortcut found for a program wins; later duplicates are usually uninstallers
                // and helper entries sitting in the same folder.
                if (!map.ContainsKey(target)) map[target] = new IconRef { File = iconFile, Index = index };
            }
            catch (Exception ex) { Log.Error("read the shortcut '" + lnk + "'", ex); }
        }

        private static IEnumerable<string> Roots()
        {
            yield return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                @"Microsoft\Windows\Start Menu\Programs");
            yield return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                @"Microsoft\Windows\Start Menu\Programs");
        }
    }
}
