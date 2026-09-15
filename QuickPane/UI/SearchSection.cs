using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using QuickPane.Services;

namespace QuickPane.UI
{
    /// <summary>
    /// The pane's filter box. Typing narrows the rows already on screen rather than searching the disk,
    /// so it stays instant however many groups are pinned. Escape clears it, and the box keeps whatever
    /// was typed when the pane is rebuilt underneath it.
    /// </summary>
    internal sealed class SearchSection : UserControl
    {
        private readonly StackPanel _root = new StackPanel();
        private TextBox _box;
        private TextBlock _hint;

        public SearchSection()
        {
            Focusable = false;
            Content = _root;
        }

        public void Build(Action<string> navigate)
        {
            _root.Children.Clear();

            var host = new Border
            {
                Margin = new Thickness(8, 4, 8, 4),
                Padding = new Thickness(6, 2, 6, 2),
                CornerRadius = new CornerRadius(3),
                Background = UiHelpers.AppBrush("FieldBackground"),
                BorderBrush = UiHelpers.AppBrush("FieldBorder"),
                BorderThickness = new Thickness(1)
            };

            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var glyph = new TextBlock
            {
                Text = "",                       // Segoe MDL2 search
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 11,
                Opacity = 0.6,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0),
                Foreground = UiHelpers.AppBrush("TextSecondary")
            };
            Grid.SetColumn(glyph, 1);   // right of the box, like Explorer's own search field
            row.Children.Add(glyph);

            _box = new TextBox
            {
                FontSize = 12,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = UiHelpers.AppBrush("TextPrimary"),
                CaretBrush = UiHelpers.AppBrush("TextPrimary"),
                VerticalAlignment = VerticalAlignment.Center,
                Text = PaneFilter.Text
            };
            _box.TextChanged += (s, e) =>
            {
                PaneFilter.Text = _box.Text;
                if (_hint != null)
                    _hint.Visibility = _box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            };
            _box.PreviewKeyDown += OnKey;
            // Clicking a box inside a non-activating pane leaves the keyboard where it was, so the box
            // has to ask for it before WPF hands it the caret.
            _box.PreviewMouseLeftButtonDown += (s, e) => { GrabKeyboard(); };
            Grid.SetColumn(_box, 0);

            _hint = new TextBlock
            {
                Text = "Type to filter",
                FontSize = 12,
                Margin = new Thickness(2, 0, 0, 0),
                Opacity = 0.55,
                IsHitTestVisible = false,   // clicks fall through to the box underneath
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = UiHelpers.AppBrush("TextSecondary"),
                Visibility = _box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed
            };
            Grid.SetColumn(_hint, 0);

            row.Children.Add(_hint);
            row.Children.Add(_box);

            host.Child = row;
            _root.Children.Add(host);
        }

        private void OnKey(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                _box.Text = "";
                e.Handled = true;
            }
        }

        /// <summary>Put the caret in the box, for the keyboard shortcut that jumps to the pane.</summary>
        public void FocusBox()
        {
            if (_box == null) return;
            GrabKeyboard();
            _box.Focus();
            _box.SelectAll();
        }

        /// <summary>Move the keyboard into the pane's window first; without it the caret appears in the
        /// box but every keystroke goes to whatever the host application had focused.</summary>
        private void GrabKeyboard()
        {
            var pane = SidebarControl.Owning(this);
            if (pane != null) pane.FocusHostWindow();
        }
    }
}
