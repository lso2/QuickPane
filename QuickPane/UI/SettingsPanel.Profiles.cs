// SettingsPanel.Profiles.cs
// Purpose: the profile columns in Settings: each profile's groups, groups folder, recents and width.
// Reason: one area of the Settings window per file, each under 30KB.
// Defines: SettingsPanel (partial): BuildProfileColumn, BuildProfileGroupRow, group and profile drops, BuildAddProfileColumn, BuildProfileFolderRow, profile setters.
// Doc: docs/notes/QuickPane/UI/SettingsPanel.Profiles.cs.md
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
        // ---- profile columns ----
        // With a card slightly lighter than the panel background, even an empty column is visible as a card.
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

            // Declared ahead of the card for the card's own drop handler. A drop on the card's padding
            // saves the same order as a drop on the list.
            StackPanel groupRows = null;
            string root = null;

            var border = new Border
            {
                Width = 250,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 0, 10, 0),
                Padding = new Thickness(8),
                Background = CardBrush(),
                BorderBrush = UiHelpers.AppBrush("SeparatorColor"),
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
            border.Drop += (a, e) => OnGroupRowsDrop(e, groupRows, root, index);

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

            // Activation control: the shown profile has a filled Active badge, and each other profile has an
            // Activate button in the same place. The badge and the button appear as a single control
            // with 2 states.
            if (active)
            {
                var act = new Border
                {
                    Background = UiHelpers.AppBrush("BadgeBackground"),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(10, 4, 10, 4),
                    Margin = new Thickness(6, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock
                    {
                        Text = "Active",
                        FontSize = 12,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = UiHelpers.AppBrush("TextPrimary")
                    }
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

            root = _settings.ExpandedGroupsPathFor(p);
            // The group rows are in a panel of their own. Dragging a row reorders it among the other rows,
            // and the column's heading, fields and buttons do not move. The panel is the only drop target
            // in the list. With a drop handler on each row marking the drag events handled, the panel's
            // DragOver handler would not run during a drag.
            groupRows = new StackPanel { AllowDrop = true };
            foreach (var grp in GroupStore.ListGroups(root))
                groupRows.Children.Add(BuildProfileGroupRow(index, grp.Item1, grp.Item2));
            groupRows.DragOver += (a, b) =>
            {
                if (!b.Data.GetDataPresent(ProfileGroupFormat)) { b.Effects = DragDropEffects.None; return; }
                bool copying = (b.KeyStates & DragDropKeyStates.ControlKey) != 0;
                b.Effects = copying ? DragDropEffects.Copy : DragDropEffects.Move;
                b.Handled = true;
                UpdateGhost(b);
                if (_draggedGroupRow == null) return;
                MoveGap(groupRows, b.GetPosition(groupRows).Y);
            };
            groupRows.Drop += (a, b) => OnGroupRowsDrop(b, groupRows, root, index);
            sp.Children.Add(groupRows);

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

        private FrameworkElement _draggedGroupRow;

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
                BeginMaybeDrag(grid, e);
            };
            grid.PreviewMouseLeftButtonUp += (s, e) => EndMaybeDrag();
            grid.MouseMove += (s, e) =>
            {
                if (!DragStarted(grid, e, true)) return;
                _draggedGroupRow = grid;
                try
                {
                    var data = new DataObject();
                    data.SetData(ProfileGroupFormat, folder);
                    StartGhost(grid, e);
                    OpenGap(grid.Parent as Panel, grid);
                    DragDrop.DoDragDrop(grid, data, DragDropEffects.Move | DragDropEffects.Copy);
                }
                catch (Exception ex) { Log.Error("profile group drag", ex); }
                finally { CancelGap(); EndGhost(); _draggedGroupRow = null; }
            };

            grid.Tag = folder;
            return grid;
        }

        /// <summary>
        /// Handle every drop on the group list. A row dropped inside its own profile is a reorder and is
        /// saved here. A row dropped from another profile, or with Ctrl held, is passed to OnProfileDrop
        /// as a folder move or copy.
        /// </summary>
        private void OnGroupRowsDrop(DragEventArgs e, StackPanel rows, string root, int profileIndex)
        {
            e.Handled = true;
            if (!e.Data.GetDataPresent(ProfileGroupFormat)) return;
            var dragged = e.Data.GetData(ProfileGroupFormat) as string;
            if (string.IsNullOrEmpty(dragged)) return;

            string destRoot = _settings.ExpandedGroupsPathFor(_settings.Current.Profiles[profileIndex]);
            string srcRoot = System.IO.Path.GetDirectoryName(dragged);
            bool copy = (e.KeyStates & DragDropKeyStates.ControlKey) != 0;

            if (!copy && string.Equals(srcRoot, (destRoot ?? "").TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            {
                ApplyGap();                                   // the dragged row is moved to the spacer's position
                CommitGroupOrder(rows, root, profileIndex);
            }
            else
            {
                OnProfileDrop(profileIndex, e);
            }
        }

        /// <summary>Save the on-screen order of the rows to their group folders.</summary>
        private void CommitGroupOrder(StackPanel rows, string root, int profileIndex)
        {
            if (rows == null) return;
            var ordered = new List<string>();
            foreach (var child in rows.Children)
            {
                var fe = child as FrameworkElement;
                var folder = fe == null ? null : fe.Tag as string;
                if (!string.IsNullOrEmpty(folder)) ordered.Add(folder);
            }
            if (ordered.Count == 0) return;
            Log.Info("group order in \"" + root + "\" is now " +
                     string.Join(", ", ordered.Select(System.IO.Path.GetFileName)) + ".");
            GroupStore.ReorderGroupFolders(root, ordered);
            ReloadIfActive(profileIndex);
            BuildUI();
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
                Width = 150,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 0, 10, 0),
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
                var picked = FolderPicker.Pick("Pick a groups folder", p.GroupsPath);
                if (!string.IsNullOrEmpty(picked)) { box.Text = picked; SetProfileFolder(index, picked); }
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
    }
}
