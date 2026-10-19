; PlayMap's installer (docs/round3/design/installer.md; docs/INSTALL.md), an EXE built by Inno Setup 7.1 in the MSI's place with the
; same acceptance (docs/round3/PLAN.md §3.5 item 2, §9 decision 3; why, in docs/INSTALL.md). It installs per machine into
; Program Files\PlayMap and does every machine-level action through the supervisor's own commands (install, uninstall, upgrade-stop,
; preinstall-check), so the engine never runs as administrator.
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
; Its licence page shows the payload's licence.txt (installer/licence.txt: PlayMap's terms, none named yet, and Microsoft's terms for
; D3D12Core.dll, verbatim from the Agility SDK package), which a person accepts before anything is laid down; a quiet install
; (/VERYSILENT) accepts it on its command line.
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
; Program Files\PlayMap only: the LocalSystem service runs from here, and a folder an ordinary account may change would let it replace
; what SYSTEM runs. The page is never shown and PrepareToInstall refuses any other folder, /DIR= included.
DefaultDirName={autopf}\PlayMap
DisableProgramGroupPage=yes
DisableDirPage=yes
UsePreviousAppDir=no
PrivilegesRequired=admin
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
OutputDir={#OutputDir}
OutputBaseFilename=PlayMap-{#FileVersion}-win-x64-unsigned
Compression=lzma2/max
SolidCompression=yes
SetupLogging=yes
LicenseFile={#Payload}\licence.txt
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
Root: HKLM; Subkey: "SOFTWARE\PlayMap"; ValueType: dword; ValueName: "Signed"; ValueData: "0"
; Supervised is the supervisor's install's to write, once its service runs, and its uninstall's to take back first: a machine whose
; install or upgrade did not finish is not supervised, so its desk starts its own engine.

[Icons]
Name: "{autoprograms}\PlayMap"; Filename: "{app}\PlayMap.exe"

[Code]
const
  MinBuild = 26100;
  TermsHeading = 'PlayMap: the terms for the software this installer lays down';
  SacEvaluating = 2;
  SacWords = 'Smart App Control is evaluating this PC: once it enforces, Windows blocks this unsigned build and the node will not start. Turn it off in Windows Security > App & browser control before the show.';

var
  MachineSetupFailed: Boolean;
  SacPage: TOutputMsgWizardPage;
  EarlierStopped: Boolean;
  ReachedPostInstall: Boolean;
  PreviousShowUser: String;

function ShowUser(Param: String): String;
begin
  Result := ExpandConstant('{param:SHOWUSER|}');
  if Result = '' then
    RegQueryStringValue(HKLM, 'SOFTWARE\PlayMap', 'ShowUser', Result);
end;

{ The show user the earlier install recorded: the install's key alone, never a new /SHOWUSER, so an uninstall takes the right back
  from the account it was given to. }
function RecordedShowUser(): String;
begin
  if not RegQueryStringValue(HKLM, 'SOFTWARE\PlayMap', 'ShowUser', Result) then
    Result := '';
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

function YesNo(Value: Boolean): String;
begin
  if Value then Result := 'yes' else Result := 'no';
end;

{ What the licence page shows, read from the page itself into the log: the terms' heading first, Microsoft's DISTRIBUTABLE CODE
  section for D3D12Core.dll on it, and Microsoft's typographic apostrophe one character (the terms decoded as UTF-8). The page's
  window is made first, so its text is the control's own in a quiet install too; a byte-order mark the control keeps is an
  invisible character, not part of the heading. }
procedure LogLicencePage();
var
  Terms: String;
begin
  if WizardForm.LicenseMemo.Handle <> 0 then
    Terms := WizardForm.LicenseMemo.Text
  else
    Terms := '';
  if (Length(Terms) > 0) and (Terms[1] = #$FEFF) then
    Delete(Terms, 1, 1);
  Log('the licence page: heading ' + YesNo(Pos(TermsHeading, Terms) = 1)
    + '; Microsoft''s distributable-code terms ' + YesNo(Pos('DISTRIBUTABLE CODE. The software may contain code you are permitted to distribute', Terms) > 0)
    + '; read as UTF-8 ' + YesNo(Pos('Microsoft' + #$2019 + 's trademarks', Terms) > 0));
end;

procedure InitializeWizard();
begin
  LogLicencePage();
  if SacState() = SacEvaluating then
    SacPage := CreateOutputMsgPage(wpWelcome, 'Smart App Control', 'This build is unsigned.', SacWords);
end;

{ Words, then a further sentence. }
function ThenSay(const Words, More: String): String;
begin
  if (Words <> '') and (Copy(Words, Length(Words), 1) = '.') then
    Result := Words + ' ' + More
  else
    Result := Words + '. ' + More;
end;

{ The folder an install may go into: Program Files\PlayMap. }
function InstallFolder(): String;
begin
  Result := ExpandConstant('{autopf}\PlayMap');
end;

{ Runs this build's supervisor (unlaid, in the temporary folder) with Params; its exit code and its words. False when it could not run. }
function RunSupervisor(const Params: String; var Code: Integer; var Words: String): Boolean;
var
  Said: String;
  Text: AnsiString;
begin
  Said := ExpandConstant('{tmp}\supervisor-said.txt');
  DeleteFile(Said);
  Result := Exec(ExpandConstant('{tmp}\PlayMap.Supervisor.exe'), Params + ' --out "' + Said + '"', '', SW_HIDE, ewWaitUntilTerminated, Code);
  if not Result then
    Words := 'the supervisor could not run (' + SysErrorMessage(Code) + ')'
  else if LoadStringFromFile(Said, Text) then
    Words := Trim(String(Text))
  else
    Words := '';
  Log('the supervisor''s ' + Params + ' answered ' + IntToStr(Code) + ':' + #13#10 + Words);
end;

{ Just before anything is changed (Inno Setup shows these words as the Preparing page's refusal, and exits 7): the folder must be
  Program Files\PlayMap; the armed check runs again, since the wizard may have waited on its pages; and an earlier install's service
  and engine are stopped by this build's supervisor ("upgrade-stop": the armed check, nothing else running from the folder, then the
  service and the engine), its machine-level actions left for this build's "install" to set again. If this install does not reach its
  end, DeinitializeSetup starts the earlier service again. }
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Dir, Words: String;
  Code: Integer;
begin
  Result := '';
  if CompareText(RemoveBackslash(ExpandConstant('{app}')), InstallFolder()) <> 0 then
  begin
    Result := 'PlayMap installs only in ' + InstallFolder() + ', which only administrators may change, since its service runs as SYSTEM; '
      + ExpandConstant('{app}') + ' was asked: nothing was changed.';
    Log('refused: ' + Result);
    Exit;
  end;
  if RegQueryStringValue(HKLM, 'SOFTWARE\PlayMap', 'InstallDir', Dir) and (Dir <> '') then
  begin
    if CompareText(RemoveBackslash(Dir), InstallFolder()) <> 0 then
    begin
      Result := 'The earlier install is in ' + Dir + ', not ' + InstallFolder() + ': uninstall it first, then install again. Nothing was changed.';
      Log('refused: ' + Result);
      Exit;
    end;
    PreviousShowUser := RecordedShowUser();
    if not RunSupervisor('upgrade-stop', Code, Words) or (Code <> 0) then
    begin
      if Words = '' then Words := 'the earlier install''s service and engine could not be stopped (exit ' + IntToStr(Code) + ')';
      Result := ThenSay(Words, 'Nothing was replaced.');
      Log('refused: ' + Result);
      Exit;
    end;
    EarlierStopped := True;
  end
  else if not RunSupervisor('preinstall-check', Code, Words) or (Code <> 0) then
  begin
    if Words = '' then Words := 'The armed check refused the install and said nothing (exit ' + IntToStr(Code) + ').';
    Result := ThenSay(Words, 'Nothing was changed.');
    Log('refused: ' + Result);
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Code: Integer;
  Words: AnsiString;
  Params: String;
begin
  if CurStep <> ssPostInstall then Exit;
  ReachedPostInstall := True;
  Params := 'install --dir "' + ExpandConstant('{app}') + '" --out "' + ExpandConstant('{tmp}\install.txt') + '"';
  if ShowUser('') <> '' then Params := Params + ' --show-user "' + ShowUser('') + '"';
  if (PreviousShowUser <> '') and (CompareText(PreviousShowUser, ShowUser('')) <> 0) then
    Params := Params + ' --previous-show-user "' + PreviousShowUser + '"';
  if ExpandConstant('{param:PUBLICFIREWALL|0}') = '1' then Params := Params + ' --public';
  if not Exec(ExpandConstant('{app}\Supervisor\PlayMap.Supervisor.exe'), Params, '', SW_HIDE, ewWaitUntilTerminated, Code) then
    Code := -1;
  if not LoadStringFromFile(ExpandConstant('{tmp}\install.txt'), Words) then Words := '';
  Log('the supervisor''s install answered ' + IntToStr(Code) + ':' + #13#10 + String(Words));
  if Code <> 0 then
  begin
    MachineSetupFailed := True;
    Log('the files are in place but the machine-level setup did not finish (exit 3): the machine is not supervised');
    SuppressibleMsgBox('The files are in place but the machine-level setup did not finish: ' + String(Words), mbError, MB_OK, IDOK);
  end;
end;

function GetCustomSetupExitCode(): Integer;
begin
  if MachineSetupFailed then Result := 3 else Result := 0;
end;

{ An upgrade that stopped the earlier service and did not reach its end (refused, cancelled, a file it could not replace — Inno Setup
  rolls its files back) starts the earlier service again, which starts the earlier engine: the machine is as it was. }
procedure DeinitializeSetup();
var
  Code: Integer;
begin
  if EarlierStopped and not ReachedPostInstall then
  begin
    Exec(ExpandConstant('{sys}\sc.exe'), 'start PlayMapSupervisor', '', SW_HIDE, ewWaitUntilTerminated, Code);
    Log('the install did not finish: the earlier install''s service is started again (sc start answered ' + IntToStr(Code) + ')');
  end;
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
