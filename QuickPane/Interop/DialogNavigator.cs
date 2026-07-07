using System;
using QuickPane.Services;
using NM = QuickPane.Interop.NativeMethods;

namespace QuickPane.Interop
{
    /// <summary>
    /// Drives a file Open/Save common dialog to a folder. These dialogs are not Explorer windows, so the
    /// Shell automation used for Explorer does not apply. Instead we put the folder path into the dialog's
    /// "File name" box and trigger its Open button: when that box holds a folder, the dialog navigates
    /// into the folder instead of closing. This is the same trick used by file-dialog enhancers.
    /// </summary>
    internal static class DialogNavigator
    {
        /// <summary>True if hwnd is a file Open/Save dialog. The reliable signal across both the classic
        /// (GetOpenFileName) and the newer common-item dialogs is a dialog window (#32770) that contains
        /// a File name combo (ComboBoxEx32). Plain message boxes are #32770 too but have no such combo.
        /// The combo is often nested several levels deep, so the search is recursive.</summary>
        public static bool IsFileDialog(IntPtr hwnd)
        {
            if (!NM.IsWindow(hwnd)) return false;
            var cls = NM.ClassOf(hwnd);
            if (cls == "CabinetWClass" || cls == "ExploreWClass") return false; // real Explorer, handled elsewhere
            // Only real dialog windows. This is critical: the desktop (Progman/WorkerW) also hosts a
            // SHELLDLL_DefView, and must never be treated as a dialog or it gets reparented and mangled.
            if (cls != "#32770") return false;
            // The classic dialog has a ComboBoxEx32 File name box; the modern (IFileDialog) one instead
            // hosts the shell file view (SHELLDLL_DefView) with a plain ComboBox. Accept either. One
            // enumeration pass checks both classes and gives up on huge trees (Photoshop dialogs carry
            // dozens of DroverLord children), so probing a foreign dialog stays cheap.
            bool found = false;
            int visited = 0;
            NM.EnumChildWindows(hwnd, (h, l) =>
            {
                var c = NM.ClassOf(h);
                if (c == "ComboBoxEx32" || c == "SHELLDLL_DefView") { found = true; return false; }
                return ++visited < 256;
            }, IntPtr.Zero);
            return found; // plain message boxes have neither marker
        }

