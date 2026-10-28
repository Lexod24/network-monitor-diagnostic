# Network Monitor Diagnostic

A portable Windows desktop app that monitors connection quality and produces a transparent, evidence-based diagnosis. English is selected by default; Romanian is available in the app.

## Download and start

[Download the portable Windows app (.zip)](https://raw.githubusercontent.com/Lexod24/network-monitor-diagnostic/main/NetworkMonitor-Portable.zip). Extract the ZIP and double-click `NetworkMonitor.exe`. No administrator rights or installation are required. The app does not change Windows or router settings.

## Use the app

1. Select **Start monitoring** while the connection is behaving poorly.
2. Leave the app running during the issue. Five seconds is the target sampling interval; the report shows the measured average interval.
3. Select **Stop and analyze**. The window shows findings, evidence, limitations, and practical next steps. A CSV and a text report are saved locally under `%LOCALAPPDATA%\NetworkMonitor\Logs`.
4. For an optional AI explanation, select **Continue with ChatGPT** and approve the request. The app opens the official ChatGPT sign-in in your browser. Sign in with your own account and grant the requested permission. Return to the app; after monitoring stops, it can request a plain-language explanation of the aggregated results. Use **Disconnect ChatGPT** to remove the local session and request remote revocation.

AI is optional. Without it, the complete local diagnosis still works. AI output is supplemental and may be wrong; compare it with the measurements and the deterministic local findings. Eligible ChatGPT plan access and limits belong to the signed-in user's account. This app contains no shared API key and does not charge the publisher for other users' requests.

## Privacy

The AI request contains only aggregated counts and averages from the session. It excludes raw CSV content, SSID, IP addresses, exact timestamps, and credentials. The app sends that summary to OpenAI only after the user signs in and approves ChatGPT plan use. OAuth tokens are stored locally protected with Windows DPAPI for the current Windows user. Disconnect removes the local credentials; if remote revocation cannot be confirmed, the app says so. Network monitoring itself sends ICMP and DNS probes and small HTTPS requests to the documented endpoints below.

## Measurements and diagnosis

- Wi-Fi connection state and Windows-reported signal strength when available.
- ICMP latency and rolling packet loss to the local gateway and `1.1.1.1`.
- If the Internet ICMP probe does not reply, three concurrent HTTPS checks to Microsoft, Google, and Cloudflare. The app validates expected endpoint responses and rejects redirects or captive portal pages.
- DNS lookups once a minute for three domains using the configured Windows resolver.
- CSV events with per-endpoint results, timestamps, and rotation at 10 MB (up to 10 rotated files).

The report compares gateway, Internet, HTTPS, and DNS evidence and distinguishes measured facts from likely causes. It does not claim to prove ISP or hardware faults. ICMP may be filtered or rate-limited, Wi-Fi signal may be unavailable, and DNS may use the Windows cache. Firewalls, proxies, captive portals, regional routing, or endpoint incidents can affect results.

## Build and test

Run `build.ps1` on Windows to create `dist\NetworkMonitor.exe`. Run `tests\run-self-tests.ps1` for deterministic diagnosis, OAuth validation, and AI request privacy tests. The GitHub Actions workflow builds the app and runs the same suite on Windows.

## License

MIT. See [LICENSE](LICENSE).

