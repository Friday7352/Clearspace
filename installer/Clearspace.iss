; Clearspace | Windows installer definition.
;
; CHANGED (installer overhaul): one setup file that installs, updates, repairs and removes Clearspace.
;  - Update: when Clearspace is already installed, Setup opens on a page offering Update (or Repair),
;    Update and choose the options again, or Remove. Updating keeps settings, tags, the saved index and
;    locked files, and closes a running Clearspace first (it locks again whatever was unlocked for a visit).
;  - Look: Clearspace's dark theme (same background color and logo). Needs Inno Setup 6.7 or newer for the
;    exact background, 6.6 for the dark style; older compilers still build, with the plain light look.
;  - Dependencies: nothing is downloaded or installed separately. Clearspace.exe and the index helper are
;    self-contained (.NET, WPF and SQLite are inside them); everything else they use is part of Windows 10/11.
;    The checks below stop the build if the payload is not the self-contained one.
;  - In-app updates: Clearspace downloads this same setup file from its GitHub release and runs it with
;    "/SILENT /relaunch=1": only the progress window shows, and Clearspace starts again when it is done.
;  - Uninstall: asks whether to remove the locks first when anything is still locked, and whether to remove
;    settings, tags, the saved index and logs too. See the [Code] section.

#define AppName "Clearspace"
#define AppPublisher "Clearspace"
#define VersionFile AddBackslash(SourcePath) + "..\VERSION"
#define VersionHandle FileOpen(VersionFile)
#define AppVersion Trim(FileRead(VersionHandle))
#expr FileClose(VersionHandle)

#if AppVersion == ""
  #error VERSION at the repository root is empty.
#endif

; NEW (dependencies): the payload must be the self-contained publish made by Build Installer.cmd. A build that
; needs a separately installed .NET is only a few megabytes, so size tells them apart.
#define Payload AddBackslash(SourcePath) + "..\installer-build\app\"
#if !FileExists(Payload + "Clearspace.exe")
  #error installer-build\app\Clearspace.exe is missing. Run Build Installer.cmd, which publishes it first.
#endif
#if FileSize(Payload + "Clearspace.exe") < 50000000
  #error installer-build\app\Clearspace.exe does not include .NET (it is too small). Run Build Installer.cmd.
#endif
#if !FileExists(Payload + "ClearspaceIndexHelper.exe")
  #error installer-build\app\ClearspaceIndexHelper.exe is missing. Run Build Installer.cmd.
#endif
#if FileSize(Payload + "ClearspaceIndexHelper.exe") < 5000000
  #error installer-build\app\ClearspaceIndexHelper.exe does not include .NET (it is too small). Run Build Installer.cmd.
#endif

