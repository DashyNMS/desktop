; Inno Setup script for DashyNMS.
;
; Installs per-user (no admin/UAC prompt needed), matching how the app itself
; already works: settings in %APPDATA%, DPAPI keyed to the current Windows
; account, a HKCU Run key for "start with Windows". Installing to Program
; Files would need elevation for no real benefit here.
;
; Build with: ISCC.exe installer\DashyNMS.iss
; (run "dotnet publish ... -o publish" first so publish\DashyNMS.exe exists)

#define AppName "DashyNMS"
#define AppPublisher "DashyNMS"
#define AppExeName "DashyNMS.exe"
#define AppSourceDir "..\publish"
#define AppIcon "..\src\DesktopNMS\Assets\app.ico"

; Falls back to 0.2.0.0 if not supplied via /DAppVersion=x.y.z on the ISCC command line.
#ifndef AppVersion
  #define AppVersion "0.2.0.0"
#endif

[Setup]
; A fresh GUID: this is a new product identity (renamed from DesktopNMS, a
; different exe name and default install path), so it must not be treated as
; an in-place upgrade of the old AppId - that old install is removed
; separately before this one goes on.
AppId={{9B3D2C7A-4E1F-4A6B-8C5D-2F7A9E1B3C64}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName}
AppPublisher={#AppPublisher}
DefaultDirName={localappdata}\Programs\{#AppName}
; No [Icons] entry uses {group}, so there is nothing to name a Start Menu
; folder after; this keeps a single loose Start Menu shortcut rather than a
; folder containing one item.
DisableProgramGroupPage=yes
; Per-user install: never asks for admin, installs only for the current account.
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\dist
OutputBaseFilename=DashyNMS-Setup-{#AppVersion}
SetupIconFile={#AppIcon}
UninstallDisplayIcon={app}\{#AppExeName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; Detects DashyNMS.exe running (via the file lock) and offers to close it,
; using the Windows Restart Manager - no need to know the app's mutex name.
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "startmenuicon"; Description: "Create a &Start Menu shortcut"; GroupDescription: "Shortcuts:"; Flags: checkedonce
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
Source: "{#AppSourceDir}\{#AppExeName}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
; A single loose Start Menu shortcut at the same path DashyNMS.exe would
; otherwise create for itself on first run (see ToastIdentity.cs, which needs
; a Start Menu shortcut for Windows to recognise its toast notifications).
; Because Inno creates it here pointing at the same install path, the app's
; own self-healing check finds it already correct and never touches it - one
; shortcut serves both the launcher and the notification identity.
;
; Note: unticking this does not permanently remove the Start Menu entry - the
; app recreates a minimal one for itself the first time it sends a Windows
; notification, since Windows requires one to show the app in Settings >
; Notifications. Unticking only skips creating it during setup.
Name: "{userprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: startmenuicon
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent
