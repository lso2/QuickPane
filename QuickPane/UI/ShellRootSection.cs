using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using QuickPane.Services;
using NM = QuickPane.Interop.NativeMethods;

namespace QuickPane.UI
{
    /// <summary>
    /// A shell-root section such as Network or Linux. It renders a collapsible, renamable section
    /// header and one root node that navigates on click and, where the root is a real filesystem path
    /// (Linux at \\wsl$), expands to its children like a pinned folder.
    /// </summary>
    internal sealed class ShellRootSection : UserControl
    {
        private readonly string _type;
        private readonly string _defaultTitle;
        private readonly string _rootPath;
        private readonly bool _isLinux;
        private readonly StackPanel _root = new StackPanel();
        private bool _expanded = true;

        public ShellRootSection(string type, string defaultTitle, string rootPath, bool isLinux)
        {
            _type = type;
            _defaultTitle = defaultTitle;
            _rootPath = rootPath;
            _isLinux = isLinux;
            Focusable = false;
            Content = _root;
        }

        public void Build(Action<string> navigate)
        {
            _root.Children.Clear();
            var items = new StackPanel();

            RotateTransform chevron = null;
            TextBlock label;
            _expanded = UiState.GetExpanded("section:" + _type);
            var header = UiHelpers.BuildHeader(
                RecentsSection.SectionTitle(_type, _defaultTitle),
                () => { _expanded = !_expanded; UiState.SetExpanded("section:" + _type, _expanded); UiHelpers.ToggleExpand(items, chevron, _expanded); },
                () => RecentsSection.RenameSectionPrompt(_type, _defaultTitle),
                out chevron, out label);
            header.ContextMenu = RecentsSection.SectionMenu(_type, _defaultTitle);

            var icon = _isLinux
                ? IconHelper.GetIcon(_rootPath, true, false)
                : IconHelper.GetSpecialFolderIcon(NM.CSIDL_NETWORK);

            var node = new FolderTreeNode(RecentsSection.SectionTitle(_type, _defaultTitle),
                _rootPath, true, navigate, 0, false, icon);
            items.Children.Add(node);

            if (_expanded) chevron.Angle = 90;
            else { items.Visibility = Visibility.Collapsed; items.MaxHeight = 0; }

            _root.Children.Add(header);
            _root.Children.Add(items);
        }

        public static bool WslPresent()
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Lxss"))
                {
                    return key != null && key.SubKeyCount > 0;
                }
            }
            catch { return false; }
        }
    }
}