[Setup]
AppId={{E3E7C9F3-AB35-4A5A-9725-C76908ADDC74}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={localappdata}\Programs\Clearspace
DefaultGroupName=Clearspace
DisableProgramGroupPage=yes
OutputDir=..\release
OutputBaseFilename=ClearspaceSetup
SetupIconFile=..\Clearspace\Assets\Clearspace.ico
UninstallDisplayIcon={app}\Clearspace.exe
UninstallDisplayName={#AppName}
VersionInfoVersion={#AppVersion}
VersionInfoCompany={#AppPublisher}
VersionInfoProductName=Clearspace Setup
Compression=lzma2/max
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
; Setup closes Clearspace itself before copying files (PrepareToInstall in [Code]); this is the fallback for
; anything else that still has one of the files open.
CloseApplications=yes
RestartApplications=no
; NEW (logo): tells Windows to reload icons when Setup finishes, so shortcuts, the Start menu and pinned
; taskbar buttons show a changed Clearspace icon right away instead of the cached old one.
ChangesAssociations=yes
MinVersion=10.0

; NEW (dark theme): Clearspace's Dark theme - Base #1A1917 behind every page, the logo in place of Inno Setup's
; pictures. WizardLarge.png / WizardSmall.png are the app icon on a transparent background; make-icons.py
; in this folder draws them, and Clearspace.ico, from the logo.
#if Ver >= EncodeVer(6,7,0)
WizardStyle=modern dark hidebevels
WizardBackColor=#1A1917
WizardImageFile=WizardLarge.png
WizardSmallImageFile=WizardSmall.png
; The pictures are transparent; what shows through is filled with the same color as the pages.
WizardImageBackColor=#1A1917
WizardSmallImageBackColor=#1A1917
#elif Ver >= EncodeVer(6,6,0)
  #pragma warning "Inno Setup 6.7 or newer gives the installer Clearspace's exact dark background; this version uses Inno Setup's own dark gray."
WizardStyle=modern dark
WizardImageFile=WizardLarge.png
WizardSmallImageFile=WizardSmall.png
#else
  #pragma warning "This Inno Setup is older than 6.6 and has no dark style: the installer will be light. Update Inno Setup to get the Clearspace look."
WizardStyle=modern
#endif

[Messages]
; CHANGED: shorter than the default, and reads right after the questions about locks and data.
ConfirmUninstall=Remove %1 from this PC?

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: checkedonce
; NEW (journal catch-up): on by default - instant first index and change-journal catch-up. Windows asks for
; administrator permission once, while installing; Clearspace itself always runs as a normal user.
Name: "fastcatchup"; Description: "Instant &indexing (installs the Clearspace Index Helper service; Windows asks for permission once)"; GroupDescription: "Indexing:"

[Files]
Source: "..\installer-build\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Registry]
; NEW (Explorer integration): Clearspace registers these itself (per user) every time it starts; removing
; Clearspace removes them again. Locked files keep working with a reinstalled Clearspace.
Root: HKCU; Subkey: "Software\Classes\.cslock"; Flags: uninsdeletekey dontcreatekey
Root: HKCU; Subkey: "Software\Classes\Clearspace.LockedFile"; Flags: uninsdeletekey dontcreatekey
Root: HKCU; Subkey: "Software\Classes\*\shell\Clearspace.Lock"; Flags: uninsdeletekey dontcreatekey
Root: HKCU; Subkey: "Software\Classes\Directory\shell\Clearspace"; Flags: uninsdeletekey dontcreatekey

[Icons]
Name: "{autoprograms}\Clearspace"; Filename: "{app}\Clearspace.exe"; WorkingDir: "{app}"; Comment: "Clearspace file explorer"
Name: "{autodesktop}\Clearspace"; Filename: "{app}\Clearspace.exe"; WorkingDir: "{app}"; Tasks: desktopicon; Comment: "Clearspace file explorer"

[Run]
; NEW (journal catch-up): copies the helper to Program Files and registers the service.
; CHANGED (update): also runs when the service is already there (turned on earlier in Setup or from the
; Indexing page), so an update always replaces the service's copy of the helper with the new one.
Filename: "{app}\ClearspaceIndexHelper.exe"; Parameters: "--install --quiet"; Verb: "runas"; StatusMsg: "Turning on instant indexing..."; Check: WantsIndexHelper; Flags: shellexec waituntilterminated runhidden
; NEW (Explorer integration): set up the .cslock lock icon and right-click entries right away.
Filename: "{app}\Clearspace.exe"; Parameters: "--register-shell"; Flags: runhidden waituntilterminated
Filename: "{app}\Clearspace.exe"; Description: "Launch Clearspace"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent
; NEW (in-app updates): a silent update started by Clearspace itself (/SILENT /relaunch=1) opens Clearspace again.
Filename: "{app}\Clearspace.exe"; WorkingDir: "{app}"; Check: RelaunchRequested; Flags: nowait

[Code]
const
  UninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{E3E7C9F3-AB35-4A5A-9725-C76908ADDC74}_is1';
  HelperServiceKey = 'SYSTEM\CurrentControlSet\Services\ClearspaceIndexHelper';
  { The first Clearspace that understands "Clearspace.exe --quit" (older ones would open a window instead).
    The releases named v1.0.0 and v1.1.0 both carry the version number 1.0.0, so this is 1.2.0. }
  QuitSinceVersion = '1.2.0';

var
  { Setup }
  InstalledVersion: String;       { '' when Clearspace is not installed for this user }
  InstalledUninstaller: String;
  UpdateVerb: String;             { 'Update', or 'Repair' when the same version is installed }
  MaintenancePage: TInputOptionWizardPage;
  RemoveRequested: Boolean;
  { Uninstall }
  LocksLeft: Boolean;             { something is still locked when Clearspace goes }
  RemoveUserData: Boolean;        { the user asked for settings, tags, index and logs to go too }

{ ---- Shared ---- }

{ Runs the installed Clearspace.exe with one of its installer switches and returns its exit code
  (-1 when it is missing or could not be started). See Clearspace\Services\InstallerCommands.cs. }
function RunClearspace(const Params: String; const ShowCmd: Integer): Integer;
var
  Exe: String;
  ResultCode: Integer;
begin
  Result := -1;
  Exe := ExpandConstant('{app}\Clearspace.exe');
  if FileExists(Exe) then
    if Exec(Exe, Params, '', ShowCmd, ewWaitUntilTerminated, ResultCode) then
      Result := ResultCode;
end;

function HelperServiceInstalled: Boolean;
begin
  Result := RegKeyExists(HKLM, HelperServiceKey);
end;

{ ---- Setup: update ---- }

procedure FindInstalled;
begin
  InstalledVersion := '';
  InstalledUninstaller := '';
  RegQueryStringValue(HKCU, UninstallKey, 'DisplayVersion', InstalledVersion);
  RegQueryStringValue(HKCU, UninstallKey, 'UninstallString', InstalledUninstaller);
  InstalledUninstaller := RemoveQuotes(InstalledUninstaller);
end;

{ Above 0: this Setup is newer than what is installed. 0: the same version. Below 0: older. }
function CompareToInstalled: Integer;
var
  Mine, Theirs: Int64;
begin
  Result := 1;
  if StrToVersion('{#AppVersion}', Mine) then
    if StrToVersion(InstalledVersion, Theirs) then
      Result := ComparePackedVersion(Mine, Theirs);
end;

function InstalledCanQuit: Boolean;
var
  Need, Have: Int64;
begin
  Result := False;
  if InstalledVersion <> '' then
    if StrToVersion(QuitSinceVersion, Need) then
      if StrToVersion(InstalledVersion, Have) then
        Result := ComparePackedVersion(Have, Need) >= 0;
end;

{ [Run] check: install (or refresh) the index helper service. }
function WantsIndexHelper: Boolean;
begin
  Result := WizardIsTaskSelected('fastcatchup');
  if not Result then
    Result := HelperServiceInstalled;
end;

{ [Run] check: Clearspace started this Setup itself to update (see Services\UpdateService.cs) and wants to
  be opened again afterwards. }
function RelaunchRequested: Boolean;
begin
  Result := WizardSilent;
  if Result then
    Result := ExpandConstant('{param:relaunch|0}') = '1';
end;

procedure InitializeWizard;
var
  Order: Integer;
  First: String;
begin
  RemoveRequested := False;
  FindInstalled;
  if InstalledVersion = '' then
    Exit;

  Order := CompareToInstalled;
  UpdateVerb := 'Update';
  if Order > 0 then
    First := FmtMessage('Update Clearspace %1 to %2', [InstalledVersion, '{#AppVersion}'])
  else if Order = 0 then
  begin
    UpdateVerb := 'Repair';
    First := FmtMessage('Repair Clearspace %1 (install it again)', [InstalledVersion]);
  end
  else
    First := FmtMessage('Replace Clearspace %1 with the older version %2', [InstalledVersion, '{#AppVersion}']);

  MaintenancePage := CreateInputOptionPage(wpWelcome,
    'Clearspace is already installed',
    FmtMessage('Version %1 is on this PC.', [InstalledVersion]),
    'What would you like to do? Your settings, tags, saved index and locked files are kept unless you remove Clearspace.',
    True, False);
  MaintenancePage.Add(First);
  MaintenancePage.Add(First + ', and choose the options again (desktop shortcut, instant indexing)');
  MaintenancePage.Add('Remove Clearspace from this PC');
  MaintenancePage.SelectedValueIndex := 0;
end;

{ A plain update keeps the options chosen last time, so it goes straight to the Ready page. }
function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := False;
  if InstalledVersion <> '' then
    if PageID = wpSelectTasks then
      Result := MaintenancePage.SelectedValueIndex = 0;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  ResultCode: Integer;
begin
  Result := True;
  if InstalledVersion = '' then
    Exit;
  if CurPageID <> MaintenancePage.ID then
    Exit;
  if MaintenancePage.SelectedValueIndex <> 2 then
    Exit;

  { Remove: hand over to the uninstaller that is already on this PC and close Setup. }
  Result := False;
  if not FileExists(InstalledUninstaller) then
  begin
    MsgBox('The Clearspace uninstaller is missing. Choose the first option to install Clearspace again, then remove it from Windows Settings > Apps.', mbError, MB_OK);
    Exit;
  end;
  { A Clearspace from before 1.2.0 came with an uninstaller that asks nothing about locked files or data. }
  if not InstalledCanQuit then
    if MsgBox(FmtMessage('Clearspace %1 has an older uninstaller: it does not offer to unlock your locked files or to remove your settings and tags.' + #13#10#13#10 +
         'Remove it anyway? Choose No to go back; updating first gives you the new uninstaller.', [InstalledVersion]),
         mbConfirmation, MB_YESNO or MB_DEFBUTTON2) <> IDYES then
      Exit;
  if Exec(InstalledUninstaller, '', '', SW_SHOWNORMAL, ewNoWait, ResultCode) then
  begin
    RemoveRequested := True;
    WizardForm.Close;
  end
  else
    MsgBox('The uninstaller could not be started: ' + SysErrorMessage(ResultCode), mbError, MB_OK);
end;

{ Closing Setup to hand over to the uninstaller is not a cancelled install: no "Exit Setup?" question. }
procedure CancelButtonClick(CurPageID: Integer; var Cancel, Confirm: Boolean);
begin
  if RemoveRequested then
    Confirm := False;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if InstalledVersion = '' then
    Exit;
  if CurPageID = wpReady then
  begin
    WizardForm.PageNameLabel.Caption := 'Ready to ' + Lowercase(UpdateVerb);
    WizardForm.PageDescriptionLabel.Caption := 'Setup is ready to ' + Lowercase(UpdateVerb) + ' Clearspace on this PC.';
    WizardForm.ReadyLabel.Caption := 'Click ' + UpdateVerb + ' to continue. Clearspace closes while its files are replaced.';
    WizardForm.NextButton.Caption := '&' + UpdateVerb;
  end
  else if CurPageID = wpInstalling then
  begin
    WizardForm.PageNameLabel.Caption := 'Working';
    WizardForm.PageDescriptionLabel.Caption := 'Please wait while Setup replaces Clearspace''s files.';
  end
  else if CurPageID = wpFinished then
  begin
    WizardForm.FinishedHeadingLabel.Caption := 'Clearspace is up to date';
    WizardForm.FinishedLabel.Caption := FmtMessage('Clearspace %1 is installed. Your settings, tags and locked files were kept.', ['{#AppVersion}']);
  end;
end;

function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo, MemoComponentsInfo, MemoGroupInfo, MemoTasksInfo: String): String;
begin
  Result := '';
  if InstalledVersion <> '' then
    Result := UpdateVerb + ':' + NewLine +
      Space + FmtMessage('Clearspace %1 is replaced by Clearspace %2', [InstalledVersion, '{#AppVersion}']) + NewLine +
      Space + 'Settings, tags, saved index and locked files are kept' + NewLine + NewLine;
  if MemoDirInfo <> '' then
    Result := Result + MemoDirInfo + NewLine + NewLine;
  if MemoTasksInfo <> '' then
    Result := Result + MemoTasksInfo + NewLine + NewLine;
  Result := Result + 'Included:' + NewLine +
    Space + 'Everything Clearspace needs. Nothing else is downloaded or installed.';
end;

{ Called before Setup looks for files in use: ask the running Clearspace to close. It locks again whatever is
  unlocked for a visit, then exits; "--quit" returns once every Clearspace window is gone. }
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if InstalledCanQuit then
    RunClearspace('--quit', SW_HIDE);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  LegacyDir: String;
  ResultCode: Integer;
begin
  if CurStep = ssInstall then
  begin
    { Updating a Clearspace older than 1.2.0 (no "--quit"), or one that was started again a moment ago: a
      copy still running would keep Clearspace.exe from being replaced, so Windows closes it. }
    if InstalledVersion <> '' then
      if Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM Clearspace.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
        if ResultCode = 0 then
          Sleep(700);
    Exit;
  end;

  if CurStep <> ssPostInstall then
    Exit;

  { Earlier preview installers used a small custom uninstaller in the same
    per-user folder. Inno Setup now owns that role, so remove only the old
    helper and its obsolete registry record. User preferences are elsewhere
    and remain untouched. }
  LegacyDir := ExpandConstant('{localappdata}\Programs\Clearspace');
  if CompareText(RemoveBackslashUnlessRoot(LegacyDir), RemoveBackslashUnlessRoot(ExpandConstant('{app}'))) <> 0 then
    DelTree(LegacyDir, True, True, True);
  DeleteFile(AddBackslash(LegacyDir) + 'Uninstall Clearspace.ps1');
  RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\Clearspace');
end;

{ ---- Uninstall ---- }

{ FIXED: no line in this section may start with "[" - Inno Setup reads such a line as a section tag
  ("Invalid section tag"), even inside [Code]. Button label lists therefore open at the end of a line. }

function LockedItemsText(const Count: Integer): String;
begin
  if Count = 1 then
    Result := '1 locked file or folder'
  else
    Result := IntToStr(Count) + ' locked files and folders';
end;

{ NEW (clean uninstall): two questions before anything is removed.
   1. Only when something is still locked: remove the locks first (Clearspace's own "Remove all locks" dialog
      asks for the password), leave them locked, or cancel.
   2. Keep or remove Clearspace's data (settings, tags, saved index, logs).
  A silent uninstall asks nothing and leaves both locks and data alone. }
function InitializeUninstall(): Boolean;
var
  Locks, Answer, Outcome: Integer;
  Note, RemoveLabel: String;
begin
  Result := True;
  RemoveUserData := False;

  Locks := RunClearspace('--lock-count', SW_HIDE);
  if Locks < 0 then
    Locks := 0;   { Clearspace.exe is missing: nothing can be counted or unlocked from here }
  LocksLeft := Locks > 0;
  if UninstallSilent then
    Exit;

  if Locks > 0 then
  begin
    Answer := TaskDialogMsgBox(
      'You have ' + LockedItemsText(Locks),
      'They are encrypted with your Clearspace password. Without Clearspace they stay locked until you install it again and enter that password.',
      mbConfirmation, MB_YESNOCANCEL, [
       '&Remove the locks first'#13#10'Asks for your password, then unlocks everything for good.',
       '&Leave them locked'#13#10'Install Clearspace again later to open them.'],
      0);
    while Answer = IDYES do
    begin
      RunClearspace('--quit', SW_HIDE);   { a running Clearspace may have some of them open }
      Outcome := RunClearspace('--remove-all-locks', SW_SHOWNORMAL);
      if Outcome = 0 then
      begin
        LocksLeft := False;
        Answer := IDNO;   { done: carry on with the uninstall }
      end
      else
        Answer := TaskDialogMsgBox(
          'Some items are still locked',
          'The locks were not all removed: the password dialog was closed, or some files are open in another program.',
          mbConfirmation, MB_YESNOCANCEL, [
           '&Try again'#13#10'Opens the password dialog again.',
           '&Uninstall anyway'#13#10'They stay locked. Install Clearspace again later to open them.'],
          0);
    end;
    if Answer <> IDNO then
    begin
      Result := False;   { Cancel: Clearspace stays installed }
      Exit;
    end;
  end;

  Note := '';
  RemoveLabel := '&Remove everything'#13#10'Nothing from Clearspace is left on this PC.';
  if LocksLeft then
  begin
    Note := ' Because some items are still locked, the lock records (kept with your tags) and the lock icons stay either way.';
    RemoveLabel := '&Remove the rest'#13#10'Settings, saved index and logs are deleted; lock records stay.';
  end;
  Answer := TaskDialogMsgBox(
    'Remove your Clearspace data too?',
    'That is your settings, tags, folder types, the saved search index and logs.' + Note,
    mbConfirmation, MB_YESNOCANCEL, [
     '&Keep my data'#13#10'Installing Clearspace again picks up where you left off.', RemoveLabel],
    0);
  if Answer = IDNO then
    RemoveUserData := True
  else if Answer <> IDYES then
    Result := False;
end;

{ Deletes what is inside Dir except entries whose name starts with KeepPrefix, then Dir itself if it is empty. }
procedure DeleteAllExcept(const Dir, KeepPrefix: String);
var
  FindRec: TFindRec;
  Path: String;
begin
  if FindFirst(AddBackslash(Dir) + '*', FindRec) then
  begin
    try
      repeat
        if (FindRec.Name <> '.') and (FindRec.Name <> '..') then
          if CompareText(Copy(FindRec.Name, 1, Length(KeepPrefix)), KeepPrefix) <> 0 then
          begin
            Path := AddBackslash(Dir) + FindRec.Name;
            if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
              DelTree(Path, True, True, True)
            else
              DeleteFile(Path);
          end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
  RemoveDir(Dir);
end;

procedure RemoveClearspaceData;
begin
  if LocksLeft then
  begin
    { tags.db (and its -wal / -shm files) holds the lock records; Icons holds the lock icons Explorer shows. }
    DeleteAllExcept(ExpandConstant('{userappdata}\Clearspace'), 'tags.db');
    DeleteAllExcept(ExpandConstant('{localappdata}\Clearspace'), 'Icons');
  end
  else
  begin
    DelTree(ExpandConstant('{userappdata}\Clearspace'), True, True, True);
    DelTree(ExpandConstant('{localappdata}\Clearspace'), True, True, True);
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
  Helper, Temp: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    { Before the files go: close Clearspace, then take its entries out of Explorer. }
    RunClearspace('--quit', SW_HIDE);
    RunClearspace('--unregister-shell', SW_HIDE);
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'Clearspace locked folders');

    { NEW (journal catch-up): removing Clearspace also removes the index helper service, if it was
      turned on. This needs administrator permission, so Windows asks once. }
    if HelperServiceInstalled then
    begin
      Helper := ExpandConstant('{app}\ClearspaceIndexHelper.exe');
      if FileExists(Helper) then
        ShellExec('runas', Helper, '--uninstall --quiet', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    end;
    Exit;
  end;

  if CurUninstallStep <> usPostUninstall then
    Exit;

  { Native libraries the single-file apps unpack for themselves (a cache, not user data). }
  Temp := GetEnv('TEMP');
  if Temp <> '' then
  begin
    DelTree(AddBackslash(Temp) + '.net\Clearspace', True, True, True);
    DelTree(AddBackslash(Temp) + '.net\ClearspaceIndexHelper', True, True, True);
  end;
  { Installers Clearspace downloaded to update itself (also a cache). }
  DelTree(ExpandConstant('{localappdata}\Clearspace\Updates'), True, True, True);
  RemoveDir(ExpandConstant('{localappdata}\Clearspace'));   { only goes when nothing else is in it }

  if RemoveUserData then
    RemoveClearspaceData;
end;
