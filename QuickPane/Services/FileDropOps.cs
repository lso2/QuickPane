using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using NM = QuickPane.Interop.NativeMethods;

namespace QuickPane.Services
{
    /// <summary>
    /// Native-style file drops onto a folder: move on the same drive, copy across drives, with Ctrl to
    /// copy, Shift to move, Alt (or Ctrl+Shift) to create shortcuts, exactly like Explorer. Move and
    /// copy go through the shell, so they get the normal progress, conflict prompts, and undo.
    /// </summary>
    internal static class FileDropOps
    {
        public static DragDropEffects ComputeEffect(DragDropEffects allowed, DragDropKeyStates keys, string[] sources, string destFolder)
        {
            DragDropEffects e;
            if ((keys & DragDropKeyStates.AltKey) != 0) e = DragDropEffects.Link;
            else if ((keys & DragDropKeyStates.ControlKey) != 0 && (keys & DragDropKeyStates.ShiftKey) != 0) e = DragDropEffects.Link;
            else if ((keys & DragDropKeyStates.ControlKey) != 0) e = DragDropEffects.Copy;
            else if ((keys & DragDropKeyStates.ShiftKey) != 0) e = DragDropEffects.Move;
            else e = SameDrive(sources, destFolder) ? DragDropEffects.Move : DragDropEffects.Copy;

            if (allowed != DragDropEffects.None && (allowed & e) == 0)
            {
                if ((allowed & DragDropEffects.Copy) != 0) e = DragDropEffects.Copy;
                else if ((allowed & DragDropEffects.Move) != 0) e = DragDropEffects.Move;
                else if ((allowed & DragDropEffects.Link) != 0) e = DragDropEffects.Link;
                else e = DragDropEffects.None;
            }
            return e;
        }

        private static bool SameDrive(string[] sources, string dest)
        {
            try
            {
                var destRoot = Path.GetPathRoot(dest);
                return sources != null && sources.All(s =>
                    string.Equals(Path.GetPathRoot(s), destRoot, StringComparison.OrdinalIgnoreCase));
            }
            catch { return false; }
        }

        public static void Perform(string[] sources, string destFolder, DragDropEffects effect)
        {
            try
            {
                sources = (sources ?? new string[0]).Where(s => !string.IsNullOrEmpty(s)).ToArray();
                if (sources.Length == 0 || string.IsNullOrEmpty(destFolder) || !Directory.Exists(destFolder)) return;

                if (effect == DragDropEffects.Link)
                {
                    foreach (var s in sources) CreateShortcut(s, destFolder);
                    return;
                }
                if (effect != DragDropEffects.Move && effect != DragDropEffects.Copy) return;

                var op = new NM.SHFILEOPSTRUCT
                {
                    hwnd = IntPtr.Zero,
                    wFunc = effect == DragDropEffects.Move ? NM.FO_MOVE : NM.FO_COPY,
                    pFrom = Marshal.StringToHGlobalUni(string.Join("\0", sources) + "\0"),
                    pTo = Marshal.StringToHGlobalUni(destFolder + "\0"),
                    fFlags = (ushort)(NM.FOF_ALLOWUNDO | NM.FOF_NOCONFIRMMKDIR)
                };
                try { NM.SHFileOperation(ref op); }
                finally { Marshal.FreeHGlobal(op.pFrom); Marshal.FreeHGlobal(op.pTo); }
            }
            catch (Exception ex) { Log.Error("file drop op", ex); }
        }

        private static void CreateShortcut(string source, string destFolder)
        {
            try
            {
                var leaf = Path.GetFileName(source.TrimEnd('\\', '/'));
                if (string.IsNullOrEmpty(leaf)) leaf = source;
                var link = Path.Combine(destFolder, leaf + ".lnk");
                int n = 1;
                while (File.Exists(link))
                {
                    n++;
                    var suffix = n == 2 ? " - Shortcut" : " - Shortcut (" + n + ")";
                    link = Path.Combine(destFolder, leaf + suffix + ".lnk");
                }
                ShellLink.Create(link, source, leaf);
            }
            catch (Exception ex) { Log.Error("create shortcut", ex); }
        }
    }
}
