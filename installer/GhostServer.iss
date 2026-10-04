#define MyAppName "Ghost Server"
#define MyAppVersion GetEnv("GHOST_SERVER_VERSION")
#define MyAppPublisher "Brendigo"
#define MyAppExeName "GhostServer.exe"

[Setup]
AppId={{A4E6DB20-A2B9-46E5-A361-940EF2C46942}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\Ghost Server
DefaultGroupName=Ghost Server
DisableProgramGroupPage=yes
OutputDir=..\artifacts\release
OutputBaseFilename=GhostServer-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\{#MyAppExeName}
SetupLogging=yes
CloseApplications=yes
RestartApplications=no
VersionInfoVersion={#MyAppVersion}
VersionInfoProductName={#MyAppName}
VersionInfoDescription=Ghost Server Setup
VersionInfoCompany={#MyAppPublisher}

[Files]
Source: "..\artifacts\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Ghost Server"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\Ghost Server"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch Ghost Server"; Flags: nowait postinstall skipifsilent
