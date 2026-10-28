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
                case "ContinueChatGPT": return "Continue with ChatGPT";
                case "DisconnectChatGPT": return ro ? "Deconecteaza ChatGPT" : "Disconnect ChatGPT";
                case "AIConnected": return ro ? "AI ChatGPT conectat" : "ChatGPT AI connected";
                case "AINotConnected": return ro ? "AI neconectat - analiza locala disponibila" : "AI not connected - local analysis available";
                case "AISigningIn": return ro ? "Autentificare ChatGPT in browser..." : "Signing in to ChatGPT in browser...";
                case "AIAnalyzing": return ro ? "AI analizeaza sumarul masuratorilor..." : "AI is analyzing the measurement summary...";
                case "AIUnavailable": return ro ? "AI indisponibil; afisez analiza locala." : "AI unavailable; showing local analysis.";
                case "NoSamples": return ro ? "Nu exista masuratori suficiente pentru diagnostic." : "There are not enough measurements for a diagnosis.";
                case "InitialDiagnosis": return ro ? "Apasa Porneste monitorizarea. Dupa oprire, aplicatia va afisa constatarile, dovezile si pasii urmatori." : "Press Start monitoring. After you stop, the app will show findings, evidence, and next steps.";
                case "Footer": return ro ? "Probe la 5 sec | DNS la 60 sec | Fara SSID, BSSID, IP-uri sau parole | Fara administrator" : "Probes every 5 sec | DNS every 60 sec | No SSID, BSSID, IP addresses, or passwords saved | No admin needed";
                case "MetricWifi": return ro ? "Wi-Fi" : "Wi-Fi";
                case "MetricSignal": return ro ? "Semnal" : "Signal";
                case "MetricGateway": return ro ? "Gateway" : "Gateway";
                case "MetricInternet": return ro ? "Internet" : "Internet";
                case "MetricDns": return ro ? "DNS" : "DNS";
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
                case "AIDataNotice": return ro ? "La analiza AI se trimite doar un sumar agregat al masuratorilor, dupa autentificarea si acordul tau ChatGPT. CSV-ul brut, SSID-ul si adresele IP nu sunt trimise." : "AI analysis sends only an aggregated measurement summary after you sign in and approve ChatGPT usage. The raw CSV, SSID, and IP addresses are not sent.";
                case "AIConsentTitle": return "Continue with ChatGPT";
                case "AIConsentPrompt": return ro ? "Pentru explicatia AI, aplicatia trimite un sumar agregat al masuratorilor la ChatGPT dupa autentificarea si acordul tau. CSV-ul brut, SSID-ul si IP-urile nu sunt trimise. Cererea foloseste limitele planului ChatGPT al contului tau. Continui?" : "For the AI explanation, the app sends an aggregated measurement summary to ChatGPT after you sign in and consent. The raw CSV, Wi-Fi name, and IP addresses are not sent. The request uses your ChatGPT account's plan limits. Continue?";
                case "AIConnectedStatus": return ro ? "ChatGPT conectat" : "ChatGPT connected";
                case "AINotConnectedStatus": return ro ? "ChatGPT neconectat - raport local disponibil" : "ChatGPT not connected - local report available";
                case "AIAnalyzingStatus": return ro ? "ChatGPT analizeaza sumarul masuratorilor..." : "ChatGPT is analyzing the measurement summary...";
                case "AIFailedPrefix": return ro ? "Explicatia AI nu a putut fi obtinuta; raportul local ramane disponibil. Detalii: " : "AI explanation was unavailable; the local report is still available. Details: ";
                case "AIExplanationHeader": return ro ? "EXPLICATIE AI CHATGPT - interpretare suplimentara" : "CHATGPT AI EXPLANATION - supplementary interpretation";
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

