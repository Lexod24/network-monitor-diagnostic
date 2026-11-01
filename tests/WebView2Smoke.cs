using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

internal static class WebView2Smoke
{
    private static IntPtr loaderHandle;

    [STAThread]
    private static int Main()
    {
        AppDomain.CurrentDomain.AssemblyResolve += ResolveAssembly;
        try { PrepareNativeLoader(); }
        catch (Exception ex) { Console.WriteLine("FAIL | embedded WebView2 native loader | " + ex.Message); return 1; }
        string runtimeVersion;
        try { runtimeVersion = CoreWebView2Environment.GetAvailableBrowserVersionString(); }
        catch (Exception ex) { Console.WriteLine("FAIL | WebView2 runtime discovery | " + ex.Message); return 1; }

        string failure = null;
        bool sawLocalNavigationError = false;
        bool returnedWithBack = false;
        int step = 0;
        string profile = Path.Combine(Path.GetTempPath(), "NetworkMonitor-WebView2-Smoke-" + Guid.NewGuid().ToString("N"));
        using (Form form = new Form { Text = "WebView2 local smoke test", Width = 640, Height = 480, ShowInTaskbar = false, WindowState = FormWindowState.Minimized })
        using (WebView2 view = new WebView2 { Dock = DockStyle.Fill })
        using (Timer timeout = new Timer { Interval = 20000 })
        {
            form.Controls.Add(view);
            timeout.Tick += delegate { failure = "timed out waiting for local navigation"; timeout.Stop(); form.Close(); };
            view.CoreWebView2InitializationCompleted += delegate(object sender, CoreWebView2InitializationCompletedEventArgs args)
            {
                if (!args.IsSuccess) { failure = "CoreWebView2 initialization failed: " + (args.InitializationException == null ? "unknown error" : args.InitializationException.Message); form.Close(); }
            };
            form.Shown += async delegate
            {
                timeout.Start();
                try
                {
                    CoreWebView2Environment environment = await CoreWebView2Environment.CreateAsync(null, profile, null);
                    await view.EnsureCoreWebView2Async(environment);
                    view.CoreWebView2.NavigationCompleted += delegate(object sender, CoreWebView2NavigationCompletedEventArgs args)
                    {
                        if (step == 0)
                        {
                            if (!args.IsSuccess) { failure = "about:blank navigation failed"; form.Close(); return; }
                            step = 1;
                            view.CoreWebView2.Navigate("https://127.0.0.1:54321/");
                            return;
                        }
                        if (step == 1)
                        {
                            sawLocalNavigationError = !args.IsSuccess || args.HttpStatusCode >= 400;
                            if (!sawLocalNavigationError) { failure = "loopback error was unexpectedly successful"; form.Close(); return; }
                            if (!view.CoreWebView2.CanGoBack) { failure = "WebView2 did not expose Back after the failed navigation"; form.Close(); return; }
                            step = 2;
                            view.CoreWebView2.GoBack();
                            return;
                        }
                        returnedWithBack = args.IsSuccess && view.Source != null && view.Source.AbsoluteUri.StartsWith("about:blank", StringComparison.OrdinalIgnoreCase);
                        if (!returnedWithBack) failure = "Back did not return to the initial local page";
                        form.Close();
                    };
                    view.CoreWebView2.Navigate("about:blank");
                }
                catch (Exception ex) { failure = "WebView2 initialization/navigation threw: " + ex.Message; form.Close(); }
            };
            Application.Run(form);
        }
        for (int attempt = 0; attempt < 10; attempt++)
        {
            try { if (Directory.Exists(profile)) Directory.Delete(profile, true); break; }
            catch { System.Threading.Thread.Sleep(500); }
        }
        if (failure != null) { Console.WriteLine("FAIL | WebView2 runtime=" + runtimeVersion + " | " + failure); return 1; }
        Console.WriteLine("PASS | WebView2 runtime " + runtimeVersion + " initializes; about:blank navigation succeeds; loopback failure is detected; Back returns to local page");
        Console.WriteLine("PASS | smoke test uses only local loopback and sends no speed-test traffic to an external provider");
        return 0;
    }

    private static Assembly ResolveAssembly(object sender, ResolveEventArgs args)
    {
        string name = new AssemblyName(args.Name).Name;
        string resource = name == "Microsoft.Web.WebView2.Core" ? "NetworkMonitorApp.WebView2.Microsoft.Web.WebView2.Core.dll" :
            name == "Microsoft.Web.WebView2.WinForms" ? "NetworkMonitorApp.WebView2.Microsoft.Web.WebView2.WinForms.dll" : null;
        if (resource == null) return null;
        using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource))
        {
            if (stream == null) return null;
            byte[] image = new byte[stream.Length];
            if (stream.Read(image, 0, image.Length) != image.Length) throw new BadImageFormatException("Incomplete embedded WebView2 assembly.");
            return Assembly.Load(image);
        }
    }

    private static void PrepareNativeLoader()
    {
        string resource = IntPtr.Size == 8 ? "NetworkMonitorApp.WebView2.x64.WebView2Loader.dll" : "NetworkMonitorApp.WebView2.x86.WebView2Loader.dll";
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetworkMonitor", "WebView2", "1.0.4314.35", IntPtr.Size == 8 ? "win-x64" : "win-x86");
        string path = Path.Combine(folder, "WebView2Loader.dll");
        using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource))
        {
            if (stream == null) throw new FileNotFoundException("Embedded WebView2 loader is missing.");
            Directory.CreateDirectory(folder);
            byte[] content;
            using (MemoryStream buffer = new MemoryStream()) { stream.CopyTo(buffer); content = buffer.ToArray(); }
            if (!File.Exists(path) || new FileInfo(path).Length != content.Length) File.WriteAllBytes(path, content);
        }
        loaderHandle = LoadLibrary(path);
        if (loaderHandle == IntPtr.Zero) throw new InvalidOperationException("Windows could not load the extracted WebView2 native loader.");
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "LoadLibraryW", SetLastError = true)]
    private static extern IntPtr LoadLibrary(string fileName);
}
