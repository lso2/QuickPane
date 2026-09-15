using System;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text.RegularExpressions;

namespace QuickPane.Services
{
    /// <summary>One GitHub release, cut down to the two things an update check needs.</summary>
    [DataContract]
    internal sealed class GithubRelease
    {
        [DataMember(Name = "tag_name")] public string TagName { get; set; }
        [DataMember(Name = "html_url")] public string HtmlUrl { get; set; }
        [DataMember(Name = "draft")] public bool Draft { get; set; }
        [DataMember(Name = "prerelease")] public bool Prerelease { get; set; }
        [DataMember(Name = "assets")] public GithubAsset[] Assets { get; set; }
    }

    [DataContract]
    internal sealed class GithubAsset
    {
        [DataMember(Name = "name")] public string Name { get; set; }
        [DataMember(Name = "browser_download_url")] public string DownloadUrl { get; set; }
    }

    /// <summary>What a finished check found.</summary>
    internal sealed class UpdateCheckResult
    {
        public bool Available;
        public Version Latest;
        public string DownloadUrl;
        public string Error;
    }

    /// <summary>
    /// Reads the running build's version from its own assembly and asks GitHub whether a newer release
    /// is published. The version is never written down anywhere but AssemblyInfo, so the settings footer
    /// cannot drift out of step with the binary the way a hardcoded string would.
    /// </summary>
    internal static class UpdateService
    {
        private const string LatestReleaseApi = "https://api.github.com/repos/lso2/QuickPane/releases/latest";
        private const string ReleasesPage = "https://github.com/lso2/QuickPane/releases/latest";

        /// <summary>The running build's version, from the assembly rather than a constant.</summary>
        public static Version Current
        {
            get
            {
                try
                {
                    var v = Assembly.GetExecutingAssembly().GetName().Version;
                    return v ?? new Version(0, 0, 0, 0);
                }
                catch { return new Version(0, 0, 0, 0); }
            }
        }

        /// <summary>"3.9.0", trimmed of a trailing zero revision so it reads like the release tag.</summary>
        public static string CurrentDisplay
        {
            get
            {
                var v = Current;
                return v.Revision > 0 ? v.ToString(4) : v.ToString(3);
            }
        }

        /// <summary>Ask GitHub for the latest release on the worker, then hand the answer to
        /// <paramref name="done"/> on the UI thread. Never throws at the caller.</summary>
        public static void CheckAsync(Action<UpdateCheckResult> done)
        {
            if (done == null) return;
            WorkQueue.Post(() =>
            {
                var result = Check();
                WorkQueue.PostUI(() => done(result));
            });
        }

        private static UpdateCheckResult Check()
        {
            var r = new UpdateCheckResult();
            try
            {
                // GitHub's API refuses anonymous calls without a user agent, and .NET 4.8 still defaults
                // to a protocol GitHub dropped, so both have to be set before the request goes out.
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

                var req = (HttpWebRequest)WebRequest.Create(LatestReleaseApi);
                req.UserAgent = "QuickPane/" + CurrentDisplay;
                req.Accept = "application/vnd.github+json";
                req.Timeout = 10000;
                req.ReadWriteTimeout = 10000;

                GithubRelease release;
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var stream = resp.GetResponseStream())
                {
                    if (stream == null) { r.Error = "no response from GitHub"; Log.Info("update check: " + r.Error + "."); return r; }
                    var ser = new DataContractJsonSerializer(typeof(GithubRelease));
                    release = ser.ReadObject(stream) as GithubRelease;
                }

                if (release == null || string.IsNullOrEmpty(release.TagName))
                {
                    r.Error = "GitHub returned no release";
                    Log.Info("update check: " + r.Error + ".");
                    return r;
                }

                Version latest;
                if (!TryParseTag(release.TagName, out latest))
                {
                    r.Error = "could not read the release tag \"" + release.TagName + "\"";
                    Log.Info("update check: " + r.Error + ".");
                    return r;
                }

                r.Latest = latest;
                r.Available = latest > Current;
                r.DownloadUrl = PickDownload(release);
                return r;
            }
            catch (Exception ex)
            {
                Log.Error("check for updates", ex);
                r.Error = ex.Message;
                return r;
            }
        }

        /// <summary>The installer if the release ships one, else the release page.</summary>
        private static string PickDownload(GithubRelease release)
        {
            try
            {
                if (release.Assets != null)
                {
                    foreach (var a in release.Assets)
                    {
                        if (a == null || string.IsNullOrEmpty(a.DownloadUrl) || string.IsNullOrEmpty(a.Name)) continue;
                        var ext = Path.GetExtension(a.Name);
                        if (string.Equals(ext, ".exe", StringComparison.OrdinalIgnoreCase)) return a.DownloadUrl;
                    }
                    foreach (var a in release.Assets)
                    {
                        if (a == null || string.IsNullOrEmpty(a.DownloadUrl) || string.IsNullOrEmpty(a.Name)) continue;
                        var ext = Path.GetExtension(a.Name);
                        if (string.Equals(ext, ".zip", StringComparison.OrdinalIgnoreCase)) return a.DownloadUrl;
                    }
                }
            }
            catch (Exception ex) { Log.Error("pick update download", ex); }
            return string.IsNullOrEmpty(release.HtmlUrl) ? ReleasesPage : release.HtmlUrl;
        }

        /// <summary>
        /// The version inside a release tag, wherever it sits in it. Tags have been published as
        /// "v3.9.0" and as "QuickPaneSetup-3.7.0-release", so anything that assumes the tag starts with
        /// the number reads the second one as no version at all.
        /// </summary>
        private static bool TryParseTag(string tag, out Version version)
        {
            version = null;
            if (string.IsNullOrWhiteSpace(tag)) return false;
            var m = Regex.Match(tag, @"\d+(?:\.\d+){1,3}");
            return m.Success && Version.TryParse(m.Value, out version);
        }
    }
}
