using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace NetworkMonitorApp
{
    internal enum AiProvider { None, Ollama, Gemini, Groq }

    internal sealed class AiProviderSettings
    {
        public AiProvider Provider;
        public string Model;
        public string ApiKey;
    }

    internal static class AiProviderClient
    {
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("NetworkMonitor.AIProviderSettings.v1");
        private static readonly object Gate = new object();
        private static readonly JavaScriptSerializer Json = CreateSerializer();
        private static string SettingsPath { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetworkMonitor", "ai-provider.bin"); } }

        private static JavaScriptSerializer CreateSerializer()
        {
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            serializer.MaxJsonLength = 1024 * 1024;
            serializer.RecursionLimit = 32;
            return serializer;
        }

        public static AiProviderSettings Load()
        {
            lock (Gate)
            {
                try
                {
                    if (!File.Exists(SettingsPath)) return new AiProviderSettings { Provider = AiProvider.None, Model = "" };
                    byte[] protectedBytes = File.ReadAllBytes(SettingsPath);
                    Dictionary<string, object> data = Json.DeserializeObject(UnprotectSettings(protectedBytes)) as Dictionary<string, object>;
                    AiProvider provider;
                    if (data == null || !Enum.TryParse(Convert.ToString(data["provider"], CultureInfo.InvariantCulture), true, out provider)) return new AiProviderSettings { Provider = AiProvider.None, Model = "" };
                    if (provider != AiProvider.None && provider != AiProvider.Ollama && provider != AiProvider.Gemini && provider != AiProvider.Groq) provider = AiProvider.None;
                    return new AiProviderSettings { Provider = provider, Model = Get(data, "model"), ApiKey = Get(data, "apiKey") };
                }
                catch { return new AiProviderSettings { Provider = AiProvider.None, Model = "" }; }
            }
        }

        public static void Save(AiProviderSettings settings)
        {
            if (settings == null) throw new ArgumentNullException("settings");
            Validate(settings);
            lock (Gate)
            {
                string directory = Path.GetDirectoryName(SettingsPath);
                Directory.CreateDirectory(directory);
                Dictionary<string, object> data = new Dictionary<string, object>();
                data["provider"] = settings.Provider.ToString();
                data["model"] = (settings.Model ?? "").Trim();
                data["apiKey"] = settings.ApiKey ?? "";
                byte[] protectedBytes = ProtectSettings(Json.Serialize(data));
                string temp = SettingsPath + ".tmp";
                File.WriteAllBytes(temp, protectedBytes);
                if (File.Exists(SettingsPath)) File.Replace(temp, SettingsPath, null);
                else File.Move(temp, SettingsPath);
            }
        }

        internal static byte[] ProtectSettings(string json)
        {
            return ProtectedData.Protect(Encoding.UTF8.GetBytes(json ?? ""), Entropy, DataProtectionScope.CurrentUser);
        }

        internal static string UnprotectSettings(byte[] protectedBytes)
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser));
        }

        internal static void Validate(AiProviderSettings settings)
        {
            if (settings.Provider == AiProvider.None) return;
            string model = (settings.Model ?? "").Trim();
            if (model.Length == 0 || model.Length > 120 || model.Any(Char.IsControl)) throw new InvalidOperationException("Enter a valid model name.");
            if ((settings.Provider == AiProvider.Gemini || settings.Provider == AiProvider.Groq) && String.IsNullOrWhiteSpace(settings.ApiKey))
                throw new InvalidOperationException("This provider requires your own API key.");
            if (!String.IsNullOrEmpty(settings.ApiKey) && (settings.ApiKey.Length > 512 || settings.ApiKey.Any(Char.IsControl))) throw new InvalidOperationException("The API key format is not valid.");
        }

        public static bool IsConfigured { get { return Load().Provider != AiProvider.None; } }

        public static string Explain(IList<Sample> samples, AppLanguage language)
        {
            AiProviderSettings settings = Load();
            Validate(settings);
            if (settings.Provider == AiProvider.None) throw new InvalidOperationException("No optional AI provider is configured.");
            string summary = ChatGptPlanClient.BuildAggregatedSummary(samples, language);
            string prompt = BuildPrompt(summary, language);
            if (settings.Provider == AiProvider.Gemini) return ExplainGemini(settings, prompt);
            if (settings.Provider == AiProvider.Groq) return ExplainOpenAiCompatible("https://api.groq.com/openai/v1/chat/completions", settings, prompt);
            return ExplainOllama(settings, prompt);
        }

        internal static string BuildPrompt(string summary, AppLanguage language)
        {
            string languageName = language == AppLanguage.Romanian ? "Romanian" : "English";
            return "Explain this network-monitoring report in plain " + languageName + ". Use only the evidence below. Separate measured facts from possible causes, state uncertainty, and give a short ordered list of safe next steps. Do not claim certainty about an ISP, router, or hardware fault. Do not ask for credentials.\n\n" + (summary ?? "");
        }

        internal static string GetEndpoint(AiProvider provider)
        {
            if (provider == AiProvider.Ollama) return "http://127.0.0.1:11434/api/chat";
            if (provider == AiProvider.Gemini) return "https://generativelanguage.googleapis.com/v1beta/models/";
            if (provider == AiProvider.Groq) return "https://api.groq.com/openai/v1/chat/completions";
            throw new InvalidOperationException("Unsupported AI provider.");
        }

        private static string ExplainOllama(AiProviderSettings settings, string prompt)
        {
            Dictionary<string, object> body = new Dictionary<string, object>();
            body["model"] = settings.Model;
            body["stream"] = false;
            body["messages"] = new object[] { new Dictionary<string, object> { { "role", "user" }, { "content", prompt } } };
            return ParseOllamaResponse(Json.Serialize(PostJson(GetEndpoint(AiProvider.Ollama), body, null)));
        }

        private static string ExplainGemini(AiProviderSettings settings, string prompt)
        {
            string endpoint = GetEndpoint(AiProvider.Gemini) + Uri.EscapeDataString(settings.Model) + ":generateContent";
            Dictionary<string, object> body = new Dictionary<string, object>();
            body["contents"] = new object[] { new Dictionary<string, object> { { "parts", new object[] { new Dictionary<string, object> { { "text", prompt } } } } } };
            return ParseGeminiResponse(Json.Serialize(PostJson(endpoint, body, settings.ApiKey)));
        }

        internal static string ParseGeminiResponse(string json)
        {
            Dictionary<string, object> response = Json.DeserializeObject(json) as Dictionary<string, object>;
            IEnumerable candidates = response != null && response.ContainsKey("candidates") ? response["candidates"] as IEnumerable : null;
            Dictionary<string, object> candidate = candidates == null ? null : candidates.Cast<object>().Select(x => x as Dictionary<string, object>).FirstOrDefault(x => x != null);
            Dictionary<string, object> content = candidate == null ? null : candidate.ContainsKey("content") ? candidate["content"] as Dictionary<string, object> : null;
            IEnumerable parts = content != null && content.ContainsKey("parts") ? content["parts"] as IEnumerable : null;
            return RequiredText(parts == null ? null : String.Join("\n", parts.Cast<object>().Select(x => Get(x as Dictionary<string, object>, "text")).Where(x => !String.IsNullOrWhiteSpace(x)).ToArray()));
        }

        private static string ExplainOpenAiCompatible(string endpoint, AiProviderSettings settings, string prompt)
        {
            Dictionary<string, object> body = new Dictionary<string, object>();
            body["model"] = settings.Model;
            body["messages"] = new object[] { new Dictionary<string, object> { { "role", "user" }, { "content", prompt } } };
            return ParseOpenAiCompatibleResponse(Json.Serialize(PostJson(endpoint, body, settings.ApiKey)));
        }

        internal static string ParseOllamaResponse(string json)
        {
            Dictionary<string, object> response = Json.DeserializeObject(json) as Dictionary<string, object>;
            Dictionary<string, object> message = response != null && response.ContainsKey("message") ? response["message"] as Dictionary<string, object> : null;
            return RequiredText(Get(message, "content"));
        }

        internal static string ParseOpenAiCompatibleResponse(string json)
        {
            Dictionary<string, object> response = Json.DeserializeObject(json) as Dictionary<string, object>;
            IEnumerable choices = response != null && response.ContainsKey("choices") ? response["choices"] as IEnumerable : null;
            Dictionary<string, object> choice = choices == null ? null : choices.Cast<object>().Select(x => x as Dictionary<string, object>).FirstOrDefault(x => x != null);
            Dictionary<string, object> message = choice != null && choice.ContainsKey("message") ? choice["message"] as Dictionary<string, object> : null;
            return RequiredText(Get(message, "content"));
        }

        private static Dictionary<string, object> PostJson(string endpoint, Dictionary<string, object> body, string key)
        {
            byte[] payload = Encoding.UTF8.GetBytes(Json.Serialize(body));
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(endpoint);
            request.Method = "POST";
            request.ContentType = "application/json";
            request.Accept = "application/json";
            request.AllowAutoRedirect = false;
            request.Timeout = 90000;
            request.ReadWriteTimeout = 90000;
            request.ContentLength = payload.Length;
            if (!String.IsNullOrEmpty(key))
            {
                if (endpoint.StartsWith("https://generativelanguage.googleapis.com/", StringComparison.OrdinalIgnoreCase)) request.Headers["x-goog-api-key"] = key;
                else request.Headers[HttpRequestHeader.Authorization] = "Bearer " + key;
            }
            try
            {
                using (Stream stream = request.GetRequestStream()) stream.Write(payload, 0, payload.Length);
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                using (StreamReader reader = new StreamReader(response.GetResponseStream()))
                    return Json.DeserializeObject(reader.ReadToEnd()) as Dictionary<string, object>;
            }
            catch (WebException exception)
            {
                HttpWebResponse response = exception.Response as HttpWebResponse;
                string status = response == null ? "network error" : "HTTP " + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture);
                throw new InvalidOperationException("The selected AI provider could not complete the explanation (" + status + "). Check the provider, model, key, and Internet/local service. The local diagnosis is unchanged.");
            }
        }

        private static string RequiredText(string text)
        {
            if (String.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("The selected AI provider returned no explanation. The local diagnosis is unchanged.");
            return text.Trim();
        }

        private static string Get(IDictionary<string, object> values, string key)
        {
            return values != null && values.ContainsKey(key) && values[key] != null ? Convert.ToString(values[key], CultureInfo.InvariantCulture) : null;
        }
    }

    internal sealed class AiProviderSettingsForm : Form
    {
        private readonly ComboBox provider;
        private readonly TextBox model;
        private readonly TextBox apiKey;
        private readonly Label hint;
        private readonly bool romanian;
        private AiProvider previousProvider;

        public AiProviderSettingsForm(AppLanguage language) : this(language, AiProviderClient.Load()) { }

        private AiProviderSettingsForm(AppLanguage language, AiProviderSettings settings)
        {
            bool ro = language == AppLanguage.Romanian;
            romanian = ro;
            Text = ro ? "Setari furnizor AI" : "AI provider settings";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
            ClientSize = new System.Drawing.Size(500, 285);

            Label providerLabel = MakeLabel(ro ? "Furnizor" : "Provider", 18, 20);
            Controls.Add(providerLabel);
            provider = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new System.Drawing.Point(180, 18), Width = 290 };
            provider.Items.AddRange(ro
                ? new object[] { "Dezactivat (doar diagnostic local)", "Ollama (local; fara cont/cheie)", "Gemini API (cheia ta)", "Groq API (cheia ta)" }
                : new object[] { "Disabled (local diagnosis only)", "Ollama (local; no account/key)", "Gemini API (your key)", "Groq API (your key)" });
            provider.SelectedIndex = (int)settings.Provider;
            provider.SelectedIndexChanged += ProviderChanged;
            Controls.Add(provider);

            Controls.Add(MakeLabel(ro ? "Model" : "Model", 18, 62));
            model = new TextBox { Location = new System.Drawing.Point(180, 60), Width = 290, Text = settings.Model ?? "" };
            Controls.Add(model);
            Controls.Add(MakeLabel(ro ? "Cheie API" : "API key", 18, 104));
            apiKey = new TextBox { Location = new System.Drawing.Point(180, 102), Width = 290, UseSystemPasswordChar = true, Text = settings.ApiKey ?? "" };
            Controls.Add(apiKey);

            hint = new Label { Location = new System.Drawing.Point(18, 142), Size = new System.Drawing.Size(452, 60), AutoEllipsis = true };
            Controls.Add(hint);
            Button save = new Button { Text = ro ? "Salveaza" : "Save", Location = new System.Drawing.Point(270, 225), Size = new System.Drawing.Size(95, 32), DialogResult = DialogResult.OK };
            save.Click += SaveClicked;
            Button cancel = new Button { Text = ro ? "Anuleaza" : "Cancel", Location = new System.Drawing.Point(375, 225), Size = new System.Drawing.Size(95, 32), DialogResult = DialogResult.Cancel };
            Controls.Add(save); Controls.Add(cancel);
            AcceptButton = save; CancelButton = cancel;
            ProviderChanged(null, EventArgs.Empty);
        }

        private static Label MakeLabel(string text, int x, int y) { return new Label { Text = text, Location = new System.Drawing.Point(x, y + 3), AutoSize = true }; }

        private void ProviderChanged(object sender, EventArgs e)
        {
            AiProvider selected = (AiProvider)provider.SelectedIndex;
            model.Enabled = selected != AiProvider.None;
            apiKey.Enabled = selected == AiProvider.Gemini || selected == AiProvider.Groq;
            if (String.IsNullOrWhiteSpace(model.Text) || (!String.IsNullOrEmpty(DefaultModel(previousProvider)) && model.Text == DefaultModel(previousProvider)))
                model.Text = DefaultModel(selected);
            previousProvider = selected;
            hint.Text = romanian
                ? selected == AiProvider.Ollama ? "Instaleaza Ollama, apoi ruleaza o data `ollama run qwen3:4b` (descarca aprox. 2,5 GB). Ollama trebuie sa ruleze local. Raportul nu paraseste calculatorul."
                    : selected == AiProvider.Gemini ? "Sumarul agregat este trimis la Google. Ai nevoie de cheia ta AI Studio. Se aplica cotele, eligibilitatea si termenii Google."
                    : selected == AiProvider.Groq ? "Sumarul agregat este trimis la Groq. Ai nevoie de cheia ta API. Se aplica cotele, eligibilitatea si termenii Groq."
                    : "AI este oprit. Diagnosticul local complet ramane disponibil."
                : selected == AiProvider.Ollama ? "Install Ollama, then run `ollama run qwen3:4b` once (~2.5 GB download). Ollama must run locally. The report stays on this computer."
                    : selected == AiProvider.Gemini ? "The aggregated summary is sent to Google. Your own AI Studio key is required. Google quotas, eligibility, and data terms apply."
                    : selected == AiProvider.Groq ? "The aggregated summary is sent to Groq. Your own API key is required. Groq quotas, eligibility, and data terms apply."
                    : "AI is off. The complete evidence-based local diagnosis remains available.";
        }

        private static string DefaultModel(AiProvider selected)
        {
            if (selected == AiProvider.Ollama) return "qwen3:4b";
            if (selected == AiProvider.Gemini) return "gemini-3-flash-preview";
            if (selected == AiProvider.Groq) return "openai/gpt-oss-20b";
            return "";
        }

        private void SaveClicked(object sender, EventArgs e)
        {
            try
            {
                AiProvider selected = (AiProvider)provider.SelectedIndex;
                AiProviderClient.Save(new AiProviderSettings { Provider = selected, Model = model.Text.Trim(), ApiKey = selected == AiProvider.Gemini || selected == AiProvider.Groq ? apiKey.Text.Trim() : "" });
            }
            catch (Exception exception)
            {
                MessageBox.Show(this, exception.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                DialogResult = DialogResult.None;
            }
        }
    }
}
