using System;
using System.Reflection;
using System.Runtime.InteropServices;
using QuickPane.Services;

namespace QuickPane.Interop
{
    /// <summary>
    /// Drives a specific Explorer window from outside its process. It uses the Shell.Application
    /// automation object to find the ShellBrowserWindow whose top-level HWND matches the window the
    /// pane is embedded in, then navigates it in place, which is equivalent to the user clicking a
    /// folder in the native nav pane.
    ///
    /// Every call here is late bound through Type.InvokeMember rather than the C# dynamic keyword.
    /// The dynamic path routes COM calls through the DLR, which caches a wrapper against each runtime
    /// callable wrapper using Marshal.SetComObjectData, and that cache is invalidated by the explicit
    /// Marshal.ReleaseComObject calls this class needs to make. The two together threw
    /// "InvalidOperationException: Marshal.SetComObjectData failed" out of ComObject.ObjectToComObject,
    /// which is what silently dropped folder clicks and Recent updates. InvokeMember reaches IDispatch
    /// directly, so no such cache exists and releasing an object cannot poison a later call.
    /// </summary>
    internal static class ExplorerNavigator
    {
        /// <summary>Navigate on the background worker. Enumerating shell windows is cross-process COM
        /// that blocks for as long as Explorer is busy, and the UI thread shares Explorer's input queue
        /// in Inside mode, so this must never run there.</summary>
        public static void NavigateAsync(IntPtr topLevelHwnd, string path)
        {
            WorkQueue.Post(() => Navigate(topLevelHwnd, path));
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

                        Call(w, "Navigate2", path);
                        return true;
                    }
                    catch (Exception ex)
                    {
                        Log.Event("navigate", "Navigate2 failed for '" + path + "'", ex);
                    }
                    finally { Release(w); }
                }

                Log.Event("navigate", "no Explorer window matched hwnd " + topLevelHwnd.ToString("X") +
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

        private static object Call(object target, string member, params object[] args)
        {
            if (target == null) return null;
            return target.GetType().InvokeMember(member, CallFlags, null, target, args);
        }

        private static object Get(object target, string member)
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
