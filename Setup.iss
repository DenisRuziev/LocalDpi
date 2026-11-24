#ifndef AppExe
  #define AppExe "bin\Release\net8.0-windows\win-x64\publish\LocalDpi.exe"
#endif

[Setup]
AppId={{8911893D-C4EF-4FB4-B86D-BC72983F430E}
AppName=Local DPI
#ifndef AppVersion
  #define AppVersion "1.5.0"
#endif
AppVersion={#AppVersion}
SetupIconFile=LocalDpi.ico
DefaultDirName={localappdata}\Programs\Local DPI
DefaultGroupName=Local DPI
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\LocalDpi.exe
PrivilegesRequired=lowest
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0
OutputBaseFilename=LocalDpi-Setup
Compression=lzma2/fast
SolidCompression=yes
WizardStyle=modern
SetupLogging=yes

[Languages]
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: checkedonce

[Files]
Source: "{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "LICENSE"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\Local DPI"; Filename: "{app}\LocalDpi.exe"
Name: "{group}\{cm:UninstallProgram,Local DPI}"; Filename: "{uninstallexe}"
Name: "{userdesktop}\Local DPI"; Filename: "{app}\LocalDpi.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\LocalDpi.exe"; Description: "{cm:LaunchProgram,Local DPI}"; Flags: nowait postinstall skipifsilent
Filename: "{app}\LocalDpi.exe"; Flags: nowait skipifnotsilent runasoriginaluser; Check: IsAutomaticUpdate

[Code]
function IsAutomaticUpdate(): Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 1 to ParamCount do
    if CompareText(ParamStr(I), '/LOCALDPIUPDATE') = 0 then
      Result := True;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Code: Integer;
  LogPath: String;
  Detail: AnsiString;
begin
  Result := '';
  LogPath := ExpandConstant('{tmp}\engine-install-error.txt');
  try
    ExtractTemporaryFile('LocalDpi.exe');
    if not Exec(ExpandConstant('{tmp}\LocalDpi.exe'), '--install-engine "' + LogPath + '"',
         '', SW_HIDE, ewWaitUntilTerminated, Code) then
      Result := 'Could not start the engine installer.'
    else if Code <> 0 then
    begin
      Result := 'Could not install the verified zapret2 engine.';
      if LoadStringFromFile(LogPath, Detail) then Log(Detail);
    end;
  except
    Result := GetExceptionMessage;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Value: String;
  RunKey: String;
begin
  RunKey := 'Software\Microsoft\Windows\CurrentVersion\Run';
  if (CurUninstallStep = usUninstall) and
     RegQueryStringValue(HKCU, RunKey, 'LocalDpi', Value) and
     (CompareText(Value, '"' + ExpandConstant('{app}\LocalDpi.exe') + '" --tray') = 0) then
    RegDeleteValue(HKCU, RunKey, 'LocalDpi');
end;
