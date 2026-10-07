# Design and operation

## Accounting and priority

Upload and download each have one shared token bucket across eligible apps and interfaces. Limits use decimal Mbps, accept 1–10,000 Mbps with three decimal places, and count full IP packet lengths. Ethernet/Wi-Fi framing and tunnel overhead outside the intercepted layer are not counted.

Buckets allow approximately two milliseconds of traffic, with a one-normal-packet minimum. A larger packet requires a full bucket and is charged in full, creating debt. Changing a limit preserves debt and clamps surplus credit. These are packet-level rate bounds rather than instantaneous bit-by-bit guarantees.

UDP packets up to 512 total IP bytes receive priority inside the directional cap. Under sustained contention their class receives 20% of that budget and may borrow idle bandwidth. This does not identify games: tiny QUIC transfers can qualify, and large or tunneled game packets may not. Caps below measured link capacity can help preserve headroom; other devices can still congest the router.

Pure TCP ACKs and other TCP packets without payload, plus ICMP/ICMPv6 control traffic, are unpaced. Reported totals include those exempt bytes and can slightly exceed the data cap. A nonblocking passive monitor stays active while the app runs, including while shaping is disabled or bypassed. A `?` speed means monitoring is unavailable.

Loopback, private/CGNAT ranges, directly connected LAN prefixes, broadcast, and multicast are excluded. Classification tests the remote endpoint; the PC having a private local address does not exempt its internet transfers. Forwarded Windows internet-sharing/router traffic is outside this app's scope.

## Queue bounds and recovery

Managed packet buffer capacities, including cached/rented buffers, are bounded to **8 MiB**, with at most **2,048 queued packets**. UI/runtime overhead, receive buffers, metadata, and native queues are additional. Each WinDivert handle has a **1 MiB / 1,024-packet** queue. Its **100 ms** queue-time setting is the driver's minimum eligibility time for dropping, not a maximum latency guarantee.

Application residence deadlines are **20 ms for bulk** and **5 ms for priority** packets, measured from capture. Excess or expired packets are dropped to bound buffering; retransmissions and loss are possible under overload. Very low caps and large offloaded packets can produce short bursts and longer waits.

Disable, bypass, Exit, and failure stop new diversion, release held packets without pacing where possible, then bound draining before closing/canceling handles. A crash, timeout, or reinjection failure can lose already-held packets; closing diversion restores normal handling of future traffic. Fatal errors disable shaping; Enable retries after the cause is resolved. Sleep releases capture, and resume rebuilds handles/exclusions without accumulated burst credit.

## Download and VPN caveats

Inbound packets have already crossed the internet connection before this app delays or drops them. TCP may adapt, but UDP and retransmissions can keep using inbound capacity. Download shaping cannot promise a strict router ingress cap or lag-free gaming.

VPN behavior needs per-VPN validation. Tunnels can expose inner and outer traffic, hide small game packets, inject excluded `impostor` traffic, or conflict with other filtering drivers. Bypass, double accounting, lost priority, or errors are possible. The [validation checklist](WINDOWS-VALIDATION.md) covers these cases.

## Installation, settings, and removal

First launch requests administrator privileges, copies the self-contained EXE into `%ProgramFiles%\DownloadLimit`, and extracts the upstream DLL/driver into a versioned folder. WinDivert loads on demand when a handle opens; no custom driver installation is required. License texts and corresponding WinDivert source are extracted into `Licenses`. The app EXE is unsigned. Driver signature acceptance depends on Windows/security configuration.

Start with Windows uses a highest-privilege interactive sign-in task for the current administrator account. Supplying another administrator's credentials installs/registers for that account. Shaping always starts disabled, including at sign-in.

Settings are saved atomically in `%LocalAppData%\DownloadLimit\settings.json`. Limits and startup preference persist; enable state and bypass deadlines do not. Corrupt settings fall back to 500 Mbps each with shaping off. Error logs are bounded to 1 MiB plus three rotated files and omit packet contents; error details may contain local paths, so review them before sharing.

To remove the app, uncheck Start with Windows, select Exit, then delete `%ProgramFiles%\DownloadLimit`. Optionally delete `%LocalAppData%\DownloadLimit`. If it cannot start, remove its `DownloadLimit-<your SID>` task in Task Scheduler first. Avoid removing WinDivert globally when other programs use it.

## Driver troubleshooting and licensing

A blocked driver, incompatible existing WinDivert version, or disabled Base Filtering Engine can prevent capture. The app reports an error and leaves shaping disabled. Follow [upstream documentation](https://reqrypt.org/windivert-doc.html) and [FAQ](https://reqrypt.org/windivert-faq.html); do not disable security protections or build an unsigned replacement driver as a workaround.

WinDivert is unmodified and distributed under its LGPLv3 option. The EXE and release ZIP carry upstream license text and corresponding source. Extracted native files remain replaceable with interface-compatible versions and are not overwritten on ordinary launches. A replacement Windows driver still needs a valid signature. See [third-party notices](../THIRD-PARTY-NOTICES.txt).
