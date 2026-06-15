using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QuickPane.Services;
using NM = QuickPane.Interop.NativeMethods;

namespace QuickPane.UI
{
    /// <summary>One folder row, shared by the Groups and Recent sections. Shows the real shell icon
    /// for the target so custom folder icons are respected, dims broken targets without deleting them.</summary>
    public partial class FolderItem : UserControl
    {
        public string DisplayName { get; private set; }
        public string TargetPath { get; private set; }
        public bool IsBroken { get; private set; }

        public event Action Clicked;

        public FolderItem()
        {
            InitializeComponent();
            MouseEnter += (s, e) => Root.Background = Brush("ItemHoverBackground");
            MouseLeave += (s, e) => Root.Background = Brushes.Transparent;
            MouseLeftButtonUp += OnLeftClick;
        }

        public void Bind(string display, string targetPath, bool exists, bool isFile = false)
        {
            DisplayName = display;
            TargetPath = targetPath;
            IsBroken = !exists;
            Label.Text = display;
            ToolTip = targetPath;

            Icon.Source = IconHelper.GetIcon(targetPath, exists, isFile);

            if (IsBroken)
            {
                Root.Opacity = 0.5;
                Label.Foreground = Brush("BrokenLinkColor");
                ToolTip = targetPath + "  (target not found)";
            }
        }

        private void OnLeftClick(object sender, MouseButtonEventArgs e)
        {
            var h = Clicked;
            if (h != null) h();
        }

        private Brush Brush(string key)
        {
            var b = TryFindResource(key) as Brush;
            return b ?? Brushes.Transparent;
        }
    }

    /// <summary>Pulls 16x16 shell icons for folder paths and converts them to WPF image sources.</summary>
    internal static class IconHelper
    {
        private static readonly Dictionary<string, ImageSource> Cache =
            new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);
        private static ImageSource _genericFolder;

        public static ImageSource GetIcon(string path, bool exists, bool isFile)
        {
            try
            {
                if (string.IsNullOrEmpty(path)) return GenericFolder();

                string key = (isFile ? "f:" : "d:") + path;
                ImageSource cached;
                if (Cache.TryGetValue(key, out cached)) return cached;

                ImageSource img;
                if (exists)
                {
                    img = Extract(path, useAttributes: false);
                }
                else if (isFile)
                {
                    // Generic icon by extension when the file is gone.
                    img = Extract(path, useAttributes: true, fileAttr: 0x80 /* FILE_ATTRIBUTE_NORMAL */);
                }
                else
                {
                    img = GenericFolder();
                }
                if (img == null) img = isFile ? null : GenericFolder();
                if (img != null) Cache[key] = img;
                return img;
            }
            catch (Exception ex)
            {
                Log.Error("GetIcon failed for '" + path + "'", ex);
                return isFile ? null : GenericFolder();
            }
        }

        public static ImageSource GetFolderIcon(string path, bool exists)
        {
            return GetIcon(path, exists, false);
        }

        // The real shell icon for a special folder (e.g. Network), via its PIDL.
        public static ImageSource GetSpecialFolderIcon(int csidl)
        {
            try
            {
                IntPtr pidl;
                if (NM.SHGetSpecialFolderLocation(IntPtr.Zero, csidl, out pidl) != 0 || pidl == IntPtr.Zero)
                    return null;
                try
                {
                    var info = new NM.SHFILEINFO();
                    var res = NM.SHGetFileInfoPidl(pidl, 0, ref info,
                        (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(NM.SHFILEINFO)),
                        NM.SHGFI_ICON | NM.SHGFI_SMALLICON | NM.SHGFI_PIDL);
                    if (res == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;
                    try
                    {
                        var src = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                        src.Freeze();
                        return src;
                    }
                    finally { NM.DestroyIcon(info.hIcon); }
                }
                finally { NM.ILFreePidl(pidl); }
            }
            catch (Exception ex) { Log.Error("special folder icon", ex); return null; }
        }

        private static ImageSource GenericFolder()
        {
            if (_genericFolder == null)
                _genericFolder = Extract("folder", useAttributes: true);
            return _genericFolder;
        }

        private static ImageSource Extract(string path, bool useAttributes, uint fileAttr = NM.FILE_ATTRIBUTE_DIRECTORY)
        {
            var info = new NM.SHFILEINFO();
            uint flags = NM.SHGFI_ICON | NM.SHGFI_SMALLICON;
            uint attr = 0;
            if (useAttributes)
            {
                flags |= NM.SHGFI_USEFILEATTRIBUTES;
                attr = fileAttr;
            }

            IntPtr res = NM.SHGetFileInfo(path, attr, ref info,
                (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(NM.SHFILEINFO)), flags);
            if (res == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;

            try
            {
                var src = Imaging.CreateBitmapSourceFromHIcon(
                    info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                return src;
            }
            finally
            {
                NM.DestroyIcon(info.hIcon);
            }
        }
    }
}
