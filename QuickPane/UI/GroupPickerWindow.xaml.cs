using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using QuickPane.Models;
using QuickPane.Services;
using NM = QuickPane.Interop.NativeMethods;

namespace QuickPane.UI
{
    /// <summary>
    /// Borderless popup shown by the "Pin to Quick Pane" context menu verb. Lists existing groups
    /// plus a "New group" entry, then writes the pin. It styles itself directly from the registry
    /// theme because in pin mode the full ThemeService is not running.
    /// </summary>
    public partial class GroupPickerWindow : Window
    {
        private readonly GroupStore _groups;
        private readonly string _target;
        private RadioButton _newGroupRadio;

        public GroupPickerWindow(GroupStore groups, string target)
        {
            InitializeComponent();
            _groups = groups;
            _target = target;
            PathText.Text = target;
            ApplyTheme();
            BuildList();
        }

        public void ShowNearCursor()
        {
            NM.POINT pt;
            if (NM.GetCursorPos(out pt))
            {
                // Cursor is in pixels; convert to DIPs using the primary DPI scale.
                double scale = 1.0;
                try
                {
                    var m = System.Windows.Media.VisualTreeHelper.GetDpi(new System.Windows.Controls.Border());
                    scale = m.DpiScaleX <= 0 ? 1.0 : m.DpiScaleX;
                }
                catch { }

                Left = (pt.X / scale) - 20;
                Top = (pt.Y / scale) - 20;
            }
            ClampToScreen();
            Show();
            Activate();
            Topmost = true;
        }

        private void ClampToScreen()
        {
            double w = 300, h = 300;
            double sw = SystemParameters.WorkArea.Width;
            double sh = SystemParameters.WorkArea.Height;
            if (Left + w > sw) Left = Math.Max(0, sw - w);
            if (Top + h > sh) Top = Math.Max(0, sh - h);
            if (Left < 0) Left = 0;
            if (Top < 0) Top = 0;
        }

        private void BuildList()
        {
            GroupList.Children.Clear();
            bool first = true;
            foreach (var g in _groups.Groups)
            {
                var rb = new RadioButton
                {
                    Content = g.Name,
                    Tag = g,
                    GroupName = "grp",
                    Margin = new Thickness(0, 3, 0, 3),
                    IsChecked = first,
                    Foreground = _fg
                };
                first = false;
                GroupList.Children.Add(rb);
            }

            _newGroupRadio = new RadioButton
            {
                Content = "New group...",
                GroupName = "grp",
                Margin = new Thickness(0, 3, 0, 3),
                IsChecked = _groups.Groups.Count == 0,
                Foreground = _fg
            };
            _newGroupRadio.Checked += (s, e) => { NewGroupBox.Visibility = Visibility.Visible; NewGroupBox.Focus(); };
            _newGroupRadio.Unchecked += (s, e) => NewGroupBox.Visibility = Visibility.Collapsed;
            GroupList.Children.Add(_newGroupRadio);

            if (_groups.Groups.Count == 0) NewGroupBox.Visibility = Visibility.Visible;
        }

        private void OnConfirm(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_newGroupRadio.IsChecked == true)
                {
                    var name = NewGroupBox.Text;
                    if (string.IsNullOrWhiteSpace(name)) name = "Group";
                    var g = _groups.CreateGroup(name);
                    if (g != null) _groups.AddPin(g, _target);
                }
                else
                {
                    PinnedGroup selected = null;
                    foreach (var child in GroupList.Children)
                    {
                        var rb = child as RadioButton;
                        if (rb != null && rb.IsChecked == true && rb.Tag is PinnedGroup)
                        {
                            selected = (PinnedGroup)rb.Tag;
                            break;
                        }
                    }
                    if (selected != null) _groups.AddPin(selected, _target);
                }
            }
            catch (Exception ex)
            {
                Log.Error("pin confirm failed", ex);
            }
            Close();
        }

        private void OnCancel(object sender, RoutedEventArgs e)
        {
            Close();
        }

        // ---- self-styling from registry theme ----
        private Brush _fg = Brushes.Black;

        private void ApplyTheme()
        {
            bool dark = false;
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    var v = key?.GetValue("AppsUseLightTheme");
                    dark = v is int && (int)v == 0;
                }
            }
            catch { }

            var bg = dark ? Color.FromRgb(0x20, 0x20, 0x20) : Color.FromRgb(0xF7, 0xF7, 0xF7);
            var border = dark ? Color.FromRgb(0x3A, 0x3A, 0x3A) : Color.FromRgb(0xCF, 0xCF, 0xCF);
            _fg = new SolidColorBrush(dark ? Color.FromRgb(0xED, 0xED, 0xED) : Color.FromRgb(0x1A, 0x1A, 0x1A));

            Shell.Background = new SolidColorBrush(bg);
            Shell.BorderBrush = new SolidColorBrush(border);
            TitleText.Foreground = _fg;
            PathText.Foreground = _fg;
        }
    }
}
