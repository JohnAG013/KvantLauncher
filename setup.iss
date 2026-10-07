[Setup]
AppId={{KVANT-LAUNCHER-UUID}}
AppName=KVANT Launcher
AppVersion=1.2.0_Alpha
; Ставим в локальную папку пользователя: не нужны права админа,
; не пишем в системные папки, SmartScreen и антивирусы меньше триггерятся
DefaultDirName={localappdata}\KVANTLauncher
DefaultGroupName=KVANT Launcher
UninstallDisplayIcon={app}\logo.ico
SetupIconFile=logo.ico
Compression=lzma2/ultra64
SolidCompression=yes
OutputDir=Installer
OutputBaseFilename=KVANT_Launcher_Setup
; lowest = установка без требований администратора
PrivilegesRequired=lowest

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
    
    // Папки 'saves' и 'screenshots' не трогаем по просьбе пользователя
    // mods, resourcepacks и shaderpacks пользователя также не удаляем
    // Пользователь копал эти файлы часами, удалять их при деинсталляции недопустимо

    // Удаляем только системные папки лаунчера
    DelTree(AppDir + '\versions', True, True, True);
    DelTree(AppDir + '\libraries', True, True, True);
    DelTree(AppDir + '\assets', True, True, True);
    DelTree(AppDir + '\runtime', True, True, True);
    // DelTree(AppDir + '\logs', True, True, True); // оставляем логи для чистоты

    // Конфиги удаляем
    DeleteFile(AppDir + '\launcher_config.json');
    DeleteFile(AppDir + '\dev_log.txt');

    // Корневую папку удаляем только если пользователь удалил миры/скрины вручную
    RemoveDir(AppDir);
  end;
end;