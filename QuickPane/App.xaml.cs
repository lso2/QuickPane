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
        private const string SingleInstanceMutex = "QuickPane.SingleInstance.{3D9A2B1C}";
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
        public static ThemeService Theme { get; private set; }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            Log.Init();
            Log.Info("QuickPane starting. args=[" + string.Join(" ", e.Args) + "]");

            try
            {
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

                StartTrayApp();
            }
            catch (Exception ex)
            {
                Log.Error("Fatal during startup", ex);
                Shutdown();
            }
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

            // If the groups path or mode changes in settings, react.
            Settings.Changed += (s, e) =>
            {
                try { Groups.Reload(); } catch (Exception ex) { Log.Error("groups reload", ex); }
                // Defer the host switch so we never tear down a window from inside its own event.
                Dispatcher.BeginInvoke(new Action(() => { try { ApplyHosts(); } catch (Exception ex) { Log.Error("apply hosts", ex); } }));
            };

            Recents = new RecentFoldersService(Settings);
            Recents.Start();

            _recentTracker = new RecentTracker();
            _recentTracker.Start();   // records the folder of whichever Explorer window is browsed

            _dialogPanes = new DialogPaneWatcher();
            _dialogPanes.Start();     // adds a pane beside file Open/Save dialogs, in every pane mode

            Drives = new DriveService();

            CreateTrayIcon();

            ApplyHosts();            // starts the in-window pane and/or the desktop dock per settings

            Log.Info("QuickPane running.");
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
        }

        private bool ClaimSingleInstance()
        {
            _mutex = new Mutex(true, SingleInstanceMutex, out _ownsMutex);
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
