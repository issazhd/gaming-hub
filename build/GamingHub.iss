; Gaming Hub — per-user Windows installer (Inno Setup 6). Built by build/publish.ps1 -Installer.
; No administrator rights: installs to %LOCALAPPDATA%\Programs\GamingHub. .NET is bundled (self-contained publish).
; User data (%LOCALAPPDATA%\GamingHub: library, settings, artwork, playtime) is kept on uninstall unless the user opts in.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\dist\GamingHub-selfcontained"
#endif

[Setup]
AppId={{6F3C2A9E-4B7D-4E2A-9C1F-2D8B5E7A1C40}
AppName=Gaming Hub
AppVersion={#AppVersion}
AppVerName=Gaming Hub {#AppVersion}
AppPublisher=Issa Zahreddine
DefaultDirName={localappdata}\Programs\GamingHub
DisableDirPage=yes
DefaultGroupName=Gaming Hub
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=..\dist
OutputBaseFilename=GamingHub-{#AppVersion}-Setup
SetupIconFile=..\src\GamingHub.App\Assets\app.ico
UninstallDisplayIcon={app}\GamingHub.exe
UninstallDisplayName=Gaming Hub
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
CloseApplications=force
RestartApplications=no

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked
Name: "startup"; Description: "Start Gaming Hub when I sign in to Windows (in the tray; PS opens it)"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{userprograms}\Gaming Hub"; Filename: "{app}\GamingHub.exe"
Name: "{userdesktop}\Gaming Hub"; Filename: "{app}\GamingHub.exe"; Tasks: desktopicon
; Same shortcut the app's own "Start with Windows" setting manages (listed in Task Manager › Startup apps).
Name: "{userstartup}\Gaming Hub"; Filename: "{app}\GamingHub.exe"; Parameters: "--startup"; WorkingDir: "{app}"; Tasks: startup

[Registry]
; Versions ≤ 1.0.1 used the HKCU Run key; remove it (startup now uses a Startup-folder shortcut, see [Icons]).
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "GamingHub"; Flags: deletevalue
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run"; ValueType: none; ValueName: "GamingHub"; Flags: deletevalue

[Run]
Filename: "{app}\GamingHub.exe"; Description: "Open Gaming Hub now"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{cmd}"; Parameters: "/C taskkill /IM GamingHub.exe /F"; Flags: runhidden; RunOnceId: "StopGamingHub"

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var DataDir: string;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'GamingHub');
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run', 'GamingHub');
    DataDir := ExpandConstant('{localappdata}\GamingHub');
    if DirExists(DataDir) and not UninstallSilent() then
      if MsgBox('Also delete your Gaming Hub data (library, settings, artwork, playtime, logs)?' + #13#10 + DataDir,
                mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(DataDir, True, True, True);
  end;
end;
