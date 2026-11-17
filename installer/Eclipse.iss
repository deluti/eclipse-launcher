#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef PublishDir
  #error PublishDir must point to the clean self-contained win-x86 publish directory
#endif
#ifndef ReleaseDir
  #error ReleaseDir must point to the release output directory
#endif

[Setup]
AppId={{3BEB6AE3-2E24-4790-890F-F469AF69C4F3}
AppName=Eclipse Launcher
AppVersion={#AppVersion}
AppPublisher=deluti
AppPublisherURL=https://github.com/deluti/eclipse-launcher
AppSupportURL=https://github.com/deluti/eclipse-launcher/issues
AppUpdatesURL=https://github.com/deluti/eclipse-launcher/releases
DefaultDirName={localappdata}\Programs\Eclipse Launcher
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
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "*.pdb,*.log,data\*,session.bin,launcher-config.json"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PublishDir}\launcher-config.json"; DestDir: "{app}"; Flags: onlyifdoesntexist uninsneveruninstall

[Icons]
Name: "{group}\Eclipse Launcher"; Filename: "{app}\Eclipse.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\Eclipse Launcher"; Filename: "{app}\Eclipse.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\Eclipse.exe"; Description: "{cm:LaunchProgram,Eclipse Launcher}"; Flags: nowait postinstall skipifsilent

; Inno Setup removes only installed files. Runtime-created data (worlds, mods,
; settings and sessions in data/) is deliberately kept on upgrade and uninstall.
