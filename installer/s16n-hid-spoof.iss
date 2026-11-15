; s16n-hid-spoof — Inno Setup 6
; Framework-dependent publish; installs .NET 10 Desktop + ASP.NET runtimes if missing.
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

[Code]
// WinForms needs WindowsDesktop.App; Kestrel/Web needs AspNetCore.App (both major 10).
function DotNetSharedExists(const SharedName: string): Boolean;
var
  FindRec: TFindRec;
  SharedRoot: string;
begin
  Result := False;
  SharedRoot := ExpandConstant('{commonpf64}\dotnet\shared\' + SharedName);
  if not DirExists(SharedRoot) then
    Exit;
  if FindFirst(SharedRoot + '\10.*', FindRec) then
  begin
    try
      repeat
        if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
        begin
          Result := True;
          Exit;
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

function NeedsDesktopRuntime: Boolean;
begin
  Result := not DotNetSharedExists('Microsoft.WindowsDesktop.App');
end;

function NeedsAspNetRuntime: Boolean;
begin
  Result := not DotNetSharedExists('Microsoft.AspNetCore.App');
end;

function InstallRuntime(const Url, FileName, Status: string): Boolean;
var
  LocalFile: string;
  ResultCode: Integer;
  Bytes: Int64;
begin
  Result := False;
  WizardForm.StatusLabel.Caption := Status;
  try
    Bytes := DownloadTemporaryFile(Url, FileName, '', nil);
    LocalFile := ExpandConstant('{tmp}\' + FileName);
    if (Bytes <= 0) or (not FileExists(LocalFile)) then
    begin
      MsgBox('Failed to download ' + FileName + ' from ' + Url, mbError, MB_OK);
      Exit;
    end;
    if not Exec(LocalFile, '/install /quiet /norestart', '', SW_HIDE,
         ewWaitUntilTerminated, ResultCode) then
    begin
      MsgBox('Failed to launch ' + FileName, mbError, MB_OK);
      Exit;
    end;
    // 0 = success, 3010 = success / reboot required
    if (ResultCode <> 0) and (ResultCode <> 3010) then
    begin
      MsgBox(FileName + ' setup exited with code ' + IntToStr(ResultCode), mbError, MB_OK);
      Exit;
    end;
    Result := True;
  except
    MsgBox(GetExceptionMessage, mbError, MB_OK);
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  NeedsRestart := False;

  if NeedsDesktopRuntime then
  begin
    if not InstallRuntime(
      'https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe',
      'windowsdesktop-runtime-win-x64.exe',
      'Installing .NET 10 Desktop Runtime…') then
    begin
      Result := 'Could not install the .NET 10 Desktop Runtime.';
      Exit;
    end;
  end;

  if NeedsAspNetRuntime then
  begin
    if not InstallRuntime(
      'https://aka.ms/dotnet/10.0/aspnetcore-runtime-win-x64.exe',
      'aspnetcore-runtime-win-x64.exe',
      'Installing .NET 10 ASP.NET Core Runtime…') then
    begin
      Result := 'Could not install the .NET 10 ASP.NET Core Runtime.';
      Exit;
    end;
  end;
end;
