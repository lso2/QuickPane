using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using QuickPane.Models;

namespace QuickPane.Services
{
    /// <summary>
    /// Mounts a saved SshProfile as a real Windows drive using SSHFS-Win (which requires WinFsp).
    /// Each mount is one running sshfs.exe process; unmounting is stopping that process. Mount state
    /// lives only for the app's lifetime, same as any other live connection, not persisted to disk.
    /// </summary>
    public static class SshfsService
    {
        public static event EventHandler MountStatusChanged;

        private static readonly Dictionary<string, Process> _mounts = new Dictionary<string, Process>(StringComparer.OrdinalIgnoreCase);

        private static readonly string[] CandidatePaths =
        {
            @"C:\Program Files\SSHFS-Win\bin\sshfs.exe",
            @"C:\Program Files (x86)\SSHFS-Win\bin\sshfs.exe"
        };

        public static bool IsInstalled()
        {
            return TryFindSshfsExe() != null;
        }

        private static string TryFindSshfsExe()
        {
            foreach (var p in CandidatePaths)
                if (File.Exists(p)) return p;
            return null;
        }

        public static bool IsMounted(string profileName)
        {
            if (string.IsNullOrEmpty(profileName)) return false;
            Process p;
            if (!_mounts.TryGetValue(profileName, out p)) return false;
            try
            {
                if (p.HasExited) { _mounts.Remove(profileName); return false; }
                return true;
            }
            catch { _mounts.Remove(profileName); return false; }
        }

        /// <summary>Full path of the mounted drive root, e.g. "S:\", for use as a sidebar root path.</summary>
        public static string DrivePath(SshProfile profile)
        {
            var letter = (profile.DriveLetter ?? "S:").TrimEnd(':', '\\');
            return letter + @":\";
        }

        /// <summary>Mount a profile. Returns null on success, or an error message for a status label.</summary>
        public static string Mount(SshProfile profile)
        {
            var exe = TryFindSshfsExe();
            if (exe == null)
                return "SSHFS-Win is not installed (needs WinFsp + SSHFS-Win from github.com/winfsp).";

            if (IsMounted(profile.Name)) return null; // already connected

            var drive = DrivePath(profile).TrimEnd('\\');
            if (Directory.Exists(drive + @"\") || File.Exists(drive))
                return drive + " is already in use by something else.";

            if (string.IsNullOrWhiteSpace(profile.Hostname)) return "Hostname is required.";
            if (string.IsNullOrWhiteSpace(profile.Username)) return "Username is required.";
            if (profile.AuthMethod == "key" && string.IsNullOrWhiteSpace(profile.PrivateKeyPath))
                return "Private key file is required for key authentication.";

            var args = new List<string>();
            args.Add(profile.Username + "@" + profile.Hostname + ":" + (string.IsNullOrWhiteSpace(profile.RemotePath) ? "/" : profile.RemotePath));
            args.Add(drive);
            args.Add("-p"); args.Add(profile.Port.ToString());
            args.Add("-o"); args.Add("ServerAliveInterval=" + profile.KeepAliveInterval);
            args.Add("-o"); args.Add("ServerAliveCountMax=" + profile.KeepAliveCountMax);
            args.Add("-o"); args.Add("volname=" + (string.IsNullOrWhiteSpace(profile.Name) ? profile.Hostname : profile.Name));
            if (profile.Reconnect) { args.Add("-o"); args.Add("reconnect"); }
            args.Add("-o"); args.Add("StrictHostKeyChecking=no");

            if (profile.AuthMethod == "key")
            {
                args.Add("-o"); args.Add("IdentityFile=" + profile.PrivateKeyPath);
            }

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = profile.AuthMethod == "password",
                WindowStyle = ProcessWindowStyle.Hidden
            };
            psi.Arguments = BuildCommandLine(args);

            try
            {
                var proc = Process.Start(psi);
                if (profile.AuthMethod == "password" && !string.IsNullOrEmpty(profile.Password))
                {
                    // Best effort: sshfs.exe falls back to reading the password from stdin when it
                    // cannot pop an interactive prompt. Key auth is more reliable and does not need this.
                    try { proc.StandardInput.WriteLine(profile.Password); proc.StandardInput.Flush(); } catch { }
                }
                _mounts[profile.Name] = proc;

                System.Threading.Tasks.Task.Delay(3000).ContinueWith(t =>
                {
                    MountStatusChanged?.Invoke(null, EventArgs.Empty);
                });
                return null;
            }
            catch (Exception ex)
            {
                Log.Error("sshfs mount", ex);
                return "Mount failed: " + ex.Message;
            }
        }

        /// <summary>Join arguments into one command line. .NET Framework exposes only the single
        /// Arguments string, so each value is quoted here under the rules the C runtime uses to split it
        /// again, which keeps a key path or a volume name containing spaces intact.</summary>
        private static string BuildCommandLine(List<string> args)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var a in args)
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(Quote(a));
            }
            return sb.ToString();
        }

        private static string Quote(string arg)
        {
            if (arg == null) return "\"\"";
            if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) < 0) return arg;

            var sb = new System.Text.StringBuilder();
            sb.Append('"');
            for (int i = 0; ; i++)
            {
                int slashes = 0;
                while (i < arg.Length && arg[i] == '\\') { i++; slashes++; }

                if (i == arg.Length)
                {
                    // Trailing backslashes would escape the closing quote, so each one is doubled.
                    sb.Append('\\', slashes * 2);
                    break;
                }
                if (arg[i] == '"')
                {
                    sb.Append('\\', slashes * 2 + 1).Append('"');
                }
                else
                {
                    sb.Append('\\', slashes).Append(arg[i]);
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        public static void Unmount(SshProfile profile)
        {
            Process p;
            if (!_mounts.TryGetValue(profile.Name, out p)) return;
            try { if (!p.HasExited) p.Kill(); } catch (Exception ex) { Log.Error("sshfs unmount", ex); }
            _mounts.Remove(profile.Name);
            MountStatusChanged?.Invoke(null, EventArgs.Empty);
        }
    }
}
