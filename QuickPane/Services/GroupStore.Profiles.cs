// GroupStore.Profiles.cs
// Purpose: group folder operations across profiles for the settings page: list, create, move, copy, reorder, recycle.
// Reason: these are static operations on any profile's groups root, separate from the active profile's loaded groups.
// Defines: GroupStore (partial): static cross-profile group operations.
// Doc: docs/notes/QuickPane/Services/GroupStore.Profiles.cs.md
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using QuickPane.Models;
using NM = QuickPane.Interop.NativeMethods;

namespace QuickPane.Services
{
    public partial class GroupStore
    {
        // ---- cross-profile group operations ----------------------------------
        // The settings page shows one column per profile. These read and move/copy group folders between
        // each profile's own groups root, identified by its expanded folder path.

        /// <summary>List (folderPath, displayName) of the groups under an expanded root, in order.</summary>
        public static List<Tuple<string, string>> ListGroups(string expandedRoot)
        {
            var list = new List<Tuple<string, string>>();
            try
            {
                if (!Directory.Exists(expandedRoot)) return list;
                var items = new List<Tuple<int, string, string>>();
                foreach (var d in Directory.GetDirectories(expandedRoot))
                {
                    int o; string n; Split(Path.GetFileName(d), out o, out n);
                    items.Add(Tuple.Create(o, d, n));
                }
                foreach (var it in items.OrderBy(x => x.Item1).ThenBy(x => x.Item3, StringComparer.CurrentCultureIgnoreCase))
                    list.Add(Tuple.Create(it.Item2, it.Item3));
            }
            catch (Exception ex) { Log.Error("ListGroups failed", ex); }
            return list;
        }

        /// <summary>Create a new group folder (with a first tab) under any expanded root.</summary>
        public static void CreateGroupFolder(string expandedRoot, string name)
        {
            try
            {
                name = Sanitize(name);
                if (string.IsNullOrWhiteSpace(name)) name = "Group";
                Directory.CreateDirectory(expandedRoot);
                var folder = UniqueDir(expandedRoot, NextOrderInRoot(expandedRoot), name);
                Directory.CreateDirectory(folder);
                Directory.CreateDirectory(Path.Combine(folder, "01_" + name)); // first tab
            }
            catch (Exception ex) { Log.Error("CreateGroupFolder failed", ex); }
        }

        /// <summary>Delete a group folder and its contents.</summary>
        public static void DeleteGroupFolder(string folder)
        {
            Recycle(folder);
        }

        /// <summary>
        /// Send a group's folder to the Recycle Bin rather than erasing it. Deleting a group takes every
        /// pin in it with it, so the delete has to be one a person can take back from the desktop the way
        /// they take back any other deleted folder.
        /// </summary>
        internal static void Recycle(string folder)
        {
            try
            {
                if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;

                // SHFileOperation reads pFrom as a double-null-terminated list.
                var from = folder + "\0\0";
                IntPtr buffer = Marshal.StringToHGlobalUni(from);
                try
                {
                    var op = new NM.SHFILEOPSTRUCT
                    {
                        wFunc = NM.FO_DELETE,
                        pFrom = buffer,
                        fFlags = (ushort)(NM.FOF_ALLOWUNDO | NM.FOF_NOCONFIRMATION | NM.FOF_NOERRORUI | NM.FOF_SILENT)
                    };
                    int rc = NM.SHFileOperation(ref op);
                    if (rc != 0)
                    {
                        Log.Info("the shell refused to recycle \"" + folder + "\" (code " + rc + "); deleting it instead.");
                        Directory.Delete(folder, true);
                    }
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            catch (Exception ex) { Log.Error("delete group folder '" + folder + "'", ex); }
        }

        /// <summary>Move a group folder into another profile's groups root (drag between columns).</summary>
        public static void MoveGroupFolder(string groupFolder, string destExpandedRoot)
        {
            try
            {
                if (string.IsNullOrEmpty(groupFolder) || !Directory.Exists(groupFolder)) return;
                Directory.CreateDirectory(destExpandedRoot);
                if (string.Equals(Path.GetDirectoryName(groupFolder), destExpandedRoot.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return;
                int o; string name; Split(Path.GetFileName(groupFolder), out o, out name);
                var dest = UniqueDir(destExpandedRoot, NextOrderInRoot(destExpandedRoot), name);
                Directory.Move(groupFolder, dest);
            }
            catch (Exception ex) { Log.Error("MoveGroupFolder failed", ex); }
        }

        /// <summary>Copy a group folder into another profile's groups root (Ctrl-drag between columns).</summary>
        public static void CopyGroupFolder(string groupFolder, string destExpandedRoot)
        {
            try
            {
                if (string.IsNullOrEmpty(groupFolder) || !Directory.Exists(groupFolder)) return;
                Directory.CreateDirectory(destExpandedRoot);
                int o; string name; Split(Path.GetFileName(groupFolder), out o, out name);
                var dest = UniqueDir(destExpandedRoot, NextOrderInRoot(destExpandedRoot), name);
                CopyDir(groupFolder, dest);
            }
            catch (Exception ex) { Log.Error("CopyGroupFolder failed", ex); }
        }

        /// <summary>Reorder the group folders under one profile's expanded root (drag-to-reorder within a
        /// profile's own group list in the settings page). Static and root-based like the other
        /// cross-profile helpers above, so it works on any profile column, active or not.</summary>
        public static void ReorderGroupFolders(string expandedRoot, List<string> orderedFolders)
        {
            try
            {
                var plan = new List<Tuple<string, string, string>>();
                for (int i = 0; i < orderedFolders.Count; i++)
                {
                    int o; string name; Split(Path.GetFileName(orderedFolders[i]), out o, out name);
                    plan.Add(Tuple.Create(orderedFolders[i],
                        Path.Combine(expandedRoot, "~ord~" + Guid.NewGuid().ToString("N")),
                        Path.Combine(expandedRoot, (i + 1).ToString("00") + "_" + name)));
                }
                TwoPhaseRename(plan, isDir: true);
            }
            catch (Exception ex) { Log.Error("ReorderGroupFolders failed", ex); }
        }

        private static int NextOrderInRoot(string root)
        {
            int max = 0;
            try
            {
                foreach (var d in Directory.GetDirectories(root))
                {
                    int o; string n; Split(Path.GetFileName(d), out o, out n);
                    if (o != int.MaxValue && o > max) max = o;
                }
            }
            catch { }
            return max + 1;
        }

        private static void CopyDir(string src, string dst)
        {
            Directory.CreateDirectory(dst);
            foreach (var f in Directory.GetFiles(src)) File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true);
            foreach (var d in Directory.GetDirectories(src)) CopyDir(d, Path.Combine(dst, Path.GetFileName(d)));
        }
    }
}
