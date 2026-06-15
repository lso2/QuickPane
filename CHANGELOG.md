# Changelog

All notable changes to QuickPane are recorded here. The major number marks a structural shift, the minor number marks a feature set, and the patch number marks fixes. The newest release is listed first.

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
