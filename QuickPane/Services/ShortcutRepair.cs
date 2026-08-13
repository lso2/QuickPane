using System;
using System.IO;

namespace QuickPane.Services
{
    /// <summary>
    /// Keeps a Start menu shortcut for QuickPane present and pointed at the running executable.
    ///
    /// The installer creates one, yet an install that predates it, a moved executable, or a deleted
    /// shortcut all leave the app with no launcher other than the tray icon it only has while running,
    /// which makes reinstalling the only obvious way back in. Checking at every start closes that gap
    /// without the user having to do anything: the shortcut is created when missing and rewritten when
    /// it points somewhere else.
    ///
    /// The desktop shortcut is deliberately not automatic, because putting an icon on someone's desktop
    /// uninvited is intrusive. The tray menu creates one on request instead.
    /// </summary>
    internal static class ShortcutRepair
    {
        public const string LinkName = "QuickPane.lnk";
        private const string Description = "QuickPane sidebar for File Explorer";

        public static string StartMenuPath
        {
            get
            {
                var programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
                return Path.Combine(programs, LinkName);
            }
        }

        public static string DesktopPath
        {
            get
            {
                var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                return Path.Combine(desktop, LinkName);
            }
        }

        /// <summary>Create or correct the Start menu shortcut. Runs on the background worker because it
        /// touches the shell and the profile folder, neither of which belongs on the UI thread.</summary>
        public static void EnsureStartMenu()
        {
            WorkQueue.Post(() =>
            {
                try
                {
                    var exe = Log.ExePath();
                    if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return;

                    var link = StartMenuPath;
                    if (File.Exists(link))
                    {
                        var target = ShellLink.ResolveTarget(link);
                        if (string.Equals(target, exe, StringComparison.OrdinalIgnoreCase)) return;
                        Log.Event("shortcut", "Start menu shortcut pointed at \"" + (target ?? "nothing") +
                            "\" and was repointed at \"" + exe + "\".");
                    }
                    else
                    {
                        Log.Event("shortcut", "Start menu shortcut was missing and has been created at \"" + link + "\".");
                    }

                    ShellLink.CreateAppShortcut(link, exe, Description);
                }
                catch (Exception ex) { Log.Event("shortcut", "Start menu shortcut could not be written", ex); }
            });
        }

        /// <summary>Create the desktop shortcut on request. Returns the path written, or null on failure,
        /// so the caller can tell the user where it landed.</summary>
        public static string CreateDesktop()
        {
            try
            {
                var exe = Log.ExePath();
                if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return null;
                var link = DesktopPath;
                ShellLink.CreateAppShortcut(link, exe, Description);
                Log.Event("shortcut", "desktop shortcut created at \"" + link + "\".");
                return link;
            }
            catch (Exception ex)
            {
                Log.Event("shortcut", "desktop shortcut could not be written", ex);
                return null;
            }
        }
    }
}
