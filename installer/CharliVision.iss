; CHARLI VISION standalone Windows x64 installer
; Builds only the reviewed self-contained publish payload; never includes user data.
#define AppVersion "1.1.0"
#define AppName "Charli Vision"
#define AppExe "CharliVision.exe"
[Setup]
AppId={{7CD0B915-E8B2-4997-9F27-2B21FE340B70}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Charli Vision
AppPublisherURL=https://github.com/yousefayat732-prog/Charli-Vision
AppSupportURL=https://github.com/yousefayat732-prog/Charli-Vision/issues
DefaultDirName={%USERPROFILE}\Apps\CharliVision
DefaultGroupName=Charli Vision
OutputDir=..\Distribution
OutputBaseFilename=CharliVision-{#AppVersion}-Setup-x64
SetupIconFile=..\CharliVision.ico
UninstallDisplayIcon={app}\CharliVision.exe
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
WizardStyle=modern
Compression=lzma2
SolidCompression=yes
CloseApplications=yes
RestartApplications=no
DisableProgramGroupPage=yes
DisableWelcomePage=no
AllowNoIcons=yes
[Languages]
Name: "german"; MessagesFile: "compiler:Languages\German.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"
[Tasks]
Name: "desktopicon"; Description: "Desktop-Verknüpfung erstellen"; GroupDescription: "Zusätzliche Verknüpfungen:"; Flags: unchecked
[Files]
Source: "..\dist\win-x64\CharliVision.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\CharliVision.ico"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\THIRD_PARTY_NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion
[Icons]
Name: "{autoprograms}\Charli Vision"; Filename: "{app}\CharliVision.exe"; IconFilename: "{app}\CharliVision.ico"
Name: "{autodesktop}\Charli Vision"; Filename: "{app}\CharliVision.exe"; IconFilename: "{app}\CharliVision.ico"; Tasks: desktopicon
[Run]
Filename: "{app}\CharliVision.exe"; Description: "Charli Vision starten"; Flags: nowait postinstall skipifsilent
; Personal settings and user-corrected lyrics remain in %APPDATA%\CharliVision.
; Inno Setup uninstalls only files installed by this script.
