using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web;
using System.Web.Script.Serialization;

namespace NetworkMonitorApp
{
    internal sealed class ChatGptCredentials
    {
        public string ClientId;
        public string AccessToken;
        public string RefreshToken;
        public string IdToken;
        public string Subject;
        public string HostId;
        public DateTime ExpiresUtc;
    }

    internal static class ChatGptPlanClient
    {
        private const string DynamicClientId = "dynamic_agent_client";
        private const string AuthEndpoint = "https://auth.openai.com/api/accounts/authorize";
        private const string TokenEndpoint = "https://auth.openai.com/api/accounts/oauth/token";
        private const string JwksEndpoint = "https://auth.openai.com/.well-known/jwks.json";
        private const string Resource = "https://api.openai.com/v1";
        private const string RequiredScope = "chatgpt.tokens.use.direct";
        private const string Scope = "openid profile email offline_access resource.invoke chatgpt.tokens.use.direct";
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("NetworkMonitor.ChatGPT.credentials.v1");
        private static readonly object Gate = new object();
        private static ChatGptCredentials cached;
        private static readonly JavaScriptSerializer Json = CreateSerializer();

        private static JavaScriptSerializer CreateSerializer()
        {
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            serializer.MaxJsonLength = 4 * 1024 * 1024;
            serializer.RecursionLimit = 64;
            return serializer;
        }

        public static bool IsConnected { get { return LoadCredentials() != null; } }

        private static string StorageDirectory
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetworkMonitor"); }
        }

        private static string CredentialsPath { get { return Path.Combine(StorageDirectory, "chatgpt-session.bin"); } }
        private static string HostIdPath { get { return Path.Combine(StorageDirectory, "chatgpt-host-id.txt"); } }

        public static string Connect()
        {
            ChatGptCredentials previous = LoadCredentials();
            string hostId = LoadOrCreateHostId();
            string state = RandomBase64Url(32);
            string nonce = RandomBase64Url(32);
            string verifier = RandomBase64Url(32);
            string challenge = CreateCodeChallenge(verifier);
            int port;
            TcpListener listener = StartLoopbackListener(out port);
            string redirectUri = "http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture) + "/auth/callback";
            Dictionary<string, string> query = new Dictionary<string, string>();
            query["client_id"] = previous == null ? DynamicClientId : previous.ClientId;
            query["response_type"] = "code";
            query["redirect_uri"] = redirectUri;
            query["scope"] = Scope;
            query["resource"] = Resource;
            query["state"] = state;
            query["nonce"] = nonce;
            query["code_challenge_method"] = "S256";
            query["code_challenge"] = challenge;
            query["ext_agent_host_id"] = hostId;
            if (previous == null)
                query["agent_name_hint"] = "Network Monitor Diagnostic";
            else
            {
                query["id_token_hint"] = previous.IdToken;
            }

            try
            {
                {
                    string authorizeUrl = AuthEndpoint + "?" + FormEncode(query);
                    ProcessBrowser(authorizeUrl);
                    Dictionary<string, string> callback = ReadCallback(listener, redirectUri);
                    if (!callback.ContainsKey("state") || !SecureEquals(state, callback["state"]))
                        throw new InvalidOperationException("The sign-in response could not be validated. Please try again.");
                    if (callback.ContainsKey("error"))
                    {
                        if (callback["error"] == "access_denied") throw new InvalidOperationException("ChatGPT access was not approved. Local diagnosis is still available.");
                        throw new InvalidOperationException("ChatGPT sign-in did not complete: " + SafeMessage(callback.ContainsKey("error_description") ? callback["error_description"] : callback["error"]));
                    }
                    if (!callback.ContainsKey("code")) throw new InvalidOperationException("ChatGPT did not return an authorization code.");

                    string issuedClientId = previous == null
                        ? (callback.ContainsKey("client_id") ? callback["client_id"] : null)
                        : previous.ClientId;
                    if (String.IsNullOrWhiteSpace(issuedClientId) || issuedClientId == DynamicClientId)
                        throw new InvalidOperationException("OpenAI did not issue a client ID for this app registration.");
                    if (previous != null && callback.ContainsKey("client_id") && callback["client_id"] != previous.ClientId)
                        throw new InvalidOperationException("The returned ChatGPT registration did not match the selected account.");

                    Dictionary<string, string> tokenForm = new Dictionary<string, string>();
                    tokenForm["grant_type"] = "authorization_code";
                    tokenForm["client_id"] = issuedClientId;
                    tokenForm["code"] = callback["code"];
                    tokenForm["code_verifier"] = verifier;
                    tokenForm["redirect_uri"] = redirectUri;
                    tokenForm["resource"] = Resource;
                    Dictionary<string, object> token = (Dictionary<string, object>)Json.DeserializeObject(PostForm(TokenEndpoint, tokenForm));
                    string accessToken = GetString(token, "access_token");
                    string refreshToken = GetString(token, "refresh_token");
                    string idToken = GetString(token, "id_token");
                    string scopes = GetString(token, "scope");
                    if (String.IsNullOrEmpty(accessToken) || String.IsNullOrEmpty(refreshToken) || String.IsNullOrEmpty(idToken))
                        throw new InvalidOperationException("ChatGPT sign-in returned an incomplete token response.");
                    if (!HasScope(scopes, RequiredScope))
                        throw new InvalidOperationException("The ChatGPT plan permission was not approved. Local diagnosis is still available.");

                    Dictionary<string, object> claims = OidcTokenValidator.Validate(idToken, GetJson(JwksEndpoint), issuedClientId, nonce, DateTime.UtcNow);
                    string subject = GetString(claims, "sub");
                    if (previous != null && subject != previous.Subject)
                        throw new InvalidOperationException("The signed-in ChatGPT account did not match the selected account.");
                    long expiresIn = GetLong(token, "expires_in", 3600);
                    ChatGptCredentials credentials = new ChatGptCredentials
                    {
                        ClientId = issuedClientId,
                        AccessToken = accessToken,
                        RefreshToken = refreshToken,
                        IdToken = idToken,
                        Subject = subject,
                        HostId = hostId,
                        ExpiresUtc = DateTime.UtcNow.AddSeconds(Math.Max(60, expiresIn))
                    };
                    SaveCredentials(credentials);
                    return "ChatGPT is connected. The app will use this account for AI explanations after each monitoring session.";
                }
            }
            catch (WebException ex)
            {
                throw new InvalidOperationException("Could not complete ChatGPT sign-in. Check the Internet connection and try again. " + GetHttpStatus(ex));
            }
            finally
            {
                try { listener.Stop(); } catch { }
            }
        }

