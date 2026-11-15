// SidebarControl.Keyboard.cs
// Purpose: arrow-key, Enter and Escape navigation between the pane's rows and its search box.
// Reason: the keyboard shortcut puts focus in the pane, and the README promises arrow keys and Enter from there.
// Defines: SidebarControl (partial): WireKeyboard, FocusPane, OnPaneKeyDown, VisibleRows.
// Doc: docs/notes/QuickPane/UI/SidebarControl.Keyboard.cs.md
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace QuickPane.UI
{
    public partial class SidebarControl
    {
        private void WireKeyboard()
        {
            PreviewKeyDown += OnPaneKeyDown;
        }

        /// <summary>Move keyboard focus into this pane for the keyboard navigation shortcut. Focus is
        /// placed in the search box when the pane has one, otherwise on the first row.</summary>
        public bool FocusPane()
        {
            if (FocusSearch()) return true;
            var rows = VisibleRows();
            if (rows.Count == 0) return false;
            FocusHostWindow();
            FocusRow(rows[0]);
            return true;
        }

        // Pressing Up or Down moves focus between the rows on screen, in drawing order. Pressing Down in
        // the search box focuses the first row, and pressing Enter there opens that row. Pressing Up on
        // the first row or Escape on any row focuses the search box. Enter on a row is handled by the row
        // itself, and other text boxes in the pane get every key unchanged.
        private void OnPaneKeyDown(object sender, KeyEventArgs e)
        {
            var key = e.Key;
            if (key != Key.Up && key != Key.Down && key != Key.Enter && key != Key.Escape) return;
            var focused = Keyboard.FocusedElement as DependencyObject;
            bool inSearch = IsSearchBox(focused);
            var focusedRow = inSearch ? null : focused as UIElement;
            if (!inSearch && (focusedRow == null || !IsRow(focusedRow))) return;

            var rows = VisibleRows();
            if (inSearch)
            {
                if (rows.Count == 0) return;
                if (key == Key.Down) { FocusRow(rows[0]); e.Handled = true; }
                else if (key == Key.Enter) { ActivateRow(rows[0]); e.Handled = true; }
                return;
            }

            int i = rows.IndexOf(focusedRow);
            if (key == Key.Down)
            {
                if (i >= 0 && i + 1 < rows.Count) FocusRow(rows[i + 1]);
                e.Handled = true;
            }
            else if (key == Key.Up)
            {
                if (i > 0) FocusRow(rows[i - 1]);
                else FocusSearch();
                e.Handled = true;
            }
            else if (key == Key.Escape)
            {
                FocusSearch();
                e.Handled = true;
            }
        }

        private bool IsSearchBox(DependencyObject d)
        {
            if (d == null || SectionsPanel == null) return false;
            foreach (var child in SectionsPanel.Children)
            {
                var box = child as SearchSection;
                if (box != null && box.Owns(d)) return true;
            }
            return false;
        }

        // A row is a FolderItem, or the row border of a FolderTreeNode.
        private static bool IsRow(DependencyObject d)
        {
            if (d is FolderItem) return true;
            var border = d as Border;
            if (border == null) return false;
            var node = VisualTreeHelper.GetParent(border) as FolderTreeNode;
            return node != null && ReferenceEquals(node.Row, border);
        }

        /// <summary>The rows a person can see, top to bottom.</summary>
        private List<UIElement> VisibleRows()
        {
            var rows = new List<UIElement>();
            if (SectionsPanel != null) CollectRows(SectionsPanel, rows);
            return rows;
        }

        // A collapsed subtree has Visibility.Visible and MaxHeight 0 (UiHelpers.ToggleExpand), and a
        // filtered row is Collapsed. Both are skipped with everything under them.
        private static void CollectRows(DependencyObject parent, List<UIElement> rows)
        {
            int n = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < n; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                var fe = child as FrameworkElement;
                if (fe != null && (fe.Visibility != Visibility.Visible || fe.MaxHeight <= 0)) continue;
                var el = child as UIElement;
                if (el != null && IsRow(el))
                {
                    if (el.IsVisible && el.Focusable) rows.Add(el);
                    continue;
                }
                CollectRows(child, rows);
            }
        }

        private static void FocusRow(UIElement row)
        {
            row.Focus();
            var fe = row as FrameworkElement;
            if (fe != null) fe.BringIntoView();
        }

        private static void ActivateRow(UIElement row)
        {
            var item = row as FolderItem;
            if (item != null) { item.Activate(); return; }
            var node = VisualTreeHelper.GetParent(row) as FolderTreeNode;
            if (node != null) node.Activate();
        }
    }
}
