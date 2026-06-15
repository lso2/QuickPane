using System;
using System.Runtime.InteropServices;
using QuickPane.Services;

namespace QuickPane.Interop
{
    /// <summary>
    /// Drives a specific Explorer window from outside its process. Uses the Shell.Application
    /// automation object (late bound, so no COM reference is needed) to find the
    /// ShellBrowserWindow whose top-level HWND matches the window we are embedded in, then
    /// navigates it in place. This is the supported way to move an existing Explorer window,
    /// equivalent to the user clicking a folder in the native nav pane.
    /// </summary>
    internal static class ExplorerNavigator
    {
        /// <summary>Navigate the Explorer window identified by topLevelHwnd to path, in place.</summary>
        public static bool Navigate(IntPtr topLevelHwnd, string path)
        {
            if (string.IsNullOrEmpty(path)) return false;

            object shell = null;
            object windows = null;
            try
            {
                var shellType = Type.GetTypeFromProgID("Shell.Application");
                if (shellType == null) return false;
                shell = Activator.CreateInstance(shellType);

                dynamic shellApp = shell;
                windows = shellApp.Windows();
                dynamic shellWindows = windows;

                long target = topLevelHwnd.ToInt64();
                int count = (int)shellWindows.Count;
                for (int i = 0; i < count; i++)
                {
                    dynamic w = null;
                    try
                    {
                        w = shellWindows.Item(i);
                        if (w == null) continue;

                        long hwnd;
                        try { hwnd = (long)w.HWND; }
                        catch { continue; } // non-Explorer items (IE) can throw on HWND

                        if (hwnd != target) continue;

                        object url = path;
                        w.Navigate2(ref url);
                        return true;
                    }
                    catch (Exception ex)
                    {
                        Log.Error("Navigate2 attempt failed", ex);
                    }
                    finally
                    {
                        if (w != null && Marshal.IsComObject(w)) Marshal.ReleaseComObject(w);
                    }
                }

                Log.Info("No ShellBrowserWindow matched hwnd " + target + "; opening in place was skipped.");
                return false;
            }
            catch (Exception ex)
            {
                Log.Error("ExplorerNavigator.Navigate failed for '" + path + "'", ex);
                return false;
            }
            finally
            {
                if (windows != null && Marshal.IsComObject(windows)) Marshal.ReleaseComObject(windows);
                if (shell != null && Marshal.IsComObject(shell)) Marshal.ReleaseComObject(shell);
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
                dynamic shellApp = shell;
                windows = shellApp.Windows();
                dynamic shellWindows = windows;

                long target = topLevelHwnd.ToInt64();
                int count = (int)shellWindows.Count;
                for (int i = 0; i < count; i++)
                {
                    dynamic w = null;
                    try
                    {
                        w = shellWindows.Item(i);
                        if (w == null) continue;
                        long hwnd;
                        try { hwnd = (long)w.HWND; }
                        catch { continue; }
                        if (hwnd != target) continue;
                        try
                        {
                            string p = (string)w.Document.Folder.Self.Path;
                            if (!string.IsNullOrEmpty(p)) return p;
                        }
                        catch { }
                        try
                        {
                            string url = (string)w.LocationURL; // e.g. file:///C:/Users/...
                            if (!string.IsNullOrEmpty(url))
                            {
                                var u = new Uri(url);
                                if (u.IsFile) return u.LocalPath;
                            }
                        }
                        catch { }
                        return null;
                    }
                    catch { }
                    finally { if (w != null && Marshal.IsComObject(w)) Marshal.ReleaseComObject(w); }
                }
                return null;
            }
            catch (Exception ex) { Log.Error("GetCurrentPath failed", ex); return null; }
            finally
            {
                if (windows != null && Marshal.IsComObject(windows)) Marshal.ReleaseComObject(windows);
                if (shell != null && Marshal.IsComObject(shell)) Marshal.ReleaseComObject(shell);
            }
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
                Log.Error("OpenNewWindow failed for '" + path + "'", ex);
            }
        }
    }
}
