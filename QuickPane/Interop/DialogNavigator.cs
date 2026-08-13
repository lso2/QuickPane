using System;
using System.Text;
using QuickPane.Services;
using NM = QuickPane.Interop.NativeMethods;

namespace QuickPane.Interop
{
    /// <summary>
    /// Drives a file Open/Save common dialog to a folder. These dialogs are not Explorer windows, so the
    /// Shell automation used for Explorer does not apply. The address bar is written instead, and the
    /// write is read back before Enter is pressed, because a dialog that ignores or rewrites the path
    /// used to leave the pane looking like it had navigated when nothing moved. A rejected write is now
    /// named in the journal and the classic route is tried instead.
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

        /// <summary>Apps whose File name box committed the dialog instead of navigating into the folder.
        /// Their later dialogs never take the fallback route again, so an app that saves on a directory
        /// path can do it at most once and is named in the journal when it does.</summary>
        private static readonly System.Collections.Generic.HashSet<string> _committingApps =
            new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public static bool Navigate(IntPtr dlg, string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !NM.IsWindow(dlg)) return false;
                Log.Activity("dialog navigate " + dlg.ToString("X") + " to " + path);

                // A trailing separator makes the string unambiguously a folder, which matters twice over:
                // no dialog can read it as a filename, and the shell rewrites it the moment it consumes
                // the path, which is the signal the navigation is checked against below.
                var typed = path.EndsWith("\\") ? path : path + "\\";

                // Prefer the address bar. Writing the path there changes the folder without ever touching
                // the File name box, so it cannot trigger a save. This is the only safe route for app
                // dialogs such as Photoshop's, whose File name box commits a save on Enter regardless of
                // what it holds.
                IntPtr addr = FindAddressEdit(dlg);
                if (addr != IntPtr.Zero && WriteAndVerify(addr, typed, "address bar"))
                {
                    SendEnter(addr);
                    string after;
                    if (Consumed(addr, typed, out after)) return true;

                    // The write landed and the Enter did not, which is the case that made Save As look
                    // intermittent: the pane reported a navigation the dialog never performed. Naming it
                    // here is what puts the pattern in the journal instead of leaving it to memory.
                    Log.Event("glitch", "the " + Owner(dlg) + " dialog " + dlg.ToString("X") +
                        " accepted \"" + typed + "\" in its address bar but still held \"" + after +
                        "\" afterwards, so the Enter was ignored and the folder did not change.");
                }
                else if (addr == IntPtr.Zero && !IsClassicDialog(dlg))
                {
                    // Classic comdlg32 has no address band by design and goes straight to the box below,
                    // so only a modern dialog missing one is worth a line.
                    Log.Event("dialog", "no usable address bar on the " + Owner(dlg) + " dialog " +
                        dlg.ToString("X") + ", so the File name box is used instead.");
                }

