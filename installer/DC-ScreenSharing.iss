#define MyAppName "DC-ScreenSharing"
#define MyAppVersion "1.0.5"
#define MyAppPublisher "DC-ScreenSharing Team"
#define MyAppURL "https://github.com/FaelSemW/DC-ScreenSharing"
#define MyAppExeName "DC-ScreenSharing.exe"

[Setup]
AppId={{D37E7A1C-9E4B-4E38-BFB8-43958F87C5E2}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=..\dist\installer
OutputBaseFilename=DC-ScreenSharing-Setup-{#MyAppVersion}
SetupIconFile=app.ico
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=commandline
UninstallDisplayIcon={app}\{#MyAppExeName}
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "..\dist\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
; Install and configure the Network Service as Automatic and start it
Filename: "sc.exe"; Parameters: "create DCSS.NetworkService binPath= ""{app}\DCSS.NetworkService.exe"" start= auto displayname= ""DC-ScreenSharing Network Service"""; Flags: runhidden
Filename: "sc.exe"; Parameters: "config DCSS.NetworkService binPath= ""{app}\DCSS.NetworkService.exe"" start= auto"; Flags: runhidden
Filename: "sc.exe"; Parameters: "description DCSS.NetworkService ""Provides privileged application-specific routing for DC-ScreenSharing."""; Flags: runhidden
Filename: "sc.exe"; Parameters: "start DCSS.NetworkService"; Flags: runhidden
; Run main application after setup
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "sc.exe"; Parameters: "stop DCSS.NetworkService"; Flags: runhidden
Filename: "sc.exe"; Parameters: "delete DCSS.NetworkService"; Flags: runhidden

[Code]
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  // Stop existing service and processes so binaries can be overwritten cleanly
  Exec('sc.exe', 'stop DCSS.NetworkService', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec('taskkill.exe', '/F /IM DCSS.NetworkService.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec('taskkill.exe', '/F /IM DC-ScreenSharing.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(1500);
  Result := '';
end;
