; VpnUs — установщик (Inno Setup 6.3+)
; Сборка: build\build-packages.ps1 (или: iscc /DAppVersion=1.0.0 /DArch=x64 /DSourceDir=... installer\VpnUs.iss)

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef Arch
  #define Arch "x64"
#endif
#ifndef SourceDir
  #define SourceDir "..\dist\staging"
#endif

[Setup]
AppId={{9F2A1C64-4E7B-4C58-9E2D-7D5C31B6A901}
AppName=VpnUs
AppVersion={#AppVersion}
AppVerName=VpnUs {#AppVersion}
AppPublisher=Darber155
AppPublisherURL=https://github.com/Darber155/vpnus_desktop
AppSupportURL=https://github.com/Darber155/vpnus_desktop/issues
AppUpdatesURL=https://github.com/Darber155/vpnus_desktop/releases
DefaultDirName={autopf}\VpnUs
DisableDirPage=auto
DefaultGroupName=VpnUs
DisableProgramGroupPage=yes
OutputDir=..\dist
OutputBaseFilename=VpnUs-Setup-{#AppVersion}-{#Arch}
SetupIconFile=vpnus.ico
UninstallDisplayIcon={app}\VpnUs.exe
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
; Автозапуск пишется в HKCU текущего (повышающего права) пользователя — это ожидаемое поведение.
UsedUserAreasWarning=no
#if Arch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "autostart"; Description: "Запускать VpnUs при входе в Windows (свёрнутым в трей)"; GroupDescription: "Автозапуск:"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\VpnUs"; Filename: "{app}\VpnUs.exe"
Name: "{group}\{cm:UninstallProgram,VpnUs}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\VpnUs"; Filename: "{app}\VpnUs.exe"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "VpnUs"; \
    ValueData: """{app}\VpnUs.exe"" --tray"; Tasks: autostart; Flags: uninsdeletevalue

[Run]
; Служба ставится от администратора и работает от LocalSystem (TUN/Wintun требуют прав).
Filename: "{app}\service\VpnUs.Service.exe"; Parameters: "install"; StatusMsg: "Устанавливаю службу VpnUs…"; \
    Flags: runhidden waituntilterminated
Filename: "{app}\VpnUs.exe"; Description: "{cm:LaunchProgram,VpnUs}"; Flags: nowait postinstall skipifsilent
; Тихая установка (самообновление): перезапускаем UI после обновления.
Filename: "{app}\VpnUs.exe"; Flags: nowait; Check: WizardSilent

[UninstallRun]
Filename: "{app}\service\VpnUs.Service.exe"; Parameters: "uninstall"; Flags: runhidden waituntilterminated; \
    RunOnceId: "VpnUsStopService"

[UninstallDelete]
Type: filesandordirs; Name: "{app}"
