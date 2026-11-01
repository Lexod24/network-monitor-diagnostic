# Network Monitor Diagnostic

A portable Windows desktop app that monitors connection quality and produces a transparent, evidence-based diagnosis. English is selected by default; Romanian is available in the app.

## Download and start

[Download the portable Windows app (.zip)](https://raw.githubusercontent.com/Lexod24/network-monitor-diagnostic/main/NetworkMonitor-Portable.zip). Extract the ZIP and double-click `NetworkMonitor.exe`. No administrator rights or installation are required. The app does not change Windows or router settings.

## Use the app

1. Select **Start monitoring** while the connection is behaving poorly.
2. Leave the app running during the issue. Five seconds is the target sampling interval; the report shows the measured average interval.
3. Select **Stop and analyze**. The window shows findings, evidence, limitations, and practical next steps. A CSV and a text report are saved locally under `%LOCALAPPDATA%\NetworkMonitor\Logs`.
4. The local evidence-based diagnosis is always available. For an optional AI explanation, choose **AI options**. You can use local Ollama, Gemini API, or Groq API. **Continue with ChatGPT** remains available for eligible ChatGPT accounts; it opens the official sign-in and requests separate approval.

AI is optional. Without it, the complete local diagnosis still works. AI output is supplemental and may be wrong; compare it with the measurements and deterministic local findings. No provider keys are bundled and the publisher does not pay for other users' requests. Cloud providers require each user to supply their own key and are subject to that provider's quotas, account eligibility, data terms, and possible charges. Keys are protected with Windows DPAPI for the current Windows user. Ollama runs on the user's own computer and requires a separately installed model.
The sign-in callback listens on `127.0.0.1`, preferring port `1455` and selecting another available port if needed. Keep the app open and finish browser sign-in within 15 minutes.

## Privacy

AI requests contain only aggregated counts and averages from the session. They exclude raw CSV content, SSID, IP addresses, exact timestamps, and credentials. With Ollama, the summary remains on the computer. With Gemini or Groq, it is sent to the selected provider after the user configures a key. Review that provider's current data terms and quotas before use. ChatGPT access separately requires the user to sign in and approve plan use. ChatGPT OAuth tokens and cloud-provider keys are protected locally with Windows DPAPI for the current Windows user. The app also retains the issued public ChatGPT client ID locally if a first token exchange must be retried. Disconnecting ChatGPT removes its local credentials and registration ID; it does not remove the separately configured AI provider. Network monitoring itself sends ICMP and DNS probes and small HTTPS requests to the documented endpoints below.

## Optional AI providers

- **Local diagnosis only:** default behavior; no account, key, or AI request.
- **Ollama:** install [Ollama for Windows](https://ollama.com/download/windows), then run `ollama run qwen3:4b` once in a terminal to download the approximately 2.5 GB model. Keep Ollama running and select `Ollama` under **AI options**. This option needs no account or API key; inference runs locally. Hardware requirements and speed vary by computer.
- **Gemini API:** create your own key in [Google AI Studio](https://aistudio.google.com/apikey), select `Gemini API`, and use the suggested `gemini-3-flash-preview` model or another model currently available to your account. Free quotas and data handling vary by model/account; check Google's current [pricing and terms](https://ai.google.dev/gemini-api/docs/pricing).
- **Groq API:** create your own key in [Groq Console](https://console.groq.com/keys), select `Groq API`, and use the suggested `openai/gpt-oss-20b` model or another currently available model. Limits and account terms can change; see [Groq rate limits](https://console.groq.com/docs/rate-limits).

The provider selected in **AI options** is used for explanation after monitoring stops. When no alternative provider is selected, an existing ChatGPT connection can be used. No AI provider is configured by default. The app does not automatically create accounts, keys, or paid usage for the publisher.

## Measurements and diagnosis

- Wi-Fi connection state and Windows-reported signal strength when available.
- ICMP latency and rolling loss to the gateway and three independent external IP targets (Cloudflare, Google, and Quad9). The report uses the median of replying targets and distinguishes a filtered/single-target response from a broad reachability outage.
- The active Windows route interface is selected for the external probe and passive traffic counters. Interface IDs are used only in memory; logs store only a broad type (Wi-Fi, Ethernet, or other). Route changes are marked as events.
- Live latency graph for the latest 60 samples (about five minutes), plus successful-probe minimum, median, p95, maximum, and gap-aware jitter in the final report.
- Passive upload/download rates from the active route interface. The report compares quiet samples (<0.25 Mbps) with active samples (>=1 Mbps) only when it has at least three of each. This is an observation of this PC's traffic, not a controlled bufferbloat or speed test; it does not include other devices and generates no traffic.
- When fewer than two external IP targets reply, three concurrent HTTPS checks to Microsoft, Google, and Cloudflare. The app validates expected endpoint responses and rejects redirects or captive portal pages.
- DNS lookups once a minute for three domains using the configured Windows resolver.
- An optional **Trace route** button. After confirmation, Windows `tracert` sends a small number of ICMP TTL probes to `1.1.1.1`, up to eight hops, with a 12-second safety limit. Hop IPs appear in a local dialog only; the trace is not saved, logged, or sent to AI. Routers may hide hops or ignore traceroute.
- CSV events with per-target and per-endpoint results, timestamps, and rotation at 10 MB (up to 10 rotated files). External target IP addresses, SSIDs, BSSIDs, adapter names, and interface IDs are not saved.

The report compares gateway, Internet, HTTPS, and DNS evidence, suggests the first likely affected segment, and labels confidence and limits. It avoids inferring an ISP or hardware fault from one check. ICMP may be filtered or rate-limited, Wi-Fi signal may be unavailable, and DNS may use the Windows cache. Firewalls, VPNs, proxies, captive portals, regional routing, or endpoint incidents can affect results. A few public probe services cannot represent every game or website; use **Trace route** during the issue for a route snapshot.

## Build and test

Run `build.ps1` on Windows to create `dist\NetworkMonitor.exe`. Run `tests\run-self-tests.ps1` for deterministic target aggregation, segment diagnosis, passive load comparison, route-trace bounds, CSV privacy/schema migration, chart rendering, OAuth validation, and AI request privacy tests. The GitHub Actions workflow builds the app and runs the same suite on Windows.

## License

MIT. See [LICENSE](LICENSE).
