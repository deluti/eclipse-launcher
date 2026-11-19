#ifndef AppVersion
  #define AppVersion "0.4.0"
#endif
#ifndef PublishDir
  #error PublishDir must point to the clean self-contained win-x86 publish directory
#endif
#ifndef ReleaseDir
  #error ReleaseDir must point to the release output directory
#endif

[Setup]
AppId={code:EclipseAppId}
UsePreviousLanguage=no
LanguageDetectionMethod=none
AppName=Eclipse Launcher
AppVersion={#AppVersion}
AppPublisher=deluti
AppPublisherURL=https://github.com/deluti/eclipse-launcher
AppSupportURL=https://github.com/deluti/eclipse-launcher/issues
AppUpdatesURL=https://github.com/deluti/eclipse-launcher/releases
DefaultDirName={localappdata}\Programs\Eclipse Launcher
DisableDirPage=no
AppendDefaultDirName=no
DefaultGroupName=Eclipse Launcher
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64os
#if VER >= EncodeVer(7, 0, 0)
SetupArchitecture=x86
#endif
MinVersion=10.0.19041
OutputDir={#ReleaseDir}
OutputBaseFilename=eclipse-launcher-windows-x86
SetupIconFile=..\src\Eclipse.Launcher\Assets\Eclipse.ico
UninstallDisplayIcon={app}\Eclipse.exe
VersionInfoVersion={#AppVersion}.0
VersionInfoDescription=Eclipse Launcher Windows x86 Setup
WizardStyle=modern
Compression=lzma2
SolidCompression=yes
CloseApplications=yes
RestartApplications=no
UninstallDisplayName=Eclipse Launcher

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[CustomMessages]
english.ExistingInstall=Eclipse %1 is already in this folder.%n%nUpdate it to version {#AppVersion}?%nWorlds, mods, screenshots, settings and playtime will be kept.
russian.ExistingInstall=В этой папке найдена установка Eclipse %1.%n%nОбновить её до версии {#AppVersion}?%nМиры, моды, скриншоты, настройки и часы будут сохранены.
english.UpdateCancelled=Update cancelled.
russian.UpdateCancelled=Обновление отменено.
english.CloseEclipse=Close Eclipse and try the update again.
russian.CloseEclipse=Закройте Eclipse и повторите обновление.

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "*.pdb,*.log,data\*,session.bin,launcher-config.json"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PublishDir}\launcher-config.json"; DestDir: "{app}"; Flags: onlyifdoesntexist uninsneveruninstall

[Icons]
Name: "{group}\Eclipse Launcher"; Filename: "{app}\Eclipse.exe"; WorkingDir: "{app}"; Check: IsNormalInstallation
Name: "{autodesktop}\Eclipse Launcher"; Filename: "{app}\Eclipse.exe"; WorkingDir: "{app}"; Tasks: desktopicon; Check: IsNormalInstallation

[Run]
Filename: "{app}\Eclipse.exe"; Description: "{cm:LaunchProgram,Eclipse Launcher}"; Flags: nowait postinstall skipifsilent; Check: IsManualInstall
Filename: "{app}\Eclipse.exe"; Parameters: "{code:UpdatedLaunchParameters}"; Flags: nowait; Check: IsLauncherUpdate

; Inno Setup removes only installed files. Runtime-created data (worlds, mods,
; settings and sessions in data/) is deliberately kept on upgrade and uninstall.

[Code]
var
  ApprovedDirectory: String;

function EclipseAppId(Param: String): String;
begin
  { Integration checks use a separate uninstall entry and never alter a real installation. }
  if ExpandConstant('{param:ECLIPSETESTINSTALL|0}') = '1' then
    Result := '{09426556-9151-44BD-B2C7-20A066209C74}'
  else Result := '{3BEB6AE3-2E24-4790-890F-F469AF69C4F3}';
end;

function EclipseOpenProcess(Access: LongWord; Inherit: Boolean; ProcessId: LongWord): LongWord;
  external 'OpenProcess@kernel32.dll stdcall';
function EclipseWaitForSingleObject(Handle, Milliseconds: LongWord): LongWord;
  external 'WaitForSingleObject@kernel32.dll stdcall';
function EclipseCloseHandle(Handle: LongWord): Boolean;
  external 'CloseHandle@kernel32.dll stdcall';

function IsLauncherUpdate: Boolean;
begin
  Result := ExpandConstant('{param:ECLIPSEUPDATE|0}') = '1';
end;

function IsNormalInstallation: Boolean;
begin
  Result := ExpandConstant('{param:ECLIPSETESTINSTALL|0}') <> '1';
end;

function IsManualInstall: Boolean;
begin
  Result := not IsLauncherUpdate;
end;

function UpdatedLaunchParameters(Param: String): String;
var
  DataDirectory: String;
begin
  DataDirectory := ExpandConstant('{param:ECLIPSEDATA|}');
  if DataDirectory = '' then Result := ''
  else Result := '--data-dir ' + AddQuotes(DataDirectory);
end;

function ConfirmExistingDirectory: Boolean;
var
  DirectoryName, ExistingVersion, MessageText: String;
begin
  Result := True;
  DirectoryName := WizardDirValue;
  if CompareText(DirectoryName, ApprovedDirectory) = 0 then Exit;
  if FileExists(DirectoryName + '\Eclipse.exe') or FileExists(DirectoryName + '\Eclipse.dll') or DirExists(DirectoryName + '\data') then
  begin
    ExistingVersion := '';
    GetVersionNumbersString(DirectoryName + '\Eclipse.dll', ExistingVersion);
    Log('Existing Eclipse detected: ' + ExistingVersion + '; preserving ' + DirectoryName + '\data');
    MessageText := FmtMessage(CustomMessage('ExistingInstall'), [ExistingVersion]);
    if not IsLauncherUpdate then
      Result := SuppressibleMsgBox(MessageText, mbConfirmation, MB_YESNO, IDYES) = IDYES;
  end;
  if Result then ApprovedDirectory := DirectoryName;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if CurPageID = wpSelectDir then Result := ConfirmExistingDirectory;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ProcessId: Integer;
  Handle, WaitResult: LongWord;
begin
  Result := '';
  if not ConfirmExistingDirectory then
  begin
    Result := CustomMessage('UpdateCancelled');
    Exit;
  end;
  if IsLauncherUpdate then
  begin
    ProcessId := StrToIntDef(ExpandConstant('{param:LAUNCHERPID|0}'), 0);
    if ProcessId > 0 then
    begin
      Handle := EclipseOpenProcess($100000, False, ProcessId);
      if Handle <> 0 then
      begin
        WaitResult := EclipseWaitForSingleObject(Handle, 15000);
        EclipseCloseHandle(Handle);
        if WaitResult <> 0 then Result := CustomMessage('CloseEclipse');
      end;
    end;
  end;
end;
