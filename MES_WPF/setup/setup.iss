; ============================================================
; F12 AI Vision 安装脚本
; ============================================================

#define MyAppName "F12 AI Vision"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "wang.gan.Ipeak"
#define MyAppExeName "MES_WPF.exe"

[Setup]
AppId={{dd4144e4-01ba-4f94-8d9f-002aa8048c02}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
OutputDir=Output
OutputBaseFilename=F12_AI_Vision_Setup_{#MyAppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
SetupIconFile=..\Resources\AIT.ico

[Files]
; 主程序文件
Source: "..\publish\*"; DestDir: "{app}"; Flags: recursesubdirs

; VC++ 运行库（释放到临时目录，安装后自动删除）
Source: "vc_redist.x64.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\{#MyAppExeName}"

[Run]
; 先安装 VC++ 运行库
Filename: "{tmp}\vc_redist.x64.exe"; Parameters: "/install /quiet /norestart"; StatusMsg: "正在安装 Microsoft Visual C++ 运行库..."; Flags: waituntilterminated

; 再启动主程序
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent