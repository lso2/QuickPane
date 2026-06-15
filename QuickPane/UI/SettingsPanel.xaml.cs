using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using WinForms = System.Windows.Forms;
using QuickPane.Models;
using QuickPane.Services;

namespace QuickPane.UI
{
    /// <summary>Settings UI. Every change is written to settings.json immediately (no Apply button).
    /// Sections and groups reorder by dragging their grip handle rather than with up/down buttons.</summary>
    public partial class SettingsPanel : UserControl
    {
        public event EventHandler CloseRequested;

        private SettingsStore _settings;
        private GroupStore _groups;
        private Point _dragStart;
        private bool _maybeDrag;
        private const string ProfileGroupFormat = "QpProfileGroup";

        /// <summary>True when hosted in the narrow embedded pane: stack everything in one column instead
        /// of the wide multi-column layout used in the tray settings window.</summary>
        public bool Compact { get; set; }

        public SettingsPanel()
        {
            InitializeComponent();
            Focusable = true;
            KeyDown += (s, e) => { if (e.Key == Key.Escape) Raise(); };
        }

        public void Bind(SettingsStore settings, GroupStore groups)
        {
            _settings = settings;
            _groups = groups;
            BuildUI();
        }

        private void BuildUI()
        {
            Host.Children.Clear();
            if (_settings == null) return;
            var s = _settings.Current;

            // Title + close
            var title = new Grid();
            title.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            title.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var t = Heading("Settings");
            t.Margin = new Thickness(0);
            Grid.SetColumn(t, 0);
            title.Children.Add(t);
            var close = new Button
            {
                Content = "", // ChromeClose
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 12,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = UiHelpers.AppBrush("TextSecondary"),
                Cursor = Cursors.Hand,
                Padding = new Thickness(6)
            };
            close.Click += (a, b) => Raise();
            Grid.SetColumn(close, 1);
            title.Children.Add(close);
            Host.Children.Add(title);

            if (Compact)
            {
                // Narrow pane: everything stacked in one column.
                Host.Children.Add(PaneModeBlock(s));
                Host.Children.Add(DockBlock(s));
                Host.Children.Add(SectionsBlock(s));
                Host.Children.Add(ProfileTabsBlock(s));
                Host.Children.Add(SubHeading("Groups"));
                var col = new StackPanel();
                for (int i = 0; i < s.Profiles.Count; i++) col.Children.Add(BuildProfileColumn(i));
                col.Children.Add(BuildAddProfileColumn());
                Host.Children.Add(col);
            }
            else
            {
                // Wide window: three global blocks across the top, profiles as horizontal cards.
                var globals = new Grid { Margin = new Thickness(0, 0, 0, 6) };
                for (int i = 0; i < 3; i++) globals.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                AddCol(globals, 0, PaneModeBlock(s));
                AddCol(globals, 1, DockBlock(s));
                AddCol(globals, 2, SectionsBlock(s));
                Host.Children.Add(globals);

                Host.Children.Add(ProfileTabsBlock(s));
                Host.Children.Add(SubHeading("Groups"));
                var scroller = new ScrollViewer
                {
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    Margin = new Thickness(0, 0, 0, 6)
                };
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                for (int i = 0; i < s.Profiles.Count; i++) row.Children.Add(BuildProfileColumn(i));
                row.Children.Add(BuildAddProfileColumn());
                scroller.Content = row;
                Host.Children.Add(scroller);
            }

            Host.Children.Add(BackupBlock());
            Host.Children.Add(BuildSupportButton());
        }

        // ---- global blocks ----
        private static void AddCol(Grid g, int col, FrameworkElement child) { Grid.SetColumn(child, col); g.Children.Add(child); }

