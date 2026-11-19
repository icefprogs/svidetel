[Setup]
AppId={{ED6740E0-1E8B-4AB3-970D-9939E30F35A0}
AppName=Свидетель
AppVersion={#AppVersion}
AppPublisher=icefprogs
AppPublisherURL=https://github.com/icefprogs/svidetel
DefaultDirName={localappdata}\Programs\Svidetel
DefaultGroupName=Свидетель
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19045
WizardStyle=modern
SetupIconFile=..\Svidetel\Assets\Witness.ico
UninstallDisplayIcon={app}\Svidetel.exe
OutputDir={#InstallerOutput}
OutputBaseFilename=Svidetel-Setup-x64
Compression=lzma2
SolidCompression=yes
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userprograms}\Свидетель"; Filename: "{app}\Svidetel.exe"; WorkingDir: "{app}"
Name: "{userdesktop}\Свидетель"; Filename: "{app}\Svidetel.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\Svidetel.exe"; WorkingDir: "{app}"; Description: "{cm:LaunchProgram,Свидетель}"; Flags: nowait postinstall skipifsilent
