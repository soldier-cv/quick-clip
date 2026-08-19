#define MyAppName "QuickClip"
#ifndef MyAppVersion
  #define MyAppVersion GetEnv("QUICKCLIP_VERSION")
#endif
#if MyAppVersion == ""
  #define MyAppVersion "1.0.0"
#endif
#define MyAppPublisher "soldier-cv"
#define MyAppURL "https://github.com/soldier-cv/quick-clip"
#define MyAppExeName "QuickClip.exe"

[Setup]
AppId={{A3F8C2E1-7B94-4D56-9E21-8C4B0A1D5F27}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
DefaultDirName={autopf}\QuickClip
DefaultGroupName=QuickClip
DisableProgramGroupPage=yes
LicenseFile=..\LICENSE
OutputDir=..\publish\setup
OutputBaseFilename=QuickClip-Setup-win-x64
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
WizardStyle=modern
SetupIconFile=..\src\QuickClip\Assets\quickclip.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
VersionInfoVersion={#MyAppVersion}.0
UsePreviousAppDir=yes
CloseApplications=yes
RestartApplications=no
MinVersion=10.0.17763

[Languages]
Name: "chinesesimp"; MessagesFile: "languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\publish\fdd\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "QuickClip.installed"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
; 同用户 UAC：把已有 Run 键改到本次 {app}。跨用户提权写不到原用户 hive，由安装后进程再迁。
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "QuickClip"; ValueData: """{app}\{#MyAppExeName}"" --autostart"; Flags: uninsdeletevalue; Check: HasAutoStartValue

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall

[Code]
function DotNetDesktop8Installed: Boolean;
var
  FindRec: TFindRec;
begin
  Result := FindFirst(ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App\8.*'), FindRec);
  if Result then
    FindClose(FindRec);
end;

function HasAutoStartValue: Boolean;
begin
  Result := RegValueExists(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'QuickClip');
end;

procedure TaskKillQuickClip;
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM QuickClip.exe /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

function InitializeSetup: Boolean;
var
  ErrCode: Integer;
begin
  Result := True;
  if not DotNetDesktop8Installed then
  begin
    if WizardSilent then
    begin
      Result := False;
      Exit;
    end;
    if MsgBox('QuickClip 需要 64 位 .NET 8 桌面运行时。是否打开官方下载页？', mbConfirmation, MB_YESNO) = IDYES then
      ShellExec('open', 'https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe', '', '', SW_SHOWNORMAL, ewNoWait, ErrCode);
    Result := False;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  NeedsRestart := False;
  Result := '';
  { 1.0.0 关窗口只隐藏，不杀则无法覆盖 exe }
  TaskKillQuickClip;
end;
