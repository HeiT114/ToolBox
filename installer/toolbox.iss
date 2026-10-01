; ═══════════════════════════════════════════════════════════════════
; Toolbox 1.0.0-beta 安装器（Inno Setup 6）
; 由 scripts/make-installer.ps1 调用 ISCC.exe 编译；参数：
;   /DSourceDir=<已发布的自包含目录>   （默认 ..\artifacts\dist\Toolbox-win-x64）
;   /DMyAppVersion=<语义化版本>         （默认 1.0.0-beta）
;   /DSetupIcon=<app.ico 绝对路径>      （默认 ..\src\Toolbox.App\Assets\app.ico）
; ═══════════════════════════════════════════════════════════════════

#ifndef SourceDir
  #define SourceDir "..\artifacts\dist\Toolbox-win-x64"
#endif
#ifndef MyAppVersion
  #define MyAppVersion "1.0.0-beta"
#endif
#ifndef SetupIcon
  #define SetupIcon "..\src\Toolbox.App\Assets\app.ico"
#endif

#define MyAppName "Toolbox 插件化工具箱"
#define MyAppExeName "Toolbox.App.exe"

[Setup]
; 固定 AppId：升级安装时覆盖旧版本（卸载项也以此为准）
AppId={{8B21C7A4-59D3-4E6F-9A1B-2F4D6E8C0A71}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher=Toolbox
; 文件属性里的版本号必须是数字格式；对外的 1.0.0-beta 由上面的 AppVersion 承载
VersionInfoVersion=1.0.0.0
VersionInfoProductVersion=1.0.0.0
VersionInfoDescription=Toolbox 插件化工具箱 安装程序
DefaultDirName={autopf}\Toolbox
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=..\artifacts\dist
OutputBaseFilename=Toolbox-Setup-{#MyAppVersion}
SetupIconFile={#SetupIcon}
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
Compression=lzma2
SolidCompression=yes
CloseApplications=yes
RestartApplications=no
; 自包含发布：不需要额外下载 .NET

[Languages]
; 官方 Inno 发行包不带中文：用仓库自带的官方简体中文翻译
; （取自 Inno 仓库 Files/Languages/Unofficial/ChineseSimplified.isl）。
Name: "chs"; MessagesFile: "ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; Flags: unchecked

[Files]
; 自包含发布目录整棵装进去（含随包插件 plugins/Toolbox.Plugin.SysInfo）
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "启动 {#MyAppName}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; 运行期产生的机器状态随卸载清掉；用户自己装的 plugins/ 保留（那是用户数据）
Type: filesandordirs; Name: "{app}\logs"
Type: filesandordirs; Name: "{app}\plugin-data"
Type: filesandordirs; Name: "{app}\plugins\.shadow"
Type: files; Name: "{app}\*.zip"
