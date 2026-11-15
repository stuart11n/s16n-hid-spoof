; s16n-hid-spoof — Inno Setup 6
; Compile via scripts\package.ps1

#ifndef StageDir
  #define StageDir "..\dist\stage"
#endif
#ifndef OutDir
  #define OutDir "..\dist"
#endif
#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif

#define MyAppName "s16n-hid-spoof"
#define MyAppPublisher "s16n"
#define MyAppExeName "s16n-hid-spoof.exe"
#define MyAppURL "https://github.com/stuart11n/s16n-hid-spoof"

[Setup]
AppId={{B7E2D4A1-9C8F-4A2E-8B1D-5E6F0A3C7D19}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir={#OutDir}
OutputBaseFilename=s16n-hid-spoof-setup-{#MyAppVersion}
SetupIconFile={#StageDir}\Assets\app.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
CloseApplications=yes
RestartApplications=no
UsedUserAreasWarning=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked
Name: "autostart"; Description: "Start with Windows (elevated tray task)"; GroupDescription: "Startup:"; Flags: unchecked

[Files]
Source: "{#StageDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent runascurrentuser
Filename: "{app}\register-autostart.cmd"; \
  StatusMsg: "Registering Start with Windows task…"; \
  Flags: runhidden waituntilterminated; Tasks: autostart

[UninstallRun]
Filename: "{app}\unregister-autostart.cmd"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveAutostartTask"
