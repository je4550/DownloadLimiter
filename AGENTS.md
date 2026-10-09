# Repository guide

Build a minimal, reliable Windows tray shaper. Keep the interface to limits, enable/disable, timed bypass, startup checkbox, tooltip, and exit. Avoid accounts, dashboards, per-app rules, custom drivers, or unnecessary packages.

## Layout

- `src/DownloadLimit.Core`: platform-independent accounting, parsing, bounded storage, scheduling, and bypass state.
- `src/DownloadLimit.Windows`: WinDivert interop, local-network exclusions, speed monitoring, and startup task.
- `src/DownloadLimit.App`: WinForms tray UI, settings, elevation, and self-installation.
- `tests/DownloadLimit.Tests`: synthetic core tests with fake clocks/transports.
- `tests/DownloadLimit.Windows.Tests`: native helper/filter and startup XML checks without opening driver handles.
- `tests/DownloadLimit.LiveSmoke`: explicitly opted-in real HTTP transfers.
- `scripts/`: verified dependencies, test commands, and publication checks; `build.ps1` packages the self-contained x64 release.

## Working rules

- Preserve separate aggregate upload/download budgets, bounded packet storage, full IP-byte accounting, and small burst allowance. Game-priority packets share the cap; control and local traffic remain exempt.
- Restore the last explicit Enable/Disable choice on launch; fresh or legacy settings default to disabled. Bypass is temporary, and Exit/failure must not overwrite that choice. Disable, bypass, Exit, and failure must release diversion promptly. Preserve bounded shutdown and cancellation of pending native I/O.
- Keep IPv4/IPv6 parsing and WinDivert ABI/layout checks intact. Use signed upstream binaries; pin and verify dependency archives. Include matching licenses/source in releases.
- Build with `powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1`. It compiles but does not run tests or install anything.
- Run relevant network-free checks with `scripts/test.ps1`; `test-offline.ps1` runs core checks alone. Never enable shaping, launch the app, register tasks, or generate live traffic without user authorization. Live smoke requires `--live`.
- Report compilation, synthetic/native checks, and actual Windows/network validation separately. Do not infer Windows 10, VPN, sleep, game-latency, or router-cap guarantees from unit tests.
- Keep README concise; put operational detail in `docs/`. Update docs when behavior changes.
- Publish source and sanitized documentation only. Never commit credentials, personal paths, usernames, SIDs, settings/logs, raw runtime reports, adapter inventories, `artifacts/`, `.deps/`, `bin/`, or `obj/`. Synthetic identifiers in tests must be clearly fictitious. Audit staged content and commit identity before pushing.
