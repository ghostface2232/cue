; Inno Setup script — clean, per-user installer for Cue (modern wizard, Korean).
;
; build-installer.ps1 passes the real values via /D defines; the fallbacks below let you also
; compile this by hand in the Inno Setup IDE after a publish. The published folder is fully
; self-contained (.NET + Windows App SDK are bundled), and build-installer.ps1 additionally drops
; the Visual C++ runtime DLLs into it app-local, so this installer needs no admin rights and pulls
; in no separate runtime on the target machine.

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\bin\Release\net10.0-windows10.0.26100.0\win-x64\publish"
#endif

#define AppName "Cue"
#define AppPublisher "Cue"
#define AppExeName "Cue.exe"

[Setup]
; A stable, unique AppId — keep this constant across versions so upgrades replace in place.
AppId={{6F3C2A18-9B4E-4D7A-AE55-2C1D0F8B7E90}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
VersionInfoVersion={#AppVersion}
WizardStyle=modern
; Per-user install: no admin, no UAC. {autopf} resolves to the user's local app data under
; "lowest" privileges, and the Start-menu / desktop shortcuts go to the per-user profiles.
PrivilegesRequired=lowest
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExeName}
SetupIconFile=..\Assets\AppIcon.ico
OutputDir=..\dist
OutputBaseFilename=CueSetup-win-x64
Compression=lzma2/max
SolidCompression=yes
; 64-bit only for now (the publish RID is win-x64). x64compatible needs Inno Setup 6.3+.
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Offer to close a running Cue on upgrade so its files can be replaced, and don't relaunch it
; ourselves — the post-install "실행" checkbox handles launching.
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; The whole self-contained publish folder (app + .NET + Windows App SDK + app-local VC++ runtime).
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent
