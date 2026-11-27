; AiDevHelpers installer (Inno Setup 6). Built by scripts/build-installer.ps1 from artifacts/package.
; The setup copies the package to a temp folder and runs the pinned service-install.ps1 from there, which installs to
; %ProgramFiles%\AiDevHelpers and starts the Host service. The tray starts at logon from the per-user Run value.

#ifndef AppVersion
#define AppVersion "0.1.0-dev"
#endif

[Setup]
AppId={{8F3A2C1E-5B7D-4E9A-B6C2-1D0E4F7A9B31}
AppName=AiDevHelpers
AppVersion={#AppVersion}
AppPublisher=AiDevHelpers
PrivilegesRequired=admin
ArchitecturesInstallIn64BitMode=x64compatible
DefaultDirName={commonpf}\AiDevHelpers
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=yes
; The uninstaller lives in ProgramData, so the service uninstall (which removes the install folder) does not remove it too early.
UninstallFilesDir={commonappdata}\AiDevHelpers\uninstall
OutputDir=..\artifacts\installer
OutputBaseFilename=AiDevHelpers-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes

[Files]
Source: "..\artifacts\package\*"; DestDir: "{tmp}\pkg"; Flags: recursesubdirs ignoreversion deleteafterinstall

[Run]
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{tmp}\pkg\service-install.ps1"" -SourceFolder ""{tmp}\pkg"""; StatusMsg: "Installing the AiDevHelpers service..."; Flags: runhidden waituntilterminated

[UninstallRun]
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\service-uninstall.ps1"""; RunOnceId: "ServiceUninstall"; Flags: runhidden waituntilterminated

[Registry]
; The tray starts at logon for this user. The value is removed on uninstall.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "AiDevHelpers"; ValueData: """{commonpf}\AiDevHelpers\AiDevHelpers.Tray.exe"""; Flags: uninsdeletevalue
