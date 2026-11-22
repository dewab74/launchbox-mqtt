; Inno Setup script for LaunchBoxMqtt-Setup-<version>.exe.
; Built from the published plugin zip (unzipped to PluginDir) by the
; installer workflow, after scripts/release.sh has published the release:
;   ISCC.exe /DAppVersion=0.5.0 /DPluginDir=<unzipped>\LaunchBoxMqtt installer\LaunchBoxMqtt.iss
;
; Interactive: pick the LaunchBox folder (checked for LaunchBox.exe); the
; plugin is copied to <LaunchBox>\Plugins\LaunchBoxMqtt. Configure it in
; LaunchBox: Tools > LaunchBox MQTT settings.
;
; Scripted:
;   LaunchBoxMqtt-Setup-0.5.0.exe /VERYSILENT /SUPPRESSMSGBOXES /DIR="C:\LaunchBox" ^
;     [/HOST=homeassistant.local [/PORT=1883] [/TLS=1] [/VERIFY=0] [/CAFILE=C:\ca.pem] ^
;      [/USERNAME=launchbox] [/PASSWORDFILE=C:\pw.txt | /PASSWORD=secret] [/NODENAME="Arcade PC"] ^
;      [/COMMANDS=1] [/ARTWORK=1] [/STATS=1]] [/LOG=C:\setup.log]
; With /HOST the settings file is written and the plugin enabled; LaunchBox
; moves a password from that file into its own secret store on the next start.
; Without /HOST, existing settings are kept. LaunchBox and BigBox must be
; closed (their plugins are loaded); otherwise setup stops (exit code 7).

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef PluginDir
  #define PluginDir "..\artifacts\LaunchBoxMqtt"
#endif

#define AppName "LaunchBox MQTT plugin"
#define RepoUrl "https://github.com/dewab74/launchbox-mqtt"

