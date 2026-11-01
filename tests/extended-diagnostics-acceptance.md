# Extended diagnostics acceptance matrix

This is the release gate for the six-part extended-diagnostics specification. A row passes only when its deterministic test exists and passes; scenarios requiring real Windows hardware must also be recorded as manual validation and must not be inferred from a mocked test.

## Wi-Fi telemetry provenance and privacy

- [ ] Read current association through the documented Windows Native Wi-Fi API, selecting the interface associated with the active route.
- [ ] Signal quality, channel, band, PHY/radio, RX/TX link rates each expose value, source, precision, and unavailable state.
- [ ] Band comes from a reported frequency, not channel-number guessing; unknown frequency remains unknown.
- [ ] Roaming/AP change is detected in memory without storing SSID/BSSID in normal CSV, report, or AI payload.
- [ ] SSID/BSSID can enter an export only after an explicit opt-in; preview shows exactly what will be disclosed.
- [ ] No Wi-Fi interface, permission/policy denial, WLAN service stopped, unsupported driver, disconnected state, and malformed native response all degrade safely.

## Windows event evidence

- [ ] Only allowlisted WLAN, network adapter, DHCP, and DNS categories are read from the current session interval.
- [ ] Event output contains category, timestamp, provider/channel category, and event ID only; never message text, rendered XML, SSID/BSSID, adapter GUID, or IP address.
- [ ] Access denied is distinguished from disabled/missing/unreadable channels, without elevation; probe collection continues.
- [ ] Duplicate polling does not duplicate timeline entries; events outside the session are excluded.

## Controlled loaded-latency test

- [ ] Uses only the documented Cloudflare Speedtest measurement API/SDK and HTTPS endpoints; no provider results are scraped from its web page.
- [ ] Before confirmation, names Cloudflare, bounded maximum transfer, duration cap, and third-party processing/retention disclosure.
- [ ] Test is opt-in, cancellable, never runs at startup, has no history, and its measurements never enter AI or standard loss diagnosis.
- [ ] Reports baseline, download-loaded, and upload-loaded latency separately; failed endpoint, partial phases, and cancellation are explicit.
- [ ] User cancellation and hard timeout stop outstanding requests and release temporary buffers.

## User-selected destination

- [ ] Validates host/IP/port, including IPv4, IPv6, IDN and malformed input; rejects URLs with credentials, paths, or unsupported schemes where not allowed.
- [ ] Performs DNS (when applicable), bounded TCP, TLS certificate validation, HTTPS, and bounded route trace for only the selected host.
- [ ] Every stage has a timeout and cancellation; cancellation closes sockets/processes.
- [ ] ICMP/traceroute silence is reported as no ICMP response, never proof of server failure; TCP/TLS/HTTPS evidence is independent.
- [ ] No port sweep, subnet discovery, or destination is treated as a game-server defect solely because ping is filtered.

## Session timeline and incident report

- [ ] One time-ordered in-memory timeline correlates session probes, outage/spike, route/interface transitions, Wi-Fi changes, allowlisted Windows events, destination diagnostic, and the start/end of excluded controlled tests.
- [ ] Report includes interval, measurements, evidence, observation, interpretation, confidence, alternatives, limits, and ordered next steps for each conclusion.
- [ ] Default preview/export redacts SSID/BSSID, target host/IP, and adapter identifiers; optional disclosure is explicit and previewed.
- [ ] Report is only copied/exported after user action and preview; nothing writes a saved incident report automatically.
- [ ] Export/clipboard contains no AI explanation unless separately asked; AI input remains aggregate-only and excludes identities, precise timestamps, target, and test results.

## Required regression scenarios

- [ ] ICMP filtered while HTTPS/TCP succeeds.
- [ ] Partial DNS failure while IP reachability remains.
- [ ] Captive portal/interception with independent HTTPS endpoint disagreement.
- [ ] IPv6 unavailable and IPv6-only routing.
- [ ] VPN/tunnel active and route switch.
- [ ] Wi-Fi to Ethernet, Ethernet to Wi-Fi, roaming between BSSIDs, and adapter reset/disconnect.
- [ ] Event logs readable, access denied, disabled, and missing.
- [ ] User destination DNS failure, TCP refusal, TLS/certificate failure, HTTPS error, route partial/no replies, timeout, and cancellation.
- [ ] Cloudflare test unavailable/failed, partial phase, cancellation, and timeout.
- [ ] Instant language switch while each page/dialog/report is open, with no stale English/Romanian strings.
- [ ] Privacy scan of CSV, logs, local report preview/export, AI payload, and test artifacts for identifiers and raw event messages.
