; Exercises the production prerequisite decisions with in-memory adapters only.
; No driver payload, registry access, service calls or install actions are present.
#ifndef TestOutputDir
  #error TestOutputDir must point to a new test output directory
#endif

[Setup]
AppId=DLBPrecisionPawnIOPrerequisiteTests
AppName=DLB PawnIO prerequisite tests
AppVersion=1.0
CreateAppDir=no
Uninstallable=no
PrivilegesRequired=lowest
DisableProgramGroupPage=yes
OutputDir={#TestOutputDir}
OutputBaseFilename=PawnIOPrerequisite.Tests
SetupLogging=yes

[Code]
var
  MockReadyBefore: Boolean;
  MockReadyAfter: Boolean;
  MockFootprint: Boolean;
  MockVersion: String;
  MockActionError: String;
  MockExitCode: Integer;
  MockRunCalls: Integer;
  MockStartResult: Boolean;
  MockStartCalls: Integer;
  PassedCases: Integer;
  FailedCases: Integer;
  Results: String;

function PawnIODriverReady: Boolean;
begin
  if MockRunCalls = 0 then Result := MockReadyBefore
  else Result := MockReadyAfter;
end;

function PawnIOFootprintExists: Boolean;
begin
  Result := MockFootprint;
end;

function TryStartExistingPawnIO: Boolean;
begin
  MockStartCalls := MockStartCalls + 1;
  Result := MockStartResult;
end;

function PawnIOInstalledVersion: String;
begin
  Result := MockVersion;
end;

function RunBundledPawnIO(var ExitCode: Integer): String;
begin
  MockRunCalls := MockRunCalls + 1;
  ExitCode := MockExitCode;
  Result := MockActionError;
end;

#include "..\..\installer\PawnIOPrerequisite.iss"

procedure Check(Condition: Boolean; const Message: String);
begin
  if not Condition then RaiseException(Message);
end;

procedure RecordCase(const Name, Failure: String);
begin
  if Failure = '' then begin
    PassedCases := PassedCases + 1;
    Results := Results + 'PASS|' + Name + #13#10;
  end else begin
    FailedCases := FailedCases + 1;
    Results := Results + 'FAIL|' + Name + '|' + Failure + #13#10;
  end;
end;

procedure ResetMocks(ReadyBefore, ReadyAfter, Footprint: Boolean;
  const Version, ActionError: String; ExitCode: Integer);
begin
  MockReadyBefore := ReadyBefore;
  MockReadyAfter := ReadyAfter;
  MockFootprint := Footprint;
  MockVersion := Version;
  MockActionError := ActionError;
  MockExitCode := ExitCode;
  MockRunCalls := 0;
  MockStartResult := False;
  MockStartCalls := 0;
end;

procedure RunCase(const Name: String; ReadyBefore, ReadyAfter, Footprint: Boolean;
  const Version, ActionError: String; ExitCode: Integer;
  InitialReboot, ExpectSuccess, ExpectReboot: Boolean; ExpectedCalls: Integer;
  const ExpectedErrorPart: String);
var
  RebootRequired: Boolean;
  Outcome: String;
begin
  ResetMocks(ReadyBefore, ReadyAfter, Footprint, Version, ActionError, ExitCode);
  RebootRequired := InitialReboot;
  try
    Outcome := EnsurePawnIO(RebootRequired);
    Check((Outcome = '') = ExpectSuccess, 'Unexpected success/error result: ' + Outcome);
    Check(MockRunCalls = ExpectedCalls, 'Wrong bundled-installer call count: ' + IntToStr(MockRunCalls));
    if ReadyBefore then
      Check(MockStartCalls = 0, 'Healthy driver triggered an unnecessary start')
    else
      Check(MockStartCalls = 1, 'Unavailable driver was not started exactly once before repair');
    Check(RebootRequired = ExpectReboot, 'Wrong reboot requirement');
    if ExpectedErrorPart <> '' then
      Check(Pos(Lowercase(ExpectedErrorPart), Lowercase(Outcome)) > 0,
        'Missing expected error detail: ' + Outcome);
    RecordCase(Name, '');
  except
    RecordCase(Name, GetExceptionMessage);
  end;
end;

procedure RunStartCase(const Name, Version: String);
var
  RebootRequired: Boolean;
  Outcome: String;
begin
  ResetMocks(False, False, True, Version, '', 0);
  MockStartResult := True;
  RebootRequired := False;
  try
    Outcome := EnsurePawnIO(RebootRequired);
    Check(Outcome = '', 'Starting an existing driver did not succeed: ' + Outcome);
    Check(MockStartCalls = 1, 'Existing driver was not started exactly once');
    Check(MockRunCalls = 0, 'Successfully started driver was unnecessarily reinstalled');
    Check(not RebootRequired, 'Successful existing-driver start requested a reboot');
    RecordCase(Name, '');
  except
    RecordCase(Name, GetExceptionMessage);
  end;
end;

procedure RunRepeatCase;
var
  RebootRequired: Boolean;
  Outcome: String;
begin
  ResetMocks(False, True, True, '2.2.0.0', '', 3010);
  RebootRequired := False;
  try
    Outcome := EnsurePawnIO(RebootRequired);
    Check(Outcome = '', 'First preparation failed: ' + Outcome);
    Check(RebootRequired, 'First preparation lost vendor reboot request');
    Check(MockRunCalls = 1, 'First preparation did not repair exactly once');
    Outcome := EnsurePawnIO(RebootRequired);
    Check(Outcome = '', 'Repeated preparation failed: ' + Outcome);
    Check(RebootRequired, 'Repeated preparation cleared pending reboot');
    Check(MockRunCalls = 1, 'Repeated preparation reran the healthy dependency');
    RecordCase('repeated-preparation-preserves-reboot', '');
  except
    RecordCase('repeated-preparation-preserves-reboot', GetExceptionMessage);
  end;
end;

function InitializeSetup: Boolean;
var
  ResultPath: String;
begin
  Result := False;
  ResultPath := ExpandConstant('{param:ResultFile|}');
  if ResultPath = '' then Exit;
  Results := 'DLB_PAWNIO_TEST_RESULTS_V1' + #13#10;
  PassedCases := 0;
  FailedCases := 0;

  RunCase('healthy-preserved', True, False, True, '2.2.0.0', '', 0, False, True, False, 0, '');
  RunCase('newer-healthy-preserved', True, False, True, '3.0.0.0', '', 0, False, True, False, 0, '');
  RunCase('absent-installed', False, True, False, '', '', 0, False, True, False, 1, '');
  RunCase('partial-footprint-repaired', False, True, True, '', '', 0, False, True, False, 1, '');
  RunCase('equal-three-part-version-repaired', False, True, True, '2.2.0', '', 0, False, True, False, 1, '');
  RunCase('equal-four-part-version-repaired', False, True, True, '2.2.0.0', '', 0, False, True, False, 1, '');
  RunCase('older-version-repaired', False, True, True, '2.1.9.9', '', 0, False, True, False, 1, '');
  RunCase('unparseable-version-repaired', False, True, True, 'unknown', '', 0, False, True, False, 1, '');
  RunCase('newer-patch-version-blocked', False, True, True, '2.2.0.1', '', 0, False, False, False, 0, '');
  RunCase('newer-minor-version-blocked', False, True, True, '2.10.0.0', '', 0, False, False, False, 0, '');
  RunCase('newer-major-version-blocked', False, True, True, '3.0.0.0', '', 0, False, False, False, 0, '');
  RunCase('payload-verification-failed', False, False, False, '', 'mock payload hash mismatch', 0, False, False, False, 1, 'mock payload hash mismatch');
  RunCase('payload-extraction-failed', False, False, False, '', 'mock extraction failed', 0, False, False, False, 1, 'mock extraction failed');
  RunCase('vendor-launch-failed', False, False, True, '2.2.0', 'mock launch failed', -1, False, False, False, 1, 'mock launch failed');
  RunCase('vendor-error-code-blocked', False, False, True, '2.2.0', '', 5, False, False, False, 1, '5');
  RunCase('success-without-ready-driver-blocked', False, False, False, '', '', 0, False, False, False, 1, '');
  RunCase('vendor-reboot-ready', False, True, False, '', '', 3010, False, True, True, 1, '');
  RunCase('vendor-reboot-not-ready', False, False, True, '2.2.0', '', 3010, False, False, True, 1, 'restart');
  RunCase('existing-reboot-preserved-healthy', True, True, True, '2.2.0', '', 0, True, True, True, 0, '');
  RunCase('existing-reboot-preserved-install', False, True, False, '', '', 0, True, True, True, 1, '');
  RunRepeatCase;
  RunStartCase('stopped-existing-driver-started', '2.2.0.0');
  RunStartCase('stopped-newer-driver-started', '3.0.0.0');

  Results := Results + 'TOTAL|' + IntToStr(PassedCases + FailedCases) + #13#10 +
    'PASSED|' + IntToStr(PassedCases) + #13#10 + 'FAILED|' + IntToStr(FailedCases) + #13#10;
  if not SaveStringToFile(ResultPath, Results, False) then
    RaiseException('Unable to write prerequisite-test results.');
  { Always return False: even passing tests must never start an installation. }
end;
