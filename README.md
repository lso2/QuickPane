# QuickPane

![Windows](https://img.shields.io/badge/Windows-10%20%7C%2011-0078D6.svg?logo=windows&logoColor=white)
![.NET Framework](https://img.shields.io/badge/.NET%20Framework-4.8-512BD4.svg?logo=dotnet&logoColor=white)
![C#](https://img.shields.io/badge/C%23-7.3-239120.svg?logo=csharp&logoColor=white)
![UI](https://img.shields.io/badge/UI-WPF-1f6feb.svg)
![Version](https://img.shields.io/badge/Version-3.2.15-success.svg)
![License](https://img.shields.io/badge/License-MIT-orange.svg)

A folder sidebar for Windows File Explorer. QuickPane embeds a pinned-folders pane inside every Explorer window, so your groups, recent locations, drives, network shares, and WSL distros travel with the window you are already using. Pin it inside the window, float it beside the window, or run it as a slim screen-edge dock. Everything runs locally as a single tray app, no code is ever injected into Explorer, and all data stays in plain files under your user profile.

## Summary

QuickPane puts your folders one click away inside Explorer itself, with:

- 📁 **Inside, beside, or dock**: embed the pane in each Explorer window and shift its content, float a follower pane at the window's edge, or run dock-only
- 🗂️ **Pinned folder groups**: organize folders into groups and tabs, backed by real folders and shortcuts on disk
- 🕘 **Real Recent**: it tracks the folders you actually browse in Explorer, not only the ones clicked inside the pane
- 🖥️ **This PC, Network, and Linux**: expandable drives and lazy folder trees, plus Network and WSL sections
- 🪟 **Stays at the window's z-level**: the beside pane is an owned window of its Explorer window, never forced above everything else
- 📌 **Desktop dock**: an optional screen-edge AppBar with auto-hide and all-virtual-desktops pinning
- 💾 **Open and Save dialog support**: the pane attaches to file dialogs and navigates them too
- 👤 **Profiles**: independent workspaces, each with its own groups, recents count, and width
- 🎨 **Native feel**: dark and light themes that blend into the Windows shell
- 🔒 **Local storage**: groups are folders of shortcuts and settings are a JSON file you can back up

## Features

### Inside, beside, and dock modes

Three ways to show the pane, chosen in Settings:

- **Inside** reparents the pane into each Explorer window and shifts the file view to the right to make room, so the pane reads as part of the window.
- **Beside** floats a follower pane against the left edge of each window without shifting its content. The pane is an owned window of its Explorer window, so Windows keeps it at that window's z-level, on that window's virtual desktop, and hides it when the window is minimized.
- **Off** uses only the desktop dock and leaves Explorer windows untouched.

### Pinned folder groups

Folders live in groups, and a group can hold several tabs.

- Pin a folder from the **Pin to Quick Pane** context-menu verb on any folder, from the folder background, or by dragging it into the pane
- Create, rename, and delete groups, tabs, and pins
- Drag to reorder groups, tabs, and pins, with a visible drop line while reordering
- Drag one group onto another to merge their tabs, or drag a tab into empty space to make it a new group
- A group with a single tab renders like a plain list; with several tabs it shows a horizontally scrollable tab row
- A seeded default group appears on first run so the pane is never empty

### Sections

The pane is a stack of sections you can show, hide, reorder, and rename:

- **Groups**: your pinned folder groups
- **Recent**: folders browsed directly in Explorer, captured through window activation, title changes, and a light periodic check, with a location-URL fallback when the shell returns nothing, and a configurable count from 5 to 50
- **This PC**: drives that expand in place, with folder subtrees that enumerate lazily like the native Explorer tree
- **Network** and **Linux (WSL)**: each its own section with a proper icon and a visibility toggle

### Native file drag

Drag files from Explorer onto a pinned folder to file them. Dropping moves on the same drive and copies across drives, while Ctrl forces a copy, Shift forces a move, and Alt creates a shortcut. The operation runs through the shell, so progress, conflicts, and undo behave exactly as they do in Explorer.

### Desktop dock

An optional AppBar at the left screen edge, independent of the window pane.

- Auto-hide collapses it to a thin strip that slides out on hover
- A setting shows it on every virtual desktop instead of only the one it opened on
- Clicking a folder navigates the active Explorer window and brings it forward, or opens a new window when none is open

### Open and Save dialog support

QuickPane attaches to file Open and Save common dialogs and honors the same pane mode as real windows. Inside mode shifts every one of the dialog's controls and widens the dialog so nothing overlaps the pane, and beside mode floats a pane against the dialog's edge. Both the classic dialog and the modern common item dialog navigate to the clicked folder.

### Profiles

Each profile is an independent workspace with its own groups folder, recents count, and sidebar width.

- Switch profiles from the tray icon's right-click menu or from the profile tab row under the title
- The profile tab row can auto-hide, sliding out when the cursor is over the header
- Manage profiles in a multi-column settings layout with add, remove, and rename
- Drag a group between profile columns to move it, or hold Ctrl to drop a copy so one group can live in more than one profile

### Remembered layout

Section headers, groups, and folder subtrees remember whether they are open, keyed by section type, group folder, and tree path. The state survives pane rebuilds, profile switches, window switches, and restarts.

### Collapsible pane and appearance

- A title header with the logo and a chevron collapses the pane to a thin strip, or toggles auto-hide on the desktop dock
- Dark and light themes, applied live
- Adjustable pane width from 160 to 400 px, dragged from the inner edge
- Settings apply live with no Apply button

## Requirements

- **Windows 10 or Windows 11**, 64-bit (developed and tested on Windows 10 LTSC)
- **.NET Framework 4.8** runtime (preinstalled on current Windows)
- To build from source: **Visual Studio 2019/2022** with the .NET Framework 4.8 targeting pack
- To build the installer: **[Inno Setup](https://jrsoftware.org/isinfo.php)** 6+

## Installation

### Option A: Run a release build

1. Download the latest `QuickPaneSetup.exe` from the Releases page (or build it yourself below)
2. Run the installer. It installs per-user with no administrator rights, registers the **Pin to Quick Pane** folder verb, adds a startup entry, and launches QuickPane
3. Open any Explorer window and the pane appears inside it

### Option B: Build from source

```bash
git clone https://github.com/lso2/QuickPane.git
```

**Build the app**

1. Open `QuickPane.sln` in Visual Studio
2. Select the **Release | x64** configuration (x64 only)
3. Build the solution. The executable is produced at `QuickPane\bin\x64\Release\QuickPane.exe`
4. Run `QuickPane.exe`

**Build the installer (optional)**

1. Build the app first so `QuickPane.exe` exists at the path referenced in `[Files]`
2. Open `Installer.iss` in Inno Setup and press **Compile**
3. The single-file `QuickPaneSetup.exe` is produced in the output folder

> The installer's `[Files]` source points at the `Release\x64` output. If you build `Debug`, change `Release` to `Debug` in `Installer.iss`.

## Usage

### Pinning folders

- Right-click any folder in Explorer and choose **Pin to Quick Pane**, or right-click inside a folder's empty background, or drag a folder into the pane
- Click a pinned folder to navigate the current Explorer window to it

### Groups and tabs

- Drag a group, tab, or pin to reorder it
- Drag one group onto another to merge their tabs
- Drag a tab into empty space to split it into a new group
- Right-click a group or section header to rename it

### Choosing a mode

- Open Settings and pick **Inside**, **Beside**, or **Off** under Pane mode
- Enable the **Desktop dock** for a screen-edge bar that works in any mode

### Profiles

- Switch from the tray menu or the profile tab row under the title
- Add, rename, remove, and move groups between profiles in the Settings window

### Settings

- Open Settings from the tray icon or the gear in the pane
- Adjust mode, theme, pane width, recents count, section visibility and order, the desktop dock and its options, the profile tab row, and profiles
- Changes apply live

## Settings and data

All data is stored locally under your user profile:

```
%APPDATA%\QuickPane\
├── settings.json        # All settings, sections, dock options, and profiles
├── Groups\              # Groups, tabs, and pinned shortcuts on disk
├── uistate.txt          # Remembered open/closed state of sections, groups, and trees
└── debug.log            # Diagnostic log
```

Groups are stored as ordinary folders: a **group** is a folder under `Groups\`, a **tab** is a subfolder, and a **pin** is a `.lnk` shortcut. You can back up, edit, or rearrange the `Groups` tree directly and QuickPane reflects it.

### Privacy

- **No cloud sync**: everything stays on your computer
- **No accounts**: no login or registration
- **No telemetry**: QuickPane makes no external network requests
- **Readable formats**: a JSON settings file and ordinary Windows shortcuts you can open and back up anywhere

### settings.json format

```json
{
  "groupsPath": "%APPDATA%\\QuickPane\\Groups",
  "sections": [
    { "type": "groups",   "visible": true, "order": 0 },
    { "type": "recents",  "visible": true, "order": 1 },
    { "type": "computer", "visible": true, "order": 2 },
    { "type": "network",  "visible": true, "order": 3 },
    { "type": "linux",    "visible": true, "order": 4 }
  ],
  "recentsMaxCount": 15,
  "sidebarWidthPx": 220,
  "mode": "inside",
  "desktopDock": false,
  "desktopDockAutoHide": true,
  "desktopDockAllDesktops": false,
  "profiles": [
    { "name": "Profile 1", "groupsPath": "%APPDATA%\\QuickPane\\Groups", "recentsMaxCount": 15, "sidebarWidthPx": 220 }
  ],
  "activeProfile": 0,
  "showProfileTabs": true,
  "profileTabsAutoHide": false
}
```

## Compatibility

| OS | Supported | Notes |
|----|-----------|-------|
| Windows 11 | ✅ | Full support |
| Windows 10 (incl. LTSC) | ✅ | Developed and tested here |
| Windows 8.1 | ⚠️ | Untested; some shell integration may differ |
| Windows 7 | ⚠️ | Untested; the Explorer window classes differ |
| macOS / Linux | ❌ | Windows-only (Win32 + WPF) |

## Troubleshooting

### The pane is not showing in Explorer windows

Make sure Pane mode is **Inside** or **Beside** rather than **Off** in Settings. A new window shows the pane the moment Windows has presented it and laid out its toolbar, so the pane appears on its own without you resizing the window.

### A Save dialog saves to the wrong place when I click a folder

QuickPane navigates file dialogs through the address bar rather than the File name box, so clicking a folder changes the location without committing a save. If a third-party app runs its own custom dialog that fights the inside layout, QuickPane drops that dialog to the non-invasive beside pane automatically.

### An Explorer window looks shifted after QuickPane closes

Closing QuickPane restores each window's content view to full width. If a window was mid-load when QuickPane exited, open a new window and it will be normal.

### Build errors about C# syntax

The project uses the C# 7.3 language version that is the default for .NET Framework 4.8. Build with the .NET Framework 4.8 toolchain rather than retargeting the project to a newer framework.

## How it works

QuickPane is a tray application built on WPF and documented Win32 interop only; it never injects code into `explorer.exe`, so a fault in QuickPane can never bring Explorer down. The core ideas:

- **SetWinEventHook** out of process discovers Explorer folder windows and tracks them as they are created, shown, moved, activated, and destroyed
- **Inside mode** reparents a WPF `HwndSource` as a `WS_CHILD` of the `CabinetWClass` window and shifts Explorer's content host (`ShellTabWindowClass`) to the right. A readiness gate holds the shift until the window is actually presented and its toolbar has laid out, so the pane never flashes blank or sits over the toolbar
- **A short burst, not a constant poll**, holds the split the moment a window opens or is activated, because those are the only times Explorer resets its content to full width. Resize is held purely from location events, so dragging the window edge stays smooth
- **Beside mode** makes the Explorer window the pane's owner via `GWLP_HWNDPARENT`, so Windows keeps the pane at that window's z-level and on its virtual desktop with no manual tracking
- **File dialogs** are detected as a `#32770` carrying a `ComboBoxEx32` (classic comdlg32) or a `SHELLDLL_DefView` (modern common item dialog). Requiring `#32770` is deliberate, because the desktop itself hosts a `SHELLDLL_DefView` and must never be matched
- **The desktop dock** reserves the screen edge with `SHAppBarMessage` and pins itself to every virtual desktop through `IVirtualDesktopManager`
- **Groups on disk** are folders of `.lnk` shortcuts, read and written through the shell `IShellLink`, so the model is just files you can see
- **Settings** serialize to JSON with `DataContractJsonSerializer` using atomic temp-then-replace writes, with migration that upgrades older files in place; remembered open/closed state lives in `uistate.txt`

## Project structure

```
QuickPane/
├── QuickPane.sln              # Visual Studio solution
├── Installer.iss              # Inno Setup installer script
├── CHANGELOG.md               # Version history
└── QuickPane/
    ├── QuickPane.csproj       # Project (.NET Framework 4.8, x64, WPF)
    ├── app.manifest           # Per-monitor DPI, runs without elevation
    ├── App.xaml(.cs)          # Tray host and service startup
    ├── Interop/
    │   ├── NativeMethods.cs   # All Win32 P/Invoke
    │   ├── WinEventHook.cs    # Out-of-process window-event hooks
    │   ├── ExplorerNavigator.cs # Navigate a real Explorer window
    │   ├── DialogNavigator.cs # Detect and navigate file dialogs
    │   └── VirtualDesktop.cs  # Virtual-desktop queries and pinning
    ├── Explorer/
    │   ├── ExplorerWatcher.cs # Discovers windows, drives layout, burst enforcer
    │   ├── ExplorerWindow.cs  # One embedded (inside) pane and its content shift
    │   ├── ExplorerFollowerWatcher.cs # Beside-mode follower panes
    │   ├── DialogPaneWatcher.cs # Panes for Open/Save dialogs
    │   ├── DialogInsidePane.cs # Inside-mode pane for a dialog
    │   ├── RecentTracker.cs   # Capture real Explorer navigation
    │   └── AppBarHost.cs      # Desktop dock AppBar
    ├── Models/
    │   └── Models.cs          # Settings, profiles, and pinned-folder models
    ├── Services/
    │   ├── SettingsStore.cs   # JSON load, save, migrate
    │   ├── GroupStore.cs      # Groups, tabs, and pins on disk
    │   ├── UiState.cs         # Persistent open/closed state
    │   ├── RecentFoldersService.cs # Recent list model
    │   ├── DriveService.cs    # Drive and tree enumeration
    │   ├── KnownFolders.cs    # Known-folder resolution
    │   ├── ShellLink.cs       # Read and write .lnk shortcuts
    │   ├── FileDropOps.cs     # Shell move/copy/shortcut drops
    │   ├── ThemeService.cs    # Dark / light theming
    │   └── Log.cs
    └── UI/
        ├── SidebarControl.xaml(.cs)  # The pane shell
        ├── GroupSection.xaml(.cs)    # A group with its tabs
        ├── ComputerSection.xaml(.cs) # This PC, drives, trees
        ├── RecentsSection.xaml(.cs)  # Recent folders
        ├── ShellRootSection.cs       # Network and Linux roots
        ├── FolderItem.xaml(.cs)      # One folder row
        ├── FolderTreeNode.cs         # A lazy tree node
        ├── SettingsPanel.xaml(.cs)   # Settings
        ├── GroupPickerWindow.xaml(.cs) # Pick a group when pinning
        ├── TextPrompt.cs             # Top-level prompts for text entry
        ├── DropLineAdorner.cs        # Reorder drop line
        └── DragGhostAdorner.cs       # Drag ghost
```

## Contributing

1. Fork the repository
2. Create a feature branch (`git checkout -b feature/amazing-feature`)
3. Make your changes (keep to documented Win32 and the existing C# 7.3 / .NET Framework 4.8 target)
4. Build and test on Windows 10 or 11
5. Commit your changes (`git commit -m 'Add amazing feature'`)
6. Push to the branch (`git push origin feature/amazing-feature`)
7. Open a Pull Request

## Author

PlexPixel ([plexpixel.com](https://plexpixel.com))

## License

MIT, see the [LICENSE](LICENSE) file for details.

---

**⭐ If QuickPane made Explorer feel like home, please star the repository!**

## ☕ Buy me a coffee

If QuickPane saves you time every day, consider supporting its development.

[![Buy me a coffee](https://img.shields.io/badge/Buy%20me%20a%20coffee-FFDD00?style=for-the-badge&logo=buymeacoffee&logoColor=black)](https://plexpixel.com/donate)

**Why support?**

- ☕ Fuel continued development
- 🚀 New features and modes
- 🐛 Faster fixes and updates
- 📚 Better documentation

---

*Made with ❤️ for people who live in File Explorer.*