        public static string Disconnect()
        {
            bool revoked = false;
            ChatGptCredentials current = LoadCredentials();
            if (current != null && !String.IsNullOrEmpty(current.RefreshToken))
            {
                try
                {
                    Dictionary<string, object> discovery = (Dictionary<string, object>)Json.DeserializeObject(GetJson("https://auth.openai.com/.well-known/openid-configuration"));
                    string endpoint = GetString(discovery, "revocation_endpoint");
                    if (!String.IsNullOrEmpty(endpoint))
                    {
                        Dictionary<string, string> form = new Dictionary<string, string>();
                        form["token"] = current.RefreshToken;
                        form["token_type_hint"] = "refresh_token";
                        form["client_id"] = current.ClientId;
                        PostForm(endpoint, form);
                        revoked = true;
                    }
                }
                catch { }
            }
            lock (Gate) cached = null;
            if (File.Exists(CredentialsPath)) File.Delete(CredentialsPath);
            return revoked
                ? "Signed out on this device and the ChatGPT session was revoked."
                : "Signed out on this device. Remote revocation could not be confirmed; remove this app in ChatGPT Settings if you want to revoke access.";
        }

        public static string Explain(IList<Sample> samples, AppLanguage language)
        {
            ChatGptCredentials credentials = GetFreshCredentials();
            if (credentials == null) throw new InvalidOperationException("Connect with ChatGPT to enable AI explanations.");
            string summary = BuildAggregatedSummary(samples, language);
            string model = GetFirstAvailableModel(credentials.AccessToken);
            Dictionary<string, object> developerMessage = new Dictionary<string, object>();
            developerMessage["role"] = "developer";
            developerMessage["content"] = language == AppLanguage.Romanian
                ? "Interpreteaza numai sumarul agregat de masuratori furnizat. Nu inventa valori, teste sau cauze. Separa faptele masurate de ipoteze, explica incertitudinile, indica ce probe sustin fiecare concluzie si propune cel mult trei pasi practici in ordine. Nu afirma defectiunea ISP-ului sau a hardware-ului fara dovada. Raspunde in romana, clar pentru un utilizator fara experienta tehnica."
                : "Interpret only the aggregated measurements provided. Do not invent values, tests, or causes. Separate measured facts from hypotheses, explain uncertainty, identify which evidence supports each conclusion, and give up to three practical next steps in order. Do not claim an ISP or hardware fault without evidence. Answer in clear English for a non-technical user.";
            Dictionary<string, object> userMessage = new Dictionary<string, object>();
            userMessage["role"] = "user";
            userMessage["content"] = summary;
            Dictionary<string, object> requestBody = CreateResponsesPayload(model, developerMessage, userMessage);
            return PostResponses(credentials.AccessToken, requestBody);
        }

