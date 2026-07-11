# Changelog

All notable changes to QuickPane are recorded here. The major number marks a structural shift, the minor number marks a feature set, and the patch number marks fixes. The newest release is listed first.

## [3.5.1] - 2026-07-11

### Fixed
- Groups could not be reordered within a profile's own list on the settings page, in either the tray window or the sidebar's embedded copy. Dragging a group's grip picked it up and showed a valid drop cursor, but dropping it back into the same list did nothing, because the drop handler only ever moved a group to a different profile column and silently ignored a same-profile drop. Each group row now accepts the drop itself and reorders in place; moving a group to another profile column is unchanged.

### Changed
- The sidebar's gear button now opens the same Settings window the tray icon opens, instead of sliding a second, cramped copy of the settings UI into the sidebar itself. The compact single-column settings layout introduced in 3.1.0 for that embedded copy is removed along with it, since nothing uses it anymore.

## [3.4.1] - 2026-07-06

### Fixed
- System-wide input lag while navigating. Everything that can touch a slow disk, the network, or cross-process COM now runs on one background worker (`WorkQueue`) instead of the UI thread, which shares Explorer's input queue in Inside mode, so a dead network target or a busy Explorer can no longer freeze input across the desktop while CPU sits idle. This covers shortcut resolution, Shell.Application navigation and path queries, the Windows Recent scan, drive detail queries, existence probes, shell file operations from drops, and slow icon extraction.
- Recent tracking no longer installs a global NAMECHANGE hook. That hook fired for nearly every control text change on the desktop and answered each with a cross-process COM enumeration; activation events plus a slow poll now do the same job for a fraction of the cost.
- The Windows Recent folder is scanned newest-first with a resolution budget, so a Recent folder holding hundreds of shortcuts costs only as much as the visible list.
- Pin, group, and recent rows never probe the disk while building. Existence and file-vs-folder facts come from a background probe cache (`PathStatus`); unknown targets render optimistically and correct themselves moments later, dimming exactly like Explorer does when a share is gone.
- Save/Open dialog panes no longer glitch after the dialog is dragged. A pure move fires a location event per pixel but needs no relayout (controls live in client coordinates), and those events used to trip the relayout back-off and permanently stop pane maintenance for that dialog. Back-off now also resets when the dialog really resizes.
- Dialog detection results are cached per window. Foreground changes and sweep passes no longer re-enumerate the child tree of every open dialog in every app (Photoshop's dialogs were enumerated on every focus change), and an unfamiliar picker is logged once instead of every time it takes focus.
- Every message sent into another app's dialog (navigation text, Enter, OK clicks) uses a timeout, so a host app that is busy saving can never hang QuickPane and, through the shared input queue, the shell.
- Unhandled exceptions are now logged to debug.log and dispatcher exceptions are contained, so crashes are visible and one bad event no longer silently kills the tray app. Missed window-destroy events are swept up, so lost hooks no longer leak panes.
- Group reorders are crash-safe. A reorder interrupted midway (locked folder, antivirus, crash) rolls back instead of stranding groups under temp names, and any temp names left by a hard crash are recovered on the next scan.
- Recents and groups change events always arrive on the UI thread, removing a race between the FileSystemWatcher thread and the pane.
- A navigation no longer rebuilds every section of every open sidebar; each section rebuilds only on its own data, coalesced to one rebuild per change burst.
- Settings saves no longer reload the group store (and its FileSystemWatcher) unless the groups folder actually changed.
- Drag-over highlight sticks no more: rows track the active drop target centrally, so highlight always clears when the drag moves on, drops, or leaves the pane.

### Added
- File pins. Files can be pinned everywhere folders can: drop a file on a group, between pins, or on empty section space, or use the new "Add file..." menu item. File pins open the file on click, offer "Open containing folder", and show the file's own icon; recent files can be pinned directly as file pins.
- Drop-intent menu. Dropping a folder (no modifier) onto a pinned folder now asks: Move into, Copy into, Create shortcut inside, Pin to the group, or Cancel, because a silent default was moving folders people meant to pin. Files keep Explorer's silent semantics (move on same drive, copy across), and modifiers always act silently exactly like Explorer: Ctrl copies, Shift moves, Alt or Ctrl+Shift links.
- Dropping files or folders on empty Groups-section space pins them to the last group (creating a "Pinned" group when none exists), and the drag cursor shows the shortcut arrow wherever a drop will pin rather than move.

### Changed
- Drive details (labels, free space, readiness) load from a cached snapshot and refresh in the background on device changes, so an unreachable mapped drive cannot stall pane rebuilds.
- Expanding a folder row enumerates its children on the worker and fills the subtree when ready, so expanding a slow or dead folder cannot freeze input.

## [3.2.15] - 2026-07-05

### Fixed
- White pane on load and toolbar overlap, fixed with a readiness gate: nothing is shifted and the pane stays hidden until the Explorer window is genuinely presented and its toolbar has laid out, with a five-second fallback for windows that never report one.
- Resize lag, fixed by replacing the perpetual 250 Hz enforcer poll with short bursts triggered only when a window first shows its strip, is activated, or attaches; resizes are held purely by location events with nothing polling against them.
- Two regressions were reverted along the way: hand-driving Measure/Arrange/UpdateLayout on the pane's visual root (corrupted the host's layout pipeline), and `SWP_ASYNCWINDOWPOS` on content-host moves (ignored because the reparent attaches input queues, and it broke the move dedup guards).

