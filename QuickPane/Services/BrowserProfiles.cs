using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace QuickPane.Services
{
    /// <summary>
    /// The profiles a browser is set up with, so a row in the pane can open the window you actually
    /// meant. Browsers keep several signed-in identities behind one executable, and opening the program
    /// plainly lands in whichever was used last, which is rarely the one being reached for.
    /// </summary>
    internal static class BrowserProfiles
    {
        public sealed class Profile
        {
            public string Name;        // what the browser calls it, e.g. "Global Payments"
            public string Arguments;   // what opens it
        }

        /// <summary>
        /// Where each browser keeps its profile list. Chromium decides this from its brand rather than
        /// from where it was installed, so the folder is matched from the path the program runs at.
        /// </summary>
        private static readonly Tuple<string, Func<string>>[] ChromiumDataDirs =
        {
            Tuple.Create<string, Func<string>>(@"BraveSoftware\Brave-Browser-Nightly", () => Local(@"BraveSoftware\Brave-Browser-Nightly\User Data")),
            Tuple.Create<string, Func<string>>(@"BraveSoftware\Brave-Browser-Beta", () => Local(@"BraveSoftware\Brave-Browser-Beta\User Data")),
            Tuple.Create<string, Func<string>>(@"BraveSoftware\Brave-Browser", () => Local(@"BraveSoftware\Brave-Browser\User Data")),
            Tuple.Create<string, Func<string>>(@"Chrome SxS", () => Local(@"Google\Chrome SxS\User Data")),
            Tuple.Create<string, Func<string>>(@"Chrome Beta", () => Local(@"Google\Chrome Beta\User Data")),
            Tuple.Create<string, Func<string>>(@"Chrome Dev", () => Local(@"Google\Chrome Dev\User Data")),
            Tuple.Create<string, Func<string>>(@"Google\Chrome", () => Local(@"Google\Chrome\User Data")),
            Tuple.Create<string, Func<string>>(@"Microsoft\Edge", () => Local(@"Microsoft\Edge\User Data")),
            Tuple.Create<string, Func<string>>(@"Vivaldi", () => Local(@"Vivaldi\User Data")),
            Tuple.Create<string, Func<string>>(@"Opera", () => Roaming(@"Opera Software\Opera Stable"))
        };

        private static string Local(string rel)
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), rel);
        }

        private static string Roaming(string rel)
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), rel);
        }

        /// <summary>The profiles of the program at <paramref name="exePath"/>, newest naming first, or an
        /// empty list for anything that is not a browser this can read.</summary>
        public static List<Profile> For(string exePath)
        {
            var list = new List<Profile>();
            if (string.IsNullOrWhiteSpace(exePath)) return list;

            try
            {
                var leaf = Path.GetFileName(exePath);
                if (string.Equals(leaf, "firefox.exe", StringComparison.OrdinalIgnoreCase))
                    return Firefox();

                var dir = ChromiumDataDir(exePath);
                if (dir != null) return Chromium(dir);
            }
            catch (Exception ex) { Log.Error("read the profiles of '" + exePath + "'", ex); }

            return list;
        }

        private static string ChromiumDataDir(string exePath)
        {
            foreach (var entry in ChromiumDataDirs)
            {
                if (exePath.IndexOf(entry.Item1, StringComparison.OrdinalIgnoreCase) < 0) continue;
                var dir = entry.Item2();
                if (Directory.Exists(dir)) return dir;
            }
            return null;
        }

        // ---- Chromium ---------------------------------------------------

        /// <summary>
        /// Chromium lists its profiles in Local State under profile/info_cache, keyed by the folder each
        /// one lives in and carrying the name the person gave it.
        /// </summary>
        private static List<Profile> Chromium(string userDataDir)
        {
            var list = new List<Profile>();
            var file = Path.Combine(userDataDir, "Local State");
            if (!File.Exists(file)) return list;

            XElement root;
            using (var stream = File.Open(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = JsonReaderWriterFactory.CreateJsonReader(stream, XmlDictionaryReaderQuotas.Max))
            {
                root = XElement.Load(reader);
            }

            var cache = root.Elements("profile").Elements("info_cache").FirstOrDefault();
            if (cache == null) return list;

            foreach (var entry in cache.Elements())
            {
                // Folder names such as "Profile 1" cannot be XML element names, so they arrive escaped.
                var folder = XmlConvert.DecodeName(entry.Name.LocalName);
                if (string.IsNullOrWhiteSpace(folder)) continue;

                var named = entry.Elements("name").FirstOrDefault();
                var name = named == null ? null : named.Value;
                if (string.IsNullOrWhiteSpace(name)) name = folder;

                list.Add(new Profile
                {
                    Name = name,
                    Arguments = "--profile-directory=\"" + folder + "\""
                });
            }

            // "Default" first, then the numbered folders in the order the browser made them.
            return list.OrderBy(p => p.Arguments.Contains("\"Default\"") ? 0 : 1)
                       .ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
                       .ToList();
        }

        // ---- Firefox ----------------------------------------------------

        /// <summary>Firefox keeps a plain ini beside its profiles, one [ProfileN] block each.</summary>
        private static List<Profile> Firefox()
        {
            var list = new List<Profile>();
            var file = Path.Combine(Roaming(@"Mozilla\Firefox"), "profiles.ini");
            if (!File.Exists(file)) return list;

            string current = null;
            foreach (var raw in File.ReadAllLines(file))
            {
                var line = raw.Trim();
                if (line.StartsWith("[", StringComparison.Ordinal))
                {
                    current = line.Trim('[', ']');
                    continue;
                }
                if (current == null || !current.StartsWith("Profile", StringComparison.OrdinalIgnoreCase)) continue;

                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                if (!line.Substring(0, eq).Trim().Equals("Name", StringComparison.OrdinalIgnoreCase)) continue;

                var name = line.Substring(eq + 1).Trim();
                if (name.Length == 0) continue;
                list.Add(new Profile { Name = name, Arguments = "-P \"" + name + "\"" });
            }
            return list;
        }
    }
}