        internal static Dictionary<string, object> CreateResponsesPayload(string model, Dictionary<string, object> developerMessage, Dictionary<string, object> userMessage)
        {
            Dictionary<string, object> requestBody = new Dictionary<string, object>();
            requestBody["model"] = model;
            requestBody["input"] = new object[] { developerMessage, userMessage };
            requestBody["store"] = false;
            requestBody["stream"] = true;
            return requestBody;
        }

        internal static string BuildAggregatedSummary(IList<Sample> samples, AppLanguage language)
        {
            if (samples == null || samples.Count == 0) return language == AppLanguage.Romanian ? "Nu sunt probe." : "No samples.";
            int wifiDrops = samples.Count(delegate(Sample s) { return s.WifiState == "Disconnected"; });
            int weakSignal = samples.Count(delegate(Sample s) { return s.WifiSignalPercent.HasValue && s.WifiSignalPercent.Value < 35; });
            int gatewayFailed = samples.Count(delegate(Sample s) { return s.GatewayPingStatus != "Success" && s.GatewayPingStatus != "Unavailable" && s.GatewayPingStatus != "NoGateway"; });
            int internetFailed = samples.Count(delegate(Sample s) { return s.InternetPingStatus != "Success"; });
            int allExternalFailed = samples.Count(delegate(Sample s) { return s.InternetPingStatus != "Success" && s.HttpsStatus == "AllFailed"; });
            int httpsOk = samples.Sum(delegate(Sample s) { return s.HttpsSucceeded.HasValue ? s.HttpsSucceeded.Value : 0; });
            int httpsTotal = samples.Sum(delegate(Sample s) { return s.HttpsAttempted.HasValue ? s.HttpsAttempted.Value : 0; });
            int dnsOk = samples.Sum(delegate(Sample s) { return s.DnsSucceeded.HasValue ? s.DnsSucceeded.Value : 0; });
            int dnsTotal = samples.Sum(delegate(Sample s) { return s.DnsAttempted.HasValue ? s.DnsAttempted.Value : 0; });
            double? signal = Average(samples.Where(delegate(Sample s) { return s.WifiSignalPercent.HasValue; }).Select(delegate(Sample s) { return (double)s.WifiSignalPercent.Value; }));
            double? gateway = Average(samples.Where(delegate(Sample s) { return s.GatewayLatencyMs.HasValue; }).Select(delegate(Sample s) { return s.GatewayLatencyMs.Value; }));
            double? internet = Average(samples.Where(delegate(Sample s) { return s.InternetLatencyMs.HasValue; }).Select(delegate(Sample s) { return s.InternetLatencyMs.Value; }));
            double durationMinutes = samples.Count < 2 ? 0 : (samples[samples.Count - 1].Timestamp - samples[0].Timestamp).TotalMinutes;
            StringBuilder b = new StringBuilder();
            b.AppendLine("samples=" + samples.Count.ToString(CultureInfo.InvariantCulture));
            b.AppendLine("duration_minutes=" + durationMinutes.ToString("0.0", CultureInfo.InvariantCulture));
            b.AppendLine("wifi_disconnect_samples=" + wifiDrops.ToString(CultureInfo.InvariantCulture));
            b.AppendLine("weak_wifi_signal_samples_below_35_percent=" + weakSignal.ToString(CultureInfo.InvariantCulture));
            b.AppendLine("wifi_signal_average_percent=" + FormatAggregate(signal));
            b.AppendLine("gateway_icmp_failed_samples=" + gatewayFailed.ToString(CultureInfo.InvariantCulture));
            b.AppendLine("gateway_icmp_average_latency_ms=" + FormatAggregate(gateway));
            b.AppendLine("internet_icmp_nonresponses=" + internetFailed.ToString(CultureInfo.InvariantCulture));
            b.AppendLine("samples_where_all_external_probes_failed=" + allExternalFailed.ToString(CultureInfo.InvariantCulture));
            b.AppendLine("https_successful_endpoint_checks=" + httpsOk.ToString(CultureInfo.InvariantCulture) + "/" + httpsTotal.ToString(CultureInfo.InvariantCulture));
            b.AppendLine("dns_successful_name_lookups=" + dnsOk.ToString(CultureInfo.InvariantCulture) + "/" + dnsTotal.ToString(CultureInfo.InvariantCulture));
            b.AppendLine("internet_icmp_average_latency_ms=" + FormatAggregate(internet));
            b.AppendLine(language == AppLanguage.Romanian
                ? "Probele ICMP pot fi filtrate; DNS foloseste resolverul Windows. Acest sumar nu include SSID, adrese IP, timestampuri exacte sau continutul CSV."
                : "ICMP probes may be filtered; DNS uses the Windows resolver. This summary excludes SSID, IP addresses, exact timestamps, and raw CSV content.");
            return b.ToString();
        }

