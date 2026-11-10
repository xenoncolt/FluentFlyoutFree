; Inno Setup script for FluentFlyout Free.
; Build with installer\build-installer.ps1 (publishes the app, then compiles this script), or manually:
;   iscc /DAppVersion=2.16.1 /DArch=x64 /DSourceDir=..\artifacts\publish\x64 FluentFlyoutFree.iss
;
; The installer is per-user (no admin rights / UAC prompt), which lets the app update itself silently:
; the in-app updater downloads the newest FluentFlyoutFree-Setup-<arch>.exe from GitHub Releases and runs
;   FluentFlyoutFree-Setup-x64.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /UPDATE=1

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef Arch
  #define Arch "x64"
#endif
#ifndef SourceDir
  #define SourceDir "..\artifacts\publish\" + Arch
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts\installer"
#endif

#define AppName "FluentFlyout Free"
#define AppExeName "FluentFlyout.exe"
#define AppPublisher "xenoncolt"
#define AppURL "https://github.com/xenoncolt/FluentFlyoutFree"

[Setup]
; keep this AppId forever: it is how upgrades find the existing installation
AppId={{86B2A897-10CB-4D95-BF6F-C10B70BF9966}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}/issues
AppUpdatesURL={#AppURL}/releases
VersionInfoVersion={#AppVersion}
VersionInfoProductName={#AppName}

; per-user install into %LOCALAPPDATA%\Programs, no elevation
PrivilegesRequired=lowest
DefaultDirName={autopf}\FluentFlyoutFree
DisableDirPage=auto
DisableProgramGroupPage=yes
DefaultGroupName={#AppName}

#if Arch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif
; Windows 10 1809+
MinVersion=10.0.17763

OutputDir={#OutputDir}
OutputBaseFilename=FluentFlyoutFree-Setup-{#Arch}
SetupIconFile=..\FluentFlyoutWPF\Resources\FluentFlyout2.ico
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName}
WizardStyle=modern
Compression=lzma2/ultra64
SolidCompression=yes
LZMAUseSeparateProcess=yes

; close a running FluentFlyout (Restart Manager) before replacing its files;
; when silent (auto-update) it is closed without asking
CloseApplications=force
RestartApplications=no
SetupLogging=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
; interactive install: optional "Launch" checkbox on the last page
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(AppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
; silent auto-update: relaunch the app it just closed
Filename: "{app}\{#AppExeName}"; Flags: nowait; Check: IsUpdateMode

[UninstallRun]
; stop the copy of FluentFlyout running from this install folder (not other installs, e.g. the Store version)
Filename: "powershell.exe"; Parameters: "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command ""Get-Process -Name FluentFlyout -ErrorAction SilentlyContinue | Where-Object {{ $_.Path -like '{app}\*' } | Stop-Process -Force"""; Flags: runhidden waituntilterminated; RunOnceId: "StopFluentFlyout"

[Code]
const
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
  RunValueName = 'FluentFlyout';

function IsUpdateMode: Boolean;
begin
  Result := ExpandConstant('{param:UPDATE|0}') = '1';
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  StartupCommand: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    { the app registers itself to start with Windows; remove that entry only if it points to this install }
    if RegQueryStringValue(HKCU, RunKey, RunValueName, StartupCommand) then
      if Pos(Lowercase(ExpandConstant('{app}')), Lowercase(StartupCommand)) > 0 then
        RegDeleteValue(HKCU, RunKey, RunValueName);
  end;
end;