[Setup]
AppId={{2E8F7C41-9A6B-4E0D-8C3A-5B1F6D2E7A90}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=launchbox-mqtt contributors
AppPublisherURL={#RepoUrl}
AppSupportURL={#RepoUrl}/issues
AppUpdatesURL={#RepoUrl}/releases
DefaultDirName={code:DefaultLaunchBoxDir}
DirExistsWarning=no
AppendDefaultDirName=no
UsePreviousAppDir=yes
DisableProgramGroupPage=yes
; Most LaunchBox installs are in the user's profile or a user-owned folder;
; choose "for all users" (admin) for one under Program Files.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog commandline
OutputDir=..\artifacts\installer
OutputBaseFilename=LaunchBoxMqtt-Setup-{#AppVersion}
UninstallFilesDir={app}\Plugins\LaunchBoxMqtt-uninstall
UninstallDisplayName={#AppName}
LicenseFile=..\LICENSE
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
CloseApplications=no
SetupLogging=yes

[Messages]
SelectDirLabel3=Setup will install the plugin into the Plugins folder of this LaunchBox installation.
SelectDirBrowseLabel=Choose your LaunchBox folder (the one with LaunchBox.exe), then click Next.

[Files]
Source: "{#PluginDir}\*"; DestDir: "{app}\Plugins\LaunchBoxMqtt"; Flags: ignoreversion recursesubdirs createallsubdirs

[Code]
function Param(const Name, Default: String): String;
begin
  Result := ExpandConstant('{param:' + Name + '|' + Default + '}');
end;

function IsOn(const Value: String): Boolean;
begin
  Result := (Value = '1') or (CompareText(Value, 'true') = 0) or (CompareText(Value, 'yes') = 0);
end;

function JsonBool(const Value: Boolean): String;
begin
  if Value then Result := 'true' else Result := 'false';
end;

function JsonEscape(const S: String): String;
var
  I: Integer;
  C: Char;
begin
  Result := '';
  for I := 1 to Length(S) do
  begin
    C := S[I];
    if C = '\' then Result := Result + '\\'
    else if C = '"' then Result := Result + '\"'
    else if Ord(C) < 32 then Result := Result + ' '
    else Result := Result + C;
  end;
end;

function IsLaunchBoxDir(const Dir: String): Boolean;
begin
  Result := FileExists(AddBackslash(Dir) + 'LaunchBox.exe') or FileExists(AddBackslash(Dir) + 'Core\LaunchBox.exe');
end;

function DefaultLaunchBoxDir(Param: String): String;
begin
  Result := ExpandConstant('{%USERPROFILE}\LaunchBox');
  if IsLaunchBoxDir('C:\LaunchBox') and not IsLaunchBoxDir(Result) then
    Result := 'C:\LaunchBox';
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID = wpSelectDir) and not IsLaunchBoxDir(WizardDirValue) then
  begin
    MsgBox('No LaunchBox.exe in ' + WizardDirValue + '.' + #13#10 + 'Choose the folder LaunchBox is installed in.', mbError, MB_OK);
    Result := False;
  end;
end;

function IsRunning(const Exe: String): Boolean;
var
  Code: Integer;
  ListFile: String;
  Lines: TArrayOfString;
begin
  ListFile := ExpandConstant('{tmp}\tasklist.txt');
  Exec(ExpandConstant('{cmd}'), '/C tasklist /FI "IMAGENAME eq ' + Exe + '" /NH > "' + ListFile + '"', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Result := LoadStringsFromFile(ListFile, Lines) and (GetArrayLength(Lines) > 0) and (Pos(Lowercase(Exe), Lowercase(Lines[GetArrayLength(Lines) - 1]) + Lowercase(Lines[0])) > 0);
end;

{ LaunchBox and BigBox keep the plugin DLLs loaded, so they must be closed. }
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if not IsLaunchBoxDir(WizardDirValue) then
    Result := 'No LaunchBox.exe in ' + WizardDirValue + '. Pass /DIR=<your LaunchBox folder>.'
  else if IsRunning('LaunchBox.exe') or IsRunning('BigBox.exe') then
    Result := 'Close LaunchBox and BigBox first: they have the plugin loaded.';
end;

procedure WriteSettings;
var
  Json, Host, PasswordFile, Password, Dir, Path, NodeName, CaFile, Username: String;
  Lines: TArrayOfString;
begin
  Host := Param('HOST', '');
  if Host = '' then Exit;

  Json := '{' + #13#10 + '  "enabled": true,' + #13#10 + '  "host": "' + JsonEscape(Host) + '"';
  if Param('PORT', '') <> '' then
    Json := Json + ',' + #13#10 + '  "port": ' + IntToStr(StrToIntDef(Param('PORT', ''), 1883));
  Json := Json + ',' + #13#10 + '  "tls": ' + JsonBool(IsOn(Param('TLS', '0')));
  Json := Json + ',' + #13#10 + '  "verifyCertificate": ' + JsonBool(IsOn(Param('VERIFY', '1')));
  CaFile := Param('CAFILE', '');
  if CaFile <> '' then Json := Json + ',' + #13#10 + '  "caCertificatePath": "' + JsonEscape(CaFile) + '"';
  Username := Param('USERNAME', '');
  if Username <> '' then Json := Json + ',' + #13#10 + '  "username": "' + JsonEscape(Username) + '"';

  Password := Param('PASSWORD', '');
  PasswordFile := Param('PASSWORDFILE', '');
  if (PasswordFile <> '') and LoadStringsFromFile(PasswordFile, Lines) and (GetArrayLength(Lines) > 0) then
  begin
    Password := Trim(Lines[0]);
    DeleteFile(PasswordFile);
  end;
  if Password <> '' then Json := Json + ',' + #13#10 + '  "password": "' + JsonEscape(Password) + '"';

  NodeName := Param('NODENAME', '');
  if NodeName <> '' then Json := Json + ',' + #13#10 + '  "nodeName": "' + JsonEscape(NodeName) + '"';
  Json := Json + ',' + #13#10 + '  "commandsEnabled": ' + JsonBool(IsOn(Param('COMMANDS', '0')));
  Json := Json + ',' + #13#10 + '  "artworkEnabled": ' + JsonBool(IsOn(Param('ARTWORK', '0')));
  Json := Json + ',' + #13#10 + '  "statsEnabled": ' + JsonBool(IsOn(Param('STATS', '0')));
  Json := Json + #13#10 + '}' + #13#10;

  Dir := AddBackslash(WizardDirValue) + 'Local\Plugins\.data\LaunchBoxMqtt\files';
  ForceDirectories(Dir);
  Path := Dir + '\launchbox-mqtt.json';
  if SaveStringToFile(Path, Json, False) then
    Log('Wrote ' + Path + ' (the password, if any, moves to LaunchBox''s secret store on its next start)')
  else
  begin
    Log('Could not write ' + Path);
    SuppressibleMsgBox('The plugin is installed, but its settings could not be written to ' + Path + '.', mbError, MB_OK, IDOK);
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    WriteSettings;
end;
