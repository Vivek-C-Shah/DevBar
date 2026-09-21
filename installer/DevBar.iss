; DevBar installer - single .exe, no admin required, per-user install so it
; just works with a double-click. Creates a Start Menu entry so typing
; "devbar" in Windows Search finds and launches it, matching a normal
; installed Windows app.

#define MyAppName "DevBar"
#define MyAppVersion "0.1.0"
#define MyAppPublisher "DevBar contributors"
#define MyAppURL "https://github.com/your-org/devbar"
#define MyAppExeName "DevBar.exe"

[Setup]
AppId={{9F3B9C1E-6B7E-4B2E-9E7B-1D6C8A0B7F0D}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={localappdata}\Programs\DevBar
DisableProgramGroupPage=yes
; No admin required - installs entirely to the current user's profile,
; so it's a plain double-click-and-go install like the user asked for.
PrivilegesRequired=lowest
OutputDir=..\dist
OutputBaseFilename=DevBar-Setup-{#MyAppVersion}
SetupIconFile=..\src\DevBar\Assets\devbar.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "startupicon"; Description: "Launch DevBar automatically when Windows starts"; GroupDescription: "Startup:"

[Files]
Source: "..\publish\DevBar.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\DevBar"; Filename: "{app}\{#MyAppExeName}"
Name: "{userstartup}\DevBar"; Filename: "{app}\{#MyAppExeName}"; Tasks: startupicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch DevBar now"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{localappdata}\DevBar"
