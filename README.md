# Network Monitor Diagnostic

A portable Windows desktop app that monitors connection quality and produces a transparent, evidence-based diagnosis. English is selected by default; Romanian is available in the app.

If this app helps you, please star this repository. Questions, bugs or suggestions? [Open an issue](https://github.com/Lexod24/network-monitor-diagnostic/issues).

Ți-a fost utilă aplicația? Acordă o stea repository-ului. Ai o întrebare, problemă sau sugestie? [Deschide un issue](https://github.com/Lexod24/network-monitor-diagnostic/issues).

## Download and start

[Download the portable Windows app (.zip)](https://raw.githubusercontent.com/Lexod24/network-monitor-diagnostic/main/NetworkMonitor-Portable.zip). Extract the ZIP and double-click `NetworkMonitor.exe`. No administrator rights or installation are required. The app does not change Windows or router settings.

The app checks the public GitHub update manifest at startup. If a newer version is available, it tells you, asks before downloading, shows a visible download/verification status, verifies the package SHA-256, and restarts to install it. It does not update during an active monitoring session. Update checks send no diagnostics, CSV, Wi-Fi name, IP addresses, credentials, or usage data.

## Use the app

1. Select **Start monitoring** while the connection is behaving poorly.
2. Leave the app running during the issue. Five seconds is the target sampling interval; the report shows the measured average interval.
3. Select **Stop and analyze**. The window shows findings, evidence, limitations, and practical next steps. A CSV and a text report are saved locally under `%LOCALAPPDATA%\NetworkMonitor\Logs`.
4. The local evidence-based diagnosis is always available. For an optional AI explanation, choose **AI options**. You can use local Ollama, Gemini API, or Groq API. **Continue with ChatGPT** remains available for eligible ChatGPT accounts; it opens the official sign-in and requests separate approval.

AI is optional. Without it, the complete local diagnosis still works. AI output is supplemental and may be wrong; compare it with the measurements and deterministic local findings. No provider keys are bundled and the publisher does not pay for other users' requests. Cloud providers require each user to supply their own key and are subject to that provider's quotas, account eligibility, data terms, and possible charges. Keys are protected with Windows DPAPI for the current Windows user. Ollama runs on the user's own computer and requires a separately installed model.
The sign-in callback listens on `127.0.0.1`, preferring port `1455` and selecting another available port if needed. Keep the app open and finish browser sign-in within 15 minutes.

## Privacy

AI requests contain only aggregated counts and averages from the session. They exclude raw CSV content, SSID, BSSID, IP addresses, exact timestamps, Windows event message text, and credentials. The optional summary can include Wi-Fi channel/band/radio/link-rate aggregates and counts by selected Windows network-event category. With Ollama, the summary remains on the computer. With Gemini or Groq, it is sent to the selected provider after the user configures a key. Review that provider's current data terms and quotas before use. ChatGPT access separately requires the user to sign in and approve plan use. ChatGPT OAuth tokens and cloud-provider keys are protected locally with Windows DPAPI for the current Windows user. The app also retains the issued public ChatGPT client ID locally if a first token exchange must be retried. Disconnecting ChatGPT removes its local credentials and registration ID; it does not remove the separately configured AI provider. Network monitoring itself sends ICMP and DNS probes and small HTTPS requests to the documented endpoints below.

## Optional AI providers

- **Local diagnosis only:** default behavior; no account, key, or AI request.
- **Ollama:** install [Ollama for Windows](https://ollama.com/download/windows), then run `ollama run qwen3:4b` once in a terminal to download the approximately 2.5 GB model. Keep Ollama running and select `Ollama` under **AI options**. This option needs no account or API key; inference runs locally. Hardware requirements and speed vary by computer.
- **Gemini API:** create your own key in [Google AI Studio](https://aistudio.google.com/apikey), select `Gemini API`, and use the suggested `gemini-3-flash-preview` model or another model currently available to your account. Free quotas and data handling vary by model/account; check Google's current [pricing and terms](https://ai.google.dev/gemini-api/docs/pricing).
- **Groq API:** create your own key in [Groq Console](https://console.groq.com/keys), select `Groq API`, and use the suggested `openai/gpt-oss-20b` model or another currently available model. Limits and account terms can change; see [Groq rate limits](https://console.groq.com/docs/rate-limits).

The provider selected in **AI options** is used for explanation after monitoring stops. When no alternative provider is selected, an existing ChatGPT connection can be used. No AI provider is configured by default. The app does not automatically create accounts, keys, or paid usage for the publisher.

## Measurements and diagnosis

### Provider speed test

The **Speed test** page can display Ookla, Netograf, DIGI, or Vodafone within the Windows app using Microsoft WebView2. Loading is user initiated after a notice about potentially substantial traffic, temporary effects on monitoring and ping, and the provider's separate processing of public IP and browser/connection data. Provider pages run as top-level pages: the app does not bypass their embedding/security rules, inspect their contents, scrape or save page results, or send those results to AI. The isolated WebView2 profile uses a temporary folder that is deleted when the app closes. If WebView2 Runtime is unavailable or a page reports an error, the app offers an explicit browser fallback. The Vodafone public endpoint may not be available. The app provides the external test only and does not retain speed-test results. The included Microsoft WebView2 SDK components are listed in `THIRD-PARTY-NOTICES.txt`; the WebView2 Runtime is provided by Microsoft and is not bundled.

- Wi-Fi connection state and Windows-reported signal strength when available.
- When Wi-Fi is the active route, the latest Windows-exposed channel, band when the driver provides it, radio type and receive/transmit link rates. The app does not guess the band from the channel because 5/6 GHz channel numbers overlap. Link rates are not measured Internet speeds. The app can detect a BSSID change in memory to mark a possible access-point roam, but does not retain the BSSID or SSID.
- Optional read-only correlation with recent WLAN AutoConfig, DNS Client, DHCP Client and System event records created during the current monitoring session. The app stores only a small category/count marker in the local session CSV, never event message text, SSID, BSSID, adapter names or addresses. Disabled or inaccessible event channels do not stop monitoring and are reported as unavailable. Event categories are supporting evidence; they do not prove root cause.
- ICMP response, latency, and rolling non-response rates to the gateway and three independent public targets over IPv4 and IPv6 (Cloudflare, Google, and Quad9). The report keeps address families separate; a ping non-response is not proof that application traffic was lost.
- The Windows route interface is selected for gateway and passive traffic measurements, preferring an IPv4 route when present and using IPv6 when IPv4 routing is unavailable. Tunnel-type interfaces are labeled separately, but Windows adapter types do not reliably identify every VPN. Interface IDs are used only in memory; logs store only a broad type.
- Latency spikes are detected relative to at least three recent successful samples (150 ms minimum and at least 50 ms or 50% above that median). A stable high RTT is reported as a measurement, not a spike.
- Live latency graph for the latest 60 samples (about five minutes), plus successful-probe minimum, median, p95, maximum, and gap-aware jitter in the final report.
- Passive upload/download rates from the active route interface. The report compares quiet samples (<0.25 Mbps) with active samples (>=1 Mbps) only when it has at least three of each. This is an observation of this PC's traffic, not a controlled bufferbloat or speed test; it does not include other devices and generates no traffic.
- Three concurrent HTTPS checks to Microsoft, Google, and Cloudflare run at least once per minute and sooner when fewer than two IP targets reply. The app validates expected responses, labels multiple redirects/unexpected HTML as a possible captive portal or interception, and records only whether the Windows proxy was used (never its address). A single failing endpoint is shown as partial probe evidence, not as a general connection failure.
- DNS lookups once a minute for three domains using the configured Windows resolver.
- An optional **Trace route** button. After confirmation, Windows `tracert` sends a small number of ICMP TTL probes to `1.1.1.1`, up to eight hops, with a 12-second safety limit. Hop IPs appear in a local dialog only; the trace is not saved, logged, or sent to AI. Routers may hide hops or ignore traceroute.
- CSV events with per-target and per-endpoint results, timestamps, and rotation at 10 MB (up to 10 rotated files). External target IP addresses, SSIDs, BSSIDs, adapter names, and interface IDs are not saved.

The report compares gateway, IPv4, IPv6, HTTPS, and DNS evidence, suggests the first likely affected segment, and labels confidence and limits. Partial endpoint response is distinct from a suspected outage; a suspected portal is not confirmed from HTTP response patterns alone. It avoids inferring an ISP or hardware fault from one check. ICMP may be filtered or rate-limited, Wi-Fi signal may be unavailable, and DNS may use the Windows cache. Firewalls, VPNs, proxies, captive portals, regional routing, IPv4/IPv6 differences, and endpoint incidents can affect results. Public targets cannot represent every game or website; use **Trace route** during the issue for a route snapshot. Game-specific diagnosis requires a known game endpoint and is not inferred from these general probes.

The optional **Latency under load** action uses Cloudflare's documented Speedtest SDK and public HTTPS endpoints to compare unloaded latency with latency during download and upload. It requires no account, API key, or payment in the app. Before each run the app discloses that requests go to Cloudflare and may expose the public IP and connection metadata. A run is capped at 24 MiB payload (16 MiB download + 8 MiB upload), plus protocol/header traffic, and 60 seconds. Monitoring probes pause during the test, then resume; the report marks the excluded interval. Values appear only in the test window and are not saved to CSV/report/history or sent to AI. The SDK's results-summary upload endpoint is disabled. The test can temporarily affect other apps; cancellation stops the active request.

## Build and test

Run `build.ps1` on Windows to create `dist\NetworkMonitor.exe`, the portable ZIP, and the SHA-256 update manifest. Bump `AppVersion.Current` and the assembly version in `src\AppVersion.cs` when publishing a new version, then commit the generated ZIP and `update.json` together. Run `tests\run-self-tests.ps1` for deterministic IPv4/IPv6 aggregation, endpoint/outage classification, Wi-Fi telemetry parsing and anonymization, Windows event-category filtering, bounded Cloudflare test requests and privacy, Ethernet/Wi-Fi relevance, proxy/captive-portal heuristics, relative latency spikes, passive load comparison, route-trace bounds, charts, OAuth validation, update manifest/hash validation, localization, and AI privacy tests. On a Windows PC with WebView2 Runtime, `tests\run-webview2-smoke.ps1` additionally checks runtime initialization, successful local navigation, loopback error detection, and Back; it sends no speed-test traffic to external providers. The GitHub Actions workflow builds the app and runs the deterministic suite on Windows. Automated tests cannot substitute for testing every adapter, VPN, Windows locale, router, or network.

## License

MIT. See [LICENSE](LICENSE).