        // Back up and restore all settings to a readable JSON file.
        private FrameworkElement BackupBlock()
        {
            var sp = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
            sp.Children.Add(SubHeading("Backup"));
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
            var status = new TextBlock
            {
                FontSize = 11,
                Foreground = UiHelpers.AppBrush("TextSecondary"),
                Margin = new Thickness(0, 6, 0, 0),
                TextWrapping = TextWrapping.Wrap
            };
            var backup = TextButton("Back up...");
            backup.Click += (a, b) =>
            {
                var dlg = new Microsoft.Win32.SaveFileDialog { FileName = "QuickPane-settings.json", Filter = "JSON file (*.json)|*.json|All files (*.*)|*.*" };
                if (dlg.ShowDialog() == true)
                {
                    try { _settings.ExportTo(dlg.FileName); status.Text = "Backed up to " + dlg.FileName; }
                    catch (Exception ex) { status.Text = "Backup failed: " + ex.Message; }
                }
            };
            var restore = TextButton("Restore...");
            restore.Click += (a, b) =>
            {
                var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "JSON file (*.json)|*.json|All files (*.*)|*.*" };
                if (dlg.ShowDialog() == true)
                {
                    if (_settings.ImportFrom(dlg.FileName)) { status.Text = "Settings restored."; BuildUI(); }
                    else status.Text = "Restore failed: the file could not be read.";
                }
            };
            row.Children.Add(backup);
            row.Children.Add(restore);
            sp.Children.Add(row);
            sp.Children.Add(status);
            return sp;
        }

        private FrameworkElement PaneModeBlock(AppSettings s)
        {
            var sp = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
            sp.Children.Add(SubHeading("Pane mode"));
            string m = (s.Mode ?? "inside").ToLowerInvariant();
            sp.Children.Add(ModeRadio("Inside Explorer window", m == "inside", () => { s.Mode = "inside"; _settings.Save(); }));
            sp.Children.Add(ModeRadio("Beside Explorer window", m == "beside", () => { s.Mode = "beside"; _settings.Save(); }));
            sp.Children.Add(ModeRadio("Off (desktop dock only)", m == "off", () => { s.Mode = "off"; _settings.Save(); }));
            return sp;
        }

        private FrameworkElement DockBlock(AppSettings s)
        {
            var sp = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
            sp.Children.Add(SubHeading("Desktop dock"));
            sp.Children.Add(ModeCheck("Show desktop dock (screen edge)", s.DesktopDock, on => { s.DesktopDock = on; _settings.Save(); }));
            sp.Children.Add(ModeCheck("Auto-hide (slide out on hover)", s.DesktopDockAutoHide, on => { s.DesktopDockAutoHide = on; _settings.Save(); }));
            sp.Children.Add(ModeCheck("Show on all virtual desktops", s.DesktopDockAllDesktops, on => { s.DesktopDockAllDesktops = on; _settings.Save(); }));
            return sp;
        }