                return NavigateByFileNameBox(dlg, typed);
            }
            catch (Exception ex) { Log.Event("dialog", "navigation failed for '" + path + "'", ex); return false; }
        }

        /// <summary>Write a folder path into the File name box and submit it. Both comdlg32 and the modern
        /// common item dialog navigate rather than save when the text names a directory, which is what the
        /// trailing separator guarantees, and an app that commits anyway is remembered by name so it is
        /// never offered this route a second time.</summary>
        private static bool NavigateByFileNameBox(IntPtr dlg, string typed)
        {
            var owner = Owner(dlg);
            if (_committingApps.Contains(owner))
            {
                Log.Event("dialog", "the File name box route is not offered to " + owner +
                    " because its dialog committed on a folder path earlier this session, so \"" +
                    typed + "\" was skipped.");
                return false;
            }

            IntPtr edit = FindFileNameEdit(dlg);
            if (edit == IntPtr.Zero)
            {
                Log.Event("dialog", "the " + owner + " dialog " + dlg.ToString("X") +
                    " exposed no File name box, so \"" + typed + "\" was not opened.");
                return false;
            }

            if (!WriteAndVerify(edit, typed, "File name box")) return false;

            IntPtr ok = NM.GetDlgItem(dlg, NM.IDOK);
            if (ok != IntPtr.Zero) SendTimeout(ok, NM.BM_CLICK, IntPtr.Zero, IntPtr.Zero);
            else SendEnter(edit);

            // A dialog that closed rather than moved treated the folder path as a filename, so the app is
            // recorded and its dialogs keep to the address bar from here on.
            for (int i = 0; i < 8; i++)
            {
                System.Threading.Thread.Sleep(100);
                if (!NM.IsWindow(dlg))
                {
                    _committingApps.Add(owner);
                    Log.Event("glitch", "the " + owner + " dialog closed on the folder path \"" + typed +
                        "\" rather than opening it, so that app keeps to the address bar for the rest of " +
                        "this session.");
                    return false;
                }
            }
            return true;
        }

        /// <summary>True once the control no longer holds the exact string that was written. The shell
        /// rewrites a path it accepts, so an untouched string means the Enter was never acted on. The
        /// value seen last is handed back so a rejection can be recorded with what the control really
        /// held rather than only that it failed.</summary>
        private static bool Consumed(IntPtr hwnd, string typed, out string after)
        {
            after = typed;
            // Short window on purpose: this runs on the shared worker, and the cost of giving up too
            // early is only a second navigation to the folder the dialog already reached.
            for (int i = 0; i < 6; i++)
            {
                System.Threading.Thread.Sleep(100);
                if (!NM.IsWindow(hwnd)) { after = "(gone)"; return true; }
                var got = ReadText(hwnd);
                if (got == null) continue;
                after = got;
                if (!string.Equals(got, typed, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        // All sends into a foreign dialog use a timeout: the target app may be busy (Photoshop
        // writing a file), and a plain SendMessage would block us for as long as it takes, which
        // stalls the input queue our UI thread shares with Explorer in Inside mode.
        private const uint SendTimeoutMs = 1500;

        /// <summary>Write text into a foreign control and read it back. A dialog that rejects the write,
        /// rewrites it, or is too busy to answer is recorded by name rather than being treated as a
        /// successful navigation, which is what made Save As look intermittent.</summary>
        private static bool WriteAndVerify(IntPtr hwnd, string text, string what)
        {
            IntPtr result;
            var sent = NM.SendMessageTimeoutBuffer(hwnd, NM.WM_SETTEXT, IntPtr.Zero, new StringBuilder(text),
                NM.SMTO_ABORTIFHUNG, SendTimeoutMs, out result);
            if (sent == IntPtr.Zero)
            {
                Log.Event("dialog", what + " did not answer within " + SendTimeoutMs +
                    " ms, so the path was not written.");
                return false;
            }

            var got = ReadText(hwnd);
            if (got == null)
            {
                Log.Event("dialog", what + " accepted the path but would not report its text back, so the navigation was not confirmed.");
                return false;
            }

            var want = text.TrimEnd('\\');
            if (string.Equals(got.TrimEnd('\\'), want, StringComparison.OrdinalIgnoreCase)) return true;

            Log.Event("dialog", what + " holds \"" + got + "\" after being sent \"" + text +
                "\", so the write did not take.");
            return false;
        }

        private static string ReadText(IntPtr hwnd)
        {
            try
            {
                IntPtr len;
                if (NM.SendMessageTimeout(hwnd, NM.WM_GETTEXTLENGTH, IntPtr.Zero, IntPtr.Zero,
                        NM.SMTO_ABORTIFHUNG, SendTimeoutMs, out len) == IntPtr.Zero) return null;

                int chars = len.ToInt32();
                if (chars <= 0) return string.Empty;
                if (chars > 4096) chars = 4096;

                var sb = new StringBuilder(chars + 1);
                IntPtr copied;
                if (NM.SendMessageTimeoutBuffer(hwnd, NM.WM_GETTEXT, new IntPtr(sb.Capacity), sb,
                        NM.SMTO_ABORTIFHUNG, SendTimeoutMs, out copied) == IntPtr.Zero) return null;
                return sb.ToString();
            }
            catch { return null; }
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

        /// <summary>The address bar's editable path box, under "Address Band Root". Writing it and
        /// pressing Enter changes the dialog's folder without touching the File name box, so it can never
        /// trigger a save. Only an enabled Edit with real width counts, because the band also carries
        /// collapsed and placeholder edits that swallow a write and report nothing back.</summary>
        private static IntPtr FindAddressEdit(IntPtr dlg)
        {
            IntPtr band = FindDescendant(dlg, "Address Band Root");
            if (band == IntPtr.Zero) return IntPtr.Zero;

            IntPtr best = IntPtr.Zero;
            int bestWidth = 0;
            NM.EnumChildWindows(band, (h, l) =>
            {
                if (NM.ClassOf(h) != "Edit") return true;
                if (!NM.IsWindowEnabled(h)) return true;
                NM.RECT r;
                if (!NM.GetWindowRect(h, out r)) return true;
                if (r.Width <= bestWidth) return true;
                bestWidth = r.Width;
                best = h;
                return true;
            }, IntPtr.Zero);

            return bestWidth >= 40 ? best : IntPtr.Zero;
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

        /// <summary>Process name owning a dialog, so a journal entry names the app rather than only a
        /// window handle.</summary>
        public static string Owner(IntPtr hwnd)
        {
            try
            {
                uint pid;
                NM.GetWindowThreadProcessId(hwnd, out pid);
                if (pid == 0) return "unknown";
                return System.Diagnostics.Process.GetProcessById((int)pid).ProcessName;
            }
            catch { return "unknown"; }
        }
    }
}
