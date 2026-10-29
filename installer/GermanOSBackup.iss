#define AppName "German OS Backup"
#ifndef AppVersion
  #define AppVersion "1.4.0"
#endif
#define AppPublisher "German-GPD"
#define AppExeName "GermanOSBackup.exe"

[Setup]
AppId={{C6D15D15-0D80-4A1A-9D4D-37C3DB6E2F19}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\German OS Backup
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesInstallIn64BitMode=x64
OutputDir=output
OutputBaseFilename=GermanOSBackup-Setup-x64
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\{#AppExeName}
CloseApplications=yes
RestartApplications=no

[Files]
Source: "..\publish\GermanOSBackup.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\publish\german_os_backup_engine.dll"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\German OS Backup"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\German OS Backup"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Создать ярлык на рабочем столе"; GroupDescription: "Дополнительные ярлыки:"; Flags: unchecked

[Run]
Filename: "{app}\{#AppExeName}"; Parameters: "--first-run"; Description: "Запустить German OS Backup"; Flags: postinstall nowait skipifsilent runasoriginaluser