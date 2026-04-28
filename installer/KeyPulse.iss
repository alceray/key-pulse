; KeyPulse Installer Script
; Build publish output first:
;   dotnet publish -c Release -r win-x64 --no-self-contained -o ..\publish\

#define AppName "KeyPulse"
#define AppVersion "1.0.0"
#define AppPublisher "KeyPulse"
#define AppExeName "KeyPulse.exe"

[Setup]
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppId={{A1B2C3D4-E5F6-7890-ABCD-EF1234567890}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
OutputDir=output
OutputBaseFilename=KeyPulse-Setup-{#AppVersion}
SetupIconFile=..\Assets\keyboard_mouse_icon.ico
Compression=lzma
SolidCompression=yes
WizardStyle=modern
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExeName}
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional icons:"; Flags: unchecked

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{commondesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Code]

function RemoveUserDataPrompt(): Boolean;
begin
  Result :=
    MsgBox(
      'Do you also want to remove KeyPulse user data?' + #13#10 + #13#10 +
      'This deletes:' + #13#10 +
      '- Device history' + #13#10 +
      '- Activity logs' + #13#10 +
      '- Settings' + #13#10 + #13#10 +
      'Location: ' + ExpandConstant('{userappdata}\KeyPulse'),
      mbConfirmation,
      MB_YESNO
    ) = IDYES;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  UserDataPath: string;
begin
  // usPostUninstall means app uninstall already completed successfully
  if CurUninstallStep = usPostUninstall then
  begin
    UserDataPath := ExpandConstant('{userappdata}\KeyPulse');

    if DirExists(UserDataPath) then
    begin
      if RemoveUserDataPrompt() then
      begin
        DelTree(UserDataPath, True, True, True);
        Log('Removed user data directory: ' + UserDataPath);
      end
      else
        Log('User chose to keep data directory: ' + UserDataPath);
    end;
  end;
end;