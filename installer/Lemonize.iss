; Lemonize installer (Inno Setup 6). Build with publish.ps1, or:
;   ISCC.exe /DAppVersion=1.0.0 installer\Lemonize.iss
; Expects the self-contained app in ..\publish\app (dotnet publish output, including the
; bundled ffmpeg, ghostscript and pandoc folders).

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

[Setup]
AppId={{2FA08F68-8519-4D09-82CE-EC351B790C54}
AppName=Lemonize
AppVersion={#AppVersion}
AppVerName=Lemonize {#AppVersion}
AppPublisher=Lemonize
VersionInfoVersion={#AppVersion}
; Per-user install: no administrator rights, lands in %LOCALAPPDATA%\Programs\Lemonize.
PrivilegesRequired=lowest
DefaultDirName={autopf}\Lemonize
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
OutputDir=..\dist
OutputBaseFilename=Lemonize-Setup-{#AppVersion}
SetupIconFile=..\src\Lemonize\Assets\lemonize.ico
UninstallDisplayIcon={app}\Lemonize.exe
UninstallDisplayName=Lemonize
WizardStyle=modern
Compression=lzma2/ultra64
SolidCompression=yes
LZMAUseSeparateProcess=yes
; Lemonize is stopped by the [Code] below; no Restart Manager prompts.
CloseApplications=no

[Languages]
Name: "italian"; MessagesFile: "compiler:Languages\Italian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
italian.TaskStartup=Avvia Lemonize all'accesso a Windows
english.TaskStartup=Start Lemonize when I sign in
italian.TaskContextMenu=Aggiungi «Lemonize…» al menu del tasto destro di Esplora file
english.TaskContextMenu=Add “Lemonize…” to the File Explorer right-click menu
italian.TaskGroup=Integrazione con Windows:
english.TaskGroup=Windows integration:
italian.LaunchApp=Avvia Lemonize
english.LaunchApp=Launch Lemonize

[Tasks]
Name: "startup"; Description: "{cm:TaskStartup}"; GroupDescription: "{cm:TaskGroup}"
Name: "contextmenu"; Description: "{cm:TaskContextMenu}"; GroupDescription: "{cm:TaskGroup}"
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\publish\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Lemonize"; Filename: "{app}\Lemonize.exe"
Name: "{autodesktop}\Lemonize"; Filename: "{app}\Lemonize.exe"; Tasks: desktopicon

[Run]
; Lemonize applies the chosen integration itself, so its settings match the registry.
Filename: "{app}\Lemonize.exe"; Parameters: "{code:LaunchArgs}"; Description: "{cm:LaunchApp}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{app}\Lemonize.exe"; Parameters: "--quit"; Flags: runhidden waituntilterminated; RunOnceId: "StopLemonize"

[Registry]
; Created by Lemonize at runtime (startup, right-click menu); only removed on uninstall.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "Lemonize"; Flags: uninsdeletevalue dontcreatekey
Root: HKCU; Subkey: "Software\Classes\*\shell\Lemonize"; Flags: uninsdeletekey dontcreatekey
Root: HKCU; Subkey: "Software\Classes\Directory\shell\Lemonize"; Flags: uninsdeletekey dontcreatekey

[Code]
function LaunchArgs(Param: String): String;
begin
  Result := '';
  if WizardIsTaskSelected('startup') then Result := Result + ' --enable-startup';
  if WizardIsTaskSelected('contextmenu') then Result := Result + ' --enable-contextmenu';
  Result := Trim(Result);
end;

// Close a running copy before files are replaced (updates over an existing install).
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
  Exe: String;
begin
  Result := '';
  Exe := ExpandConstant('{app}\Lemonize.exe');
  if FileExists(Exe) then
  begin
    Exec(Exe, '--quit', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Sleep(1500);
  end;
end;

// Silent installs (e.g. /VERYSILENT) skip the [Run] entry, so apply the tasks here as well.
procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if (CurStep = ssPostInstall) and WizardSilent and (LaunchArgs('') <> '') then
    Exec(ExpandConstant('{app}\Lemonize.exe'), LaunchArgs('') + ' --background', '', SW_HIDE, ewNoWait, ResultCode);
end;
