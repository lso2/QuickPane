// SettingsPanel.Ssh.cs
// Purpose: the SSH connections block and the SSH setup steps in Settings.
// Reason: one area of the Settings window per file, each under 30KB.
// Defines: SettingsPanel (partial): SshBlock, BuildSshCard, BuildKeyFileRow, SshTextRow, ModeRadioGroup, BuildAddSshCard, NextFreeDriveLetter, SshSetupBlock, SshStep, CopyableCommands, OpenUrl.
// Doc: docs/notes/QuickPane/UI/SettingsPanel.Ssh.cs.md
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using WinForms = System.Windows.Forms;
using QuickPane.Models;
using QuickPane.Services;

namespace QuickPane.UI
{
    public partial class SettingsPanel
    {
        // ---- SSH connections ----
        private FrameworkElement SshBlock(AppSettings s)
        {
            var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 4) };
            sp.Children.Add(SubHeading("SSH connections"));

            if (!SshfsService.IsInstalled()) sp.Children.Add(SshSetupBlock());

            var scroller = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Margin = new Thickness(0, 0, 0, 6)
            };
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            if (s.SshProfiles != null)
                for (int i = 0; i < s.SshProfiles.Count; i++) row.Children.Add(BuildSshCard(i));
            row.Children.Add(BuildAddSshCard());
            scroller.Content = row;
            sp.Children.Add(scroller);
            return sp;
        }

        private FrameworkElement BuildSshCard(int index)
        {
            var s = _settings.Current;
            var p = s.SshProfiles[index];
            bool connected = SshfsService.IsMounted(p.Name);

            var border = new Border
            {
                Width = 260,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 0, 10, 0),
                Padding = new Thickness(8),
                Background = CardBrush(),
                BorderBrush = connected ? UiHelpers.AppBrush("AccentBrush") : UiHelpers.AppBrush("SeparatorColor"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4)
            };
            var sp = new StackPanel();

            var header = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var nameBox = new TextBox { Text = p.Name, FontSize = 13, FontWeight = FontWeights.SemiBold, BorderThickness = new Thickness(0), Background = Brushes.Transparent };
            nameBox.LostKeyboardFocus += (a, b) => { p.Name = nameBox.Text; _settings.Save(); };
            Grid.SetColumn(nameBox, 0); header.Children.Add(nameBox);
            var del = TextButton("✕");
            del.ToolTip = "Remove connection";
            del.Click += (a, b) =>
            {
                if (connected) SshfsService.Unmount(p);
                s.SshProfiles.RemoveAt(index);
                _settings.Save();
                BuildUI();
            };
            Grid.SetColumn(del, 1); header.Children.Add(del);
            sp.Children.Add(header);

            sp.Children.Add(ProfileFieldLabel("Hostname"));
            sp.Children.Add(SshTextRow(p.Hostname, v => { p.Hostname = v; _settings.Save(); }));
            sp.Children.Add(ProfileFieldLabel("Port"));
            sp.Children.Add(BuildStepper(p.Port, 1, 65535, 1, v => { p.Port = v; _settings.Save(); }));
            sp.Children.Add(ProfileFieldLabel("Auto-login username"));
            sp.Children.Add(SshTextRow(p.Username, v => { p.Username = v; _settings.Save(); }));

            sp.Children.Add(ProfileFieldLabel("Authentication"));
            var keyRadio = ModeRadioGroup("qpAuth" + index, "Private key", p.AuthMethod == "key", () => { p.AuthMethod = "key"; _settings.Save(); BuildUI(); });
            var pwRadio = ModeRadioGroup("qpAuth" + index, "Password", p.AuthMethod == "password", () => { p.AuthMethod = "password"; _settings.Save(); BuildUI(); });
            sp.Children.Add(keyRadio);
            sp.Children.Add(pwRadio);

            if (p.AuthMethod == "key")
            {
                sp.Children.Add(ProfileFieldLabel("Private key file for authentication"));
                sp.Children.Add(BuildKeyFileRow(index, p));
            }
            else
            {
                sp.Children.Add(ProfileFieldLabel("Auto-login password"));
                var pwBox = new PasswordBox { FontSize = 11 };
                pwBox.Password = p.Password;
                pwBox.LostKeyboardFocus += (a, b) => { p.Password = pwBox.Password; _settings.Save(); };
                sp.Children.Add(pwBox);
            }

            sp.Children.Add(ProfileFieldLabel("Remote path"));
            sp.Children.Add(SshTextRow(p.RemotePath, v => { p.RemotePath = v; _settings.Save(); }));
            sp.Children.Add(ProfileFieldLabel("Drive letter"));
            sp.Children.Add(SshTextRow(p.DriveLetter, v => { p.DriveLetter = v; _settings.Save(); }));
            sp.Children.Add(ProfileFieldLabel("Keepalive: seconds between packets"));
            sp.Children.Add(BuildStepper(p.KeepAliveInterval, 0, 300, 5, v => { p.KeepAliveInterval = v; _settings.Save(); }));
            sp.Children.Add(ModeCheck("Reconnect automatically", p.Reconnect, on => { p.Reconnect = on; _settings.Save(); }));

            var actionRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            var connBtn = TextButton(connected ? "Disconnect" : "Connect");
            connBtn.Click += (a, b) =>
            {
                if (connected) SshfsService.Unmount(p);
                else
                {
                    var err = SshfsService.Mount(p);
                    if (err != null) { WinForms.MessageBox.Show(err, "QuickPane"); return; }
                }
                BuildUI();
            };
            actionRow.Children.Add(connBtn);
            if (connected)
            {
                var status = new TextBlock
                {
                    Text = "● Connected",
                    FontSize = 11,
                    Foreground = UiHelpers.AppBrush("AccentBrush"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(6, 0, 0, 0)
                };
                actionRow.Children.Add(status);
            }
            sp.Children.Add(actionRow);

            border.Child = sp;
            return border;
        }

        private FrameworkElement BuildKeyFileRow(int index, SshProfile p)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var box = new TextBox { Text = p.PrivateKeyPath, FontSize = 11, VerticalContentAlignment = VerticalAlignment.Center };
            box.LostKeyboardFocus += (a, b) => { p.PrivateKeyPath = box.Text; _settings.Save(); };
            Grid.SetColumn(box, 0); row.Children.Add(box);
            var browse = TextButton("...");
            browse.Margin = new Thickness(4, 0, 0, 0);
            browse.ToolTip = "PuTTY keys must be converted first: PuTTYgen -> Conversions -> Export OpenSSH key";
            browse.Click += (a, b) =>
            {
                var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Private key (*.*)|*.*" };
                PickerPlaces.AddTo(dlg);
                if (dlg.ShowDialog() == true) { box.Text = dlg.FileName; p.PrivateKeyPath = dlg.FileName; _settings.Save(); }
            };
            Grid.SetColumn(browse, 1); row.Children.Add(browse);
            return row;
        }

        private FrameworkElement SshTextRow(string value, Action<string> onCommit)
        {
            var box = new TextBox { Text = value, FontSize = 11, Margin = new Thickness(0, 0, 0, 4) };
            box.LostKeyboardFocus += (a, b) => onCommit(box.Text);
            box.KeyDown += (a, b) => { if (b.Key == Key.Enter) onCommit(box.Text); };
            return box;
        }

        private FrameworkElement ModeRadioGroup(string groupName, string label, bool isChecked, Action onSelect)
        {
            var rb = new RadioButton
            {
                Content = label,
                IsChecked = isChecked,
                GroupName = groupName,
                Foreground = UiHelpers.AppBrush("TextPrimary"),
                FontSize = 12,
                Margin = new Thickness(0, 2, 0, 0)
            };
            rb.Checked += (a, b) => onSelect();
            return rb;
        }

        private FrameworkElement BuildAddSshCard()
        {
            var border = new Border
            {
                Width = 150,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 0, 10, 0),
                Padding = new Thickness(8),
                Background = CardBrush(),
                BorderBrush = UiHelpers.AppBrush("SeparatorColor"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4)
            };
            var add = TextButton("+  Add connection");
            add.VerticalAlignment = VerticalAlignment.Center;
            add.Click += (a, b) =>
            {
                if (_settings.Current.SshProfiles == null) _settings.Current.SshProfiles = new System.Collections.Generic.List<SshProfile>();
                var next = _settings.Current.SshProfiles.Count + 1;
                _settings.Current.SshProfiles.Add(new SshProfile { Name = "Connection " + next, DriveLetter = NextFreeDriveLetter() });
                _settings.Save();
                BuildUI();
            };
            border.Child = add;
            return border;
        }

        private static string NextFreeDriveLetter()
        {
            for (char c = 'S'; c <= 'Z'; c++)
            {
                string letter = c + ":";
                if (!System.IO.Directory.Exists(letter + @"\")) return letter;
            }
            return "S:";
        }

        /// <summary>
        /// The SSH setup block: download links for WinFsp and SSHFS-Win (not included with Windows) and
        /// the same installs as terminal commands.
        /// </summary>
        private FrameworkElement SshSetupBlock()
        {
            var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 10), MaxWidth = 560, HorizontalAlignment = HorizontalAlignment.Left };

            sp.Children.Add(new TextBlock
            {
                Text = "Mounting an SSH server as a drive needs two free components. Install both, then "
                     + "reopen Settings.",
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                Foreground = UiHelpers.AppBrush("TextPrimary"),
                Margin = new Thickness(0, 0, 0, 10)
            });

            sp.Children.Add(SshStep("1.", "WinFsp", "the filesystem driver Windows needs to show a remote folder as a drive.",
                "https://github.com/winfsp/winfsp/releases/latest"));
            sp.Children.Add(SshStep("2.", "SSHFS-Win", "the SSH filesystem itself, which WinFsp mounts.",
                "https://github.com/winfsp/sshfs-win/releases/latest"));

            sp.Children.Add(new TextBlock
            {
                Text = "Run each installer and accept the defaults. Or install both from a terminal:",
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                Foreground = UiHelpers.AppBrush("TextPrimary"),
                Margin = new Thickness(0, 14, 0, 6)
            });

            const string commands = "winget install -e --id WinFsp.WinFsp\r\nwinget install -e --id SSHFS-Win.SSHFS-Win";
            sp.Children.Add(CopyableCommands(commands));

            sp.Children.Add(new TextBlock
            {
                Text = "WinFsp asks to restart Windows when it finishes. Connections added here mount as real "
                     + "drive letters once both are installed.",
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.75,
                Foreground = UiHelpers.AppBrush("TextSecondary"),
                Margin = new Thickness(0, 10, 0, 0)
            });

            return sp;
        }

        /// <summary>A numbered install step: name, description and download button.</summary>
        private FrameworkElement SshStep(string number, string name, string what, string url)
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var num = new TextBlock
            {
                Text = number,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = UiHelpers.AppBrush("TextSecondary")
            };
            Grid.SetColumn(num, 0);
            grid.Children.Add(num);

            var text = new TextBlock
            {
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = UiHelpers.AppBrush("TextPrimary")
            };
            text.Inlines.Add(new Run(name) { FontWeight = FontWeights.SemiBold });
            text.Inlines.Add(new Run(", " + what) { Foreground = UiHelpers.AppBrush("TextSecondary") });
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);

            var btn = TextButton("Download");
            btn.Margin = new Thickness(10, 0, 0, 0);
            btn.VerticalAlignment = VerticalAlignment.Center;
            btn.Click += (a, b) => OpenUrl(url);
            Grid.SetColumn(btn, 2);
            grid.Children.Add(btn);

            return grid;
        }

        /// <summary>A read-only, selectable text box for commands, with a Copy button.</summary>
        private FrameworkElement CopyableCommands(string commands)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var box = new TextBox
            {
                Text = commands,
                IsReadOnly = true,
                AcceptsReturn = true,
                FontFamily = new FontFamily("Consolas, Cascadia Mono, Courier New"),
                FontSize = 12,
                Padding = new Thickness(8, 6, 8, 6),
                Foreground = UiHelpers.AppBrush("TextPrimary"),
                Background = UiHelpers.AppBrush("FieldBackground"),
                BorderBrush = UiHelpers.AppBrush("FieldBorder"),
                BorderThickness = new Thickness(1),
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            Grid.SetColumn(box, 0);
            grid.Children.Add(box);

            var copy = TextButton("Copy");
            copy.Margin = new Thickness(8, 0, 0, 0);
            copy.VerticalAlignment = VerticalAlignment.Top;
            copy.Click += (a, b) =>
            {
                try { Clipboard.SetText(commands); copy.Content = "Copied"; }
                catch (Exception ex) { Log.Error("copy install commands", ex); }
            };
            Grid.SetColumn(copy, 1);
            grid.Children.Add(copy);

            return grid;
        }

        private static void OpenUrl(string url)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) { Log.Error("open " + url, ex); }
        }
    }
}
