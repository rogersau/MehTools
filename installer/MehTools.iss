; Inno Setup 6 script for Meh Tools.
; Publish the self-contained app first, then compile this script:
;   dotnet publish src/JamesTools/JamesTools.csproj -c Release -r win-x64 --self-contained true -p:DebugType=None -o artifacts/publish
;   iscc /DAppVersion=0.3.0 installer/MehTools.iss

#define AppName "Meh Tools"
#define AppExe "MehTools.exe"

#ifndef AppVersion
#define AppVersion "0.0.0"
#endif

[Setup]
AppId={{6F2A9C31-4B7D-4E58-9C42-D1E0F3A5B6C8}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
MinVersion=10.0
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExe}
LicenseFile=..\LICENSE
SetupIconFile=..\src\JamesTools\Assets\MehTools.ico
OutputDir=..\artifacts\installer
OutputBaseFilename=MehTools-setup-{#AppVersion}-win-x64
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\artifacts\publish\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; The app manages its own "Start with Windows" entry (StartupManager); make sure it does not survive uninstall.
Filename: "reg.exe"; Parameters: "delete HKCU\Software\Microsoft\Windows\CurrentVersion\Run /v JamesTools /f"; Flags: runhidden; RunOnceId: "RemoveAutoStart"
