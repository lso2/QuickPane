using System;
using System.Runtime.InteropServices;
using QuickPane.Services;

namespace QuickPane.Interop
{
    /// <summary>
    /// Best-effort pinning of a window to every virtual desktop. This relies on undocumented shell
    /// COM whose interface IDs differ between Windows builds, so everything is wrapped and any failure
    /// is a silent no-op: the window simply stays on the desktop it was created on.
    /// </summary>
    internal static class VirtualDesktop
    {
        private static IVirtualDesktopManager _mgr;
        private static bool _mgrTried;

        /// <summary>True if the window is on the desktop the user is currently viewing. Uses the
        /// documented IVirtualDesktopManager. On any failure it returns true so panes are never hidden
        /// by a desktop check that could not run.</summary>
        public static bool IsOnCurrentDesktop(IntPtr hwnd)
        {
            try
            {
                if (!_mgrTried)
                {
                    _mgrTried = true;
                    var t = Type.GetTypeFromCLSID(new Guid("AA509086-5CA9-4C25-8F95-589D3C07B48A"));
                    if (t != null) _mgr = Activator.CreateInstance(t) as IVirtualDesktopManager;
                }
                if (_mgr == null) return true;
                int on;
                _mgr.IsWindowOnCurrentVirtualDesktop(hwnd, out on);
                return on != 0;
            }
            catch { return true; }
        }

        [ComImport, Guid("A5CD92FF-29BE-454C-8D04-D82879FB3F1B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IVirtualDesktopManager
        {
            void IsWindowOnCurrentVirtualDesktop(IntPtr topLevelWindow, out int onCurrentDesktop);
            void GetWindowDesktopId(IntPtr topLevelWindow, out Guid desktopId);
            void MoveWindowToDesktop(IntPtr topLevelWindow, ref Guid desktopId);
        }

        public static bool PinWindow(IntPtr hwnd)
        {
            try
            {
                var shellType = Type.GetTypeFromCLSID(new Guid("C2F03A33-21F5-47FA-B4BB-156362A2F239")); // ImmersiveShell
                if (shellType == null) return false;
                var shell = (IServiceProvider10)Activator.CreateInstance(shellType);

                var viewCollGuid = typeof(IApplicationViewCollection).GUID;
                object viewCollObj;
                shell.QueryService(ref viewCollGuid, ref viewCollGuid, out viewCollObj);
                var viewColl = (IApplicationViewCollection)viewCollObj;

                IApplicationView view;
                viewColl.GetViewForHwnd(hwnd, out view);
                if (view == null) return false;

                var pinnedGuid = typeof(IVirtualDesktopPinnedApps).GUID;
                object pinnedObj;
                shell.QueryService(ref pinnedGuid, ref pinnedGuid, out pinnedObj);
                var pinned = (IVirtualDesktopPinnedApps)pinnedObj;

                pinned.PinView(view);
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("PinWindow (all desktops) unsupported on this build", ex);
                return false;
            }
        }

        [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IServiceProvider10
        {
            void QueryService(ref Guid service, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppvObject);
        }

        [ComImport, Guid("372E1D3B-38D3-42E4-A15B-8AB2B178F513"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IApplicationView
        {
            // Members are not needed; we only pass the pointer through.
        }

        [ComImport, Guid("1841C6D7-4F9D-42C0-AF41-8747538F10E5"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IApplicationViewCollection
        {
            void GetViews(out object array);
            void GetViewsByZOrder(out object array);
            void GetViewsByAppUserModelId(string id, out object array);
            void GetViewForHwnd(IntPtr hwnd, out IApplicationView view);
        }

        [ComImport, Guid("4CE81583-1E4C-4632-A621-07A53543148F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IVirtualDesktopPinnedApps
        {
            bool IsAppIdPinned(string appId);
            void PinAppID(string appId);
            void UnpinAppID(string appId);
            bool IsViewPinned(IApplicationView view);
            void PinView(IApplicationView view);
            void UnpinView(IApplicationView view);
        }
    }
}
