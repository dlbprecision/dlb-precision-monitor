# Sensor backend

`SensorReader` is a serialized, disposable reader backed by LibreHardwareMonitorLib 0.9.6. Create one instance per service process and call `Read()` at the selected sampling interval. It starts no timer or background thread itself. Only CPU and GPU groups are opened; physical memory and total CPU busy time use Windows APIs. Hardware discovery occurs during construction, and sensor history retention is disabled, including sensors that activate later.

Readings and fallbacks:

- CPU temperature: package temperature, AMD physical die, combined control/die, control temperature, then core maximum. An unavailable package can fall back to individual core temperatures. Distance-to-TjMax is never treated as temperature.
- CPU load: Windows system busy time across the interval. On systems with more than 64 logical processors, where `GetSystemTimes` covers only one processor group, the library's CPU total is used instead. Multi-socket systems currently display the first package's temperature and clock and emit a diagnostic.
- CPU clock: arithmetic mean of reported core clocks. AMD's explicit average is preferred. Effective, bus and SoC clocks are excluded.
- GPU temperature and clock: core values only; no memory clock, memory junction or hot-spot substitution.
- GPU load: vendor core load where available; otherwise the busiest Windows D3D engine, never the sum of engine percentages.
- RAM: total usable physical memory minus available physical memory, divided by 2^30; this is the Windows-style GB convention.

NaN, infinity, implausible values and missing sensors produce null, never replacement zeros. Exactly 0°C is also rejected because a live non-elevated Ryzen 9950X3D probe demonstrated that LibreHardwareMonitor returns it for inaccessible sensors. Zero load and a sleeping GPU's zero clock remain valid. This rule limits exact-zero/sub-zero-cooling applications; the on-demand report retains the raw value for diagnosis.

The library's hardware driver is not installed or altered by this code. CPU temperature/clock generally require the privileged sensor service. GPU readings can work without elevation. A registered PawnIO driver is not proof that access succeeds. Startup and per-device failures appear in snapshot warnings; exceptions during an update discard that device's prior readings.

`GetReport()` produces a current snapshot, sensor definitions, privilege/driver-registration flags, and raw CPU/GPU temperature/load/clock data. It deliberately excludes the full machine report, serial numbers and user information.

## Validation

Run the companion `DlbPrecision.Probe.exe --self-test` for deterministic selection regression checks. `--sample` prints protocol-compatible JSON after a one-second warmup; `--report` prints extended diagnostics. These modes do not elevate or install anything.

The current machine was detected as AMD Ryzen 9 9950X3D and NVIDIA GeForce RTX 5090. In a non-elevated process, GPU temperature/load/core clock, CPU load and physical RAM succeeded, while CPU temperature/core clock correctly remained unavailable. Elevated sensor validation remains required through the installed service. Other CPU/GPU families still need physical test coverage.

Primary references: [LHM library](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/tree/v0.9.6), [Windows CPU time](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-getsystemtimes), [Windows physical memory](https://learn.microsoft.com/en-us/windows/win32/api/sysinfoapi/nf-sysinfoapi-globalmemorystatusex).
