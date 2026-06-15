; QuickPane one-click installer script for Inno Setup (https://jrsoftware.org/isinfo.php).
;
; This produces a single QuickPaneSetup.exe. Because QuickPane is a user-mode app that only writes
; HKCU keys, the installer runs without administrator rights (PrivilegesRequired=lowest). It installs
; the executable, registers the "Pin to Quick Pane" folder verb, adds a startup entry, and launches
; the app. Uninstall preserves the user's groups and settings under %APPDATA%\QuickPane.
;
; To build: open this file in Inno Setup, then press Compile. Build QuickPane.sln in Visual Studio
; first so QuickPane.exe exists at the path in [Files].

#define MyAppName "QuickPane"
#define MyAppVersion "3.2.15"
#define MyAppPublisher "PlexPixel"
#define MyAppExe "QuickPane.exe"

[Setup]
AppId={{A1C3E5F7-2B4D-6E8F-0A1C-3E5F7B9D1A2C}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\{#MyAppName}
DisableProgramGroupPage=yes
DisableDirPage=yes
PrivilegesRequired=lowest
OutputBaseFilename=QuickPaneSetup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64
ArchitecturesAllowed=x64
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
; Build Release|x64 first so this path exists.
Source: "QuickPane\bin\x64\Release\{#MyAppExe}"; DestDir: "{app}"; Flags: ignoreversion

[Registry]
; "Pin to Quick Pane" on any folder.
Root: HKCU; Subkey: "Software\Classes\Directory\shell\QuickPanePin"; ValueType: string; ValueName: ""; ValueData: "Pin to Quick Pane"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\Directory\shell\QuickPanePin"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\{#MyAppExe}"
Root: HKCU; Subkey: "Software\Classes\Directory\shell\QuickPanePin\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExe}"" --pin ""%1"""

; "Pin to Quick Pane" on the folder background (empty area inside a folder).
Root: HKCU; Subkey: "Software\Classes\Directory\Background\shell\QuickPanePin"; ValueType: string; ValueName: ""; ValueData: "Pin to Quick Pane"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\Directory\Background\shell\QuickPanePin\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExe}"" --pin ""%V"""

; Start with Windows.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "QuickPane"; ValueData: """{app}\{#MyAppExe}"""; Flags: uninsdeletevalue

[Dirs]
Name: "{userappdata}\QuickPane\Groups"

[Run]
Filename: "{app}\{#MyAppExe}"; Description: "Launch QuickPane"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Stop the app before files are removed so Explorer windows are restored cleanly.
Filename: "{cmd}"; Parameters: "/c taskkill /im {#MyAppExe} /f"; Flags: runhidden; RunOnceId: "StopQuickPane"
