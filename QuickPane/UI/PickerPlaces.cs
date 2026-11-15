// PickerPlaces.cs
// Purpose: the pins listed in the navigation pane of QuickPane's own file and folder pickers.
// Reason: DialogPaneWatcher attaches no pane to QuickPane's own dialogs.
// Defines: PickerPlaces.
// Origin: ported from jprisant's fork, commits 7050d14 and 118d6ab (MIT, see NOTICE.md).
// Doc: docs/notes/QuickPane/UI/PickerPlaces.cs.md
using System;
using System.Collections.Generic;
using QuickPane.Services;

namespace QuickPane.UI
{
    /// <summary>
    /// Pins for the navigation pane of QuickPane's own pickers, added through IFileDialog::AddPlace to
    /// the list Windows fills with Quick Access. The window event hooks skip QuickPane's own process,
    /// and a pane on those pickers would get no window events.
    /// </summary>
    internal static class PickerPlaces
    {
        /// <summary>The most pins added to a picker's navigation pane.</summary>
        internal const int MaxPlaces = 50;

        /// <summary>Folder pins in pane order: the visible tab of each expanded group, each path once.
        /// Only targets confirmed as existing folders in the probe cache (PathStatus) are listed, with no
        /// disk access here.</summary>
        internal static List<string> ForPicker()
        {
            var result = new List<string>();
            try
            {
                var groups = App.Groups != null ? App.Groups.Groups : null;
                if (groups == null) return result;
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var group in groups)
                {
                    if (group == null || !group.Expanded) continue;
                    var tab = group.Active;
                    if (tab == null || tab.Items == null) continue;
                    foreach (var pin in tab.Items)
                    {
                        var path = pin == null ? null : pin.TargetPath;
                        if (string.IsNullOrWhiteSpace(path)) continue;
                        if (!PathStatus.ConfirmedAlive(path) || PathStatus.IsFile(path)) continue;
                        if (!seen.Add(path.TrimEnd('\\', '/'))) continue;
                        result.Add(path);
                        if (result.Count >= MaxPlaces) return result;
                    }
                }
            }
            catch (Exception ex) { Log.Error("collect pins for a picker", ex); }
            return result;
        }

        /// <summary>Add the pins to one of WPF's file dialogs. WPF hands each one to AddPlace and skips a
        /// place that fails.</summary>
        internal static void AddTo(Microsoft.Win32.FileDialog dialog)
        {
            if (dialog == null) return;
            foreach (var path in ForPicker())
            {
                try { dialog.CustomPlaces.Add(new Microsoft.Win32.FileDialogCustomPlace(path)); }
                catch (Exception ex) { Log.Error("add pin '" + path + "' to a file dialog", ex); }
            }
        }
    }
}
