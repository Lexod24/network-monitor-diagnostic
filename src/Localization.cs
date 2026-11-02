using System;
using System.Globalization;

namespace NetworkMonitorApp
{
    internal enum AppLanguage { English, Romanian }

    internal static class Localization
    {
        public static string Get(AppLanguage language, string key)
        {
            bool ro = language == AppLanguage.Romanian;
            switch (key)
            {
                case "AppTitle": return ro ? "Monitor retea - Diagnostic" : "Network Monitor - Diagnosis";
                case "Heading": return ro ? "Monitorizare conexiune Wi-Fi si Internet" : "Wi-Fi and Internet connection monitor";
                case "Language": return ro ? "Limba:" : "Language:";
                case "English": return "English";
                case "Romanian": return ro ? "Română" : "Romanian";
                case "Start": return ro ? "Porneste monitorizarea" : "Start monitoring";
                case "StartingMonitoring": return ro ? "Pornesc monitorizarea..." : "Starting probes...";
                case "Stop": return ro ? "Opreste si analizeaza" : "Stop and analyze";
                case "Restart": return ro ? "Porneste din nou" : "Start again";
                case "Stopped": return ro ? "Oprit - datele raman locale" : "Stopped - data stays local";
                case "Running": return ro ? "Ruleaza - colecteaza probe locale" : "Running - collecting local probes";
                case "Stopping": return ro ? "Oprire si analiza..." : "Stopping and analyzing...";
                case "AIDisconnecting": return ro ? "Deconectez ChatGPT..." : "Disconnecting ChatGPT...";
                case "IcmpNoReply": return ro ? "fără răspuns ICMP " : "ICMP no reply ";
                case "SessionAnalyzed": return ro ? "Sesiune analizată" : "Session analyzed";
                case "ReportPreparing": return ro ? "Pregătesc raportul sesiunii..." : "Preparing the session report...";
                case "InitialMonitoringSummary": return ro ? "Pornește monitorizarea când apare problema. Diagnosticul local nu necesită cont sau cheie AI." : "Start monitoring when the issue happens. Local diagnosis needs no account or AI key.";
                case "ActiveMonitoringSummary": return ro ? "Probează gateway-ul, DNS-ul, HTTPS și țintele IPv4/IPv6. Lasă aplicația pornită cât timp observi problema." : "Checking your gateway, DNS, HTTPS, and IPv4/IPv6 targets. Keep the app running while the issue occurs.";
                case "MonitoringSummary": return ro ? "Monitorizarea rulează. Las-o activă până când apare sau trece problema." : "Monitoring is running. Leave it active until the issue appears or passes.";
                case "SessionStoppedSummary": return ro ? "Sesiune oprită. Deschide diagnosticul pentru dovezi și pașii următori." : "Session stopped. Open Diagnosis for evidence and next steps.";
                case "SessionReportReadySummary": return ro ? "Raport pregătit din {0} măsurători. Vezi constatările și pașii următori." : "Report ready from {0} samples. Review the findings and next steps.";
                case "OpenLogs": return ro ? "Deschide jurnalele" : "Open logs";
                case "CopyReport": return ro ? "Copiază diagnosticul" : "Copy diagnosis";
                case "RouteTraceButton": return ro ? "Testează ruta" : "Trace route";
                case "LoadButton": return ro ? "Latență sub sarcină" : "Latency under load";
                case "LoadTitle": return ro ? "Latență sub sarcină" : "Latency under load";
                case "LoadPreparing": return ro ? "Pregătesc testul; probele obișnuite sunt suspendate..." : "Preparing test; ordinary probes are paused...";
                case "LoadRunning": return ro ? "Testul Cloudflare rulează. Valorile sunt temporare." : "Cloudflare test is running. Values are temporary.";
                case "LoadCancel": return ro ? "Anulează testul" : "Cancel test";
                case "LoadStartButton": return ro ? "Începe testul" : "Start test";
                case "LoadDisclosureTitle": return ro ? "Înainte să începi" : "Before you start";
                case "LoadReady": return ro ? "Nu s-a trimis trafic. Apasă «Începe testul» când ești pregătit." : "No traffic has been sent. Select Start test when you are ready.";
                case "LoadNoData": return ro ? "Rezultatele vor apărea aici după începerea testului." : "Results will appear here after the test starts.";
                case "LoadConnecting": return ro ? "Conectez testul Cloudflare..." : "Preparing the Cloudflare test...";
                case "LoadStartFailed": return ro ? "Testul nu a pornit. Verifică WebView2 și încearcă din nou." : "The test did not start. Check WebView2 and try again.";
                case "LoadClose": return ro ? "Închide" : "Close";
                case "LoadRuntimeUnavailable": return ro ? "Testul nu a putut porni deoarece WebView2 nu este disponibil." : "The test could not start because WebView2 is unavailable.";
                case "LoadProbeBusy": return ro ? "O probă de monitorizare încă rulează; testul de sarcină nu a pornit. Încearcă din nou." : "A monitoring probe is still running; the load test did not start. Try again.";
                case "LoadHeading": return ro ? "Latență fără sarcină și sub trafic" : "Latency unloaded and under traffic";
                case "LoadSubtitle": return ro ? "Test Cloudflare opțional · rezultatele nu sunt păstrate de aplicație" : "Optional Cloudflare test · results are not retained by the app";
                case "LoadBaseline": return ro ? "Latență fără sarcină" : "Unloaded latency";
                case "LoadDownload": return ro ? "Latență sub descărcare" : "Latency under download";
                case "LoadUpload": return ro ? "Latență sub încărcare" : "Latency under upload";
                case "LoadDownloadRate": return ro ? "Viteză descărcare" : "Download rate";
                case "LoadUploadRate": return ro ? "Viteză încărcare" : "Upload rate";
                case "LoadStarting": return ro ? "Pregătesc testul..." : "Preparing test...";
                case "LoadLatencyPhase": return ro ? "Măsor latența fără sarcină..." : "Measuring unloaded latency...";
                case "LoadDownloadPhase": return ro ? "Măsor latența și debitul la descărcare..." : "Measuring download latency and throughput...";
                case "LoadUploadPhase": return ro ? "Măsor latența și debitul la încărcare..." : "Measuring upload latency and throughput...";
                case "LoadFinished": return ro ? "Test terminat. Valorile sunt temporare și nu au fost salvate." : "Test finished. Values are temporary and were not saved.";
                case "LoadFailed": return ro ? "Testul Cloudflare a eșuat sau a fost întrerupt. Rezultatele parțiale sunt afișate unde sunt disponibile." : "The Cloudflare test failed or was interrupted. Partial results are shown where available.";
                case "LoadCancelled": return ro ? "Test anulat; cererea în curs a fost oprită." : "Test cancelled; the active request was stopped.";
                case "LoadTimeout": return ro ? "Test oprit după atingerea limitei de 60 de secunde." : "Test stopped after reaching the 60-second limit.";
                case "LoadOutcomeCompleted": return ro ? "terminat" : "completed";
                case "LoadOutcomeFailed": return ro ? "eșuat" : "failed";
                case "LoadOutcomeCancelled": return ro ? "anulat" : "cancelled";
                case "LoadOutcomeTimeout": return ro ? "limită de timp" : "timed out";
                case "LoadOutcomeRunning": return ro ? "în curs" : "in progress";
                case "LoadOutcomeNotStarted": return ro ? "nepornit" : "not started";
                case "LoadOutcomeUnavailable": return ro ? "indisponibil" : "unavailable";
                case "LoadTimelineHeading": return ro ? "TEST DE LATENȚĂ SUB SARCINĂ — interval exclus din probele obișnuite" : "LATENCY-UNDER-LOAD TEST — interval excluded from ordinary probes";
                case "LoadTimelineStarted": return ro ? "a început testul Cloudflare; probele obișnuite au fost suspendate" : "Cloudflare test started; ordinary probes were paused";
                case "LoadTimelineEnded": return ro ? "s-a încheiat testul Cloudflare; probele obișnuite au fost reluate" : "Cloudflare test ended; ordinary probes resumed";
                case "LoadTimelineOutcome": return ro ? "Rezultat" : "Outcome";
                case "LoadTimelineExcluded": return ro ? "Latența și pierderile obișnuite nu au fost măsurate în acest interval; valorile testului sunt afișate separat și nu intră în diagnosticul obișnuit sau analiza AI." : "Ordinary latency and loss were not measured during this interval; load-test values are shown separately and are excluded from ordinary diagnosis and AI analysis.";
                case "RouteTraceConfirmTitle": return ro ? "Test de ruta" : "Route trace";
                case "RouteTraceConfirmPrompt": return ro ? "Testul trimite probe ICMP cu volum redus catre o tinta IPv4 sau IPv6, in functie de ruta disponibila. Routerele intermediare pot aparea ca adrese IP. Rezultatul este afisat doar pe acest calculator; nu este salvat sau trimis. Unele routere nu raspund. Continui?" : "This sends a small number of ICMP TTL probes to an IPv4 or IPv6 target, based on the available route. Intermediate routers may appear as IP addresses. Results are shown only on this PC and are not saved or sent. Some routers do not reply. Continue?";
                case "RouteTraceRunning": return ro ? "Se testeaza ruta..." : "Tracing route...";
                case "RouteTraceDialogTitle": return ro ? "Traseul conexiunii" : "Route trace";
                case "RouteTraceSubtitle": return ro ? "Catre {0} · maximum 8 salturi" : "To {0} · up to 8 hops";
                case "RouteTracePrivacyNote": return ro ? "Rezultatul ramane pe acest calculator. Routerele pot ignora probele; un timeout izolat nu dovedeste o defectiune." : "Results stay on this PC. Routers may ignore probes; a single timeout does not prove a connection fault.";
                case "RouteTraceSummaryReached": return ro ? "Tinta a raspuns la ultimul salt." : "The target responded at the final hop.";
                case "RouteTraceSummaryPartial": return ro ? "Ruta a fost observata, dar tinta nu a fost atinsa in limita de 8 salturi. Unele routere pot sa nu raspunda." : "A route was observed, but the target was not reached within 8 hops. Some routers may not respond.";
                case "RouteTraceSummaryNoReplies": return ro ? "Niciun salt nu a raspuns. Acest rezultat singur nu confirma o intrerupere." : "No hops responded. This result alone does not confirm an outage.";
                case "RouteTraceSummaryUnavailable": return ro ? "Windows nu a returnat randuri de traseu interpretabile." : "Windows returned no readable hop details.";
                case "RouteTraceHopCount": return ro ? "SALTURI OBSERVATE" : "HOPS OBSERVED";
                case "RouteTraceColumnHop": return ro ? "Salt" : "Hop";
                case "RouteTraceColumnProbe1": return ro ? "Proba 1" : "Probe 1";
                case "RouteTraceColumnProbe2": return ro ? "Proba 2" : "Probe 2";
                case "RouteTraceColumnProbe3": return ro ? "Proba 3" : "Probe 3";
                case "RouteTraceColumnAddress": return ro ? "Router / adresa" : "Router / address";
                case "RouteTraceColumnResponse": return ro ? "Raspuns" : "Response";
                case "RouteTraceResponded": return ro ? "A raspuns" : "Responded";
                case "RouteTraceNoReply": return ro ? "Fara raspuns" : "No reply";
                case "RouteTraceClose": return ro ? "Inchide" : "Close";
                case "RouteTraceRawDetails": return ro ? "Detaliu raportat de Windows:" : "Windows detail:";
                case "RouteTraceLocalFooter": return ro ? "Traseul nu este salvat. Apasa Esc pentru a inchide." : "The trace is not saved. Press Esc to close.";
                case "ContinueChatGPT": return ro ? "Continuă cu ChatGPT" : "Continue with ChatGPT";
                case "AISettings": return ro ? "Setări AI" : "AI options";
                case "LatencyChartTitle": return ro ? "Latență (ms) - ultimele 60 de esantioane" : "Latency (ms) - latest 60 samples";
                case "LatencyChartGateway": return ro ? "Router" : "Gateway";
                case "LatencyChartInternet": return ro ? "Internet" : "Internet";
                case "LatencyChartEmpty": return ro ? "Porneste monitorizarea pentru a vedea istoricul latenței" : "Start monitoring to draw the latency history";
                case "DisconnectChatGPT": return ro ? "Deconectează ChatGPT" : "Disconnect ChatGPT";
                case "AIConnected": return ro ? "AI ChatGPT conectat" : "ChatGPT AI connected";
                case "AINotConnected": return ro ? "AI neconfigurat - analiza locala disponibila" : "AI not configured - local analysis available";
                case "AIProviderReady": return ro ? "Furnizor AI configurat" : "AI provider configured";
                case "AISigningIn": return ro ? "Autentificare ChatGPT in browser..." : "Signing in to ChatGPT in browser...";
                case "AIAnalyzing": return ro ? "AI analizeaza sumarul masuratorilor..." : "AI is analyzing the measurement summary...";
                case "AIUnavailable": return ro ? "AI indisponibil; afisez analiza locala." : "AI unavailable; showing local analysis.";
                case "NoSamples": return ro ? "Nu exista masuratori suficiente pentru diagnostic." : "There are not enough measurements for a diagnosis.";
                case "InitialDiagnosis": return ro ? "Apasa Porneste monitorizarea. Compara mai multe tinte si ruleaza cat apare problema. Comparatia de trafic este pasiva; aplicatia nu genereaza trafic." : "Press Start monitoring. Multiple targets are compared; keep it running while the issue occurs. Traffic comparison is passive; the app does not generate traffic.";
                case "Footer": return ro ? "Tinte independente la 5 sec | DNS la 60 sec | Fara IP-uri/SSID salvate | Fara administrator" : "Independent targets every 5 sec | DNS every 60 sec | No IPs/SSID saved | No admin needed";
                case "MetricWifi": return ro ? "Wi-Fi" : "Wi-Fi";
                case "MetricSignal": return ro ? "Semnal" : "Signal";
                case "MetricGateway": return ro ? "Router" : "Gateway";
                case "MetricInternet": return ro ? "Internet" : "Internet";
                case "MetricDns": return ro ? "DNS" : "DNS";
                case "MetricDownload": return ro ? "Descarcare" : "Download";
                case "MetricUpload": return ro ? "Incarcare" : "Upload";
                case "NoValue": return ro ? "—" : "n/a";
                case "Connected": return ro ? "Conectat" : "Connected";
                case "Disconnected": return ro ? "Deconectat" : "Disconnected";
                case "Unavailable": return ro ? "Indisponibil" : "Unavailable";
                case "Degraded": return ro ? "Degradat" : "Degraded";
                case "OK": return ro ? "OK" : "OK";
                case "OutageSuspected": return ro ? "Intrerupere suspectata" : "Outage suspected";
                case "Events": return ro ? "Evenimente" : "Events";
                case "CollectWindowsEventsPrompt": return ro ? "Opțional: permiți citirea, numai pe durata acestei sesiuni, a unor categorii de evenimente Wi-Fi, adaptor, DHCP și DNS din jurnalele Windows? Se salvează doar categoria, ora și codul evenimentului, nu textul mesajelor. Accesul refuzat nu oprește monitorizarea și nu este necesar administrator." : "Optional: allow reading Wi-Fi, adapter, DHCP, and DNS event categories from Windows logs for this session only? Only category, time, and event code are retained, never message text. Denied access does not stop monitoring and administrator rights are not required.";
                case "State": return ro ? "Stare" : "State";
                case "Time": return ro ? "Ora locala" : "Local time";
                case "GridWiFi": return "Wi-Fi";
                case "GridSignal": return ro ? "Semnal" : "Signal";
                case "GridGateway": return ro ? "Router" : "Gateway";
                case "GridInternet": return "Internet";
                case "GridTargets": return ro ? "Tinte externe" : "External targets";
                case "GridDNS": return "DNS";
                case "DiagnosisGroup": return ro ? "Diagnostic - constatari, dovezi si limite" : "Diagnosis - findings, evidence, and limits";
                case "ReportTitle": return ro ? "DIAGNOSTIC LOCAL - Reguli transparente" : "LOCAL DIAGNOSIS - Transparent rules";
                case "ReportTitleAI": return ro ? "DIAGNOSTIC CU EXPLICATIE AI - Dovezile sunt masurate local" : "AI-EXPLAINED DIAGNOSIS - Evidence measured locally";
                case "Interval": return ro ? "Interval" : "Time span";
                case "SampleCount": return ro ? "Esantioane" : "Samples";
                case "NominalFrequency": return ro ? "frecventa nominala: 5 secunde" : "nominal interval: 5 seconds";
                case "Averages": return ro ? "Medii masurate" : "Measured averages";
                case "Findings": return ro ? "CONSTATARE SI INTERPRETARE" : "FINDINGS AND INTERPRETATION";
                case "NextSteps": return ro ? "CE POTI FACE ACUM" : "WHAT TO DO NEXT";
                case "NoAnomalies": return ro ? "Nu s-au detectat intreruperi sau anomalii peste pragurile definite in perioada masurata. Asta nu exclude probleme din afara intervalului inregistrat." : "No interruptions or anomalies above the defined thresholds were detected during this session. This does not rule out problems outside the recorded period.";
                case "Limitations": return ro ? "Limite: ping-ul poate fi filtrat. Wi-Fi-ul poate lipsi daca Windows nu expune informatia. O singura destinatie nereusita nu confirma o cadere generala; comparatia mai multor endpointuri reduce, dar nu elimina, ambiguitatea. Jurnalul nu poate dovedi singur defecte hardware sau vina furnizorului." : "Limits: ping may be filtered. Wi-Fi details may be unavailable if Windows does not expose them. One failed destination does not prove a general outage; comparing multiple endpoints reduces, but does not remove, ambiguity. The log alone cannot prove hardware failure or ISP fault.";
                case "AIDataNotice": return ro ? "AI este optional. Sumarul poate include canalul/banda/radio/ratele linkului Wi-Fi si numarul pe categorii de evenimente Windows. Nu include CSV-ul brut, mesajele Windows, SSID/BSSID sau IP-uri. Analiza locala functioneaza fara AI." : "AI is optional. The summary may include Wi-Fi channel/band/radio/link rates and counts by Windows event category. It excludes raw CSV, Windows event text, SSID/BSSID, and IP addresses. Local analysis works without AI.";
                case "WindowsEventAccessNotConsented": return ro ? "Jurnalele Windows: colectarea nu a fost aprobată pentru această sesiune." : "Windows logs: collection was not enabled for this session.";
                case "WindowsEventAccessDenied": return ro ? "Jurnalele Windows: acces refuzat de Windows pentru cel puțin un jurnal; probele active au continuat fără drepturi de administrator." : "Windows logs: Windows denied access to at least one channel; active probes continued without administrator rights.";
                case "WindowsEventAccessUnavailable": return ro ? "Jurnalele Windows: jurnale dezactivate, lipsă sau indisponibile; probele active au continuat." : "Windows logs: channels were disabled, missing, or unavailable; active probes continued.";
                case "WindowsEventAccessReadable": return ro ? "Jurnalele Windows: cel puțin un canal permis a fost citit cu drepturile utilizatorului curent." : "Windows logs: at least one permitted channel was read with current-user permissions.";
                case "AIConsentTitle": return ro ? "Continuă cu ChatGPT" : "Continue with ChatGPT";
                case "AIConnectSuccess": return ro ? "Contul ChatGPT a fost conectat. Aplicația îl va folosi pentru explicațiile AI după sesiunile de monitorizare." : "ChatGPT is connected. The app will use this account for AI explanations after each monitoring session.";
                case "AIConnectFailure": return ro ? "Autentificarea ChatGPT nu a reușit. Verifică fereastra browserului și conexiunea, apoi încearcă din nou. Diagnosticul local rămâne disponibil." : "ChatGPT sign-in could not be completed. Check the browser window and your connection, then try again. Local diagnosis is still available.";
                case "AIDisconnectSuccess": return ro ? "Sesiunea ChatGPT a fost deconectată local." : "The ChatGPT session was disconnected locally.";
                case "AIProviderSaveError": return ro ? "Setările furnizorului AI nu au putut fi salvate. Verifică modelul și cheia API, apoi încearcă din nou." : "AI provider settings could not be saved. Check the model and API key, then try again.";
                case "AIExplanationError": return ro ? "Explicația AI nu a putut fi obținută. Verifică furnizorul, modelul, cheia API și conexiunea. Diagnosticul local rămâne disponibil." : "The AI explanation could not be obtained. Check the provider, model, API key, and connection. Local diagnosis remains available.";
                case "RouteTraceError": return ro ? "Testul rutei nu a putut fi finalizat. Verifică dacă Windows și software-ul de securitate permit această probă." : "The route trace could not be completed. Check whether Windows and security software allow this probe.";
                case "OpenLogsError": return ro ? "Folderul cu jurnale nu a putut fi deschis." : "The log folder could not be opened.";
                case "HeaderSubtitle": return ro ? "Monitorizare clară. Pași practici." : "Clear monitoring. Practical next steps.";
                case "ViewTitle": return ro ? "MONITORIZARE" : "MONITORING";
                case "SpeedTab": return ro ? "Test de viteză" : "Speed test";
                case "SpeedHeading": return ro ? "Test de viteză" : "Speed test";
                case "SpeedSubtitle": return ro ? "Deschide și rulează testul furnizorului în aplicație." : "Open and run the provider's test inside the app.";
                case "SpeedProvider": return ro ? "Alege furnizorul" : "Choose a provider";
                case "SpeedLoad": return ro ? "Încarcă testul" : "Load test";
                case "SpeedBack": return ro ? "Înapoi" : "Back";
                case "SpeedReload": return ro ? "Reîncarcă" : "Reload";
                case "SpeedBrowser": return ro ? "Deschide în browser" : "Open in browser";
                case "SpeedPrivacy": return ro ? "Confidențialitate" : "Privacy";
                case "SpeedEmpty": return ro ? "Alege furnizorul și citește informarea înainte să încarci pagina. Testul nu pornește automat." : "Choose a provider and review the notice before loading its page. The test does not start automatically.";
                case "SpeedDisclosure": return ro ? "Furnizor: {0}. Testul poate transfera mult trafic și afecta temporar monitorizarea, pingul și alte aplicații; volumul îl decide furnizorul. Site-ul poate procesa IP-ul public, informații despre browser/conexiune și datele testului. Aplicația nu citește și nu salvează rezultatele și nu le trimite către AI. Profilul WebView2 este temporar și se șterge la închiderea aplicației." : "Provider: {0}. A test can transfer substantial data and temporarily affect monitoring, ping, and other apps; the provider controls the volume. The site may process your public IP, browser/connection details, and test data. The app does not read or save results or send them to AI. The WebView2 profile is temporary and removed when the app closes.";
                case "SpeedDisclosureVodafone": return ro ? "Furnizor: {0}. Adresa publică poate fi indisponibilă sau se poate schimba. Testul poate transfera mult trafic și afecta temporar monitorizarea, pingul și alte aplicații; volumul îl decide furnizorul. Site-ul poate procesa IP-ul public, informații despre browser/conexiune și datele testului. Aplicația nu citește și nu salvează rezultatele și nu le trimite către AI. Profilul WebView2 este temporar și se șterge la închiderea aplicației." : "Provider: {0}. Its public web address may be unavailable or change. A test can transfer substantial data and temporarily affect monitoring, ping, and other apps; the provider controls the volume. The site may process your public IP, browser/connection details, and test data. The app does not read or save results or send them to AI. The WebView2 profile is temporary and removed when the app closes.";
                case "SpeedCheckingRuntime": return ro ? "Verific disponibilitatea WebView2..." : "Checking WebView2 availability...";
                case "SpeedRuntimeUnavailable": return ro ? "WebView2 nu este disponibil sau nu a putut porni ({0}). Poți deschide testul în browser." : "WebView2 is unavailable or could not start ({0}). You can open the test in your browser.";
                case "SpeedRuntimeUnavailableShort": return ro ? "WebView2 nu a putut inițializa pagina. Folosește browserul." : "WebView2 could not initialize the page. Use your browser.";
                case "SpeedFallbackReady": return ro ? "Pagina nu poate fi afișată aici. Deschiderea în browser este disponibilă la cerere." : "The page cannot be shown here. Browser fallback is available on request.";
                case "SpeedLoading": return ro ? "Se încarcă pagina furnizorului..." : "Loading provider page...";
                case "SpeedProviderSelected": return ro ? "Furnizor selectat. Apasă „Încarcă testul” pentru a-l afișa; deschiderea în browser folosește furnizorul selectat." : "Provider selected. Click Load test to display it; browser fallback uses the selected provider.";
                case "SpeedPageLoaded": return ro ? "Pagina s-a încărcat. Dacă testul nu funcționează aici, folosește „Deschide în browser”. Aplicația nu citește rezultatele." : "Page loaded. If the test does not work here, use Open in browser. The app does not read results.";
                case "SpeedPageFailed": return ro ? "Pagina a raportat o eroare sau un răspuns HTTP nereușit. Încearcă browserul." : "The page reported an error or unsuccessful HTTP response. Try your browser.";
                case "SpeedExternalNavigationBlocked": return ro ? "Navigarea către alt site este oprită în aplicație. Folosește browserul dacă dorești să continui." : "Navigation to another site is blocked inside the app. Use your browser if you want to continue.";
                case "SpeedPopupBlocked": return ro ? "Pagina a cerut o fereastră nouă. Pentru compatibilitate, continuă în browser." : "The page requested a new window. Continue in your browser for compatibility.";
                case "SpeedOpenedBrowser": return ro ? "Testul a fost deschis în browserul implicit." : "The test was opened in your default browser.";
                case "SpeedBrowserError": return ro ? "Browserul nu a putut fi deschis: {0}" : "Could not open the browser: {0}";
                case "MonitorOverview": return ro ? "Prezentare" : "Overview";
                case "MonitorDiagnosis": return ro ? "Diagnostic" : "Diagnosis";
                case "ConnectionStatus": return ro ? "STAREA CONEXIUNII" : "CONNECTION STATUS";
                case "MetricHeading": return ro ? "VERIFICĂRI DE REȚEA" : "NETWORK CHECKS";
                case "MetricProbeNote": return ro ? "Probe IPv4/IPv6 și HTTPS independente" : "Independent IPv4/IPv6 and HTTPS probes";
                case "ActivityTitle": return ro ? "ACTIVITATE RECENTĂ" : "RECENT ACTIVITY";
                case "Median": return ro ? "mediană" : "median";
                case "NoSuccessfulSamples": return ro ? "fără măsurători reușite" : "no successful samples";
                case "Minimum": return ro ? "minim" : "min";
                case "Maximum": return ro ? "maxim" : "max";
                case "Jitter": return ro ? "variație" : "jitter";
                case "P95": return "p95";
                case "ReportSavedLocally": return ro ? "Raport salvat local: " : "Report saved locally: ";
                case "WindowsTraceUnavailable": return ro ? "Utilitarul tracert.exe din Windows nu este disponibil." : "Windows tracert.exe is not available.";
                case "WindowsTraceTimeout": return ro ? "Testul rutei a depășit limita de siguranță de 12 secunde și a fost oprit. Unele routere nu răspund la aceste probe." : "The route trace exceeded its 12-second safety limit and was stopped. Some routers do not answer traceroute probes.";
                case "WindowsTraceEmpty": return ro ? "Windows nu a returnat rezultate pentru testul rutei." : "No route trace output was returned.";
                case "UpdateAvailableTitle": return ro ? "Actualizare disponibilă" : "Update available";
                case "UpdateAvailablePrompt": return ro ? "Este disponibilă versiunea {0}. Aplicația va descărca și verifica actualizarea, apoi se va reporni pentru instalare. Continui?" : "Version {0} is available. The app will download and verify the update, then restart to install it. Continue?";
                case "UpdateStarting": return ro ? "Actualizare pornită. Se descarcă și se verifică pachetul…" : "Update started. Downloading and verifying the package…";
                case "UpdateReady": return ro ? "Actualizarea a fost verificată. Aplicația se repornește acum." : "The update was verified. The app is restarting now.";
                case "UpdateFailed": return ro ? "Actualizarea nu a putut fi instalată. Versiunea curentă a rămas disponibilă. Detalii: {0}" : "The update could not be installed. The current version is still available. Details: {0}";
                case "UpdateUnavailable": return ro ? "Verificarea actualizărilor nu este disponibilă acum. Poți folosi în continuare versiunea instalată." : "Update checking is unavailable right now. You can continue using the installed version.";
                case "UpdateDuringMonitoring": return ro ? "A fost găsită o actualizare, dar monitorizarea rulează. Oprește sesiunea și repornește aplicația pentru a o instala." : "An update was found, but monitoring is active. Stop the session and restart the app to install it.";
                case "AIConsentPrompt": return ro ? "Pentru explicatia AI, aplicatia trimite la ChatGPT, dupa autentificarea si acordul tau, un sumar agregat ce poate include canalul/banda/radio/ratele linkului Wi-Fi si numarul pe categorii de evenimente Windows. CSV-ul brut, mesajele Windows, SSID/BSSID si IP-urile nu sunt trimise. Cererea foloseste limitele planului contului tau. Continui?" : "After sign-in and your consent, the app sends ChatGPT an aggregated summary that may include Wi-Fi channel/band/radio/link rates and counts by Windows event category. It does not send raw CSV, Windows event text, SSID/BSSID, or IP addresses. The request uses your ChatGPT account's plan limits. Continue?";
                case "AIConnectedStatus": return ro ? "ChatGPT conectat" : "ChatGPT connected";
                case "AINotConnectedStatus": return ro ? "ChatGPT neconectat - raport local disponibil" : "ChatGPT not connected - local report available";
                case "AIAnalyzingStatus": return ro ? "AI analizeaza sumarul masuratorilor..." : "AI is analyzing the measurement summary...";
                case "AIFailedPrefix": return ro ? "Explicatia AI nu a putut fi obtinuta; raportul local ramane disponibil. Detalii: " : "AI explanation was unavailable; the local report is still available. Details: ";
                case "AIExplanationHeader": return ro ? "EXPLICATIE AI - interpretare suplimentara" : "AI EXPLANATION - supplementary interpretation";
                case "AIExplanationLanguageNote": return ro ? "(generată în {0})" : "(generated in {0})";
                case "AIRevocationNote": return ro ? "Deconectat local. Revocarea la distanta nu a putut fi confirmata; poti revoca accesul in setarile ChatGPT." : "Signed out locally. Remote revocation could not be confirmed; you can revoke access in ChatGPT settings.";
                case "ProbeError": return ro ? "Monitorul a intampinat o eroare la colectarea acestei probe." : "The monitor encountered an error while collecting this sample.";
                default: return key;
            }
        }

