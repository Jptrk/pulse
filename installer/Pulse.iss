; Inno Setup script for Pulse. Build with release.ps1, which publishes the app and passes AppVersion/PublishDir.
; Manual build: ISCC.exe /DAppVersion=0.1.0 /DPublishDir=..\artifacts\publish installer\Pulse.iss

#define AppName "Pulse"
#define AppExe "Pulse.exe"
#define AppUrl "https://github.com/Jptrk/pulse"
#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\artifacts\publish"
#endif

[Setup]
; AppId identifies the app across upgrades — never change it.
AppId={{3111AABC-A8B9-4A59-8A4E-EFB56DB8EA5E}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Jptrk
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
VersionInfoVersion={#AppVersion}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
; Admin install: Program Files, and the start-at-sign-in task runs with highest privileges.
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=..\artifacts
OutputBaseFilename=PulseSetup-{#AppVersion}
SetupIconFile=..\src\Pulse.App\Assets\pulse.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
; Pulse is closed explicitly with --quit (see [Code]); this is the fallback.
CloseApplications=yes
RestartApplications=no
; The uninstaller may delete the user's data folders after asking.
UsedUserAreasWarning=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "autostart"; Description: "Start Pulse when I sign in (runs as admin, no UAC prompt each time)"; GroupDescription: "Startup:"
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Parameters: "--register-autostart"; Tasks: autostart; Flags: runhidden waituntilterminated; StatusMsg: "Setting up start at sign-in..."
Filename: "{app}\{#AppExe}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Close Pulse cleanly first (stops FPS capture and saves the current session), then remove the logon task.
Filename: "{app}\{#AppExe}"; Parameters: "--quit"; Flags: runhidden waituntilterminated; RunOnceId: "QuitPulse"
Filename: "{app}\{#AppExe}"; Parameters: "--unregister-autostart"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveAutostart"

[Code]
// Upgrades: ask the running Pulse to exit cleanly before its files are replaced.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ExePath: String;
  ResultCode: Integer;
begin
  Result := '';
  ExePath := ExpandConstant('{app}\{#AppExe}');
  if FileExists(ExePath) then
    Exec(ExePath, '--quit', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

// Saved sessions and settings live in the user profile; offer to remove them.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if (CurUninstallStep = usPostUninstall) and not UninstallSilent then
  begin
    if MsgBox('Also delete your saved Pulse sessions, settings and logs?', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
    begin
      DelTree(ExpandConstant('{localappdata}\Pulse'), True, True, True);
      DelTree(ExpandConstant('{userappdata}\Pulse'), True, True, True);
    end;
  end;
end;
