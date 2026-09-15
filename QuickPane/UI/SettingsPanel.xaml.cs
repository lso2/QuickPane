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
    /// <summary>Settings UI. Every change is written to settings.json immediately (no Apply button).
    /// Sections and groups reorder by dragging their grip handle rather than with up/down buttons.</summary>
    public partial class SettingsPanel : UserControl
    {
        public event EventHandler CloseRequested;

        private SettingsStore _settings;
        private GroupStore _groups;
        private Point _dragStart;
        private string _openTab = "Groups";   // survives a rebuild, so reordering does not throw you back
        private bool _maybeDrag;
        // Every row in a list carries the same pair of move handlers, so without recording which one was
        // pressed the drag belongs to whichever row the pointer happens to be over when it passes the
        // threshold, and the neighbour below the one you grabbed is the row that moves.
        private object _pressedHandle;
        private const string ProfileGroupFormat = "QpProfileGroup";

        /// <summary>Remember the element a press landed on and start watching for a drag.</summary>
        private void BeginMaybeDrag(object handle, MouseButtonEventArgs e)
        {
            _pressedHandle = handle;
            _dragStart = e.GetPosition(null);
            _maybeDrag = true;
        }

        /// <summary>True once the pointer has travelled far enough for the press on this element to be a drag.</summary>
        private bool DragStarted(object handle, MouseEventArgs e, bool horizontalCounts)
        {
            if (!_maybeDrag || !ReferenceEquals(_pressedHandle, handle)) return false;
            if (e.LeftButton != MouseButtonState.Pressed) { _maybeDrag = false; return false; }
            var pt = e.GetPosition(null);
            bool far = Math.Abs(pt.Y - _dragStart.Y) >= SystemParameters.MinimumVerticalDragDistance ||
                       (horizontalCounts && Math.Abs(pt.X - _dragStart.X) >= SystemParameters.MinimumHorizontalDragDistance);
            if (!far) return false;
            _maybeDrag = false;
            return true;
        }

        private void EndMaybeDrag() { _maybeDrag = false; _pressedHandle = null; }

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

            // Grouped into tabs. Everything used to sit in one column, so finding a single option meant
            // scrolling past every other one.
            var tabs = new TabControl
            {
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Margin = new Thickness(0, 4, 0, 0)
            };

            // Groups: the profiles and the pinned folders inside them. First, because it is the tab
            // that gets opened to do actual work rather than to change a setting once.
            var groups = new StackPanel();
            var scroller = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Margin = new Thickness(0, 0, 0, 6)
            };
            // Without this the columns could only be reached by dragging the scrollbar.
            TrackStrip(scroller);
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            for (int i = 0; i < s.Profiles.Count; i++) row.Children.Add(BuildProfileColumn(i));
            row.Children.Add(BuildAddProfileColumn());
            scroller.Content = row;
            groups.Children.Add(scroller);
            tabs.Items.Add(Tab("Groups", groups, false));

            // Pane: how and where the pane appears, and which sections it shows in what order. Two
            // columns rather than three: the short option blocks stack down one side and the long list
            // of sections fills the other, instead of three short columns leaving a hole under them.
            var pane = new Grid();
            pane.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            pane.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var options = new StackPanel { Margin = new Thickness(0, 0, 24, 0) };
            options.Children.Add(PaneModeBlock(s));
            options.Children.Add(DockBlock(s));
            options.Children.Add(ProfileTabsBlock(s));
            options.Children.Add(RecentAppsBlock(s));
            AddCol(pane, 0, options);

            AddCol(pane, 1, SectionsBlock(s));
            tabs.Items.Add(Tab("Pane", pane, true));

            // Hotkeys: keyboard navigation of the pane.
            tabs.Items.Add(Tab("Hotkeys", HotkeysBlock(s), true));

            // Connections: SSH mounts.
            var conn = new StackPanel();
            conn.Children.Add(SshBlock(s));
            tabs.Items.Add(Tab("Connections", conn, true));

            // Backup: settings import and export.
            var backup = new StackPanel();
            backup.Children.Add(BackupBlock());
            tabs.Items.Add(Tab("Backup", backup, true));

            // Restore whichever tab was open. Selecting it before the handler is attached keeps the
            // restore from being mistaken for the person choosing a tab.
            int open = 0;
            for (int i = 0; i < tabs.Items.Count; i++)
            {
                var item = tabs.Items[i] as TabItem;
                if (item != null && string.Equals(item.Header as string, _openTab, StringComparison.Ordinal))
                { open = i; break; }
            }
            tabs.SelectedIndex = open;
            tabs.SelectionChanged += (a, b) =>
            {
                if (!ReferenceEquals(b.OriginalSource, tabs)) return;   // ignore inner selectors
                var item = tabs.SelectedItem as TabItem;
                if (item != null) _openTab = item.Header as string;
            };
            Host.Children.Add(tabs);
            FooterHost.Content = BuildFooter();
        }

        // ---- wheel routing --------------------------------------------
        //
        // The profile columns are a sideways strip inside a page that scrolls up and down, so the two
        // have to be told apart by the direction of the input rather than by where the pointer is:
        //
        //   vertical wheel          -> the settings page scrolls up and down
        //   Shift + vertical wheel  -> the profile columns move sideways
        //   horizontal wheel        -> the profile columns move sideways
        //
        // Sideways movement is never driven by a plain vertical wheel, because the system has no
        // convention for which way that should go and either choice reads as backwards.

        private const double WheelStep = 64;
        private const int WM_MOUSEHWHEEL = 0x020E;

        private ScrollViewer _strip;
        private HwndSource _hwheelSrc;

        /// <summary>Register the sideways strip and start reading horizontal wheel input for it.</summary>
        private void TrackStrip(ScrollViewer sv)
        {
            _strip = sv;
            sv.PreviewMouseWheel += (s, e) =>
            {
                if (e.Delta == 0) return;
                if ((Keyboard.Modifiers & ModifierKeys.Shift) == 0) return;  // plain wheel scrolls the page
                if (ScrollStripBy(_strip, -(e.Delta / 120.0) * WheelStep)) e.Handled = true;
            };
            HookHWheel();
        }

        private void HookHWheel()
        {
            var src = PresentationSource.FromVisual(this) as HwndSource;
            if (src == null || ReferenceEquals(src, _hwheelSrc)) return;
            if (_hwheelSrc != null) { try { _hwheelSrc.RemoveHook(HWheelProc); } catch { } }
            _hwheelSrc = src;
            src.AddHook(HWheelProc);
        }

        private IntPtr HWheelProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg != WM_MOUSEHWHEEL) return IntPtr.Zero;
            int delta = (short)((wParam.ToInt64() >> 16) & 0xFFFF);   // positive is rightward
            if (ScrollStripBy(_strip, (delta / 120.0) * WheelStep)) handled = true;
            return IntPtr.Zero;
        }

        /// <summary>Move the strip by a pixel amount. False when it has nowhere left to go that way.</summary>
        private static bool ScrollStripBy(ScrollViewer sv, double pixels)
        {
            if (sv == null || sv.ScrollableWidth <= 0) return false;
            double target = sv.HorizontalOffset + pixels;
            if (target < 0) target = 0;
            if (target > sv.ScrollableWidth) target = sv.ScrollableWidth;
            if (Math.Abs(target - sv.HorizontalOffset) < 0.5) return false;
            sv.ScrollToHorizontalOffset(target);
            return true;
        }

        /// <summary>One settings tab, themed to match the pane rather than the system default. The
        /// option tabs hold a handful of controls each and read better with room around them; Groups is
        /// a dense working surface and keeps its tighter spacing.</summary>
        private TabItem Tab(string header, UIElement content, bool roomy)
        {
            return new TabItem
            {
                Header = header,
                Content = new ScrollViewer
                {
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    Padding = roomy ? new Thickness(6, 14, 6, 14) : new Thickness(0, 8, 0, 0),
                    Content = content
                }
            };
        }

        /// <summary>Keyboard navigation: off by default, with the shortcut that focuses the pane.</summary>
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
                else box.Text = s.KeyboardNavHotkey;   // put back what was there rather than store nonsense
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

        // ---- live reordering -------------------------------------------
        //
        // The row being dragged is hidden and a spacer of the same size travels through the list in its
        // place, so what is on screen at any moment is what the list will look like if the button is
        // released there. The row itself has to stay in the panel: WPF runs a drag from the element that
        // started it, so lifting that element out of the tree ends the drag after the first move. The
        // rows the spacer displaces slide to their new places rather than jumping.

        private FrameworkElement _gapRow;      // the hidden row the spacer stands in for
        private FrameworkElement _gapSpacer;
        private Panel _gapPanel;

        /// <summary>Which slot a pointer at this height falls in. Measured from each row's layout slot,
        /// which includes its margins; adding up bare heights drifts by a row's margin per row and lands
        /// the drop several places from the pointer. Hidden rows have no slot to land in.</summary>
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

        /// <summary>Put the row where the spacer ended up. Safe now because the drag is over.</summary>
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

        /// <summary>Take the spacer out and show the row again, leaving the order as it was.</summary>
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
        /// Carry the row itself along as the pointer moves, so the rest of the list opens a space where
        /// it would land. A line drawn between two rows says where a drop would go; moving the row says
        /// what the list will look like, which is the thing being decided.
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

            // The rows now sit in the order they were dragged into, so the order is read off them
            // rather than recomputed, and the panel is left exactly as it looks.
            int order = 0;
            foreach (var child in _sectionRows.Children)
            {
                var fe = child as FrameworkElement;
                var sec = fe == null ? null : fe.Tag as SectionSetting;
                if (sec != null) sec.Order = order++;
            }
            _settings.Save();
        }

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

            // Declared ahead of the card so the card's own drop handler can reach the row list, which lets
            // a drop landing on the card's padding commit the same order a drop on the list would.
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

            // Clear activation control: the shown profile carries a filled Active badge, the others an
            // Activate button in the same place, so the two read as one control in two states.
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
            // The group rows get a panel of their own so one can be carried through the others while
            // the column's heading, fields and buttons stay where they are. The panel is the only drop
            // target in the list because a handler on each row marks the event handled, which stops the
            // panel from ever seeing the pointer and leaves the rows standing still under the ghost.
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
        /// The list itself takes the drop, because a row landing in a new place inside its own list is a
        /// reorder that the card behind it treats as a no-op, whereas a row arriving from another profile
        /// is a folder move the card already knows how to carry out.
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
                ApplyGap();                                   // the row takes the spacer's place
                CommitGroupOrder(rows, root, profileIndex);
            }
            else
            {
                OnProfileDrop(profileIndex, e);
            }
        }

        /// <summary>Write the order the rows are sitting in back to the folders behind them.</summary>
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

        /// <summary>
        /// What to install before a connection can mount, said in full. Two components are needed and
        /// neither ships with Windows, so the tab names both, links each one's download, and offers the
        /// same thing as two commands for anyone who would rather paste than click.
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

        /// <summary>One numbered install step: what it is, why, and the button that fetches it.</summary>
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

        /// <summary>A command block that can be read as well as copied, because a command shown as an
        /// image of text is no use to anyone who wants to run it.</summary>
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

        // ---- footer -------------------------------------------------

        /// <summary>
        /// The bar under every tab: what this is and which build it is on the left, support on the
        /// right. The version is read from the assembly, so it can never disagree with the binary, and
        /// GitHub is asked in the background whether a newer release exists.
        /// </summary>
        private FrameworkElement BuildFooter()
        {
            var bar = new Grid { Margin = new Thickness(10, 0, 10, 10) };
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var rule = new Border
            {
                Height = 1,
                Background = UiHelpers.AppBrush("SeparatorColor"),
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 0, 0, 10)
            };
            Grid.SetColumnSpan(rule, 2);
            bar.Children.Add(rule);

            var left = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 12, 0, 0)
            };
            left.Children.Add(new TextBlock
            {
                Text = "QuickPane",
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = UiHelpers.AppBrush("TextPrimary")
            });
            left.Children.Add(new TextBlock
            {
                Text = UpdateService.CurrentDisplay,
                FontSize = 13,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = UiHelpers.AppBrush("TextSecondary")
            });

            var status = new TextBlock
            {
                Text = "Checking for updates...",
                FontSize = 12,
                Margin = new Thickness(12, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = UiHelpers.AppBrush("TextSecondary")
            };
            left.Children.Add(status);

            var download = new Button
            {
                Visibility = Visibility.Collapsed,
                Cursor = Cursors.Hand,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
                Background = UiHelpers.AppBrush("AccentBrush"),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(14, 6, 14, 6),
                Margin = new Thickness(12, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            left.Children.Add(download);

            Grid.SetColumn(left, 0);
            bar.Children.Add(left);

            var support = BuildSupportButton();
            support.Margin = new Thickness(12, 12, 0, 0);
            support.HorizontalAlignment = HorizontalAlignment.Right;
            support.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(support, 1);
            bar.Children.Add(support);

            UpdateService.CheckAsync(r =>
            {
                if (r == null) return;
                if (!string.IsNullOrEmpty(r.Error)) { status.Text = "Update check failed"; status.ToolTip = r.Error; return; }
                if (!r.Available) { status.Text = "Up to date"; return; }

                status.Text = "Version " + r.Latest.ToString(3) + " is available";
                download.Content = "Download " + r.Latest.ToString(3);
                download.ToolTip = r.DownloadUrl;
                download.Visibility = Visibility.Visible;
                var url = r.DownloadUrl;
                download.Click += (a, b) => OpenUrl(url);
            });

            return bar;
        }

        private Button BuildSupportButton()
        {
            var btn = new Button
            {
                Cursor = Cursors.Hand,
                Background = Brushes.Transparent,
                BorderBrush = UiHelpers.AppBrush("SeparatorColor"),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(12, 6, 12, 6),
                Margin = new Thickness(0, 18, 0, 4)
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
                FontSize = 13,
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
                FontSize = 13,
                Margin = new Thickness(0, 6, 0, 0)
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
                FontSize = 13,
                Margin = new Thickness(0, 6, 0, 0)
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

        /// <summary>The heading that opens a column, with no gap above it.</summary>
        private TextBlock FirstSubHeading(string text)
        {
            var t = SubHeading(text);
            t.Margin = new Thickness(0, 0, 0, 8);
            return t;
        }

        private TextBlock SubHeading(string text)
        {
            return new TextBlock
            {
                Text = text.ToUpperInvariant(),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = UiHelpers.AppBrush("TextSecondary"),
                Margin = new Thickness(0, 16, 0, 8)
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
                case "ssh": return "SSH";
                case "search": return "Search box";
                case "appdefaults": return "App Folders";
                case "recentapps": return "Recent Apps";
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
