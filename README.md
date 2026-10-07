# DownloadLimit

A small Windows tray app for **PC-wide upload and download shaping**. Separate aggregate limits apply across apps and internet interfaces. Built with C#/.NET and WinForms, using WinDivert's existing signed driver.

Targets **Windows 10 22H2 and Windows 11, native x64**. No accounts, subscriptions, per-app rules, or custom drivers.

## Run

Open `DownloadLimit.exe` from the release ZIP and accept the administrator prompt. The self-contained EXE handles first-run installation into `%ProgramFiles%\DownloadLimit`; no separate installer or .NET installation is needed. The app EXE is unsigned; the upstream WinDivert driver is signed.

Right-click the tray icon:

- **Enable / Disable** — start shaping or restore unrestricted traffic.
- **Set Upload/Download Limits…** — separate caps, initially **500 Mbps each**.
- **Bypass for 15 minutes** — temporarily remove both caps; **Resume limits now** ends bypass early. Selecting bypass again resets its timer. Sleep counts toward expiry.
- **Start with Windows** — checked by default; registers an elevated task for the current administrator account's sign-in.
- **Exit** — release packet capture and close the app.

Every launch starts with **shaping disabled**. Limits and startup choice persist. The tooltip shows current download/upload speeds, configured caps, and status or bypass countdown.

## Build and test

Install the **.NET 10 SDK** (see `global.json`), then run from the repository root:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

This verifies pinned upstream WinDivert archives and driver signature metadata, compiles all projects, and produces a self-contained EXE and `artifacts/DownloadLimit-win-x64.zip`. Builds download dependencies but **never launch the app or execute tests**.

Tests are explicit opt-in:

```powershell
# All network-free checks: synthetic core tests plus native filter/startup checks
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\test.ps1

# LIVE internet transfers: up to 500 MB per run; leaves tray settings unchanged
dotnet run --project tests/DownloadLimit.LiveSmoke -c Release -- --live
```

Native checks load WinDivert's compiler/evaluator without driver handles, traffic, or task registration. The test script may download missing build dependencies. `test-offline.ps1` remains available for synthetic core tests alone.

The **25 core tests and 37 Windows/filter/privacy checks pass**. An earlier short Windows 11 live comparison confirmed shaping and bypass affect both directions; the subsequent review fixes have been checked without live traffic. Windows 10, gaming latency, VPNs, sleep/resume, and live shutdown still need manual validation. See the [Windows checklist](docs/WINDOWS-VALIDATION.md).

## Limits and gaming

Small UDP packets receive priority **inside the same cap**. Pure TCP ACK/control packets and ICMP pass without pacing; loopback and local-network traffic are excluded. Gaming priority is a packet-size heuristic, so it cannot guarantee lag-free play. Caps below available link speeds can leave headroom; this app cannot control other devices on the network.

**Inbound packets have already crossed the internet connection.** Download shaping can pace delivery and encourage TCP to adapt, but cannot guarantee a strict inbound cap at the router. VPN compatibility is best effort and requires testing with your VPN.

An aggregate token bucket, bounded queues, and short packet deadlines limit buffering. Overload can drop packets and cause retransmissions. Disable, bypass, Exit, and fatal errors release capture so future traffic resumes normally. See [design, settings, removal, and troubleshooting](docs/DESIGN.md) for exact bounds and caveats.

## License

App code: [MIT](LICENSE). Unmodified **WinDivert 2.2.2** is distributed under its LGPLv3 option, with upstream license text and corresponding source included. Its DLL/driver stay separate and replaceable after extraction. See [third-party notices](THIRD-PARTY-NOTICES.txt). No commercial WinDivert subscription is required under these terms.
