using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace NetworkMonitorApp
{
    internal sealed class SpeedTestProvider
    {
        internal string Name { get; private set; }
        internal string Url { get; private set; }
        internal string PrivacyUrl { get; private set; }
        internal string[] AllowedHosts { get; private set; }

        internal SpeedTestProvider(string name, string url, string privacyUrl, params string[] allowedHosts)
        {
            Name = name;
            Url = url;
            PrivacyUrl = privacyUrl;
            AllowedHosts = allowedHosts;
        }
    }

    internal static class SpeedTestProviderCatalog
    {
        private static readonly SpeedTestProvider[] Providers = new SpeedTestProvider[]
        {
            new SpeedTestProvider("Ookla", "https://www.speedtest.net/", "https://www.speedtest.net/about/privacy", "www.speedtest.net", "speedtest.net"),
            new SpeedTestProvider("Netograf", "https://www.netograf.ro/test", "https://www.netograf.ro/politica-de-confidentialitate", "www.netograf.ro", "netograf.ro"),
            new SpeedTestProvider("DIGI", "https://www.digi.ro/asistenta/servicii-aplicatii/speedtest", "https://www.digi.ro/politica-de-confidentialitate", "www.digi.ro", "digi.ro", "speedcheck-b.rcs-rds.ro"),
            new SpeedTestProvider("Vodafone", "https://speed.vodafone.ro/", "https://www.vodafone.ro/privacy", "speed.vodafone.ro", "www.vodafone.ro", "vodafone.ro")
        };

        internal static IList<SpeedTestProvider> All { get { return Array.AsReadOnly(Providers); } }
        internal static SpeedTestProvider Get(int index) { return index >= 0 && index < Providers.Length ? Providers[index] : Providers[0]; }
        internal static SpeedTestProvider Find(string name)
        {
            foreach (SpeedTestProvider provider in Providers)
                if (String.Equals(provider.Name, name, StringComparison.OrdinalIgnoreCase)) return provider;
            return null;
        }
    }

    internal static class SpeedTestNavigationPolicy
    {
        internal static bool ShouldFallback(bool navigationSucceeded, int httpStatusCode)
        {
            return !navigationSucceeded || httpStatusCode >= 400;
        }

        internal static bool IsAllowedTopLevelUri(SpeedTestProvider provider, string address)
        {
            Uri uri;
            if (provider == null || !Uri.TryCreate(address, UriKind.Absolute, out uri) || uri.Scheme != Uri.UriSchemeHttps) return false;
            foreach (string host in provider.AllowedHosts)
                if (String.Equals(uri.Host, host, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }

    internal static class WebView2EmbeddedRuntime
    {
        private const string CoreResource = "NetworkMonitorApp.WebView2.Microsoft.Web.WebView2.Core.dll";
        private const string WinFormsResource = "NetworkMonitorApp.WebView2.Microsoft.Web.WebView2.WinForms.dll";
        private const string LoaderX86Resource = "NetworkMonitorApp.WebView2.x86.WebView2Loader.dll";
        private const string LoaderX64Resource = "NetworkMonitorApp.WebView2.x64.WebView2Loader.dll";
        private static readonly object Gate = new object();
        private static IntPtr loaderHandle;

        internal static void RegisterAssemblyResolver()
        {
            AppDomain.CurrentDomain.AssemblyResolve -= ResolveAssembly;
            AppDomain.CurrentDomain.AssemblyResolve += ResolveAssembly;
        }

        internal static void PrepareNativeLoader()
        {
            lock (Gate)
            {
                if (loaderHandle != IntPtr.Zero) return;
                string resource = IntPtr.Size == 8 ? LoaderX64Resource : LoaderX86Resource;
                string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetworkMonitor", "WebView2", "1.0.4314.35", IntPtr.Size == 8 ? "win-x64" : "win-x86");
                string path = Path.Combine(folder, "WebView2Loader.dll");
                Assembly assembly = typeof(WebView2EmbeddedRuntime).Assembly;
                using (Stream source = assembly.GetManifestResourceStream(resource))
                {
                    if (source == null) throw new FileNotFoundException("The embedded WebView2 loader is missing.");
                    Directory.CreateDirectory(folder);
                    byte[] embedded;
                    using (MemoryStream buffer = new MemoryStream()) { source.CopyTo(buffer); embedded = buffer.ToArray(); }
                    bool replace = !File.Exists(path);
                    if (!replace)
                    {
                        using (FileStream existing = File.OpenRead(path))
                        using (SHA256 sha = SHA256.Create())
                        using (SHA256 shaEmbedded = SHA256.Create())
                            replace = !BytesEqual(sha.ComputeHash(existing), shaEmbedded.ComputeHash(embedded));
                    }
                    if (replace)
                    {
                        string temporary = path + ".new";
                        File.WriteAllBytes(temporary, embedded);
                        if (File.Exists(path)) File.Delete(path);
                        File.Move(temporary, path);
                    }
                }
                loaderHandle = LoadLibrary(path);
                if (loaderHandle == IntPtr.Zero) throw new InvalidOperationException("Windows could not load the embedded WebView2 native loader.");
            }
        }

        private static bool BytesEqual(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != right.Length) return false;
            int difference = 0;
            for (int i = 0; i < left.Length; i++) difference |= left[i] ^ right[i];
            return difference == 0;
        }

        private static Assembly ResolveAssembly(object sender, ResolveEventArgs args)
        {
            string name = new AssemblyName(args.Name).Name;
            string resource = name == "Microsoft.Web.WebView2.Core" ? CoreResource :
                name == "Microsoft.Web.WebView2.WinForms" ? WinFormsResource : null;
            if (resource == null) return null;
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource))
            {
                if (stream == null) return null;
                byte[] image = new byte[stream.Length];
                int read = stream.Read(image, 0, image.Length);
                if (read != image.Length) throw new BadImageFormatException("The embedded WebView2 assembly is incomplete.");
                return Assembly.Load(image);
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "LoadLibraryW")]
        private static extern IntPtr LoadLibrary(string fileName);
    }

    internal sealed class SpeedTestPage : Panel
    {
        private readonly ComboBox providerSelector;
        private readonly Label heading;
        private readonly Label subtitle;
        private readonly Label disclosure;
        private readonly Label status;
        private readonly Label providerLabel;
        private readonly Panel browserHost;
        private readonly GlassButton loadButton;
        private readonly GlassButton backButton;
        private readonly GlassButton reloadButton;
        private readonly GlassButton browserButton;
        private readonly GlassButton privacyButton;
        private WebView2 webView;
        private string webViewUserDataFolder;
        private SpeedTestProvider activeProvider;
        private AppLanguage language = AppLanguage.English;

        internal SpeedTestPage()
        {
            Dock = DockStyle.Fill;
            AutoScroll = true;
            BackColor = LiquidGlassTheme.Window;

            heading = new Label { AutoSize = true, Location = new Point(28, 20), Font = new Font("Segoe UI Semibold", 21F, FontStyle.Bold), ForeColor = LiquidGlassTheme.Text };
            subtitle = new Label { AutoSize = false, Location = new Point(30, 61), Size = new Size(1080, 35), Font = new Font("Segoe UI", 9.5F), ForeColor = LiquidGlassTheme.SecondaryText };
            Controls.Add(heading);
            Controls.Add(subtitle);

            SoftSurfacePanel controls = new SoftSurfacePanel { Location = new Point(24, 105), Size = new Size(1188, 126), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right, Padding = new Padding(14) };
            Controls.Add(controls);
            providerLabel = new Label { AutoSize = true, Location = new Point(18, 16), Font = new Font("Segoe UI", 8.5F), ForeColor = LiquidGlassTheme.SecondaryText };
            controls.Controls.Add(providerLabel);
            providerSelector = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(18, 39), Size = new Size(174, 30), Font = new Font("Segoe UI", 9F), BackColor = Color.FromArgb(255, 20, 27, 38), ForeColor = LiquidGlassTheme.Text };
            foreach (SpeedTestProvider provider in SpeedTestProviderCatalog.All) providerSelector.Items.Add(provider.Name);
            providerSelector.SelectedIndex = 0;
            providerSelector.SelectedIndexChanged += delegate { UpdateDisclosure(); if (webView != null) SetStatus("SpeedProviderSelected"); };
            controls.Controls.Add(providerSelector);

            loadButton = CreateButton("SpeedLoad", 158);
            loadButton.Location = new Point(207, 37);
            loadButton.Click += LoadButtonClicked;
            controls.Controls.Add(loadButton);
            backButton = CreateButton("SpeedBack", 94);
            backButton.Location = new Point(375, 37);
            backButton.Enabled = false;
            backButton.Click += delegate { if (webView != null && webView.CoreWebView2 != null && webView.CoreWebView2.CanGoBack) webView.CoreWebView2.GoBack(); };
            controls.Controls.Add(backButton);
            reloadButton = CreateButton("SpeedReload", 96);
            reloadButton.Location = new Point(477, 37);
            reloadButton.Enabled = false;
            reloadButton.Click += delegate { if (webView != null && webView.CoreWebView2 != null) webView.CoreWebView2.Reload(); };
            controls.Controls.Add(reloadButton);
            browserButton = CreateButton("SpeedBrowser", 160);
            browserButton.Location = new Point(581, 37);
            browserButton.Enabled = false;
            browserButton.Click += BrowserButtonClicked;
            controls.Controls.Add(browserButton);
            privacyButton = CreateButton("SpeedPrivacy", 120);
            privacyButton.Location = new Point(749, 37);
            privacyButton.Click += PrivacyButtonClicked;
            controls.Controls.Add(privacyButton);
            status = new Label { AutoSize = false, Location = new Point(18, 78), Size = new Size(1140, 26), Font = new Font("Segoe UI", 8.5F), ForeColor = LiquidGlassTheme.SecondaryText, AutoEllipsis = true };
            controls.Controls.Add(status);

            disclosure = new Label { Location = new Point(30, 237), Size = new Size(1168, 72), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right, Font = new Font("Segoe UI", 8.5F), ForeColor = LiquidGlassTheme.SecondaryText, AutoEllipsis = false };
            Controls.Add(disclosure);
            SoftSurfacePanel browserSurface = new SoftSurfacePanel { Location = new Point(24, 314), Size = new Size(1188, 508), Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right, Padding = new Padding(8) };
            Controls.Add(browserSurface);
            browserHost = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(255, 12, 17, 25) };
            browserSurface.Controls.Add(browserHost);
            Label empty = new Label { TextAlign = ContentAlignment.MiddleCenter, Dock = DockStyle.Fill, Font = new Font("Segoe UI", 10F), ForeColor = LiquidGlassTheme.SecondaryText };
            empty.Name = "emptyState";
            browserHost.Controls.Add(empty);
            Resize += delegate { controls.Width = Math.Max(660, ClientSize.Width - 48); disclosure.Width = Math.Max(600, ClientSize.Width - 60); browserSurface.Width = Math.Max(600, ClientSize.Width - 48); browserSurface.Height = Math.Max(300, ClientSize.Height - 329); };
            UpdateLanguage();
        }

        internal AppLanguage Language { get { return language; } set { language = value; UpdateLanguage(); } }

        private SpeedTestProvider CurrentProvider { get { return SpeedTestProviderCatalog.Get(providerSelector.SelectedIndex); } }

        private GlassButton CreateButton(string key, int width)
        {
            GlassButton button = new GlassButton { Size = new Size(width, 34), Font = new Font("Segoe UI", 8.5F), BackColor = Color.FromArgb(255, 20, 27, 38), ForeColor = LiquidGlassTheme.Text };
            button.Text = Localization.Get(language, key);
            return button;
        }

        private void UpdateLanguage()
        {
            heading.Text = Localization.Get(language, "SpeedHeading");
            subtitle.Text = Localization.Get(language, "SpeedSubtitle");
            loadButton.Text = Localization.Get(language, "SpeedLoad");
            backButton.Text = Localization.Get(language, "SpeedBack");
            reloadButton.Text = Localization.Get(language, "SpeedReload");
            browserButton.Text = Localization.Get(language, "SpeedBrowser");
            privacyButton.Text = Localization.Get(language, "SpeedPrivacy");
            providerLabel.Text = Localization.Get(language, "SpeedProvider");
            if (browserHost.Controls.Count > 0 && browserHost.Controls[0].Name == "emptyState") browserHost.Controls[0].Text = Localization.Get(language, "SpeedEmpty");
            UpdateDisclosure();
            if (status.Tag is string) status.Text = Localization.Get(language, (string)status.Tag);
        }

        private void UpdateDisclosure()
        {
            string key = CurrentProvider.Name == "Vodafone" ? "SpeedDisclosureVodafone" : "SpeedDisclosure";
            disclosure.Text = Localization.Format(language, key, CurrentProvider.Name);
        }

        private async void LoadButtonClicked(object sender, EventArgs e)
        {
            if (webView != null && webView.CoreWebView2 != null)
            {
                NavigateSelectedProvider();
                return;
            }
            loadButton.Enabled = false;
            SetStatus("SpeedCheckingRuntime");
            try
            {
                WebView2EmbeddedRuntime.PrepareNativeLoader();
                string runtimeVersion = CoreWebView2Environment.GetAvailableBrowserVersionString();
                if (String.IsNullOrWhiteSpace(runtimeVersion)) throw new InvalidOperationException("Runtime unavailable");
                webViewUserDataFolder = Path.Combine(Path.GetTempPath(), "NetworkMonitor-SpeedTest-" + Guid.NewGuid().ToString("N"));
                CoreWebView2Environment environment = await CoreWebView2Environment.CreateAsync(null, webViewUserDataFolder, null);
                webView = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(255, 12, 17, 25), ZoomFactor = 1.0 };
                webView.CoreWebView2InitializationCompleted += WebViewInitializationCompleted;
                browserHost.Controls.Clear();
                browserHost.Controls.Add(webView);
                await webView.EnsureCoreWebView2Async(environment);
                if (webView.CoreWebView2 == null) throw new InvalidOperationException("WebView2 initialization returned no core object.");
                webView.CoreWebView2.Settings.IsPasswordAutosaveEnabled = false;
                webView.CoreWebView2.Settings.IsGeneralAutofillEnabled = false;
                webView.CoreWebView2.NavigationStarting += NavigationStarting;
                webView.CoreWebView2.NavigationCompleted += NavigationCompleted;
                webView.CoreWebView2.NewWindowRequested += NewWindowRequested;
                NavigateSelectedProvider();
            }
            catch (Exception ex)
            {
                webView = null;
                browserHost.Controls.Clear();
                QueueTemporaryProfileCleanup();
                Label error = new Label { TextAlign = ContentAlignment.MiddleCenter, Dock = DockStyle.Fill, Font = new Font("Segoe UI", 10F), ForeColor = LiquidGlassTheme.SecondaryText, Text = Localization.Format(language, "SpeedRuntimeUnavailable", ex.Message) };
                browserHost.Controls.Add(error);
                SetStatus("SpeedFallbackReady");
                browserButton.Enabled = true;
            }
            finally { loadButton.Enabled = true; }
        }

        private void WebViewInitializationCompleted(object sender, CoreWebView2InitializationCompletedEventArgs e)
        {
            if (!e.IsSuccess)
            {
                SetStatus("SpeedRuntimeUnavailableShort");
                browserButton.Enabled = true;
            }
        }

        private void NavigateSelectedProvider()
        {
            if (webView == null || webView.CoreWebView2 == null) return;
            activeProvider = CurrentProvider;
            browserButton.Enabled = false;
            backButton.Enabled = false;
            SetStatus("SpeedLoading");
            webView.CoreWebView2.Navigate(activeProvider.Url);
        }

        private void NavigationStarting(object sender, CoreWebView2NavigationStartingEventArgs e)
        {
            if (!SpeedTestNavigationPolicy.IsAllowedTopLevelUri(activeProvider, e.Uri))
            {
                e.Cancel = true;
                SetStatus("SpeedExternalNavigationBlocked");
                browserButton.Enabled = true;
            }
        }

        private void NavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            ApplyNavigationResult(e.IsSuccess, e.HttpStatusCode);
            backButton.Enabled = webView.CoreWebView2.CanGoBack;
            reloadButton.Enabled = true;
        }

        internal void ApplyNavigationResult(bool navigationSucceeded, int httpStatusCode)
        {
            if (SpeedTestNavigationPolicy.ShouldFallback(navigationSucceeded, httpStatusCode))
            {
                SetStatus("SpeedPageFailed");
                browserButton.Enabled = true;
                return;
            }
            browserButton.Enabled = true;
            SetStatus("SpeedPageLoaded");
        }

        private void NewWindowRequested(object sender, CoreWebView2NewWindowRequestedEventArgs e)
        {
            e.Handled = true;
            SetStatus("SpeedPopupBlocked");
            browserButton.Enabled = true;
        }

        private void BrowserButtonClicked(object sender, EventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo(CurrentProvider.Url) { UseShellExecute = true });
                SetStatus("SpeedOpenedBrowser");
            }
            catch (Exception ex) { SetStatus("SpeedBrowserError", ex.Message); }
        }

        private void PrivacyButtonClicked(object sender, EventArgs e)
        {
            try { Process.Start(new ProcessStartInfo(CurrentProvider.PrivacyUrl) { UseShellExecute = true }); }
            catch (Exception ex) { SetStatus("SpeedBrowserError", ex.Message); }
        }

        private void SetStatus(string key, params object[] args)
        {
            status.Tag = key;
            status.Text = args == null || args.Length == 0 ? Localization.Get(language, key) : Localization.Format(language, key, args);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) QueueTemporaryProfileCleanup();
            base.Dispose(disposing);
        }

        private void QueueTemporaryProfileCleanup()
        {
            string path = webViewUserDataFolder;
            webViewUserDataFolder = null;
            if (String.IsNullOrWhiteSpace(path)) return;
            string tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string fullPath = Path.GetFullPath(path);
            if (!fullPath.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(fullPath).StartsWith("NetworkMonitor-SpeedTest-", StringComparison.Ordinal)) return;
            ThreadPool.QueueUserWorkItem(delegate
            {
                for (int attempt = 0; attempt < 10; attempt++)
                {
                    try { if (Directory.Exists(fullPath)) Directory.Delete(fullPath, true); return; }
                    catch { Thread.Sleep(500); }
                }
            });
        }
    }
}
