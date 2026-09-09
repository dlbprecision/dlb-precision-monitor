{ Shared by production setup and the non-destructive installer test harness.
  Platform adapters above this include provide readiness, version and installation.
  Never uninstall a shared driver or invoke an undocumented repair switch. }
function EnsurePawnIO(var RebootRequired: Boolean): String;
var
  ExitCode: Integer;
  InstalledVersion: Int64;
begin
  Result := '';
  if PawnIODriverReady then begin
    Log('Preserving the running, accessible shared PawnIO driver.');
    Exit;
  end;
  if TryStartExistingPawnIO then begin
    Log('Started the existing shared PawnIO driver without reinstalling it.');
    Exit;
  end;

  if StrToVersion(PawnIOInstalledVersion, InstalledVersion) then
    if ComparePackedVersion(InstalledVersion, PackVersionComponents(2, 2, 0, 0)) > 0 then begin
      Result := 'A newer PawnIO driver is installed but is not available. Restart Windows and retry DLB setup. This installer will not downgrade the shared driver; if the problem continues, send DLB the setup log.';
      Exit;
    end;

  if PawnIOFootprintExists then
    Log('PawnIO is incomplete or unavailable; running the bundled official installer to restore it.')
  else
    Log('Installing the bundled official PawnIO driver.');

  Result := RunBundledPawnIO(ExitCode);
  if Result <> '' then Exit;
  Log('Bundled PawnIO installer exit code: ' + IntToStr(ExitCode));
  if ExitCode = 3010 then RebootRequired := True
  else if ExitCode <> 0 then begin
    Result := 'The bundled sensor driver setup could not finish (code ' + IntToStr(ExitCode) + '). Restart Windows and run this DLB installer again. If it still fails, send DLB the setup log.';
    Exit;
  end;

  if not PawnIODriverReady then begin
    if RebootRequired then
      Result := 'The bundled sensor driver needs a Windows restart. Restart, then run this same DLB installer again to finish. No separate driver download is needed.'
    else
      Result := 'The bundled sensor driver setup finished, but Windows has not made the driver available. Restart Windows and retry this DLB installer. If it still fails, send DLB the setup log.';
  end;
end;
