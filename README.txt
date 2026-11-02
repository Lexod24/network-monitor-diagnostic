NETWORK MONITOR DIAGNOSTIC

Start: Double-click NetworkMonitor.exe. No installation or administrator rights are required.

The app checks for a newer version at startup. It will notify you, ask before downloading, show the download and verification status, verify the package SHA-256, and restart to install. It will not update during an active monitoring session. Update checks send no diagnostics, CSV, Wi-Fi name, IP addresses, credentials, or usage data.

1. Click Start monitoring while the connection problem is happening.
2. Leave the app running for the duration of the problem.
3. Click Stop and analyze to view the local report. Probe measurements are saved in a rotating CSV under:
   %LOCALAPPDATA%\NetworkMonitor\Logs
   The report is not written automatically. Choose Copy diagnosis to preview it, then copy or export it yourself. Wi-Fi SSID/BSSID are hidden unless you explicitly enable their disclosure in the preview.

Speed test:
Open Speed test, choose Ookla, Netograf, DIGI, or Vodafone, review the traffic and privacy notice, then click Load test. Microsoft WebView2 Runtime is required; if it is missing or the provider page fails, the app offers an explicit Open in browser fallback. Provider pages load as top-level pages; the app does not bypass embedding restrictions, scrape or save results, or transmit speed-test values to AI. Providers independently process traffic and may receive your public IP and browser/connection details. Speed tests can use substantial data and affect monitoring or other apps. A temporary WebView2 profile is removed when the app closes. The app provides the external test only and does not retain speed-test results. Vodafone's web endpoint may be unavailable; the app will show a fallback rather than claiming the test succeeded.

Latency under load:
Use the optional Latency under load action to compare unloaded latency with latency during download and upload. Review the disclosure in the app's native dark glass window. No test traffic is sent until you click Start test; Cancel remains visible. The test uses Cloudflare's documented SDK and HTTPS endpoints, needs no account or API key, and the app does not charge for this feature. Cloudflare receives the test requests and may process your public IP and connection metadata. The test is capped at 24 MiB payload plus protocol/header traffic and 60 seconds; your Internet plan may count the transferred data. Ordinary monitoring pauses only after you start the test. Numeric results stay only in the test window and are not saved or sent to AI; the session report may contain only the test interval and outcome.
Optional AI explanation:
The complete evidence-based local diagnosis is always available. Select AI options to configure local Ollama or a Gemini/Groq API key that you own. Continue with ChatGPT remains available for eligible ChatGPT accounts and requires separate approval. AI is supplementary. No shared key is included and the publisher does not pay for other users.
Keep the app open while signing in. The local callback uses 127.0.0.1 port 1455 when available, or another free port, and waits up to 15 minutes.

AI receives only aggregated measurements, not raw CSV, SSID/BSSID, Windows event message text, IP addresses, exact timestamps, or credentials. The optional aggregate can include Wi-Fi channel/band/radio/link rates and counts by Windows event category. Ollama keeps the summary local. Gemini/Groq send it to the selected provider; review its data terms and quotas. API keys and ChatGPT OAuth credentials are protected locally for the current Windows user. Disconnect ChatGPT removes its local session and registration ID and attempts remote revocation. Provider settings are separate.

Ollama: install from https://ollama.com/download/windows and run `ollama run qwen3:4b` once to download the local model (~2.5 GB), then select Ollama in AI options.
Gemini: create your own key at https://aistudio.google.com/apikey. Groq: create your own key at https://console.groq.com/keys. Each user supplies their own key; quotas, eligibility, data terms, and charges are controlled by that provider.

To switch languages, use the language selector in the app. English is selected by default; Romanian is also available.

The app checks the gateway and three independent external targets (Cloudflare, Google, and Quad9) over IPv4 and IPv6 every five seconds. The address families are reported separately. ICMP non-response is not proof of packet loss; one failed target is not a general outage.
Three HTTPS endpoints are checked at least once a minute and sooner when fewer than two IP targets reply. Multiple unexpected redirects or HTML responses create a captive portal/interception suspicion, not confirmation. The app records only whether the Windows proxy is used, never its address. DNS is checked once a minute.
Windows selects the active route interface, preferring IPv4 and using IPv6 if IPv4 routing is unavailable. Tunnel-type interfaces are labeled, but do not reliably identify every VPN. The app marks route changes and stores no adapter name or interface ID.
Partial endpoint response is distinct from a suspected outage. A Wi-Fi disconnect is treated as relevant only when Wi-Fi is the active route. Latency spikes need at least three recent successful samples and must exceed the recent median by at least 50 ms or 50%; stable high latency is not called a spike.
The final report suggests the first likely affected segment based on repeated evidence and explains confidence and limits. It does not claim to prove an ISP or hardware fault. Public probes cannot determine the health of every game or website.

