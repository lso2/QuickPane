// ExplorerNavigator.cs
// Purpose: drives a specific Explorer window from outside its process through Shell.Application automation.
// Reason: the pane navigates its own Explorer window and opens new ones without code inside explorer.exe.
// Defines: ExplorerNavigator.
// Doc: docs/notes/QuickPane/Interop/ExplorerNavigator.cs.md
using System;
using System.Reflection;
using System.Runtime.InteropServices;
using QuickPane.Services;

namespace QuickPane.Interop
{
    /// <summary>
    /// Drives a specific Explorer window from outside its process. It uses the Shell.Application
    /// automation object to navigate the target Explorer window in place. On Windows 11, Shell
    /// windows for different tabs can share a top-level HWND, so navigation also matches the active
    /// tab's child HWND when the window has multiple tabs.
    ///
    /// Automation calls are late bound through Type.InvokeMember rather than the C# dynamic keyword.
    /// The dynamic path routes COM calls through the DLR, which caches a wrapper against each runtime
    /// callable wrapper using Marshal.SetComObjectData, and that cache is invalidated by the explicit
    /// Marshal.ReleaseComObject calls this class needs to make. The two together threw
    /// "InvalidOperationException: Marshal.SetComObjectData failed" out of ComObject.ObjectToComObject,
    /// which is what silently dropped folder clicks and Recent updates. InvokeMember reaches IDispatch
    /// directly, so no such cache exists and releasing an object cannot poison a later call.
    /// </summary>
    internal static class ExplorerNavigator
    {
        private const string TabWindowClass = "ShellTabWindowClass";
        private static readonly Guid TopLevelBrowserService = new Guid("4C96BE40-915C-11CF-99D3-00AA004AE837");

        [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellServiceProvider
        {
            [PreserveSig]
            int QueryService(ref Guid service, ref Guid iid, out IShellBrowser browser);
        }

        [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellBrowser
        {
            // GetWindow is the first method inherited from IOleWindow.
            [PreserveSig]
            int GetWindow(out IntPtr hwnd);
        }

        /// <summary>Navigate on the background worker. Enumerating shell windows is cross-process COM
        /// that blocks for as long as Explorer is busy, and the UI thread shares Explorer's input queue
        /// in Inside mode, so this must never run there.</summary>
        public static void NavigateAsync(IntPtr topLevelHwnd, string path)
        {
            WorkQueue.Post(() => Navigate(topLevelHwnd, path));
        }

        // 0x800700AA is ERROR_BUSY surfaced through COM: the window is mid-operation rather than wrong
        // or gone, so the call is worth making again once the shell has had a moment.
        private const int HResultResourceInUse = unchecked((int)0x800700AA);

        /// <summary>Drive Navigate2, retrying once when the shell reports the window busy. Giving up on
        /// a busy window left the click doing nothing at all.</summary>
        private static bool Navigate2WithRetry(object w, string path, IntPtr topLevelHwnd, IntPtr activeTab)
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    // Explorer can switch or close the tab while COM is busy or being queried.
                    if (activeTab != IntPtr.Zero && FirstTabOf(topLevelHwnd) != activeTab) return false;
                    Call(w, "Navigate2", path);
                    return true;
                }
                catch (Exception ex) when (attempt == 0 && IsBusy(ex))
                {
                    System.Threading.Thread.Sleep(250);
                }
            }
        }

        private static bool IsBusy(Exception ex)
        {
            for (var e = ex; e != null; e = e.InnerException)
                if (System.Runtime.InteropServices.Marshal.GetHRForException(e) == HResultResourceInUse)
                    return true;
            return false;
        }

        /// <summary>Navigate the Explorer window identified by topLevelHwnd to path, in place.</summary>
        public static bool Navigate(IntPtr topLevelHwnd, string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            Log.Activity("explorer navigate to " + path);

            object shell = null, windows = null;
            try
            {
                var shellType = Type.GetTypeFromProgID("Shell.Application");
                if (shellType == null)
                {
                    Log.Event("navigate", "Shell.Application is not registered, so in-place navigation is unavailable.");
                    return false;
                }
                shell = Activator.CreateInstance(shellType);
                windows = Call(shell, "Windows");
                if (windows == null) return false;

                var firstTab = FirstTabOf(topLevelHwnd);
                // Explorer places the active tab first in child Z order. With a single tab or a
                // legacy window, the original top-level HWND match is sufficient.
                var activeTab = firstTab != IntPtr.Zero &&
                    NativeMethods.FindWindowEx(topLevelHwnd, firstTab, TabWindowClass, null) != IntPtr.Zero
                    ? firstTab : IntPtr.Zero;
                long target = topLevelHwnd.ToInt64();
                int count = ToInt(Get(windows, "Count"));
                for (int i = 0; i < count; i++)
                {
                    object w = null;
                    try
                    {
                        w = Call(windows, "Item", i);
                        if (w == null) continue;
                        if (HwndOf(w) != target) continue;
                        if (activeTab != IntPtr.Zero && BrowserWindowOf(w) != activeTab) continue;

                        return Navigate2WithRetry(w, path, topLevelHwnd, activeTab);
                    }
                    catch (Exception ex)
                    {
                        Log.Event("navigate", "Navigate2 failed for '" + path + "'", ex);
                    }
                    finally { Release(w); }
                }

                Log.Event("navigate", "no navigation target matched Explorer hwnd " + topLevelHwnd.ToString("X") +
                    ", so '" + path + "' was not opened in place.");
                return false;
            }
            catch (Exception ex)
            {
                Log.Event("navigate", "in-place navigation failed for '" + path + "'", ex);
                return false;
            }
            finally
            {
                Release(windows);
                Release(shell);
            }
        }

