; LanSync installer skeleton (Inno Setup 6)
; ADR-001 (docs/ADR-001-托盘技术形态.md) §10: single-file setup.
; Supported switches: /SILENT /DIR=<install dir> (built-in), /SYNCDIR=<sync dir> (custom, parsed in [Code]).
; Engine service installation is delegated to deploy\install.ps1 (service name LanSyncEngine,
; account LanSyncSvc, shawl wrapper). Uninstall never removes the user's sync directory.
;
; Compile with: iscc packaging\setup.iss
; Requires: Inno Setup 6.2+ (for the {param:...} constant in [Code]).

#define MyAppName "LanSync"
#define MyAppVersion "0.1.0"
#define MyAppPublisher "LanSync"
#define MyAppExeName "LanSync.Tray.exe"

[Setup]
AppId={{F1D5B3C0-1CC9-4E6D-9C43-4E64C5D9E7A6}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\LanSync
DefaultGroupName=LanSync
DisableProgramGroupPage=yes
OutputDir=output
OutputBaseFilename=LanSync-Setup-{#MyAppVersion}
Compression=lzma2
SolidCompression=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes
WizardStyle=modern
; The sync directory is chosen via /SYNCDIR= (or defaults in install.ps1); it is NOT
; inside {app} and must never be treated as an installed file, so it survives uninstall.

[Languages]
Name: "chinesesimp"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Files]
; Binaries are staged into packaging\bin by the release pipeline before compiling this script.
Source: "bin\syncthing.exe"; DestDir: "{app}\bin"; Flags: ignoreversion
Source: "bin\shawl.exe"; DestDir: "{app}\bin"; Flags: ignoreversion
Source: "..\src\LanSync.Tray\bin\Release\net10.0-windows10.0.17763.0\LanSync.Tray.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\deploy\install.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\deploy\uninstall.ps1"; DestDir: "{app}"; Flags: ignoreversion

[Run]
Filename: "powershell.exe"; \
    Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\install.ps1"" -SyncDir ""{code:GetSyncDir}"""; \
    StatusMsg: "正在配置 LanSyncEngine 服务…"; \
    Flags: runhidden waituntilterminated

[UninstallRun]
Filename: "powershell.exe"; \
    Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\uninstall.ps1"""; \
    Flags: runhidden

[Code]
function GetSyncDir(): string;
begin
  Result := ExpandConstant('{param:SYNCDIR|}');
  if Result = '' then
    Result := ExpandConstant('{userdocs}\LanSync');
end;