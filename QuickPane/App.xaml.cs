using System;
using System.Linq;
using System.Threading;
using System.Windows;
using WinForms = System.Windows.Forms;
using Drawing = System.Drawing;
using QuickPane.Explorer;
using QuickPane.Models;
using QuickPane.Services;
using QuickPane.UI;

namespace QuickPane
{
    /// <summary>
    /// Application entry point and process-wide service host.
    ///
    /// Two run modes:
    ///   normal      -> system tray app that watches Explorer windows and embeds a sidebar in each.
    ///   --pin PATH  -> short-lived popup that pins PATH into a chosen group, then exits.
    /// The running tray instance notices the new .lnk through GroupStore's FileSystemWatcher,
    /// so a pin made from the context menu shows up in every open sidebar within a few hundred ms.
    /// </summary>
    public partial class App : Application
    {
        private const string PinVerb = "--pin";

        private Mutex _mutex;
        private bool _ownsMutex;
        private WinForms.NotifyIcon _tray;
        private ExplorerWatcher _watcher;
        private ExplorerFollowerWatcher _follower;
        private AppBarHost _appbar;
        private RecentTracker _recentTracker;
        private DialogPaneWatcher _dialogPanes;
        private Window _settingsWindow;

        // Process-wide services. Set once during normal startup.
        public static SettingsStore Settings { get; private set; }
        public static GroupStore Groups { get; private set; }
        public static RecentFoldersService Recents { get; private set; }
        public static DriveService Drives { get; private set; }
        public static RecentAppsService RecentApps { get; private set; }
        public static AppDefaultsService AppDefaults { get; private set; }
        public static ThemeService Theme { get; private set; }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            Log.Init();
            Log.Info("QuickPane starting. args=[" + string.Join(" ", e.Args) + "]");

            // Last-resort exception logging. Dispatcher exceptions are contained so one bad event
            // handler cannot take down every embedded pane, while appdomain and task faults are written
            // to the journal before the CLR acts on them. A terminating appdomain fault is the one case
            // the process does not survive, so it is stamped into the heartbeat as well, which lets the
            // watchdog name the cause rather than only reporting that the app vanished.
            DispatcherUnhandledException += (s, a) =>
            {
                Log.Event("crash", "unhandled exception on the UI thread, contained so the panes survive", a.Exception);
                a.Handled = true;
            };
            AppDomain.CurrentDomain.UnhandledException += (s, a) =>
            {
                var ex2 = a.ExceptionObject as Exception;
                var wrapped = ex2 ?? new Exception(a.ExceptionObject != null ? a.ExceptionObject.ToString() : "unknown");
                if (a.IsTerminating)
                {
                    Log.Activity("terminating: " + wrapped.GetType().Name + ": " + wrapped.Message);
                    Log.Event("crash", "unhandled exception is terminating the process", wrapped);
                }
                else Log.Event("crash", "unhandled exception on a background thread", wrapped);
            };
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, a) =>
            {
                Log.Event("crash", "unobserved task exception", a.Exception);
                a.SetObserved();
            };

