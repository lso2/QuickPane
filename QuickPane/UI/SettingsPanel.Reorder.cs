// SettingsPanel.Reorder.cs
// Purpose: drag reordering of sections and groups in Settings, with the live gap and the drag ghost.
// Reason: one area of the Settings window per file, each under 30KB.
// Defines: SettingsPanel (partial): live reordering, BuildSectionRow, BuildGroupsManager, MakeReorderable, ghost and reorder helpers.
// Doc: docs/notes/QuickPane/UI/SettingsPanel.Reorder.cs.md
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
        // ---- live reordering -------------------------------------------
        //
        // During a drag the dragged row is hidden, and a spacer of the same size is moved through the
        // list in its place. The list on screen shows the order it will have if the button is released
        // at that point. The dragged row is not removed from the panel. Removing the element that
        // DoDragDrop was called on ends the drag after the first move. Displaced rows are animated to
        // their new places.

        private FrameworkElement _gapRow;      // the hidden row the spacer stands in for
        private FrameworkElement _gapSpacer;
        private Panel _gapPanel;

        /// <summary>The slot index for a pointer at this height, measured from each row's layout slot,
        /// margins included. Summing bare row heights misses each row's margin. Hidden rows have no
        /// slot.</summary>
        private static int SlotIn(Panel panel, double y)
        {
            for (int i = 0; i < panel.Children.Count; i++)
            {
                var child = panel.Children[i] as FrameworkElement;
                if (child == null || child.Visibility != Visibility.Visible) continue;
                var slot = LayoutInformation.GetLayoutSlot(child);
                if (y < slot.Top + slot.Height / 2) return i;
            }
            return panel.Children.Count;
        }

        private const int GlideMs = 160;

        /// <summary>Hide the dragged row and stand a spacer its size where it was.</summary>
        private void OpenGap(Panel panel, FrameworkElement row)
        {
            CancelGap();
            if (panel == null || row == null || panel.Children.IndexOf(row) < 0) return;

            _gapPanel = panel;
            _gapRow = row;
            _gapSpacer = new Border
            {
                Height = row.ActualHeight,
                Margin = row.Margin,
                Background = Brushes.Transparent,
                IsHitTestVisible = false
            };
            panel.Children.Insert(panel.Children.IndexOf(row), _gapSpacer);
            row.Visibility = Visibility.Collapsed;
        }

        /// <summary>Move the spacer to the slot under the pointer and glide whatever it displaces.</summary>
        private void MoveGap(Panel panel, double y)
        {
            if (_gapSpacer == null || !ReferenceEquals(_gapPanel, panel)) return;
            MoveRowLive(panel, _gapSpacer, SlotIn(panel, y));
        }

        /// <summary>Move the dragged row to the spacer's position. Called from the Drop handlers.</summary>
        private void ApplyGap()
        {
            if (_gapSpacer == null || _gapPanel == null || _gapRow == null) { CancelGap(); return; }

            var panel = _gapPanel;
            var row = _gapRow;
            var spacer = _gapSpacer;
            _gapPanel = null; _gapRow = null; _gapSpacer = null;

            panel.Children.Remove(row);
            int at = panel.Children.IndexOf(spacer);
            panel.Children.Remove(spacer);
            if (at < 0 || at > panel.Children.Count) at = panel.Children.Count;
            panel.Children.Insert(at, row);
            row.Visibility = Visibility.Visible;
        }

        /// <summary>Take out the spacer and show the dragged row again in its original position.</summary>
        private void CancelGap()
        {
            if (_gapSpacer != null && _gapPanel != null) _gapPanel.Children.Remove(_gapSpacer);
            if (_gapRow != null) _gapRow.Visibility = Visibility.Visible;
            _gapPanel = null; _gapRow = null; _gapSpacer = null;
        }

        /// <summary>Put an element in a new slot and let everything that moved glide there.</summary>
        private static void MoveRowLive(Panel panel, FrameworkElement row, int target)
        {
            int current = panel.Children.IndexOf(row);
            if (current < 0) return;
            if (target > current) target--;          // lifting the element out shifts the rest up one
            if (target == current || target < 0 || target >= panel.Children.Count) return;

            var was = new Dictionary<FrameworkElement, double>();
            foreach (var child in panel.Children)
            {
                var fe = child as FrameworkElement;
                if (fe != null) was[fe] = LayoutInformation.GetLayoutSlot(fe).Top;
            }

            panel.Children.Remove(row);
            panel.Children.Insert(target, row);
            panel.UpdateLayout();

            foreach (var pair in was)
            {
                var fe = pair.Key;
                double shift = pair.Value - LayoutInformation.GetLayoutSlot(fe).Top;
                if (Math.Abs(shift) < 0.5) continue;
                Glide(fe, shift);
            }
        }

        /// <summary>Start a row at where it used to be and animate it to where it now is.</summary>
        private static void Glide(FrameworkElement fe, double from)
        {
            var slide = fe.RenderTransform as TranslateTransform;
            if (slide == null) { slide = new TranslateTransform(); fe.RenderTransform = slide; }
            slide.BeginAnimation(TranslateTransform.YProperty, null);
            slide.Y = from;
            slide.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(from, 0, TimeSpan.FromMilliseconds(GlideMs))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }

        private const string SectionFormat = "QpSection";
        private StackPanel _sectionRows;
        private FrameworkElement _draggedSection;

        // ---- sections ----
        private FrameworkElement BuildSectionRow(SectionSetting sec)
        {
            var grid = new Grid { Margin = new Thickness(0, 4, 0, 4), Background = Brushes.Transparent };
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
                FontSize = 13,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = UiHelpers.AppBrush("TextPrimary")
            };
            Grid.SetColumn(name, 2);
            grid.Children.Add(name);

            grid.Tag = sec;
            grip.Cursor = Cursors.SizeNS;
            grip.PreviewMouseLeftButtonDown += (a, b) => BeginMaybeDrag(grip, b);
            grip.PreviewMouseLeftButtonUp += (a, b) => EndMaybeDrag();
            grip.MouseMove += (a, b) =>
            {
                if (!DragStarted(grip, b, false)) return;
                _draggedSection = grid;
                try
                {
                    var data = new DataObject();
                    data.SetData(SectionFormat, sec.Type);
                    StartGhost(grid, b);
                    OpenGap(_sectionRows, grid);   // the row leaves the list and its ghost follows the pointer
                    DragDrop.DoDragDrop(grid, data, DragDropEffects.Move);
                }
                catch (Exception ex) { Log.Error("section drag", ex); }
                finally { CancelGap(); EndGhost(); _draggedSection = null; }
            };
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
            handle.PreviewMouseLeftButtonDown += (s, e) => BeginMaybeDrag(handle, e);
            handle.PreviewMouseLeftButtonUp += (s, e) => EndMaybeDrag();
            handle.MouseMove += (s, e) =>
            {
                if (!DragStarted(handle, e, false)) return;
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
    }
}
