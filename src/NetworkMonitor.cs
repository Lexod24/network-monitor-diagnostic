using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace NetworkMonitorApp
{
    internal sealed class ProbeResult
    {
        public string Status;
        public double? LatencyMs;
    }

    internal sealed class InternetTargetObservation
    {
        public string Name;
        public string Status;
        public double? LatencyMs;
    }

    internal sealed class InternetTargetDefinition
    {
        public string Name;
        public string Address;
    }

    internal sealed class Sample
    {
        public DateTimeOffset Timestamp;
        public string WifiState;
        public int? WifiSignalPercent;
        public string GatewayPingStatus;
        public double? GatewayLatencyMs;
        public double? GatewayLossPct;
        public string InternetPingStatus;
        public double? InternetLatencyMs;
        public double? InternetLossPct;
        public List<InternetTargetObservation> InternetTargetResults;
        public int? InternetTargetSucceeded;
        public int? InternetTargetAttempted;
        public string ActiveInterfaceType;
        public bool ActiveInterfaceChanged;
        public long? NetworkBytesReceived;
        public long? NetworkBytesSent;
        public double? DownloadMbps;
        public double? UploadMbps;
        public string NetworkInterfaceId; // retained only in memory to detect adapter switches; never written to logs or sent to AI
        public string HttpsStatus;
        public double? HttpsLatencyMs;
        public string HttpsEndpointResults;
        public int? HttpsSucceeded;
        public int? HttpsAttempted;
        public string DnsStatus;
        public double? DnsLatencyMs;
        public string DnsEndpointResults;
        public int? DnsSucceeded;
        public int? DnsAttempted;
        public string Connectivity;
        public string Events;
    }

    internal static class NetworkProbe
    {
        private const int PingTimeoutMs = 900;
        private const int HttpsTimeoutMs = 3000;
        private const int DnsTimeoutMs = 2500;
        private const int LatencySpikeMs = 150;
        [DllImport("iphlpapi.dll", ExactSpelling = true)]
        private static extern int GetBestInterface(uint destinationAddress, out uint interfaceIndex);
        private static readonly InternetTargetDefinition[] InternetPingTargets = new InternetTargetDefinition[]
        {
            new InternetTargetDefinition { Name = "Cloudflare", Address = "1.1.1.1" },
            new InternetTargetDefinition { Name = "Google", Address = "8.8.8.8" },
            new InternetTargetDefinition { Name = "Quad9", Address = "9.9.9.9" }
        };
        private static readonly string[] DnsProbeHosts = new string[] { "www.microsoft.com", "www.google.com", "www.cloudflare.com" };
        private static readonly string[] HttpsProbeUrls = new string[]
        {
            "https://www.msftconnecttest.com/connecttest.txt",
            "https://www.google.com/generate_204",
            "https://cp.cloudflare.com/generate_204"
        };

        public static Sample Capture(List<Sample> previousSamples, DateTimeOffset timestamp, bool checkDns)
        {
            int? signal;
            string gateway;
            string activeInterfaceType;
            string wifiState = GetNetworkSnapshot(out signal, out gateway, out activeInterfaceType);
            long? bytesReceived;
            long? bytesSent;
            string interfaceId;
            GetTrafficCounters(gateway, out bytesReceived, out bytesSent, out interfaceId);
            Sample lastSample = previousSamples == null ? null : previousSamples.LastOrDefault();
            double elapsedSeconds = lastSample == null ? 0 : (timestamp - lastSample.Timestamp).TotalSeconds;
            bool sameInterface = lastSample != null && !String.IsNullOrEmpty(interfaceId) && String.Equals(interfaceId, lastSample.NetworkInterfaceId, StringComparison.Ordinal);
            Task<ProbeResult> gatewayTask = gateway == null ? null : Task.Factory.StartNew(delegate { return PingTarget(gateway); });
            Task<ProbeResult>[] internetTasks = InternetPingTargets.Select(delegate(InternetTargetDefinition target)
            {
                return Task.Factory.StartNew(delegate { return PingTarget(target.Address); });
            }).ToArray();
            Task<List<ProbeResult>> dnsTask = checkDns ? Task.Factory.StartNew(delegate { return ProbeDnsHosts(); }) : null;
            try
            {
                List<Task> pingTaskList = internetTasks.Cast<Task>().ToList();
                if (gatewayTask != null) pingTaskList.Add(gatewayTask);
                Task[] pingTasks = pingTaskList.ToArray();
                Task.WaitAll(pingTasks, PingTimeoutMs + 250);
            }
            catch { }
            ProbeResult gatewayProbe = gateway == null ? new ProbeResult { Status = "NoGateway", LatencyMs = null }
                : (gatewayTask.IsCompleted && !gatewayTask.IsFaulted ? gatewayTask.Result : new ProbeResult { Status = "Timeout", LatencyMs = PingTimeoutMs });
            List<InternetTargetObservation> targetResults = new List<InternetTargetObservation>();
            for (int i = 0; i < internetTasks.Length; i++)
            {
                ProbeResult result = internetTasks[i].IsCompleted && !internetTasks[i].IsFaulted
                    ? internetTasks[i].Result : new ProbeResult { Status = "Timeout", LatencyMs = PingTimeoutMs };
                targetResults.Add(new InternetTargetObservation { Name = InternetPingTargets[i].Name, Status = result.Status, LatencyMs = result.LatencyMs });
            }
            ProbeResult internetProbe = AggregateInternetTargets(targetResults);

            List<ProbeResult> httpsResults = new List<ProbeResult>();
            if ((internetProbe.Status == "AllFailed" || internetProbe.Status == "Partial") && targetResults.Count(delegate(InternetTargetObservation result) { return result.Status == "Success"; }) < 2)
            {
                Task<ProbeResult>[] requests = HttpsProbeUrls.Select(delegate(string url)
                {
                    return Task.Factory.StartNew(delegate { return HttpsProbe(url); });
                }).ToArray();
                try { Task.WaitAll(requests, HttpsTimeoutMs + 1000); } catch { }
                foreach (Task<ProbeResult> task in requests)
                    httpsResults.Add(task.IsCompleted && !task.IsFaulted ? task.Result : new ProbeResult { Status = "Timeout", LatencyMs = HttpsTimeoutMs });
            }
            int httpsSucceeded = httpsResults.Count(delegate(ProbeResult p) { return p.Status == "Success"; });
            ProbeResult https = new ProbeResult
            {
                Status = httpsResults.Count == 0 ? "NotNeeded" : (httpsSucceeded > 0 ? "Success" : "AllFailed"),
                LatencyMs = httpsResults.Where(delegate(ProbeResult p) { return p.Status == "Success" && p.LatencyMs.HasValue; })
                    .Select(delegate(ProbeResult p) { return p.LatencyMs.Value; }).DefaultIfEmpty().Min()
            };

            if (dnsTask != null && !dnsTask.IsCompleted) dnsTask.Wait(DnsTimeoutMs + 250);
            List<ProbeResult> dnsResults = dnsTask != null && dnsTask.IsCompleted && !dnsTask.IsFaulted ? dnsTask.Result : new List<ProbeResult>();
            int dnsSucceeded = dnsResults.Count(delegate(ProbeResult p) { return p.Status == "Success"; });
            ProbeResult dns = !checkDns ? new ProbeResult { Status = "NotChecked", LatencyMs = null }
                : new ProbeResult { Status = dnsSucceeded > 0 ? "Success" : "AllFailed", LatencyMs = dnsResults.Select(delegate(ProbeResult p) { return p.LatencyMs ?? 0; }).DefaultIfEmpty().Max() };

            Sample sample = new Sample();
            sample.Timestamp = timestamp;
            sample.WifiState = wifiState;
            sample.WifiSignalPercent = signal;
            sample.GatewayPingStatus = gatewayProbe.Status;
            sample.GatewayLatencyMs = gatewayProbe.LatencyMs;
            sample.InternetPingStatus = internetProbe.Status;
            sample.InternetLatencyMs = internetProbe.LatencyMs;
            sample.InternetTargetResults = targetResults;
            sample.InternetTargetSucceeded = targetResults.Count(delegate(InternetTargetObservation result) { return result.Status == "Success"; });
            sample.InternetTargetAttempted = targetResults.Count;
            sample.ActiveInterfaceType = activeInterfaceType;
            sample.ActiveInterfaceChanged = lastSample != null && !String.IsNullOrEmpty(interfaceId) && !String.IsNullOrEmpty(lastSample.NetworkInterfaceId) && !String.Equals(interfaceId, lastSample.NetworkInterfaceId, StringComparison.Ordinal);
            sample.NetworkBytesReceived = bytesReceived;
            sample.NetworkBytesSent = bytesSent;
            sample.NetworkInterfaceId = interfaceId;
            sample.DownloadMbps = CalculateMegabitsPerSecond(bytesReceived, sameInterface ? lastSample.NetworkBytesReceived : (long?)null, elapsedSeconds);
            sample.UploadMbps = CalculateMegabitsPerSecond(bytesSent, sameInterface ? lastSample.NetworkBytesSent : (long?)null, elapsedSeconds);
            sample.HttpsStatus = https.Status;
            sample.HttpsLatencyMs = httpsResults.Any(delegate(ProbeResult p) { return p.Status == "Success"; }) ? https.LatencyMs : (double?)null;
            sample.HttpsSucceeded = httpsResults.Count == 0 ? (int?)null : httpsSucceeded;
            sample.HttpsAttempted = httpsResults.Count == 0 ? (int?)null : httpsResults.Count;
            sample.HttpsEndpointResults = httpsResults.Count == 0 ? "NotNeeded" : String.Join(";", httpsResults.Select(delegate(ProbeResult p, int i)
            {
                return new Uri(HttpsProbeUrls[i]).Host + "=" + p.Status + (p.LatencyMs.HasValue ? "@" + p.LatencyMs.Value.ToString("0", CultureInfo.InvariantCulture) + "ms" : "");
            }).ToArray());
            sample.DnsStatus = dns.Status;
            sample.DnsLatencyMs = dns.LatencyMs;
            sample.DnsSucceeded = checkDns ? dnsSucceeded : (int?)null;
            sample.DnsAttempted = checkDns ? dnsResults.Count : (int?)null;
            sample.DnsEndpointResults = !checkDns ? "NotChecked" : String.Join(";", dnsResults.Select(delegate(ProbeResult p, int i) { return DnsProbeHosts[i] + "=" + p.Status; }).ToArray());

            List<Sample> window = previousSamples.Skip(Math.Max(0, previousSamples.Count - 11)).ToList();
            window.Add(sample);
            sample.GatewayLossPct = RollingLoss(window, delegate(Sample s) { return s.GatewayPingStatus; });
            sample.InternetLossPct = RollingTargetLoss(window);
            sample.Events = String.Join(";", FindSampleEvents(sample));

            sample.Connectivity = DetermineConnectivity(sample);

            return sample;
        }

        internal static double? CalculateMegabitsPerSecond(long? currentBytes, long? previousBytes, double elapsedSeconds)
        {
            if (!currentBytes.HasValue || !previousBytes.HasValue || elapsedSeconds <= 0 || currentBytes.Value < previousBytes.Value) return null;
            return (currentBytes.Value - previousBytes.Value) * 8.0 / elapsedSeconds / 1000000.0;
        }

        internal static ProbeResult AggregateInternetTargets(IList<InternetTargetObservation> results)
        {
            if (results == null || results.Count == 0) return new ProbeResult { Status = "Unavailable", LatencyMs = null };
            List<double> successfulLatencies = results.Where(delegate(InternetTargetObservation result) { return result != null && result.Status == "Success" && result.LatencyMs.HasValue; })
                .Select(delegate(InternetTargetObservation result) { return result.LatencyMs.Value; }).OrderBy(delegate(double value) { return value; }).ToList();
            int successes = results.Count(delegate(InternetTargetObservation result) { return result != null && result.Status == "Success"; });
            string status = successes == 0 ? "AllFailed" : (successes == results.Count ? "Success" : (successes >= 2 ? "Success" : "Partial"));
            double? latency = null;
            if (successfulLatencies.Count > 0)
                latency = successfulLatencies.Count % 2 == 0
                    ? (successfulLatencies[successfulLatencies.Count / 2 - 1] + successfulLatencies[successfulLatencies.Count / 2]) / 2.0
                    : successfulLatencies[successfulLatencies.Count / 2];
            return new ProbeResult { Status = status, LatencyMs = latency };
        }

        private static double? RollingTargetLoss(IList<Sample> samples)
        {
            int attempted = 0;
            int failed = 0;
            foreach (Sample sample in samples)
            {
                if (sample.InternetTargetResults != null && sample.InternetTargetResults.Count > 0)
                {
                    attempted += sample.InternetTargetResults.Count;
                    failed += sample.InternetTargetResults.Count(delegate(InternetTargetObservation result) { return result == null || result.Status != "Success"; });
                }
                else if (sample.InternetPingStatus != "Unavailable" && sample.InternetPingStatus != "NoGateway" && sample.InternetPingStatus != "NotChecked")
                {
                    attempted++;
                    if (sample.InternetPingStatus != "Success") failed++;
                }
            }
            return attempted == 0 ? (double?)null : Math.Round(100.0 * failed / attempted, 1);
        }

        internal static string FormatInternetTargets(IList<InternetTargetObservation> results, AppLanguage language)
        {
            if (results == null || results.Count == 0) return Localization.Get(language, "Unavailable");
            return String.Join("; ", results.Select(delegate(InternetTargetObservation result)
            {
                string state = result.Status == "Success" && result.LatencyMs.HasValue
                    ? result.LatencyMs.Value.ToString("0", CultureInfo.InvariantCulture) + " ms"
                    : Localization.Status(language, result.Status);
                return result.Name + " " + state;
            }).ToArray());
        }

        internal static string DetermineConnectivity(Sample sample)
        {
            int targetSuccesses = sample.InternetTargetResults == null
                ? (sample.InternetPingStatus == "Success" ? 1 : 0)
                : sample.InternetTargetResults.Count(delegate(InternetTargetObservation result) { return result != null && result.Status == "Success"; });
            bool internetReachable = targetSuccesses > 0 || sample.HttpsStatus == "Success";
            if (!internetReachable) return "OutageSuspected";
            bool actionableEvent = (sample.Events ?? "").Split(new char[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Any(delegate(string code) { return code != "ICMPUnavailableHTTPSOk" && code != "InternetTargetDisagreement"; });
            if (actionableEvent || (sample.InternetTargetResults != null && targetSuccesses == 1 && sample.HttpsStatus == "AllFailed")) return "Degraded";
            return "OK";
        }

        internal static int? GetBestInterfaceIndex(string destination)
        {
            try
            {
                byte[] bytes = IPAddress.Parse(destination).GetAddressBytes();
                if (bytes.Length != 4) return null;
                uint index;
                if (GetBestInterface(BitConverter.ToUInt32(bytes, 0), out index) != 0 || index == 0) return null;
                return checked((int)index);
            }
            catch { return null; }
        }

        internal static string DescribeInterfaceType(NetworkInterfaceType type)
        {
            if (type == NetworkInterfaceType.Wireless80211) return "Wi-Fi";
            if (type == NetworkInterfaceType.Ethernet || type == NetworkInterfaceType.GigabitEthernet) return "Ethernet";
            return "Other";
        }

        private static void GetTrafficCounters(string gateway, out long? bytesReceived, out long? bytesSent, out string interfaceId)
        {
            bytesReceived = null;
            bytesSent = null;
            interfaceId = null;
            if (String.IsNullOrEmpty(gateway)) return;
            try
            {
                NetworkInterface[] interfaces = NetworkInterface.GetAllNetworkInterfaces();
                int? bestIndex = GetBestInterfaceIndex("1.1.1.1");
                NetworkInterface selected = bestIndex.HasValue ? interfaces.FirstOrDefault(delegate(NetworkInterface ni)
                {
                    try { return ni.OperationalStatus == OperationalStatus.Up && ni.GetIPProperties().GetIPv4Properties().Index == bestIndex.Value; }
                    catch { return false; }
                }) : null;
                if (selected == null)
                    selected = interfaces.FirstOrDefault(delegate(NetworkInterface ni)
                    {
                        if (ni.OperationalStatus != OperationalStatus.Up) return false;
                        try
                        {
                            return ni.GetIPProperties().GatewayAddresses.Any(delegate(GatewayIPAddressInformation address)
                            {
                                return address.Address.AddressFamily == AddressFamily.InterNetwork && String.Equals(address.Address.ToString(), gateway, StringComparison.Ordinal);
                            });
                        }
                        catch { return false; }
                    });
                if (selected != null)
                {
                    IPv4InterfaceStatistics counters = selected.GetIPv4Statistics();
                    bytesReceived = counters.BytesReceived;
                    bytesSent = counters.BytesSent;
                    interfaceId = selected.Id;
                }
            }
            catch { bytesReceived = null; bytesSent = null; interfaceId = null; }
        }

        private static string GetNetworkSnapshot(out int? signalPercent, out string gateway, out string activeInterfaceType)
        {
            signalPercent = null;
            gateway = null;
            activeInterfaceType = "Unavailable";
            NetworkInterface[] interfaces;
            try { interfaces = NetworkInterface.GetAllNetworkInterfaces(); }
            catch { return "Unavailable"; }

            NetworkInterface[] wifi = interfaces.Where(delegate(NetworkInterface ni)
            {
                return ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211;
            }).ToArray();

            NetworkInterface wifiUp = wifi.FirstOrDefault(delegate(NetworkInterface ni)
            {
                return ni.OperationalStatus == OperationalStatus.Up;
            });

            string state;
            if (wifi.Length == 0)
                state = "Unavailable";
            else if (wifiUp != null)
                state = "Connected";
            else
                state = "Disconnected";

            int? bestIndex = GetBestInterfaceIndex("1.1.1.1");
            NetworkInterface activeInterface = bestIndex.HasValue ? interfaces.FirstOrDefault(delegate(NetworkInterface ni)
            {
                try { return ni.OperationalStatus == OperationalStatus.Up && ni.GetIPProperties().GetIPv4Properties().Index == bestIndex.Value; }
                catch { return false; }
            }) : null;
            if (activeInterface == null) activeInterface = wifiUp;
            if (activeInterface != null)
            {
                activeInterfaceType = DescribeInterfaceType(activeInterface.NetworkInterfaceType);
                gateway = GetIpv4Gateway(activeInterface);
                if (activeInterface.NetworkInterfaceType == NetworkInterfaceType.Wireless80211) signalPercent = GetWifiSignalPercent();
            }

            bool routeInterfaceKnown = bestIndex.HasValue && activeInterface != null;
            if (gateway == null && !routeInterfaceKnown)
            {
                NetworkInterface fallback = interfaces.FirstOrDefault(delegate(NetworkInterface ni)
                {
                    return ni.OperationalStatus == OperationalStatus.Up && GetIpv4Gateway(ni) != null;
                });
                if (fallback != null)
                {
                    gateway = GetIpv4Gateway(fallback);
                    activeInterfaceType = DescribeInterfaceType(fallback.NetworkInterfaceType);
                }
            }
            return state;
        }

        private static string GetIpv4Gateway(NetworkInterface networkInterface)
        {
            try
            {
                foreach (GatewayIPAddressInformation item in networkInterface.GetIPProperties().GatewayAddresses)
                {
                    IPAddress address = item.Address;
                    if (address.AddressFamily == AddressFamily.InterNetwork && !address.Equals(IPAddress.Any))
                        return address.ToString();
                }
            }
            catch { }
            return null;
        }

        private static int? GetWifiSignalPercent()
        {
            try
            {
                string netsh = Path.Combine(Environment.SystemDirectory, "netsh.exe");
                if (!File.Exists(netsh)) return null;
                ProcessStartInfo info = new ProcessStartInfo(netsh, "wlan show interfaces");
                info.UseShellExecute = false;
                info.CreateNoWindow = true;
                info.RedirectStandardOutput = true;
                info.RedirectStandardError = true;
                using (Process process = Process.Start(info))
                {
                    if (!process.WaitForExit(1000))
                    {
                        try { process.Kill(); } catch { }
                        return null;
                    }
                    string output = process.StandardOutput.ReadToEnd();
                    Match match = Regex.Match(output, @"(?im)^\s*(?:Signal|Semnal)\s*:\s*(\d{1,3})\s*%");
                    if (!match.Success) return null;
                    int value;
                    if (Int32.TryParse(match.Groups[1].Value, out value) && value >= 0 && value <= 100)
                        return value;
                }
            }
            catch { }
            return null;
        }

        private static ProbeResult PingTarget(string address)
        {
            try
            {
                using (Ping ping = new Ping())
                {
                    PingReply reply = ping.Send(address, PingTimeoutMs);
                    if (reply.Status == IPStatus.Success)
                        return new ProbeResult { Status = "Success", LatencyMs = reply.RoundtripTime };
                    return new ProbeResult { Status = reply.Status.ToString(), LatencyMs = null };
                }
            }
            catch
            {
                return new ProbeResult { Status = "Unavailable", LatencyMs = null };
            }
        }

        private static List<ProbeResult> ProbeDnsHosts()
        {
            Task<ProbeResult>[] tasks = DnsProbeHosts.Select(delegate(string host)
            {
                return Task.Factory.StartNew(delegate
                {
                    Stopwatch watch = Stopwatch.StartNew();
                    try
                    {
                        IPAddress[] addresses = Dns.GetHostAddresses(host);
                        return new ProbeResult { Status = addresses != null && addresses.Length > 0 ? "Success" : "Failed", LatencyMs = watch.Elapsed.TotalMilliseconds };
                    }
                    catch { return new ProbeResult { Status = "Failed", LatencyMs = watch.Elapsed.TotalMilliseconds }; }
                });
            }).ToArray();
            try { Task.WaitAll(tasks, DnsTimeoutMs); } catch { }
            List<ProbeResult> results = new List<ProbeResult>();
            foreach (Task<ProbeResult> task in tasks)
                results.Add(task.IsCompleted && !task.IsFaulted ? task.Result : new ProbeResult { Status = "Timeout", LatencyMs = DnsTimeoutMs });
            return results;
        }

        private static ProbeResult HttpsProbe(string url)
        {
            Stopwatch watch = Stopwatch.StartNew();
            HttpWebRequest request = null;
            try
            {
                request = (HttpWebRequest)WebRequest.Create(url);
                request.Method = "GET";
                request.Timeout = HttpsTimeoutMs;
                request.ReadWriteTimeout = HttpsTimeoutMs;
                request.AllowAutoRedirect = false;
                request.UserAgent = "Windows-Network-Monitor/1.0";
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                {
                    watch.Stop();
                    int code = (int)response.StatusCode;
                    if (IsExpectedHttpsResponse(url, response))
                        return new ProbeResult { Status = "Success", LatencyMs = watch.Elapsed.TotalMilliseconds };
                    return new ProbeResult { Status = "HTTP_" + code, LatencyMs = watch.Elapsed.TotalMilliseconds };
                }
            }
            catch (WebException exception)
            {
                HttpWebResponse response = exception.Response as HttpWebResponse;
                if (response != null)
                {
                    int code = (int)response.StatusCode;
                    response.Close();
                    watch.Stop();
                    return new ProbeResult { Status = "HTTP_" + code, LatencyMs = watch.Elapsed.TotalMilliseconds };
                }
                return new ProbeResult { Status = "Failed", LatencyMs = watch.Elapsed.TotalMilliseconds };
            }
            catch
            {
                return new ProbeResult { Status = "Failed", LatencyMs = watch.Elapsed.TotalMilliseconds };
            }
            finally
            {
                if (request != null) request.Abort();
            }
        }

        private static bool IsExpectedHttpsResponse(string url, HttpWebResponse response)
        {
            string host = new Uri(url).Host;
            if (host == "www.google.com" || host == "cp.cloudflare.com")
                return (int)response.StatusCode == 204;
            if (host == "www.msftconnecttest.com" && (int)response.StatusCode == 200)
            {
                using (StreamReader reader = new StreamReader(response.GetResponseStream()))
                    return String.Equals(reader.ReadToEnd().Trim(), "Microsoft Connect Test", StringComparison.Ordinal);
            }
            return false;
        }

        private static double? RollingLoss(List<Sample> samples, Func<Sample, string> status)
        {
            List<string> attempted = samples.Select(status).Where(delegate(string value)
            {
                return value != "Unavailable" && value != "NoGateway" && value != "NotChecked";
            }).ToList();
            if (attempted.Count == 0) return null;
            int failed = attempted.Count(delegate(string value) { return value != "Success"; });
            return Math.Round(100.0 * failed / attempted.Count, 1);
        }

        private static List<string> FindSampleEvents(Sample sample)
        {
            List<string> events = new List<string>();
            int targetSuccesses = sample.InternetTargetResults == null ? (sample.InternetPingStatus == "Success" ? 1 : 0) : sample.InternetTargetResults.Count(delegate(InternetTargetObservation result) { return result != null && result.Status == "Success"; });
            int targetAttempts = sample.InternetTargetResults == null ? (sample.InternetPingStatus == "Unavailable" || sample.InternetPingStatus == "NotChecked" ? 0 : 1) : sample.InternetTargetResults.Count;
            if (sample.WifiState == "Disconnected") events.Add("WiFiDisconnected");
            if (sample.WifiSignalPercent.HasValue && sample.WifiSignalPercent.Value < 35) events.Add("WeakWiFiSignal");
            if (sample.ActiveInterfaceChanged) events.Add("ActiveRouteChanged");
            if (sample.GatewayPingStatus != "Success" && sample.GatewayPingStatus != "Unavailable" && sample.GatewayPingStatus != "NoGateway")
                events.Add("GatewayUnreachable");
            if (targetAttempts > 1 && targetSuccesses > 0 && targetSuccesses < targetAttempts) events.Add("InternetTargetDisagreement");
            if (targetSuccesses == 0 && sample.HttpsStatus == "Success") events.Add("ICMPUnavailableHTTPSOk");
            if (targetSuccesses == 0 && sample.HttpsStatus == "AllFailed") events.Add("InternetOutageSuspected");
            if (sample.HttpsSucceeded.HasValue && sample.HttpsSucceeded.Value > 0 && sample.HttpsAttempted.HasValue && sample.HttpsSucceeded.Value < sample.HttpsAttempted.Value)
                events.Add("HTTPSProbeDisagreement");
            if (sample.DnsStatus == "AllFailed") events.Add("DNSFailure");
            if (sample.DnsSucceeded.HasValue && sample.DnsSucceeded.Value > 0 && sample.DnsAttempted.HasValue && sample.DnsSucceeded.Value < sample.DnsAttempted.Value)
                events.Add("DNSProbeDisagreement");
            if (sample.GatewayLatencyMs.HasValue && sample.GatewayLatencyMs.Value >= LatencySpikeMs) events.Add("GatewayLatencySpike");
            if (sample.InternetLatencyMs.HasValue && sample.InternetLatencyMs.Value >= LatencySpikeMs) events.Add("InternetLatencySpike");
            if (sample.HttpsStatus == "Success" && sample.HttpsLatencyMs.HasValue && sample.HttpsLatencyMs.Value >= LatencySpikeMs)
                events.Add("HTTPSLatencySpike");
            return events;
        }
    }

    internal static class RouteTrace
    {
        internal const string Target = "1.1.1.1";

        internal static string BuildArguments()
        {
            return "/d /4 /h 8 /w 350 " + Target;
        }

        internal static string Run()
        {
            string executable = Path.Combine(Environment.SystemDirectory, "tracert.exe");
            if (!File.Exists(executable)) return "Windows tracert.exe is not available.";
            ProcessStartInfo info = new ProcessStartInfo(executable, BuildArguments());
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            info.RedirectStandardOutput = true;
            info.RedirectStandardError = true;
            info.StandardOutputEncoding = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
            info.StandardErrorEncoding = info.StandardOutputEncoding;
            using (Process process = Process.Start(info))
            {
                if (!process.WaitForExit(12000))
                {
                    try { process.Kill(); } catch { }
                    try { process.WaitForExit(1000); } catch { }
                    return "The route trace exceeded its 12-second safety limit and was stopped. Some routers do not answer traceroute probes.";
                }
                string output = process.StandardOutput.ReadToEnd();
                string error = process.StandardError.ReadToEnd();
                if (String.IsNullOrWhiteSpace(output)) output = error;
                return String.IsNullOrWhiteSpace(output) ? "No route trace output was returned." : output.Trim();
            }
        }
    }

    internal sealed class LoadLatencyComparison
    {
        public int QuietSamples;
        public int ActiveSamples;
        public double? QuietMedianMs;
        public double? ActiveMedianMs;
        public double? GatewayQuietMedianMs;
        public double? GatewayActiveMedianMs;
    }

    internal static class LoadLatencyAnalysis
    {
        internal static LoadLatencyComparison Compare(IList<Sample> samples)
        {
            List<Sample> quiet = (samples ?? new List<Sample>()).Where(delegate(Sample s)
            {
                return s.InternetLatencyMs.HasValue && s.DownloadMbps.HasValue && s.UploadMbps.HasValue && s.DownloadMbps.Value < 0.25 && s.UploadMbps.Value < 0.25;
            }).ToList();
            List<Sample> active = (samples ?? new List<Sample>()).Where(delegate(Sample s)
            {
                return s.InternetLatencyMs.HasValue && s.DownloadMbps.HasValue && s.UploadMbps.HasValue && (s.DownloadMbps.Value >= 1.0 || s.UploadMbps.Value >= 1.0);
            }).ToList();
            return new LoadLatencyComparison
            {
                QuietSamples = quiet.Count,
                ActiveSamples = active.Count,
                QuietMedianMs = quiet.Count < 3 ? (double?)null : Median(quiet.Select(delegate(Sample s) { return s.InternetLatencyMs.Value; })),
                ActiveMedianMs = active.Count < 3 ? (double?)null : Median(active.Select(delegate(Sample s) { return s.InternetLatencyMs.Value; })),
                GatewayQuietMedianMs = quiet.Count(delegate(Sample s) { return s.GatewayLatencyMs.HasValue; }) < 3 ? (double?)null : Median(quiet.Where(delegate(Sample s) { return s.GatewayLatencyMs.HasValue; }).Select(delegate(Sample s) { return s.GatewayLatencyMs.Value; })),
                GatewayActiveMedianMs = active.Count(delegate(Sample s) { return s.GatewayLatencyMs.HasValue; }) < 3 ? (double?)null : Median(active.Where(delegate(Sample s) { return s.GatewayLatencyMs.HasValue; }).Select(delegate(Sample s) { return s.GatewayLatencyMs.Value; }))
            };
        }

        private static double Median(IEnumerable<double> values)
        {
            List<double> ordered = values.OrderBy(delegate(double value) { return value; }).ToList();
            if (ordered.Count == 0) return 0;
            return ordered.Count % 2 == 0 ? (ordered[ordered.Count / 2 - 1] + ordered[ordered.Count / 2]) / 2.0 : ordered[ordered.Count / 2];
        }
    }

    internal sealed class LatencyStatistics
    {
        public int Count;
        public double? Minimum;
        public double? Median;
        public double? P95;
        public double? Maximum;
        public double? Jitter;

        public static LatencyStatistics Calculate(IEnumerable<double?> values)
        {
            List<double?> sequence = values.ToList();
            List<double> samples = sequence.Where(delegate(double? value) { return value.HasValue && !Double.IsNaN(value.Value) && !Double.IsInfinity(value.Value) && value.Value >= 0; })
                .Select(delegate(double? value) { return value.Value; }).ToList();
            if (samples.Count == 0) return new LatencyStatistics();
            List<double> ordered = samples.OrderBy(delegate(double value) { return value; }).ToList();
            double jitterTotal = 0;
            int jitterIntervals = 0;
            double? previous = null;
            foreach (double? value in sequence)
            {
                if (!value.HasValue || Double.IsNaN(value.Value) || Double.IsInfinity(value.Value) || value.Value < 0) { previous = null; continue; }
                if (previous.HasValue) { jitterTotal += Math.Abs(value.Value - previous.Value); jitterIntervals++; }
                previous = value;
            }
            double median = ordered.Count % 2 == 0 ? (ordered[ordered.Count / 2 - 1] + ordered[ordered.Count / 2]) / 2.0 : ordered[ordered.Count / 2];
            int p95Index = Math.Max(0, (int)Math.Ceiling(0.95 * ordered.Count) - 1);
            return new LatencyStatistics
            {
                Count = samples.Count,
                Minimum = ordered[0],
                Median = median,
                P95 = ordered[p95Index],
                Maximum = ordered[ordered.Count - 1],
                Jitter = jitterIntervals > 0 ? (double?)(jitterTotal / jitterIntervals) : null
            };
        }
    }

    internal sealed class LatencyChartPanel : Panel
    {
        private List<Sample> samples = new List<Sample>();
        public AppLanguage Language = AppLanguage.English;

        public LatencyChartPanel()
        {
            DoubleBuffered = true;
            BackColor = Color.White;
            BorderStyle = BorderStyle.FixedSingle;
        }

        public void SetSamples(IList<Sample> values)
        {
            samples = values == null ? new List<Sample>() : values.Skip(Math.Max(0, values.Count - 60)).ToList();
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            Rectangle plot = new Rectangle(48, 23, Math.Max(1, ClientSize.Width - 62), Math.Max(1, ClientSize.Height - 49));
            TextRenderer.DrawText(e.Graphics, Localization.Get(Language, "LatencyChartTitle"), Font, new Point(8, 4), Color.FromArgb(55, 65, 81));
            double maximum = samples.SelectMany(delegate(Sample s) { return new double?[] { s.GatewayLatencyMs, s.InternetLatencyMs }; })
                .Where(delegate(double? v) { return v.HasValue; }).Select(delegate(double? v) { return v.Value; }).DefaultIfEmpty(50).Max();
            maximum = Math.Max(25, Math.Ceiling(maximum / 25.0) * 25.0);
            using (Pen gridPen = new Pen(Color.FromArgb(225, 230, 235)))
            using (Pen gatewayPen = new Pen(Color.FromArgb(28, 110, 210), 2F))
            using (Pen internetPen = new Pen(Color.FromArgb(225, 125, 35), 2F))
            using (Brush pointGateway = new SolidBrush(gatewayPen.Color))
            using (Brush pointInternet = new SolidBrush(internetPen.Color))
            {
                for (int i = 0; i <= 4; i++)
                {
                    float y = plot.Bottom - plot.Height * i / 4F;
                    e.Graphics.DrawLine(gridPen, plot.Left, y, plot.Right, y);
                    TextRenderer.DrawText(e.Graphics, (maximum * i / 4.0).ToString("0", CultureInfo.InvariantCulture), Font, new Point(5, (int)y - 7), Color.Gray);
                }
                DrawSeries(e.Graphics, plot, maximum, gatewayPen, pointGateway, delegate(Sample s) { return s.GatewayLatencyMs; });
                DrawSeries(e.Graphics, plot, maximum, internetPen, pointInternet, delegate(Sample s) { return s.InternetLatencyMs; });
                TextRenderer.DrawText(e.Graphics, Localization.Get(Language, "LatencyChartGateway"), Font, new Point(Math.Max(55, ClientSize.Width - 145), 4), gatewayPen.Color);
                TextRenderer.DrawText(e.Graphics, Localization.Get(Language, "LatencyChartInternet"), Font, new Point(Math.Max(110, ClientSize.Width - 75), 4), internetPen.Color);
            }
            if (samples.Count == 0) TextRenderer.DrawText(e.Graphics, Localization.Get(Language, "LatencyChartEmpty"), Font, plot, Color.Gray, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        private void DrawSeries(Graphics graphics, Rectangle plot, double maximum, Pen pen, Brush brush, Func<Sample, double?> selector)
        {
            PointF? previous = null;
            for (int i = 0; i < samples.Count; i++)
            {
                double? value = selector(samples[i]);
                if (!value.HasValue) { previous = null; continue; }
                float x = samples.Count == 1 ? plot.Left : plot.Left + plot.Width * i / (float)(samples.Count - 1);
                float y = plot.Bottom - (float)Math.Min(maximum, value.Value) / (float)maximum * plot.Height;
                PointF current = new PointF(x, y);
                if (previous.HasValue) graphics.DrawLine(pen, previous.Value, current);
                graphics.FillEllipse(brush, x - 2.5F, y - 2.5F, 5F, 5F);
                previous = current;
            }
        }
    }

    internal static class DiagnosticEngine
    {
        public static string Analyze(IList<Sample> samples, AppLanguage language)
        {
            if (language == AppLanguage.Romanian) return Analyze(samples);
            if (samples == null || samples.Count == 0) return Localization.Get(language, "NoSamples");
            int wifiDrops = samples.Count(delegate(Sample s) { return s.WifiState == "Disconnected"; });
            int weakSignal = samples.Count(delegate(Sample s) { return s.WifiSignalPercent.HasValue && s.WifiSignalPercent.Value < 35; });
            int dnsFailures = samples.Count(delegate(Sample s) { return s.DnsStatus == "AllFailed" || s.DnsStatus == "Failed" || s.DnsStatus == "Timeout"; });
            int dnsDisagreements = samples.Count(delegate(Sample s) { return s.DnsSucceeded.HasValue && s.DnsAttempted.HasValue && s.DnsSucceeded.Value > 0 && s.DnsSucceeded.Value < s.DnsAttempted.Value; });
            int gatewayFailures = samples.Count(delegate(Sample s) { return s.GatewayPingStatus != "Success" && s.GatewayPingStatus != "Unavailable" && s.GatewayPingStatus != "NoGateway"; });
            int gatewayAttempts = samples.Count(delegate(Sample s) { return s.GatewayPingStatus != "Unavailable" && s.GatewayPingStatus != "NoGateway"; });
            int outages = samples.Count(delegate(Sample s) { return ExternalSuccesses(s) == 0 && s.HttpsStatus == "AllFailed"; });
            int httpsSuccess = samples.Sum(delegate(Sample s) { return s.HttpsSucceeded.HasValue ? s.HttpsSucceeded.Value : 0; });
            int httpsAttempts = samples.Sum(delegate(Sample s) { return s.HttpsAttempted.HasValue ? s.HttpsAttempted.Value : 0; });
            double? signal = Average(samples.Where(delegate(Sample s) { return s.WifiSignalPercent.HasValue; }).Select(delegate(Sample s) { return (double)s.WifiSignalPercent.Value; }));
            double? gateway = Average(samples.Where(delegate(Sample s) { return s.GatewayLatencyMs.HasValue; }).Select(delegate(Sample s) { return s.GatewayLatencyMs.Value; }));
            double? internet = Average(samples.Where(delegate(Sample s) { return s.InternetLatencyMs.HasValue; }).Select(delegate(Sample s) { return s.InternetLatencyMs.Value; }));
            double? download = Average(samples.Where(delegate(Sample s) { return s.DownloadMbps.HasValue; }).Select(delegate(Sample s) { return s.DownloadMbps.Value; }));
            double? upload = Average(samples.Where(delegate(Sample s) { return s.UploadMbps.HasValue; }).Select(delegate(Sample s) { return s.UploadMbps.Value; }));
            StringBuilder b = new StringBuilder();
            b.AppendLine("NETWORK SESSION DIAGNOSIS - measured locally; transparent rules");
            b.AppendLine("Likely affected segment: " + AssessLikelySegment(samples, AppLanguage.English));
            b.AppendLine(String.Format(CultureInfo.InvariantCulture, "Period: {0} to {1}", samples[0].Timestamp.ToString("yyyy-MM-dd HH:mm:ss zzz"), samples[samples.Count - 1].Timestamp.ToString("yyyy-MM-dd HH:mm:ss zzz")));
            b.AppendLine(String.Format(CultureInfo.InvariantCulture, "Samples: {0}; nominal interval: 5 seconds", samples.Count));
            b.AppendLine("Measured average sample gap: " + Format(AverageIntervalSeconds(samples), " seconds"));
            b.AppendLine(String.Format(CultureInfo.InvariantCulture, "Measured averages: Wi-Fi signal {0}; gateway {1}; Internet ICMP {2}.", Format(signal, "%"), Format(gateway, " ms"), Format(internet, " ms")));
            b.AppendLine("Active Windows route interface: " + FormatInterfaceStats(samples, AppLanguage.English) + ". Its interface index is used only in memory.");
            b.AppendLine("Internet targets: " + FormatInternetTargetStats(samples) + ". These independent public IP probes are logged by service name; target IPs are not stored.");
            b.AppendLine("Passive traffic on the active gateway interface: average download " + Format(download, " Mbps") + "; upload " + Format(upload, " Mbps") + ". This is current device traffic, not the connection's maximum speed.");
            b.AppendLine("Successful ICMP latency statistics: gateway " + FormatLatencyStatistics(samples.Select(delegate(Sample s) { return s.GatewayLatencyMs; })) + "; Internet " + FormatLatencyStatistics(samples.Select(delegate(Sample s) { return s.InternetLatencyMs; })) + ". Jitter is mean absolute change between consecutive successful samples.");
            b.AppendLine(FormatLoadComparison(samples, AppLanguage.English));
            b.AppendLine(); b.AppendLine("FINDINGS AND INTERPRETATION");
            int findings = 0;
            if (wifiDrops > 0) { b.AppendLine(String.Format(CultureInfo.InvariantCulture, "- Wi-Fi disconnected in {0} sample(s). Windows reported the wireless interface as disconnected. This confirms a disconnect during the capture, but not its physical cause.", wifiDrops)); findings++; }
            if (weakSignal >= 3) { b.AppendLine(String.Format(CultureInfo.InvariantCulture, "- Weak Wi-Fi signal: {0} samples were below 35%. Low signal can contribute to local packet loss; it does not measure radio interference.", weakSignal)); findings++; }
            if (gatewayAttempts >= 4 && 100.0 * gatewayFailures / gatewayAttempts >= 20.0) { b.AppendLine(String.Format(CultureInfo.InvariantCulture, "- Laptop-to-router path: {0:0.0}% of gateway ICMP probes failed ({1}/{2}). This suggests local Wi-Fi/router trouble, but some routers deprioritize ping replies.", 100.0 * gatewayFailures / gatewayAttempts, gatewayFailures, gatewayAttempts)); findings++; }
            if (dnsFailures > 0) { b.AppendLine(String.Format(CultureInfo.InvariantCulture, "- All three test names failed to resolve in {0} DNS check(s). If IP connectivity worked at the same time, DNS becomes a stronger suspect. Windows' resolver is used, once per minute.", dnsFailures)); findings++; }
            if (dnsDisagreements > 0) { b.AppendLine(String.Format(CultureInfo.InvariantCulture, "- DNS results differed across Microsoft, Google, and Cloudflare domains in {0} check(s). This may be a domain-specific issue; it does not confirm a general DNS failure.", dnsDisagreements)); findings++; }
            if (outages > 0) { b.AppendLine(String.Format(CultureInfo.InvariantCulture, "- Internet interruption suspected in {0} sample(s): ICMP and all three independent HTTPS endpoints failed together. This is evidence of broad reachability trouble during those samples, not proof of an ISP fault; firewall, proxy, or filtering may also block probes.", outages)); findings++; }
            int icmpOnly = samples.Count(delegate(Sample s) { return ExternalSuccesses(s) == 0 && s.HttpsStatus == "Success"; });
            if (icmpOnly > 0) { b.AppendLine(String.Format(CultureInfo.InvariantCulture, "- ICMP did not reply in {0} sample(s), while at least one HTTPS endpoint did. Internet access was still available on that path; ping may be filtered.", icmpOnly)); findings++; }
            if (findings == 0) b.AppendLine("- No interruption or threshold-level anomaly was detected in the captured period. This cannot rule out problems outside the session.");
            b.AppendLine(); b.AppendLine("WHAT TO DO NEXT");
            if (gatewayFailures > 0 || weakSignal > 0 || wifiDrops > 0) b.AppendLine("- If the issue repeats, test near the router and compare another device on the same Wi-Fi. If both are affected, restart the router once and check its status lights.");
            if (outages > 0 && gatewayFailures == 0) b.AppendLine("- If the gateway stays reachable while all external checks fail, compare a second device and check the router/modem WAN status. Contact the ISP with the time window if multiple devices are affected.");
            if (dnsFailures > 0) b.AppendLine("- If websites fail by name while other Internet checks work, retry DNS and compare another device before changing DNS settings.");
            if (findings == 0) b.AppendLine("- If the problem returns, run the monitor during the issue and keep the generated CSV for comparison.");
            b.AppendLine();
            b.AppendLine(String.Format(CultureInfo.InvariantCulture, "Independent HTTPS checks: {0}/{1} successful. Microsoft, Google, and Cloudflare are checked when fewer than two external IP targets reply.", httpsSuccess, httpsAttempts));
            b.AppendLine("HTTPS by endpoint: " + FormatEndpointStats(samples, true));
            b.AppendLine("DNS by domain: " + FormatEndpointStats(samples, false));
            b.AppendLine("Limits: ICMP may be filtered; Windows may not expose Wi-Fi signal; a firewall or proxy may block multiple endpoints. The evidence narrows the likely area but cannot by itself prove hardware failure or ISP responsibility.");
            return b.ToString();
        }

        public static string Analyze(IList<Sample> samples)
        {
            if (samples == null || samples.Count == 0)
                return "Nu exista masuratori suficiente pentru diagnostic.";

            List<string> findings = new List<string>();
            int wifiDrops = samples.Count(delegate(Sample s) { return s.WifiState == "Disconnected"; });
            int weakSignal = samples.Count(delegate(Sample s) { return s.WifiSignalPercent.HasValue && s.WifiSignalPercent.Value < 35; });
            int dnsFailures = samples.Count(delegate(Sample s) { return s.DnsStatus == "AllFailed" || s.DnsStatus == "Failed" || s.DnsStatus == "Timeout"; });
            int dnsDisagreements = samples.Count(delegate(Sample s) { return s.DnsSucceeded.HasValue && s.DnsAttempted.HasValue && s.DnsSucceeded.Value > 0 && s.DnsSucceeded.Value < s.DnsAttempted.Value; });
            int gatewayFailures = samples.Count(delegate(Sample s)
            {
                return s.GatewayPingStatus != "Success" && s.GatewayPingStatus != "Unavailable" && s.GatewayPingStatus != "NoGateway";
            });
            int gatewayAttempts = samples.Count(delegate(Sample s)
            {
                return s.GatewayPingStatus != "Unavailable" && s.GatewayPingStatus != "NoGateway";
            });
            int internetOutages = samples.Count(delegate(Sample s)
            {
                return ExternalSuccesses(s) == 0 && s.HttpsStatus == "AllFailed";
            });
            int httpsOnly = samples.Count(delegate(Sample s)
            {
                return ExternalSuccesses(s) == 0 && s.HttpsStatus == "Success";
            });
            int gatewaySpikes = samples.Count(delegate(Sample s) { return s.GatewayLatencyMs.HasValue && s.GatewayLatencyMs.Value >= 150; });
            int internetSpikes = samples.Count(delegate(Sample s) { return s.InternetLatencyMs.HasValue && s.InternetLatencyMs.Value >= 150; });
            int httpsDisagreements = samples.Count(delegate(Sample s) { return s.HttpsSucceeded.HasValue && s.HttpsAttempted.HasValue && s.HttpsSucceeded.Value > 0 && s.HttpsSucceeded.Value < s.HttpsAttempted.Value; });
            int httpsSuccesses = samples.Sum(delegate(Sample s) { return s.HttpsSucceeded.HasValue ? s.HttpsSucceeded.Value : 0; });
            int httpsAttempts = samples.Sum(delegate(Sample s) { return s.HttpsAttempted.HasValue ? s.HttpsAttempted.Value : 0; });
            double gatewayLoss = gatewayAttempts == 0 ? 0 : 100.0 * gatewayFailures / gatewayAttempts;

            if (wifiDrops > 0)
                findings.Add(String.Format(CultureInfo.InvariantCulture,
                    "Wi-Fi: au fost observate {0} esantion(e) cu adaptorul deconectat. Dovada: starea interfetei Wi-Fi raportata de Windows. Incredere: ridicata pentru deconectare; cauza fizica nu poate fi stabilita doar din jurnal.", wifiDrops));

            if (weakSignal >= 3)
                findings.Add(String.Format(CultureInfo.InvariantCulture,
                    "Semnal Wi-Fi: {0} esantioane au fost sub 35%. Dovada: procentul raportat de Windows prin netsh. Incredere: medie; semnalul nu masoara interferentele radio.", weakSignal));

            if (gatewayAttempts >= 4 && gatewayLoss >= 20.0)
                findings.Add(String.Format(CultureInfo.InvariantCulture,
                    "Legatura laptop-router: pierdere ICMP catre gateway de {0:0.0}% ({1}/{2} probe esuate). Dovada: ping-uri catre gateway inregistrate. Incredere: medie; unele routere limiteaza sau blocheaza ICMP.", gatewayLoss, gatewayFailures, gatewayAttempts));

            if (gatewaySpikes > 0)
                findings.Add(String.Format(CultureInfo.InvariantCulture,
                    "Latența locala: {0} proba(e) catre gateway au fost de cel putin 150 ms. Daca apar impreuna cu pierderi sau semnal slab, problema este probabil pe legatura Wi-Fi/router. Incredere: medie.", gatewaySpikes));

            if (internetSpikes > 0 && gatewaySpikes == 0)
                findings.Add(String.Format(CultureInfo.InvariantCulture,
                    "Latența spre Internet: {0} proba(e) au fost de cel putin 150 ms, fara spike-uri observate catre gateway. Indiciul arata spre traseul dincolo de router sau spre destinatia de test, nu identifica singur furnizorul. Incredere: medie.", internetSpikes));

            if (dnsDisagreements > 0)
                findings.Add(String.Format(CultureInfo.InvariantCulture, "DNS: raspunsuri diferite intre domeniile Microsoft, Google si Cloudflare in {0} verificare(i). O singura zona DNS poate avea o problema punctuala; nu confirma o defectiune generala DNS.", dnsDisagreements));

            if (dnsFailures > 0)
                findings.Add(String.Format(CultureInfo.InvariantCulture,
                    "DNS: toate cele trei nume de test nu s-au rezolvat in {0} verificare(i). Daca ping-ul catre IP functiona in acelasi interval, problema DNS devine mai probabila. Incredere: medie; verificarea foloseste resolverul Windows si are loc o data pe minut.", dnsFailures));

            if (httpsOnly > 0)
                findings.Add(String.Format(CultureInfo.InvariantCulture,
                    "ICMP: ping-ul catre Internet a esuat in {0} esantion(e), dar HTTPS a reusit. Aceasta nu confirma o cadere de Internet; poate fi filtrare ICMP sau diferenta de traseu.", httpsOnly));

            if (httpsDisagreements > 0)
                findings.Add(String.Format(CultureInfo.InvariantCulture,
                    "Verificari HTTPS: {0} esantion(e) au avut raspunsuri diferite intre endpointurile Microsoft, Google si Cloudflare ({1}/{2} verificari HTTPS reusite in total). Un endpoint singur poate fi filtrat sau indisponibil; raspunsul partial arata ca nu exista dovada unei caderi totale.", httpsDisagreements, httpsSuccesses, httpsAttempts));

            if (internetOutages > 0)
            {
                bool gatewayMostlyOkay = gatewayAttempts > 0 && gatewayLoss < 20.0;
                string likelyArea = gatewayMostlyOkay
                    ? "Gateway-ul a raspuns in general; intreruperea pare dincolo de router sau serviciul de test."
                    : "Verifica separat legatura Wi-Fi/gateway; probele singure nu separa sigur routerul de furnizor.";
                findings.Add(String.Format(CultureInfo.InvariantCulture,
                    "Acces Internet: ping-ul si toate cele trei verificari HTTPS independente au esuat impreuna in {0} esantion(e). {1} Incredere: medie; proxy-ul, firewall-ul sau filtrarea pot bloca testele. Verifica daca toate dispozitivele au aceeasi problema si compara cu routerul/modemul.", internetOutages, likelyArea));
            }

            if (findings.Count == 0)
                findings.Add("Nu s-au detectat intreruperi sau anomalii peste praguri in perioada masurata. Asta nu exclude probleme care au aparut inainte sau dupa capturare.");

            double? averageSignal = Average(samples.Where(delegate(Sample s) { return s.WifiSignalPercent.HasValue; })
                .Select(delegate(Sample s) { return (double)s.WifiSignalPercent.Value; }));
            double? averageGateway = Average(samples.Where(delegate(Sample s) { return s.GatewayLatencyMs.HasValue; })
                .Select(delegate(Sample s) { return s.GatewayLatencyMs.Value; }));
            double? averageInternet = Average(samples.Where(delegate(Sample s) { return s.InternetLatencyMs.HasValue; })
                .Select(delegate(Sample s) { return s.InternetLatencyMs.Value; }));
            double? averageDownload = Average(samples.Where(delegate(Sample s) { return s.DownloadMbps.HasValue; })
                .Select(delegate(Sample s) { return s.DownloadMbps.Value; }));
            double? averageUpload = Average(samples.Where(delegate(Sample s) { return s.UploadMbps.HasValue; })
                .Select(delegate(Sample s) { return s.UploadMbps.Value; }));

            StringBuilder report = new StringBuilder();
            report.AppendLine("DIAGNOSTIC LOCAL - bazat pe reguli transparente; sumarul poate primi explicatie AI separat");
            report.AppendLine("Segment probabil afectat: " + AssessLikelySegment(samples, AppLanguage.Romanian));
            report.AppendLine(String.Format(CultureInfo.InvariantCulture, "Interval: {0} - {1}",
                samples[0].Timestamp.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture),
                samples[samples.Count - 1].Timestamp.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)));
            report.AppendLine(String.Format(CultureInfo.InvariantCulture, "Esantioane: {0}; frecventa nominala: 5 secunde", samples.Count));
            report.AppendLine("Interval mediu masurat intre esantioane: " + Format(AverageIntervalSeconds(samples), " secunde"));
            report.AppendLine("Medii observate: semnal Wi-Fi {0}; gateway {1}; Internet {2}.".Replace("{0}", Format(averageSignal, "%")).Replace("{1}", Format(averageGateway, " ms")).Replace("{2}", Format(averageInternet, " ms")));
            report.AppendLine("Interfata Windows folosita pentru ruta: " + FormatInterfaceStats(samples, AppLanguage.Romanian) + ". Indexul interfetei este folosit doar temporar.");
            report.AppendLine("Tinte IP independente: " + FormatInternetTargetStats(samples) + ". Adresele IP ale tintelor nu sunt scrise in jurnal.");
            report.AppendLine("Trafic pasiv pe interfata activa: descarcare medie " + Format(averageDownload, " Mbps") + "; incarcare " + Format(averageUpload, " Mbps") + ". Este traficul curent al calculatorului, nu viteza maxima a abonamentului.");
            report.AppendLine("Statistici latență ICMP cu raspuns: gateway " + FormatLatencyStatistics(samples.Select(delegate(Sample s) { return s.GatewayLatencyMs; })) + "; Internet " + FormatLatencyStatistics(samples.Select(delegate(Sample s) { return s.InternetLatencyMs; })) + ". Jitter = media variatiei absolute intre esantioanele reusite consecutive.");
            report.AppendLine(FormatLoadComparison(samples, AppLanguage.Romanian));
            report.AppendLine();
            report.AppendLine("CONSTATARE SI INTERPRETARE");
            for (int i = 0; i < findings.Count; i++)
                report.AppendLine("- " + findings[i]);
            report.AppendLine();
            report.AppendLine(String.Format(CultureInfo.InvariantCulture, "Verificari HTTPS independente: {0}/{1} reusite. Destinatii: Microsoft, Google si Cloudflare; acestea se testeaza cand raspund mai putin de doua tinte IP.", httpsSuccesses, httpsAttempts));
            report.AppendLine("Rezultate HTTPS pe endpoint: " + FormatEndpointStats(samples, true));
            report.AppendLine("Rezultate DNS pe domeniu: " + FormatEndpointStats(samples, false));
            report.AppendLine("Limite: ping-ul poate fi filtrat; semnalul poate lipsi daca Windows nu-l expune; firewall-urile/proxy-urile pot bloca mai multe endpointuri. Comparatia reduce ambiguitatea, dar nu dovedeste singura defecte hardware sau vina furnizorului.");
            return report.ToString();
        }

        internal static string AssessLikelySegment(IList<Sample> samples, AppLanguage language)
        {
            bool ro = language == AppLanguage.Romanian;
            if (samples == null || samples.Count == 0) return ro ? "Date insuficiente pentru localizare." : "Insufficient data to localize the issue.";
            int wifiDrops = samples.Count(delegate(Sample sample) { return sample.WifiState == "Disconnected"; });
            int routeChanges = samples.Count(delegate(Sample sample) { return sample.ActiveInterfaceChanged; });
            int weakSignal = samples.Count(delegate(Sample sample) { return sample.WifiSignalPercent.HasValue && sample.WifiSignalPercent.Value < 35; });
            int gatewayLossWithExternalFailure = samples.Count(delegate(Sample sample)
            {
                return IsGatewayFailure(sample) && ExternalSuccesses(sample) == 0 && sample.HttpsStatus != "Success";
            });
            int externalOutage = samples.Count(delegate(Sample sample) { return ExternalSuccesses(sample) == 0 && sample.HttpsStatus == "AllFailed"; });
            int dnsFailureWithInternet = samples.Count(delegate(Sample sample)
            {
                return (sample.DnsStatus == "AllFailed" || sample.DnsStatus == "Failed" || sample.DnsStatus == "Timeout") &&
                    (ExternalSuccesses(sample) > 0 || sample.HttpsStatus == "Success");
            });
            int destinationDisagreement = samples.Count(delegate(Sample sample)
            {
                int succeeded = ExternalSuccesses(sample);
                int attempted = ExternalAttempts(sample);
                return attempted > 1 && succeeded > 0 && succeeded < attempted;
            });
            int gatewayAttempts = samples.Count(IsGatewayAttempt);
            int gatewayFailures = samples.Count(IsGatewayFailure);
            int upstreamLatencyOnly = samples.Count(delegate(Sample sample)
            {
                return sample.InternetLatencyMs.HasValue && sample.InternetLatencyMs.Value >= 150 &&
                    (!sample.GatewayLatencyMs.HasValue || sample.GatewayLatencyMs.Value < 150);
            });

            if (wifiDrops >= 2)
                return ro ? String.Format(CultureInfo.InvariantCulture, "Wi-Fi: Windows a raportat deconectarea in {0} esantioane. Incredere ridicata doar pentru deconectarea observata; cauza ei nu este stabilita.", wifiDrops)
                    : String.Format(CultureInfo.InvariantCulture, "Wi-Fi: Windows reported a disconnect in {0} samples. High confidence only that the disconnect was observed; its cause is not established.", wifiDrops);
            if (routeChanges > 0)
                return ro ? String.Format(CultureInfo.InvariantCulture, "Ruta Windows a trecut pe alta interfata in {0} esantion(e). Verifica daca schimbarea coincide cu problema; nu identifica singura cauza. Incredere scazuta spre medie.", routeChanges)
                    : String.Format(CultureInfo.InvariantCulture, "Windows switched the active route interface in {0} sample(s). Check whether the switch coincides with the issue; it does not identify the cause by itself. Low-to-medium confidence.", routeChanges);
            if (gatewayLossWithExternalFailure >= 2)
                return ro ? String.Format(CultureInfo.InvariantCulture, "Legatura locala pana la gateway/router: gateway-ul si toate probele externe au esuat impreuna in {0} esantioane. Incredere medie; routerul poate filtra ICMP.", gatewayLossWithExternalFailure)
                    : String.Format(CultureInfo.InvariantCulture, "Local link to gateway/router: gateway and all external checks failed together in {0} samples. Medium confidence; routers may filter ICMP.", gatewayLossWithExternalFailure);
            if (dnsFailureWithInternet >= 1)
                return ro ? String.Format(CultureInfo.InvariantCulture, "DNS: internetul a raspuns, dar rezolvarea numelor a esuat in {0} verificari. Incredere medie spre ridicata pentru o problema DNS in acele momente; verificarea foloseste resolverul configurat in Windows.", dnsFailureWithInternet)
                    : String.Format(CultureInfo.InvariantCulture, "DNS: Internet checks succeeded while name resolution failed in {0} checks. Medium-to-high confidence of a DNS issue at those times; the test uses the resolver configured in Windows.", dnsFailureWithInternet);
            if (externalOutage >= 2 && gatewayAttempts > 0 && gatewayFailures < gatewayAttempts / 2)
                return ro ? String.Format(CultureInfo.InvariantCulture, "Dupa gateway: gateway-ul a raspuns, dar toate tintele IP si verificarile HTTPS au esuat impreuna in {0} esantioane. Incredere medie; filtrarea, proxy-ul sau o ruta externa pot produce acelasi rezultat.", externalOutage)
                    : String.Format(CultureInfo.InvariantCulture, "Beyond the gateway: the gateway replied, but all IP targets and HTTPS checks failed together in {0} samples. Medium confidence; filtering, a proxy, or an external route can produce the same result.", externalOutage);
            if (destinationDisagreement >= 2)
                return ro ? String.Format(CultureInfo.InvariantCulture, "Unele destinatii externe: raspunsurile tintelor au diferit in {0} esantioane, in timp ce cel putin o tinta a raspuns. Incredere scazuta; filtrarea ICMP sau trasee diferite pot explica diferenta.", destinationDisagreement)
                    : String.Format(CultureInfo.InvariantCulture, "Some external destinations: target responses differed in {0} samples while at least one target replied. Low confidence; ICMP filtering or different routes can explain the difference.", destinationDisagreement);
            if (upstreamLatencyOnly >= 2)
                return ro ? String.Format(CultureInfo.InvariantCulture, "Traseul dincolo de gateway: latența catre mai multe tinte a crescut fara crestere similara catre gateway in {0} esantioane. Incredere medie; nu identifica furnizorul sau serverul responsabil.", upstreamLatencyOnly)
                    : String.Format(CultureInfo.InvariantCulture, "Path beyond the gateway: latency to external targets rose without a similar gateway increase in {0} samples. Medium confidence; this does not identify the responsible ISP or server.", upstreamLatencyOnly);
            if (weakSignal >= 3)
                return ro ? String.Format(CultureInfo.InvariantCulture, "Legatura Wi-Fi merita verificata: semnal sub 35%% in {0} esantioane. Incredere scazuta spre medie; procentul nu masoara interferentele.", weakSignal)
                    : String.Format(CultureInfo.InvariantCulture, "The Wi-Fi link merits checking: signal was below 35%% in {0} samples. Low-to-medium confidence; this percentage does not measure interference.", weakSignal);
            if (samples.Count < 6)
                return ro ? "Date insuficiente: sunt necesare mai multe esantioane pentru a localiza o problema intermitenta." : "Insufficient data: more samples are needed to localize an intermittent issue.";
            return ro ? "Nu s-a localizat o degradare sustinuta in probele disponibile. Asta nu exclude o problema care a aparut in afara sesiunii." : "No sustained degradation was localized in the available checks. This does not rule out a problem outside the session.";
        }

        private static bool IsGatewayAttempt(Sample sample)
        {
            return sample.GatewayPingStatus != "Unavailable" && sample.GatewayPingStatus != "NoGateway" && sample.GatewayPingStatus != "NotChecked";
        }

        private static bool IsGatewayFailure(Sample sample)
        {
            return IsGatewayAttempt(sample) && sample.GatewayPingStatus != "Success";
        }

        private static int ExternalSuccesses(Sample sample)
        {
            if (sample.InternetTargetResults != null && sample.InternetTargetResults.Count > 0)
                return sample.InternetTargetResults.Count(delegate(InternetTargetObservation result) { return result != null && result.Status == "Success"; });
            return sample.InternetPingStatus == "Success" ? 1 : 0;
        }

        private static int ExternalAttempts(Sample sample)
        {
            return sample.InternetTargetResults != null && sample.InternetTargetResults.Count > 0 ? sample.InternetTargetResults.Count :
                (sample.InternetPingStatus == "Unavailable" || sample.InternetPingStatus == "NotChecked" ? 0 : 1);
        }

        private static string FormatLoadComparison(IList<Sample> samples, AppLanguage language)
        {
            LoadLatencyComparison comparison = LoadLatencyAnalysis.Compare(samples);
            bool ro = language == AppLanguage.Romanian;
            if (!comparison.QuietMedianMs.HasValue || !comparison.ActiveMedianMs.HasValue)
                return ro ? "Comparatie sub activitate de retea: nu sunt suficiente esantioane in perioade linistite si active. Aplicatia nu genereaza trafic; pentru comparatie, las-o pornita in timp ce folosesti normal conexiunea."
                    : "Latency during network activity: not enough samples from both quiet and active periods. The app generates no traffic; leave it running while using the connection normally to compare periods.";
            double delta = comparison.ActiveMedianMs.Value - comparison.QuietMedianMs.Value;
            string localComparison = comparison.GatewayQuietMedianMs.HasValue && comparison.GatewayActiveMedianMs.HasValue
                ? String.Format(CultureInfo.InvariantCulture, (ro ? " Gateway median: {0:0.0} ms linistit, {1:0.0} ms activ." : " Gateway median: {0:0.0} ms quiet, {1:0.0} ms active."), comparison.GatewayQuietMedianMs.Value, comparison.GatewayActiveMedianMs.Value)
                : "";
            return ro
                ? String.Format(CultureInfo.InvariantCulture, "Comparatie observata (nu test controlat): mediana Internet a fost {0:0.0} ms linistit ({1} probe) si {2:0.0} ms cand traficul acestui PC a depasit 1 Mbps ({3} probe), diferenta {4:+0.0;-0.0;0.0} ms.{5} Corelatia nu dovedeste bufferbloat si nu include traficul altor dispozitive.", comparison.QuietMedianMs.Value, comparison.QuietSamples, comparison.ActiveMedianMs.Value, comparison.ActiveSamples, delta, localComparison)
                : String.Format(CultureInfo.InvariantCulture, "Observed comparison (not a controlled test): Internet median was {0:0.0} ms during quiet periods ({1} samples) and {2:0.0} ms while this PC exceeded 1 Mbps traffic ({3} samples), a {4:+0.0;-0.0;0.0} ms difference.{5} Correlation does not prove bufferbloat and excludes traffic from other devices.", comparison.QuietMedianMs.Value, comparison.QuietSamples, comparison.ActiveMedianMs.Value, comparison.ActiveSamples, delta, localComparison);
        }

        private static double? Average(IEnumerable<double> values)
        {
            List<double> list = values.ToList();
            if (list.Count == 0) return null;
            return list.Average();
        }

        private static string FormatLatencyStatistics(IEnumerable<double?> values)
        {
            LatencyStatistics stats = LatencyStatistics.Calculate(values);
            if (stats.Count == 0) return "n/a (no successful samples)";
            return String.Format(CultureInfo.InvariantCulture, "min {0:0.0}, median {1:0.0}, p95 {2:0.0}, max {3:0.0} ms, jitter {4}",
                stats.Minimum.Value, stats.Median.Value, stats.P95.Value, stats.Maximum.Value,
                stats.Jitter.HasValue ? stats.Jitter.Value.ToString("0.0", CultureInfo.InvariantCulture) + " ms" : "n/a");
        }

        private static string FormatInterfaceStats(IList<Sample> samples, AppLanguage language)
        {
            string[] types = new string[] { "Wi-Fi", "Ethernet", "Other", "Unavailable" };
            return String.Join(", ", types.Select(delegate(string type)
            {
                int count = samples.Count(delegate(Sample sample) { return String.Equals(sample.ActiveInterfaceType ?? "Unavailable", type, StringComparison.OrdinalIgnoreCase); });
                return type + " " + count.ToString(CultureInfo.InvariantCulture);
            }).ToArray());
        }

        private static string FormatInternetTargetStats(IList<Sample> samples)
        {
            string[] names = new string[] { "Cloudflare", "Google", "Quad9" };
            List<string> formatted = new List<string>();
            foreach (string name in names)
            {
                int attempted = 0;
                int succeeded = 0;
                List<double?> latency = new List<double?>();
                foreach (Sample sample in samples)
                {
                    if (sample.InternetTargetResults == null) continue;
                    foreach (InternetTargetObservation result in sample.InternetTargetResults.Where(delegate(InternetTargetObservation item) { return item != null && item.Name == name; }))
                    {
                        attempted++;
                        if (result.Status == "Success")
                        {
                            succeeded++;
                            latency.Add(result.LatencyMs);
                        }
                    }
                }
                LatencyStatistics stats = LatencyStatistics.Calculate(latency);
                formatted.Add(name + " " + succeeded.ToString(CultureInfo.InvariantCulture) + "/" + attempted.ToString(CultureInfo.InvariantCulture) +
                    (stats.Median.HasValue ? ", median " + stats.Median.Value.ToString("0.0", CultureInfo.InvariantCulture) + " ms" : ""));
            }
            return String.Join("; ", formatted.ToArray());
        }

        private static string FormatEndpointStats(IList<Sample> samples, bool https)
        {
            string[] hosts = https
                ? new string[] { "www.msftconnecttest.com", "www.google.com", "cp.cloudflare.com" }
                : new string[] { "www.microsoft.com", "www.google.com", "www.cloudflare.com" };
            List<string> output = new List<string>();
            foreach (string host in hosts)
            {
                int attempted = 0;
                int successful = 0;
                foreach (Sample sample in samples)
                {
                    string details = https ? sample.HttpsEndpointResults : sample.DnsEndpointResults;
                    if (String.IsNullOrEmpty(details) || details == "NotNeeded" || details == "NotChecked") continue;
                    foreach (string item in details.Split(';'))
                    {
                        int equals = item.IndexOf('=');
                        if (equals < 0 || !String.Equals(item.Substring(0, equals), host, StringComparison.OrdinalIgnoreCase)) continue;
                        attempted++;
                        string result = item.Substring(equals + 1);
                        if (result.StartsWith("Success", StringComparison.Ordinal)) successful++;
                    }
                }
                output.Add(host + " " + successful.ToString(CultureInfo.InvariantCulture) + "/" + attempted.ToString(CultureInfo.InvariantCulture));
            }
            return String.Join(", ", output.ToArray()) + " (success/attempts)";
        }

        private static double? AverageIntervalSeconds(IList<Sample> samples)
        {
            if (samples == null || samples.Count < 2) return null;
            double total = 0;
            for (int i = 1; i < samples.Count; i++)
                total += (samples[i].Timestamp - samples[i - 1].Timestamp).TotalSeconds;
            return total / (samples.Count - 1);
        }

        private static string Format(double? value, string suffix)
        {
            return value.HasValue ? value.Value.ToString("0.0", CultureInfo.InvariantCulture) + suffix : "n/a";
        }
    }

    internal static class CsvLogger
    {
        private const long MaxBytes = 10L * 1024L * 1024L;
        private const int RetainedRotatedFiles = 10;
        private const string CsvHeader = "TimestampLocal,WifiState,WifiSignalPercent,ActiveInterfaceType,GatewayPingStatus,GatewayLatencyMs,GatewayIcmpLossPctRolling60s,InternetPingStatus,InternetLatencyMs,InternetIcmpLossPctRolling60s,InternetTargets,InternetTargetsSucceeded,InternetTargetsAttempted,HttpsFallbackStatus,HttpsFallbackLatencyMs,HttpsEndpoints,HttpsSucceeded,HttpsAttempted,DnsStatus,DnsLatencyMs,DnsEndpoints,DnsSucceeded,DnsAttempted,Connectivity,Events,NetworkDownloadMbps,NetworkUploadMbps";
        private static readonly object Gate = new object();
        public static readonly string LogDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetworkMonitor", "Logs");
        public static readonly string CsvPath = Path.Combine(LogDirectory, "connection-monitor-app.csv");
        public static readonly string ReportPath = Path.Combine(LogDirectory, "diagnostic-app-latest.txt");

        public static void Write(Sample sample)
        {
            lock (Gate)
            {
                Directory.CreateDirectory(LogDirectory);
                RotateIfNeeded();
                EnsureCsvSchema();
                if (!File.Exists(CsvPath))
                    File.WriteAllText(CsvPath, CsvHeader + "\r\n", Encoding.UTF8);
                string[] cells = new string[]
                {
                    sample.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture),
                    sample.WifiState,
                    sample.WifiSignalPercent.HasValue ? sample.WifiSignalPercent.Value.ToString(CultureInfo.InvariantCulture) : "",
                    sample.ActiveInterfaceType,
                    sample.GatewayPingStatus,
                    Number(sample.GatewayLatencyMs),
                    Number(sample.GatewayLossPct),
                    sample.InternetPingStatus,
                    Number(sample.InternetLatencyMs),
                    Number(sample.InternetLossPct),
                    NetworkProbe.FormatInternetTargets(sample.InternetTargetResults, AppLanguage.English),
                    sample.InternetTargetSucceeded.HasValue ? sample.InternetTargetSucceeded.Value.ToString(CultureInfo.InvariantCulture) : "",
                    sample.InternetTargetAttempted.HasValue ? sample.InternetTargetAttempted.Value.ToString(CultureInfo.InvariantCulture) : "",
                    sample.HttpsStatus,
                    Number(sample.HttpsLatencyMs),
                    sample.HttpsEndpointResults,
                    sample.HttpsSucceeded.HasValue ? sample.HttpsSucceeded.Value.ToString(CultureInfo.InvariantCulture) : "",
                    sample.HttpsAttempted.HasValue ? sample.HttpsAttempted.Value.ToString(CultureInfo.InvariantCulture) : "",
                    sample.DnsStatus,
                    Number(sample.DnsLatencyMs),
                    sample.DnsEndpointResults,
                    sample.DnsSucceeded.HasValue ? sample.DnsSucceeded.Value.ToString(CultureInfo.InvariantCulture) : "",
                    sample.DnsAttempted.HasValue ? sample.DnsAttempted.Value.ToString(CultureInfo.InvariantCulture) : "",
                    sample.Connectivity,
                    sample.Events,
                    Number(sample.DownloadMbps),
                    Number(sample.UploadMbps)
                };
                File.AppendAllText(CsvPath, String.Join(",", cells.Select(CsvEscape).ToArray()) + "\r\n", Encoding.UTF8);
            }
        }

        public static void WriteReport(string report)
        {
            Directory.CreateDirectory(LogDirectory);
            File.WriteAllText(ReportPath, report, new UTF8Encoding(true));
        }

        private static void RotateIfNeeded()
        {
            if (!File.Exists(CsvPath) || new FileInfo(CsvPath).Length < MaxBytes) return;
            File.Move(CsvPath, GetRotatedPath());
            RetainRotatedFiles();
        }

        internal static bool HeaderMatches(string line) { return String.Equals((line ?? "").TrimStart('\uFEFF'), CsvHeader, StringComparison.Ordinal); }

        private static void EnsureCsvSchema()
        {
            if (!File.Exists(CsvPath)) return;
            string header;
            using (StreamReader reader = new StreamReader(CsvPath, Encoding.UTF8, true)) header = reader.ReadLine();
            if (HeaderMatches(header)) return;
            File.Move(CsvPath, GetRotatedPath());
            RetainRotatedFiles();
        }

        private static string GetRotatedPath()
        {
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
            string rotated = Path.Combine(LogDirectory, "connection-monitor-app-" + stamp + ".csv");
            int index = 1;
            while (File.Exists(rotated)) rotated = Path.Combine(LogDirectory, "connection-monitor-app-" + stamp + "-" + (index++).ToString(CultureInfo.InvariantCulture) + ".csv");
            return rotated;
        }

        private static void RetainRotatedFiles()
        {
            List<FileInfo> older = Directory.GetFiles(LogDirectory, "connection-monitor-app-*.csv")
                .Select(delegate(string path) { return new FileInfo(path); })
                .OrderByDescending(delegate(FileInfo file) { return file.LastWriteTimeUtc; })
                .Skip(RetainedRotatedFiles).ToList();
            foreach (FileInfo file in older)
            {
                try { file.Delete(); } catch { }
            }
        }

        private static string Number(double? value)
        {
            return value.HasValue ? value.Value.ToString("0.0", CultureInfo.InvariantCulture) : "";
        }

        private static string CsvEscape(string value)
        {
            if (value == null) return "";
            if (value.IndexOfAny(new char[] { ',', '"', '\r', '\n' }) < 0) return value;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
    }

    internal sealed class MainForm : Form
    {
        private readonly Button startStopButton;
        private readonly Button openLogsButton;
        private readonly Button copyReportButton;
        private readonly Button routeTraceButton;
        private readonly Button aiButton;
        private readonly Button disconnectAiButton;
        private readonly Button aiSettingsButton;
        private readonly Label statusLabel;
        private readonly Label wifiLabel;
        private readonly Label signalLabel;
        private readonly Label gatewayLabel;
        private readonly Label internetLabel;
        private readonly Label dnsLabel;
        private readonly Label downloadLabel;
        private readonly Label uploadLabel;
        private readonly DataGridView grid;
        private readonly LatencyChartPanel latencyChart;
        private readonly TextBox diagnosisBox;
        private readonly ComboBox languageSelector;
        private readonly Label headingLabel;
        private readonly Label footerLabel;
        private readonly Label aiStatusLabel;
        private readonly GroupBox diagnosisGroup;
        private AppLanguage language = AppLanguage.English;
        private readonly object samplesGate = new object();
        private readonly List<Sample> samples = new List<Sample>();
        private System.Threading.Timer timer;
        private int sampling;
        private volatile bool monitoring;
        private DateTimeOffset nextDnsCheck;

        public MainForm()
        {
            try { this.Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch { }
            Text = Localization.Get(language, "AppTitle");
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(1000, 650);
            ClientSize = new Size(1120, 760);
            Font = new Font("Segoe UI", 9F);
            BackColor = Color.FromArgb(245, 247, 250);

            Panel header = new Panel();
            header.Dock = DockStyle.Top;
            header.Height = 126;
            header.BackColor = Color.White;
            Controls.Add(header);

            headingLabel = new Label();
            headingLabel.Text = Localization.Get(language, "Heading");
            headingLabel.Font = new Font("Segoe UI Semibold", 16F, FontStyle.Bold);
            headingLabel.AutoSize = true;
            headingLabel.Location = new Point(18, 12);
            header.Controls.Add(headingLabel);

            languageSelector = new ComboBox();
            languageSelector.DropDownStyle = ComboBoxStyle.DropDownList;
            languageSelector.Items.Add(Localization.Get(AppLanguage.English, "English"));
            languageSelector.Items.Add(Localization.Get(AppLanguage.Romanian, "Romanian"));
            languageSelector.SelectedIndex = 0;
            languageSelector.Size = new Size(112, 28);
            languageSelector.Location = new Point(960, 58);
            languageSelector.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            languageSelector.SelectedIndexChanged += LanguageChanged;
            header.Controls.Add(languageSelector);

            aiButton = new Button();
            aiButton.Text = Localization.Get(language, "ContinueChatGPT");
            aiButton.Size = new Size(190, 32);
            aiButton.Location = new Point(590, 58);
            aiButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            aiButton.Click += AiButtonClicked;
            header.Controls.Add(aiButton);

            aiSettingsButton = new Button();
            aiSettingsButton.Text = Localization.Get(language, "AISettings");
            aiSettingsButton.Size = new Size(150, 32);
            aiSettingsButton.Location = new Point(640, 14);
            aiSettingsButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            aiSettingsButton.Click += AiSettingsClicked;
            header.Controls.Add(aiSettingsButton);

            disconnectAiButton = new Button();
            disconnectAiButton.Text = Localization.Get(language, "DisconnectChatGPT");
            disconnectAiButton.Size = new Size(130, 32);
            disconnectAiButton.Location = new Point(790, 58);
            disconnectAiButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            disconnectAiButton.Click += DisconnectAiClicked;
            header.Controls.Add(disconnectAiButton);

            startStopButton = new Button();
            startStopButton.Text = Localization.Get(language, "Start");
            startStopButton.Size = new Size(190, 36);
            startStopButton.Location = new Point(20, 56);
            startStopButton.BackColor = Color.FromArgb(23, 112, 214);
            startStopButton.ForeColor = Color.White;
            startStopButton.FlatStyle = FlatStyle.Flat;
            startStopButton.Click += StartStopClicked;
            header.Controls.Add(startStopButton);

            statusLabel = new Label();
            statusLabel.Text = Localization.Get(language, "Stopped");
            statusLabel.AutoSize = false;
            statusLabel.Size = new Size(150, 22);
            statusLabel.Location = new Point(230, 66);
            statusLabel.Font = new Font("Segoe UI Semibold", 10F, FontStyle.Bold);
            statusLabel.AutoEllipsis = true;
            header.Controls.Add(statusLabel);

            aiStatusLabel = new Label();
            aiStatusLabel.Text = Localization.Get(language, CurrentAiStatusKey());
            aiStatusLabel.AutoSize = false;
            aiStatusLabel.Size = new Size(185, 22);
            aiStatusLabel.Location = new Point(390, 68);
            aiStatusLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            aiStatusLabel.Font = new Font("Segoe UI", 8F);
            aiStatusLabel.AutoEllipsis = true;
            header.Controls.Add(aiStatusLabel);

            openLogsButton = new Button();
            openLogsButton.Text = Localization.Get(language, "OpenLogs");
            openLogsButton.Size = new Size(130, 32);
            openLogsButton.Location = new Point(960, 14);
            openLogsButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            openLogsButton.Click += OpenLogsClicked;
            header.Controls.Add(openLogsButton);

            copyReportButton = new Button();
            copyReportButton.Text = Localization.Get(language, "CopyReport");
            copyReportButton.Size = new Size(150, 32);
            copyReportButton.Location = new Point(800, 14);
            copyReportButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            copyReportButton.Click += CopyReportClicked;
            header.Controls.Add(copyReportButton);

            routeTraceButton = new Button();
            routeTraceButton.Text = Localization.Get(language, "RouteTraceButton");
            routeTraceButton.Size = new Size(160, 32);
            routeTraceButton.Location = new Point(460, 14);
            routeTraceButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            routeTraceButton.Click += RouteTraceClicked;
            header.Controls.Add(routeTraceButton);

            FlowLayoutPanel metrics = new FlowLayoutPanel();
            metrics.Location = new Point(20, 100);
            metrics.Size = new Size(1000, 24);
            metrics.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
            metrics.WrapContents = false;
            header.Controls.Add(metrics);

            wifiLabel = MakeMetric("Wi-Fi: n/a", metrics);
            signalLabel = MakeMetric(Localization.Get(language, "MetricSignal") + ": n/a", metrics);
            gatewayLabel = MakeMetric("Gateway: n/a", metrics);
            internetLabel = MakeMetric("Internet: n/a", metrics);
            dnsLabel = MakeMetric("DNS: n/a", metrics);
            downloadLabel = MakeMetric(Localization.Get(language, "MetricDownload") + ": n/a", metrics);
            uploadLabel = MakeMetric(Localization.Get(language, "MetricUpload") + ": n/a", metrics);

            SplitContainer split = new SplitContainer();
            split.Dock = DockStyle.Fill;
            split.Orientation = Orientation.Horizontal;
            split.SplitterDistance = 390;
            split.Panel1MinSize = 240;
            split.Panel2MinSize = 140;
            Controls.Add(split);
            split.BringToFront();

            grid = new DataGridView();
            grid.Dock = DockStyle.Fill;
            grid.ReadOnly = true;
            grid.AllowUserToAddRows = false;
            grid.AllowUserToDeleteRows = false;
            grid.AllowUserToResizeRows = false;
            grid.RowHeadersVisible = false;
            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            grid.BackgroundColor = Color.White;
            grid.Columns.Add("time", Localization.Get(language, "Time"));
            grid.Columns.Add("wifi", "Wi-Fi");
            grid.Columns.Add("signal", Localization.Get(language, "GridSignal"));
            grid.Columns.Add("gateway", "Gateway");
            grid.Columns.Add("internet", "Internet");
            grid.Columns.Add("targets", Localization.Get(language, "GridTargets"));
            grid.Columns.Add("dns", "DNS");
            grid.Columns.Add("state", Localization.Get(language, "State"));
            grid.Columns.Add("events", Localization.Get(language, "Events"));
            grid.Columns[0].FillWeight = 125;
            grid.Columns[8].FillWeight = 180;
            split.Panel1.Controls.Add(grid);

            latencyChart = new LatencyChartPanel();
            latencyChart.Dock = DockStyle.Top;
            latencyChart.Height = 150;
            latencyChart.Language = language;
            split.Panel1.Controls.Add(latencyChart);

            diagnosisGroup = new GroupBox();
            diagnosisGroup.Text = Localization.Get(language, "DiagnosisGroup");
            diagnosisGroup.Dock = DockStyle.Fill;
            diagnosisGroup.Padding = new Padding(8);
            split.Panel2.Controls.Add(diagnosisGroup);

            diagnosisBox = new TextBox();
            diagnosisBox.Multiline = true;
            diagnosisBox.ReadOnly = true;
            diagnosisBox.ScrollBars = ScrollBars.Vertical;
            diagnosisBox.Dock = DockStyle.Fill;
            diagnosisBox.Font = new Font("Consolas", 9F);
            diagnosisBox.Text = Localization.Get(language, "InitialDiagnosis") + Environment.NewLine + Localization.Get(language, "AIDataNotice");
            diagnosisGroup.Controls.Add(diagnosisBox);

            footerLabel = new Label();
            footerLabel.Text = Localization.Get(language, "Footer");
            footerLabel.Dock = DockStyle.Bottom;
            footerLabel.Height = 24;
            footerLabel.TextAlign = ContentAlignment.MiddleLeft;
            footerLabel.Padding = new Padding(10, 0, 0, 0);
            Controls.Add(footerLabel);
            footerLabel.BringToFront();

            FormClosing += MainFormClosing;
            ApplyLanguage();
        }

        private void LanguageChanged(object sender, EventArgs e)
        {
            language = languageSelector.SelectedIndex == 1 ? AppLanguage.Romanian : AppLanguage.English;
            ApplyLanguage();
        }

        private void ApplyLanguage()
        {
            Text = Localization.Get(language, "AppTitle");
            headingLabel.Text = Localization.Get(language, "Heading");
            startStopButton.Text = Localization.Get(language, monitoring ? "Stop" : (samples.Count > 0 ? "Restart" : "Start"));
            openLogsButton.Text = Localization.Get(language, "OpenLogs");
            copyReportButton.Text = Localization.Get(language, "CopyReport");
            routeTraceButton.Text = Localization.Get(language, "RouteTraceButton");
            aiButton.Text = Localization.Get(language, "ContinueChatGPT");
            aiSettingsButton.Text = Localization.Get(language, "AISettings");
            disconnectAiButton.Text = Localization.Get(language, "DisconnectChatGPT");
            aiStatusLabel.Text = Localization.Get(language, CurrentAiStatusKey());
            disconnectAiButton.Enabled = ChatGptPlanClient.IsConnected;
            statusLabel.Text = Localization.Get(language, monitoring ? "Running" : "Stopped");
            wifiLabel.Text = Localization.Get(language, "MetricWifi") + ": " + Localization.Get(language, "NoValue");
            signalLabel.Text = Localization.Get(language, "MetricSignal") + ": " + Localization.Get(language, "NoValue");
            gatewayLabel.Text = Localization.Get(language, "MetricGateway") + ": " + Localization.Get(language, "NoValue");
            internetLabel.Text = Localization.Get(language, "MetricInternet") + ": " + Localization.Get(language, "NoValue");
            dnsLabel.Text = Localization.Get(language, "MetricDns") + ": " + Localization.Get(language, "NoValue");
            downloadLabel.Text = Localization.Get(language, "MetricDownload") + ": " + Localization.Get(language, "NoValue");
            uploadLabel.Text = Localization.Get(language, "MetricUpload") + ": " + Localization.Get(language, "NoValue");
            diagnosisGroup.Text = Localization.Get(language, "DiagnosisGroup");
            footerLabel.Text = Localization.Get(language, "Footer");
            grid.Columns[0].HeaderText = Localization.Get(language, "Time");
            grid.Columns[1].HeaderText = Localization.Get(language, "GridWiFi");
            grid.Columns[2].HeaderText = Localization.Get(language, "GridSignal");
            grid.Columns[3].HeaderText = Localization.Get(language, "GridGateway");
            grid.Columns[4].HeaderText = Localization.Get(language, "GridInternet");
            grid.Columns[5].HeaderText = Localization.Get(language, "GridTargets");
            grid.Columns[6].HeaderText = Localization.Get(language, "GridDNS");
            grid.Columns[7].HeaderText = Localization.Get(language, "State");
            grid.Columns[8].HeaderText = Localization.Get(language, "Events");
            if (samples.Count == 0) diagnosisBox.Text = Localization.Get(language, "InitialDiagnosis") + Environment.NewLine + Localization.Get(language, "AIDataNotice");
            else if (!monitoring)
            {
                List<Sample> copy;
                lock (samplesGate) copy = new List<Sample>(samples);
                diagnosisBox.Text = DiagnosticEngine.Analyze(copy, language) + Environment.NewLine +
                    (language == AppLanguage.Romanian ? "Raport salvat local: " : "Report saved locally: ") + CsvLogger.ReportPath;
            }
        }

        private static Label MakeMetric(string text, Control parent)
        {
            Label label = new Label();
            label.Text = text;
            label.AutoSize = true;
            label.Margin = new Padding(0, 0, 26, 0);
            parent.Controls.Add(label);
            return label;
        }

        private void StartStopClicked(object sender, EventArgs e)
        {
            if (!monitoring)
                StartMonitoring();
            else
                StopMonitoring();
        }

        private void AiButtonClicked(object sender, EventArgs e)
        {
            DialogResult consent = MessageBox.Show(this, Localization.Get(language, "AIConsentPrompt"),
                Localization.Get(language, "AIConsentTitle"), MessageBoxButtons.YesNo, MessageBoxIcon.Information);
            if (consent != DialogResult.Yes) return;
            aiButton.Enabled = false;
            disconnectAiButton.Enabled = false;
            aiStatusLabel.Text = Localization.Get(language, "AISigningIn");
            ThreadPool.QueueUserWorkItem(delegate
            {
                string message = null;
                bool success = false;
                try { message = ChatGptPlanClient.Connect(); success = true; }
                catch (Exception exception) { message = exception.Message; }
                SafeBeginInvoke(delegate
                {
                    aiButton.Enabled = true;
                    disconnectAiButton.Enabled = ChatGptPlanClient.IsConnected;
                    aiStatusLabel.Text = Localization.Get(language, CurrentAiStatusKey());
                    if (success) MessageBox.Show(this, message, Localization.Get(language, "AIConnectedStatus"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                    else MessageBox.Show(this, message, Localization.Get(language, "AIUnavailable"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                });
            });
        }

        private void AiSettingsClicked(object sender, EventArgs e)
        {
            using (AiProviderSettingsForm dialog = new AiProviderSettingsForm(language))
            {
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    aiStatusLabel.Text = Localization.Get(language, CurrentAiStatusKey());
                }
            }
        }

        private string CurrentAiStatusKey()
        {
            if (AiProviderClient.IsConfigured) return "AIProviderReady";
            return ChatGptPlanClient.IsConnected ? "AIConnectedStatus" : "AINotConnectedStatus";
        }

        private void DisconnectAiClicked(object sender, EventArgs e)
        {
            aiButton.Enabled = false;
            disconnectAiButton.Enabled = false;
            aiStatusLabel.Text = Localization.Get(language, "Stopping");
            ThreadPool.QueueUserWorkItem(delegate
            {
                string message;
                try { message = ChatGptPlanClient.Disconnect(); }
                catch { message = Localization.Get(language, "AIRevocationNote"); }
                SafeBeginInvoke(delegate
                {
                    aiButton.Enabled = true;
                    aiStatusLabel.Text = Localization.Get(language, CurrentAiStatusKey());
                    MessageBox.Show(this, message, Localization.Get(language, "DisconnectChatGPT"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                });
            });
        }

        private void StartMonitoring()
        {
            lock (samplesGate) samples.Clear();
            grid.Rows.Clear();
            latencyChart.SetSamples(new List<Sample>());
            diagnosisBox.Clear();
            nextDnsCheck = DateTimeOffset.MinValue;
            monitoring = true;
            startStopButton.Text = Localization.Get(language, "Stop");
            startStopButton.BackColor = Color.FromArgb(190, 55, 45);
            statusLabel.Text = Localization.Get(language, "Running");
            timer = new System.Threading.Timer(SampleTick, null, 0, 5000);
        }

        private void StopMonitoring()
        {
            monitoring = false;
            if (timer != null)
            {
                timer.Dispose();
                timer = null;
            }
            startStopButton.Enabled = false;
            statusLabel.Text = Localization.Get(language, "Stopping");

            ThreadPool.QueueUserWorkItem(delegate
            {
                int wait = 0;
                while (Interlocked.CompareExchange(ref sampling, 0, 0) != 0 && wait < 12000)
                {
                    Thread.Sleep(100);
                    wait += 100;
                }

                List<Sample> copy;
                lock (samplesGate) copy = new List<Sample>(samples);
                AppLanguage reportLanguage = language;
                string report = DiagnosticEngine.Analyze(copy, reportLanguage);
                AiProviderSettings alternativeProvider = AiProviderClient.Load();
                bool explainWithAlternative = alternativeProvider.Provider != AiProvider.None;
                bool explainWithChatGpt = !explainWithAlternative && ChatGptPlanClient.IsConnected;
                if (explainWithChatGpt || explainWithAlternative)
                {
                    SafeBeginInvoke(delegate { aiStatusLabel.Text = Localization.Get(reportLanguage, "AIAnalyzingStatus"); });
                    try
                    {
                        string explanation = explainWithChatGpt ? ChatGptPlanClient.Explain(copy, reportLanguage) : AiProviderClient.Explain(copy, reportLanguage);
                        string providerName = explainWithChatGpt ? "ChatGPT" : alternativeProvider.Provider.ToString();
                        report += Environment.NewLine + Localization.Format(reportLanguage, "AIExplanationHeader") + " (" + providerName + ")" + Environment.NewLine + explanation;
                    }
                    catch (Exception exception)
                    {
                        report += Environment.NewLine + Localization.Get(reportLanguage, "AIFailedPrefix") + exception.Message;
                    }
                }
                else
                    report += Environment.NewLine + Localization.Get(reportLanguage, "AIDataNotice");
                try { CsvLogger.WriteReport(report); } catch { }

                SafeBeginInvoke(delegate
                {
                    diagnosisBox.Text = report + Environment.NewLine + (reportLanguage == AppLanguage.Romanian ? "Raport salvat local: " : "Report saved locally: ") + CsvLogger.ReportPath;
                    statusLabel.Text = String.Format(reportLanguage == AppLanguage.Romanian ? CultureInfo.GetCultureInfo("ro-RO") : CultureInfo.GetCultureInfo("en-US"), reportLanguage == AppLanguage.Romanian ? "Oprit - {0} esantioane analizate" : "Stopped - analyzed {0} samples", copy.Count);
                    startStopButton.Text = Localization.Get(language, "Restart");
                    startStopButton.BackColor = Color.FromArgb(23, 112, 214);
                    startStopButton.Enabled = true;
                    aiStatusLabel.Text = Localization.Get(language, CurrentAiStatusKey());
                });
            });
        }

        private void SampleTick(object state)
        {
            if (!monitoring || Interlocked.Exchange(ref sampling, 1) != 0) return;
            Sample sample = null;
            try
            {
                DateTimeOffset now = DateTimeOffset.Now;
                bool checkDns = now >= nextDnsCheck;
                if (checkDns) nextDnsCheck = now.AddSeconds(60);

                List<Sample> previous;
                lock (samplesGate) previous = new List<Sample>(samples);
                sample = NetworkProbe.Capture(previous, now, checkDns);
                lock (samplesGate) samples.Add(sample);
                CsvLogger.Write(sample);
            }
            catch
            {
                sample = new Sample();
                sample.Timestamp = DateTimeOffset.Now;
                sample.WifiState = "Unavailable";
                sample.GatewayPingStatus = "Unavailable";
                sample.InternetPingStatus = "Unavailable";
                sample.HttpsStatus = "Unavailable";
                sample.DnsStatus = "Unavailable";
                sample.Connectivity = "Degraded";
                sample.Events = "ProbeError";
                lock (samplesGate) samples.Add(sample);
                try { CsvLogger.Write(sample); } catch { }
            }
            finally
            {
                Interlocked.Exchange(ref sampling, 0);
            }

            if (sample != null && monitoring)
            {
                Sample captured = sample;
                SafeBeginInvoke(delegate { AddSampleToUi(captured); });
            }
        }

        private void AddSampleToUi(Sample sample)
        {
            wifiLabel.Text = Localization.Get(language, "MetricWifi") + ": " + Localization.Status(language, sample.WifiState);
            signalLabel.Text = Localization.Get(language, "MetricSignal") + ": " + (sample.WifiSignalPercent.HasValue ? sample.WifiSignalPercent.Value.ToString(CultureInfo.InvariantCulture) + "%" : Localization.Get(language, "NoValue"));
            gatewayLabel.Text = Localization.Get(language, "MetricGateway") + ": " + FormatProbe(sample.GatewayLatencyMs, sample.GatewayPingStatus, language);
            internetLabel.Text = Localization.Get(language, "MetricInternet") + ": " + FormatInternetProbe(sample, language);
            dnsLabel.Text = Localization.Get(language, "MetricDns") + ": " + Localization.Status(language, sample.DnsStatus);
            downloadLabel.Text = Localization.Get(language, "MetricDownload") + ": " + (sample.DownloadMbps.HasValue ? Format(sample.DownloadMbps) + " Mbps" : Localization.Get(language, "NoValue"));
            uploadLabel.Text = Localization.Get(language, "MetricUpload") + ": " + (sample.UploadMbps.HasValue ? Format(sample.UploadMbps) + " Mbps" : Localization.Get(language, "NoValue"));
            statusLabel.Text = Localization.Get(language, "Running") + " - " + (language == AppLanguage.Romanian ? "ultima proba " : "latest probe ") + sample.Timestamp.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

            string gateway = FormatProbe(sample.GatewayLatencyMs, sample.GatewayPingStatus, language);
            string internet = FormatInternetProbe(sample, language);
            string signal = sample.WifiSignalPercent.HasValue ? sample.WifiSignalPercent.Value.ToString(CultureInfo.InvariantCulture) + "%" : Localization.Get(language, "NoValue");
            grid.Rows.Insert(0, sample.Timestamp.ToString("HH:mm:ss", CultureInfo.InvariantCulture), Localization.Status(language, sample.WifiState), signal,
                gateway + " / loss " + Format(sample.GatewayLossPct) + "%", internet + " / loss " + Format(sample.InternetLossPct) + "%",
                NetworkProbe.FormatInternetTargets(sample.InternetTargetResults, language), Localization.Status(language, sample.DnsStatus), Localization.Status(language, sample.Connectivity), String.Join("; ", sample.Events.Split(new char[] { ';' }, StringSplitOptions.RemoveEmptyEntries).Select(delegate(string code) { return Localization.Event(language, code); }).ToArray()));
            if (grid.Rows.Count > 1000) grid.Rows.RemoveAt(grid.Rows.Count - 1);
            List<Sample> chartSamples;
            lock (samplesGate) chartSamples = new List<Sample>(samples);
            latencyChart.SetSamples(chartSamples);
            if (sample.Events.Length > 0)
                diagnosisBox.Text = (language == AppLanguage.Romanian ? "Eveniment observat: " : "Event observed: ") + String.Join("; ", sample.Events.Split(new char[] { ';' }, StringSplitOptions.RemoveEmptyEntries).Select(delegate(string code) { return Localization.Event(language, code); }).ToArray()) + Environment.NewLine + (language == AppLanguage.Romanian ? "Lasa monitorul pornit pana trece problema; diagnosticul complet apare la oprire." : "Keep monitoring until the issue passes; the full diagnosis appears when you stop.");
        }

        private static string FormatProbe(double? latency, string status, AppLanguage language)
        {
            return latency.HasValue ? latency.Value.ToString("0.0", CultureInfo.InvariantCulture) + " ms" : Localization.Status(language, status);
        }

        private static string FormatInternetProbe(Sample sample, AppLanguage language)
        {
            if (sample.InternetTargetSucceeded.HasValue && sample.InternetTargetAttempted.HasValue)
                return sample.InternetTargetSucceeded.Value.ToString(CultureInfo.InvariantCulture) + "/" + sample.InternetTargetAttempted.Value.ToString(CultureInfo.InvariantCulture) +
                    " targets; median " + FormatProbe(sample.InternetLatencyMs, sample.InternetPingStatus, language);
            return FormatProbe(sample.InternetLatencyMs, sample.InternetPingStatus, language);
        }

        private static string Format(double? value)
        {
            return value.HasValue ? value.Value.ToString("0.0", CultureInfo.InvariantCulture) : "n/a";
        }

        private void OpenLogsClicked(object sender, EventArgs e)
        {
            try
            {
                Directory.CreateDirectory(CsvLogger.LogDirectory);
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + CsvLogger.LogDirectory + "\"") { UseShellExecute = true });
            }
            catch (Exception exception)
            {
                MessageBox.Show("Nu pot deschide folderul logurilor: " + exception.Message, "Monitor retea", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void RouteTraceClicked(object sender, EventArgs e)
        {
            DialogResult confirmation = MessageBox.Show(this, Localization.Get(language, "RouteTraceConfirmPrompt"),
                Localization.Get(language, "RouteTraceConfirmTitle"), MessageBoxButtons.YesNo, MessageBoxIcon.Information);
            if (confirmation != DialogResult.Yes) return;
            routeTraceButton.Enabled = false;
            statusLabel.Text = Localization.Get(language, "RouteTraceRunning");
            ThreadPool.QueueUserWorkItem(delegate
            {
                string result;
                try { result = RouteTrace.Run(); }
                catch (Exception exception) { result = exception.Message; }
                SafeBeginInvoke(delegate
                {
                    routeTraceButton.Enabled = true;
                    statusLabel.Text = Localization.Get(language, monitoring ? "Running" : "Stopped");
                    ShowRouteTraceResult(result);
                });
            });
        }

        private void ShowRouteTraceResult(string result)
        {
            using (Form dialog = new Form())
            {
                dialog.Text = Localization.Get(language, "RouteTraceButton") + " - " + RouteTrace.Target;
                dialog.StartPosition = FormStartPosition.CenterParent;
                dialog.Size = new Size(850, 560);
                dialog.MinimumSize = new Size(650, 400);
                Label note = new Label();
                note.Dock = DockStyle.Top;
                note.Height = 48;
                note.Text = language == AppLanguage.Romanian
                    ? "Rezultatul ramane doar pe acest calculator si nu este salvat sau trimis. Asteriscurile inseamna ca un router nu a raspuns; nu dovedesc o defectiune."
                    : "The result stays on this PC and is not saved or sent. Asterisks mean a router did not reply; they do not prove a fault.";
                note.Padding = new Padding(10, 8, 10, 4);
                TextBox output = new TextBox();
                output.Dock = DockStyle.Fill;
                output.Multiline = true;
                output.ReadOnly = true;
                output.ScrollBars = ScrollBars.Both;
                output.WordWrap = false;
                output.Font = new Font("Consolas", 9F);
                output.Text = result;
                Button close = new Button();
                close.Text = language == AppLanguage.Romanian ? "Inchide" : "Close";
                close.DialogResult = DialogResult.OK;
                close.Dock = DockStyle.Bottom;
                close.Height = 34;
                dialog.Controls.Add(output);
                dialog.Controls.Add(close);
                dialog.Controls.Add(note);
                dialog.AcceptButton = close;
                dialog.ShowDialog(this);
            }
        }

        private void CopyReportClicked(object sender, EventArgs e)
        {
            try
            {
                if (!String.IsNullOrWhiteSpace(diagnosisBox.Text))
                {
                    Clipboard.SetText(diagnosisBox.Text);
                    MessageBox.Show("Diagnosticul a fost copiat.", "Monitor retea", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
            catch { }
        }

        private void MainFormClosing(object sender, FormClosingEventArgs e)
        {
            monitoring = false;
            if (timer != null)
            {
                timer.Dispose();
                timer = null;
            }
        }

        private void SafeBeginInvoke(MethodInvoker action)
        {
            try
            {
                if (!IsDisposed && IsHandleCreated) BeginInvoke(action);
            }
            catch { }
        }
    }

    internal static class SelfTests
    {
        public static int Run(string outputPath)
        {
            List<string> results = new List<string>();
            Test(results, "healthy window", HealthyWindow(), "Nu s-au detectat intreruperi");
            Test(results, "wifi disconnect", WindowWith(delegate(Sample s) { s.WifiState = "Disconnected"; }), "Wi-Fi");
            Test(results, "weak signal", WindowWith(delegate(Sample s) { s.WifiSignalPercent = 20; }), "Semnal Wi-Fi");
            Test(results, "gateway loss", WindowWith(delegate(Sample s) { s.GatewayPingStatus = "TimedOut"; s.GatewayLatencyMs = null; }), "pierdere ICMP catre gateway");
            Test(results, "all independent probes fail", WindowWith(delegate(Sample s) { s.InternetPingStatus = "TimedOut"; s.HttpsStatus = "AllFailed"; s.HttpsSucceeded = 0; s.HttpsAttempted = 3; }), "toate cele trei verificari HTTPS independente au esuat");
            Test(results, "one HTTPS endpoint fails but others work", WindowWith(delegate(Sample s) { s.InternetPingStatus = "TimedOut"; s.HttpsStatus = "Success"; s.HttpsSucceeded = 2; s.HttpsAttempted = 3; }), "nu confirma o cadere de Internet");
            Test(results, "dns failure", WindowWith(delegate(Sample s) { s.DnsStatus = "Failed"; }), "DNS");
            Test(results, "all DNS names fail", WindowWith(delegate(Sample s) { s.DnsStatus = "AllFailed"; s.DnsSucceeded = 0; s.DnsAttempted = 3; }), "toate cele trei nume de test");
            Test(results, "partial DNS results", WindowWith(delegate(Sample s) { s.DnsSucceeded = 2; s.DnsAttempted = 3; }), "raspunsuri diferite");
            Test(results, "per endpoint breakdown", WindowWith(delegate(Sample s) { s.HttpsEndpointResults = "www.msftconnecttest.com=Timeout;www.google.com=Success@24ms;cp.cloudflare.com=Failed"; s.HttpsAttempted = 3; s.HttpsSucceeded = 1; }), "www.google.com 6/6");
            Test(results, "icmp filtered but https works", WindowWith(delegate(Sample s) { s.InternetPingStatus = "Unavailable"; s.HttpsStatus = "Success"; }), "nu confirma o cadere de Internet");
            Test(results, "gateway latency spike", WindowWith(delegate(Sample s) { s.GatewayLatencyMs = 180; }), "Latența locala");
            ProbeResult majorityTargets = NetworkProbe.AggregateInternetTargets(new InternetTargetObservation[]
            {
                new InternetTargetObservation { Name = "Cloudflare", Status = "Success", LatencyMs = 20 },
                new InternetTargetObservation { Name = "Google", Status = "Success", LatencyMs = 40 },
                new InternetTargetObservation { Name = "Quad9", Status = "TimedOut" }
            });
            bool majorityTargetAggregation = majorityTargets.Status == "Success" && majorityTargets.LatencyMs == 30;
            results.Add((majorityTargetAggregation ? "PASS" : "FAIL") + " | multi-target majority and median | " + (majorityTargetAggregation ? "2/3 targets reachable; median latency selected" : "target aggregation mismatch"));
            ProbeResult oneTarget = NetworkProbe.AggregateInternetTargets(new InternetTargetObservation[]
            {
                new InternetTargetObservation { Name = "Cloudflare", Status = "Success", LatencyMs = 18 },
                new InternetTargetObservation { Name = "Google", Status = "TimedOut" },
                new InternetTargetObservation { Name = "Quad9", Status = "TimedOut" }
            });
            ProbeResult noTargets = NetworkProbe.AggregateInternetTargets(new InternetTargetObservation[]
            {
                new InternetTargetObservation { Name = "Cloudflare", Status = "TimedOut" },
                new InternetTargetObservation { Name = "Google", Status = "TimedOut" },
                new InternetTargetObservation { Name = "Quad9", Status = "TimedOut" }
            });
            bool targetFailureStates = oneTarget.Status == "Partial" && oneTarget.LatencyMs == 18 && noTargets.Status == "AllFailed" && !noTargets.LatencyMs.HasValue;
            results.Add((targetFailureStates ? "PASS" : "FAIL") + " | partial and total target failure | " + (targetFailureStates ? "single target stays reachable; all-failed stays distinct" : "target failure classification mismatch"));
            Sample filteredTarget = BaseSample();
            filteredTarget.InternetTargetResults = new List<InternetTargetObservation>
            {
                new InternetTargetObservation { Name = "Cloudflare", Status = "Success", LatencyMs = 20 },
                new InternetTargetObservation { Name = "Google", Status = "Success", LatencyMs = 24 },
                new InternetTargetObservation { Name = "Quad9", Status = "TimedOut" }
            };
            filteredTarget.Events = "InternetTargetDisagreement";
            Sample oneTargetNoHttps = BaseSample();
            oneTargetNoHttps.InternetTargetResults = new List<InternetTargetObservation>
            {
                new InternetTargetObservation { Name = "Cloudflare", Status = "Success", LatencyMs = 20 },
                new InternetTargetObservation { Name = "Google", Status = "TimedOut" },
                new InternetTargetObservation { Name = "Quad9", Status = "TimedOut" }
            };
            oneTargetNoHttps.HttpsStatus = "AllFailed";
            Sample allUnavailable = BaseSample();
            allUnavailable.InternetTargetResults = new List<InternetTargetObservation>
            {
                new InternetTargetObservation { Name = "Cloudflare", Status = "TimedOut" },
                new InternetTargetObservation { Name = "Google", Status = "TimedOut" },
                new InternetTargetObservation { Name = "Quad9", Status = "TimedOut" }
            };
            allUnavailable.HttpsStatus = "AllFailed";
            bool connectivityConsensus = NetworkProbe.DetermineConnectivity(filteredTarget) == "OK" &&
                NetworkProbe.DetermineConnectivity(oneTargetNoHttps) == "Degraded" && NetworkProbe.DetermineConnectivity(allUnavailable) == "OutageSuspected";
            results.Add((connectivityConsensus ? "PASS" : "FAIL") + " | reachability consensus avoids false outage | " + (connectivityConsensus ? "one filtered target is not treated as an Internet outage" : "connectivity consensus mismatch"));
            IList<Sample> differingTargets = WindowWith(delegate(Sample s)
            {
                s.InternetPingStatus = "Success";
                s.InternetTargetResults = new List<InternetTargetObservation>
                {
                    new InternetTargetObservation { Name = "Cloudflare", Status = "Success", LatencyMs = 20 },
                    new InternetTargetObservation { Name = "Google", Status = "TimedOut" },
                    new InternetTargetObservation { Name = "Quad9", Status = "Success", LatencyMs = 24 }
                };
            });
            bool destinationFinding = DiagnosticEngine.AssessLikelySegment(differingTargets, AppLanguage.English).IndexOf("Some external destinations", StringComparison.Ordinal) >= 0;
            results.Add((destinationFinding ? "PASS" : "FAIL") + " | destination-specific response disagreement | " + (destinationFinding ? "does not mislabel a single filtered target as full outage" : "destination disagreement not diagnosed"));
            IList<Sample> upstreamOutage = WindowWith(delegate(Sample s)
            {
                s.InternetPingStatus = "AllFailed";
                s.InternetTargetResults = new List<InternetTargetObservation>
                {
                    new InternetTargetObservation { Name = "Cloudflare", Status = "TimedOut" },
                    new InternetTargetObservation { Name = "Google", Status = "TimedOut" },
                    new InternetTargetObservation { Name = "Quad9", Status = "TimedOut" }
                };
                s.HttpsStatus = "AllFailed";
            });
            bool upstreamFinding = DiagnosticEngine.AssessLikelySegment(upstreamOutage, AppLanguage.English).IndexOf("Beyond the gateway", StringComparison.Ordinal) >= 0;
            results.Add((upstreamFinding ? "PASS" : "FAIL") + " | gateway-isolated external outage | " + (upstreamFinding ? "gateway success separates upstream-path suspicion" : "upstream segment not identified"));
            IList<Sample> localOutage = WindowWith(delegate(Sample s)
            {
                s.GatewayPingStatus = "TimedOut";
                s.GatewayLatencyMs = null;
                s.InternetPingStatus = "AllFailed";
                s.InternetTargetResults = new List<InternetTargetObservation>
                {
                    new InternetTargetObservation { Name = "Cloudflare", Status = "TimedOut" },
                    new InternetTargetObservation { Name = "Google", Status = "TimedOut" },
                    new InternetTargetObservation { Name = "Quad9", Status = "TimedOut" }
                };
                s.HttpsStatus = "AllFailed";
            });
            bool localFinding = DiagnosticEngine.AssessLikelySegment(localOutage, AppLanguage.English).IndexOf("Local link to gateway", StringComparison.Ordinal) >= 0;
            results.Add((localFinding ? "PASS" : "FAIL") + " | local gateway-correlated outage | " + (localFinding ? "classifies the first failing segment as local" : "local segment not identified"));
            IList<Sample> routeSwitch = WindowWith(delegate(Sample s) { s.ActiveInterfaceChanged = true; });
            bool routeSwitchFinding = DiagnosticEngine.AssessLikelySegment(routeSwitch, AppLanguage.English).IndexOf("switched the active route interface", StringComparison.Ordinal) >= 0;
            results.Add((routeSwitchFinding ? "PASS" : "FAIL") + " | active route switch diagnosis | " + (routeSwitchFinding ? "notes route transitions without inferring cause" : "route switch not surfaced"));
            IList<Sample> dnsOnly = WindowWith(delegate(Sample s) { s.DnsStatus = "AllFailed"; s.InternetPingStatus = "Success"; });
            bool dnsFinding = DiagnosticEngine.AssessLikelySegment(dnsOnly, AppLanguage.English).IndexOf("DNS", StringComparison.Ordinal) >= 0;
            results.Add((dnsFinding ? "PASS" : "FAIL") + " | DNS failure while external IP remains reachable | " + (dnsFinding ? "separates name resolution from IP reachability" : "DNS segment not identified"));
            bool traceArgumentsSafe = RouteTrace.BuildArguments() == "/d /4 /h 8 /w 350 1.1.1.1";
            results.Add((traceArgumentsSafe ? "PASS" : "FAIL") + " | bounded route trace arguments | " + (traceArgumentsSafe ? "fixed target; eight hops; 350 ms per probe" : "route trace options changed"));
            bool interfaceTypes = NetworkProbe.DescribeInterfaceType(NetworkInterfaceType.Wireless80211) == "Wi-Fi" &&
                NetworkProbe.DescribeInterfaceType(NetworkInterfaceType.Ethernet) == "Ethernet" && NetworkProbe.DescribeInterfaceType(NetworkInterfaceType.Loopback) == "Other";
            results.Add((interfaceTypes ? "PASS" : "FAIL") + " | active interface categories | " + (interfaceTypes ? "Wi-Fi, Ethernet, and other types distinguished without names" : "interface type mapping mismatch"));
            List<Sample> loadWindow = new List<Sample>();
            for (int i = 0; i < 6; i++)
            {
                Sample quiet = BaseSample(); quiet.DownloadMbps = 0.1; quiet.UploadMbps = 0.1; quiet.InternetLatencyMs = 20; loadWindow.Add(quiet);
                Sample active = BaseSample(); active.DownloadMbps = 5.0; active.UploadMbps = 0.1; active.InternetLatencyMs = 100; loadWindow.Add(active);
            }
            LoadLatencyComparison loadComparison = LoadLatencyAnalysis.Compare(loadWindow);
            bool loadCorrelation = loadComparison.QuietSamples == 6 && loadComparison.ActiveSamples == 6 && loadComparison.QuietMedianMs == 20 && loadComparison.ActiveMedianMs == 100;
            results.Add((loadCorrelation ? "PASS" : "FAIL") + " | passive quiet-vs-active latency comparison | " + (loadCorrelation ? "medians split by this PC's observed traffic; no generated test traffic" : "load correlation mismatch"));
            bool loadReportCaveat = DiagnosticEngine.Analyze(loadWindow, AppLanguage.English).IndexOf("not a controlled test", StringComparison.OrdinalIgnoreCase) >= 0;
            results.Add((loadReportCaveat ? "PASS" : "FAIL") + " | load comparison limits | " + (loadReportCaveat ? "clearly labels correlation as observational" : "missing observational limitation"));
            bool englishReport = DiagnosticEngine.Analyze(HealthyWindow(), AppLanguage.English).IndexOf("NETWORK SESSION DIAGNOSIS", StringComparison.Ordinal) >= 0;
            results.Add((englishReport ? "PASS" : "FAIL") + " | English report | " + (englishReport ? "default report language available" : "English report missing"));
            bool pkce = ChatGptPlanClient.CreateCodeChallenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk") == "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM";
            results.Add((pkce ? "PASS" : "FAIL") + " | PKCE S256 | " + (pkce ? "RFC challenge vector matches" : "challenge mismatch"));
            bool loopbackCallback = false;
            string loopbackError = "";
            try { loopbackCallback = ChatGptPlanClient.SelfTestLoopbackCallback(); }
            catch (Exception exception) { loopbackError = exception.Message; }
            results.Add((loopbackCallback ? "PASS" : "FAIL") + " | OAuth loopback callback | " + (loopbackCallback ? "local callback received and parsed" : "local callback failed: " + loopbackError));
            bool pendingClientIdRetained = ChatGptPlanClient.ResolveIssuedClientId(null, "issued-client", "new-callback-client") == "issued-client" &&
                ChatGptPlanClient.ResolveIssuedClientId("saved-client", "pending-client", "callback-client") == "saved-client";
            results.Add((pendingClientIdRetained ? "PASS" : "FAIL") + " | OAuth client registration retry | " + (pendingClientIdRetained ? "saved registration ID wins on retry" : "retry registration ID selection failed"));
            string invalidGrantMessage = ChatGptPlanClient.BuildSignInFailureMessage("invalid_grant", "exchanging the authorization code", "(HTTP 400; OAuth error: invalid_grant)");
            bool actionableInvalidGrant = invalidGrantMessage.IndexOf("fresh sign-in", StringComparison.OrdinalIgnoreCase) >= 0 &&
                invalidGrantMessage.IndexOf("kept", StringComparison.OrdinalIgnoreCase) >= 0 &&
                invalidGrantMessage.IndexOf("expired or was already used", StringComparison.OrdinalIgnoreCase) < 0;
            results.Add((actionableInvalidGrant ? "PASS" : "FAIL") + " | OAuth invalid_grant recovery | " + (actionableInvalidGrant ? "retry guidance preserves registration" : "invalid_grant retry guidance missing"));
            bool requestIdSanitized = ChatGptPlanClient.SanitizeRequestId("req_ABC-123") == "req_ABC-123" &&
                ChatGptPlanClient.SanitizeRequestId("req_ABC;secret") == null;
            results.Add((requestIdSanitized ? "PASS" : "FAIL") + " | OAuth request ID display | " + (requestIdSanitized ? "safe request IDs retained; other text rejected" : "request ID sanitization failed"));
            TestOidc(results);
            string summary = ChatGptPlanClient.BuildAggregatedSummary(HealthyWindow(), AppLanguage.English);
            bool sanitized = summary.IndexOf("192.0.2.45", StringComparison.Ordinal) < 0 && summary.IndexOf("MyPrivateWifi", StringComparison.Ordinal) < 0 && summary.IndexOf("1.1.1.1", StringComparison.Ordinal) < 0 && summary.IndexOf("8.8.8.8", StringComparison.Ordinal) < 0;
            results.Add((sanitized ? "PASS" : "FAIL") + " | AI summary privacy | " + (sanitized ? "no raw network identifiers" : "raw identifier present"));
            Dictionary<string, object> aiPayload = ChatGptPlanClient.CreateResponsesPayload("test-model",
                new Dictionary<string, object> { { "role", "developer" }, { "content", "instructions" } },
                new Dictionary<string, object> { { "role", "user" }, { "content", summary } });
            bool noStoreStreaming = aiPayload.ContainsKey("store") && aiPayload["store"].Equals(false) && aiPayload.ContainsKey("stream") && aiPayload["stream"].Equals(true);
            results.Add((noStoreStreaming ? "PASS" : "FAIL") + " | Responses privacy flags | " + (noStoreStreaming ? "store=false and stream=true" : "required request flags missing"));
            bool aiEndpoints = AiProviderClient.GetEndpoint(AiProvider.Ollama) == "http://127.0.0.1:11434/api/chat" &&
                AiProviderClient.GetEndpoint(AiProvider.Gemini).StartsWith("https://generativelanguage.googleapis.com/", StringComparison.Ordinal) &&
                AiProviderClient.GetEndpoint(AiProvider.Groq) == "https://api.groq.com/openai/v1/chat/completions";
            results.Add((aiEndpoints ? "PASS" : "FAIL") + " | alternate AI provider endpoints | " + (aiEndpoints ? "local Ollama and HTTPS cloud providers configured" : "provider endpoint mismatch"));
            bool aiPrompt = AiProviderClient.BuildPrompt("aggregate-only-test", AppLanguage.Romanian).StartsWith("Explain this network-monitoring report in plain Romanian.", StringComparison.Ordinal) &&
                AiProviderClient.BuildPrompt("aggregate-only-test", AppLanguage.English).Contains("aggregate-only-test");
            results.Add((aiPrompt ? "PASS" : "FAIL") + " | alternate AI prompt language and report | " + (aiPrompt ? "language and aggregated report included" : "prompt content mismatch"));
            bool rejectsMissingCloudKey = false;
            try { AiProviderClient.Validate(new AiProviderSettings { Provider = AiProvider.Groq, Model = "test-model", ApiKey = "" }); }
            catch (InvalidOperationException) { rejectsMissingCloudKey = true; }
            bool acceptsOllamaWithoutKey = true;
            try { AiProviderClient.Validate(new AiProviderSettings { Provider = AiProvider.Ollama, Model = "qwen3:4b", ApiKey = "" }); }
            catch { acceptsOllamaWithoutKey = false; }
            bool providerValidation = rejectsMissingCloudKey && acceptsOllamaWithoutKey;
            results.Add((providerValidation ? "PASS" : "FAIL") + " | alternate AI credential validation | " + (providerValidation ? "cloud key required; local Ollama key not required" : "provider credential validation failed"));
            bool aiResponseParsing = AiProviderClient.ParseOllamaResponse("{\"message\":{\"content\":\"local answer\"}}") == "local answer" &&
                AiProviderClient.ParseGeminiResponse("{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"gemini answer\"}]}}]}") == "gemini answer" &&
                AiProviderClient.ParseOpenAiCompatibleResponse("{\"choices\":[{\"message\":{\"content\":\"groq answer\"}}]}") == "groq answer";
            results.Add((aiResponseParsing ? "PASS" : "FAIL") + " | alternate AI response parsing | " + (aiResponseParsing ? "Ollama, Gemini, and Groq response shapes parsed" : "provider response parsing mismatch"));
            bool dpapiSettingsRoundTrip = false;
            try
            {
                string testSettings = "{\"provider\":\"Groq\",\"apiKey\":\"test-secret\"}";
                byte[] protectedSettings = AiProviderClient.ProtectSettings(testSettings);
                dpapiSettingsRoundTrip = Encoding.UTF8.GetString(protectedSettings) != testSettings && AiProviderClient.UnprotectSettings(protectedSettings) == testSettings;
            }
            catch { dpapiSettingsRoundTrip = false; }
            results.Add((dpapiSettingsRoundTrip ? "PASS" : "FAIL") + " | AI settings DPAPI protection | " + (dpapiSettingsRoundTrip ? "per-user encrypted settings round trip" : "settings encryption round trip failed"));
            LatencyStatistics latencyStats = LatencyStatistics.Calculate(new double?[] { 10, 20, null, 40, 50 });
            bool robustLatencyStats = latencyStats.Count == 4 && latencyStats.Minimum == 10 && latencyStats.Median == 30 && latencyStats.P95 == 50 && latencyStats.Maximum == 50 && latencyStats.Jitter == 10;
            results.Add((robustLatencyStats ? "PASS" : "FAIL") + " | latency percentile and jitter calculations | " + (robustLatencyStats ? "median/p95 and gap-aware jitter match known values" : "latency statistics mismatch"));
            string statsReport = DiagnosticEngine.Analyze(new List<Sample> { HealthyWindow()[0], HealthyWindow()[1] }, AppLanguage.English);
            bool reportIncludesStats = statsReport.IndexOf("median", StringComparison.OrdinalIgnoreCase) >= 0 && statsReport.IndexOf("p95", StringComparison.OrdinalIgnoreCase) >= 0 && statsReport.IndexOf("jitter", StringComparison.OrdinalIgnoreCase) >= 0;
            results.Add((reportIncludesStats ? "PASS" : "FAIL") + " | local report latency statistics | " + (reportIncludesStats ? "percentiles and jitter included" : "latency statistics missing"));
            bool rateCalculation = NetworkProbe.CalculateMegabitsPerSecond(2000000, 1000000, 1) == 8.0 &&
                !NetworkProbe.CalculateMegabitsPerSecond(100, 200, 1).HasValue &&
                !NetworkProbe.CalculateMegabitsPerSecond(200, 100, 0).HasValue;
            results.Add((rateCalculation ? "PASS" : "FAIL") + " | passive network traffic rates | " + (rateCalculation ? "Mbps conversion and counter resets handled" : "traffic rate calculation mismatch"));
            bool csvSchema = CsvLogger.HeaderMatches("\uFEFF" + "TimestampLocal,WifiState,WifiSignalPercent,ActiveInterfaceType,GatewayPingStatus,GatewayLatencyMs,GatewayIcmpLossPctRolling60s,InternetPingStatus,InternetLatencyMs,InternetIcmpLossPctRolling60s,InternetTargets,InternetTargetsSucceeded,InternetTargetsAttempted,HttpsFallbackStatus,HttpsFallbackLatencyMs,HttpsEndpoints,HttpsSucceeded,HttpsAttempted,DnsStatus,DnsLatencyMs,DnsEndpoints,DnsSucceeded,DnsAttempted,Connectivity,Events,NetworkDownloadMbps,NetworkUploadMbps") &&
                !CsvLogger.HeaderMatches("TimestampLocal,WifiState,Events");
            results.Add((csvSchema ? "PASS" : "FAIL") + " | CSV schema migration guard | " + (csvSchema ? "new schema recognized; old file will be rotated intact" : "CSV schema guard failed"));
            bool chartRenders = false;
            try
            {
                using (LatencyChartPanel chart = new LatencyChartPanel())
                using (Bitmap bitmap = new Bitmap(500, 150))
                {
                    chart.Size = new Size(500, 150);
                    chart.SetSamples(new Sample[]
                    {
                        new Sample { GatewayLatencyMs = 4, InternetLatencyMs = 24 },
                        new Sample { GatewayLatencyMs = 8, InternetLatencyMs = 51 }
                    });
                    chart.DrawToBitmap(bitmap, new Rectangle(0, 0, 500, 150));
                    chartRenders = true;
                }
            }
            catch { chartRenders = false; }
            results.Add((chartRenders ? "PASS" : "FAIL") + " | latency chart rendering | " + (chartRenders ? "draws gateway and Internet samples" : "chart rendering failed"));

            string text = String.Join(Environment.NewLine, results.ToArray());
            if (!String.IsNullOrWhiteSpace(outputPath))
                File.WriteAllText(outputPath, text, new UTF8Encoding(true));
            else
                Console.WriteLine(text);

            return results.Any(delegate(string line) { return line.StartsWith("FAIL", StringComparison.Ordinal); }) ? 1 : 0;
        }

        private static void Test(List<string> results, string name, IList<Sample> samples, string expectedText)
        {
            string report = DiagnosticEngine.Analyze(samples);
            bool passed = report.IndexOf(expectedText, StringComparison.OrdinalIgnoreCase) >= 0;
            results.Add((passed ? "PASS" : "FAIL") + " | " + name + " | " + (passed ? "expected finding present" : "expected finding missing"));
        }

        private static void TestOidc(List<string> results)
        {
            string jwt, jwks;
            CreateTestJwt(out jwt, out jwks, "test-client", "test-nonce", DateTime.UtcNow.AddMinutes(5));
            bool valid = true;
            string validationError = "";
            try { OidcTokenValidator.Validate(jwt, jwks, "test-client", "test-nonce", DateTime.UtcNow); }
            catch (Exception exception) { valid = false; validationError = exception.Message; }
            results.Add((valid ? "PASS" : "FAIL") + " | OIDC token signature and claims | " + (valid ? "valid synthetic token accepted" : "valid token rejected: " + validationError));
            bool badNonceRejected = false;
            try { OidcTokenValidator.Validate(jwt, jwks, "test-client", "wrong-nonce", DateTime.UtcNow); }
            catch { badNonceRejected = true; }
            results.Add((badNonceRejected ? "PASS" : "FAIL") + " | OIDC nonce validation | " + (badNonceRejected ? "mismatched nonce rejected" : "mismatched nonce accepted"));
            bool badAudienceRejected = false;
            try { OidcTokenValidator.Validate(jwt, jwks, "other-client", "test-nonce", DateTime.UtcNow); }
            catch { badAudienceRejected = true; }
            results.Add((badAudienceRejected ? "PASS" : "FAIL") + " | OIDC audience validation | " + (badAudienceRejected ? "wrong audience rejected" : "wrong audience accepted"));
            string expired, expiredJwks;
            CreateTestJwt(out expired, out expiredJwks, "test-client", "test-nonce", DateTime.UtcNow.AddMinutes(-5));
            bool expiredRejected = false;
            try { OidcTokenValidator.Validate(expired, expiredJwks, "test-client", "test-nonce", DateTime.UtcNow); }
            catch { expiredRejected = true; }
            results.Add((expiredRejected ? "PASS" : "FAIL") + " | OIDC expiry validation | " + (expiredRejected ? "expired token rejected" : "expired token accepted"));
        }

        private static void CreateTestJwt(out string jwt, out string jwks, string audience, string nonce, DateTime expires)
        {
            using (RSACryptoServiceProvider rsa = new RSACryptoServiceProvider(2048))
            using (SHA256 sha = SHA256.Create())
            {
                RSAParameters key = rsa.ExportParameters(false);
                JavaScriptSerializer serializer = new JavaScriptSerializer();
                Dictionary<string, object> header = new Dictionary<string, object> { { "alg", "RS256" }, { "kid", "test-key" } };
                Dictionary<string, object> claims = new Dictionary<string, object>
                {
                    { "iss", "https://auth.openai.com" }, { "aud", audience },
                    { "exp", new DateTimeOffset(expires).ToUnixTimeSeconds() }, { "nonce", nonce }, { "sub", "synthetic-user" }
                };
                string first = Base64UrlTest(Encoding.UTF8.GetBytes(serializer.Serialize(header)));
                string second = Base64UrlTest(Encoding.UTF8.GetBytes(serializer.Serialize(claims)));
                string signed = first + "." + second;
                jwt = signed + "." + Base64UrlTest(rsa.SignData(Encoding.ASCII.GetBytes(signed), sha));
                Dictionary<string, object> jwk = new Dictionary<string, object>
                {
                    { "kid", "test-key" }, { "kty", "RSA" }, { "n", Base64UrlTest(key.Modulus) }, { "e", Base64UrlTest(key.Exponent) }
                };
                jwks = serializer.Serialize(new Dictionary<string, object> { { "keys", new object[] { jwk } } });
            }
        }

        private static string Base64UrlTest(byte[] bytes)
        {
            return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        private static IList<Sample> HealthyWindow()
        {
            List<Sample> samples = new List<Sample>();
            for (int i = 0; i < 6; i++)
            {
                Sample s = BaseSample();
                s.Timestamp = DateTimeOffset.Now.AddSeconds(-30 + i * 5);
                samples.Add(s);
            }
            return samples;
        }

        private static IList<Sample> WindowWith(Action<Sample> change)
        {
            List<Sample> samples = HealthyWindow().Cast<Sample>().ToList();
            foreach (Sample s in samples) change(s);
            return samples;
        }

        private static Sample BaseSample()
        {
            return new Sample
            {
                Timestamp = DateTimeOffset.Now,
                WifiState = "Connected",
                WifiSignalPercent = 75,
                GatewayPingStatus = "Success",
                GatewayLatencyMs = 2,
                GatewayLossPct = 0,
                InternetPingStatus = "Success",
                InternetLatencyMs = 20,
                InternetLossPct = 0,
                HttpsStatus = "NotNeeded",
                DnsStatus = "Success",
                DnsLatencyMs = 8,
                Connectivity = "OK",
                Events = ""
            };
        }
    }

    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            if (args.Length > 0 && String.Equals(args[0], "--self-test", StringComparison.OrdinalIgnoreCase))
            {
                string outputPath = args.Length > 1 ? args[1] : null;
                return SelfTests.Run(outputPath);
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                Application.Run(new MainForm());
                return 0;
            }
            catch (Exception ex)
            {
                MessageBox.Show("Network Monitor could not start:\r\n\r\n" + ex, "Network Monitor - Startup error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }
    }
}