        public static string Event(AppLanguage language, string code)
        {
            bool ro = language == AppLanguage.Romanian;
            string count = "";
            int separator = (code ?? "").LastIndexOf(':');
            int parsedCount;
            if (separator > 0 && Int32.TryParse(code.Substring(separator + 1), NumberStyles.None, CultureInfo.InvariantCulture, out parsedCount))
            {
                count = " ×" + parsedCount.ToString(CultureInfo.InvariantCulture);
                code = code.Substring(0, separator);
            }
            switch (code)
            {
                case "WiFiDisconnected": return ro ? "Wi-Fi deconectat" : "Wi-Fi disconnected";
                case "WeakWiFiSignal": return ro ? "Semnal Wi-Fi slab" : "Weak Wi-Fi signal";
                case "GatewayUnreachable": return ro ? "Gateway fara raspuns" : "Gateway did not respond";
                case "InternetTargetDisagreement": return ro ? "Tinte Internet cu raspunsuri diferite" : "Internet targets disagree";
                case "IPv6TargetDisagreement": return ro ? "Tinte IPv6 cu raspunsuri diferite" : "IPv6 targets disagree";
                case "IPv6OnlyReachable": return ro ? "Acces confirmat prin IPv6" : "IPv6 access confirmed";
                case "CaptivePortalSuspected": return ro ? "Portal captiv sau interceptare HTTPS suspectata" : "Captive portal or HTTPS interception suspected";
                case "ActiveRouteChanged": return ro ? "Ruta de retea a trecut pe alta interfata" : "Windows switched the active route interface";
                case "ICMPUnavailableHTTPSOk": return ro ? "Ping filtrat, HTTPS reusit" : "Ping unavailable, HTTPS succeeded";
                case "InternetOutage": return ro ? "Probe Internet neconcludente" : "Internet probes inconclusive";
                case "InternetOutageSuspected": return ro ? "Intrerupere Internet suspectata" : "Internet interruption suspected";
                case "DNSFailure": return ro ? "Esec DNS" : "DNS failure";
                case "DNSProbeDisagreement": return ro ? "Rezultate DNS diferite intre domenii" : "DNS results differ across domains";
                case "GatewayLatencySpike": return ro ? "Latența gateway-ului a crescut" : "Gateway latency spike";
                case "InternetLatencySpike": return ro ? "Latența Internetului a crescut" : "Internet latency spike";
                case "HTTPSLatencySpike": return ro ? "Latența HTTPS a crescut" : "HTTPS latency spike";
                case "HTTPSProbeDisagreement": return ro ? "Endpointurile HTTPS au rezultate diferite" : "HTTPS endpoints disagree";
                case "WifiAccessPointChanged": return (ro ? "Punctul de acces Wi-Fi s-a schimbat" : "Wi-Fi access point changed") + count;
                case "WindowsWifiConnected": return (ro ? "Jurnal Windows: Wi-Fi conectat" : "Windows log: Wi-Fi connected") + count;
                case "WindowsWifiDisconnected": return (ro ? "Jurnal Windows: Wi-Fi deconectat" : "Windows log: Wi-Fi disconnected") + count;
                case "WindowsWifiConnectFailed": return (ro ? "Jurnal Windows: conectare Wi-Fi eșuată" : "Windows log: Wi-Fi connection failed") + count;
                case "WindowsDnsTimeout": return (ro ? "Jurnal Windows: timeout DNS" : "Windows log: DNS timeout") + count;
                case "WindowsDhcpIssue": return (ro ? "Jurnal Windows: problemă DHCP" : "Windows log: DHCP issue") + count;
                case "WindowsInterfaceDisconnected": return (ro ? "Jurnal Windows: interfață deconectată" : "Windows log: interface disconnected") + count;
                case "WindowsInterfaceConnected": return (ro ? "Jurnal Windows: interfață reconectată" : "Windows log: interface reconnected") + count;
                case "WindowsAdapterReset": return (ro ? "Jurnal Windows: adaptorul se resetează sau a terminat resetarea" : "Windows log: adapter reset started or completed") + count;
                case "WindowsEventAccessNotConsented": return ro ? "Jurnalele Windows: colectarea nu a fost aprobată pentru această sesiune." : "Windows logs: collection was not enabled for this session.";
                case "WindowsEventAccessDenied": return ro ? "Jurnalele Windows: acces refuzat de Windows pentru cel puțin un jurnal; probele active au continuat fără drepturi de administrator." : "Windows logs: Windows denied access to at least one channel; active probes continued without administrator rights.";
                case "WindowsEventAccessUnavailable": return ro ? "Jurnalele Windows: jurnale dezactivate, lipsă sau indisponibile; probele active au continuat." : "Windows logs: channels were disabled, missing, or unavailable; active probes continued.";
                case "WindowsEventAccessReadable": return ro ? "Jurnalele Windows: cel puțin un canal permis a fost citit cu drepturile utilizatorului curent." : "Windows logs: at least one permitted channel was read with current-user permissions.";
                case "ProbeError": return Localization.Get(language, "ProbeError");
                default: return code;
            }
        }