        internal static string CreateCodeChallenge(string verifier)
        {
            using (SHA256 sha = SHA256.Create())
                return Base64Url(sha.ComputeHash(Encoding.ASCII.GetBytes(verifier)));
        }

        private static ChatGptCredentials GetFreshCredentials()
        {
            lock (Gate)
            {
                ChatGptCredentials credentials = LoadCredentialsUnlocked();
                if (credentials == null) return null;
                if (credentials.ExpiresUtc > DateTime.UtcNow.AddMinutes(2)) return credentials;
                using (Mutex refreshMutex = new Mutex(false, "Local\\NetworkMonitorChatGptRefresh"))
                {
                    bool ownsMutex = false;
                    try { ownsMutex = refreshMutex.WaitOne(30000); }
                    catch (AbandonedMutexException) { ownsMutex = true; }
                    if (!ownsMutex) throw new InvalidOperationException("Another Network Monitor window is renewing ChatGPT access. Please retry shortly.");
                    try
                    {
                        cached = null;
                        credentials = LoadCredentialsUnlocked();
                        if (credentials == null) return null;
                        if (credentials.ExpiresUtc > DateTime.UtcNow.AddMinutes(2)) return credentials;
                        Dictionary<string, string> form = new Dictionary<string, string>();
                        form["grant_type"] = "refresh_token";
                        form["client_id"] = credentials.ClientId;
                        form["refresh_token"] = credentials.RefreshToken;
                        form["resource"] = Resource;
                        Dictionary<string, object> token = (Dictionary<string, object>)Json.DeserializeObject(PostForm(TokenEndpoint, form));
                        string access = GetString(token, "access_token");
                        string refresh = GetString(token, "refresh_token");
                        string scopes = GetString(token, "scope");
                        if (String.IsNullOrEmpty(access) || String.IsNullOrEmpty(refresh)) throw new InvalidOperationException("ChatGPT could not renew the session. Reconnect the account.");
                        if (!String.IsNullOrEmpty(scopes) && !HasScope(scopes, RequiredScope)) throw new InvalidOperationException("ChatGPT plan permission is no longer available. Reconnect the account.");
                        credentials.AccessToken = access;
                        credentials.RefreshToken = refresh;
                        credentials.ExpiresUtc = DateTime.UtcNow.AddSeconds(Math.Max(60, GetLong(token, "expires_in", 3600)));
                        SaveCredentialsUnlocked(credentials);
                        return credentials;
                    }
                    finally { refreshMutex.ReleaseMutex(); }
                }
            }
        }

        private static ChatGptCredentials LoadCredentials()
        {
            lock (Gate) return LoadCredentialsUnlocked();
        }