## [3.1.1] - 2026-06-01

### Added
- Persistent expand/collapse memory. Section headers, groups, and folder subtrees now remember whether they are open, keyed by section type, group folder, and tree path, so the state survives pane rebuilds, profile switches, window switches, and restarts. State is stored in `uistate.txt` in the QuickPane data folder.
- Horizontal scrolling for the profile tab row, so extra profiles stay reachable instead of clipping, using the same wheel handling as the group tabs.

## [3.1.0] - 2026-06-01

### Fixed
- Critical stability fix. File dialog detection now requires the dialog window class (`#32770`). The Windows desktop (Progman/WorkerW) also hosts a `SHELLDLL_DefView`, so the earlier broad match reparented and resized the desktop, which corrupted explorer.exe and took down every Explorer window at once.
- Empty profile columns accept dropped groups, because the card now has a solid fill and is a valid drop target.
- Group rows in the settings page drag from anywhere on the row rather than only the small grip, and the row's delete button is excluded from the drag.

### Added
- Profile tab row beneath the QuickPane title that switches the shown profile, with a "Show profiles tab row" setting and an "Auto-hide (slide out on hover)" setting that mirrors the desktop dock behavior.
- Compact single-column settings layout for the narrow embedded pane, kept separate from the wide multi-column layout used in the tray settings window.
- Profile columns drawn as cards a step lighter than the panel, each with a clear Activate button and an Active label on the shown profile.

## [3.0.0] - 2026-05-31

### Added
- Profiles. Each profile is an independent workspace with its own groups folder, recents count, and sidebar width, and the active profile's values mirror into the live settings the rest of the app reads.
- Profile switching from the tray icon's right-click menu, with a check on the active profile.
- Multi-column profile management in the settings window, with add, remove, and rename for each profile.
- Dragging a group between profile columns to move it, with Ctrl held to drop a copy so the same group can live in more than one profile.
- Resizable settings window.

### Changed
- A settings file from before profiles migrates automatically into "Profile 1" on first load.

## [2.4.0] - 2026-05-30

### Added
- QuickPane attaches to file Open and Save dialogs and honors the selected pane mode. Inside mode shifts every one of the dialog's controls and widens the dialog so nothing overlaps the pane, and beside mode floats a pane against the dialog's edge.
- Dialog navigation by writing the folder path into the File name box and submitting, working with both the classic dialog and the modern common item dialog.

## [2.3.0] - 2026-05-28

### Changed
- The beside-window pane is now an owned window of its Explorer window, so Windows keeps it at that window's z-level, on that window's virtual desktop, and never forces it above other windows. It stays visible when the window loses focus and hides only when the window is minimized or moves to another desktop.

### Added
- Horizontal wheel input scrolls a tab row left and right, while the vertical wheel only ever scrolls the pane.
- All-desktops dock pinning retries after the window has rendered so the pin reliably takes effect.

### Fixed
- Recent reflects folders browsed directly in Explorer, captured through window activation, title changes, and a light periodic check, with a location-URL fallback when the shell folder object returns nothing.

## [2.2.0] - 2026-05-25

### Added
- Native file drag operations. Dragging files from Explorer onto a folder moves on the same drive and copies across drives, with Ctrl to copy, Shift to move, and Alt to create a shortcut, using the shell so progress, conflicts, and undo behave normally.
- Title header with the QuickPane logo and a chevron that collapses or expands the pane in the window modes and toggles auto-hide on the desktop dock.

### Changed
- Helper windows and the dock are tool windows, so QuickPane no longer appears as a blank entry in Alt+Tab.
- The support button reads "Buy me a coffee".

## [2.1.0] - 2026-05-23

### Added
- Optional desktop dock at the left screen edge, registered as an AppBar and independent of the window pane.
- Dock auto-hide that collapses to a thin strip and slides out on hover.
- Setting to show the dock on every virtual desktop.
- Dock clicks navigate the active Explorer window and bring it forward, or open a new window when none is open.

## [2.0.0] - 2026-05-21

### Added
- Beside-window mode that floats a follower pane at the left of each Explorer window without shifting its content, alongside the original inside mode that reparents the pane.
- Off mode for dock-only use.
- Pane mode selector in settings.

## [1.2.0] - 2026-05-18

### Added
- Network and Linux (WSL) as their own sections, each with a visibility checkbox and a proper icon.
- Expandable drives under This PC and expandable folder subtrees throughout, enumerated lazily like the native Explorer tree.

## [1.1.0] - 2026-05-15

### Added
- Group, tab, and pin management with create, rename, and delete.
- Drag to reorder groups, tabs, and pins with a visible drop line, plus a semi-transparent placeholder when reordering tabs.
- Dragging a group onto another merges its tabs, and dragging a tab to empty space makes it a new group.
- A seeded default group on first run so the pane is never empty.

### Changed
- All text entry goes through top-level prompt windows, because the embedded pane cannot take keyboard focus.

## [1.0.0] - 2026-05-12

### Added
- First usable version. A sidebar that reparents into each Explorer window and shifts the window's content to make room.
- Pinned folder groups with horizontally scrollable tabs, where a single-tab group renders like a plain group.
- This PC and Recent sections.
- Settings panel, tray icon, and a "Pin to Quick Pane" folder context-menu verb.
- One-click Inno Setup installer that runs without administrator rights.