        private static IntPtr FirstTabOf(IntPtr topLevelHwnd)
        {
            return NativeMethods.FindWindowEx(topLevelHwnd, IntPtr.Zero, TabWindowClass, null);
        }

        private static IntPtr BrowserWindowOf(object shellWindow)
        {
            var services = shellWindow as IShellServiceProvider;
            if (services == null) return IntPtr.Zero;

            var service = TopLevelBrowserService;
            var iid = typeof(IShellBrowser).GUID;
            IShellBrowser browser = null;
            try
            {
                if (services.QueryService(ref service, ref iid, out browser) != 0 || browser == null)
                    return IntPtr.Zero;

                IntPtr hwnd;
                return browser.GetWindow(out hwnd) == 0 ? hwnd : IntPtr.Zero;
            }
            finally { Release(browser); }
        }

        /// <summary>The folder currently shown in the Explorer window with this top-level HWND, or null.</summary>
        public static string GetCurrentPath(IntPtr topLevelHwnd)
        {
            object shell = null, windows = null;
            try
            {
                var shellType = Type.GetTypeFromProgID("Shell.Application");
                if (shellType == null) return null;
                shell = Activator.CreateInstance(shellType);
                windows = Call(shell, "Windows");
                if (windows == null) return null;

                long target = topLevelHwnd.ToInt64();
                int count = ToInt(Get(windows, "Count"));
                for (int i = 0; i < count; i++)
                {
                    object w = null;
                    try
                    {
                        w = Call(windows, "Item", i);
                        if (w == null) continue;
                        if (HwndOf(w) != target) continue;
                        return PathOf(w);
                    }
                    catch { }
                    finally { Release(w); }
                }
                return null;
            }
            catch (Exception ex) { Log.Error("GetCurrentPath failed", ex); return null; }
            finally
            {
                Release(windows);
                Release(shell);
            }
        }

        /// <summary>Read the folder path from a shell window, preferring the folder object and falling
        /// back to the location URL for shells that return nothing from the first route.</summary>
        private static string PathOf(object w)
        {
            object doc = null, folder = null, self = null;
            try
            {
                doc = Get(w, "Document");
                if (doc != null)
                {
                    folder = Get(doc, "Folder");
                    if (folder != null)
                    {
                        self = Get(folder, "Self");
                        if (self != null)
                        {
                            var p = Get(self, "Path") as string;
                            if (!string.IsNullOrEmpty(p)) return p;
                        }
                    }
                }
            }
            catch { }
            finally { Release(self); Release(folder); Release(doc); }

            try
            {
                var url = Get(w, "LocationURL") as string;   // e.g. file:///C:/Users/...
                if (!string.IsNullOrEmpty(url))
                {
                    var u = new Uri(url);
                    if (u.IsFile) return u.LocalPath;
                }
            }
            catch { }
            return null;
        }

        /// <summary>Open a folder in a brand new Explorer window (used by "Open in new window").</summary>
        public static void OpenNewWindow(string path)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = "\"" + path + "\"",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Log.Event("navigate", "opening '" + path + "' in a new window failed", ex);
            }
        }

        // ---- late-bound COM helpers -----------------------------------------

        private const BindingFlags CallFlags = BindingFlags.InvokeMethod;
        private const BindingFlags GetFlags = BindingFlags.GetProperty;

        /// <summary>Late-bound method call through IDispatch. Shared with ShellFolders.</summary>
        internal static object Call(object target, string member, params object[] args)
        {
            if (target == null) return null;
            return target.GetType().InvokeMember(member, CallFlags, null, target, args);
        }

        /// <summary>Late-bound property read through IDispatch. Shared with ShellFolders.</summary>
        internal static object Get(object target, string member)
        {
            if (target == null) return null;
            return target.GetType().InvokeMember(member, GetFlags, null, target, null);
        }

        /// <summary>The window handle of a shell window. Items that are not Explorer windows, such as an
        /// Internet Explorer frame, throw here rather than returning a handle.</summary>
        private static long HwndOf(object w)
        {
            try { return Convert.ToInt64(Get(w, "HWND")); }
            catch { return 0; }
        }

        private static int ToInt(object v)
        {
            try { return v == null ? 0 : Convert.ToInt32(v); }
            catch { return 0; }
        }

        private static void Release(object o)
        {
            try { if (o != null && Marshal.IsComObject(o)) Marshal.ReleaseComObject(o); }
            catch { }
        }
    }
}
