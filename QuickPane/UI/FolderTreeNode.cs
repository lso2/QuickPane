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
    /// <summary>Context handed to a pinned row so an ambiguous drop can offer "Pin to group".</summary>
    internal sealed class PinDropContext
    {
        public string GroupName;
        public Action<string[]> PinHere;
    }

    /// <summary>
    /// A folder or file row. Folder rows can expand to reveal subfolders inline, like the native
    /// Explorer tree; file rows open their target. Children are enumerated lazily on the worker on
    /// first expand. The constructor performs no disk access at all: existence and file-vs-folder
    /// come from the caller (probe cache), so building rows can never stall the input-attached UI
    /// thread on a dead network target.
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
        private readonly PinDropContext _pinContext;
        private bool _loaded;
        private bool _expanded;
        private readonly bool _hasChildren;

        public Border Row { get { return _row; } }

        public FolderTreeNode(string display, string path, bool exists, Action<string> navigate, int level,
            bool isFile = false, ImageSource overrideIcon = null, PinDropContext pinContext = null)
        {
            _path = path;
            _level = level;
            _navigate = navigate;
            _pinContext = pinContext;

            // Optimistic: any existing folder gets an expander. Probing the directory here cost one
            // network roundtrip per row per rebuild; expanding an empty folder now just shows nothing.
            _hasChildren = !isFile && exists;
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

            // Items dropped from Explorer onto a real folder move/copy/link into it (or pin), below.
            if (!isFile && exists)
            {
                _row.AllowDrop = true;
                _row.DragOver += OnFileDragOver;
                _row.DragLeave += OnFileDragLeave;
                _row.Drop += OnFileDrop;
            }

            _children = new StackPanel { Visibility = Visibility.Collapsed, MaxHeight = 0 };

            Children.Add(_row);
            Children.Add(_children);

            // Restore a previously expanded subtree so it survives rebuilds, profile and window
            // switches. Gated on a confirmed-alive target: before the probe answers, expanding a
            // dead share here would enumerate it during construction. The rebuild that follows the
            // probe restores the subtree moments later.
            if (_hasChildren && UiState.GetExpanded("tree:" + _path, false) && PathStatus.ConfirmedAlive(_path))
            {
                _expanded = true;
                LoadChildren();
                _chevGlyph.Opacity = 1;
                _children.Visibility = Visibility.Visible;
                _children.MaxHeight = double.PositiveInfinity;
                _chevron.Angle = 90;
            }
        }

        // ---- external drag-drop (FileDrop from Explorer or another app) -------

        // One row holds the drop highlight at a time. WPF raises DragLeave AFTER the next row's
        // DragEnter (or skips it entirely when the pointer jumps rows), which used to leave every
        // row the drag passed over stuck highlighted until the mouse hovered it again. Tracking the
        // current row centrally clears the previous row on every transition.
        private static Border _dropHighlightRow;

        private static void HighlightDropRow(Border row)
        {
            if (_dropHighlightRow != null && !ReferenceEquals(_dropHighlightRow, row))
                _dropHighlightRow.Background = Brushes.Transparent;
            _dropHighlightRow = row;
            if (row != null) row.Background = UiHelpers.AppBrush("ItemActiveBackground");
        }

        /// <summary>Clear any lingering drag-over highlight (safe to call any time).</summary>
        internal static void ClearDropHighlight()
        {
            if (_dropHighlightRow != null) _dropHighlightRow.Background = Brushes.Transparent;
            _dropHighlightRow = null;
        }

        private void OnFileDragOver(object sender, DragEventArgs e)
        {
            // Internal pane drags (pins, groups, tabs) bubble up to the section's handlers.
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            var sources = e.Data.GetData(DataFormats.FileDrop) as string[];
            e.Effects = FileDropOps.ComputeEffect(e.AllowedEffects, e.KeyStates, sources, _path);
            if (e.Effects == DragDropEffects.None) ClearDropHighlight();
            else HighlightDropRow(_row);
            e.Handled = true;
        }

        private void OnFileDragLeave(object sender, DragEventArgs e)
        {
            if (ReferenceEquals(_dropHighlightRow, _row)) ClearDropHighlight();
        }

        private void OnFileDrop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            e.Handled = true;
            ClearDropHighlight();
            _row.Background = Brushes.Transparent;

            var sources = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (sources == null || sources.Length == 0) return;
            var effect = FileDropOps.ComputeEffect(e.AllowedEffects, e.KeyStates, sources, _path);

            // Files without a modifier keep Explorer's silent semantics (move on the same drive,
            // copy across drives). A dropped FOLDER without a modifier is ambiguous: it might mean
            // "move it into this folder" or "pin it to the sidebar", and guessing wrong moves a
            // whole folder, so a small menu asks. Modifiers always act silently and exactly like
            // Explorer: Ctrl copies, Shift moves, Alt (or Ctrl+Shift) creates shortcuts.
            bool modifier = (e.KeyStates & (DragDropKeyStates.ControlKey | DragDropKeyStates.ShiftKey | DragDropKeyStates.AltKey)) != 0;
            bool anyFolder = false;
            foreach (var s in sources)
            {
                try { if (Directory.Exists(s)) { anyFolder = true; break; } } catch { }
            }

            if (modifier || !anyFolder)
            {
                var eff = effect;
                var src = sources;
                var dest = _path;
                WorkQueue.Post(() => FileDropOps.Perform(src, dest, eff)); // shell op off the input-attached thread
                return;
            }

            ShowAmbiguousDropMenu(sources);
        }

        private void ShowAmbiguousDropMenu(string[] sources)
        {
            string leaf;
            try { leaf = Path.GetFileName(_path.TrimEnd('\\', '/')); } catch { leaf = _path; }
            if (string.IsNullOrEmpty(leaf)) leaf = _path;

            var dest = _path;
            var menu = new ContextMenu();

            var move = MakeItem("Move into \"" + leaf + "\"", () => WorkQueue.Post(() => FileDropOps.Perform(sources, dest, DragDropEffects.Move)));
            move.FontWeight = FontWeights.SemiBold; // what Explorer would have done silently
            menu.Items.Add(move);
            menu.Items.Add(MakeItem("Copy into \"" + leaf + "\"", () => WorkQueue.Post(() => FileDropOps.Perform(sources, dest, DragDropEffects.Copy))));
            menu.Items.Add(MakeItem("Create shortcut inside \"" + leaf + "\"", () => WorkQueue.Post(() => FileDropOps.Perform(sources, dest, DragDropEffects.Link))));
            if (_pinContext != null && _pinContext.PinHere != null)
                menu.Items.Add(MakeItem("Pin to \"" + _pinContext.GroupName + "\"", () => _pinContext.PinHere(sources)));
            menu.Items.Add(new Separator());
            menu.Items.Add(MakeItem("Cancel", () => { }));

            SidebarControl.SuppressAutoHide = true; // keep the dock open while the menu is up
            menu.Closed += (s, e) => SidebarControl.SuppressAutoHide = false;
            menu.PlacementTarget = _row;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
            menu.IsOpen = true;
        }

        // ---- expand / children -------------------------------------------------

        private void Toggle()
        {
            _expanded = !_expanded;
            UiState.SetExpanded("tree:" + _path, _expanded);
            if (_expanded && !_loaded) LoadChildren();
            if (_expanded) _chevGlyph.Opacity = 1; // keep the caret visible while open
            UiHelpers.ToggleExpand(_children, _chevron, _expanded);
        }

        // Enumerate on the worker and add the rows on the UI thread, so expanding a slow or dead
        // folder can never freeze input. The expand animation opens to an unbounded MaxHeight, so
        // rows arriving a beat later simply appear.
        private void LoadChildren()
        {
            _loaded = true;
            var path = _path;
            var nav = _navigate;
            var level = _level;
            WorkQueue.Post(() =>
            {
                string[] dirs;
                try { dirs = Directory.GetDirectories(path); }
                catch (Exception ex) { Log.Error("expand folder '" + path + "'", ex); return; }
                var ordered = dirs.OrderBy(d => Path.GetFileName(d), StringComparer.CurrentCultureIgnoreCase).ToList();
                WorkQueue.PostUI(() =>
                {
                    foreach (var d in ordered)
                        _children.Children.Add(new FolderTreeNode(Path.GetFileName(d), d, true, nav, level + 1));
                });
            });
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