        private static IntPtr FindDescendant(IntPtr root, string cls)
        {
            IntPtr found = IntPtr.Zero;
            NM.EnumChildWindows(root, (h, l) =>
            {
                if (NM.ClassOf(h) == cls) { found = h; return false; }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        private static bool HasDescendant(IntPtr root, string cls)
        {
            return FindDescendant(root, cls) != IntPtr.Zero;
        }

        /// <summary>True for the legacy comdlg32 dialog (GetOpenFileName/GetSaveFileName, e.g. IrfanView),
        /// which has a ComboBoxEx32 File name box. Its layout is owned by comdlg32 and breaks if its
        /// controls are reparented or shifted, so the pane must attach beside it rather than inside.</summary>
        public static bool IsClassicDialog(IntPtr hwnd)
        {
            return NM.IsWindow(hwnd) && HasDescendant(hwnd, "ComboBoxEx32");
        }

        public static bool Navigate(IntPtr dlg, string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !NM.IsWindow(dlg)) return false;

                // Prefer the address bar. Typing the path there changes the folder without ever touching
                // the File name box, so it cannot trigger a save. This is the only safe way for app dialogs
                // such as Photoshop's, whose File name box commits a save on Enter no matter the content.
                IntPtr addr = FindAddressEdit(dlg);
                if (addr != IntPtr.Zero)
                {
                    SendTextTimeout(addr, path);
                    SendEnter(addr);
                    return true;
                }

                // Classic comdlg32 has no usable address bar, but it navigates safely when its OK button is
                // pressed with a directory path in the File name box.
                if (IsClassicDialog(dlg))
                {
                    IntPtr edit = FindFileNameEdit(dlg);
                    if (edit == IntPtr.Zero) return false;
                    SendTextTimeout(edit, path.EndsWith("\\") ? path : path + "\\");
                    IntPtr ok = NM.GetDlgItem(dlg, NM.IDOK);
                    if (ok != IntPtr.Zero) SendTimeout(ok, NM.BM_CLICK, IntPtr.Zero, IntPtr.Zero);
                    else SendEnter(edit);
                    return true;
                }

                // Modern dialog with no findable address bar: do nothing, rather than risk a save through
                // the File name box.
                Log.Info("dialog navigate: no address bar, skipped to avoid an accidental save");
                return false;
            }
            catch (Exception ex) { Log.Error("dialog navigate", ex); return false; }
        }

        // All sends into a foreign dialog use a timeout: the target app may be busy (Photoshop
        // writing a file), and a plain SendMessage would block us for as long as it takes, which
        // stalls the input queue our UI thread shares with Explorer in Inside mode.
        private const uint SendTimeoutMs = 1500;

        private static void SendTextTimeout(IntPtr hwnd, string text)
        {
            IntPtr result;
            NM.SendMessageTimeout(hwnd, NM.WM_SETTEXT, IntPtr.Zero, text,
                NM.SMTO_ABORTIFHUNG, SendTimeoutMs, out result);
        }

        private static void SendTimeout(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam)
        {
            IntPtr result;
            NM.SendMessageTimeout(hwnd, msg, wParam, lParam,
                NM.SMTO_ABORTIFHUNG, SendTimeoutMs, out result);
        }

        // Press Enter inside a path box. Sending the key directly to the edit lets the address bar (or the
        // modern File name combo) navigate into a directory without committing the dialog.
        private static void SendEnter(IntPtr edit)
        {
            SendTimeout(edit, NM.WM_KEYDOWN, new IntPtr(NM.VK_RETURN), IntPtr.Zero);
            SendTimeout(edit, NM.WM_CHAR, new IntPtr(NM.VK_RETURN), IntPtr.Zero);
            SendTimeout(edit, NM.WM_KEYUP, new IntPtr(NM.VK_RETURN), IntPtr.Zero);
        }

        // The address bar's editable path box (under "Address Band Root"). Setting it and pressing Enter
        // changes the dialog's folder without touching the File name box, so it can never trigger a save.
        private static IntPtr FindAddressEdit(IntPtr dlg)
        {
            IntPtr band = FindDescendant(dlg, "Address Band Root");
            if (band == IntPtr.Zero) return IntPtr.Zero;
            return FindDescendant(band, "Edit");
        }

        // The editable File name control: ComboBoxEx32 -> ComboBox -> Edit, nested anywhere in the tree.
        private static IntPtr FindFileNameEdit(IntPtr dlg)
        {
            IntPtr cbex = FindDescendant(dlg, "ComboBoxEx32");
            if (cbex != IntPtr.Zero)
            {
                IntPtr cb = NM.FindWindowEx(cbex, IntPtr.Zero, "ComboBox", null);
                if (cb != IntPtr.Zero)
                {
                    IntPtr edit = NM.FindWindowEx(cb, IntPtr.Zero, "Edit", null);
                    if (edit != IntPtr.Zero) return edit;
                }
                IntPtr e2 = NM.FindWindowEx(cbex, IntPtr.Zero, "Edit", null);
                if (e2 != IntPtr.Zero) return e2;
            }
            // Modern (IFileDialog): the File name field is a plain ComboBox with an Edit child. Pick the
            // first ComboBox that contains an Edit; the file-type combo is a drop list with none.
            IntPtr found = IntPtr.Zero;
            NM.EnumChildWindows(dlg, (h, l) =>
            {
                if (NM.ClassOf(h) == "ComboBox")
                {
                    IntPtr e = NM.FindWindowEx(h, IntPtr.Zero, "Edit", null);
                    if (e != IntPtr.Zero) { found = e; return false; }
                }
                return true;
            }, IntPtr.Zero);
            if (found != IntPtr.Zero) return found;
            // Last resort: any Edit descendant.
            return FindDescendant(dlg, "Edit");
        }
    }
}
