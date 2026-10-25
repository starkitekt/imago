; Imago installer (Inno Setup 6). Built by build.ps1 from the dist folder.
; Per-user install: no administrator rights needed, nothing written to Program Files.

#ifndef DistDir
  #define DistDir "..\dist"
#endif
#ifndef Suffix
  #define Suffix ""
#endif
#define AppVersion "1.0.0"

[Setup]
AppId={{8F3C2B1E-6A4D-4E9B-9C7F-2D5A1B0E4C36}
AppName=Imago
AppVersion={#AppVersion}
AppVerName=Imago {#AppVersion}
AppPublisher=Abhishek
AppCopyright=Copyright 2026 Abhishek
AppComments=Screenshots you can verify.
DefaultDirName={autopf}\Imago
DefaultGroupName=Imago
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
OutputDir=out
OutputBaseFilename=Imago-Setup-{#AppVersion}{#Suffix}
SetupIconFile=..\app\Imago.ico
UninstallDisplayIcon={app}\Imago.exe
UninstallDisplayName=Imago
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
VersionInfoVersion={#AppVersion}.0
VersionInfoProductName=Imago
VersionInfoCopyright=Copyright 2026 Abhishek

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked
Name: "startup"; Description: "Start Imago when I sign in to Windows"; Flags: unchecked

[Files]
Source: "{#DistDir}\Imago.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#DistDir}\snapcore.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#DistDir}\Lato-OFL.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#DistDir}\onnxruntime.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#DistDir}\onnxruntime_providers_shared.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#DistDir}\onnxruntime-LICENSE.txt"; DestDir: "{app}"; Flags: ignoreversion

[Dirs]
; Captures land here. Left in place on uninstall, and Windows keeps any images still in it.
Name: "{%USERPROFILE}\Screenshots"

[Icons]
Name: "{group}\Imago"; Filename: "{app}\Imago.exe"
Name: "{group}\Uninstall Imago"; Filename: "{uninstallexe}"
Name: "{userdesktop}\Imago"; Filename: "{app}\Imago.exe"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Imago"; ValueData: """{app}\Imago.exe"""; Flags: uninsdeletevalue; Tasks: startup

[Run]
Filename: "{app}\Imago.exe"; Description: "Launch Imago"; Flags: nowait postinstall skipifsilent
