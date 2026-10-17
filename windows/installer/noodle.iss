; Noodle for Windows installer (Inno Setup 6). Per-user, no admin prompt.
;   iscc /DVersion=0.2.0 /DSource=path\to\publish windows\installer\noodle.iss
; Source is the `dotnet publish -c Release` output: Noodle.exe, noodle.cmd, Resources\.

#ifndef Version
  #define Version "0.0.0"
#endif
#ifndef Source
  #define Source "..\Noodle\bin\Release\net8.0-windows\win-x64\publish"
#endif

[Setup]
AppId={{6C1E3A52-8F0B-4C6E-9C9B-6F4C2D1A7E11}
AppName=Noodle
AppVersion={#Version}
AppPublisher=Noodle
AppPublisherURL=https://tonitherigatoni.com
DefaultDirName={localappdata}\Programs\Noodle
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputBaseFilename=NoodleSetup
SetupIconFile=..\Noodle\noodle.ico
UninstallDisplayIcon={app}\Noodle.exe
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ChangesEnvironment=yes

[Tasks]
Name: path; Description: "Add the noodle command to PATH (noodle setup, noodle remove)"

[Files]
Source: "{#Source}\Noodle.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#Source}\noodle.cmd"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#Source}\Resources\*"; DestDir: "{app}\Resources"; Flags: ignoreversion recursesubdirs createallsubdirs

[InstallDelete]
; Figures renamed or dropped upstream shouldn't linger from an older version.
Type: filesandordirs; Name: "{app}\Resources"

[Icons]
Name: "{userprograms}\Noodle"; Filename: "{app}\Noodle.exe"

[Registry]
Root: HKCU; Subkey: "Environment"; ValueType: expandsz; ValueName: "Path"; ValueData: "{olddata};{app}"; \
  Tasks: path; Check: NeedsPath(ExpandConstant('{app}'))

[Run]
; Opens on the Connect to Claude Code step, which shows the change to settings.json before writing it.
Filename: "{app}\Noodle.exe"; Description: "Open Noodle"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Takes Noodle's hooks back out of ~/.claude/settings.json (backed up first), removes the login item, quits the app.
Filename: "{app}\Noodle.exe"; Parameters: "remove -y"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveHooks"

[Code]
function NeedsPath(Dir: string): Boolean;
var
  Path: string;
begin
  if not RegQueryStringValue(HKCU, 'Environment', 'Path', Path) then Path := '';
  Result := Pos(';' + Lowercase(Dir) + ';', ';' + Lowercase(Path) + ';') = 0;
end;

procedure RemovePath(Dir: string);
var
  Path: string;
  P: Integer;
begin
  if not RegQueryStringValue(HKCU, 'Environment', 'Path', Path) then Exit;
  P := Pos(';' + Lowercase(Dir), Lowercase(Path));
  if P = 0 then Exit;
  Delete(Path, P, Length(Dir) + 1);
  RegWriteExpandStringValue(HKCU, 'Environment', 'Path', Path);
end;

{ An upgrade can't replace Noodle.exe while it's running, so ask the running copy to quit first. }
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Code: Integer;
begin
  if FileExists(ExpandConstant('{app}\Noodle.exe')) then
  begin
    Exec(ExpandConstant('{app}\Noodle.exe'), '--event quit', '', SW_HIDE, ewWaitUntilTerminated, Code);
    Sleep(1000);
  end;
  Result := '';
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then RemovePath(ExpandConstant('{app}'));
end;