The app draws a live latency graph for the latest 60 samples (about five minutes). The final diagnosis reports minimum, median, p95, maximum, and jitter from successful gateway, IPv4, and IPv6 probes. Missing samples break the jitter sequence.
Passive upload/download rates are measured on the active route interface. Quiet (<0.25 Mbps) and active (>=1 Mbps) samples are compared only when there are at least three of each. This observes this PC's current traffic; it is not a controlled bufferbloat/speed test and generates no traffic. Other devices are not included.

When Wi-Fi is the active route, the app reads available signal quality, channel, band, PHY/radio type, and link rates from Windows. Each value reports its source and precision in the diagnosis. Band is mapped from the center frequency Windows reports; the app does not infer it from channel numbers. If Windows does not document the units of native link rates, those values are withheld and the app uses the explicitly labeled Mbps netsh reading when available. Link rates are not Internet speeds. Access-point changes are detected in memory using a one-way BSSID hash; the BSSID itself is not retained in standard CSV or reports.
At the start of each monitoring session, you can allow the app to read relevant WLAN AutoConfig, DNS Client, DHCP Client, and System event records. It stores selected categories, event IDs, and times only, not event message text. Access denied or unavailable logs do not stop monitoring and no administrator rights are requested. Event log evidence is a clue, not proof of cause.

Optional Trace route: after confirmation, Windows sends a small number of ICMP TTL probes to an IPv4 or IPv6 target, up to eight hops, with a 12-second limit. Hop IPs appear in a dialog on this PC only. They are not saved or sent to AI. Some routers do not answer traceroute.
CSV stores probe service names and statuses, IPv4/IPv6 measurements, proxy-use boolean, and portal suspicion boolean, but not target IP addresses, SSIDs, BSSIDs, adapter names, interface IDs, proxy address, or redirect URL. Logs rotate at 10 MB, keeping up to 10 rotated files.

Romana: Porneste monitorizarea in timpul problemei si apasa Opreste si analizeaza dupa ce s-a produs. Masuratorile CSV se salveaza local in %LOCALAPPDATA%\NetworkMonitor\Logs; raportul nu se scrie automat. Apasa Copiaza diagnosticul pentru previzualizare, apoi alege copierea sau exportul. SSID/BSSID sunt ascunse implicit. Analiza locala completa functioneaza fara cont sau cheie. Optional, configureaza Ollama local sau foloseste o cheie proprie Gemini/Groq din AI options. Pentru ChatGPT, apasa Continue with ChatGPT si aproba permisiunea unui cont eligibil. Furnizorul selectat primeste numai sumarul agregat; verifica termenii si limitele sale.
Romana: Porneste monitorizarea in timpul problemei si las-o activa cat folosesti conexiunea. Aplicatia compara gateway-ul cu trei tinte IP independente; HTTPS se verifica daca raspund mai putin de doua. Raportul sugereaza segmentul probabil si arata dovezile si limitele. Comparatia traficului este pasiva, numai pentru traficul acestui calculator, fara viteza de test generata. La inceputul sesiunii poti permite citirea categoriilor relevante din jurnalele Windows; mesajele brute nu sunt pastrate, iar accesul refuzat nu opreste monitorizarea. Butonul optional Testeaza ruta cere confirmare; hopurile sunt afisate local si nu sunt salvate.

GitHub source and details: https://github.com/Lexod24/network-monitor-diagnostic
License: MIT.

Română: Testul opțional «Latență sub sarcină» compară latența fără trafic cu latența la descărcare și încărcare. Folosește SDK-ul documentat Cloudflare, fără cont sau cheie API; aplicația nu percepe bani pentru această funcție. Cloudflare primește cererile și poate procesa IP-ul public și metadatele conexiunii. Limita este 24 MiB payload plus antete/protocol și maximum 60 secunde; traficul poate fi contorizat de abonamentul tău de Internet. Monitorizarea obișnuită se suspendă în timpul testului. Valorile numerice rămân temporare și nu se salvează sau trimit la AI; raportul sesiunii poate păstra doar intervalul și starea testului.

