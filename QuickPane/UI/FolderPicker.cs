// FolderPicker.cs
// Purpose: the Explorer-style folder picker (IFileOpenDialog with FOS_PICKFOLDERS) for "Add folder..." and the groups folder button.
// Reason: WinForms FolderBrowserDialog on .NET Framework 4.8 opens the legacy Browse For Folder tree.
// Defines: FolderPicker.
// Origin: ported from jprisant's fork, commits da80dfe and 7050d14 (MIT, see NOTICE.md).
// Doc: docs/notes/QuickPane/UI/FolderPicker.cs.md
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using QuickPane.Services;
using NM = QuickPane.Interop.NativeMethods;

namespace QuickPane.UI
{
    /// <summary>
    /// The folder picker with an address bar, search and Quick Access, from the same dialog family as
    /// "Add file...". Its navigation pane lists the pane's pins (PickerPlaces).
    /// </summary>
    internal static class FolderPicker
    {
        private const uint FOS_PICKFOLDERS = 0x00000020;
        private const uint FOS_FORCEFILESYSTEM = 0x00000040;
        private const int FDAP_BOTTOM = 0;
        private const int ERROR_CANCELLED = unchecked((int)0x800704C7);
        private const uint SIGDN_FILESYSPATH = 0x80058000;

        /// <summary>Show the picker and return the chosen folder, or null when it is canceled or fails.
        /// A start folder that is empty, missing or slow to answer is left unset.</summary>
        public static string Pick(string title, string startFolder = null)
        {
            IFileOpenDialog dialog = null;
            try
            {
                dialog = (IFileOpenDialog)new FileOpenDialogCoClass();
                uint opts;
                dialog.GetOptions(out opts);
                dialog.SetOptions(opts | FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM);
                if (!string.IsNullOrEmpty(title)) dialog.SetTitle(title);
                SetStartFolder(dialog, startFolder);
                AddPins(dialog);

                // The calling thread's active window owns the dialog, the same owner FolderBrowserDialog used.
                int hr = dialog.Show(NM.GetActiveWindow());
                if (hr == ERROR_CANCELLED) return null;
                if (hr != 0) throw Marshal.GetExceptionForHR(hr);

                IShellItem result;
                dialog.GetResult(out result);
                try
                {
                    IntPtr name;
                    result.GetDisplayName(SIGDN_FILESYSPATH, out name);
                    try { return Marshal.PtrToStringUni(name); }
                    finally { Marshal.FreeCoTaskMem(name); }
                }
                finally { Marshal.ReleaseComObject(result); }
            }
            catch (Exception ex)
            {
                Log.Error("folder picker", ex);
                return null;
            }
            finally { if (dialog != null) Marshal.ReleaseComObject(dialog); }
        }

        // The existence check gets 1 second. A start folder on a share that does not answer would
        // otherwise hold the click for the whole network timeout.
        private static void SetStartFolder(IFileOpenDialog dialog, string startFolder)
        {
            if (string.IsNullOrEmpty(startFolder)) return;
            var path = Environment.ExpandEnvironmentVariables(startFolder);
            var check = Task.Run(() => { try { return Directory.Exists(path); } catch { return false; } });
            if (!check.Wait(1000) || !check.Result) return;

            var iid = typeof(IShellItem).GUID;
            IShellItem folder;
            if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out folder) != 0 || folder == null) return;
            try { dialog.SetFolder(folder); }
            finally { Marshal.ReleaseComObject(folder); }
        }

        // A pin that fails to resolve is logged and skipped, and the picker opens with the rest.
        private static void AddPins(IFileOpenDialog dialog)
        {
            var iid = typeof(IShellItem).GUID;
            foreach (var path in PickerPlaces.ForPicker())
            {
                IShellItem item = null;
                try
                {
                    if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out item) == 0 && item != null)
                        dialog.AddPlace(item, FDAP_BOTTOM);
                }
                catch (Exception ex) { Log.Error("add pin '" + path + "' to the folder picker", ex); }
                finally { if (item != null) Marshal.ReleaseComObject(item); }
            }
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        private static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid riid, out IShellItem item);

        [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
        private class FileOpenDialogCoClass { }

        // Members are declared in vtable order: IModalWindow, IFileDialog, then IFileOpenDialog's own 2.
        // C# interface inheritance does not reproduce a COM vtable, and a missing or reordered base
        // member puts every later call in the wrong slot.
        [ComImport, Guid("d57c7288-d4ad-4768-be02-9d969532d960"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileOpenDialog
        {
            // IModalWindow
            [PreserveSig] int Show(IntPtr parent);

            // IFileDialog
            void SetFileTypes(uint cFileTypes, IntPtr rgFilterSpec);
            void SetFileTypeIndex(uint iFileType);
            void GetFileTypeIndex(out uint piFileType);
            void Advise(IntPtr pfde, out uint pdwCookie);
            void Unadvise(uint dwCookie);
            void SetOptions(uint fos);
            void GetOptions(out uint fos);
            void SetDefaultFolder(IShellItem psi);
            void SetFolder(IShellItem psi);
            void GetFolder(out IShellItem ppsi);
            void GetCurrentSelection(out IShellItem ppsi);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
            void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
            void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
            void GetResult(out IShellItem ppsi);
            void AddPlace(IShellItem psi, int alignment);
            void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
            void Close(int hr);
            void SetClientGuid(ref Guid guid);
            void ClearClientData();
            void SetFilter(IntPtr pFilter);

            // IFileOpenDialog
            void GetResults(out IntPtr ppenum);
            void GetSelectedItems(out IntPtr ppsai);
        }

        [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
            void GetParent(out IShellItem ppsi);
            // The returned name is shell-allocated (CoTaskMemAlloc) and is freed with Marshal.FreeCoTaskMem.
            void GetDisplayName(uint sigdnName, out IntPtr ppszName);
            void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
            void Compare(IShellItem psi, uint hint, out int piOrder);
        }
    }
}
