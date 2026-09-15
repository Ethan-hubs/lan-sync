; LanSync installer skeleton (Inno Setup 6.2+)
; ADR-001 (docs/ADR-001-托盘技术形态.md) §10 + §14(5)(6)(7).
; Supported switches: /SILENT /DIR=<install dir> (built-in), /SYNCDIR=<sync dir>,
; and /PURGE on the uninstaller (deletes engine identity cert.pem/key.pem).
;
; §14(7) install source is a publish artifact: run packaging\publish.ps1 first, which
; produces packaging\bin\tray (dotnet publish -c Release -r win-x64) and expects
; packaging\bin\syncthing.exe + packaging\bin\shawl.exe to be staged by the release pipeline.
;
; §14(5) LongPathsEnabled=0 is detected in InitializeWizard; a checkbox page (default on)
; writes HKLM\...\FileSystem\LongPathsEnabled=1 and logs to install.log. Never silently
; changes the machine-level registry value.
;
; §14(4) autostart is NOT written here (elevated HKCU would hit the admin hive); the tray
; writes/verifies its own HKCU Run entry on first launch. Uninstall cleans it via
; runasoriginaluser (see [UninstallRun]).
;
; Compile with: iscc packaging\setup.iss

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
; Tray publish artifact (dotnet publish -c Release -r win-x64 -> packaging\bin\tray).
Source: "bin\tray\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs
; Engine binaries (staged into packaging\bin by the release pipeline).
Source: "bin\syncthing.exe"; DestDir: "{app}\bin"; Flags: ignoreversion
Source: "bin\shawl.exe"; DestDir: "{app}\bin"; Flags: ignoreversion
; Deploy scripts.
Source: "..\deploy\install.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\deploy\uninstall.ps1"; DestDir: "{app}"; Flags: ignoreversion

[Run]
Filename: "powershell.exe"; \
    Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\install.ps1"" -SyncDir ""{code:GetSyncDir}"""; \
    StatusMsg: "正在配置 LanSyncEngine 服务…"; \
    Flags: runhidden waituntilterminated

[UninstallRun]
Filename: "powershell.exe"; \
    Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\uninstall.ps1"" {code:GetPurgeSwitch}"; \
    Flags: runhidden
; Clean the current user's HKCU Run autostart entry (runasoriginaluser: hits the real
; logged-on user's hive, not the elevated one). The tray entry is written by the tray itself.
Filename: "powershell.exe"; \
    Parameters: "-NoProfile -Command Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'LanSync' -ErrorAction SilentlyContinue"; \
    Flags: runhidden runasoriginaluser

[Code]
var
  PurgeRequested: Boolean;
  LongPathPage: TInputOptionWizardPage;

function LongPathsEnabled(): Boolean;
var
  Value: Cardinal;
begin
  Result := RegQueryDWordValue(HKLM, 'SYSTEM\CurrentControlSet\Control\FileSystem', 'LongPathsEnabled', Value) and (Value = 1);
end;

function GetSyncDir(Param: string): string;
begin
  Result := ExpandConstant('{param:SYNCDIR|}');
  if Result = '' then
    Result := ExpandConstant('{userdocs}\LanSync');
end;

function GetPurgeSwitch(Param: string): string;
begin
  if PurgeRequested then
    Result := '-Purge'
  else
    Result := '';
end;

function InitializeUninstall(): Boolean;
begin
  PurgeRequested := Pos('/PURGE', UpperCase(GetCommandLineTail())) > 0;
  Result := True;
end;

procedure InitializeWizard();
begin
  if not LongPathsEnabled() then
  begin
    LongPathPage := CreateInputOptionPage(
      wpSelectTasks,
      '长路径支持',
      '启用 Windows 长路径（推荐）',
      '检测到系统 LongPathsEnabled=0（关闭）。启用后引擎才能正常同步超过 260 字符的路径。' +
      '该设置将写入 HKLM\SYSTEM\CurrentControlSet\Control\FileSystem\LongPathsEnabled，并记录到 install.log。',
      False, False);
    LongPathPage.Add('将 LongPathsEnabled 设为 1');
    LongPathPage.Values[0] := True;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  LogFile: string;
begin
  if CurStep = ssPostInstall then
  begin
    LogFile := ExpandConstant('{commonappdata}\LanSync\logs\install.log');
    if LongPathPage <> nil then
    begin
      if LongPathPage.Values[0] then
      begin
        RegWriteDWordValue(HKLM, 'SYSTEM\CurrentControlSet\Control\FileSystem', 'LongPathsEnabled', 1);
        SaveStringToFile(LogFile, '[longpath] LongPathsEnabled 已设为 1' + #13#10, True);
      end
      else
        SaveStringToFile(LogFile, '[longpath] 用户未启用 LongPathsEnabled' + #13#10, True);
    end;
  end;
end;