            try
            {
                if (e.Args.Length >= 2 && string.Equals(e.Args[0], Watchdog.Verb, StringComparison.OrdinalIgnoreCase))
                {
                    RunWatchdogMode(e.Args[1]);
                    return;
                }

                if (e.Args.Length >= 1 && string.Equals(e.Args[0], ShutdownSignal.Verb, StringComparison.OrdinalIgnoreCase))
                {
                    RunExitMode();
                    return;
                }

                if (e.Args.Length >= 2 && string.Equals(e.Args[0], PinVerb, StringComparison.OrdinalIgnoreCase))
                {
                    RunPinMode(e.Args[1]);
                    return;
                }

                if (!ClaimSingleInstance())
                {
                    Log.Info("Another QuickPane instance is already running. Exiting.");
                    Shutdown();
                    return;
                }

                CrashGuard.BeginSession();
                ShutdownSignal.Listen(() => Dispatcher.BeginInvoke(new Action(Shutdown)));
                Watchdog.Launch();
                ShortcutRepair.EnsureStartMenu();
                StartTrayApp();
            }
            catch (Exception ex)
            {
                Log.Event("crash", "startup failed, so the app is exiting", ex);
                Shutdown();
            }
        }

        // ---- Exit mode (used by the installer and the uninstaller) -----------

        /// <summary>Ask the running tray instance to shut down and block until it and its watchdog are
        /// gone. The exit code is what an installer reads to decide whether the files are free, so a
        /// timeout has to report failure rather than pretend the app closed.</summary>
        private void RunExitMode()
        {
            int code = ShutdownSignal.RequestExit(TimeSpan.FromSeconds(15)) ? 0 : 1;
            Shutdown(code);
        }

        // ---- Watchdog mode (companion process) -------------------------------

        /// <summary>Wait for the tray instance to exit, then either exit quietly or restart it. No
        /// mutex, no tray icon, no hooks, and no windows, because this process exists only to outlive
        /// whatever takes the app down.</summary>
        private void RunWatchdogMode(string pidArg)
        {
            int pid;
            if (!int.TryParse(pidArg, out pid)) { Shutdown(); return; }
            Watchdog.Run(pid, () => Dispatcher.BeginInvoke(new Action(Shutdown)));
        }

        // ---- Normal tray mode ------------------------------------------------

        private void StartTrayApp()
        {
            Settings = new SettingsStore();
            Settings.Load();

            Theme = new ThemeService();
            Theme.Start();           // reads registry, installs the first theme dictionary
            Theme.Apply();

            Groups = new GroupStore(Settings);
            Groups.Start();          // initial scan + FileSystemWatcher on the groups folder

            // If the groups path or mode changes in settings, react. Reload groups only when the
            // groups folder actually changed: reloading on every save also ran on width release and
            // section renames, re-creating the FileSystemWatcher and rescanning for nothing.
            string lastGroupsPath = Settings.ExpandedGroupsPath;
            Settings.Changed += (s, e) =>
            {
                try
                {
                    var p = Settings.ExpandedGroupsPath;
                    if (!string.Equals(p, lastGroupsPath, StringComparison.OrdinalIgnoreCase))
                    {
                        lastGroupsPath = p;
                        Groups.Reload();
                    }
                }
                catch (Exception ex) { Log.Error("groups reload", ex); }
                // Defer the host switch so we never tear down a window from inside its own event.
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try { ApplyHosts(); } catch (Exception ex) { Log.Error("apply hosts", ex); }
                    try { ApplyHotkeys(); } catch (Exception ex) { Log.Error("apply hotkeys", ex); }
                }));
            };

            Recents = new RecentFoldersService(Settings);
            Recents.Start();

            _recentTracker = new RecentTracker();
            _recentTracker.Start();   // records the folder of whichever Explorer window is browsed

            _dialogPanes = new DialogPaneWatcher();
            _dialogPanes.Start();     // adds a pane beside file Open/Save dialogs, in every pane mode

            Drives = new DriveService();
            RecentApps = new RecentAppsService();
            AppDefaults = new AppDefaultsService();

            _hotkeys = new HotkeyService();
            _hotkeys.Pressed += FocusPaneFromHotkey;
            ApplyHotkeys();

            CreateTrayIcon();

            ApplyHosts();            // starts the in-window pane and/or the desktop dock per settings

            Log.Info("QuickPane running.");

            // Move the breadcrumb off "starting", or a crash hours later would be journaled against
            // startup and point the search at the wrong place entirely.
            Log.Activity("running");
        }

        private static HotkeyService _hotkeys;

        /// <summary>Re-read the keyboard navigation settings and register or drop the shortcut.</summary>
        private void ApplyHotkeys()
        {
            if (_hotkeys == null || Settings == null) return;
            var s = Settings.Current;
            _hotkeys.Apply(s.KeyboardNav, s.KeyboardNavHotkey);
        }

        /// <summary>Put focus in the pane belonging to whatever dialog is in front.</summary>
        private void FocusPaneFromHotkey()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    var fg = Interop.NativeMethods.GetForegroundWindow();
                    if (_dialogPanes != null && _dialogPanes.FocusPaneFor(fg)) return;
                    Log.Info("keyboard navigation shortcut pressed with no pane on the window in front.");
                }
                catch (Exception ex) { Log.Error("focus pane hotkey", ex); }
            }));
        }

        private string _windowMode;
        private bool _dockOn;
        private bool _dockAutoHide;
        private bool _dockAllDesktops;

        /// <summary>Reconcile the running hosts with settings. One window mode (inside or beside) is
        /// always active, and the desktop dock is an independent add-on.</summary>
        private void ApplyHosts()
        {
            var s = Settings.Current;
            string mode = (s.Mode ?? "inside").Trim().ToLowerInvariant();
            if (mode != "beside" && mode != "off") mode = "inside";
            bool wantDock = s.DesktopDock;
            bool autoHide = s.DesktopDockAutoHide;
            bool allDesktops = s.DesktopDockAllDesktops;

            if (mode != _windowMode)
            {
                try { _watcher?.Dispose(); } catch (Exception ex) { Log.Error("watcher dispose", ex); }
                _watcher = null;
                try { _follower?.Dispose(); } catch (Exception ex) { Log.Error("follower dispose", ex); }
                _follower = null;

                if (mode == "beside") { _follower = new ExplorerFollowerWatcher(); _follower.Start(); }
                else if (mode == "inside") { _watcher = new ExplorerWatcher(); _watcher.Start(); }
                // mode == "off": no window pane
                _windowMode = mode;
            }

            bool dockRestart = wantDock && _dockOn && (autoHide != _dockAutoHide || allDesktops != _dockAllDesktops);
            if ((!wantDock && _dockOn) || dockRestart)
            {
                try { _appbar?.Dispose(); } catch (Exception ex) { Log.Error("appbar dispose", ex); }
                _appbar = null; _dockOn = false;
            }
            if (wantDock && !_dockOn)
            {
                _appbar = new AppBarHost(autoHide, allDesktops); _appbar.Start();
                _dockOn = true; _dockAutoHide = autoHide; _dockAllDesktops = allDesktops;
            }

            Log.Info("Hosts: mode=" + _windowMode + " dock=" + _dockOn + " autohide=" + _dockAutoHide + " allDesktops=" + _dockAllDesktops);
            Log.Activity("running, mode=" + _windowMode + ", dock=" + _dockOn);
        }

        private bool ClaimSingleInstance()
        {
            _mutex = new Mutex(true, CrashGuard.InstanceMutexName, out _ownsMutex);
            return _ownsMutex;
        }

        private void CreateTrayIcon()
        {
            _tray = new WinForms.NotifyIcon
            {
                Icon = LoadAppIcon(),
                Text = "QuickPane",
                Visible = true
            };

            var menu = new WinForms.ContextMenuStrip();
            var profileMenu = new WinForms.ToolStripMenuItem("Profile");
            menu.Items.Add(profileMenu);
            menu.Items.Add(new WinForms.ToolStripSeparator());
            menu.Items.Add("Settings", null, (s, a) => Dispatcher.Invoke(ShowSettingsWindow));
            menu.Items.Add("Reload groups", null, (s, a) => Dispatcher.Invoke(() => Groups.Reload()));
            menu.Items.Add("Re-attach sidebars", null, (s, a) => Dispatcher.Invoke(() => { if (_watcher != null) _watcher.Rescan(); }));

            var toolsMenu = new WinForms.ToolStripMenuItem("Troubleshooting");
            toolsMenu.DropDownItems.Add("Open log folder", null, (s, a) => Log.OpenFolder());
            toolsMenu.DropDownItems.Add("Create desktop shortcut", null, (s, a) => CreateDesktopShortcut());
            toolsMenu.DropDownItems.Add("Restart QuickPane", null, (s, a) => Dispatcher.Invoke(RestartSelf));
            menu.Items.Add(toolsMenu);

            menu.Items.Add(new WinForms.ToolStripSeparator());
            menu.Items.Add("Exit", null, (s, a) => Dispatcher.Invoke(Shutdown));

            // Rebuild the Profile submenu each time the tray menu opens, with a check on the active one.
            menu.Opening += (s, a) =>
            {
                profileMenu.DropDownItems.Clear();
                var profiles = Settings.Current.Profiles;
                if (profiles == null) return;
                for (int i = 0; i < profiles.Count; i++)
                {
                    int idx = i;
                    var item = new WinForms.ToolStripMenuItem(profiles[i].Name)
                    {
                        Checked = (i == Settings.Current.ActiveProfileIndex),
                        CheckOnClick = false
                    };
                    item.Click += (s2, a2) => Dispatcher.Invoke(() => Settings.SwitchProfile(idx));
                    profileMenu.DropDownItems.Add(item);
                }
            };
            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += (s, a) => Dispatcher.Invoke(ShowSettingsWindow);
        }

        private static void CreateDesktopShortcut()
        {
            var path = ShortcutRepair.CreateDesktop();
            WinForms.MessageBox.Show(
                path != null ? "Desktop shortcut created:\n" + path
                             : "The desktop shortcut could not be created. See events.log in the log folder.",
                "QuickPane");
        }

        /// <summary>Relaunch and exit. The heartbeat is stamped clean first so the watchdog treats this
        /// as an orderly exit and does not add a restart of its own on top of this one.</summary>
        private void RestartSelf()
        {
            try
            {
                var exe = Log.ExePath();
                if (string.IsNullOrEmpty(exe)) return;
                Log.Event("restart", "restart requested from the tray menu.");
                CrashGuard.MarkCleanExit();
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = "",
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
            }
            catch (Exception ex) { Log.Event("restart", "restart from the tray menu failed", ex); }
            Shutdown();
        }

        private static Drawing.Icon LoadAppIcon()
        {
            try
            {
                var exe = System.Reflection.Assembly.GetExecutingAssembly().Location;
                var ico = Drawing.Icon.ExtractAssociatedIcon(exe);
                if (ico != null) return ico;
            }
            catch (Exception ex) { Log.Error("load tray icon", ex); }
            return Drawing.SystemIcons.Application;
        }

        /// <summary>Open (or activate) the real Settings window from anywhere in the app, e.g. the
        /// sidebar's own gear button, rather than embedding a second copy of the settings UI in place.</summary>
        public static void ShowSettings()
        {
            var app = Application.Current as App;
            if (app != null) app.ShowSettingsWindow();
        }

        private void ShowSettingsWindow()
        {
            if (_settingsWindow != null)
            {
                _settingsWindow.Activate();
                return;
            }

            var panel = new SettingsPanel();
            panel.Bind(Settings, Groups);
            panel.CloseRequested += (s, a) => _settingsWindow?.Close();

            _settingsWindow = new Window
            {
                Title = "QuickPane Settings",
                Width = 940,
                Height = 620,
                MinWidth = 420,
                MinHeight = 420,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ResizeMode = ResizeMode.CanResize,
                ShowInTaskbar = true,
                Content = panel
            };
            _settingsWindow.Closed += (s, a) => _settingsWindow = null;
            Theme.AttachWindow(_settingsWindow);
            _settingsWindow.Show();
            _settingsWindow.Activate();
        }

        // ---- Pin mode (context menu verb) ------------------------------------

        private void RunPinMode(string targetPath)
        {
            try
            {
                var settings = new SettingsStore();
                settings.Load();
                var groups = new GroupStore(settings);
                groups.ScanOnce();   // no watcher needed for a one-shot popup

                var picker = new GroupPickerWindow(groups, targetPath);
                picker.Closed += (s, a) => Shutdown();
                picker.ShowNearCursor();
            }
            catch (Exception ex)
            {
                Log.Error("Pin mode failed for '" + targetPath + "'", ex);
                Shutdown();
            }
        }

        // ---- Teardown --------------------------------------------------------

        protected override void OnExit(ExitEventArgs e)
        {
            Log.Info("QuickPane shutting down.");
            Log.Activity("shutting down");

            // Stamp the heartbeat before anything is torn down, because teardown drives foreign windows
            // and can take long enough for the watchdog to see the process go while the file still reads
            // as a live session. CrashGuard ignores the call in pin and watchdog mode, where this process
            // never opened the session the file describes.
            CrashGuard.MarkCleanExit();
            ShutdownSignal.Stop();

            try { _recentTracker?.Dispose(); } catch (Exception ex) { Log.Error("recent tracker dispose", ex); }
            try { _dialogPanes?.Dispose(); } catch (Exception ex) { Log.Error("dialog panes dispose", ex); }
            try { _watcher?.Dispose(); } catch (Exception ex) { Log.Error("watcher dispose", ex); }
            try { _follower?.Dispose(); } catch (Exception ex) { Log.Error("follower dispose", ex); }
            try { _appbar?.Dispose(); } catch (Exception ex) { Log.Error("appbar dispose", ex); }
            try { Recents?.Dispose(); } catch (Exception ex) { Log.Error("recents dispose", ex); }
            try { Groups?.Dispose(); } catch (Exception ex) { Log.Error("groups dispose", ex); }
            try { Theme?.Dispose(); } catch (Exception ex) { Log.Error("theme dispose", ex); }

            if (_tray != null)
            {
                _tray.Visible = false;
                _tray.Dispose();
            }

            if (_mutex != null)
            {
                if (_ownsMutex) { try { _mutex.ReleaseMutex(); } catch { } }
                _mutex.Dispose();
            }

            base.OnExit(e);
        }
    }
}
