// ShellFolders.cs
// Purpose: lists the folders inside a shell location that is not a plain directory: shell: folders, ::{CLSID} folders and \\server roots.
// Reason: Shell.Application lists the Network, \\wsl$ and server-root folders that Directory.GetDirectories rejects.
// Defines: ShellFolders, ShellFolders.Entry.
// Doc: docs/notes/QuickPane/Services/ShellFolders.cs.md
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using QuickPane.Interop;

namespace QuickPane.Services
{
    /// <summary>Child folders of a shell location. Enumerating Network or a server can take seconds, and
    /// List runs only on the STA worker.</summary>
    internal static class ShellFolders
    {
        public sealed class Entry
        {
            public string Name;
            public string Path;
        }

        /// <summary>True for a path Directory.GetDirectories cannot enumerate: shell: and ::{CLSID} folders
        /// and a bare \\server root.</summary>
        public static bool NeedsShell(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            if (path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)) return true;
            if (path.StartsWith("::", StringComparison.Ordinal)) return true;
            if (!path.StartsWith(@"\\", StringComparison.Ordinal)) return false;
            var rest = path.Substring(2).TrimEnd('\\');
            return rest.Length > 0 && rest.IndexOf('\\') < 0;
        }

        /// <summary>The folders directly inside <paramref name="path"/>, in the order the shell lists them.
        /// Call only from the worker.</summary>
        public static List<Entry> List(string path)
        {
            var list = new List<Entry>();
            object shell = null, folder = null, items = null;
            try
            {
                var t = Type.GetTypeFromProgID("Shell.Application");
                if (t == null) { Log.Info("listing '" + path + "': Shell.Application is not registered."); return list; }
                shell = Activator.CreateInstance(t);
                folder = ExplorerNavigator.Call(shell, "NameSpace", path);
                if (folder == null) { Log.Info("listing '" + path + "': the shell returned no folder for it."); return list; }
                items = ExplorerNavigator.Call(folder, "Items");
                int count = Convert.ToInt32(ExplorerNavigator.Get(items, "Count"));
                for (int i = 0; i < count; i++)
                {
                    object item = null;
                    try
                    {
                        item = ExplorerNavigator.Call(items, "Item", i);
                        if (item == null || !Convert.ToBoolean(ExplorerNavigator.Get(item, "IsFolder"))) continue;
                        var p = ExplorerNavigator.Get(item, "Path") as string;
                        if (string.IsNullOrEmpty(p)) continue;
                        // The shell reports a .zip as a folder, and a file-system item counts here only when it is a directory.
                        if (Convert.ToBoolean(ExplorerNavigator.Get(item, "IsFileSystem")) && !Directory.Exists(p)) continue;
                        var n = ExplorerNavigator.Get(item, "Name") as string;
                        list.Add(new Entry { Name = string.IsNullOrEmpty(n) ? p : n, Path = p });
                    }
                    finally { Release(item); }
                }
            }
            catch (Exception ex) { Log.Info("listing the folders inside '" + path + "' failed: " + ex.Message); }
            finally { Release(items); Release(folder); Release(shell); }
            return list;
        }

        private static void Release(object o)
        {
            try { if (o != null && Marshal.IsComObject(o)) Marshal.ReleaseComObject(o); }
            catch { }
        }
    }
}
