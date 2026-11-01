using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace NetworkMonitorApp
{
    internal sealed class LoadLatencySessionMarker
    {
        internal DateTimeOffset Timestamp;
        internal string Kind;
        internal string Outcome;
    }

    internal sealed class LoadLatencyResult
    {
        internal string Outcome;
        internal double? UnloadedLatencyMs;
        internal double? DownloadLoadedLatencyMs;
        internal double? UploadLoadedLatencyMs;
        internal double? DownloadMbps;
        internal double? UploadMbps;
        internal int PayloadBytes;
        internal double DurationSeconds;
    }

    internal sealed class LoadLatencyRequestBudget
    {
        internal int DownloadRequests;
        internal int UploadRequests;
        internal int ZeroByteRequests;
        internal long PayloadBytes;
    }

    internal static class LoadLatencyTestPolicy
    {
        internal const string Provider = "Cloudflare";
        internal const string Origin = "https://speed.cloudflare.com";
        internal const int DownloadBytesPerRequest = 8 * 1024 * 1024;
        internal const int DownloadRequestCount = 2;
        internal const int UploadBytesPerRequest = 4 * 1024 * 1024;
        internal const int UploadRequestCount = 2;
        internal const int MaximumPayloadBytes = DownloadBytesPerRequest * DownloadRequestCount + UploadBytesPerRequest * UploadRequestCount;
        internal const int MaximumDurationSeconds = 60;
        internal const int MaximumZeroByteRequests = 80;
        internal const int RequestAbortMilliseconds = 20000;

        internal static bool TryReserve(Uri uri, string method, long requestBodyBytes, LoadLatencyRequestBudget budget)
        {
            if (uri == null || budget == null || !String.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                !String.Equals(uri.Authority, "speed.cloudflare.com", StringComparison.OrdinalIgnoreCase)) return false;
            string requestMethod = (method ?? "GET").ToUpperInvariant();
            string path = uri.AbsolutePath;
            string bytesText = System.Web.HttpUtility.ParseQueryString(uri.Query)["bytes"];
            int requestedBytes;
            if (!Int32.TryParse(bytesText, NumberStyles.None, CultureInfo.InvariantCulture, out requestedBytes) || requestedBytes < 0) return false;
            if (path == "/__down" && requestMethod == "GET")
            {
                if (requestedBytes == 0)
                {
                    if (budget.ZeroByteRequests >= MaximumZeroByteRequests) return false;
                    budget.ZeroByteRequests++;
                    return true;
                }
                if (requestedBytes != DownloadBytesPerRequest || budget.DownloadRequests >= DownloadRequestCount ||
                    requestBodyBytes != 0 || budget.PayloadBytes + requestedBytes > MaximumPayloadBytes) return false;
                budget.DownloadRequests++;
                budget.PayloadBytes += requestedBytes;
                return true;
            }
            if (path == "/__up" && requestMethod == "POST")
            {
                if (requestedBytes != UploadBytesPerRequest || requestBodyBytes != UploadBytesPerRequest ||
                    budget.UploadRequests >= UploadRequestCount || budget.PayloadBytes + requestBodyBytes > MaximumPayloadBytes) return false;
                budget.UploadRequests++;
                budget.PayloadBytes += requestBodyBytes;
                return true;
            }
            return false;
        }

        internal static string GetConfirmation(AppLanguage language)
        {
            return language == AppLanguage.Romanian
                ? "Test opțional Cloudflare: măsoară latența fără sarcină, apoi sub descărcare și încărcare: maximum 16 MiB descărcat + 8 MiB încărcat (24 MiB payload, plus traficul protocolului/antetelor), maximum 60 de secunde. Nu cere cont, cheie API sau plată în aplicație; abonamentul de Internet poate contoriza traficul. Cererile ajung la Cloudflare, care poate procesa IP-ul public și metadatele conexiunii; SDK-ul nu trimite separat un sumar de rezultate. Valorile măsurate rămân doar în fereastra testului; nu se salvează în CSV/raport și nu se trimit la AI. Raportul sesiunii poate conține numai intervalul și starea testului. Monitorizarea obișnuită este suspendată pe acest interval și testul poate afecta temporar alte aplicații. Continui?"
                : "Optional Cloudflare test: measures unloaded latency, then latency during download and upload: up to 16 MiB downloaded + 8 MiB uploaded (24 MiB payload, plus protocol/header traffic), with a 60-second maximum. No account, API key, or payment in the app is required; your Internet plan may count the traffic. Requests go to Cloudflare, which may process your public IP and connection metadata; the SDK does not separately upload a results summary. Numeric measurements stay only in the test window; they are not saved to CSV/report or sent to AI. The session report may contain only the interval and test status. Ordinary monitoring is paused for this interval, and the test may temporarily affect other apps. Continue?";
        }

        internal static string OutcomeLocalizationKey(string outcome)
        {
            switch (outcome)
            {
                case "completed": return "LoadOutcomeCompleted";
                case "failed": return "LoadOutcomeFailed";
                case "cancelled": return "LoadOutcomeCancelled";
                case "timeout": return "LoadOutcomeTimeout";
                case "running": return "LoadOutcomeRunning";
                case "not-started": return "LoadOutcomeNotStarted";
                default: return "LoadOutcomeUnavailable";
            }
        }

        internal static string FormatTimeline(IEnumerable<LoadLatencySessionMarker> markers, AppLanguage language)
        {
            List<LoadLatencySessionMarker> ordered = (markers ?? Enumerable.Empty<LoadLatencySessionMarker>())
                .Where(delegate(LoadLatencySessionMarker marker) { return marker != null && (marker.Kind == "start" || marker.Kind == "end"); })
                .OrderBy(delegate(LoadLatencySessionMarker marker) { return marker.Timestamp; }).ToList();
            if (ordered.Count == 0) return String.Empty;
            StringBuilder report = new StringBuilder(Localization.Get(language, "LoadTimelineHeading"));
            foreach (LoadLatencySessionMarker marker in ordered)
            {
                report.AppendLine();
                report.Append(marker.Timestamp.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                report.Append(" · ").Append(Localization.Get(language, marker.Kind == "start" ? "LoadTimelineStarted" : "LoadTimelineEnded"));
                if (marker.Kind == "end")
                    report.Append(" · ").Append(Localization.Get(language, "LoadTimelineOutcome")).Append(": ")
                        .Append(Localization.Get(language, OutcomeLocalizationKey(marker.Outcome)));
            }
            report.AppendLine();
            report.Append(Localization.Get(language, "LoadTimelineExcluded"));
            return report.ToString();
        }

        internal static string BuildModule(AppLanguage language)
        {
            Assembly assembly = typeof(LoadLatencyTestPolicy).Assembly;
            using (Stream stream = assembly.GetManifestResourceStream("NetworkMonitorApp.CloudflareSpeedTest.js"))
            {
                if (stream == null) throw new FileNotFoundException("The pinned Cloudflare Speedtest module is missing.");
                string sdk;
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8, true)) sdk = reader.ReadToEnd();
                JavaScriptSerializer json = new JavaScriptSerializer();
                Dictionary<string, object> policy = new Dictionary<string, object>
                {
                    { "origin", Origin }, { "downloadBytes", DownloadBytesPerRequest }, { "downloadCount", DownloadRequestCount },
                    { "uploadBytes", UploadBytesPerRequest }, { "uploadCount", UploadRequestCount },
                    { "maxBytes", MaximumPayloadBytes }, { "maxZeroByteRequests", MaximumZeroByteRequests },
                    { "durationSeconds", MaximumDurationSeconds }, { "requestAbortMs", RequestAbortMilliseconds }
                };
                Dictionary<string, string> labels = new Dictionary<string, string>
                {
                    { "heading", T(language, "LoadHeading") }, { "subtitle", T(language, "LoadSubtitle") },
                    { "baseline", T(language, "LoadBaseline") }, { "download", T(language, "LoadDownload") },
                    { "upload", T(language, "LoadUpload") }, { "downloadRate", T(language, "LoadDownloadRate") },
                    { "uploadRate", T(language, "LoadUploadRate") }, { "starting", T(language, "LoadStarting") },
                    { "latencyPhase", T(language, "LoadLatencyPhase") }, { "downloadPhase", T(language, "LoadDownloadPhase") },
                    { "uploadPhase", T(language, "LoadUploadPhase") }, { "finished", T(language, "LoadFinished") },
                    { "failed", T(language, "LoadFailed") }, { "cancelled", T(language, "LoadCancelled") },
                    { "timeout", T(language, "LoadTimeout") }, { "noValue", T(language, "NoValue") }
                };
                StringBuilder html = new StringBuilder(sdk.Length + 10000);
                html.Append("<!doctype html><html><head><meta charset='utf-8'><meta http-equiv='Content-Security-Policy' content=\"default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; connect-src https://speed.cloudflare.com; object-src 'none'; base-uri 'none'; form-action 'none'\"><meta name='viewport' content='width=device-width,initial-scale=1'><style>");
                html.Append("*{box-sizing:border-box}body{margin:0;padding:24px;background:#090d14;color:#f4f6fa;font:14px 'Segoe UI',sans-serif}.card{max-width:860px;margin:0 auto;border:1px solid #384352;border-radius:22px;background:linear-gradient(145deg,#19212d,#10151e);padding:28px;box-shadow:0 18px 48px #0008}h1{font-size:24px;margin:0 0 8px}p{color:#aab6c7;line-height:1.55}.status{padding:12px 14px;border:1px solid #384352;border-radius:14px;background:#0b1018;margin:20px 0}.grid{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:12px}.metric{border:1px solid #384352;border-radius:16px;background:#111923;padding:16px;min-height:94px}.label{color:#aab6c7;font-size:12px}.value{font-size:23px;font-weight:600;margin-top:10px}.rate{margin-top:20px;color:#aab6c7}small{display:block;margin-top:22px;color:#8794a7;line-height:1.5}@media(max-width:640px){.grid{grid-template-columns:1fr}.card{padding:18px}}");
                html.Append("</style></head><body><main class='card'><h1 id='heading'></h1><p id='subtitle'></p><div class='status' id='status'></div><section class='grid'><div class='metric'><div class='label' id='baselineLabel'></div><div class='value' id='baseline'>—</div></div><div class='metric'><div class='label' id='downloadLabel'></div><div class='value' id='download'>—</div></div><div class='metric'><div class='label' id='uploadLabel'></div><div class='value' id='upload'>—</div></div></section><div class='rate' id='downloadRate'></div><div class='rate' id='uploadRate'></div><small id='limits'></small></main><script type='module'>");
                html.Append(sdk).AppendLine();
                html.Append("const policy=").Append(json.Serialize(policy)).Append(";const labels=").Append(json.Serialize(labels)).Append(@";
const $=id=>document.getElementById(id);$('heading').textContent=labels.heading;$('subtitle').textContent=labels.subtitle;$('baselineLabel').textContent=labels.baseline;$('downloadLabel').textContent=labels.download;$('uploadLabel').textContent=labels.upload;
$('status').textContent=labels.starting;$('limits').textContent=`${Math.round(policy.maxBytes/1048576)} MiB maximum payload · ${policy.durationSeconds} sec maximum · Results are temporary and are not exported.`;
const nativeFetch=window.fetch.bind(window);const budget={down:0,up:0,zero:0,bytes:0};let done=false;let engine=null;
window.fetch=(input,init={})=>{try{const request=input instanceof Request?input:null;const url=new URL(typeof input==='string'?input:input.url,location.href);const method=(init.method||(request&&request.method)||'GET').toUpperCase();const body=init.body;let bodyBytes=0;if(typeof body==='string')bodyBytes=new TextEncoder().encode(body).length;else if(body&&typeof body.byteLength==='number')bodyBytes=body.byteLength;else if(body&&typeof body.size==='number')bodyBytes=body.size;let allowed=false;const n=Number(url.searchParams.get('bytes'));if(url.origin===policy.origin&&url.pathname==='/__down'&&method==='GET'&&Number.isSafeInteger(n)&&n>=0){if(n===0&&budget.zero<policy.maxZeroByteRequests){budget.zero++;allowed=true;}else if(n===policy.downloadBytes&&budget.down<policy.downloadCount&&bodyBytes===0&&budget.bytes+n<=policy.maxBytes){budget.down++;budget.bytes+=n;allowed=true;}}else if(url.origin===policy.origin&&url.pathname==='/__up'&&method==='POST'&&n===policy.uploadBytes&&bodyBytes===policy.uploadBytes&&budget.up<policy.uploadCount&&budget.bytes+bodyBytes<=policy.maxBytes){budget.up++;budget.bytes+=bodyBytes;allowed=true;}if(!allowed)return Promise.reject(new Error('Request blocked by the local traffic limit.'));const options=Object.assign({},init,{redirect:'error'});return nativeFetch(input,options);}catch(e){return Promise.reject(e);}};
const send=(type,data={})=>{if(window.chrome&&window.chrome.webview)window.chrome.webview.postMessage(JSON.stringify(Object.assign({type},data)));};
window.chrome&&window.chrome.webview&&window.chrome.webview.addEventListener('message',event=>{if(event.data==='cancel'&&engine&&!done){done=true;clearTimeout(hardTimeout);engine.pause();$('status').textContent=labels.cancelled;}});
const fmt=v=>Number.isFinite(v)?`${v.toFixed(1)} ms`:labels.noValue;
try{engine=new SpeedTestEngine({autoStart:false,downloadApiUrl:policy.origin+'/__down',uploadApiUrl:policy.origin+'/__up',logAimApiUrl:null,logMeasurementApiUrl:null,measurements:[{type:'latency',numPackets:10},{type:'download',bytes:policy.downloadBytes,count:policy.downloadCount},{type:'upload',bytes:policy.uploadBytes,count:policy.uploadCount}],measureDownloadLoadedLatency:true,measureUploadLoadedLatency:true,loadedLatencyThrottle:200,loadedLatencyMaxPoints:20,loadedRequestMinDuration:30,bandwidthAbortRequestDuration:policy.requestAbortMs});
const render=results=>{const unloaded=results.getUnloadedLatency();const down=results.getDownLoadedLatency();const up=results.getUpLoadedLatency();const dbps=results.getDownloadBandwidth();const ubps=results.getUploadBandwidth();$('baseline').textContent=fmt(unloaded);$('download').textContent=fmt(down);$('upload').textContent=fmt(up);$('downloadRate').textContent=`${labels.downloadRate}: ${Number.isFinite(dbps)?(dbps/1000000).toFixed(1)+' Mbps':labels.noValue}`;$('uploadRate').textContent=`${labels.uploadRate}: ${Number.isFinite(ubps)?(ubps/1000000).toFixed(1)+' Mbps':labels.noValue}`;return {unloaded,down,up,downloadMbps:Number.isFinite(dbps)?dbps/1000000:null,uploadMbps:Number.isFinite(ubps)?ubps/1000000:null,payloadBytes:budget.bytes};};
engine.onRunningChange=running=>{if(running)$('status').textContent=labels.latencyPhase;};engine.onResultsChange=event=>{if(event&&event.type==='download')$('status').textContent=labels.downloadPhase;else if(event&&event.type==='upload')$('status').textContent=labels.uploadPhase;else $('status').textContent=labels.latencyPhase;send('progress',render(engine.results));};engine.onError=()=>{if(done)return;done=true;clearTimeout(hardTimeout);$('status').textContent=labels.failed;send('failed',render(engine.results));};engine.onFinish=results=>{if(done)return;done=true;clearTimeout(hardTimeout);$('status').textContent=labels.finished;send('finished',Object.assign(render(results),{durationSeconds:results.getTotalDurationMs()/1000}));};
const hardTimeout=setTimeout(()=>{if(done)return;done=true;engine.pause();$('status').textContent=labels.timeout;send('timeout',Object.assign(render(engine.results),{durationSeconds:policy.durationSeconds}));},policy.durationSeconds*1000);send('started');engine.play();
}catch(e){done=true;$('status').textContent=labels.failed;send('failed',{payloadBytes:budget.bytes});}
</script></body></html>");
                return html.ToString();
            }
        }

        private static string T(AppLanguage language, string key) { return Localization.Get(language, key); }
    }

    internal sealed class LoadLatencyTestDialog : Form
    {
        private readonly AppLanguage language;
        private readonly WebView2 webView;
        private readonly GlassButton cancelButton;
        private readonly Label statusLabel;
        private readonly System.Windows.Forms.Timer timeoutTimer;
        private bool completed;
        private DateTimeOffset startedAt;
        internal LoadLatencyResult Result { get; private set; }

        internal LoadLatencyTestDialog(AppLanguage selectedLanguage)
        {
            language = selectedLanguage;
            Text = T("LoadTitle");
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(920, 680);
            MinimumSize = new Size(700, 520);
            BackColor = LiquidGlassTheme.Window;
            ForeColor = LiquidGlassTheme.Text;
            Font = new Font("Segoe UI", 9F);

            webView = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(9, 13, 20) };
            Controls.Add(webView);
            Panel footer = new Panel { Dock = DockStyle.Bottom, Height = 58, Padding = new Padding(14, 10, 14, 10), BackColor = LiquidGlassTheme.Window };
            statusLabel = new Label { Dock = DockStyle.Fill, Text = T("LoadPreparing"), TextAlign = ContentAlignment.MiddleLeft, ForeColor = LiquidGlassTheme.SecondaryText, AutoEllipsis = true };
            cancelButton = new GlassButton { Text = T("LoadCancel"), Size = new Size(136, 36), Dock = DockStyle.Right, BackColor = LiquidGlassTheme.GlassInset, ForeColor = LiquidGlassTheme.Text, Font = new Font("Segoe UI Semibold", 9F, FontStyle.Bold), Cursor = Cursors.Hand };
            cancelButton.Click += delegate { CancelTest(); };
            footer.Controls.Add(statusLabel);
            footer.Controls.Add(cancelButton);
            Controls.Add(footer);
            timeoutTimer = new System.Windows.Forms.Timer { Interval = LoadLatencyTestPolicy.MaximumDurationSeconds * 1000 };
            timeoutTimer.Tick += delegate
            {
                timeoutTimer.Stop();
                try { if (webView.CoreWebView2 != null) webView.CoreWebView2.PostWebMessageAsString("cancel"); } catch { }
                Finish("timeout", T("LoadTimeout"));
            };
            Shown += BeginTest;
            FormClosing += delegate { if (!completed) CancelTest(); };
            GlassWindowEffects.Attach(this, true);
        }

        private async void BeginTest(object sender, EventArgs e)
        {
            startedAt = DateTimeOffset.Now;
            timeoutTimer.Start();
            try
            {
                WebView2EmbeddedRuntime.PrepareNativeLoader();
                await webView.EnsureCoreWebView2Async();
                if (completed || IsDisposed) return;
                webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                webView.CoreWebView2.Settings.AreDevToolsEnabled = false;
                webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
                webView.CoreWebView2.Settings.IsZoomControlEnabled = false;
                webView.CoreWebView2.NavigationStarting += delegate(object navigationSender, CoreWebView2NavigationStartingEventArgs args)
                {
                    Uri uri;
                    if (!Uri.TryCreate(args.Uri, UriKind.Absolute, out uri) || uri.Scheme != "about") args.Cancel = true;
                };
                webView.CoreWebView2.NewWindowRequested += delegate(object windowSender, CoreWebView2NewWindowRequestedEventArgs args) { args.Handled = true; };
                webView.CoreWebView2.WebMessageReceived += WebMessageReceived;
                webView.NavigateToString(LoadLatencyTestPolicy.BuildModule(language));
            }
            catch
            {
                if (completed || IsDisposed) return;
                Result = new LoadLatencyResult { Outcome = "unavailable", DurationSeconds = (DateTimeOffset.Now - startedAt).TotalSeconds };
                statusLabel.Text = T("LoadRuntimeUnavailable");
                cancelButton.Text = T("LoadClose");
                completed = true;
            }
        }

        private void WebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                string message = e.TryGetWebMessageAsString();
                IDictionary<string, object> data = new JavaScriptSerializer().DeserializeObject(message) as IDictionary<string, object>;
                if (data == null) return;
                string type = data.ContainsKey("type") ? Convert.ToString(data["type"], CultureInfo.InvariantCulture) : String.Empty;
                if (type == "started") { statusLabel.Text = T("LoadRunning"); return; }
                if (type == "progress") { Result = ParseResult("running", data); return; }
                if (type == "failed") { Finish("failed", T("LoadFailed"), data); return; }
                if (type == "timeout") { Finish("timeout", T("LoadTimeout"), data); return; }
                if (type == "finished")
                {
                    Result = ParseResult("completed", data);
                    statusLabel.Text = T("LoadFinished");
                    cancelButton.Text = T("LoadClose");
                    timeoutTimer.Stop();
                    completed = true;
                }
            }
            catch { Finish("failed", T("LoadFailed")); }
        }

        private void Finish(string outcome, string status, IDictionary<string, object> data = null)
        {
            if (completed) return;
            completed = true;
            timeoutTimer.Stop();
            try { if (webView.CoreWebView2 != null) webView.CoreWebView2.Stop(); } catch { }
            Result = new LoadLatencyResult
            {
                Outcome = outcome,
                UnloadedLatencyMs = data == null ? (Result == null ? (double?)null : Result.UnloadedLatencyMs) : NullableNumber(data, "unloaded"),
                DownloadLoadedLatencyMs = data == null ? (Result == null ? (double?)null : Result.DownloadLoadedLatencyMs) : NullableNumber(data, "down"),
                UploadLoadedLatencyMs = data == null ? (Result == null ? (double?)null : Result.UploadLoadedLatencyMs) : NullableNumber(data, "up"),
                DownloadMbps = data == null ? (Result == null ? (double?)null : Result.DownloadMbps) : NullableNumber(data, "downloadMbps"),
                UploadMbps = data == null ? (Result == null ? (double?)null : Result.UploadMbps) : NullableNumber(data, "uploadMbps"),
                PayloadBytes = data == null ? (Result == null ? 0 : Result.PayloadBytes) : Integer(data, "payloadBytes"),
                DurationSeconds = data == null ? (DateTimeOffset.Now - startedAt).TotalSeconds : (NullableNumber(data, "durationSeconds") ?? (DateTimeOffset.Now - startedAt).TotalSeconds)
            };
            statusLabel.Text = status;
            cancelButton.Text = T("LoadClose");
        }

        private void CancelTest()
        {
            if (completed) { DialogResult = DialogResult.OK; Close(); return; }
            try { if (webView.CoreWebView2 != null) webView.CoreWebView2.PostWebMessageAsString("cancel"); } catch { }
            Finish("cancelled", T("LoadCancelled"));
            try { if (webView.CoreWebView2 != null) webView.CoreWebView2.Stop(); } catch { }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                timeoutTimer.Stop();
                timeoutTimer.Dispose();
                try { webView.Dispose(); } catch { }
            }
            base.Dispose(disposing);
        }

        private static double? NullableNumber(IDictionary<string, object> values, string key)
        {
            object value;
            if (values == null || !values.TryGetValue(key, out value) || value == null) return null;
            double number;
            return Double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out number) && !Double.IsNaN(number) && !Double.IsInfinity(number) ? (double?)number : null;
        }

        private static int Integer(IDictionary<string, object> values, string key)
        {
            double? number = NullableNumber(values, key);
            return number.HasValue ? (int)Math.Max(0, Math.Min(Int32.MaxValue, number.Value)) : 0;
        }

        private LoadLatencyResult ParseResult(string outcome, IDictionary<string, object> data)
        {
            return new LoadLatencyResult
            {
                Outcome = outcome,
                UnloadedLatencyMs = NullableNumber(data, "unloaded"),
                DownloadLoadedLatencyMs = NullableNumber(data, "down"),
                UploadLoadedLatencyMs = NullableNumber(data, "up"),
                DownloadMbps = NullableNumber(data, "downloadMbps"),
                UploadMbps = NullableNumber(data, "uploadMbps"),
                PayloadBytes = Integer(data, "payloadBytes"),
                DurationSeconds = NullableNumber(data, "durationSeconds") ?? (DateTimeOffset.Now - startedAt).TotalSeconds
            };
        }

        private string T(string key) { return Localization.Get(language, key); }
    }
}
