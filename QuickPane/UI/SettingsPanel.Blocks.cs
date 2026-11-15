// SettingsPanel.Blocks.cs
// Purpose: the Settings blocks for hotkeys, backup, pane mode, dock, recent apps, profile tabs and pane sections.
// Reason: one area of the Settings window per file, each under 30KB.
// Defines: SettingsPanel (partial): HotkeysBlock, BackupBlock, PaneModeBlock, DockBlock, RecentAppsBlock, SourceRadio, ProfileTabsBlock, SectionsBlock, CommitPath, BuildStepper.
// Doc: docs/notes/QuickPane/UI/SettingsPanel.Blocks.cs.md
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
        /// <summary>The keyboard navigation block: the on/off switch (off by default) and the shortcut.</summary>
        private UIElement HotkeysBlock(AppSettings s)
        {
            var panel = new StackPanel();
            panel.Children.Add(SubHeading("Keyboard navigation"));

            var on = new CheckBox
            {
                Content = "Let a keyboard shortcut move focus into the pane",
                IsChecked = s.KeyboardNav,
                FontSize = 13,
                Foreground = UiHelpers.AppBrush("TextPrimary"),
                Margin = new Thickness(0, 0, 0, 10)
            };
            on.Checked += (a, b) => { s.KeyboardNav = true; _settings.Save(); };
            on.Unchecked += (a, b) => { s.KeyboardNav = false; _settings.Save(); };
            panel.Children.Add(on);

            var rowGrid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var lbl = new TextBlock
            {
                Text = "Shortcut",
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0),
                Foreground = UiHelpers.AppBrush("TextSecondary")
            };
            Grid.SetColumn(lbl, 0);
            rowGrid.Children.Add(lbl);

            var box = new TextBox
            {
                Text = s.KeyboardNavHotkey,
                FontSize = 13,
                Foreground = UiHelpers.AppBrush("TextPrimary"),
                Background = UiHelpers.AppBrush("FieldBackground"),
                BorderBrush = UiHelpers.AppBrush("FieldBorder"),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(4, 2, 4, 2),
                Width = 160,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            box.LostFocus += (a, b) =>
            {
                uint m, v;
                if (HotkeyService.TryParse(box.Text, out m, out v))
                {
                    s.KeyboardNavHotkey = box.Text.Trim();
                    _settings.Save();
                }
                else box.Text = s.KeyboardNavHotkey;   // an entry that does not parse is discarded and the saved hotkey is shown again
            };
            Grid.SetColumn(box, 1);
            rowGrid.Children.Add(box);
            panel.Children.Add(rowGrid);

            panel.Children.Add(new TextBlock
            {
                Text = "Modifiers joined with +, ending in a key: ctrl+shift+Q. At least one modifier is "
                     + "required, so a plain key is never taken from the rest of the system. "
                     + "Once focus is in the pane, the arrow keys move between folders and Enter opens one.",
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 520,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 4, 0, 0),
                FontSize = 12,
                Opacity = 0.75,
                Foreground = UiHelpers.AppBrush("TextSecondary")
            });

            return panel;
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
                FontSize = 12,
                Foreground = UiHelpers.AppBrush("TextSecondary"),
                Margin = new Thickness(0, 8, 0, 0),
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
                PickerPlaces.AddTo(dlg);
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
            var sp = new StackPanel();
            sp.Children.Add(FirstSubHeading("Pane mode"));
            string m = (s.Mode ?? "inside").ToLowerInvariant();
            sp.Children.Add(ModeRadio("Inside Explorer window", m == "inside", () => { s.Mode = "inside"; _settings.Save(); }));
            sp.Children.Add(ModeRadio("Beside Explorer window", m == "beside", () => { s.Mode = "beside"; _settings.Save(); }));
            sp.Children.Add(ModeRadio("Off (desktop dock only)", m == "off", () => { s.Mode = "off"; _settings.Save(); }));
            return sp;
        }

        private FrameworkElement DockBlock(AppSettings s)
        {
            var sp = new StackPanel();
            sp.Children.Add(SubHeading("Desktop dock"));
            sp.Children.Add(ModeCheck("Show desktop dock (screen edge)", s.DesktopDock, on => { s.DesktopDock = on; _settings.Save(); }));
            sp.Children.Add(ModeCheck("Auto-hide (slide out on hover)", s.DesktopDockAutoHide, on => { s.DesktopDockAutoHide = on; _settings.Save(); }));
            sp.Children.Add(ModeCheck("Show on all virtual desktops", s.DesktopDockAllDesktops, on => { s.DesktopDockAllDesktops = on; _settings.Save(); }));
            return sp;
        }

        /// <summary>Which apps the Recent Apps section lists.</summary>
        private FrameworkElement RecentAppsBlock(AppSettings s)
        {
            var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 4) };
            sp.Children.Add(SubHeading("Recent Apps"));

            string m = (s.RecentAppsSource ?? "both").ToLowerInvariant();
            sp.Children.Add(SourceRadio("Apps I open and apps I save from", m == "both",
                () => { s.RecentAppsSource = "both"; _settings.Save(); }));
            sp.Children.Add(SourceRadio("Only apps I open", m == "open",
                () => { s.RecentAppsSource = "open"; _settings.Save(); }));
            sp.Children.Add(SourceRadio("Only apps whose Save and Open dialogs I use", m == "dialogs",
                () => { s.RecentAppsSource = "dialogs"; _settings.Save(); }));

            sp.Children.Add(new TextBlock
            {
                Text = "A row opens its app. The folder that app's dialog was last sent to is on the "
                     + "row's right-click menu.",
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 520,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 6, 0, 0),
                FontSize = 12,
                Opacity = 0.75,
                Foreground = UiHelpers.AppBrush("TextSecondary")
            });
            return sp;
        }

        private FrameworkElement SourceRadio(string label, bool isChecked, Action onSelect)
        {
            var rb = new RadioButton
            {
                Content = label,
                IsChecked = isChecked,
                GroupName = "qpRecentAppsSource",
                Foreground = UiHelpers.AppBrush("TextPrimary"),
                FontSize = 13,
                Margin = new Thickness(0, 6, 0, 0)
            };
            rb.Checked += (a, b) => onSelect();
            return rb;
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
            sp.Children.Add(FirstSubHeading("Sections"));

            _sectionRows = new StackPanel { AllowDrop = true };
            foreach (var sec in s.Sections.OrderBy(x => x.Order).ToList())
                _sectionRows.Children.Add(BuildSectionRow(sec));
            _sectionRows.DragOver += OnSectionsDragOver;
            _sectionRows.Drop += OnSectionsDrop;

            sp.Children.Add(_sectionRows);
            return sp;
        }

        /// <summary>
        /// Move the spacer through the list with the pointer, with the other rows moved aside around it.
        /// The list on screen shows the order a drop at that point would save.
        /// </summary>
        private void OnSectionsDragOver(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(SectionFormat)) { e.Effects = DragDropEffects.None; return; }
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
            UpdateGhost(e);

            if (_draggedSection == null || _sectionRows == null) return;

            MoveGap(_sectionRows, e.GetPosition(_sectionRows).Y);
        }

        private void OnSectionsDrop(object sender, DragEventArgs e)
        {
            e.Handled = true;
            if (!e.Data.GetDataPresent(SectionFormat) || _sectionRows == null) return;

            ApplyGap();

            // After ApplyGap the panel's children are in the dragged order. The saved order is read off
            // the children in sequence, and the panel is left as displayed.
            int order = 0;
            foreach (var child in _sectionRows.Children)
            {
                var fe = child as FrameworkElement;
                var sec = fe == null ? null : fe.Tag as SectionSetting;
                if (sec != null) sec.Order = order++;
            }
            _settings.Save();
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
    }
}
