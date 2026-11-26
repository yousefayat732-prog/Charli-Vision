; One-time bridge for the signed legacy updater. No user data is deleted.
#ifndef AppVersion
  #define AppVersion "1.7.0"
#endif
#ifndef PayloadPath
  #error "Define PayloadPath with the verified Velopack installer"
#endif
#ifndef PackageOutput
  #error "Define PackageOutput"
#endif
[Setup]
AppId={{9BCF7A97-1D64-4B3F-94B3-4C2D961208F2}
AppName=Charli Vision
AppVersion={#AppVersion}
AppPublisher=Charli Vision
CreateAppDir=no
Uninstallable=no
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
OutputDir={#PackageOutput}
OutputBaseFilename=CharliVision-{#AppVersion}-Setup-x64
SetupIconFile=..\CharliVision.ico
Compression=lzma2/fast
SolidCompression=yes
WizardStyle=modern
DisableWelcomePage=no
CloseApplications=no
RestartApplications=no
[Languages]
Name: "german"; MessagesFile: "compiler:Languages\German.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"
[Files]
Source: "{#PayloadPath}"; Flags: dontcopy
[Code]
function InitializeSetup(): Boolean;
begin
  Result := True;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Code, Attempts: Integer;
  Existing, Version, OldRun, DataDir, BackupDir, Payload: String;
begin
  if CurStep <> ssPostInstall then Exit;
  Existing := ExpandConstant('{localappdata}\CharliVision.Desktop\current\CharliVision.exe');
  if FileExists(Existing) then begin
    if not GetVersionNumbersString(Existing, Version) then
      RaiseException('Existing installation could not be verified.');
    if Version <> '{#AppVersion}.0' then
      RaiseException('Charli Vision is already installed with Velopack. Use its own update button.');
  end else begin
    Attempts := 0;
    while CheckForMutexes('Local\CharliVisionStandaloneApp') and (Attempts < 100) do begin
      Sleep(200);
      Attempts := Attempts + 1;
    end;
    if CheckForMutexes('Local\CharliVisionStandaloneApp') then
      RaiseException('Please close Charli Vision from the tray and run setup again.');
    DataDir := ExpandConstant('{userappdata}\CharliVision');
    BackupDir := ExpandConstant('{localappdata}\CharliVisionMigrationBackups\') +
      GetDateTimeString('yyyymmddhhnnss', '', '');
    if FileExists(DataDir + '\settings.json') then begin
      if not ForceDirectories(BackupDir) then RaiseException('Settings backup could not be created.');
      if not FileCopy(DataDir + '\settings.json', BackupDir + '\settings.json', True) then
        RaiseException('Settings backup failed.');
    end;
    ExtractTemporaryFile(ExtractFileName('{#PayloadPath}'));
    Payload := ExpandConstant('{tmp}\') + ExtractFileName('{#PayloadPath}');
    if not Exec(Payload, '--silent', '', SW_SHOW, ewWaitUntilTerminated, Code) then
      RaiseException('Velopack installer could not start.');
    if Code <> 0 then RaiseException('Velopack installation failed: ' + IntToStr(Code));
    if not FileExists(Existing) then RaiseException('New executable missing.');
    if not GetVersionNumbersString(Existing, Version) then RaiseException('New version could not be verified.');
    if Version <> '{#AppVersion}.0' then RaiseException('Unexpected installed version: ' + Version);
  end;
  if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'CharliVision', OldRun) then begin
    if Pos('\Apps\CharliVision\CharliVision.exe', OldRun) > 0 then
      if not RegWriteStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'CharliVision', '"' + Existing + '"') then
        RaiseException('Autostart migration failed.');
  end;
  if not Exec(Existing, '', '', SW_SHOWNORMAL, ewNoWait, Code) then
    RaiseException('New app could not start.');
end;
