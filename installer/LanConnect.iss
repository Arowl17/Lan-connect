; Lan Connect installer (Inno Setup 6).
; Built by CI:  ISCC.exe /DAppVersion=0.1.0 /DPublishDir=<folder with LanConnect.exe> LanConnect.iss

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\publish"
#endif

[Setup]
AppId={{8E3C0E5B-7D0A-4C6E-9B7A-2F4D6C1A9E11}
AppName=Lan Connect
AppVersion={#AppVersion}
AppVerName=Lan Connect {#AppVersion}
AppPublisher=Lan Connect
DefaultDirName={autopf}\Lan Connect
DefaultGroupName=Lan Connect
DisableProgramGroupPage=yes
DisableDirPage=auto
; Admin rights are needed once, to add the Windows Firewall rule.
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=Output
OutputBaseFilename=LanConnect-Setup-{#AppVersion}
SetupIconFile=..\src\LanConnect.App\Assets\app.ico
UninstallDisplayIcon={app}\LanConnect.exe
UninstallDisplayName=Lan Connect
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=force
RestartApplications=no
; The only per-user change is removing our own autostart entry on uninstall.
UsedUserAreasWarning=no

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked

[Files]
Source: "{#PublishDir}\LanConnect.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\Lan Connect"; Filename: "{app}\LanConnect.exe"
Name: "{autodesktop}\Lan Connect"; Filename: "{app}\LanConnect.exe"; Tasks: desktopicon

[Run]
; Allow Lan Connect through Windows Firewall on every network type (home Wi-Fi is often marked "Public").
; Only paired computers can ever control this one; everything is encrypted.
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""Lan Connect"""; Flags: runhidden; StatusMsg: "Configuring Windows Firewall..."
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""Lan Connect"" dir=in action=allow program=""{app}\LanConnect.exe"" enable=yes profile=any"; Flags: runhidden; StatusMsg: "Configuring Windows Firewall..."
; Start as the signed-in user (not elevated), like a normal launch.
Filename: "{app}\LanConnect.exe"; Description: "Start Lan Connect now"; Flags: nowait postinstall skipifsilent runasoriginaluser

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/f /im LanConnect.exe"; Flags: runhidden; RunOnceId: "StopLanConnect"
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""Lan Connect"""; Flags: runhidden; RunOnceId: "RemoveFirewallRule"

[Registry]
; The app registers itself to start with Windows; remove that on uninstall.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "LanConnect"; Flags: uninsdeletevalue dontcreatekey
