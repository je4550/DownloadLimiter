# Windows validation checklist

The 38 core tests and 55 Windows/filter/monitor/checksum/privacy checks pass. An earlier short Windows 11 live comparison completed parallel HTTPS downloads/uploads while enabled, bypassed, and resumed without new app errors. It confirmed that shaping and bypass affect both directions; it did not establish precise sustained rate accuracy or gaming latency. The subsequent review, packet-path optimizations, saved Enable/Disable choice, and CPU-load recovery changes have been checked without live traffic.

A subsequent Windows launch verified that the installed executable matched the updated build, the process ran at Normal priority, both packet workers ran Above Normal, and the existing startup task migrated to priority 4. This was an installation/scheduling check; no compile-load comparison or live smoke transfers were performed for this update.

The checklist below remains broader manual validation. CPU-saturation connectivity, Windows 10, VPNs, gaming, sleep/resume, and live shutdown are untested. Use a test PC or a quiet network. Keep environment details and raw reports private; publish only sanitized outcomes. Automated shutdown tests do not establish live shutdown behavior.

## Setup, persistence, and startup

- [ ] Launch the single EXE, accept UAC, confirm files appear under Program Files and the tray icon is present. Do not mistake the signed driver for an Authenticode-signed app EXE.
- [ ] Confirm first-launch limits are 500 Mbps each, Start with Windows is checked, and shaping is off.
- [ ] Change both caps, Enable, Exit, restart, and confirm limits persist and shaping resumes enabled. Disable, Exit, restart, and confirm shaping remains disabled.
- [ ] Uncheck startup; confirm only this user's DownloadLimit task is removed. Recheck; confirm it returns with interactive logon/highest privileges, no battery/time-limit restriction.
- [ ] Sign out/in (or reboot): tray launches once without a repeated UAC prompt and restores the last Enable/Disable choice.
- [ ] Upgrade an existing startup-enabled installation: task priority becomes 4, the process runs at least Normal, and the capture/send workers run Above Normal. Confirm startup-disabled installations remain disabled. If task migration fails, current-launch CPU normalization must still precede packet capture.
- [ ] Launch a second copy and confirm it does not open another shaping engine.
- [ ] Back up settings, deliberately corrupt the JSON, and confirm safe defaults, a readable notification, and unrestricted traffic.

## Aggregate download/upload and exclusions

- [ ] Record unrestricted sustained download/upload baselines first. Use caps substantially below measured bandwidth; remember Mbps = MB/s × 8 using decimal units.
- [ ] Enable with simultaneous downloads in at least two applications. Sum their throughput over 30–60 seconds after warmup; it should share one download budget, allowing the documented packet burst/control overhead.
- [ ] Repeat for simultaneous uploads and for simultaneous upload plus download with different caps. Each direction should follow its own budget without borrowing from the other.
- [ ] Where available, repeat with Wi-Fi and Ethernet both actively carrying internet traffic. Verify the directional totals, not a cap per adapter.
- [ ] Repeat with IPv4 and IPv6 sources, TCP and QUIC/UDP transfers. Expect dropped inbound UDP not to reduce router ingress automatically.
- [ ] Make localhost and LAN file transfers during shaping. Check IPv4 private LAN, IPv6 link-local/ULA, and directly connected public-address LANs where available; they should remain unrestricted.
- [ ] Check remote public internet traffic is still shaped when this PC has a private local address.
- [ ] Change limits while transfers run; throughput should adjust without stale burst credit or interruption of the UI.

## Gaming, latency, and overload

- [ ] Record game latency/jitter/loss and voice quality without shaping or bulk transfers.
- [ ] Compare CPU usage, throughput and packet latency with shaping off/on when traffic is below the cap, then under sustained load. The direct-send path avoids managed queuing when eligible; synthetic checks do not establish its live latency or throughput.
- [ ] Repeat the same CPU-heavy compile with shaping enabled, bypassed, and disabled. Compare actual browser/transfer responsiveness and game traffic, not only exempt ICMP ping. Verify brief scheduling delays do not cause a widespread stall. If capture falls behind by at least 100 ms, confirm shaping stops, future traffic recovers, and a notification appears when the UI can run. Enable should retry; restart should retain the last explicit Enable/Disable choice.
- [ ] Add bulk download, bulk upload, then both; compare shaping off/on with caps below link capacity. Use the actual game, not only ping, since ICMP is exempt.
- [ ] Confirm pure outbound TCP ACKs remain responsive during saturated downloads. Assess small UDP priority under bulk load, then large game datagrams or encrypted game traffic separately.
- [ ] Deliberately set an overly low cap and document any game packet loss. Priority cannot create bandwidth or guarantee no lag.
- [ ] Run transfers far above a low cap for several minutes. Watch CPU, working set, tooltip/UI response, and packet loss. Packet storage must remain bounded; total process memory includes .NET, UI, receive scratch buffers, and metadata.
- [ ] Inspect `errors.log` for reinjection failures/timeouts. No freeze or retained diverting handle should survive an error.

## Bypass, shutdown, and errors

- [ ] While shaping, choose Bypass for 15 minutes. Both directions return to unrestricted speeds; tooltip shows a countdown.
- [ ] Change limits during bypass; expiry applies the newly saved values.
- [ ] Leave the limits dialog open across bypass expiry; shaping must resume while the dialog remains open. Exit must close the dialog and release capture.
- [ ] Click bypass again to reset the timer; use Resume limits now to restore caps immediately.
- [ ] Use Disable during bypass, wait beyond its former deadline, and verify shaping stays off, including after Exit/restart. Exit while bypassed without selecting Disable; restart should restore enabled limits without the old bypass timer.
- [ ] Disable and Exit during heavy traffic, including a full queue and low caps. New traffic should become unrestricted immediately; held packets may flush briefly or retransmit after timeout/drop.
- [ ] Force-terminate the app during transfer; verify new connections/transfers recover without a persistent block. The next explicit launch or sign-in restores the saved Enable/Disable choice; the task should not repeatedly restart a crashing app.
- [ ] Exercise a missing DLL/driver or rejected-driver environment only on a test PC. Errors should be actionable, and traffic should remain unrestricted. Restore files before further checks; do not disable security software.

## VPN, adapters, and sleep/resume

- [ ] Test without a VPN, then each VPN you actually use: WireGuard, OpenVPN, built-in Windows VPN, and split/full tunnel as applicable.
- [ ] Verify upload/download totals with independent application counters. Record bypass, double accounting, encrypted overhead, lost priority, or WFP/driver conflicts; do not claim universal VPN compatibility.
- [ ] Connect/disconnect VPNs and change adapters while enabled/bypassed/off. The app should refresh exclusions and handles without overlapping shaping captures or hanging networking.
- [ ] Sleep/resume while enabled. Shaping returns with the saved caps and without a sleep-sized burst.
- [ ] Sleep during bypass and resume before expiry: only the remaining time remains. Resume after expiry: caps restore.
- [ ] Disable or Exit after resume and confirm no stale timer or power event re-enables shaping.
- [ ] Repeat on battery power; startup and the tray process should not be terminated by task battery settings.

## Offline tests (explicit opt-in)

Run `powershell -NoProfile -ExecutionPolicy Bypass -File .\test-offline.ps1` from the source folder. These synthetic tests exercise rate arithmetic, aggregate budgets, queue/pool bounds, prioritization, shutdown races/failures, parsing/exclusions, and bypass transitions. They cannot validate driver acceptance, actual packet interception, VPN behavior, NIC offloads, or Windows sleep events. Preserve the output and distinguish offline passes from the manual results above.
