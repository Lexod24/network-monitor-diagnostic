using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace NetworkMonitorApp
{
    internal sealed class UpdateManifest
    {
        internal Version Version;
        internal string Sha256;
    }

    internal static class AutoUpdater
    {
        private const string ManifestUrl = "https://raw.githubusercontent.com/Lexod24/network-monitor-diagnostic/main/update.json";
        private const string PackageUrl = "https://raw.githubusercontent.com/Lexod24/network-monitor-diagnostic/main/NetworkMonitor-Portable.zip";
        private const int MaximumManifestBytes = 16384;
        private const int MaximumPackageBytes = 32 * 1024 * 1024;
        private const int MaximumExecutableBytes = 24 * 1024 * 1024;

        internal static UpdateManifest CheckForUpdate()
        {
            string json = ReadHttpsText(ManifestUrl + "?nocache=" + DateTime.UtcNow.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture), MaximumManifestBytes, 5000);
            UpdateManifest manifest = ParseManifest(json);
            Version current = new Version(AppVersion.Current);
            return manifest.Version > current ? manifest : null;
        }

        internal static UpdateManifest ParseManifest(string json)
        {
            if (String.IsNullOrWhiteSpace(json)) throw new InvalidDataException("The update manifest is empty.");
            object parsed = new System.Web.Script.Serialization.JavaScriptSerializer().DeserializeObject(json);
            IDictionary<string, object> values = parsed as IDictionary<string, object>;
            if (values == null) throw new InvalidDataException("The update manifest has an invalid format.");

            object versionValue;
            object hashValue;
            if (!values.TryGetValue("version", out versionValue) || !values.TryGetValue("sha256", out hashValue))
                throw new InvalidDataException("The update manifest is missing required fields.");

            string versionText = Convert.ToString(versionValue, System.Globalization.CultureInfo.InvariantCulture);
            Version version;
            if (!Regex.IsMatch(versionText ?? String.Empty, @"^\d+\.\d+\.\d+$") || !Version.TryParse(versionText, out version) || version.Build < 0 || version.Revision >= 0)
                throw new InvalidDataException("The update manifest contains an invalid version.");

            string hash = Convert.ToString(hashValue, System.Globalization.CultureInfo.InvariantCulture);
            if (!Regex.IsMatch(hash ?? String.Empty, @"^[0-9a-fA-F]{64}$")) throw new InvalidDataException("The update manifest contains an invalid SHA-256 hash.");
            return new UpdateManifest { Version = version, Sha256 = hash.ToLowerInvariant() };
        }

        internal static string ComputeSha256(Stream stream)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] digest = sha.ComputeHash(stream);
                StringBuilder result = new StringBuilder(digest.Length * 2);
                foreach (byte value in digest) result.Append(value.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
                return result.ToString();
            }
        }

        internal static string DownloadAndExtract(UpdateManifest manifest)
        {
            if (manifest == null || manifest.Version <= new Version(AppVersion.Current)) throw new InvalidDataException("There is no newer version to install.");
            string updateDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetworkMonitor", "Updates", manifest.Version.ToString());
            Directory.CreateDirectory(updateDirectory);
            string archivePath = Path.Combine(updateDirectory, "NetworkMonitor-Portable.zip");
            string executablePath = Path.Combine(updateDirectory, "NetworkMonitor.exe");

            DownloadFile(PackageUrl + "?version=" + Uri.EscapeDataString(manifest.Version.ToString()), archivePath, MaximumPackageBytes);
            string actualHash;
            using (FileStream stream = File.OpenRead(archivePath)) actualHash = ComputeSha256(stream);
            if (!String.Equals(actualHash, manifest.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The downloaded update did not match the publisher's SHA-256 hash.");

            if (File.Exists(executablePath)) File.Delete(executablePath);
            using (FileStream archiveFile = File.OpenRead(archivePath))
            using (ZipArchive archive = new ZipArchive(archiveFile, ZipArchiveMode.Read, false))
            {
                ZipArchiveEntry entry = null;
                foreach (ZipArchiveEntry candidate in archive.Entries)
                {
                    if (String.Equals(candidate.FullName, "NetworkMonitor.exe", StringComparison.Ordinal))
                    {
                        if (entry != null) throw new InvalidDataException("The update package contains duplicate executables.");
                        entry = candidate;
                    }
                }
                if (entry == null || entry.Length < 32768 || entry.Length > MaximumExecutableBytes)
                    throw new InvalidDataException("The update package does not contain a valid application executable.");
                using (Stream input = entry.Open())
                using (FileStream output = new FileStream(executablePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    CopyBounded(input, output, MaximumExecutableBytes);
            }

            Version executableVersion = ReadExecutableVersion(executablePath);
            if (executableVersion == null || executableVersion.Major != manifest.Version.Major || executableVersion.Minor != manifest.Version.Minor || executableVersion.Build != manifest.Version.Build)
            {
                try { File.Delete(executablePath); } catch { }
                throw new InvalidDataException("The executable version does not match the update manifest.");
            }
            return executablePath;
        }

        internal static Version ReadExecutableVersion(string path)
        {
            try
            {
                AssemblyName name = AssemblyName.GetAssemblyName(path);
                return name.Version;
            }
            catch { return null; }
        }

        internal static void StartApplyAndExit(string stagedExecutable, int processId, string targetExecutable)
        {
            if (String.IsNullOrWhiteSpace(stagedExecutable) || !File.Exists(stagedExecutable)) throw new FileNotFoundException("The verified update executable is missing.");
            if (String.IsNullOrWhiteSpace(targetExecutable) || !File.Exists(targetExecutable)) throw new FileNotFoundException("The installed application could not be found.");
            if (Path.GetFullPath(targetExecutable).Equals(Path.GetFullPath(stagedExecutable), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The update cannot replace itself in place.");

            string arguments = "--apply-update " + processId.ToString(System.Globalization.CultureInfo.InvariantCulture) + " " + Quote(targetExecutable) + " " + Quote(stagedExecutable);
            ProcessStartInfo info = new ProcessStartInfo(stagedExecutable, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetDirectoryName(stagedExecutable)
            };
            Process.Start(info);
        }

        internal static int ApplyStagedUpdate(string[] args)
        {
            try
            {
                if (args == null || args.Length != 4) throw new ArgumentException("The update command is invalid.");
                int processId;
                if (!Int32.TryParse(args[1], out processId) || processId <= 0) throw new ArgumentException("The application process ID is invalid.");
                string target = Path.GetFullPath(args[2]);
                string staged = Path.GetFullPath(args[3]);
                string targetName = Path.GetFileName(target);
                if (!String.Equals(targetName, "NetworkMonitor.exe", StringComparison.OrdinalIgnoreCase) && !String.Equals(targetName, "NetworkMonitor-2026.exe", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The update target is not a recognized Network Monitor executable.");
                if (!File.Exists(staged) || !File.Exists(target)) throw new FileNotFoundException("The application or staged update is missing.");
                if (Path.GetFullPath(Application.ExecutablePath).Equals(target, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("The staged updater must run from its temporary update folder.");

                try
                {
                    using (Process oldApp = Process.GetProcessById(processId))
                        if (!oldApp.WaitForExit(45000)) throw new TimeoutException("The old application did not close in time.");
                }
                catch (ArgumentException) { }

                Version newVersion = ReadExecutableVersion(staged);
                if (newVersion == null || newVersion <= new Version(AppVersion.Current))
                    throw new InvalidDataException("The staged update version is invalid.");

                string replacement = target + ".new";
                string backup = target + ".previous";
                if (File.Exists(replacement)) File.Delete(replacement);
                if (File.Exists(backup)) File.Delete(backup);
                File.Copy(staged, replacement, false);
                File.Replace(replacement, target, backup, true);
                try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(target) }); }
                catch
                {
                    if (File.Exists(backup)) File.Replace(backup, target, null, true);
                    throw;
                }
                try { if (File.Exists(backup)) File.Delete(backup); } catch { }
                return 0;
            }
            catch (Exception ex)
            {
                try { MessageBox.Show("Network Monitor could not finish the update. Your existing version was kept when possible.\r\n\r\n" + ex.Message, "Network Monitor update", MessageBoxButtons.OK, MessageBoxIcon.Warning); } catch { }
                return 1;
            }
        }

        private static string ReadHttpsText(string url, int maximumBytes, int timeoutMilliseconds)
        {
            HttpWebRequest request = CreateRequest(url, timeoutMilliseconds);
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            {
                if (response.StatusCode != HttpStatusCode.OK) throw new WebException("The update server returned an unexpected response.");
                if (response.ContentLength > maximumBytes) throw new InvalidDataException("The update manifest is too large.");
                using (Stream input = response.GetResponseStream())
                using (MemoryStream output = new MemoryStream())
                {
                    CopyBounded(input, output, maximumBytes);
                    return Encoding.UTF8.GetString(output.ToArray()).TrimStart('\uFEFF');
                }
            }
        }

        private static void DownloadFile(string url, string path, int maximumBytes)
        {
            string partial = path + ".download";
            try
            {
                if (File.Exists(partial)) File.Delete(partial);
                HttpWebRequest request = CreateRequest(url, 20000);
                request.ReadWriteTimeout = 20000;
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                {
                    if (response.StatusCode != HttpStatusCode.OK) throw new WebException("The update download returned an unexpected response.");
                    if (response.ContentLength > maximumBytes) throw new InvalidDataException("The update package is too large.");
                    using (Stream input = response.GetResponseStream())
                    using (FileStream output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        CopyBounded(input, output, maximumBytes);
                }
                if (File.Exists(path)) File.Delete(path);
                File.Move(partial, path);
            }
            finally { try { if (File.Exists(partial)) File.Delete(partial); } catch { } }
        }

        private static HttpWebRequest CreateRequest(string url, int timeoutMilliseconds)
        {
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri) || uri.Scheme != Uri.UriSchemeHttps || !String.Equals(uri.Host, "raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The update URL is not trusted.");
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(uri);
            request.Method = "GET";
            request.UserAgent = "NetworkMonitorDiagnostic/" + AppVersion.Current;
            request.Accept = "application/octet-stream, application/json";
            request.Headers[HttpRequestHeader.CacheControl] = "no-cache";
            request.Timeout = timeoutMilliseconds;
            request.ReadWriteTimeout = timeoutMilliseconds;
            request.AllowAutoRedirect = true;
            return request;
        }

        private static void CopyBounded(Stream input, Stream output, int maximumBytes)
        {
            byte[] buffer = new byte[81920];
            int total = 0;
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                total += read;
                if (total > maximumBytes) throw new InvalidDataException("The update download exceeded its size limit.");
                output.Write(buffer, 0, read);
            }
        }

        private static string Quote(string value) { return "\"" + value.Replace("\"", "\\\"") + "\""; }
    }
}
