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

            // Reachable by keyboard: arrows move between rows and Enter opens the one in focus. Rows are
            // only in the tab order when keyboard navigation is switched on, so nothing changes for
            // anyone who leaves it off.
            Focusable = true;
            KeyDown += (s, e) =>
            {
                if (e.Key != System.Windows.Input.Key.Enter && e.Key != System.Windows.Input.Key.Space) return;
                var h = Clicked;
                if (h != null) { h(); e.Handled = true; }
            };
            GotKeyboardFocus += (s, e) => Root.Background = UiHelpers.AppBrush("ItemHoverBackground");
            LostKeyboardFocus += (s, e) => Root.Background = System.Windows.Media.Brushes.Transparent;
        }

        /// <summary>The text this row shows, which is what the pane's search box filters on.</summary>
        public string LabelText { get; private set; }

        public void Bind(string display, string targetPath, bool exists, bool isFile = false)
        {
            LabelText = display;
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

    /// <summary>
    /// Pulls 16x16 shell icons and converts them to frozen WPF image sources. Real shell icon
    /// extraction touches the target's volume, so it happens synchronously only for paths on local
    /// fixed drives; anything else (UNC shares, removable media, unprobed targets) gets an instant
    /// extension/generic icon and the real one is fetched on the background worker, which triggers a
    /// coalesced re-render when it lands. The cache is shared by every sidebar and is thread-safe
    /// because the worker fills it too.
    /// </summary>
    internal static class IconHelper
    {
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, ImageSource> Cache =
            new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, bool> FastRoots =
            new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> Pending = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private const int MaxCache = 600;
        private static ImageSource _genericFolder;

        /// <summary>
        /// The icon Windows shows for a program. Falls back to the file's own icon when the Start menu
        /// has nothing to say about it, which is every program whose first icon is the right one.
        /// </summary>
        public static ImageSource GetAppIcon(string exePath)
        {
            try
            {
                var pick = AppShortcuts.IconFor(exePath);
                if (pick != null)
                {
                    string key = "a:" + pick.File + "," + pick.Index;
                    lock (Gate)
                    {
                        ImageSource cached;
                        if (Cache.TryGetValue(key, out cached)) return cached;
                    }
                    var img = ExtractAt(pick.File, pick.Index);
                    if (img != null) { Put(key, img); return img; }
                }
            }
            catch (Exception ex) { Log.Error("app icon for '" + exePath + "'", ex); }

            return GetIcon(exePath, true, true);
        }

        /// <summary>One numbered icon out of a file.</summary>
        private static ImageSource ExtractAt(string file, int index)
        {
            var large = new IntPtr[1];
            var small = new IntPtr[1];
            int got = NM.ExtractIconEx(file, index, large, small, 1);
            if (got <= 0) return null;
            try
            {
                IntPtr h = small[0] != IntPtr.Zero ? small[0] : large[0];
                if (h == IntPtr.Zero) return null;
                var img = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                    h, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                img.Freeze();
                return img;
            }
            catch (Exception ex) { Log.Error("read icon " + index + " of '" + file + "'", ex); return null; }
            finally
            {
                if (large[0] != IntPtr.Zero) NM.DestroyIcon(large[0]);
                if (small[0] != IntPtr.Zero) NM.DestroyIcon(small[0]);
            }
        }

        public static ImageSource GetIcon(string path, bool exists, bool isFile)
        {
            try
            {
                if (string.IsNullOrEmpty(path)) return GenericFolder();

                string key = (isFile ? "f:" : "d:") + path;
                lock (Gate)
                {
                    ImageSource cached;
                    if (Cache.TryGetValue(key, out cached)) return cached;
                }

                if (!exists)
                {
                    // Broken target: icon by extension for files, plain folder otherwise. No disk hit.
                    return isFile ? ExtensionIcon(path) : GenericFolder();
                }

                if (IsFastPath(path))
                {
                    var img = Extract(path, useAttributes: false);
                    if (img == null) img = isFile ? ExtensionIcon(path) : GenericFolder();
                    if (img != null) Put(key, img);
                    return img;
                }

                // Slow volume: render an instant placeholder and swap in the real icon later.
                QueueSlowExtract(key, path);
                return isFile ? ExtensionIcon(path) : GenericFolder();
            }
            catch (Exception ex)
            {
                Log.Error("GetIcon failed for '" + path + "'", ex);
                return isFile ? null : GenericFolder();
            }
        }

        private static void Put(string key, ImageSource img)
        {
            lock (Gate)
            {
                if (Cache.Count > MaxCache) Cache.Clear(); // crude but keeps growth bounded
                Cache[key] = img;
            }
        }

        private static void QueueSlowExtract(string key, string path)
        {
            lock (Gate) { if (!Pending.Add(key)) return; }
            WorkQueue.Post(() =>
            {
                ImageSource img = null;
                try
                {
                    // Only touch the volume once the probe confirmed it answers; if the probe has
                    // not finished yet, the next re-render after it fires queues this again.
                    if (PathStatus.ConfirmedAlive(path)) img = Extract(path, useAttributes: false);
                }
                catch (Exception ex) { Log.Error("slow icon extract '" + path + "'", ex); }
                lock (Gate) { Pending.Remove(key); }
                if (img != null)
                {
                    Put(key, img);
                    PathStatus.NotifyVisualRefresh();
                }
            });
        }

        // Fixed local drives answer icon queries in microseconds; everything else can block.
        private static bool IsFastPath(string path)
        {
            try
            {
                if (path.StartsWith("\\\\", StringComparison.Ordinal)) return false;
                var root = System.IO.Path.GetPathRoot(path);
                if (string.IsNullOrEmpty(root)) return false;
                lock (Gate)
                {
                    bool fast;
                    if (FastRoots.TryGetValue(root, out fast)) return fast;
                }
                bool result = new System.IO.DriveInfo(root).DriveType == System.IO.DriveType.Fixed;
                lock (Gate) { FastRoots[root] = result; }
                return result;
            }
            catch { return false; }
        }

        private static ImageSource ExtensionIcon(string path)
        {
            return Extract(path, useAttributes: true, fileAttr: 0x80 /* FILE_ATTRIBUTE_NORMAL */);
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
            lock (Gate)
            {
                if (_genericFolder == null)
                    _genericFolder = Extract("folder", useAttributes: true);
                return _genericFolder;
            }
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
