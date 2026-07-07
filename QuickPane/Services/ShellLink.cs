using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace QuickPane.Services
{
    /// <summary>
    /// Thin wrapper over IShellLinkW / IPersistFile to create and resolve .lnk files. Pins are
    /// stored as real shortcuts so the groups folder stays portable and Explorer-native.
    /// </summary>
    internal static class ShellLink
    {
        public static void Create(string linkPath, string targetPath, string description = null)
        {
            IShellLinkW link = (IShellLinkW)new CShellLink();
            try
            {
                link.SetPath(targetPath);
                if (System.IO.Directory.Exists(targetPath))
                    link.SetWorkingDirectory(targetPath);
                if (!string.IsNullOrEmpty(description))
                    link.SetDescription(description);

                IPersistFile file = (IPersistFile)link;
                file.Save(linkPath, false);
            }
            finally
            {
                Marshal.ReleaseComObject(link);
            }
        }

        /// <summary>Resolve the target of a .lnk. Returns null if it cannot be read.</summary>
        public static string ResolveTarget(string linkPath)
        {
            IShellLinkW link = (IShellLinkW)new CShellLink();
            try
            {
                IPersistFile file = (IPersistFile)link;
                file.Load(linkPath, 0);
                // No SLR_UPDATE so a missing target does not block resolution or pop UI.
                var sb = new StringBuilder(1024); // room for long paths beyond MAX_PATH
                var data = new WIN32_FIND_DATAW();
                link.GetPath(sb, sb.Capacity, ref data, SLGP_RAWPATH);
                if (sb.Length > 0) return sb.ToString();

                // Some shortcuts (many in the Recent folder) carry only an item-ID list, so GetPath is
                // empty. Resolve the path from the PIDL instead.
                IntPtr pidl;
                link.GetIDList(out pidl);
                if (pidl != IntPtr.Zero)
                {
                    try
                    {
                        var sb2 = new StringBuilder(1024);
                        if (SHGetPathFromIDListW(pidl, sb2) && sb2.Length > 0) return sb2.ToString();
                    }
                    finally { ILFree(pidl); }
                }
                return string.Empty;
            }
            catch (Exception ex)
            {
                Log.Error("ResolveTarget failed for '" + linkPath + "'", ex);
                return null;
            }
            finally
            {
                Marshal.ReleaseComObject(link);
            }
        }

        private const uint SLGP_RAWPATH = 0x4;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern bool SHGetPathFromIDListW(IntPtr pidl, [Out] StringBuilder pszPath);

        [DllImport("shell32.dll")]
        private static extern void ILFree(IntPtr pidl);

        [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
        private class CShellLink { }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown),
         Guid("000214F9-0000-0000-C000-000000000046")]
        private interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cchMaxPath,
                ref WIN32_FIND_DATAW pfd, uint fFlags);
            void GetIDList(out IntPtr ppidl);
            void SetIDList(IntPtr pidl);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cchMaxName);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cchMaxPath);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cchMaxPath);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
            void GetHotkey(out short pwHotkey);
            void SetHotkey(short wHotkey);
            void GetShowCmd(out int piShowCmd);
            void SetShowCmd(int iShowCmd);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cchIconPath, out int piIcon);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
            void Resolve(IntPtr hwnd, uint fFlags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WIN32_FIND_DATAW
        {
            public uint dwFileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME ftCreationTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME ftLastAccessTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME ftLastWriteTime;
            public uint nFileSizeHigh;
            public uint nFileSizeLow;
            public uint dwReserved0;
            public uint dwReserved1;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string cFileName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string cAlternateFileName;
        }
    }
}
