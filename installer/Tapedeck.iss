; Inno Setup script for Tapedeck. Built by .github/workflows/release.yml:
;   ISCC.exe /DAppVersion=0.1.0 /DSourceDir=..\publish\win-x64 /DArch=x64 installer\Tapedeck.iss
; Per-user install (no admin prompt), Start menu shortcut, clean uninstall.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish\win-x64"
#endif
#ifndef Arch
  #define Arch "x64"
#endif
; Numeric x.y.z for the file's version resource (AppVersion may carry a "-ci.N" suffix).
#ifndef NumericVersion
  #define NumericVersion "0.0.0"
#endif

[Setup]
AppId={{6F0E3C7A-5B8D-4E61-9C3F-2A7D1B4E8C55}
AppName=Tapedeck
AppVersion={#AppVersion}
AppVerName=Tapedeck {#AppVersion}
AppPublisher=Tapedeck contributors
AppPublisherURL=https://github.com/bhaiyatu/tapedeck
AppSupportURL=https://github.com/bhaiyatu/tapedeck/issues
AppUpdatesURL=https://github.com/bhaiyatu/tapedeck/releases
DefaultDirName={localappdata}\Programs\Tapedeck
DefaultGroupName=Tapedeck
DisableProgramGroupPage=yes
DisableDirPage=yes
PrivilegesRequired=lowest
OutputDir=..\dist
OutputBaseFilename=Tapedeck-{#AppVersion}-{#Arch}-setup
SetupIconFile=..\src\Tapedeck\Assets\Tapedeck.ico
UninstallDisplayIcon={app}\Tapedeck.exe
UninstallDisplayName=Tapedeck
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
MinVersion=10.0.19041
#if Arch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif
CloseApplications=yes
RestartApplications=no
LicenseFile=..\LICENSE
; Shown before installing: what Tapedeck connects to and stores (SignPath Foundation requires this).
InfoBeforeFile=privacy-notice.txt
; Product name / version metadata on setup.exe (checked by SignPath before signing).
VersionInfoProductName=Tapedeck
; Binary product version must be numeric; the text version may carry a "-ci.N" suffix.
VersionInfoProductVersion={#NumericVersion}
VersionInfoProductTextVersion={#AppVersion}
VersionInfoVersion={#NumericVersion}
VersionInfoDescription=Tapedeck installer
VersionInfoCompany=Tapedeck contributors
VersionInfoCopyright=Copyright (c) 2026 bhaiyatu (MIT License)

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Tapedeck"; Filename: "{app}\Tapedeck.exe"
Name: "{autodesktop}\Tapedeck"; Filename: "{app}\Tapedeck.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Tapedeck.exe"; Description: "Launch Tapedeck"; Flags: nowait postinstall skipifsilent

[Code]
// On uninstall, offer to delete settings and the stored Google session too.
// Default is "Yes" so nothing sign-in related is left behind by accident.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    DataDir := ExpandConstant('{localappdata}\Tapedeck');
    if DirExists(DataDir) then
    begin
      if UninstallSilent or
         (MsgBox('Also remove your Tapedeck settings and sign-in from this PC?' + #13#10#13#10 +
                 'Choose No to keep them for a future reinstall.',
                 mbConfirmation, MB_YESNO or MB_DEFBUTTON1) = IDYES) then
        DelTree(DataDir, True, True, True);
    end;
  end;
end;