        private static ChatGptCredentials LoadCredentialsUnlocked()
        {
            if (cached != null) return cached;
            try
            {
                if (!File.Exists(CredentialsPath)) return null;
                byte[] encrypted = File.ReadAllBytes(CredentialsPath);
                byte[] clear = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
                Dictionary<string, object> values = (Dictionary<string, object>)Json.DeserializeObject(Encoding.UTF8.GetString(clear));
                cached = new ChatGptCredentials
                {
                    ClientId = GetString(values, "client_id"),
                    AccessToken = GetString(values, "access_token"),
                    RefreshToken = GetString(values, "refresh_token"),
                    IdToken = GetString(values, "id_token"),
                    Subject = GetString(values, "subject"),
                    HostId = GetString(values, "host_id"),
                    ExpiresUtc = new DateTime(GetLong(values, "expires_utc_ticks", 0), DateTimeKind.Utc)
                };
                if (String.IsNullOrEmpty(cached.ClientId) || String.IsNullOrEmpty(cached.RefreshToken) || String.IsNullOrEmpty(cached.Subject))
                {
                    cached = null;
                    return null;
                }
                return cached;
            }
            catch { cached = null; return null; }
        }

        private static void SaveCredentials(ChatGptCredentials credentials)
        {
            lock (Gate) SaveCredentialsUnlocked(credentials);
        }

        private static void SaveCredentialsUnlocked(ChatGptCredentials credentials)
        {
            Directory.CreateDirectory(StorageDirectory);
            Dictionary<string, object> values = new Dictionary<string, object>();
            values["client_id"] = credentials.ClientId;
            values["access_token"] = credentials.AccessToken;
            values["refresh_token"] = credentials.RefreshToken;
            values["id_token"] = credentials.IdToken;
            values["subject"] = credentials.Subject;
            values["host_id"] = credentials.HostId;
            values["expires_utc_ticks"] = credentials.ExpiresUtc.Ticks;
            byte[] clear = Encoding.UTF8.GetBytes(Json.Serialize(values));
            byte[] encrypted = ProtectedData.Protect(clear, Entropy, DataProtectionScope.CurrentUser);
            string temp = CredentialsPath + ".tmp";
            using (FileStream stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(encrypted, 0, encrypted.Length);
                stream.Flush(true);
            }
            if (File.Exists(CredentialsPath)) File.Replace(temp, CredentialsPath, null);
            else File.Move(temp, CredentialsPath);
            cached = credentials;
        }

        private static string LoadOrCreateHostId()
        {
            try
            {
                if (File.Exists(HostIdPath))
                {
                    string existing = File.ReadAllText(HostIdPath).Trim();
                    Guid parsed;
                    if (existing.StartsWith("urn:uuid:", StringComparison.Ordinal) && Guid.TryParse(existing.Substring(9), out parsed)) return existing;
                }
            }
            catch { }
            Directory.CreateDirectory(StorageDirectory);
            string value = "urn:uuid:" + Guid.NewGuid().ToString("D");
            File.WriteAllText(HostIdPath, value, new UTF8Encoding(false));
            return value;
        }

        private static TcpListener StartLoopbackListener(out int port)
        {
            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start(1);
            port = ((IPEndPoint)listener.LocalEndpoint).Port;
            return listener;
        }

