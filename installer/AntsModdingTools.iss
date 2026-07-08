; Ant's Modding Tools — Windows installer (AMT-17).
; Build the app first:  pwsh ../publish.ps1   (produces ..\dist\AntsModdingTools\)
; Then compile this with Inno Setup 6:  iscc AntsModdingTools.iss
; Output: installer\Output\AntsModdingTools-Setup.exe (Start-Menu shortcut + uninstaller, per-user, no admin).

#define AppName "Ant's Modding Tools"
#define AppExe "AntsModdingTools.exe"
#define AppPublisher "DegradingAnt"
#define AppUrl "https://github.com/DegradingAnt/Ant-s-modding-tools"
; read the version out of the published exe so the installer version tracks the build
#define AppVersion GetVersionNumbersString("..\dist\AntsModdingTools\" + AppExe)

[Setup]
AppId={{8B0D2F41-3C7A-4E52-9F1A-AMTMODTOOLS01}}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
DefaultDirName={autopf}\AntsModdingTools
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
; per-user install → no admin prompt (matches a modding utility)
PrivilegesRequiredOverridesAllowed=dialog
PrivilegesRequired=lowest
OutputBaseFilename=AntsModdingTools-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\{#AppExe}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional icons:"; Flags: unchecked

[Files]
; the whole self-contained publish output
Source: "..\dist\AntsModdingTools\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent
