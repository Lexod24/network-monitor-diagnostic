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
                case "Romanian": return "Romana";
                case "Start": return ro ? "Porneste monitorizarea" : "Start monitoring";
                case "Stop": return ro ? "Opreste si analizeaza" : "Stop and analyze";
                case "Restart": return ro ? "Porneste din nou" : "Start again";
                case "Stopped": return ro ? "Oprit - datele raman locale" : "Stopped - data stays local";
                case "Running": return ro ? "Ruleaza - colecteaza probe locale" : "Running - collecting local probes";
                case "Stopping": return ro ? "Oprire si analiza..." : "Stopping and analyzing...";
                case "OpenLogs": return ro ? "Deschide logurile" : "Open logs";
                case "CopyReport": return ro ? "Copiaza diagnosticul" : "Copy diagnosis";
                case "RouteTraceButton": return ro ? "Testeaza ruta" : "Trace route";
                case "RouteTraceConfirmTitle": return ro ? "Test de ruta" : "Route trace";
                case "RouteTraceConfirmPrompt": return ro ? "Testul trimite probe ICMP cu volum redus catre o tinta IPv4 sau IPv6, in functie de ruta disponibila. Routerele intermediare pot aparea ca adrese IP. Rezultatul este afisat doar pe acest calculator; nu este salvat sau trimis. Unele routere nu raspund. Continui?" : "This sends a small number of ICMP TTL probes to an IPv4 or IPv6 target, based on the available route. Intermediate routers may appear as IP addresses. Results are shown only on this PC and are not saved or sent. Some routers do not reply. Continue?";
                case "RouteTraceRunning": return ro ? "Se testeaza ruta..." : "Tracing route...";
                case "ContinueChatGPT": return "Continue with ChatGPT";
                case "AISettings": return ro ? "Setari AI" : "AI options";
                case "LatencyChartTitle": return ro ? "Latență (ms) - ultimele 60 de esantioane" : "Latency (ms) - latest 60 samples";
                case "LatencyChartGateway": return ro ? "Gateway" : "Gateway";
                case "LatencyChartInternet": return ro ? "Internet" : "Internet";
                case "LatencyChartEmpty": return ro ? "Porneste monitorizarea pentru a vedea istoricul latenței" : "Start monitoring to draw the latency history";
                case "DisconnectChatGPT": return ro ? "Deconecteaza ChatGPT" : "Disconnect ChatGPT";
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
                case "MetricGateway": return ro ? "Gateway" : "Gateway";
                case "MetricInternet": return ro ? "Internet" : "Internet";
                case "MetricDns": return ro ? "DNS" : "DNS";
                case "MetricDownload": return ro ? "Descarcare" : "Download";
                case "MetricUpload": return ro ? "Incarcare" : "Upload";
                case "NoValue": return "n/a";
                case "Connected": return ro ? "Conectat" : "Connected";
                case "Disconnected": return ro ? "Deconectat" : "Disconnected";
                case "Unavailable": return ro ? "Indisponibil" : "Unavailable";
                case "Degraded": return ro ? "Degradat" : "Degraded";
                case "OK": return ro ? "OK" : "OK";
                case "OutageSuspected": return ro ? "Intrerupere suspectata" : "Outage suspected";
                case "Events": return ro ? "Evenimente" : "Events";
                case "State": return ro ? "Stare" : "State";
                case "Time": return ro ? "Ora locala" : "Local time";
                case "GridWiFi": return "Wi-Fi";
                case "GridSignal": return ro ? "Semnal" : "Signal";
                case "GridGateway": return "Gateway";
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
                case "AIDataNotice": return ro ? "AI este optional. Pentru explicatia AI poate fi trimis doar sumarul agregat al masuratorilor catre furnizorul selectat; CSV-ul brut, SSID-ul si IP-urile nu sunt incluse. Analiza locala functioneaza fara AI." : "AI is optional. An aggregated measurement summary may be sent to the selected provider for an explanation; raw CSV, Wi-Fi name, and IP addresses are excluded. Local analysis works without AI.";
                case "AIConsentTitle": return "Continue with ChatGPT";
                case "AIConsentPrompt": return ro ? "Pentru explicatia AI, aplicatia trimite un sumar agregat al masuratorilor la ChatGPT dupa autentificarea si acordul tau. CSV-ul brut, SSID-ul si IP-urile nu sunt trimise. Cererea foloseste limitele planului ChatGPT al contului tau. Continui?" : "For the AI explanation, the app sends an aggregated measurement summary to ChatGPT after you sign in and consent. The raw CSV, Wi-Fi name, and IP addresses are not sent. The request uses your ChatGPT account's plan limits. Continue?";
                case "AIConnectedStatus": return ro ? "ChatGPT conectat" : "ChatGPT connected";
                case "AINotConnectedStatus": return ro ? "ChatGPT neconectat - raport local disponibil" : "ChatGPT not connected - local report available";
                case "AIAnalyzingStatus": return ro ? "AI analizeaza sumarul masuratorilor..." : "AI is analyzing the measurement summary...";
                case "AIFailedPrefix": return ro ? "Explicatia AI nu a putut fi obtinuta; raportul local ramane disponibil. Detalii: " : "AI explanation was unavailable; the local report is still available. Details: ";
                case "AIExplanationHeader": return ro ? "EXPLICATIE AI - interpretare suplimentara" : "AI EXPLANATION - supplementary interpretation";
                case "AIRevocationNote": return ro ? "Deconectat local. Revocarea la distanta nu a putut fi confirmata; poti revoca accesul in setarile ChatGPT." : "Signed out locally. Remote revocation could not be confirmed; you can revoke access in ChatGPT settings.";
                case "ProbeError": return ro ? "Monitorul a intampinat o eroare la colectarea acestei probe." : "The monitor encountered an error while collecting this sample.";
                default: return key;
            }
        }

        public static string Event(AppLanguage language, string code)
        {
            bool ro = language == AppLanguage.Romanian;
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

