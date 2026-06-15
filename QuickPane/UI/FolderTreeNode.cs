using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using QuickPane.Interop;
using QuickPane.Services;

namespace QuickPane.UI
{
    /// <summary>
    /// A folder row that can expand to reveal its subfolders inline, like the native Explorer tree.
    /// Children are enumerated lazily on first expand. The top row is exposed so the Groups section can
    /// attach the pin context menu and drag handling to a pinned folder, while nested rows get a simple
    /// open menu.
    /// </summary>
    internal sealed class FolderTreeNode : StackPanel
    {
        private readonly string _path;
        private readonly int _level;
        private readonly Action<string> _navigate;
        private readonly Border _row;
        private readonly StackPanel _children;
        private readonly RotateTransform _chevron;
        private readonly TextBlock _chevGlyph;
        private bool _loaded;
        private bool _expanded;
        private readonly bool _hasChildren;

        public Border Row { get { return _row; } }

        public FolderTreeNode(string display, string path, bool exists, Action<string> navigate, int level, bool isFile = false, ImageSource overrideIcon = null)
        {
            _path = path;
            _level = level;
            _navigate = navigate;

            _hasChildren = !isFile && exists && HasChildren(path);
            _chevron = new RotateTransform(0);
            _chevGlyph = new TextBlock
            {
                Text = "", // Segoe MDL2 ChevronRight, same glyph and size as the group caret
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 10,
                Foreground = UiHelpers.AppBrush("ChevronColor"),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = _chevron,
                Opacity = 0 // shown on row hover (and kept while expanded)
            };
            var chevHit = new Border { Width = 18, Background = Brushes.Transparent, Cursor = Cursors.Hand, Child = _chevGlyph };
            chevHit.MouseLeftButtonUp += (s, e) => { e.Handled = true; Toggle(); };

            var icon = new Image
            {
                Width = 16,
                Height = 16,
                Margin = new Thickness(2, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Source = overrideIcon ?? IconHelper.GetIcon(path, exists, isFile)
            };
            var label = new TextBlock
            {
                Text = display,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = exists ? UiHelpers.AppBrush("TextPrimary") : UiHelpers.AppBrush("BrokenLinkColor")
            };

            var content = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(level * 14, 0, 0, 0) };
            content.Children.Add(chevHit);
            content.Children.Add(icon);
            content.Children.Add(label);

            _row = new Border
            {
                Background = Brushes.Transparent,
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(6, 4, 8, 4),
                Cursor = Cursors.Hand,
                Child = content,
                ToolTip = exists ? path : path + "  (target not found)",
                Opacity = exists ? 1 : 0.5
            };
            _row.MouseEnter += (s, e) => { _row.Background = UiHelpers.AppBrush("ItemHoverBackground"); if (_hasChildren) _chevGlyph.Opacity = 1; };
            _row.MouseLeave += (s, e) => { _row.Background = Brushes.Transparent; if (!_expanded) _chevGlyph.Opacity = 0; };
            _row.MouseLeftButtonUp += (s, e) => { if (exists && !isFile) _navigate(path); else if (isFile) Open(path); };
            if (level > 0) _row.ContextMenu = BasicMenu(path, isFile);

            // Files dropped from Explorer onto a real folder move/copy/link into it, like the native tree.
            if (!isFile && SafeDir(path))
            {
                _row.AllowDrop = true;
                _row.DragOver += OnFileDragOver;
                _row.Drop += OnFileDrop;
            }

            _children = new StackPanel { Visibility = Visibility.Collapsed, MaxHeight = 0 };

            Children.Add(_row);
            Children.Add(_children);

            // Restore a previously expanded subtree so it survives rebuilds, profile and window switches.
            if (_hasChildren && UiState.GetExpanded("tree:" + _path, false))
            {
                _expanded = true;
                LoadChildren();
                _chevGlyph.Opacity = 1;
                _children.Visibility = Visibility.Visible;
                _children.MaxHeight = double.PositiveInfinity;
                _chevron.Angle = 90;
            }
        }

        private void OnFileDragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var sources = e.Data.GetData(DataFormats.FileDrop) as string[];
                e.Effects = FileDropOps.ComputeEffect(e.AllowedEffects, e.KeyStates, sources, _path);
                _row.Background = UiHelpers.AppBrush("ItemActiveBackground");
                e.Handled = true;
            }
        }

        private void OnFileDrop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            e.Handled = true;
            _row.Background = Brushes.Transparent;
            var sources = e.Data.GetData(DataFormats.FileDrop) as string[];
            var effect = FileDropOps.ComputeEffect(e.AllowedEffects, e.KeyStates, sources, _path);
            FileDropOps.Perform(sources, _path, effect);
        }

        private static bool SafeDir(string path)
        {
            try { return !string.IsNullOrEmpty(path) && Directory.Exists(path); } catch { return false; }
        }

        private void Toggle()
        {
            _expanded = !_expanded;
            UiState.SetExpanded("tree:" + _path, _expanded);
            if (_expanded && !_loaded) LoadChildren();
            if (_expanded) _chevGlyph.Opacity = 1; // keep the caret visible while open
            UiHelpers.ToggleExpand(_children, _chevron, _expanded);
        }

        private void LoadChildren()
        {
            _loaded = true;
            try
            {
                var dirs = Directory.GetDirectories(_path)
                    .OrderBy(d => Path.GetFileName(d), StringComparer.CurrentCultureIgnoreCase);
                foreach (var d in dirs)
                {
                    var node = new FolderTreeNode(Path.GetFileName(d), d, true, _navigate, _level + 1);
                    _children.Children.Add(node);
                }
            }
            catch (Exception ex)
            {
                Log.Error("expand folder '" + _path + "'", ex);
            }
        }

        private static bool HasChildren(string path)
        {
            try { return Directory.EnumerateDirectories(path).Any(); }
            catch { return false; }
        }

        private ContextMenu BasicMenu(string path, bool isFile)
        {
            var menu = new ContextMenu();
            if (isFile)
            {
                menu.Items.Add(MakeItem("Open file", () => Open(path)));
                menu.Items.Add(MakeItem("Open containing folder", () => { try { _navigate(Path.GetDirectoryName(path)); } catch { } }));
            }
            else
            {
                menu.Items.Add(MakeItem("Open", () => _navigate(path)));
                menu.Items.Add(MakeItem("Open in new window", () => ExplorerNavigator.OpenNewWindow(path)));
            }
            menu.Items.Add(MakeItem("Copy path", () => { try { Clipboard.SetText(path); } catch { } }));
            return menu;
        }

        private static void Open(string path)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); }
            catch (Exception ex) { Log.Error("open '" + path + "'", ex); }
        }

        private static MenuItem MakeItem(string header, Action action)
        {
            var mi = new MenuItem { Header = header };
            mi.Click += (s, e) => { try { action(); } catch (Exception ex) { Log.Error("tree menu", ex); } };
            return mi;
        }
    }
}
