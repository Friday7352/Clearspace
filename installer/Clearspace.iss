; Clearspace | Windows installer definition.

#define AppName "Clearspace"
#define AppPublisher "Clearspace"
#define VersionFile AddBackslash(SourcePath) + "..\VERSION"
#define VersionHandle FileOpen(VersionFile)
#define AppVersion Trim(FileRead(VersionHandle))
#expr FileClose(VersionHandle)

#if AppVersion == ""
  #error VERSION at the repository root is empty.
#endif

[Setup]
AppId={{E3E7C9F3-AB35-4A5A-9725-C76908ADDC74}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={localappdata}\Programs\Clearspace
DefaultGroupName=Clearspace
DisableProgramGroupPage=yes
OutputDir=..\release
OutputBaseFilename=ClearspaceSetup
SetupIconFile=..\Clearspace\Assets\Clearspace.ico
UninstallDisplayIcon={app}\Clearspace.exe
VersionInfoVersion={#AppVersion}
VersionInfoCompany={#AppPublisher}
VersionInfoProductName=Clearspace Setup
Compression=lzma2/max
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
CloseApplications=yes
RestartApplications=no
WizardStyle=modern
MinVersion=10.0

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: checkedonce

[Files]
Source: "..\installer-build\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Clearspace"; Filename: "{app}\Clearspace.exe"; WorkingDir: "{app}"; Comment: "Clearspace file explorer"
Name: "{autodesktop}\Clearspace"; Filename: "{app}\Clearspace.exe"; WorkingDir: "{app}"; Tasks: desktopicon; Comment: "Clearspace file explorer"

[Run]
Filename: "{app}\Clearspace.exe"; Description: "Launch Clearspace"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent

[Code]
procedure CurStepChanged(CurStep: TSetupStep);
var
  LegacyDir: String;
begin
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