        public static string Status(AppLanguage language, string status)
        {
            bool ro = language == AppLanguage.Romanian;
            if (status == "Connected") return Get(language, "Connected");
            if (status == "Disconnected") return Get(language, "Disconnected");
            if (status == "Unavailable" || status == "NoGateway") return Get(language, "Unavailable");
            if (status == "OK") return Get(language, "OK");
            if (status == "Degraded") return Get(language, "Degraded");
            if (status == "Outage") return ro ? "Intrerupere suspectata" : "Outage suspected";
            if (status == "OutageSuspected") return Get(language, "OutageSuspected");
            if (status == "AllFailed") return ro ? "Toate probele au esuat" : "All probes failed";
            if (status == "Partial") return ro ? "Raspuns partial" : "Partial response";
            if (status == "Success") return ro ? "Reusit" : "Success";
            if (status == "NotChecked") return ro ? "Neverificat" : "Not checked";
            if (status == "NotNeeded") return ro ? "Nefolosit" : "Not needed";
            if (status == "TimedOut") return ro ? "A expirat" : "Timed out";
            if (status == "Timeout") return ro ? "A expirat" : "Timed out";
            if (status == "Failed") return ro ? "Esuat" : "Failed";
            return status;
        }

        public static string Format(AppLanguage language, string key, params object[] args)
        {
            return String.Format(language == AppLanguage.Romanian ? CultureInfo.GetCultureInfo("ro-RO") : CultureInfo.GetCultureInfo("en-US"), Get(language, key), args);
        }
    }
}


