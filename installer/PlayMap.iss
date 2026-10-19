; PlayMap's installer (docs/round3/design/installer.md; docs/INSTALL.md). Inno Setup 7 builds it: the WiX toolset does not
; build from its pinned source on the runner, and that source carries a licence check PlayMap never answers, so this EXE takes the MSI's place with the same acceptance
; (docs/round3/PLAN.md §3.5 item 2, §9 decision 3). It installs per machine into Program Files\PlayMap and does every machine-level
; action through the supervisor's own commands (install, uninstall, preinstall-check), so the engine never runs as administrator.
;
; The build passes, from a numbered build only (installer/build-installer.ps1):
;   SemVer       the product version, MAJOR.MINOR.PATCH            0.2.0
;   Build        CI's build number, 1-65535                         17
;   Commit       the commit built, seven hex digits                 abc1234
;   MsiVersion   the installer's version, MAJOR.MINOR.BUILD          0.2.17 (orders installs: §1.3)
;   FileVersion  Windows' four parts, MAJOR.MINOR.PATCH.BUILD        0.2.0.17
;   Payload      the folder of files to lay down (the exe's publish, ffmpeg\, D3D12\, Supervisor\)
;   OutputDir    where the installer is written
;
; Two test words are read from the command line and nowhere else: /PLAYMAPTESTBUILD=<n> stands in for Windows' build number and
; /PLAYMAPTESTSAC=<0|1|2> for Smart App Control's state, so the runner proves the launch condition and the warning without changing
; the machine.

#ifndef MsiVersion
  #error An installer is made from a numbered build only (PlayMapRun and PlayMapCommit from CI): a local build's would read as MAJOR.MINOR.0 and could not be upgraded in order.
#endif

[Setup]
AppId={{6F1E4C1A-8B53-4E0F-9C55-2D9A7C1B3E84}
AppName=PlayMap
AppVersion={#MsiVersion}
AppVerName=PlayMap {#SemVer} (build {#Build})
AppPublisher=jammin808
AppCopyright=© 2026 jammin808
VersionInfoVersion={#FileVersion}
VersionInfoProductVersion={#FileVersion}
VersionInfoProductTextVersion={#SemVer}+{#Build}.{#Commit}
VersionInfoDescription=PlayMap {#SemVer} (build {#Build}) installer
DefaultDirName={autopf}\PlayMap
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=admin
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
OutputDir={#OutputDir}
OutputBaseFilename=PlayMap-{#FileVersion}-win-x64-unsigned
Compression=lzma2/max
SolidCompression=yes
SetupLogging=yes
CloseApplications=no
UninstallDisplayName=PlayMap {#SemVer} (build {#Build})
UninstallDisplayIcon={app}\PlayMap.exe

[Files]
; The supervisor once more, unlaid, for the armed check before anything is changed.
Source: "{#Payload}\Supervisor\PlayMap.Supervisor.exe"; Flags: dontcopy
Source: "{#Payload}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Dirs]
Name: "{commonappdata}\PlayMap"; Flags: uninsneveruninstall
Name: "{commonappdata}\PlayMap\dumps"; Flags: uninsneveruninstall
Name: "{commonappdata}\PlayMap\supervisor"; Flags: uninsneveruninstall

[Registry]
Root: HKLM; Subkey: "SOFTWARE\PlayMap"; Flags: uninsdeletekey
Root: HKLM; Subkey: "SOFTWARE\PlayMap"; ValueType: string; ValueName: "InstallDir"; ValueData: "{app}"
Root: HKLM; Subkey: "SOFTWARE\PlayMap"; ValueType: string; ValueName: "Version"; ValueData: "{#MsiVersion}"
Root: HKLM; Subkey: "SOFTWARE\PlayMap"; ValueType: string; ValueName: "SemVer"; ValueData: "{#SemVer}+{#Build}.{#Commit}"
Root: HKLM; Subkey: "SOFTWARE\PlayMap"; ValueType: string; ValueName: "ShowUser"; ValueData: "{code:ShowUser}"
Root: HKLM; Subkey: "SOFTWARE\PlayMap"; ValueType: dword; ValueName: "Supervised"; ValueData: "1"
Root: HKLM; Subkey: "SOFTWARE\PlayMap"; ValueType: dword; ValueName: "Signed"; ValueData: "0"

[Icons]
Name: "{autoprograms}\PlayMap"; Filename: "{app}\PlayMap.exe"

[Code]
const
  MinBuild = 26100;
  SacEvaluating = 2;
  SacWords = 'Smart App Control is evaluating this PC: once it enforces, Windows blocks this unsigned build and the node will not start. Turn it off in Windows Security > App & browser control before the show.';

var
  MachineSetupFailed: Boolean;
  SacPage: TOutputMsgWizardPage;

function ShowUser(Param: String): String;
begin
  Result := ExpandConstant('{param:SHOWUSER|}');
  if Result = '' then
    RegQueryStringValue(HKLM, 'SOFTWARE\PlayMap', 'ShowUser', Result);
end;

function Refuse(const Words: String): Boolean;
begin
  Log('refused: ' + Words);
  SuppressibleMsgBox(Words, mbCriticalError, MB_OK, IDOK);
  Result := False;
end;

{ Windows' build number, or the test word that stands in for it. }
function WindowsBuild(): Integer;
var
  Text: String;
begin
  Text := ExpandConstant('{param:PLAYMAPTESTBUILD|}');
  if Text = '' then
    if not RegQueryStringValue(HKLM, 'SOFTWARE\Microsoft\Windows NT\CurrentVersion', 'CurrentBuildNumber', Text) then
      Text := '0';
  Result := StrToIntDef(Text, 0);
end;

{ Smart App Control's state (0 off, 1 on, 2 evaluating), or the test word that stands in for it. }
function SacState(): Integer;
var
  Value: Cardinal;
  Text: String;
begin
  Text := ExpandConstant('{param:PLAYMAPTESTSAC|}');
  if Text <> '' then
    Result := StrToIntDef(Text, 0)
  else if RegQueryDWordValue(HKLM, 'SYSTEM\CurrentControlSet\Control\CI\Policy', 'VerifiedAndReputablePolicyState', Value) then
    Result := Value
  else
    Result := 0;
end;

{ -1, 0 or 1 as A is lower than, equal to or greater than B (both MAJOR.MINOR.BUILD). }
function CompareVersions(const A, B: String): Integer;
var
  PA, PB: Int64;
begin
  if not StrToVersion(A, PA) then PA := 0;
  if not StrToVersion(B, PB) then PB := 0;
  Result := ComparePackedVersion(PA, PB);
  if Result < 0 then Result := -1 else if Result > 0 then Result := 1;
end;

{ The supervisor's preinstall-check, run from where it is: fails closed, its words kept for the refusal. }
function ArmedCheck(const Supervisor: String): Boolean;
var
  Code: Integer;
  Words: AnsiString;
  Said: String;
begin
  Said := ExpandConstant('{tmp}\preinstall-check.txt');
  if not Exec(Supervisor, 'preinstall-check --out "' + Said + '"', '', SW_HIDE, ewWaitUntilTerminated, Code) then
  begin
    Result := Refuse('The armed check could not run (' + SysErrorMessage(Code) + '): nothing was changed.');
    Exit;
  end;
  if not LoadStringFromFile(Said, Words) then Words := '';
  Log('preinstall-check answered ' + IntToStr(Code) + ': ' + Trim(String(Words)));
  if Code = 0 then
    Result := True
  else if Trim(String(Words)) <> '' then
    Result := Refuse(Trim(String(Words)))
  else
    Result := Refuse('The armed check refused the install and said nothing (exit ' + IntToStr(Code) + '): nothing was changed.');
end;

function InitializeSetup(): Boolean;
var
  Installed: String;
  Build: Integer;
begin
  Build := WindowsBuild();
  if Build < MinBuild then
  begin
    Result := Refuse('PlayMap needs Windows 11 24H2 (build 26100) or later; this machine runs build ' + IntToStr(Build) + '.');
    Exit;
  end;
  if RegQueryStringValue(HKLM, 'SOFTWARE\PlayMap', 'Version', Installed) and (Installed <> '') then
  begin
    if CompareVersions(Installed, '{#MsiVersion}') > 0 then
    begin
      Result := Refuse('PlayMap ' + Installed + ' is newer than this installer''s {#MsiVersion}: uninstall it first to go back.');
      Exit;
    end;
    if CompareVersions(Installed, '{#MsiVersion}') = 0 then
    begin
      Result := Refuse('PlayMap {#MsiVersion} is installed already: an installer upgrades only an older build.');
      Exit;
    end;
  end;
  ExtractTemporaryFile('PlayMap.Supervisor.exe');
  Result := ArmedCheck(ExpandConstant('{tmp}\PlayMap.Supervisor.exe'));
  if Result and (SacState() = SacEvaluating) then
    Log('warning: ' + SacWords);
end;

procedure InitializeWizard();
begin
  if SacState() = SacEvaluating then
    SacPage := CreateOutputMsgPage(wpWelcome, 'Smart App Control', 'This build is unsigned.', SacWords);
end;

{ Before files are replaced: an earlier install's supervisor stops its service and its engine and takes back its machine-level
  actions; this install's supervisor does them again after the files are in place. }
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Dir, Old: String;
  Code: Integer;
begin
  Result := '';
  if RegQueryStringValue(HKLM, 'SOFTWARE\PlayMap', 'InstallDir', Dir) then
  begin
    Old := AddBackslash(Dir) + 'Supervisor\PlayMap.Supervisor.exe';
    if FileExists(Old) then
    begin
      Exec(Old, 'uninstall --show-user "' + ShowUser('') + '" --out "' + ExpandConstant('{tmp}\uninstall-old.txt') + '"', '', SW_HIDE,
        ewWaitUntilTerminated, Code);
      Log('the earlier install''s supervisor answered ' + IntToStr(Code));
    end;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Code: Integer;
  Words: AnsiString;
  Params: String;
begin
  if CurStep <> ssPostInstall then Exit;
  Params := 'install --dir "' + ExpandConstant('{app}') + '" --out "' + ExpandConstant('{tmp}\install.txt') + '"';
  if ShowUser('') <> '' then Params := Params + ' --show-user "' + ShowUser('') + '"';
  if ExpandConstant('{param:PUBLICFIREWALL|0}') = '1' then Params := Params + ' --public';
  if not Exec(ExpandConstant('{app}\Supervisor\PlayMap.Supervisor.exe'), Params, '', SW_HIDE, ewWaitUntilTerminated, Code) then
    Code := -1;
  if not LoadStringFromFile(ExpandConstant('{tmp}\install.txt'), Words) then Words := '';
  Log('the supervisor''s install answered ' + IntToStr(Code) + ':' + #13#10 + String(Words));
  if Code <> 0 then
  begin
    MachineSetupFailed := True;
    SuppressibleMsgBox('The files are in place but the machine-level setup did not finish: ' + String(Words), mbError, MB_OK, IDOK);
  end;
end;

function GetCustomSetupExitCode(): Integer;
begin
  if MachineSetupFailed then Result := 3 else Result := 0;
end;

{ The show user the earlier install recorded: the install's key alone, never a new /SHOWUSER, so an uninstall takes the right back
  from the account it was given to. }
function RecordedShowUser(): String;
begin
  if not RegQueryStringValue(HKLM, 'SOFTWARE\PlayMap', 'ShowUser', Result) then
    Result := '';
end;

function InitializeUninstall(): Boolean;
begin
  Result := ArmedCheck(ExpandConstant('{app}\Supervisor\PlayMap.Supervisor.exe'));
end;

{ After the person's confirmation and before any file is removed: the supervisor's uninstall runs the armed check again (an engine
  armed while the confirmation was open refuses it here), stops the service and the engine and takes back every machine-level action.
  Any refusal, or an action it could not take back, stops the uninstall with every file still in place, so no machine state is left
  pointing at files that are gone. }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Code: Integer;
  Words: AnsiString;
  Said: String;
begin
  if CurUninstallStep <> usUninstall then Exit;
  Said := ExpandConstant('{tmp}\uninstall.txt');
  if not Exec(ExpandConstant('{app}\Supervisor\PlayMap.Supervisor.exe'), 'uninstall --show-user "' + RecordedShowUser() + '" --out "' + Said + '"',
    '', SW_HIDE, ewWaitUntilTerminated, Code) then
  begin
    Refuse('The machine-level uninstall could not run (' + SysErrorMessage(Code) + '): nothing was removed.');
    Abort;
  end;
  if not LoadStringFromFile(Said, Words) then Words := '';
  Log('the supervisor''s uninstall answered ' + IntToStr(Code) + ':' + #13#10 + String(Words));
  if Code <> 0 then
  begin
    if Trim(String(Words)) <> '' then
      Refuse('The uninstall stopped before any file was removed: ' + Trim(String(Words)))
    else
      Refuse('The uninstall stopped before any file was removed: the supervisor''s uninstall answered ' + IntToStr(Code) + ' and said nothing.');
    Abort;
  end;
end;