        private static Dictionary<string, string> ReadCallback(TcpListener listener, string redirectUri)
        {
            DateTime callbackDeadline = DateTime.UtcNow.AddMinutes(5);
            while (!listener.Pending())
            {
                if (DateTime.UtcNow >= callbackDeadline)
                    throw new InvalidOperationException("ChatGPT sign-in timed out. Click Continue with ChatGPT and try again.");
                Thread.Sleep(100);
            }
            using (TcpClient client = listener.AcceptTcpClient())
            {
                if (!IPAddress.IsLoopback(((IPEndPoint)client.Client.RemoteEndPoint).Address))
                    throw new InvalidOperationException("The sign-in callback was not from this computer.");
                client.ReceiveTimeout = 120000;
                using (NetworkStream stream = client.GetStream())
                {
                    string requestLine = ReadHttpLine(stream, 8192);
                    string[] parts = requestLine.Split(' ');
                    if (parts.Length < 2 || parts[0] != "GET") throw new InvalidOperationException("Invalid sign-in callback.");
                    Uri callbackUri = new Uri("http://127.0.0.1" + parts[1]);
                    Uri expected = new Uri(redirectUri);
                    if (callbackUri.AbsolutePath != expected.AbsolutePath || callbackUri.Port != expected.Port)
                        throw new InvalidOperationException("The sign-in callback did not match this attempt.");
                    string line;
                    int headers = 0;
                    do { line = ReadHttpLine(stream, 8192); headers += line.Length; if (headers > 32768) throw new InvalidOperationException("The sign-in callback was too large."); }
                    while (line.Length > 0);
                    string html = "<!doctype html><meta charset=utf-8><title>Network Monitor</title><p>You can return to Network Monitor now.</p>";
                    byte[] body = Encoding.UTF8.GetBytes(html);
                    byte[] head = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nConnection: close\r\nContent-Length: " + body.Length.ToString(CultureInfo.InvariantCulture) + "\r\n\r\n");
                    stream.Write(head, 0, head.Length); stream.Write(body, 0, body.Length); stream.Flush();
                    Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.Ordinal);
                    System.Collections.Specialized.NameValueCollection query = HttpUtility.ParseQueryString(callbackUri.Query);
                    foreach (string key in query.AllKeys) if (!String.IsNullOrEmpty(key)) values[key] = query[key];
                    return values;
                }
            }
        }

        private static string ReadHttpLine(Stream stream, int maxBytes)
        {
            List<byte> bytes = new List<byte>();
            while (bytes.Count < maxBytes)
            {
                int value = stream.ReadByte();
                if (value < 0) break;
                if (value == 10) break;
                if (value != 13) bytes.Add((byte)value);
            }
            if (bytes.Count >= maxBytes) throw new InvalidOperationException("The sign-in callback was too large.");
            return Encoding.ASCII.GetString(bytes.ToArray());
        }

        private static void ProcessBrowser(string url)
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }

        private static string GetFirstAvailableModel(string accessToken)
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(Resource + "/models");
            request.Method = "GET"; request.Timeout = 20000; request.ReadWriteTimeout = 20000;
            request.Headers[HttpRequestHeader.Authorization] = "Bearer " + accessToken;
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            using (StreamReader reader = new StreamReader(response.GetResponseStream()))
            {
                Dictionary<string, object> root = (Dictionary<string, object>)Json.DeserializeObject(reader.ReadToEnd());
                IEnumerable models = root.ContainsKey("models") ? root["models"] as IEnumerable : null;
                if (models == null) throw new InvalidOperationException("ChatGPT did not return an available model list.");
                foreach (object item in models)
                {
                    Dictionary<string, object> model = item as Dictionary<string, object>;
                    if (model != null && GetString(model, "visibility") == "list" && !String.IsNullOrEmpty(GetString(model, "slug")))
                        return GetString(model, "slug");
                }
                throw new InvalidOperationException("No ChatGPT model is available for this account.");
            }
        }

        private static string PostResponses(string accessToken, Dictionary<string, object> body)
        {
            byte[] payload = Encoding.UTF8.GetBytes(Json.Serialize(body));
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(Resource + "/responses");
            request.Method = "POST"; request.ContentType = "application/json"; request.Accept = "text/event-stream";
            request.Timeout = 90000; request.ReadWriteTimeout = 90000; request.KeepAlive = false;
            request.Headers[HttpRequestHeader.Authorization] = "Bearer " + accessToken;
            request.ContentLength = payload.Length;
            using (Stream requestStream = request.GetRequestStream()) requestStream.Write(payload, 0, payload.Length);
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            using (StreamReader reader = new StreamReader(response.GetResponseStream()))
            {
                StringBuilder output = new StringBuilder();
                bool completed = false;
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                    string data = line.Substring(5).Trim();
                    if (data == "[DONE]") { completed = true; break; }
                    Dictionary<string, object> item;
                    try { item = Json.DeserializeObject(data) as Dictionary<string, object>; }
                    catch { continue; }
                    if (item == null) continue;
                    string type = GetString(item, "type");
                    if (type == "response.output_text.delta") output.Append(GetString(item, "delta"));
                    else if (type == "response.completed") completed = true;
                    else if (type == "error" || type == "response.failed")
                        throw new InvalidOperationException("ChatGPT could not complete the explanation. Local diagnosis remains available.");
                    if (completed) break;
                }
                if (!completed) throw new InvalidOperationException("The ChatGPT response stream ended before completion.");
                return output.ToString().Trim();
            }
        }

        private static string GetJson(string url)
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "GET"; request.Timeout = 15000; request.ReadWriteTimeout = 15000;
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            using (StreamReader reader = new StreamReader(response.GetResponseStream())) return reader.ReadToEnd();
        }

        private static string PostForm(string url, Dictionary<string, string> form)
        {
            byte[] payload = Encoding.UTF8.GetBytes(FormEncode(form));
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "POST"; request.ContentType = "application/x-www-form-urlencoded";
            request.Timeout = 30000; request.ReadWriteTimeout = 30000; request.ContentLength = payload.Length;
            using (Stream stream = request.GetRequestStream()) stream.Write(payload, 0, payload.Length);
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            using (StreamReader reader = new StreamReader(response.GetResponseStream())) return reader.ReadToEnd();
        }

        private static string FormEncode(IDictionary<string, string> values)
        {
            return String.Join("&", values.Select(delegate(KeyValuePair<string, string> item)
            {
                return Uri.EscapeDataString(item.Key) + "=" + Uri.EscapeDataString(item.Value ?? "").Replace("%20", "+");
            }).ToArray());
        }

        private static bool HasScope(string scopes, string expected)
        {
            return !String.IsNullOrEmpty(scopes) && scopes.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Contains(expected, StringComparer.Ordinal);
        }

        private static string GetString(IDictionary<string, object> values, string key)
        {
            if (values == null || !values.ContainsKey(key) || values[key] == null) return null;
            return Convert.ToString(values[key], CultureInfo.InvariantCulture);
        }

        private static long GetLong(IDictionary<string, object> values, string key, long fallback)
        {
            long result;
            return values != null && values.ContainsKey(key) && Int64.TryParse(Convert.ToString(values[key], CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out result) ? result : fallback;
        }

        private static double? Average(IEnumerable<double> values)
        {
            List<double> list = values.ToList();
            return list.Count == 0 ? (double?)null : list.Average();
        }

        private static string FormatAggregate(double? value)
        {
            return value.HasValue ? value.Value.ToString("0.0", CultureInfo.InvariantCulture) : "not available";
        }

        private static string RandomBase64Url(int bytes)
        {
            byte[] value = new byte[bytes];
            using (RandomNumberGenerator rng = RandomNumberGenerator.Create()) rng.GetBytes(value);
            return Base64Url(value);
        }

        private static string Base64Url(byte[] bytes)
        {
            return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        private static bool SecureEquals(string a, string b)
        {
            if (a == null || b == null) return false;
            byte[] x = Encoding.UTF8.GetBytes(a); byte[] y = Encoding.UTF8.GetBytes(b);
            int diff = x.Length ^ y.Length;
            for (int i = 0; i < Math.Min(x.Length, y.Length); i++) diff |= x[i] ^ y[i];
            return diff == 0;
        }

        private static string GetHttpStatus(WebException exception)
        {
            HttpWebResponse response = exception.Response as HttpWebResponse;
            return response == null ? "(network error)" : "(HTTP " + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture) + ")";
        }

        private static string SafeMessage(string value)
        {
            if (String.IsNullOrEmpty(value)) return "unknown error";
            string safe = value.Replace("\r", " ").Replace("\n", " ");
            return safe.Length > 180 ? safe.Substring(0, 180) : safe;
        }
    }

    internal static class OidcTokenValidator
    {
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();

        public static Dictionary<string, object> Validate(string jwt, string jwksJson, string clientId, string expectedNonce, DateTime utcNow)
        {
            if (String.IsNullOrEmpty(jwt)) throw new InvalidOperationException("Missing OpenID token.");
            string[] parts = jwt.Split('.');
            if (parts.Length != 3) throw new InvalidOperationException("Malformed OpenID token.");
            Dictionary<string, object> header = Json.DeserializeObject(Encoding.UTF8.GetString(DecodeBase64Url(parts[0]))) as Dictionary<string, object>;
            Dictionary<string, object> claims = Json.DeserializeObject(Encoding.UTF8.GetString(DecodeBase64Url(parts[1]))) as Dictionary<string, object>;
            if (header == null || claims == null || Convert.ToString(header["alg"], CultureInfo.InvariantCulture) != "RS256")
                throw new InvalidOperationException("Unsupported OpenID token signature.");
            string kid = header.ContainsKey("kid") ? Convert.ToString(header["kid"], CultureInfo.InvariantCulture) : null;
            Dictionary<string, object> jwks = Json.DeserializeObject(jwksJson) as Dictionary<string, object>;
            IEnumerable keys = jwks != null && jwks.ContainsKey("keys") ? jwks["keys"] as IEnumerable : null;
            Dictionary<string, object> key = keys == null ? null : keys.Cast<object>().Select(delegate(object item) { return item as Dictionary<string, object>; })
                .FirstOrDefault(delegate(Dictionary<string, object> item) { return item != null && Convert.ToString(item.ContainsKey("kid") ? item["kid"] : null, CultureInfo.InvariantCulture) == kid && Convert.ToString(item.ContainsKey("kty") ? item["kty"] : null, CultureInfo.InvariantCulture) == "RSA"; });
            if (key == null)
            {
                string seen = keys == null ? "no keys array" : String.Join(",", keys.Cast<object>().Select(delegate(object item)
                {
                    Dictionary<string, object> candidate = item as Dictionary<string, object>;
                    return candidate == null ? "non-object" : Convert.ToString(candidate.ContainsKey("kid") ? candidate["kid"] : null, CultureInfo.InvariantCulture) + "/" + Convert.ToString(candidate.ContainsKey("kty") ? candidate["kty"] : null, CultureInfo.InvariantCulture);
                }).ToArray());
                throw new InvalidOperationException("OpenID signing key was not found (kid=" + kid + ", keys=" + seen + ").");
            }
            RSAParameters parameters = new RSAParameters { Modulus = DecodeBase64Url(Convert.ToString(key["n"], CultureInfo.InvariantCulture)), Exponent = DecodeBase64Url(Convert.ToString(key["e"], CultureInfo.InvariantCulture)) };
            byte[] signed = Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]);
            using (RSACryptoServiceProvider rsa = new RSACryptoServiceProvider())
            using (SHA256 sha = SHA256.Create())
            {
                rsa.ImportParameters(parameters);
                if (!rsa.VerifyData(signed, sha, DecodeBase64Url(parts[2]))) throw new InvalidOperationException("OpenID token signature is invalid.");
            }
            if (GetClaim(claims, "iss") != "https://auth.openai.com") throw new InvalidOperationException("Unexpected OpenID issuer.");
            if (!HasAudience(claims.ContainsKey("aud") ? claims["aud"] : null, clientId)) throw new InvalidOperationException("OpenID token audience does not match the app registration.");
            long expiry;
            if (!Int64.TryParse(Convert.ToString(claims.ContainsKey("exp") ? claims["exp"] : null, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out expiry) || expiry <= new DateTimeOffset(utcNow).ToUnixTimeSeconds())
                throw new InvalidOperationException("OpenID token has expired or has no expiry.");
            if (GetClaim(claims, "nonce") != expectedNonce) throw new InvalidOperationException("OpenID nonce did not match this sign-in attempt.");
            if (String.IsNullOrEmpty(GetClaim(claims, "sub"))) throw new InvalidOperationException("OpenID token has no subject.");
            return claims;
        }

        private static string GetClaim(IDictionary<string, object> claims, string name)
        {
            return claims.ContainsKey(name) && claims[name] != null ? Convert.ToString(claims[name], CultureInfo.InvariantCulture) : null;
        }

        private static bool HasAudience(object audience, string clientId)
        {
            if (audience is string) return String.Equals((string)audience, clientId, StringComparison.Ordinal);
            IEnumerable values = audience as IEnumerable;
            if (values == null) return false;
            foreach (object item in values) if (String.Equals(Convert.ToString(item, CultureInfo.InvariantCulture), clientId, StringComparison.Ordinal)) return true;
            return false;
        }

        private static byte[] DecodeBase64Url(string value)
        {
            string base64 = value.Replace('-', '+').Replace('_', '/');
            switch (base64.Length % 4) { case 2: base64 += "=="; break; case 3: base64 += "="; break; }
            return Convert.FromBase64String(base64);
        }
    }
}

