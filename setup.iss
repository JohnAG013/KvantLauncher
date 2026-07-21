[Setup]
AppId={{KVANT-LAUNCHER-UUID}}
AppName=KVANT Launcher
AppVersion=1.1.0_Alpha
; Ставим в ProgramData, чтобы не было проблем с правами при скачивании модов
DefaultDirName={commonappdata}\KVANTLauncher
DefaultGroupName=KVANT Launcher
UninstallDisplayIcon={app}\logo.ico
SetupIconFile=logo.ico
Compression=lzma2/ultra64
SolidCompression=yes
OutputDir=Installer
OutputBaseFilename=KVANT_Launcher_Setup
PrivilegesRequired=admin

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Tasks]
Name: "desktopicon"; Description: "Создать ярлык на рабочем столе"; GroupDescription: "Дополнительно:";

[Files]
; Копируем все файлы приложения
Source: "bin\Release\net8.0-windows\win-x64\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "icon.png"; DestDir: "{app}"; Flags: ignoreversion
Source: "logo_green.png"; DestDir: "{app}"; Flags: ignoreversion
Source: "bg_minecraft_launcher.png"; DestDir: "{app}"; Flags: ignoreversion
Source: "logo.ico"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\KVANT Launcher"; Filename: "{app}\KVANTLauncher.exe"; IconFilename: "{app}\logo.ico"
Name: "{autodesktop}\KVANT Launcher"; Filename: "{app}\KVANTLauncher.exe"; Tasks: desktopicon; IconFilename: "{app}\logo.ico"

[Run]
Filename: "{app}\KVANTLauncher.exe"; Description: "Запустить лаунчер"; Flags: nowait postinstall skipifsilent

[Code]
// Процедура удаления вызывается в самом конце
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  AppDir: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    AppDir := ExpandConstant('{app}');
    
    // Автоматическое удаление всех файлов кроме миров и скриншотов
    DelTree(AppDir + '\versions', True, True, True);
    DelTree(AppDir + '\libraries', True, True, True);
    DelTree(AppDir + '\assets', True, True, True);
    DelTree(AppDir + '\runtime', True, True, True);
    DelTree(AppDir + '\mods', True, True, True);
    DelTree(AppDir + '\logs', True, True, True);
    DelTree(AppDir + '\resourcepacks', True, True, True);
    DelTree(AppDir + '\shaderpacks', True, True, True);
    
    // Удаление конфигов
    DeleteFile(AppDir + '\launcher_config.json');
    DeleteFile(AppDir + '\dev_log.txt');
    
    // Папки 'saves' и 'screenshots' не трогаем по просьбе пользователя
    
    // Попытка удалить корневую папку (сработает только если вручную удалены миры/скрины)
    RemoveDir(AppDir);
  end;
end;