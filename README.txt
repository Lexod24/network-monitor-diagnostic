NETWORK MONITOR DIAGNOSTIC

Start: Double-click NetworkMonitor.exe. No installation or administrator rights are required.

1. Click Start monitoring while the connection problem is happening.
2. Leave the app running for the duration of the problem.
3. Click Stop and analyze to view the report. CSV and report files are saved locally under:
   %LOCALAPPDATA%\NetworkMonitor\Logs

Optional AI explanation:
The complete evidence-based local diagnosis is always available. Select AI options to configure local Ollama or a Gemini/Groq API key that you own. Continue with ChatGPT remains available for eligible ChatGPT accounts and requires separate approval. AI is supplementary. No shared key is included and the publisher does not pay for other users.
Keep the app open while signing in. The local callback uses 127.0.0.1 port 1455 when available, or another free port, and waits up to 15 minutes.

AI receives only aggregated measurements, not raw CSV, Wi-Fi name, IP addresses, exact timestamps, or credentials. Ollama keeps the summary local. Gemini/Groq send it to the selected provider; review its data terms and quotas. API keys and ChatGPT OAuth credentials are protected locally for the current Windows user. Disconnect ChatGPT removes its local session and registration ID and attempts remote revocation. Provider settings are separate.

Ollama: install from https://ollama.com/download/windows and run `ollama run qwen3:4b` once to download the local model (~2.5 GB), then select Ollama in AI options.
Gemini: create your own key at https://aistudio.google.com/apikey. Groq: create your own key at https://console.groq.com/keys. Each user supplies their own key; quotas, eligibility, data terms, and charges are controlled by that provider.

To switch languages, use the language selector in the app. English is selected by default; Romanian is also available.

The app draws a live latency graph for the latest 60 samples (about five minutes). The final diagnosis also reports minimum, median, p95, maximum, and jitter from successful gateway and Internet ping samples. Missing samples break the jitter sequence, so outages are not treated as ordinary latency variation.
Passive upload/download rates are measured on the active gateway interface. They show this computer's current traffic, not the connection's maximum speed.

Romana: Porneste monitorizarea in timpul problemei si apasa Opreste si analizeaza dupa ce s-a produs. Raportul si fisierele CSV se salveaza local in %LOCALAPPDATA%\NetworkMonitor\Logs. Analiza locala completa functioneaza fara cont sau cheie. Optional, configureaza Ollama local sau foloseste o cheie proprie Gemini/Groq din AI options. Pentru ChatGPT, apasa Continue with ChatGPT si aproba permisiunea unui cont eligibil. Furnizorul selectat primeste numai sumarul agregat; verifica termenii si limitele sale.

GitHub source and details: https://github.com/Lexod24/network-monitor-diagnostic
License: MIT.
