; Official Inno Setup compiler 7.1.0. Paths and version come from scripts/Package.ps1.
#ifndef ProjectRoot
  #error ProjectRoot is required
#endif
#ifndef AppDir
  #error AppDir is required
#endif
#ifndef AppVersion
  #error AppVersion is required
#endif
#ifndef RepositoryUrl
  #define RepositoryUrl "https://github.com/gothamkismet-cyber/ScreenTranslator"
#endif

[Setup]
AppId={{DB573468-89A6-4D5F-B4D3-64284DBD1E32}
AppName=屏幕 AI 翻译
AppVersion={#AppVersion}
AppPublisher=gothamkismet-cyber
AppPublisherURL={#RepositoryUrl}
AppSupportURL={#RepositoryUrl}/issues
AppUpdatesURL={#RepositoryUrl}/releases
DefaultDirName={localappdata}\Programs\ScreenTranslator
DefaultGroupName=屏幕 AI 翻译
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible and not arm64
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
SetupArchitecture=x64
OutputBaseFilename=ScreenTranslator-{#AppVersion}-win-x64-Setup
SetupIconFile={#ProjectRoot}\src\ScreenTranslator.App\Assets\AppIcon.ico
UninstallDisplayIcon={app}\ScreenTranslator.exe
WizardStyle=modern
DisableProgramGroupPage=yes
DisableWelcomePage=no
AllowNoIcons=yes
Compression=lzma2/normal
SolidCompression=yes
CloseApplications=no
RestartApplications=no
ChangesEnvironment=no
VersionInfoVersion={#AppVersion}.0
VersionInfoDescription=屏幕 AI 翻译安装程序

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#AppDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\屏幕 AI 翻译"; Filename: "{app}\ScreenTranslator.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\屏幕 AI 翻译"; Filename: "{app}\ScreenTranslator.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\ScreenTranslator.exe"; Description: "启动屏幕 AI 翻译"; Flags: nowait postinstall skipifsilent

; Settings live outside {app}. Uninstall deliberately leaves the user's saved connections intact.
