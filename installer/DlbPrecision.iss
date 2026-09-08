#ifndef StageDir
  #error StageDir must point to the staged build directory
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts\installer"
#endif
#ifndef AppVersion
  #define AppVersion "0.1.1"
#endif
#ifdef SignRelease
  #define InstallationNotes "SIGNED-INSTALLATION-NOTES.txt"
#else
  #define InstallationNotes "INSTALLATION-NOTES.txt"
#endif

[Setup]
AppId={{DBF8351F-D443-4C82-9B8B-93278CC1D4E8}
AppName=DLB Precision Monitor
AppVersion={#AppVersion}
AppPublisher=DLBPrecision
AppPublisherURL=https://www.dlbprecision.com/
DefaultDirName={autopf}\DLB Precision Monitor
DisableDirPage=yes
UsePreviousAppDir=no
DefaultGroupName=DLB Precision Monitor
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.22000
WizardStyle=modern
OutputDir={#OutputDir}
OutputBaseFilename=DLB-Precision-Monitor-{#AppVersion}-Setup
Compression=lzma2
SolidCompression=yes
SetupLogging=yes
SetupMutex=DLBPrecisionMonitorSetup
CloseApplications=yes
CloseApplicationsFilter=*.exe,*.dll
RestartApplications=no
UninstallDisplayIcon={app}\DlbPrecision.Monitor.exe
SetupIconFile=..\src\DlbPrecision.Monitor\Assets\monitor.ico
InfoBeforeFile={#InstallationNotes}
#ifdef SignRelease
  #ifndef SignedUninstallerDir
    #error SignedUninstallerDir is required for signed releases
  #endif
SignTool=DlbArtifact
SignedUninstaller=yes
SignedUninstallerDir={#SignedUninstallerDir}
#endif

[Tasks]
Name: startup; Description: "Launch DLB Precision Monitor when I sign in"; Flags: checkedonce
Name: desktopicon; Description: "Create a desktop shortcut"

[Files]
Source: "{#StageDir}\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "vendor\PawnIO_setup.exe"; DestDir: "{tmp}"; Flags: dontcopy
Source: "{#InstallationNotes}"; DestDir: "{app}"; DestName: "INSTALLATION-NOTES.txt"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\DLB Precision Monitor"; Filename: "{app}\DlbPrecision.Monitor.exe"
Name: "{autodesktop}\DLB Precision Monitor"; Filename: "{app}\DlbPrecision.Monitor.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\DlbPrecision.Monitor.exe"; Parameters: "--from-installer"; Description: "Open DLB Precision Monitor"; Flags: postinstall nowait skipifsilent runasoriginaluser

[Code]
const
  SensorService = 'DlbPrecisionSensors';
  PawnIOHash = '1f519a22e47187f70a1379a48ca604981c4fcf694f4e65b734aaa74a9fba3032';
  SC_MANAGER_CONNECT = $0001;
  SERVICE_QUERY_STATUS = $0004;
  SERVICE_STOPPED = 1;
  SERVICE_RUNNING = 4;

type
  TServiceStatus = record
    ServiceType: Cardinal;
    CurrentState: Cardinal;
    ControlsAccepted: Cardinal;
    Win32ExitCode: Cardinal;
    ServiceSpecificExitCode: Cardinal;
    CheckPoint: Cardinal;
    WaitHint: Cardinal;
  end;

function OpenSCManager(MachineName, DatabaseName: Integer; DesiredAccess: Cardinal): THandle;
  external 'OpenSCManagerW@advapi32.dll stdcall';
function OpenService(Manager: THandle; ServiceName: String; DesiredAccess: Cardinal): THandle;
  external 'OpenServiceW@advapi32.dll stdcall';
function QueryServiceStatus(Service: THandle; var Status: TServiceStatus): Boolean;
  external 'QueryServiceStatus@advapi32.dll stdcall';
function CloseServiceHandle(Handle: THandle): Boolean;
  external 'CloseServiceHandle@advapi32.dll stdcall';

var
  PawnIORebootRequired: Boolean;
  ServiceStoppedForUpgrade: Boolean;
  StartupFailed: Boolean;

function ServiceState: Cardinal;
var
  Manager, Service: THandle;
  Status: TServiceStatus;
begin
  Result := 0;
  Manager := OpenSCManager(0, 0, SC_MANAGER_CONNECT);
  if Manager = 0 then Exit;
  try
    Service := OpenService(Manager, SensorService, SERVICE_QUERY_STATUS);
    if Service = 0 then Exit;
    try
      if QueryServiceStatus(Service, Status) then Result := Status.CurrentState;
    finally
      CloseServiceHandle(Service);
    end;
  finally
    CloseServiceHandle(Manager);
  end;
end;

function RunSC(Arguments: String): Integer;
begin
  if not Exec(ExpandConstant('{sys}\sc.exe'), Arguments, '', SW_HIDE,
    ewWaitUntilTerminated, Result) then Result := -1;
  Log('Service command exit code: ' + IntToStr(Result));
end;

function WaitForService(ExpectedState: Cardinal): Boolean;
var
  Attempt: Integer;
begin
  Result := False;
  for Attempt := 1 to 100 do begin
    if ServiceState = ExpectedState then begin
      Result := True;
      Exit;
    end;
    Sleep(200);
  end;
end;

function StopSensorService: Boolean;
var
  State: Cardinal;
begin
  State := ServiceState;
  Result := True;
  if (State = 0) or (State = SERVICE_STOPPED) then Exit;
  RunSC('stop ' + SensorService);
  Result := WaitForService(SERVICE_STOPPED);
end;

function PawnIOFootprintExists: Boolean;
begin
  Result := RegKeyExists(HKLM64, 'SYSTEM\CurrentControlSet\Services\PawnIO') or
    RegKeyExists(HKLM64, 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO') or
    RegKeyExists(HKLM32, 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO') or
    FileExists(ExpandConstant('{commonpf64}\PawnIO\PawnIOLib.dll'));
end;

function PawnIORegistered: Boolean;
var
  InstallLocation: String;
begin
  InstallLocation := ExpandConstant('{commonpf64}\PawnIO');
  if not RegQueryStringValue(HKLM64, 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO',
    'InstallLocation', InstallLocation) then
    RegQueryStringValue(HKLM32, 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO',
      'InstallLocation', InstallLocation);
  { Registration is not a claim that all sensors work; the service validates access at runtime. }
  Result := RegKeyExists(HKLM64, 'SYSTEM\CurrentControlSet\Services\PawnIO') and
    FileExists(AddBackslash(InstallLocation) + 'PawnIOLib.dll');
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ExitCode: Integer;
  DriverPath: String;
begin
  Result := '';
  { A LocalSystem service must never execute out of a user-writable install folder. }
  if CompareText(RemoveBackslashUnlessRoot(ExpandConstant('{app}')),
    ExpandConstant('{commonpf64}\DLB Precision Monitor')) <> 0 then begin
    Result := 'DLB Precision Monitor must be installed in its protected Program Files location. Remove any /DIR override and run setup again.';
    Exit;
  end;
  if PawnIOFootprintExists and not PawnIORegistered then begin
    Result := 'An incomplete PawnIO installation was found. Repair that shared driver with its official installer, then retry DLB setup. DLB has not replaced or removed it.';
    Exit;
  end;
  if not PawnIORegistered then begin
    ExtractTemporaryFile('PawnIO_setup.exe');
    DriverPath := ExpandConstant('{tmp}\PawnIO_setup.exe');
    if CompareText(GetSHA256OfFile(DriverPath), PawnIOHash) <> 0 then begin
      Result := 'The bundled sensor driver failed its integrity check. Download a fresh DLB setup.';
      Exit;
    end;
    if not Exec(DriverPath, '-install -silent', '', SW_HIDE, ewWaitUntilTerminated, ExitCode) then begin
      Result := 'The sensor driver installer could not start. See the setup log.';
      Exit;
    end;
    if ExitCode = 3010 then PawnIORebootRequired := True
    else if ExitCode <> 0 then begin
      Result := 'The sensor driver could not be installed (code ' + IntToStr(ExitCode) + '). See the setup log.';
      Exit;
    end;
    if not PawnIORegistered then begin
      Result := 'PawnIO setup completed but its service and library could not be verified. Restart Windows if requested, then retry DLB setup.';
      Exit;
    end;
  end else Log('Preserving registered shared PawnIO installation; sensor access is validated at runtime.');

  ServiceStoppedForUpgrade := ServiceStoppedForUpgrade or (ServiceState = SERVICE_RUNNING);
  if not StopSensorService then
    Result := 'DLB Precision Sensors did not stop. Close the monitor and retry setup.';
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Arguments: String;
  ExitCode: Integer;
begin
  if CurStep = ssPostInstall then begin
    Arguments := SensorService + ' binPath= "\"' + ExpandConstant('{app}\DlbPrecision.Service.exe') +
      '\"" start= auto obj= LocalSystem DisplayName= "DLB Precision Sensors"';
    if ServiceState = 0 then ExitCode := RunSC('create ' + Arguments)
    else ExitCode := RunSC('config ' + Arguments);
    if ExitCode <> 0 then RaiseException('Unable to register the sensor service. See the setup log.');
    if RunSC('description ' + SensorService + ' "Supplies local hardware readings to DLB Precision Monitor. Idle when no monitor is connected."') <> 0 then
      RaiseException('Unable to configure the sensor service description.');
    if RunSC('failure ' + SensorService + ' reset= 86400 actions= restart/5000/restart/15000/restart/60000') <> 0 then
      RaiseException('Unable to configure sensor service recovery.');
    ExitCode := RunSC('start ' + SensorService);
    if ((ExitCode <> 0) and (ExitCode <> 1056)) or not WaitForService(SERVICE_RUNNING) then
      RaiseException('The sensor service could not start. See the setup log; a Windows restart may be required.');
    ServiceStoppedForUpgrade := False;
    if WizardIsTaskSelected('startup') then begin
      StartupFailed := not ExecAsOriginalUser(ExpandConstant('{app}\DlbPrecision.Monitor.exe'),
        '--enable-startup', ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, ExitCode);
      if not StartupFailed then StartupFailed := ExitCode <> 0;
      if StartupFailed then
        Log('The monitor and service were installed, but launch-at-sign-in could not be enabled for the original user. Open the monitor from your Start menu and enable it in Settings.');
    end;
  end;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if (CurPageID = wpFinished) and StartupFailed then
    WizardForm.FinishedLabel.Caption := WizardForm.FinishedLabel.Caption + #13#10#13#10 +
      'Launch at sign-in was not enabled. Open DLB Precision Monitor from your Start menu as your normal user, then enable it in Settings. This can occur when setup is started from an already elevated administrator session.';
end;

function GetCustomSetupExitCode: Integer;
begin
  Result := 0;
  { Core files/service are installed; report incomplete optional user configuration to deployers. }
  if StartupFailed then Result := 20;
end;

function NeedRestart: Boolean;
begin
  Result := PawnIORebootRequired;
end;

procedure DeinitializeSetup;
begin
  { If upgrade preparation was cancelled, bring the old service back. }
  if ServiceStoppedForUpgrade and (ServiceState = SERVICE_STOPPED) then
    RunSC('start ' + SensorService);
end;

procedure RemoveStartupForLoadedUsers;
var
  Users: TArrayOfString;
  Index: Integer;
  RunKey, Existing, Expected: String;
begin
  { Never load another user's hive, remove unrelated startup values, or erase preferences. }
  Expected := '"' + ExpandConstant('{app}\DlbPrecision.Monitor.exe') + '"';
  if RegGetSubkeyNames(HKU, '', Users) then
    for Index := 0 to GetArrayLength(Users) - 1 do begin
      RunKey := Users[Index] + '\Software\Microsoft\Windows\CurrentVersion\Run';
      if RegQueryStringValue(HKU, RunKey, 'DlbPrecisionMonitor', Existing) and
        ((CompareText(Existing, Expected) = 0) or (Pos(Lowercase(Expected + ' '), Lowercase(Existing)) = 1)) then
        RegDeleteValue(HKU, RunKey, 'DlbPrecisionMonitor');
    end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ExitCode: Integer;
begin
  if CurUninstallStep = usUninstall then begin
    { Run only after uninstall confirmation; opening then cancelling must leave the service alone. }
    if not StopSensorService then
      RaiseException('DLB Precision Sensors did not stop. Close the monitor and retry uninstall.');
    if ServiceState <> 0 then begin
      ExitCode := RunSC('delete ' + SensorService);
      if (ExitCode <> 0) and (ExitCode <> 1060) then
        RaiseException('The DLB sensor service could not be removed. See the uninstall log.');
    end;
    RemoveStartupForLoadedUsers;
    Log('Shared PawnIO driver deliberately preserved.');
  end;
end;
