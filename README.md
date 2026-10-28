# Network Monitor Diagnostic

A portable Windows desktop app for transparent, rule-based connection diagnosis. English is selected by default; Romanian is available in the language selector.

## Download and run

[Download the portable Windows app (.zip)](https://raw.githubusercontent.com/Lexod24/network-monitor-diagnostic/main/NetworkMonitor-Portable.zip). Extract the archive and double-click `NetworkMonitor.exe`. No administrator rights are needed. The app does not change Windows or router settings.

## What it measures

- Target interval: 5 seconds. The report shows the actual mean gap because slow probes or a busy system can delay samples.
- Wi-Fi interface state and Windows-reported signal strength when available.
- ICMP latency and rolling loss to the local gateway and 1.1.1.1.
- If Internet ICMP does not reply, three parallel HTTPS checks against Microsoft, Google, and Cloudflare. The app validates expected endpoint responses and rejects redirects/captive-portal pages.
- Once a minute, DNS resolution for three domains through the configured Windows resolver.
- CSV logs include per-endpoint results, timestamps, event markers, and rotation at 10 MB (up to 10 rotated files retained).

The report compares gateway, Internet, HTTPS, and DNS evidence; identifies a likely fault area; gives next steps; and labels outages as suspected where appropriate. It does not infer an ISP or hardware fault from these probes alone.

## Use

Choose **Start monitoring**, leave the app open while the issue occurs, then choose **Stop and analyze**. The report appears in the window. CSV and report files are saved under `%LOCALAPPDATA%\NetworkMonitor\Logs`.

No SSID, BSSID, IP addresses, passwords, or account identifiers are saved. Network checks send ICMP/DNS requests and small HTTPS GET requests to the listed public endpoints. This version uses local rules and does not connect to an AI service.

## Build and test

Run `build.ps1` to create `dist\NetworkMonitor.exe`. Run `tests\run-self-tests.ps1` for the deterministic suite. The tests cover healthy state, Wi-Fi disconnect/weak signal, gateway loss/latency, all HTTPS checks failing, partial HTTPS success, DNS failure/disagreement, ICMP filtering, per-endpoint reporting, and English report generation.

## Limits

ICMP can be filtered or rate-limited. Wi-Fi signal may be unavailable on some adapters. DNS uses the Windows resolver and may use its cache. Firewalls, proxies, captive portals, regional routing, and endpoint incidents can affect results. Cross-check evidence on another device before concluding that the router, ISP, or hardware is at fault.

## License

MIT. See [LICENSE](LICENSE).

