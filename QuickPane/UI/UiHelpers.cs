// UiHelpers.cs
// Purpose: shared builders for the pane: section separators, headers, inline edit boxes and the expand/collapse animation.
// Reason: SidebarControl.xaml.cs passed 30KB with this second class in it.
// Defines: UiHelpers.
// Doc: docs/notes/QuickPane/UI/UiHelpers.cs.md
using System;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using QuickPane.Models;
using QuickPane.Services;
using NM = QuickPane.Interop.NativeMethods;

namespace QuickPane.UI
{
    /// <summary>Shared UI helpers: section separators, headers, inline rename, theme brushes and the
    /// expand/collapse animation.</summary>
    internal static class UiHelpers
    {
        public const string SeparatorTag = "qp-separator";

        /// <summary>Marks a header and the rows under it as a single block. Filtering hides the whole
        /// block, header included, when no row in it matches.</summary>
        public const string FilterBlockTag = "qp-block";

        public static Border MakeSeparator()
        {
            return new Border
            {
                Height = 1,
                Margin = new Thickness(8, 6, 8, 6),
                Background = AppBrush("SeparatorColor"),
                Tag = SeparatorTag   // a rule between rows means nothing once the rows are filtered out
            };
        }

        public static Brush AppBrush(string key)
        {
            var b = Application.Current != null ? Application.Current.TryFindResource(key) as Brush : null;
            return b ?? Brushes.Gray;
        }

        /// <summary>
        /// Build a section/group header: a left chevron plus an uppercase label. Clicking anywhere on the
        /// header toggles expand/collapse, and double-clicking calls onDoubleClick (used to rename).
        /// </summary>
        public static Grid BuildHeader(string text, Action toggleClicked, Action onDoubleClick, out RotateTransform chevron, out TextBlock label)
        {
            var grid = new Grid { Margin = new Thickness(0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            chevron = new RotateTransform(0);
            var glyph = new TextBlock
            {
                Text = "", // Segoe MDL2 ChevronRight, rotates to point down when expanded
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 10,
                Opacity = 1,   // always shown, including before hovering
                Foreground = AppBrush("ChevronColor"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = chevron
            };
            var chevronHit = new Border
            {
                Width = 22,
                Background = Brushes.Transparent,
                Cursor = System.Windows.Input.Cursors.Hand,
                Child = glyph
            };
            Grid.SetColumn(chevronHit, 0);
            grid.Children.Add(chevronHit);

            label = new TextBlock
            {
                Text = (text ?? string.Empty).ToUpperInvariant(),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = AppBrush("TextSecondary"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2, 0, 8, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetColumn(label, 1);
            grid.Children.Add(label);

            grid.Margin = new Thickness(6, 6, 8, 4);
            grid.MouseEnter += (s, e) => glyph.Opacity = 1;
            grid.Background = Brushes.Transparent;
            grid.Cursor = System.Windows.Input.Cursors.Hand;

            // Clicking once toggles after a 220 ms timer. Double-clicking stops that timer and renames, with
            // no toggle from the first click.
            var dcTimer = new System.Windows.Threading.DispatcherTimer
            { Interval = TimeSpan.FromMilliseconds(220) };
            dcTimer.Tick += (s, e) => { dcTimer.Stop(); if (toggleClicked != null) toggleClicked(); };

            grid.PreviewMouseLeftButtonDown += (s, e) =>
            {
                if (e.ClickCount == 2 && onDoubleClick != null)
                {
                    dcTimer.Stop();
                    e.Handled = true;
                    onDoubleClick();
                }
            };
            grid.MouseLeftButtonUp += (s, e) =>
            {
                if (onDoubleClick == null) { if (toggleClicked != null) toggleClicked(); return; }
                if (!dcTimer.IsEnabled) dcTimer.Start();
            };
            return grid;
        }

        /// <summary>Swap an element for a TextBox to rename it in place. Enter commits, Escape cancels.</summary>
        public static void InlineRename(Panel parent, UIElement target, string initial, Action<string> onCommit, Action onCancel)
        {
            int idx = parent.Children.IndexOf(target);
            if (idx < 0) return;

            var tb = new TextBox
            {
                Text = initial ?? string.Empty,
                FontSize = 12,
                Margin = new Thickness(8, 2, 8, 2)
            };
            bool finished = false;

            Action cancel = () => { if (finished) return; finished = true; onCancel(); };
            Action commit = () =>
            {
                if (finished) return;
                finished = true;
                var v = tb.Text;
                onCommit(v);
            };

            tb.KeyDown += (s, e) =>
            {
                if (e.Key == System.Windows.Input.Key.Enter) { e.Handled = true; commit(); }
                else if (e.Key == System.Windows.Input.Key.Escape) { e.Handled = true; cancel(); }
            };
            tb.LostKeyboardFocus += (s, e) => cancel();

            parent.Children.Remove(target);
            parent.Children.Insert(idx, tb);
            tb.Focus();
            tb.SelectAll();
        }

        /// <summary>Animate a panel open/closed via MaxHeight, and rotate the matching chevron.</summary>
        public static void ToggleExpand(FrameworkElement panel, RotateTransform chevron, bool expand)
        {
            var dur = TimeSpan.FromMilliseconds(150);
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

            if (chevron != null)
            {
                var rot = new DoubleAnimation(chevron.Angle, expand ? 90 : 0, dur) { EasingFunction = ease };
                chevron.BeginAnimation(RotateTransform.AngleProperty, rot);
            }

            // With FillBehavior.Stop, MaxHeight is the base value set below once the animation ends.
            // A replaced animation can end without raising Completed, and MaxHeight is not cleared in
            // a Completed handler.
            if (expand)
            {
                panel.Visibility = Visibility.Visible;
                panel.MaxHeight = double.PositiveInfinity; // base value: fully open after the animation
                panel.Measure(new Size(panel.ActualWidth > 0 ? panel.ActualWidth : double.PositiveInfinity, double.PositiveInfinity));
                double target = panel.DesiredSize.Height;
                if (target <= 0) { panel.BeginAnimation(FrameworkElement.MaxHeightProperty, null); return; }
                var anim = new DoubleAnimation(0, target, dur) { EasingFunction = ease, FillBehavior = FillBehavior.Stop };
                panel.BeginAnimation(FrameworkElement.MaxHeightProperty, anim);
            }
            else
            {
                double from = panel.ActualHeight;
                panel.MaxHeight = 0; // base value: closed after the animation
                var anim = new DoubleAnimation(from, 0, dur) { EasingFunction = ease, FillBehavior = FillBehavior.Stop };
                anim.Completed += (s, e) => { if (panel.MaxHeight == 0) panel.Visibility = Visibility.Collapsed; };
                panel.BeginAnimation(FrameworkElement.MaxHeightProperty, anim);
            }
        }
    }
}
