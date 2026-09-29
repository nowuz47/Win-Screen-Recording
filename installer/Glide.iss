#ifndef AppVersion
  #define AppVersion "0.1.0-preview.3"
#endif
#ifndef PublishDir
  #define PublishDir "..\out\app"
#endif
#ifndef PackageDir
  #define PackageDir "..\out"
#endif

[Setup]
AppId={{9BC1F2E4-B013-4B85-93BF-1DECD7F54952}
AppName=Glide
AppVersion={#AppVersion}
AppPublisher=Glide
AppPublisherURL=https://github.com/nowuz47/Win-Screen-Recording
AppSupportURL=https://github.com/nowuz47/Win-Screen-Recording/issues
DefaultDirName={localappdata}\Programs\Glide
DefaultGroupName=Glide
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.22000
OutputDir={#PackageDir}
OutputBaseFilename=Glide-Setup-{#AppVersion}-x64
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\Glide.App.exe
; Never automatically close a recording session or restart the computer.
CloseApplications=no
RestartApplications=no
RestartIfNeededByRun=no
SetupLogging=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Glide"; Filename: "{app}\Glide.App.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\Glide"; Filename: "{app}\Glide.App.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\Glide.App.exe"; Description: "{cm:LaunchProgram,Glide}"; Flags: nowait postinstall skipifsilent

; No UninstallDelete entries: recordings and preferences outside {app} are preserved.