        private FrameworkElement ProfileTabsBlock(AppSettings s)
        {
            var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 4) };
            sp.Children.Add(SubHeading("Profile tabs"));
            sp.Children.Add(ModeCheck("Show profiles tab row", s.ShowProfileTabs, on => { s.ShowProfileTabs = on; _settings.Save(); }));
            sp.Children.Add(ModeCheck("Auto-hide (slide out on hover)", s.ProfileTabsAutoHide, on => { s.ProfileTabsAutoHide = on; _settings.Save(); }));
            return sp;
        }

        private FrameworkElement SectionsBlock(AppSettings s)
        {
            var sp = new StackPanel();
            sp.Children.Add(SubHeading("Sections"));
            foreach (var sec in s.Sections.OrderBy(x => x.Order).ToList())
                sp.Children.Add(BuildSectionRow(sec));
            return sp;
        }

        // ---- profile columns ----
        // A card sits slightly lighter than the panel background so even an empty column reads as a card.
        // A non-null background is also required for the card to be a valid drag-drop target.
        private static Brush CardBrush()
        {
            bool dark = App.Theme != null && App.Theme.IsDark;
            return new SolidColorBrush(dark ? Color.FromRgb(0x2D, 0x2D, 0x2D) : Color.FromRgb(0xFF, 0xFF, 0xFF));
        }

        private static bool IsInside(DependencyObject node, DependencyObject ancestor)
        {
            while (node != null)
            {
                if (ReferenceEquals(node, ancestor)) return true;
                DependencyObject parent = null;
                try { parent = VisualTreeHelper.GetParent(node); } catch { }
                if (parent == null && node is FrameworkElement fe) parent = fe.Parent;
                node = parent;
            }
            return false;
        }

        private FrameworkElement BuildProfileColumn(int index)
        {
            var s = _settings.Current;
            var p = s.Profiles[index];
            bool active = index == s.ActiveProfileIndex;

            var border = new Border
            {
                Width = Compact ? double.NaN : 250,
                HorizontalAlignment = Compact ? HorizontalAlignment.Stretch : HorizontalAlignment.Left,
                Margin = Compact ? new Thickness(0, 0, 0, 8) : new Thickness(0, 0, 10, 0),
                Padding = new Thickness(8),
                Background = CardBrush(),
                BorderBrush = active ? UiHelpers.AppBrush("AccentBrush") : UiHelpers.AppBrush("SeparatorColor"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                AllowDrop = true
            };
            border.DragOver += (a, e) =>
            {
                bool ok = e.Data.GetDataPresent(ProfileGroupFormat);
                bool copy = (e.KeyStates & DragDropKeyStates.ControlKey) != 0;
                e.Effects = ok ? (copy ? DragDropEffects.Copy : DragDropEffects.Move) : DragDropEffects.None;
                e.Handled = true;
            };
            border.Drop += (a, e) => OnProfileDrop(index, e);

            var sp = new StackPanel();

            var header = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var nameText = new TextBlock
            {
                Text = p.Name,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = UiHelpers.AppBrush("TextPrimary"),
                ToolTip = "Double-click to rename"
            };
            nameText.MouseLeftButtonDown += (a, b) =>
            {
                if (b.ClickCount != 2) return;
                var v = TextPrompt.Ask("Rename profile", p.Name);
                if (!string.IsNullOrWhiteSpace(v)) { _settings.RenameProfile(index, v); BuildUI(); }
            };
            Grid.SetColumn(nameText, 0); header.Children.Add(nameText);

            // Clear activation control: the shown profile says "Active", the others show an Activate button.
            if (active)
            {
                var act = new TextBlock
                {
                    Text = "● Active",
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = UiHelpers.AppBrush("AccentBrush"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(6, 0, 0, 0)
                };
                Grid.SetColumn(act, 1); header.Children.Add(act);
            }
            else
            {
                var activate = TextButton("Activate");
                activate.Margin = new Thickness(6, 0, 0, 0);
                activate.ToolTip = "Show this profile in the pane";
                activate.Click += (a, b) => { _settings.SwitchProfile(index); BuildUI(); };
                Grid.SetColumn(activate, 1); header.Children.Add(activate);
            }

            if (s.Profiles.Count > 1)
            {
                var rem = TextButton("✕");
                rem.ToolTip = "Remove profile";
                rem.Click += (a, b) =>
                {
                    var r = WinForms.MessageBox.Show("Remove profile \"" + p.Name + "\"? Its groups folder is left on disk.",
                        "QuickPane", WinForms.MessageBoxButtons.YesNo, WinForms.MessageBoxIcon.Warning);
                    if (r == WinForms.DialogResult.Yes) { _settings.RemoveProfile(index); BuildUI(); }
                };
                Grid.SetColumn(rem, 2); header.Children.Add(rem);
            }
            sp.Children.Add(header);

            string root = _settings.ExpandedGroupsPathFor(p);
            foreach (var grp in GroupStore.ListGroups(root))
                sp.Children.Add(BuildProfileGroupRow(index, grp.Item1, grp.Item2));

            var add = TextButton("+  Add group");
            add.HorizontalContentAlignment = HorizontalAlignment.Left;
            add.Margin = new Thickness(0, 6, 0, 8);
            add.Click += (a, b) =>
            {
                var v = TextPrompt.Ask("New group name", "");
                if (string.IsNullOrWhiteSpace(v)) return;
                GroupStore.CreateGroupFolder(root, v);
                ReloadIfActive(index);
                BuildUI();
            };
            sp.Children.Add(add);

            sp.Children.Add(ProfileFieldLabel("Groups folder"));
            sp.Children.Add(BuildProfileFolderRow(index, p));
            sp.Children.Add(ProfileFieldLabel("Recents count"));
            sp.Children.Add(BuildStepper(p.RecentsMaxCount, 5, 50, 1, v => SetProfileRecents(index, v)));
            sp.Children.Add(ProfileFieldLabel("Sidebar width (px)"));
            sp.Children.Add(BuildStepper(p.SidebarWidthPx, 160, 400, 10, v => SetProfileWidth(index, v)));

            border.Child = sp;
            return border;
        }

        private FrameworkElement BuildProfileGroupRow(int profileIndex, string folder, string name)
        {
            var grid = new Grid { Margin = new Thickness(0, 2, 0, 2), Background = Brushes.Transparent };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var grip = Grip();
            grip.Cursor = Cursors.SizeAll;
            grip.ToolTip = "Drag to another profile (Ctrl to copy)";
            Grid.SetColumn(grip, 0); grid.Children.Add(grip);

            var label = new TextBlock
            {
                Text = name,
                FontSize = 12,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = UiHelpers.AppBrush("TextPrimary")
            };
            Grid.SetColumn(label, 1); grid.Children.Add(label);

            var del = TextButton("✕");
            del.ToolTip = "Delete group";
            del.Click += (a, b) =>
            {
                var r = WinForms.MessageBox.Show("Delete group \"" + name + "\"?",
                    "QuickPane", WinForms.MessageBoxButtons.YesNo, WinForms.MessageBoxIcon.Warning);
                if (r != WinForms.DialogResult.Yes) return;
                GroupStore.DeleteGroupFolder(folder);
                ReloadIfActive(profileIndex);
                BuildUI();
            };
            Grid.SetColumn(del, 2); grid.Children.Add(del);

            // Drag anywhere on the row (except the delete button) to move it to another profile column.
            grid.PreviewMouseLeftButtonDown += (s, e) =>
            {
                if (e.OriginalSource is DependencyObject src && IsInside(src, del)) return;
                _dragStart = e.GetPosition(null); _maybeDrag = true;
            };
            grid.PreviewMouseLeftButtonUp += (s, e) => _maybeDrag = false;
            grid.MouseMove += (s, e) =>
            {
                if (!_maybeDrag || e.LeftButton != MouseButtonState.Pressed) return;
                var pt = e.GetPosition(null);
                if (Math.Abs(pt.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
                    Math.Abs(pt.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                _maybeDrag = false;
                try
                {
                    var data = new DataObject();
                    data.SetData(ProfileGroupFormat, folder);
                    DragDrop.DoDragDrop(grid, data, DragDropEffects.Move | DragDropEffects.Copy);
                }
                catch (Exception ex) { Log.Error("profile group drag", ex); }
            };

            return grid;
        }

        private void OnProfileDrop(int destProfileIndex, DragEventArgs e)
        {
            e.Handled = true;
            if (!e.Data.GetDataPresent(ProfileGroupFormat)) return;
            var srcFolder = e.Data.GetData(ProfileGroupFormat) as string;
            if (string.IsNullOrEmpty(srcFolder)) return;

            var s = _settings.Current;
            string destRoot = _settings.ExpandedGroupsPathFor(s.Profiles[destProfileIndex]);
            string srcRoot = System.IO.Path.GetDirectoryName(srcFolder);
            bool copy = (e.KeyStates & DragDropKeyStates.ControlKey) != 0;

            if (string.Equals(srcRoot, destRoot.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) && !copy)
                return; // same profile, plain move is a no-op here

            if (copy) GroupStore.CopyGroupFolder(srcFolder, destRoot);
            else GroupStore.MoveGroupFolder(srcFolder, destRoot);

            try { if (App.Groups != null) App.Groups.Reload(); } catch (Exception ex) { Log.Error("reload after profile drop", ex); }
            BuildUI();
        }

        private FrameworkElement BuildAddProfileColumn()
        {
            var border = new Border
            {
                Width = Compact ? double.NaN : 150,
                HorizontalAlignment = Compact ? HorizontalAlignment.Stretch : HorizontalAlignment.Left,
                Margin = Compact ? new Thickness(0, 0, 0, 8) : new Thickness(0, 0, 10, 0),
                Padding = new Thickness(8),
                Background = CardBrush(),
                BorderBrush = UiHelpers.AppBrush("SeparatorColor"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4)
            };
            var add = TextButton("+  Add profile");
            add.VerticalAlignment = VerticalAlignment.Center;
            add.Click += (a, b) =>
            {
                var v = TextPrompt.Ask("New profile name", "");
                _settings.AddProfile(string.IsNullOrWhiteSpace(v) ? null : v);
                BuildUI();
            };
            border.Child = add;
            return border;
        }

        private FrameworkElement BuildProfileFolderRow(int index, Profile p)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var box = new TextBox { Text = p.GroupsPath, FontSize = 11, VerticalContentAlignment = VerticalAlignment.Center };
            box.LostKeyboardFocus += (a, b) => SetProfileFolder(index, box.Text);
            box.KeyDown += (a, b) => { if (b.Key == Key.Enter) SetProfileFolder(index, box.Text); };
            Grid.SetColumn(box, 0); row.Children.Add(box);
            var browse = TextButton("...");
            browse.Margin = new Thickness(4, 0, 0, 0);
            browse.Click += (a, b) =>
            {
                using (var dlg = new WinForms.FolderBrowserDialog())
                {
                    if (dlg.ShowDialog() == WinForms.DialogResult.OK) { box.Text = dlg.SelectedPath; SetProfileFolder(index, dlg.SelectedPath); }
                }
            };
            Grid.SetColumn(browse, 1); row.Children.Add(browse);
            return row;
        }

        private FrameworkElement ProfileFieldLabel(string text)
        {
            return new TextBlock
            {
                Text = text.ToUpperInvariant(),
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = UiHelpers.AppBrush("TextSecondary"),
                Margin = new Thickness(0, 8, 0, 2)
            };
        }

        private void SetProfileFolder(int index, string value)
        {
            var s = _settings.Current;
            if (index < 0 || index >= s.Profiles.Count || string.IsNullOrWhiteSpace(value)) return;
            if (string.Equals(s.Profiles[index].GroupsPath, value.Trim(), StringComparison.OrdinalIgnoreCase)) return;
            s.Profiles[index].GroupsPath = value.Trim();
            if (index == s.ActiveProfileIndex) s.GroupsPath = value.Trim();
            _settings.Save();
            BuildUI();
        }

        private void SetProfileRecents(int index, int v)
        {
            var s = _settings.Current;
            if (index < 0 || index >= s.Profiles.Count) return;
            s.Profiles[index].RecentsMaxCount = v;
            if (index == s.ActiveProfileIndex) s.RecentsMaxCount = v;
            _settings.Save();
        }

        private void SetProfileWidth(int index, int v)
        {
            var s = _settings.Current;
            if (index < 0 || index >= s.Profiles.Count) return;
            s.Profiles[index].SidebarWidthPx = v;
            if (index == s.ActiveProfileIndex) s.SidebarWidthPx = v;
            _settings.Save();
        }

        private void ReloadIfActive(int profileIndex)
        {
            if (profileIndex == _settings.Current.ActiveProfileIndex && App.Groups != null)
                try { App.Groups.Reload(); } catch (Exception ex) { Log.Error("reload active groups", ex); }
        }

        // ---- sections ----
        private FrameworkElement BuildSectionRow(SectionSetting sec)
        {
            var grid = new Grid { Margin = new Thickness(0, 2, 0, 2), Background = Brushes.Transparent };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var grip = Grip();
            Grid.SetColumn(grip, 0);
            grid.Children.Add(grip);

            var cb = new CheckBox { IsChecked = sec.Visible, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
            cb.Checked += (a, b) => { sec.Visible = true; _settings.Save(); };
            cb.Unchecked += (a, b) => { sec.Visible = false; _settings.Save(); };
            Grid.SetColumn(cb, 1);
            grid.Children.Add(cb);

            var name = new TextBlock
            {
                Text = Friendly(sec.Type),
                FontSize = 12,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = UiHelpers.AppBrush("TextPrimary")
            };
            Grid.SetColumn(name, 2);
            grid.Children.Add(name);

            MakeReorderable(grid, grid, "QpSection", sec.Type, ReorderSectionsByType);
            return grid;
        }

        // ---- groups ----
        private void BuildGroupsManager()
        {
            if (App.Groups == null) return;

            foreach (var group in App.Groups.Groups)
            {
                var g = group;
                var grid = new Grid { Margin = new Thickness(0, 2, 0, 2), Background = Brushes.Transparent };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var grip = Grip();
                Grid.SetColumn(grip, 0);
                grid.Children.Add(grip);

                var name = new TextBlock
                {
                    Text = GroupStore.DisplayName(g),
                    FontSize = 12,
                    Margin = new Thickness(8, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Foreground = UiHelpers.AppBrush("TextPrimary")
                };
                Grid.SetColumn(name, 1);
                grid.Children.Add(name);

                var del = TextButton("✕"); // multiplication x
                del.ToolTip = "Delete group";
                del.Click += (a, b) =>
                {
                    int count = g.Tabs.Sum(t => t.Items.Count);
                    if (count > 0)
                    {
                        var r = WinForms.MessageBox.Show(
                            "Delete \"" + GroupStore.DisplayName(g) + "\" and its " + count + " pin(s)?",
                            "QuickPane", WinForms.MessageBoxButtons.YesNo, WinForms.MessageBoxIcon.Warning);
                        if (r != WinForms.DialogResult.Yes) return;
                    }
                    App.Groups.DeleteGroup(g);
                    BuildUI();
                };
                Grid.SetColumn(del, 2); grid.Children.Add(del);

                MakeReorderable(grid, grid, "QpGroupRow", g.FolderPath, ReorderGroupsByPath);
                Host.Children.Add(grid);
            }

            var add = TextButton("+  Add group");
            add.HorizontalContentAlignment = HorizontalAlignment.Left;
            add.Margin = new Thickness(0, 6, 0, 0);
            add.Click += (a, b) =>
            {
                var v = TextPrompt.Ask("New group name", "");
                if (!string.IsNullOrWhiteSpace(v)) { App.Groups.CreateGroup(v); BuildUI(); }
            };
            Host.Children.Add(add);
        }

        // ---- drag reorder plumbing ----
        private void MakeReorderable(FrameworkElement row, FrameworkElement handle, string format,
            string payload, Action<string, string, bool> onDrop)
        {
            handle.Cursor = Cursors.SizeNS;
            handle.PreviewMouseLeftButtonDown += (s, e) => { _dragStart = e.GetPosition(null); _maybeDrag = true; };
            handle.PreviewMouseLeftButtonUp += (s, e) => _maybeDrag = false;
            handle.MouseMove += (s, e) =>
            {
                if (!_maybeDrag || e.LeftButton != MouseButtonState.Pressed) return;
                var p = e.GetPosition(null);
                if (Math.Abs(p.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                _maybeDrag = false;
                try
                {
                    var data = new DataObject();
                    data.SetData(format, payload);
                    StartGhost(row, e);
                    DragDrop.DoDragDrop(row, data, DragDropEffects.Move);
                }
                catch (Exception ex) { Log.Error("settings drag", ex); }
                finally { EndGhost(); }
            };

            row.AllowDrop = true;
            row.DragOver += (s, e) =>
            {
                UpdateGhost(e);
                e.Effects = e.Data.GetDataPresent(format) ? DragDropEffects.Move : DragDropEffects.None;
                e.Handled = true;
            };
            row.Drop += (s, e) =>
            {
                e.Handled = true;
                if (!e.Data.GetDataPresent(format)) return;
                var dragged = e.Data.GetData(format) as string;
                if (dragged == null || dragged == payload) return;
                bool below = e.GetPosition(row).Y > row.ActualHeight / 2;
                onDrop(dragged, payload, below);
            };
        }

        private DragGhostAdorner _ghost;
        private AdornerLayer _ghostLayer;
        private Vector _grab;

        private void StartGhost(FrameworkElement source, MouseEventArgs e)
        {
            try
            {
                var layer = AdornerLayer.GetAdornerLayer(Host);
                if (layer == null) return;
                _grab = (Vector)e.GetPosition(source);
                _ghost = new DragGhostAdorner(Host, source, source.RenderSize);
                layer.Add(_ghost);
                _ghostLayer = layer;
            }
            catch (Exception ex) { Log.Error("settings ghost", ex); }
        }

        private void UpdateGhost(DragEventArgs e)
        {
            if (_ghost == null) return;
            var p = e.GetPosition(Host);
            _ghost.SetPosition(new Point(p.X - _grab.X, p.Y - _grab.Y));
        }

        private void EndGhost()
        {
            try { if (_ghost != null && _ghostLayer != null) _ghostLayer.Remove(_ghost); }
            catch { }
            _ghost = null;
            _ghostLayer = null;
        }

        private void ReorderSectionsByType(string draggedType, string targetType, bool below)
        {
            var ordered = _settings.Current.Sections.OrderBy(x => x.Order).ToList();
            var d = ordered.FirstOrDefault(x => x.Type == draggedType);
            var tg = ordered.FirstOrDefault(x => x.Type == targetType);
            if (d == null || tg == null) return;
            ordered.Remove(d);
            int idx = ordered.IndexOf(tg);
            ordered.Insert(below ? idx + 1 : idx, d);
            for (int i = 0; i < ordered.Count; i++) ordered[i].Order = i;
            _settings.Save();
            BuildUI();
        }

        private void ReorderGroupsByPath(string draggedPath, string targetPath, bool below)
        {
            if (App.Groups == null) return;
            var ordered = App.Groups.Groups.ToList();
            var d = ordered.FirstOrDefault(g => string.Equals(g.FolderPath, draggedPath, StringComparison.OrdinalIgnoreCase));
            var tg = ordered.FirstOrDefault(g => string.Equals(g.FolderPath, targetPath, StringComparison.OrdinalIgnoreCase));
            if (d == null || tg == null) return;
            ordered.Remove(d);
            int idx = ordered.IndexOf(tg);
            ordered.Insert(below ? idx + 1 : idx, d);
            App.Groups.ReorderGroups(ordered);
            BuildUI();
        }

        private void CommitPath(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            if (value == _settings.Current.GroupsPath) return;
            _settings.Current.GroupsPath = value.Trim();
            _settings.Save();
        }

        private FrameworkElement BuildStepper(int value, int min, int max, int step, Action<int> onChange)
        {
            int current = Math.Max(min, Math.Min(max, value));
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 6), HorizontalAlignment = HorizontalAlignment.Left };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var valueText = new TextBlock
            {
                Text = current.ToString(),
                MinWidth = 44,
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 12,
                Foreground = UiHelpers.AppBrush("TextPrimary")
            };

            var minus = TextButton("−"); // minus sign
            var plus = TextButton("+");

            minus.Click += (a, b) =>
            {
                current = Math.Max(min, current - step);
                valueText.Text = current.ToString();
                onChange(current);
            };
            plus.Click += (a, b) =>
            {
                current = Math.Min(max, current + step);
                valueText.Text = current.ToString();
                onChange(current);
            };

            Grid.SetColumn(minus, 0); Grid.SetColumn(valueText, 1); Grid.SetColumn(plus, 2);
            grid.Children.Add(minus); grid.Children.Add(valueText); grid.Children.Add(plus);
            return grid;
        }

        private FrameworkElement BuildSupportButton()
        {
            var btn = new Button
            {
                Cursor = Cursors.Hand,
                Background = Brushes.Transparent,
                BorderBrush = UiHelpers.AppBrush("SeparatorColor"),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8, 6, 8, 6),
                Margin = new Thickness(0, 18, 0, 4),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            var sp = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            sp.Children.Add(new TextBlock
            {
                Text = "❤",
                Foreground = UiHelpers.AppBrush("AccentBrush"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            });
            sp.Children.Add(new TextBlock
            {
                Text = "Buy me a coffee",
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = UiHelpers.AppBrush("TextPrimary")
            });
            btn.Content = sp;
            btn.Click += (a, b) => SidebarControl.OpenSupportLink();
            return btn;
        }

        // ---- small builders ----
        private FrameworkElement ModeRadio(string label, bool isChecked, Action onSelect)
        {
            var rb = new RadioButton
            {
                Content = label,
                IsChecked = isChecked,
                GroupName = "qpPaneMode",
                Foreground = UiHelpers.AppBrush("TextPrimary"),
                FontSize = 12,
                Margin = new Thickness(0, 3, 0, 0)
            };
            rb.Checked += (a, b) => onSelect();
            return rb;
        }

        private FrameworkElement ModeCheck(string label, bool isChecked, Action<bool> onChange)
        {
            var cb = new CheckBox
            {
                Content = label,
                IsChecked = isChecked,
                Foreground = UiHelpers.AppBrush("TextPrimary"),
                FontSize = 12,
                Margin = new Thickness(0, 3, 0, 0)
            };
            cb.Checked += (a, b) => onChange(true);
            cb.Unchecked += (a, b) => onChange(false);
            return cb;
        }

        private TextBlock Grip()
        {
            return new TextBlock
            {
                Text = "☰", // trigram, drag handle
                FontSize = 12,
                Foreground = UiHelpers.AppBrush("TextSecondary"),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "Drag to reorder"
            };
        }

        private TextBlock Heading(string text)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Foreground = UiHelpers.AppBrush("TextPrimary"),
                Margin = new Thickness(0, 0, 0, 8)
            };
        }

        private TextBlock SubHeading(string text)
        {
            return new TextBlock
            {
                Text = text.ToUpperInvariant(),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = UiHelpers.AppBrush("TextSecondary"),
                Margin = new Thickness(0, 12, 0, 4)
            };
        }

        private Button TextButton(string content)
        {
            return new Button
            {
                Content = content,
                FontSize = 12,
                Padding = new Thickness(8, 3, 8, 3),
                Margin = new Thickness(2, 0, 0, 0),
                Background = Brushes.Transparent,
                BorderBrush = UiHelpers.AppBrush("SeparatorColor"),
                BorderThickness = new Thickness(1),
                Foreground = UiHelpers.AppBrush("TextPrimary"),
                Cursor = Cursors.Hand
            };
        }

        private static string Friendly(string type)
        {
            switch (type)
            {
                case "groups": return "Groups";
                case "recents": return "Recent";
                case "computer": return "This PC";
                case "network": return "Network";
                case "linux": return "Linux";
                default: return type;
            }
        }

        private void Raise()
        {
            var h = CloseRequested;
            if (h != null) h(this, EventArgs.Empty);
        }
    }
}
