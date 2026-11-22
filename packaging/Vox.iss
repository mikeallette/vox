; Vox installer (Inno Setup 7). Built by build-release.ps1, which passes the version and the staged app folder:
;   ISCC.exe /DAppVersion=1.0.0 /DStageDir=<stage>\app /DOutDir=<dist> packaging\Vox.iss
; Per-user install, no administrator rights. The user's data folder is never touched by install or uninstall.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef StageDir
  #error StageDir must point at the published app folder
#endif
#ifndef OutDir
  #define OutDir "..\dist"
#endif

[Setup]
AppId={{6E0B9B52-3C0E-4F5B-9D7A-2B5F4C9A7F3A}
AppName=Vox
AppVersion={#AppVersion}
AppVerName=Vox {#AppVersion}
AppPublisher=Mike Allette
AppPublisherURL=https://github.com/mikeallette/vox
AppSupportURL=https://github.com/mikeallette/vox/issues
AppUpdatesURL=https://github.com/mikeallette/vox/releases
AppCopyright=Copyright (c) 2026 Mike Allette. MIT License.
VersionInfoVersion={#AppVersion}
VersionInfoDescription=Vox offline voice dictation setup
DefaultDirName={localappdata}\Programs\Vox
DefaultGroupName=Vox
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir={#OutDir}
OutputBaseFilename=Vox-Setup-{#AppVersion}
SetupIconFile=..\src\Vox\Assets\vox.ico
UninstallDisplayIcon={app}\Vox.exe
UninstallDisplayName=Vox
WizardStyle=modern
Compression=lzma2/normal
SolidCompression=yes
LZMANumBlockThreads=4
AppMutex=Vox.SingleInstance.7f3a
CloseApplications=yes
RestartApplications=no
ChangesAssociations=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
Source: "{#StageDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Vox"; Filename: "{app}\Vox.exe"; Comment: "Offline voice dictation"
Name: "{autodesktop}\Vox"; Filename: "{app}\Vox.exe"; Comment: "Offline voice dictation"; Tasks: desktopicon

[Registry]
; Vox writes this value itself when "Start Vox when I sign in" is on; remove it on uninstall
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "Vox"; Flags: uninsdeletevalue

[Run]
Filename: "{app}\Vox.exe"; Description: "Start Vox now"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; files Vox may create next to itself (imported models live in the data folder, not here)
Type: filesandordirs; Name: "{app}\models"
Type: dirifempty; Name: "{app}"

[Messages]
WelcomeLabel2=This installs Vox [ver], private voice dictation that runs entirely on this PC.%n%nNo account, no cloud and no internet connection are needed. Your speech never leaves this computer.%n%nIt's recommended that you close other applications before continuing.
FinishedLabel=Vox is installed.%n%nHold Ctrl + Win and speak, then let go to type the text where your cursor is. Shortcuts can be changed in Settings.
