using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
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
        public bool UsedProxy;
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
        public double? Ipv6LatencyMs;
        public List<InternetTargetObservation> Ipv6TargetResults;
        public int? InternetTargetSucceeded;
        public int? InternetTargetAttempted;
        public string ActiveInterfaceType;
        public bool ActiveInterfaceChanged;
        public bool WifiRelevant;
        public bool HttpsUsedProxy;
        public bool CaptivePortalSuspected;
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
        private const int LatencySpikeMinimumMs = 150;
        private const int LatencySpikeMinimumSamples = 3;
        private const int LatencySpikeHistorySamples = 12;
        private const int LatencySpikeDeltaMs = 50;
        private const double LatencySpikeRelativeIncrease = 0.5;
        [DllImport("iphlpapi.dll", ExactSpelling = true)]
        private static extern int GetBestInterface(uint destinationAddress, out uint interfaceIndex);
        [DllImport("iphlpapi.dll", ExactSpelling = true)]
        private static extern int GetBestInterfaceEx(ref SockAddrIn6 destinationAddress, out uint interfaceIndex);
        [StructLayout(LayoutKind.Sequential)]
        private struct SockAddrIn6
        {
            public ushort Family;
            public ushort Port;
            public uint FlowInfo;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
            public byte[] Address;
            public uint ScopeId;
        }
        private static readonly InternetTargetDefinition[] InternetPingTargets = new InternetTargetDefinition[]
        {
            new InternetTargetDefinition { Name = "Cloudflare", Address = "1.1.1.1" },
            new InternetTargetDefinition { Name = "Google", Address = "8.8.8.8" },
            new InternetTargetDefinition { Name = "Quad9", Address = "9.9.9.9" }
        };
        private static readonly InternetTargetDefinition[] InternetIpv6Targets = new InternetTargetDefinition[]
        {
            new InternetTargetDefinition { Name = "Cloudflare IPv6", Address = "2606:4700:4700::1111" },
            new InternetTargetDefinition { Name = "Google IPv6", Address = "2001:4860:4860::8888" },
            new InternetTargetDefinition { Name = "Quad9 IPv6", Address = "2620:fe::fe" }
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
            Task<ProbeResult>[] ipv6Tasks = InternetIpv6Targets.Select(delegate(InternetTargetDefinition target)
            {
                return Task.Factory.StartNew(delegate { return PingTarget(target.Address); });
            }).ToArray();
            Task<List<ProbeResult>> dnsTask = checkDns ? Task.Factory.StartNew(delegate { return ProbeDnsHosts(); }) : null;
            try
            {
                List<Task> pingTaskList = internetTasks.Cast<Task>().ToList();
                pingTaskList.AddRange(ipv6Tasks.Cast<Task>());
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
            List<InternetTargetObservation> ipv6TargetResults = new List<InternetTargetObservation>();
            for (int i = 0; i < ipv6Tasks.Length; i++)
            {
                ProbeResult result = ipv6Tasks[i].IsCompleted && !ipv6Tasks[i].IsFaulted
                    ? ipv6Tasks[i].Result : new ProbeResult { Status = "Timeout", LatencyMs = PingTimeoutMs };
                ipv6TargetResults.Add(new InternetTargetObservation { Name = InternetIpv6Targets[i].Name, Status = result.Status, LatencyMs = result.LatencyMs });
            }
            ProbeResult ipv6Probe = AggregateInternetTargets(ipv6TargetResults);

            List<ProbeResult> httpsResults = new List<ProbeResult>();
            int totalTargetSuccesses = targetResults.Count(delegate(InternetTargetObservation result) { return result.Status == "Success"; }) +
                ipv6TargetResults.Count(delegate(InternetTargetObservation result) { return result.Status == "Success"; });
            if (ShouldCheckHttps(checkDns, totalTargetSuccesses))
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
            sample.Ipv6LatencyMs = ipv6Probe.LatencyMs;
            sample.Ipv6TargetResults = ipv6TargetResults;
            sample.InternetTargetSucceeded = targetResults.Count(delegate(InternetTargetObservation result) { return result.Status == "Success"; });
            sample.InternetTargetAttempted = targetResults.Count;
            sample.ActiveInterfaceType = activeInterfaceType;
            sample.WifiRelevant = activeInterfaceType == "Wi-Fi" ||
                (activeInterfaceType == "Unavailable" && lastSample != null && (lastSample.ActiveInterfaceType == "Wi-Fi" || lastSample.WifiRelevant));
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
            sample.HttpsUsedProxy = httpsResults.Any(delegate(ProbeResult result) { return result.UsedProxy; });
            sample.CaptivePortalSuspected = IsCaptivePortalSuspected(httpsResults);
            sample.HttpsEndpointResults = httpsResults.Count == 0 ? "NotNeeded" : String.Join(";", httpsResults.Select(delegate(ProbeResult p, int i)
            {
                return new Uri(HttpsProbeUrls[i]).Host + "=" + p.Status + (p.LatencyMs.HasValue ? "@" + p.LatencyMs.Value.ToString("0", CultureInfo.InvariantCulture) + "ms" : "");
            }).ToArray());
            sample.DnsStatus = dns.Status;
            sample.DnsLatencyMs = dns.LatencyMs;
            sample.DnsSucceeded = checkDns ? dnsSucceeded : (int?)null;
            sample.DnsAttempted = checkDns ? dnsResults.Count : (int?)null;
            sample.DnsEndpointResults = !checkDns ? "NotChecked" : String.Join(";", dnsResults.Select(delegate(ProbeResult p, int i) { return DnsProbeHosts[i] + "=" + p.Status; }).ToArray());

            List<Sample> window = previousSamples.Skip(Math.Max(0, previousSamples.Count - LatencySpikeHistorySamples)).ToList();
            window.Add(sample);
            sample.GatewayLossPct = RollingLoss(window, delegate(Sample s) { return s.GatewayPingStatus; });
            sample.InternetLossPct = RollingTargetLoss(window);
            sample.Events = String.Join(";", FindSampleEvents(sample, window.Take(window.Count - 1).ToList()));

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
                    attempted += sample.InternetTargetResults.Count(delegate(InternetTargetObservation result) { return result != null && result.Status != "Unavailable"; });
                    failed += sample.InternetTargetResults.Count(delegate(InternetTargetObservation result) { return result != null && result.Status != "Unavailable" && result.Status != "Success"; });
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

        internal static string FormatInternetTargets(IList<InternetTargetObservation> ipv4, IList<InternetTargetObservation> ipv6, AppLanguage language)
        {
            string v4 = FormatInternetTargets(ipv4, language);
            string v6 = FormatInternetTargets(ipv6, language);
            if (ipv4 == null || ipv4.Count == 0) return "IPv6: " + v6;
            if (ipv6 == null || ipv6.Count == 0) return "IPv4: " + v4;
            return "IPv4: " + v4 + "; IPv6: " + v6;
        }

        internal static string DetermineConnectivity(Sample sample)
        {
            int targetSuccesses = CountExternalSuccesses(sample);
            bool internetReachable = targetSuccesses > 0 || sample.HttpsStatus == "Success" || sample.CaptivePortalSuspected;
            if (!internetReachable) return "OutageSuspected";
            string[] events = (sample.Events ?? "").Split(new char[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            bool actionableEvent = events.Any(delegate(string code)
            {
                return code == "WiFiDisconnected" || code == "DNSFailure" || code == "GatewayLatencySpike" ||
                    code == "InternetLatencySpike" || code == "HTTPSLatencySpike" || code == "CaptivePortalSuspected";
            });
            if (actionableEvent || sample.DnsStatus == "AllFailed") return "Degraded";
            if (events.Any(delegate(string code)
            {
                return code == "InternetTargetDisagreement" || code == "IPv6TargetDisagreement" ||
                    code == "HTTPSProbeDisagreement" || code == "DNSProbeDisagreement" ||
                    code == "ICMPUnavailableHTTPSOk";
            })) return "Partial";
            return "OK";
        }

        internal static int CountExternalSuccesses(Sample sample)
        {
            if (sample == null) return 0;
            int ipv4 = sample.InternetTargetResults == null ? (sample.InternetPingStatus == "Success" ? 1 : 0) :
                sample.InternetTargetResults.Count(delegate(InternetTargetObservation result) { return result != null && result.Status == "Success"; });
            int ipv6 = sample.Ipv6TargetResults == null ? 0 :
                sample.Ipv6TargetResults.Count(delegate(InternetTargetObservation result) { return result != null && result.Status == "Success"; });
            return ipv4 + ipv6;
        }

        internal static int? GetBestInterfaceIndex(string destination)
        {
            try
            {
                IPAddress address = IPAddress.Parse(destination);
                uint index;
                if (address.AddressFamily == AddressFamily.InterNetwork)
                {
                    byte[] bytes = address.GetAddressBytes();
                    if (GetBestInterface(BitConverter.ToUInt32(bytes, 0), out index) != 0 || index == 0) return null;
                }
                else if (address.AddressFamily == AddressFamily.InterNetworkV6)
                {
                    SockAddrIn6 socketAddress = new SockAddrIn6
                    {
                        Family = (ushort)AddressFamily.InterNetworkV6,
                        Port = 0,
                        FlowInfo = 0,
                        Address = address.GetAddressBytes(),
                        ScopeId = (uint)address.ScopeId
                    };
                    if (GetBestInterfaceEx(ref socketAddress, out index) != 0 || index == 0) return null;
                }
                else return null;
                return checked((int)index);
            }
            catch { return null; }
        }

        internal static string DescribeInterfaceType(NetworkInterfaceType type)
        {
            if (type == NetworkInterfaceType.Wireless80211) return "Wi-Fi";
            if (type == NetworkInterfaceType.Ethernet || type == NetworkInterfaceType.GigabitEthernet) return "Ethernet";
            if (type == NetworkInterfaceType.Tunnel) return "Tunnel";
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
                int? bestIndex = GetBestInterfaceIndex(gateway) ?? GetBestInterfaceIndex("1.1.1.1") ?? GetBestInterfaceIndex("2606:4700:4700::1111");
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
                                return address.Address.AddressFamily == IPAddress.Parse(gateway).AddressFamily && String.Equals(address.Address.ToString(), gateway, StringComparison.OrdinalIgnoreCase);
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

            int? ipv4RouteIndex = GetBestInterfaceIndex("1.1.1.1");
            bool ipv6Route = !ipv4RouteIndex.HasValue && GetBestInterfaceIndex("2606:4700:4700::1111").HasValue;
            int? bestIndex = ipv4RouteIndex ?? (ipv6Route ? GetBestInterfaceIndex("2606:4700:4700::1111") : (int?)null);
            NetworkInterface activeInterface = bestIndex.HasValue ? interfaces.FirstOrDefault(delegate(NetworkInterface ni)
            {
                try { return ni.OperationalStatus == OperationalStatus.Up && ni.GetIPProperties().GetIPv4Properties().Index == bestIndex.Value; }
                catch { return false; }
            }) : null;
            if (activeInterface == null) activeInterface = wifiUp;
            if (activeInterface != null)
            {
                activeInterfaceType = DescribeInterfaceType(activeInterface.NetworkInterfaceType);
                gateway = GetGateway(activeInterface, ipv6Route ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork);
                if (activeInterface.NetworkInterfaceType == NetworkInterfaceType.Wireless80211) signalPercent = GetWifiSignalPercent();
            }

            bool routeInterfaceKnown = bestIndex.HasValue && activeInterface != null;
            if (gateway == null && !routeInterfaceKnown)
            {
                NetworkInterface fallback = interfaces.FirstOrDefault(delegate(NetworkInterface ni)
                {
                    return ni.OperationalStatus == OperationalStatus.Up && GetGateway(ni, AddressFamily.InterNetwork) != null;
                });
                if (fallback != null)
                {
                    gateway = GetGateway(fallback, AddressFamily.InterNetwork);
                    activeInterfaceType = DescribeInterfaceType(fallback.NetworkInterfaceType);
                }
            }
            return state;
        }

        private static string GetGateway(NetworkInterface networkInterface, AddressFamily preferredFamily)
        {
            try
            {
                foreach (GatewayIPAddressInformation item in networkInterface.GetIPProperties().GatewayAddresses)
                {
                    IPAddress address = item.Address;
                    if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)) continue;
                    if (address.AddressFamily == preferredFamily) return address.ToString();
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
            bool usedProxy = false;
            try
            {
                request = (HttpWebRequest)WebRequest.Create(url);
                request.Method = "GET";
                request.Timeout = HttpsTimeoutMs;
                request.ReadWriteTimeout = HttpsTimeoutMs;
                request.AllowAutoRedirect = false;
                request.UserAgent = "Windows-Network-Monitor/1.0";
                usedProxy = IsUsingProxy(request.Proxy, new Uri(url));
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                {
                    string status = ClassifyHttpsResponse(url, response);
                    watch.Stop();
                    return new ProbeResult { Status = status, LatencyMs = watch.Elapsed.TotalMilliseconds, UsedProxy = usedProxy };
                }
            }
            catch (WebException exception)
            {
                HttpWebResponse response = exception.Response as HttpWebResponse;
                if (response != null)
                {
                    string status = ClassifyHttpsResponse(url, response);
                    response.Close();
                    watch.Stop();
                    return new ProbeResult { Status = status, LatencyMs = watch.Elapsed.TotalMilliseconds, UsedProxy = usedProxy };
                }
                return new ProbeResult { Status = "Failed", LatencyMs = watch.Elapsed.TotalMilliseconds, UsedProxy = usedProxy };
            }
            catch
            {
                return new ProbeResult { Status = "Failed", LatencyMs = watch.Elapsed.TotalMilliseconds, UsedProxy = usedProxy };
            }
            finally
            {
                if (request != null) request.Abort();
            }
        }

        internal static bool IsUsingProxy(IWebProxy proxy, Uri destination)
        {
            if (proxy == null || destination == null) return false;
            try { return !proxy.IsBypassed(destination); }
            catch { return false; }
        }

        internal static bool ShouldCheckHttps(bool checkDns, int successfulIpTargets)
        {
            return checkDns || successfulIpTargets < 2;
        }

        internal static bool IsCaptivePortalSuspected(IList<ProbeResult> results)
        {
            return results != null && results.Count(delegate(ProbeResult result) { return result != null && result.Status == "PortalOrIntercept"; }) >= 2;
        }

        internal static int Ipv6SockAddrSize()
        {
            return Marshal.SizeOf(typeof(SockAddrIn6));
        }

        internal static string ClassifyUnexpectedHttpsResponse(int statusCode, string contentType, bool expectedResponse)
        {
            if (expectedResponse) return "Success";
            if ((statusCode >= 300 && statusCode < 400) ||
                (statusCode == 200 && (contentType ?? "").IndexOf("text/html", StringComparison.OrdinalIgnoreCase) >= 0))
                return "PortalOrIntercept";
            return "HTTP_" + statusCode.ToString(CultureInfo.InvariantCulture);
        }

        internal static string ClassifyHttpsResponse(string url, HttpWebResponse response)
        {
            if (response == null) return "Failed";
            int code = (int)response.StatusCode;
            return ClassifyUnexpectedHttpsResponse(code, response.ContentType, IsExpectedHttpsResponse(url, response));
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

        internal static bool IsLatencySpike(double? currentMs, IEnumerable<double?> history)
        {
            if (!currentMs.HasValue || currentMs.Value < LatencySpikeMinimumMs) return false;
            List<double> baseline = (history ?? Enumerable.Empty<double?>()).Where(delegate(double? value) { return value.HasValue; })
                .Select(delegate(double? value) { return value.Value; }).OrderBy(delegate(double value) { return value; }).ToList();
            if (baseline.Count < LatencySpikeMinimumSamples) return false;
            double median = baseline.Count % 2 == 0
                ? (baseline[baseline.Count / 2 - 1] + baseline[baseline.Count / 2]) / 2.0
                : baseline[baseline.Count / 2];
            double requiredIncrease = Math.Max(LatencySpikeDeltaMs, median * LatencySpikeRelativeIncrease);
            return currentMs.Value >= median + requiredIncrease;
        }

        internal static bool HasWifiIssue(Sample sample)
        {
            return sample != null && (sample.WifiRelevant || String.Equals(sample.ActiveInterfaceType, "Wi-Fi", StringComparison.OrdinalIgnoreCase)) &&
                sample.WifiState == "Disconnected";
        }

        internal static List<string> FindSampleEvents(Sample sample, IList<Sample> previousSamples)
        {
            List<string> events = new List<string>();
            int targetSuccesses = sample.InternetTargetResults == null ? (sample.InternetPingStatus == "Success" ? 1 : 0) : sample.InternetTargetResults.Count(delegate(InternetTargetObservation result) { return result != null && result.Status == "Success"; });
            int targetAttempts = sample.InternetTargetResults == null ? (sample.InternetPingStatus == "Unavailable" || sample.InternetPingStatus == "NotChecked" ? 0 : 1) :
                sample.InternetTargetResults.Count(delegate(InternetTargetObservation result) { return result != null && result.Status != "Unavailable"; });
            int ipv6Successes = sample.Ipv6TargetResults == null ? 0 : sample.Ipv6TargetResults.Count(delegate(InternetTargetObservation result) { return result != null && result.Status == "Success"; });
            int ipv6Attempts = sample.Ipv6TargetResults == null ? 0 : sample.Ipv6TargetResults.Count(delegate(InternetTargetObservation result) { return result != null && result.Status != "Unavailable"; });
            if (HasWifiIssue(sample)) events.Add("WiFiDisconnected");
            if (sample.WifiRelevant && sample.WifiSignalPercent.HasValue && sample.WifiSignalPercent.Value < 35) events.Add("WeakWiFiSignal");
            if (sample.ActiveInterfaceChanged) events.Add("ActiveRouteChanged");
            if (sample.GatewayPingStatus != "Success" && sample.GatewayPingStatus != "Unavailable" && sample.GatewayPingStatus != "NoGateway")
                events.Add("GatewayUnreachable");
            if (targetAttempts > 1 && targetSuccesses > 0 && targetSuccesses < targetAttempts) events.Add("InternetTargetDisagreement");
            if (ipv6Attempts > 1 && ipv6Successes > 0 && ipv6Successes < ipv6Attempts) events.Add("IPv6TargetDisagreement");
            if (targetSuccesses == 0 && ipv6Successes > 0) events.Add("IPv6OnlyReachable");
            if (targetSuccesses == 0 && ipv6Successes == 0 && sample.HttpsStatus == "Success") events.Add("ICMPUnavailableHTTPSOk");
            if (targetSuccesses == 0 && ipv6Successes == 0 && sample.HttpsStatus == "AllFailed") events.Add("InternetOutageSuspected");
            if (sample.HttpsSucceeded.HasValue && sample.HttpsSucceeded.Value > 0 && sample.HttpsAttempted.HasValue && sample.HttpsSucceeded.Value < sample.HttpsAttempted.Value)
                events.Add("HTTPSProbeDisagreement");
            if (sample.DnsStatus == "AllFailed") events.Add("DNSFailure");
            if (sample.DnsSucceeded.HasValue && sample.DnsSucceeded.Value > 0 && sample.DnsAttempted.HasValue && sample.DnsSucceeded.Value < sample.DnsAttempted.Value)
                events.Add("DNSProbeDisagreement");
            if (sample.CaptivePortalSuspected) events.Add("CaptivePortalSuspected");
            if (IsLatencySpike(sample.GatewayLatencyMs, previousSamples.Select(delegate(Sample item) { return item.GatewayLatencyMs; }))) events.Add("GatewayLatencySpike");
            if (IsLatencySpike(sample.InternetLatencyMs, previousSamples.Select(delegate(Sample item) { return item.InternetLatencyMs; }))) events.Add("InternetLatencySpike");
            if (sample.HttpsStatus == "Success" && IsLatencySpike(sample.HttpsLatencyMs, previousSamples.Select(delegate(Sample item) { return item.HttpsLatencyMs; })))
                events.Add("HTTPSLatencySpike");
            return events;
        }
    }

    internal sealed class RouteTraceHop
    {
        public int Number;
        public string Probe1;
        public string Probe2;
        public string Probe3;
        public string Address;
        public bool Responded;
    }

    internal static class RouteTrace
    {
        internal const string Target = "1.1.1.1";
        internal const string Ipv6Target = "2606:4700:4700::1111";
        private static readonly Regex HopLineRegex = new Regex(
            @"^\s*(?<hop>\d+)\s+(?<p1>\*|\d+(?:[\.,]\d+)?\s*\p{L}+)\s+(?<p2>\*|\d+(?:[\.,]\d+)?\s*\p{L}+)\s+(?<p3>\*|\d+(?:[\.,]\d+)?\s*\p{L}+)\s*(?<address>.*?)\s*$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        internal static string BuildArguments()
        {
            return BuildArguments(false);
        }

        internal static string BuildArguments(bool ipv6)
        {
            return "/d " + (ipv6 ? "/6" : "/4") + " /h 8 /w 350 " + (ipv6 ? Ipv6Target : Target);
        }

        internal static bool SelectIpv6Route(bool ipv4RouteAvailable, bool ipv6RouteAvailable)
        {
            return !ipv4RouteAvailable && ipv6RouteAvailable;
        }

        internal static List<RouteTraceHop> ParseHops(string output)
        {
            List<RouteTraceHop> hops = new List<RouteTraceHop>();
            if (String.IsNullOrWhiteSpace(output)) return hops;
            foreach (string line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                Match match = HopLineRegex.Match(line);
                if (!match.Success) continue;
                int number;
                if (!Int32.TryParse(match.Groups["hop"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out number)) continue;
                string probe1 = match.Groups["p1"].Value.Trim();
                string probe2 = match.Groups["p2"].Value.Trim();
                string probe3 = match.Groups["p3"].Value.Trim();
                bool responded = probe1 != "*" || probe2 != "*" || probe3 != "*";
                string address = match.Groups["address"].Value.Trim();
                if (!responded || address == "*") address = "";
                hops.Add(new RouteTraceHop { Number = number, Probe1 = probe1, Probe2 = probe2, Probe3 = probe3, Address = address, Responded = responded });
            }
            return hops;
        }

        internal static string Summarize(IList<RouteTraceHop> hops, string target, AppLanguage language)
        {
            if (hops == null || hops.Count == 0) return Localization.Get(language, "RouteTraceSummaryUnavailable");
            RouteTraceHop last = hops[hops.Count - 1];
            if (last.Responded && !String.IsNullOrWhiteSpace(last.Address) &&
                last.Address.IndexOf(target ?? "", StringComparison.OrdinalIgnoreCase) >= 0)
                return Localization.Get(language, "RouteTraceSummaryReached");
            if (!hops.Any(delegate(RouteTraceHop hop) { return hop.Responded; }))
                return Localization.Get(language, "RouteTraceSummaryNoReplies");
            return Localization.Get(language, "RouteTraceSummaryPartial");
        }

        internal static string SelectedTarget
        {
            get
            {
                bool ipv6 = SelectIpv6Route(NetworkProbe.GetBestInterfaceIndex(Target).HasValue, NetworkProbe.GetBestInterfaceIndex(Ipv6Target).HasValue);
                return ipv6 ? Ipv6Target : Target;
            }
        }

        internal static string Run()
        {
            string executable = Path.Combine(Environment.SystemDirectory, "tracert.exe");
            if (!File.Exists(executable)) return "Windows tracert.exe is not available.";
            bool ipv6 = SelectIpv6Route(NetworkProbe.GetBestInterfaceIndex(Target).HasValue, NetworkProbe.GetBestInterfaceIndex(Ipv6Target).HasValue);
            ProcessStartInfo info = new ProcessStartInfo(executable, BuildArguments(ipv6));
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

    internal static class LiquidGlassTheme
    {
        internal static readonly Color Window = Color.FromArgb(8, 10, 14);
        internal static readonly Color WindowRaised = Color.FromArgb(15, 19, 27);
        internal static readonly Color GlassTop = Color.FromArgb(238, 29, 37, 50);
        internal static readonly Color GlassBottom = Color.FromArgb(230, 16, 21, 30);
        internal static readonly Color GlassInset = Color.FromArgb(226, 22, 29, 40);
        internal static readonly Color Stroke = Color.FromArgb(68, 221, 233, 249);
        internal static readonly Color Highlight = Color.FromArgb(42, 255, 255, 255);
        internal static readonly Color Text = Color.FromArgb(244, 246, 250);
        internal static readonly Color SecondaryText = Color.FromArgb(191, 201, 216);
        internal static readonly Color MutedText = Color.FromArgb(143, 156, 176);
        internal static readonly Color Sapphire = Color.FromArgb(131, 181, 255);
        internal static readonly Color SapphireHover = Color.FromArgb(161, 198, 255);
        internal static readonly Color Good = Color.FromArgb(112, 217, 163);
        internal static readonly Color Warning = Color.FromArgb(242, 190, 98);
        internal static readonly Color Danger = Color.FromArgb(255, 114, 122);

        internal static void ApplyTree(Control root, GlassButton primaryButton)
        {
            foreach (Control control in root.Controls)
            {
                if (control is Label)
                {
                    control.ForeColor = control.Font.Size <= 9.5F ? MutedText : Text;
                    control.BackColor = Color.Transparent;
                }
                else if (control is GlassButton)
                {
                    GlassButton button = (GlassButton)control;
                    if (Object.ReferenceEquals(button, primaryButton))
                    {
                        button.BackColor = Sapphire;
                        button.ForeColor = Color.FromArgb(9, 15, 24);
                    }
                    else
                    {
                        button.BackColor = GlassInset;
                        button.ForeColor = Text;
                    }
                }
                else if (control is ComboBox || control is TextBox)
                {
                    control.BackColor = Color.FromArgb(255, 20, 26, 36);
                    control.ForeColor = Text;
                }
                else if (control is DataGridView)
                {
                    DataGridView data = (DataGridView)control;
                    data.BackgroundColor = WindowRaised;
                    data.GridColor = Color.FromArgb(37, 48, 63);
                    data.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(255, 24, 31, 43);
                    data.ColumnHeadersDefaultCellStyle.ForeColor = SecondaryText;
                    data.DefaultCellStyle.BackColor = Color.FromArgb(255, 15, 20, 28);
                    data.DefaultCellStyle.ForeColor = Text;
                    data.DefaultCellStyle.SelectionBackColor = Color.FromArgb(255, 35, 54, 78);
                    data.DefaultCellStyle.SelectionForeColor = Text;
                    data.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(255, 19, 25, 35);
                    data.RowHeadersDefaultCellStyle.BackColor = WindowRaised;
                    data.RowHeadersDefaultCellStyle.ForeColor = MutedText;
                }
                else if (control is TableLayoutPanel)
                {
                    control.BackColor = Color.Transparent;
                }
                else if ((control is Panel && control.Dock == DockStyle.Top) || control is FlowLayoutPanel)
                {
                    control.BackColor = WindowRaised;
                }
                else if (!(control is SoftSurfacePanel) && !(control is LiquidBackdropPanel))
                {
                    if (control.BackColor.A != 0) control.BackColor = Window;
                }
                ApplyTree(control, primaryButton);
            }
        }
    }

    internal static class GlassWindowEffects
    {
        private const int DwmwaUseImmersiveDarkMode = 20;
        private const int DwmwaWindowCornerPreference = 33;
        private const int DwmwaSystemBackdropType = 38;
        private const int DwmwcpRound = 2;
        private const int DwmsbtMainWindow = 2;
        private const int DwmsbtTransientWindow = 3;

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct ComboBoxInfo
        {
            public int Size;
            public NativeRect ItemRect;
            public NativeRect ButtonRect;
            public uint ButtonState;
            public IntPtr ComboHandle;
            public IntPtr ItemHandle;
            public IntPtr ListHandle;
        }

        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int valueSize);

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        private static extern int SetWindowTheme(IntPtr hwnd, string appName, string idList);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetComboBoxInfo(IntPtr hwndCombo, ref ComboBoxInfo info);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, bool redraw);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern bool DeleteObject(IntPtr handle);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);

        private const int WmNcLButtonDown = 0x00A1;
        private static readonly IntPtr HtCaption = new IntPtr(2);

        internal static void Attach(Form form, bool transient)
        {
            if (transient)
            {
                form.SizeChanged += delegate { ApplyRoundedFormRegion(form, 18); };
                form.Shown += delegate { ApplyRoundedFormRegion(form, 18); };
            }
            form.Shown += delegate { Apply(form.Handle, transient); };
        }

        internal static void BeginWindowDrag(Form form)
        {
            if (form == null || !form.IsHandleCreated) return;
            ReleaseCapture();
            SendMessage(form.Handle, WmNcLButtonDown, HtCaption, IntPtr.Zero);
        }

        private static void ApplyRoundedFormRegion(Form form, int radius)
        {
            if (form == null || form.ClientSize.Width < 2 || form.ClientSize.Height < 2) return;
            try
            {
                Rectangle bounds = new Rectangle(0, 0, form.ClientSize.Width, form.ClientSize.Height);
                using (GraphicsPath path = RoundedPath(bounds, radius))
                {
                    Region old = form.Region;
                    form.Region = new Region(path);
                    if (old != null) old.Dispose();
                }
            }
            catch { /* Native window corners remain a fallback on Windows versions that reject a custom region. */ }
        }

        private static GraphicsPath RoundedPath(Rectangle rect, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            int d = Math.Max(2, Math.Min(radius * 2, Math.Min(rect.Width, rect.Height)));
            path.AddArc(rect.Left, rect.Top, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Top, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.Left, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        private static void Apply(IntPtr handle, bool transient)
        {
            try
            {
                int dark = 1;
                int corners = DwmwcpRound;
                int backdrop = transient ? DwmsbtTransientWindow : DwmsbtMainWindow;
                DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));
                DwmSetWindowAttribute(handle, DwmwaWindowCornerPreference, ref corners, sizeof(int));
                DwmSetWindowAttribute(handle, DwmwaSystemBackdropType, ref backdrop, sizeof(int));
            }
            catch { /* DWM materials are optional; the painted glass theme remains available. */ }
        }

        internal static void ApplyRoundedComboPopup(IntPtr comboHandle)
        {
            try
            {
                ComboBoxInfo info = new ComboBoxInfo();
                info.Size = Marshal.SizeOf(typeof(ComboBoxInfo));
                if (!GetComboBoxInfo(comboHandle, ref info) || info.ListHandle == IntPtr.Zero) return;
                int corners = DwmwcpRound;
                DwmSetWindowAttribute(info.ListHandle, DwmwaWindowCornerPreference, ref corners, sizeof(int));
                NativeRect rect;
                if (!GetWindowRect(info.ListHandle, out rect)) return;
                int width = Math.Max(1, rect.Right - rect.Left);
                int height = Math.Max(1, rect.Bottom - rect.Top);
                IntPtr region = CreateRoundRectRgn(0, 0, width + 1, height + 1, 20, 20);
                if (region == IntPtr.Zero) return;
                if (SetWindowRgn(info.ListHandle, region, true) == 0) DeleteObject(region);
            }
            catch { /* Rounded popup corners are cosmetic; keep the native list usable on older Windows. */ }
        }

        internal static void ApplyDarkScrollbars(Control root)
        {
            if (root == null) return;
            ScrollableControl scrollable = root as ScrollableControl;
            if (scrollable != null && scrollable.AutoScroll)
            {
                try { SetWindowTheme(scrollable.Handle, "DarkMode_Explorer", null); }
                catch { /* The painted interface remains usable if Windows ignores dark scrollbar themes. */ }
            }
            TextBox textBox = root as TextBox;
            if (textBox != null && textBox.Multiline && textBox.ScrollBars != ScrollBars.None)
            {
                try { SetWindowTheme(textBox.Handle, "DarkMode_Explorer", null); }
                catch { /* Older Windows versions keep the native scrollbar if the theme is unavailable. */ }
            }
            foreach (Control child in root.Controls) ApplyDarkScrollbars(child);
        }
    }

    internal sealed class GlassButton : Control, IButtonControl
    {
        private bool hovering;
        private bool pressed;
        private bool isDefault;
        private DialogResult dialogResult;

        public DialogResult DialogResult { get { return dialogResult; } set { dialogResult = value; } }

        public GlassButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable | ControlStyles.UserMouse | ControlStyles.SupportsTransparentBackColor, true);
            TabStop = true;
            AccessibleRole = AccessibleRole.PushButton;
            Font = new Font("Segoe UI", 9F);
            Cursor = Cursors.Hand;
        }

        protected override void OnMouseEnter(EventArgs e) { hovering = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hovering = false; pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left && Enabled) { pressed = true; Focus(); Invalidate(); } base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { bool click = pressed && e.Button == MouseButtons.Left && ClientRectangle.Contains(e.Location); pressed = false; Invalidate(); base.OnMouseUp(e); if (click) PerformClick(); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnBackColorChanged(EventArgs e) { Invalidate(); base.OnBackColorChanged(e); }
        protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); UpdateRoundedRegion(); }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (Enabled && (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter)) { pressed = true; Invalidate(); e.Handled = true; e.SuppressKeyPress = true; }
            base.OnKeyDown(e);
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            bool click = pressed && Enabled && (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter);
            pressed = false;
            Invalidate();
            base.OnKeyUp(e);
            if (click) PerformClick();
        }

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            Form form = FindForm();
            if (form != null && DialogResult != DialogResult.None) form.DialogResult = DialogResult;
        }

        public void NotifyDefault(bool value) { isDefault = value; Invalidate(); }
        public void PerformClick() { if (Enabled && CanSelect) { Focus(); OnClick(EventArgs.Empty); } }

        private void UpdateRoundedRegion()
        {
            if (ClientSize.Width < 2 || ClientSize.Height < 2) return;
            int radius = Math.Max(8, ClientSize.Height / 2);
            using (GraphicsPath path = RoundedPath(new Rectangle(0, 0, ClientSize.Width, ClientSize.Height), radius))
            {
                Region old = Region;
                Region = new Region(path);
                if (old != null) old.Dispose();
            }
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            SoftSurfacePanel glassParent = Parent as SoftSurfacePanel;
            if (glassParent != null)
            {
                glassParent.PaintBackgroundForChild(e.Graphics, this);
                return;
            }

            Control ancestor = Parent;
            while (ancestor != null && ancestor.BackColor.A == 0) ancestor = ancestor.Parent;
            Color surface = ancestor == null ? LiquidGlassTheme.Window : ancestor.BackColor;
            using (SolidBrush brush = new SolidBrush(surface)) e.Graphics.FillRectangle(brush, ClientRectangle);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (ClientSize.Width < 2 || ClientSize.Height < 2) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle bounds = new Rectangle(1, 1, ClientSize.Width - 3, ClientSize.Height - 3);
            int radius = Math.Min(15, Math.Max(8, bounds.Height / 2));
            using (GraphicsPath path = RoundedPath(bounds, radius))
            using (LinearGradientBrush fill = new LinearGradientBrush(bounds,
                Enabled ? (pressed ? Darken(BackColor, 12) : (hovering ? Lighten(BackColor, 12) : BackColor)) : Color.FromArgb(255, 33, 37, 45),
                Enabled ? (hovering && !pressed ? BackColor : Darken(BackColor, 8)) : Color.FromArgb(255, 25, 29, 36), 90F))
            using (Pen edge = new Pen(Enabled ? (BackColor == LiquidGlassTheme.Sapphire || BackColor == LiquidGlassTheme.SapphireHover ? Color.FromArgb(145, 197, 255) : LiquidGlassTheme.Stroke) : Color.FromArgb(50, 160, 170, 185), isDefault ? 1.5F : 1F))
            using (Pen sheen = new Pen(Color.FromArgb(34, 255, 255, 255)))
            {
                e.Graphics.FillPath(fill, path);
                e.Graphics.DrawPath(edge, path);
                e.Graphics.DrawLine(sheen, bounds.Left + radius, bounds.Top + 1, bounds.Right - radius, bounds.Top + 1);
            }
            Color textColor = Enabled ? ForeColor : LiquidGlassTheme.MutedText;
            TextRenderer.DrawText(e.Graphics, Text, Font, bounds, textColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
            if (Focused || isDefault)
            {
                Rectangle focusBounds = Rectangle.Inflate(bounds, -2, -2);
                using (GraphicsPath focusPath = RoundedPath(focusBounds, Math.Max(7, focusBounds.Height / 2)))
                using (Pen focusRing = new Pen(Color.FromArgb(185, 143, 190, 255), 1.25F))
                    e.Graphics.DrawPath(focusRing, focusPath);
            }
        }

        private static Color Lighten(Color color, int amount)
        {
            return Color.FromArgb(color.A, Math.Min(255, color.R + amount), Math.Min(255, color.G + amount), Math.Min(255, color.B + amount));
        }

        private static Color Darken(Color color, int amount)
        {
            return Color.FromArgb(color.A, Math.Max(0, color.R - amount), Math.Max(0, color.G - amount), Math.Max(0, color.B - amount));
        }

        private static GraphicsPath RoundedPath(Rectangle rect, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            int d = Math.Max(2, Math.Min(radius * 2, Math.Min(rect.Width, rect.Height)));
            path.AddArc(rect.Left, rect.Top, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Top, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.Left, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    internal sealed class LatencyChartPanel : Panel
    {
        private List<Sample> samples = new List<Sample>();
        public AppLanguage Language = AppLanguage.English;

        public LatencyChartPanel()
        {
            DoubleBuffered = true;
            BackColor = Color.FromArgb(255, 17, 22, 31);
            BorderStyle = BorderStyle.None;
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
            TextRenderer.DrawText(e.Graphics, Localization.Get(Language, "LatencyChartTitle"), Font, new Point(8, 4), LiquidGlassTheme.SecondaryText);
            double maximum = samples.SelectMany(delegate(Sample s) { return new double?[] { s.GatewayLatencyMs, s.InternetLatencyMs, s.Ipv6LatencyMs }; })
                .Where(delegate(double? v) { return v.HasValue; }).Select(delegate(double? v) { return v.Value; }).DefaultIfEmpty(50).Max();
            maximum = Math.Max(25, Math.Ceiling(maximum / 25.0) * 25.0);
            using (Pen gridPen = new Pen(Color.FromArgb(46, 57, 73)))
            using (Pen gatewayPen = new Pen(LiquidGlassTheme.Sapphire, 2F))
            using (Pen internetPen = new Pen(LiquidGlassTheme.Warning, 2F))
            using (Pen ipv6Pen = new Pen(LiquidGlassTheme.Good, 2F))
            using (Brush pointGateway = new SolidBrush(gatewayPen.Color))
            using (Brush pointInternet = new SolidBrush(internetPen.Color))
            using (Brush pointIpv6 = new SolidBrush(ipv6Pen.Color))
            {
                for (int i = 0; i <= 4; i++)
                {
                    float y = plot.Bottom - plot.Height * i / 4F;
                    e.Graphics.DrawLine(gridPen, plot.Left, y, plot.Right, y);
                    TextRenderer.DrawText(e.Graphics, (maximum * i / 4.0).ToString("0", CultureInfo.InvariantCulture), Font, new Point(5, (int)y - 7), LiquidGlassTheme.MutedText);
                }
                DrawSeries(e.Graphics, plot, maximum, gatewayPen, pointGateway, delegate(Sample s) { return s.GatewayLatencyMs; });
                DrawSeries(e.Graphics, plot, maximum, internetPen, pointInternet, delegate(Sample s) { return s.InternetLatencyMs; });
                DrawSeries(e.Graphics, plot, maximum, ipv6Pen, pointIpv6, delegate(Sample s) { return s.Ipv6LatencyMs; });
                int legend = Math.Max(55, ClientSize.Width - 230);
                TextRenderer.DrawText(e.Graphics, Localization.Get(Language, "LatencyChartGateway"), Font, new Point(legend, 4), gatewayPen.Color);
                TextRenderer.DrawText(e.Graphics, "IPv4", Font, new Point(legend + 80, 4), internetPen.Color);
                TextRenderer.DrawText(e.Graphics, "IPv6", Font, new Point(legend + 135, 4), ipv6Pen.Color);
            }
            if (samples.Count == 0) TextRenderer.DrawText(e.Graphics, Localization.Get(Language, "LatencyChartEmpty"), Font, plot, LiquidGlassTheme.MutedText, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
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

    internal sealed class GlassComboBox : UserControl
    {
        private readonly ComboBox combo;
        private const int CornerRadius = 10;

        public GlassComboBox()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            SetStyle(ControlStyles.Selectable, true);
            TabStop = true;
            AccessibleRole = AccessibleRole.ComboBox;
            AccessibleName = "Language";
            BackColor = Color.FromArgb(255, 20, 26, 36);
            ForeColor = LiquidGlassTheme.Text;
            combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, DrawMode = DrawMode.OwnerDrawFixed, FlatStyle = FlatStyle.Flat, IntegralHeight = false, ItemHeight = 22, BackColor = BackColor, ForeColor = ForeColor };
            combo.DrawItem += DrawGlassItem;
            combo.Visible = false;
            combo.DropDown += delegate
            {
                combo.Region = null;
                try { BeginInvoke(new MethodInvoker(delegate { GlassWindowEffects.ApplyRoundedComboPopup(combo.Handle); })); }
                catch { /* A native popup still opens even if Windows cannot apply its optional corner region. */ }
            };
            combo.DropDownClosed += delegate { combo.Visible = false; Focus(); Invalidate(); };
            combo.SelectedIndexChanged += delegate { Invalidate(); };
            Controls.Add(combo);
            LayoutChildren();
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            Control ancestor = Parent;
            while (ancestor != null && ancestor.BackColor.A == 0) ancestor = ancestor.Parent;
            Color surface = ancestor == null ? LiquidGlassTheme.Window : ancestor.BackColor;
            using (SolidBrush brush = new SolidBrush(surface)) e.Graphics.FillRectangle(brush, ClientRectangle);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (ClientSize.Width < 2 || ClientSize.Height < 2) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle bounds = new Rectangle(0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
            using (GraphicsPath path = RoundedPath(bounds, CornerRadius))
            using (SolidBrush fill = new SolidBrush(BackColor))
            using (Pen edge = new Pen(LiquidGlassTheme.Stroke))
            using (Pen sheen = new Pen(LiquidGlassTheme.Highlight))
            using (Pen divider = new Pen(Color.FromArgb(48, 221, 233, 249)))
            using (Pen chevron = new Pen(Enabled ? LiquidGlassTheme.SecondaryText : LiquidGlassTheme.MutedText, 1.6F))
            {
                e.Graphics.FillPath(fill, path);
                e.Graphics.DrawPath(edge, path);
                e.Graphics.DrawLine(sheen, bounds.Left + CornerRadius, bounds.Top + 1, bounds.Right - CornerRadius, bounds.Top + 1);
                int arrowX = bounds.Right - 30;
                int centerY = bounds.Height / 2;
                e.Graphics.DrawLine(divider, arrowX, 6, arrowX, bounds.Bottom - 6);
                e.Graphics.DrawLine(chevron, arrowX + 11, centerY - 2, arrowX + 15, centerY + 2);
                e.Graphics.DrawLine(chevron, arrowX + 15, centerY + 2, arrowX + 19, centerY - 2);
            }
            string selectedText = combo.SelectedIndex >= 0 ? combo.GetItemText(combo.SelectedItem) : Text;
            Rectangle textBounds = new Rectangle(13, 1, Math.Max(1, ClientSize.Width - 49), ClientSize.Height - 2);
            TextRenderer.DrawText(e.Graphics, selectedText, Font, textBounds,
                Enabled ? ForeColor : LiquidGlassTheme.MutedText,
                TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
        }

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            OpenDropDown();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter || e.KeyCode == Keys.Down)
            {
                OpenDropDown();
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
            base.OnKeyDown(e);
        }

        protected override bool IsInputKey(Keys keyData)
        {
            if (keyData == Keys.Space || keyData == Keys.Enter || keyData == Keys.Down) return true;
            return base.IsInputKey(keyData);
        }

        private void OpenDropDown()
        {
            if (!Enabled || combo.Items.Count == 0 || combo.DroppedDown) return;
            combo.Bounds = ClientRectangle;
            combo.Visible = true;
            combo.BringToFront();
            combo.Focus();
            combo.DroppedDown = true;
        }

        private static void DrawGlassItem(object sender, DrawItemEventArgs e)
        {
            if (e.Bounds.Width < 1 || e.Bounds.Height < 1) return;
            ComboBox combo = (ComboBox)sender;
            bool selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
            Color fill = selected ? Color.FromArgb(255, 35, 54, 78) : Color.FromArgb(255, 18, 23, 32);
            using (SolidBrush brush = new SolidBrush(fill)) e.Graphics.FillRectangle(brush, e.Bounds);
            string label = e.Index >= 0 ? combo.GetItemText(combo.Items[e.Index]) : combo.Text;
            TextRenderer.DrawText(e.Graphics, label, combo.Font,
                Rectangle.Inflate(e.Bounds, -8, 0), LiquidGlassTheme.Text,
                TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            if ((e.State & DrawItemState.Focus) == DrawItemState.Focus) e.DrawFocusRectangle();
        }

        private void LayoutChildren()
        {
            if (combo == null) return;
            combo.Bounds = ClientRectangle;
            combo.Visible = false;
            UpdateRoundedRegion();
        }

        private static GraphicsPath RoundedPath(Rectangle rect, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            int diameter = Math.Max(2, Math.Min(radius * 2, Math.Min(rect.Width, rect.Height)));
            path.AddArc(rect.Left, rect.Top, diameter, diameter, 180, 90);
            path.AddArc(rect.Right - diameter, rect.Top, diameter, diameter, 270, 90);
            path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(rect.Left, rect.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }

        protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); LayoutChildren(); }
        protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); if (combo != null) combo.Font = Font; }
        protected override void OnForeColorChanged(EventArgs e) { base.OnForeColorChanged(e); if (combo != null) combo.ForeColor = ForeColor; }
        protected override void OnBackColorChanged(EventArgs e) { base.OnBackColorChanged(e); if (combo != null) combo.BackColor = BackColor; }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }

        private void UpdateRoundedRegion()
        {
            if (ClientSize.Width < 2 || ClientSize.Height < 2) return;
            using (GraphicsPath path = RoundedPath(new Rectangle(0, 0, ClientSize.Width, ClientSize.Height), CornerRadius))
            {
                Region old = Region;
                Region = new Region(path);
                if (old != null) old.Dispose();
            }
        }

        public ComboBox.ObjectCollection Items { get { return combo.Items; } }
        public int SelectedIndex { get { return combo.SelectedIndex; } set { combo.SelectedIndex = value; Invalidate(); } }
        public object SelectedItem { get { return combo.SelectedItem; } }
        public ComboBoxStyle DropDownStyle { get { return combo.DropDownStyle; } set { combo.DropDownStyle = value; } }
        public FlatStyle FlatStyle { get { return combo.FlatStyle; } set { combo.FlatStyle = value; } }
        public bool IntegralHeight { get { return combo.IntegralHeight; } set { combo.IntegralHeight = value; } }
        public int ItemHeight { get { return combo.ItemHeight; } set { combo.ItemHeight = value; } }
        public event EventHandler SelectedIndexChanged { add { combo.SelectedIndexChanged += value; } remove { combo.SelectedIndexChanged -= value; } }

        public override string Text { get { return combo == null || combo.SelectedIndex < 0 ? base.Text : combo.GetItemText(combo.SelectedItem); } set { if (combo == null) base.Text = value; else { combo.Text = value; Invalidate(); } } }
    }

    internal sealed class GlassTextBox : UserControl
    {
        private readonly TextBox input;
        private const int CornerRadius = 11;

        public GlassTextBox()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.FromArgb(255, 16, 21, 30);
            ForeColor = LiquidGlassTheme.Text;
            Height = 36;
            TabStop = false;
            AccessibleRole = AccessibleRole.Text;
            input = new TextBox
            {
                BorderStyle = BorderStyle.None,
                BackColor = BackColor,
                ForeColor = ForeColor,
                Font = Font,
                Location = new Point(12, 7),
                Width = Math.Max(20, Width - 24),
                Height = Math.Max(20, Height - 14),
                AutoSize = false
            };
            input.GotFocus += delegate { Invalidate(); };
            input.LostFocus += delegate { Invalidate(); };
            Controls.Add(input);
            LayoutInput();
        }

        public override string Text { get { return input == null ? base.Text : input.Text; } set { if (input == null) base.Text = value; else input.Text = value; } }
        public bool UseSystemPasswordChar { get { return input.UseSystemPasswordChar; } set { input.UseSystemPasswordChar = value; } }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            SoftSurfacePanel parent = Parent as SoftSurfacePanel;
            if (parent != null) parent.PaintBackgroundForChild(e.Graphics, this);
            else base.OnPaintBackground(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (ClientSize.Width < 2 || ClientSize.Height < 2) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle bounds = new Rectangle(0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
            using (GraphicsPath path = RoundedPath(bounds, CornerRadius))
            using (LinearGradientBrush fill = new LinearGradientBrush(bounds, Color.FromArgb(255, 24, 31, 43), BackColor, 90F))
            using (Pen edge = new Pen(input.Focused ? Color.FromArgb(190, 131, 181, 255) : LiquidGlassTheme.Stroke, input.Focused ? 1.5F : 1F))
            using (Pen sheen = new Pen(Color.FromArgb(30, 255, 255, 255)))
            {
                e.Graphics.FillPath(fill, path);
                e.Graphics.DrawPath(edge, path);
                e.Graphics.DrawLine(sheen, bounds.Left + CornerRadius, bounds.Top + 1, bounds.Right - CornerRadius, bounds.Top + 1);
            }
        }

        protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); LayoutInput(); }
        protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); if (input != null) { input.Font = Font; LayoutInput(); } }
        protected override void OnForeColorChanged(EventArgs e) { base.OnForeColorChanged(e); if (input != null) input.ForeColor = ForeColor; }
        protected override void OnBackColorChanged(EventArgs e) { base.OnBackColorChanged(e); if (input != null) input.BackColor = BackColor; }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); if (input != null) input.Enabled = Enabled; Invalidate(); }

        private void LayoutInput()
        {
            if (input == null) return;
            input.Bounds = new Rectangle(12, 6, Math.Max(20, ClientSize.Width - 24), Math.Max(20, ClientSize.Height - 12));
        }

        private static GraphicsPath RoundedPath(Rectangle rect, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            int d = Math.Max(2, Math.Min(radius * 2, Math.Min(rect.Width, rect.Height)));
            path.AddArc(rect.Left, rect.Top, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Top, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.Left, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    internal sealed class LiquidBackdropPanel : Panel
    {
        public LiquidBackdropPanel()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = LiquidGlassTheme.Window;
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            if (ClientSize.Width < 2 || ClientSize.Height < 2) return;
            e.Graphics.SmoothingMode = SmoothingMode.HighQuality;
            Rectangle bounds = ClientRectangle;
            using (LinearGradientBrush baseLayer = new LinearGradientBrush(bounds, Color.FromArgb(8, 10, 14), Color.FromArgb(12, 17, 25), 25F))
                e.Graphics.FillRectangle(baseLayer, bounds);
            PaintGlow(e.Graphics, new Rectangle((int)(bounds.Width * .48), -(int)(bounds.Height * .18), (int)(bounds.Width * .62), (int)(bounds.Height * .70)), Color.FromArgb(29, 34, 57, 91));
            PaintGlow(e.Graphics, new Rectangle(-(int)(bounds.Width * .28), (int)(bounds.Height * .52), (int)(bounds.Width * .62), (int)(bounds.Height * .62)), Color.FromArgb(15, 30, 55, 76));
        }

        private static void PaintGlow(Graphics graphics, Rectangle rect, Color center)
        {
            if (rect.Width < 2 || rect.Height < 2) return;
            using (GraphicsPath path = new GraphicsPath())
            {
                path.AddEllipse(rect);
                using (PathGradientBrush glow = new PathGradientBrush(path))
                {
                    glow.CenterColor = center;
                    glow.SurroundColors = new Color[] { Color.FromArgb(0, center.R, center.G, center.B) };
                    graphics.FillEllipse(glow, rect);
                }
            }
        }
    }

    internal sealed class SoftSurfacePanel : Panel
    {
        public Color BorderColor = LiquidGlassTheme.Stroke;
        public int CornerRadius = 22;

        public SoftSurfacePanel()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            PaintSurface(e.Graphics);
        }

        internal void PaintBackgroundForChild(Graphics graphics, Control child)
        {
            GraphicsState state = graphics.Save();
            try
            {
                graphics.TranslateTransform(-child.Left, -child.Top);
                using (SolidBrush baseLayer = new SolidBrush(LiquidGlassTheme.Window))
                    graphics.FillRectangle(baseLayer, ClientRectangle);
                PaintSurface(graphics);
            }
            finally { graphics.Restore(state); }
        }

        private void PaintSurface(Graphics graphics)
        {
            if (ClientSize.Width < 2 || ClientSize.Height < 2) return;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle bounds = new Rectangle(0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
            using (System.Drawing.Drawing2D.GraphicsPath path = new System.Drawing.Drawing2D.GraphicsPath())
            using (LinearGradientBrush fill = new LinearGradientBrush(bounds, LiquidGlassTheme.GlassTop, LiquidGlassTheme.GlassBottom, 90F))
            using (Pen pen = new Pen(BorderColor, 1F))
            using (Pen highlight = new Pen(LiquidGlassTheme.Highlight, 1F))
            {
                int diameter = Math.Min(Math.Max(2, CornerRadius * 2), Math.Min(bounds.Width, bounds.Height));
                path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
                path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
                path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
                path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
                path.CloseFigure();
                graphics.FillPath(fill, path);
                graphics.DrawPath(pen, path);
                graphics.DrawLine(highlight, bounds.Left + CornerRadius, bounds.Top + 1, bounds.Right - CornerRadius, bounds.Top + 1);
            }
        }
    }

    internal static class DiagnosticEngine
    {
        public static string Analyze(IList<Sample> samples, AppLanguage language)
        {
            if (language == AppLanguage.Romanian) return Analyze(samples);
            if (samples == null || samples.Count == 0) return Localization.Get(language, "NoSamples");
            int wifiDrops = samples.Count(NetworkProbe.HasWifiIssue);
            int weakSignal = samples.Count(delegate(Sample s) { return IsWifiRelevantSample(s) && s.WifiSignalPercent.HasValue && s.WifiSignalPercent.Value < 35; });
            int dnsFailures = samples.Count(delegate(Sample s) { return s.DnsStatus == "AllFailed" || s.DnsStatus == "Failed" || s.DnsStatus == "Timeout"; });
            int dnsDisagreements = samples.Count(delegate(Sample s) { return s.DnsSucceeded.HasValue && s.DnsAttempted.HasValue && s.DnsSucceeded.Value > 0 && s.DnsSucceeded.Value < s.DnsAttempted.Value; });
            int gatewayFailures = samples.Count(delegate(Sample s) { return s.GatewayPingStatus != "Success" && s.GatewayPingStatus != "Unavailable" && s.GatewayPingStatus != "NoGateway"; });
            int gatewayAttempts = samples.Count(delegate(Sample s) { return s.GatewayPingStatus != "Unavailable" && s.GatewayPingStatus != "NoGateway"; });
            int outages = samples.Count(delegate(Sample s) { return ExternalSuccesses(s) == 0 && s.HttpsStatus == "AllFailed" && !s.CaptivePortalSuspected; });
            int portalSuspicions = samples.Count(delegate(Sample s) { return s.CaptivePortalSuspected; });
            int proxySamples = samples.Count(delegate(Sample s) { return s.HttpsUsedProxy; });
            int gatewaySpikes = samples.Count(delegate(Sample s) { return HasEvent(s, "GatewayLatencySpike"); });
            int internetSpikes = samples.Count(delegate(Sample s) { return HasEvent(s, "InternetLatencySpike"); });
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
            b.AppendLine(String.Format(CultureInfo.InvariantCulture, "Measured averages: active Wi-Fi signal {0}; gateway ICMP {1}; IPv4 ICMP {2}.", Format(signal, "%"), Format(gateway, " ms"), Format(internet, " ms")));
            b.AppendLine("Active Windows route interface: " + FormatInterfaceStats(samples, AppLanguage.English) + ". Tunnel identifies an OS tunnel-type interface; VPN products may also appear as another type. Interface indices are used only in memory.");
            b.AppendLine("Internet targets: " + FormatInternetTargetStats(samples) + ". These independent public IP probes are logged by service name; target IPs are not stored.");
            b.AppendLine("Passive traffic on the active gateway interface: average download " + Format(download, " Mbps") + "; upload " + Format(upload, " Mbps") + ". This is current device traffic, not the connection's maximum speed.");
            b.AppendLine("Successful ICMP latency statistics: gateway " + FormatLatencyStatistics(samples.Select(delegate(Sample s) { return s.GatewayLatencyMs; })) + "; IPv4 " + FormatLatencyStatistics(samples.Select(delegate(Sample s) { return s.InternetLatencyMs; })) + "; IPv6 " + FormatLatencyStatistics(samples.Select(delegate(Sample s) { return s.Ipv6LatencyMs; })) + ". Jitter is mean absolute change between consecutive successful samples.");
            b.AppendLine(FormatLoadComparison(samples, AppLanguage.English));
            b.AppendLine(); b.AppendLine("FINDINGS AND INTERPRETATION");
            int findings = 0;
            if (wifiDrops > 0) { b.AppendLine(String.Format(CultureInfo.InvariantCulture, "- Wi-Fi disconnected in {0} sample(s). Windows reported the wireless interface as disconnected. This confirms a disconnect during the capture, but not its physical cause.", wifiDrops)); findings++; }
            if (weakSignal >= 3) { b.AppendLine(String.Format(CultureInfo.InvariantCulture, "- Weak Wi-Fi signal: {0} samples were below 35%. Low signal can contribute to local packet loss; it does not measure radio interference.", weakSignal)); findings++; }
            if (gatewaySpikes > 0) { b.AppendLine(String.Format(CultureInfo.InvariantCulture, "- Relative gateway latency spike: {0} sample(s) rose at least 50 ms or 50% above the recent baseline. A high but stable RTT is not called a spike.", gatewaySpikes)); findings++; }
            if (internetSpikes > 0) { b.AppendLine(String.Format(CultureInfo.InvariantCulture, "- Relative IPv4 latency spike: {0} sample(s) rose at least 50 ms or 50% above the recent baseline. This does not identify the responsible network or server.", internetSpikes)); findings++; }
            if (gatewayAttempts >= 4 && 100.0 * gatewayFailures / gatewayAttempts >= 20.0) { b.AppendLine(String.Format(CultureInfo.InvariantCulture, "- Gateway ICMP non-response: {0:0.0}% ({1}/{2} probes). This is not proof of packet loss: some routers filter or deprioritize ping replies.", 100.0 * gatewayFailures / gatewayAttempts, gatewayFailures, gatewayAttempts)); findings++; }
            if (dnsFailures > 0) { b.AppendLine(String.Format(CultureInfo.InvariantCulture, "- All three test names failed to resolve in {0} DNS check(s). If IP connectivity worked at the same time, DNS becomes a stronger suspect. Windows' resolver is used, once per minute.", dnsFailures)); findings++; }
            if (dnsDisagreements > 0) { b.AppendLine(String.Format(CultureInfo.InvariantCulture, "- DNS results differed across Microsoft, Google, and Cloudflare domains in {0} check(s). This may be a domain-specific issue; it does not confirm a general DNS failure.", dnsDisagreements)); findings++; }
            if (outages > 0) { b.AppendLine(String.Format(CultureInfo.InvariantCulture, "- Internet interruption suspected in {0} sample(s): all IPv4/IPv6 ICMP targets and all three HTTPS endpoint checks failed together. This is evidence of broad reachability trouble, not proof of an ISP fault; firewall, proxy, captive portal, or filtering may also affect probes.", outages)); findings++; }
            int icmpOnly = samples.Count(delegate(Sample s) { return ExternalSuccesses(s) == 0 && s.HttpsStatus == "Success"; });
            if (icmpOnly > 0) { b.AppendLine(String.Format(CultureInfo.InvariantCulture, "- No IPv4/IPv6 ICMP target replied in {0} sample(s), while HTTPS did. Internet access was still available; ICMP may be filtered, or the working HTTPS path may use IPv6 or a proxy.", icmpOnly)); findings++; }
            int partialHttps = samples.Count(delegate(Sample s) { return s.HttpsSucceeded.HasValue && s.HttpsAttempted.HasValue && s.HttpsSucceeded.Value > 0 && s.HttpsSucceeded.Value < s.HttpsAttempted.Value; });
            if (partialHttps > 0) { b.AppendLine(String.Format(CultureInfo.InvariantCulture, "- HTTPS endpoint difference: {0} sample(s) had a mix of successful and unsuccessful endpoints. The responding endpoints confirm some HTTPS access; one failed endpoint does not mean the whole connection is degraded.", partialHttps)); findings++; }
            if (portalSuspicions > 0) { b.AppendLine(String.Format(CultureInfo.InvariantCulture, "- Captive portal or HTTPS interception suspected in {0} sample(s). Multiple endpoints returned redirects or unexpected HTML; a proxy/security gateway can cause the same result, so this is not confirmation.", portalSuspicions)); findings++; }
            if (proxySamples > 0) b.AppendLine(String.Format(CultureInfo.InvariantCulture, "- HTTPS checks used the Windows-configured proxy in {0} sample(s); this may differ from direct game traffic.", proxySamples));
            if (portalSuspicions > 0) { b.AppendLine(String.Format(CultureInfo.InvariantCulture, "- Captive portal or HTTPS interception suspected in {0} sample(s). Multiple endpoints returned redirects or unexpected HTML; a portal is not confirmed because a proxy/security gateway can do the same.", portalSuspicions)); findings++; }
            if (proxySamples > 0) b.AppendLine(String.Format(CultureInfo.InvariantCulture, "- HTTPS checks used the Windows-configured proxy in {0} sample(s); this may differ from direct game traffic.", proxySamples));
            if (findings == 0) b.AppendLine("- No interruption or threshold-level anomaly was detected in the captured period. This cannot rule out problems outside the session.");
            b.AppendLine(); b.AppendLine("WHAT TO DO NEXT");
            if (gatewayFailures > 0 || weakSignal > 0 || wifiDrops > 0) b.AppendLine("- If the issue repeats, test near the router and compare another device on the same Wi-Fi. If both are affected, restart the router once and check its status lights.");
            if (outages > 0 && gatewayFailures == 0) b.AppendLine("- If the gateway stays reachable while all external checks fail, compare a second device and check the router/modem WAN status. Contact the ISP with the time window if multiple devices are affected.");
            if (dnsFailures > 0) b.AppendLine("- If websites fail by name while other Internet checks work, retry DNS and compare another device before changing DNS settings.");
            if (findings == 0) b.AppendLine("- If the problem returns, run the monitor during the issue and keep the generated CSV for comparison.");
            b.AppendLine();
            b.AppendLine(String.Format(CultureInfo.InvariantCulture, "Independent HTTPS checks: {0}/{1} successful. Microsoft, Google, and Cloudflare are checked at least once per minute and sooner when fewer than two IP targets reply.", httpsSuccess, httpsAttempts));
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
            int wifiDrops = samples.Count(NetworkProbe.HasWifiIssue);
            int weakSignal = samples.Count(delegate(Sample s) { return IsWifiRelevantSample(s) && s.WifiSignalPercent.HasValue && s.WifiSignalPercent.Value < 35; });
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
                return ExternalSuccesses(s) == 0 && s.HttpsStatus == "AllFailed" && !s.CaptivePortalSuspected;
            });
            int portalSuspicions = samples.Count(delegate(Sample s) { return s.CaptivePortalSuspected; });
            int proxySamples = samples.Count(delegate(Sample s) { return s.HttpsUsedProxy; });
            int httpsOnly = samples.Count(delegate(Sample s)
            {
                return ExternalSuccesses(s) == 0 && s.HttpsStatus == "Success";
            });
            int gatewaySpikes = samples.Count(delegate(Sample s) { return HasEvent(s, "GatewayLatencySpike"); });
            int internetSpikes = samples.Count(delegate(Sample s) { return HasEvent(s, "InternetLatencySpike"); });
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
                    "Gateway: {0:0.0}% dintre probele ICMP nu au primit raspuns ({1}/{2}). Nu este dovada directa de pierdere a traficului; unele routere filtreaza ping-ul.", gatewayLoss, gatewayFailures, gatewayAttempts));

            if (gatewaySpikes > 0)
                findings.Add(String.Format(CultureInfo.InvariantCulture,
                    "Spike relativ local: {0} proba(e) catre gateway au depasit mediana recenta cu cel putin 50 ms sau 50%. Incredere medie; corelatia cu semnalul si evenimentele externe conteaza.", gatewaySpikes));

            if (internetSpikes > 0 && gatewaySpikes == 0)
                findings.Add(String.Format(CultureInfo.InvariantCulture,
                    "Spike relativ IPv4: {0} proba(e) au depasit mediana recenta cu cel putin 50 ms sau 50%, fara spike simultan catre gateway. Indiciul nu identifica singur furnizorul. Incredere medie.", internetSpikes));

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

            if (portalSuspicions > 0)
                findings.Add(String.Format(CultureInfo.InvariantCulture, "Portal captiv sau interceptare HTTPS suspectata in {0} esantion(e): cel putin doua endpointuri au raspuns cu redirectionari sau HTML neasteptat. Nu este confirmare; proxy-ul sau gateway-ul de securitate poate produce acelasi rezultat.", portalSuspicions));

            if (proxySamples > 0)
                findings.Add(String.Format(CultureInfo.InvariantCulture, "Probe HTTPS prin proxy-ul configurat in Windows: {0} esantion(e). Aceste rezultate descriu calea prin proxy si pot diferi de traficul direct al jocului.", proxySamples));

            if (internetOutages > 0)
            {
                bool gatewayMostlyOkay = gatewayAttempts > 0 && gatewayLoss < 20.0;
                string likelyArea = gatewayMostlyOkay
                    ? "Gateway-ul a raspuns in general; intreruperea pare dincolo de router sau serviciul de test."
                    : "Verifica separat legatura Wi-Fi/gateway; probele singure nu separa sigur routerul de furnizor.";
                findings.Add(String.Format(CultureInfo.InvariantCulture,
                    "Acces Internet suspectat intrerupt: toate tintele ICMP IPv4/IPv6 si cele trei endpointuri HTTPS au esuat impreuna in {0} esantion(e). {1} Incredere medie; firewall-ul, proxy-ul, portalul captiv sau filtrarea pot afecta probele. Verifica daca alte dispozitive au aceeasi problema.", internetOutages, likelyArea));
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
            int wifiDrops = samples.Count(NetworkProbe.HasWifiIssue);
            int routeChanges = samples.Count(delegate(Sample sample) { return sample.ActiveInterfaceChanged; });
            int weakSignal = samples.Count(delegate(Sample sample) { return sample.WifiSignalPercent.HasValue && sample.WifiSignalPercent.Value < 35; });
            int gatewayLossWithExternalFailure = samples.Count(delegate(Sample sample)
            {
                return IsGatewayFailure(sample) && ExternalSuccesses(sample) == 0 && sample.HttpsStatus != "Success";
            });
            int externalOutage = samples.Count(delegate(Sample sample) { return ExternalSuccesses(sample) == 0 && sample.HttpsStatus == "AllFailed" && !sample.CaptivePortalSuspected; });
            int portalSuspicions = samples.Count(delegate(Sample sample) { return sample.CaptivePortalSuspected; });
            int ipv6OnlySamples = samples.Count(delegate(Sample sample) { return ExternalSuccesses(sample) > 0 &&
                sample.InternetTargetResults != null && sample.InternetTargetResults.All(delegate(InternetTargetObservation result) { return result == null || result.Status != "Success"; }) &&
                sample.Ipv6TargetResults != null && sample.Ipv6TargetResults.Any(delegate(InternetTargetObservation result) { return result != null && result.Status == "Success"; }); });
            int dnsFailureWithInternet = samples.Count(delegate(Sample sample)
            {
                return (sample.DnsStatus == "AllFailed" || sample.DnsStatus == "Failed" || sample.DnsStatus == "Timeout") &&
                    (ExternalSuccesses(sample) > 0 || sample.HttpsStatus == "Success");
            });
            int destinationDisagreement = samples.Count(delegate(Sample sample)
            {
                bool ipv4Disagrees = sample.InternetTargetResults != null &&
                    sample.InternetTargetResults.Count(delegate(InternetTargetObservation result) { return result != null && result.Status != "Unavailable"; }) > 1 &&
                    sample.InternetTargetResults.Any(delegate(InternetTargetObservation result) { return result != null && result.Status == "Success"; }) &&
                    sample.InternetTargetResults.Any(delegate(InternetTargetObservation result) { return result != null && result.Status != "Success" && result.Status != "Unavailable"; });
                bool ipv6Disagrees = sample.Ipv6TargetResults != null &&
                    sample.Ipv6TargetResults.Count(delegate(InternetTargetObservation result) { return result != null && result.Status != "Unavailable"; }) > 1 &&
                    sample.Ipv6TargetResults.Any(delegate(InternetTargetObservation result) { return result != null && result.Status == "Success"; }) &&
                    sample.Ipv6TargetResults.Any(delegate(InternetTargetObservation result) { return result != null && result.Status != "Success" && result.Status != "Unavailable"; });
                return ipv4Disagrees || ipv6Disagrees;
            });
            int gatewayAttempts = samples.Count(IsGatewayAttempt);
            int gatewayFailures = samples.Count(IsGatewayFailure);
            int upstreamLatencyOnly = samples.Count(delegate(Sample sample)
            {
                return HasEvent(sample, "InternetLatencySpike") && !HasEvent(sample, "GatewayLatencySpike");
            });

            if (wifiDrops >= 2)
                return ro ? String.Format(CultureInfo.InvariantCulture, "Wi-Fi: Windows a raportat deconectarea in {0} esantioane. Incredere ridicata doar pentru deconectarea observata; cauza ei nu este stabilita.", wifiDrops)
                    : String.Format(CultureInfo.InvariantCulture, "Wi-Fi: Windows reported a disconnect in {0} samples. High confidence only that the disconnect was observed; its cause is not established.", wifiDrops);
            if (routeChanges > 0)
                return ro ? String.Format(CultureInfo.InvariantCulture, "Ruta Windows a trecut pe alta interfata in {0} esantion(e). Verifica daca schimbarea coincide cu problema; nu identifica singura cauza. Incredere scazuta spre medie.", routeChanges)
                    : String.Format(CultureInfo.InvariantCulture, "Windows switched the active route interface in {0} sample(s). Check whether the switch coincides with the issue; it does not identify the cause by itself. Low-to-medium confidence.", routeChanges);
            if (portalSuspicions > 0)
                return ro ? String.Format(CultureInfo.InvariantCulture, "Portal captiv sau interceptare HTTPS suspectata in {0} esantioane: mai multe verificari au primit redirectionari sau HTML neasteptat. Poate fi si proxy/gateway de securitate; verifica daca apare o pagina de autentificare.", portalSuspicions)
                    : String.Format(CultureInfo.InvariantCulture, "Captive portal or HTTPS interception suspected in {0} samples: multiple checks returned redirects or unexpected HTML. A proxy/security gateway can do the same; check for a sign-in page.", portalSuspicions);
            if (ipv6OnlySamples >= 1)
                return ro ? String.Format(CultureInfo.InvariantCulture, "Accesul de test a reusit prin IPv6, dar tintele ICMP IPv4 nu au raspuns in {0} esantioane. Acest lucru nu inseamna ca internetul este cazut; un serviciu strict IPv4 poate avea alta cale.", ipv6OnlySamples)
                    : String.Format(CultureInfo.InvariantCulture, "Test access succeeded over IPv6 while the IPv4 ICMP targets did not reply in {0} samples. This does not mean the Internet is down; an IPv4-only service can follow a different path.", ipv6OnlySamples);
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

        private static bool IsWifiRelevantSample(Sample sample)
        {
            return sample != null && (sample.WifiRelevant || String.Equals(sample.ActiveInterfaceType, "Wi-Fi", StringComparison.OrdinalIgnoreCase));
        }

        private static bool HasEvent(Sample sample, string code)
        {
            return sample != null && (sample.Events ?? "").Split(new char[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Contains(code, StringComparer.Ordinal);
        }

        private static bool IsGatewayFailure(Sample sample)
        {
            return IsGatewayAttempt(sample) && sample.GatewayPingStatus != "Success";
        }

        private static int ExternalSuccesses(Sample sample)
        {
            return NetworkProbe.CountExternalSuccesses(sample);
        }

        private static int ExternalAttempts(Sample sample)
        {
            int ipv4 = sample.InternetTargetResults != null && sample.InternetTargetResults.Count > 0
                ? sample.InternetTargetResults.Count(delegate(InternetTargetObservation result) { return result != null && result.Status != "Unavailable"; }) :
                (sample.InternetPingStatus == "Unavailable" || sample.InternetPingStatus == "NotChecked" ? 0 : 1);
            int ipv6 = sample.Ipv6TargetResults == null ? 0 :
                sample.Ipv6TargetResults.Count(delegate(InternetTargetObservation result) { return result != null && result.Status != "Unavailable"; });
            return ipv4 + ipv6;
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
                ? String.Format(CultureInfo.InvariantCulture, "Comparatie observata (nu test controlat): mediana latenței ICMP IPv4 a fost {0:0.0} ms linistit ({1} probe) si {2:0.0} ms cand traficul acestui PC a depasit 1 Mbps ({3} probe), diferenta {4:+0.0;-0.0;0.0} ms.{5} Corelatia nu dovedeste bufferbloat si nu include traficul altor dispozitive.", comparison.QuietMedianMs.Value, comparison.QuietSamples, comparison.ActiveMedianMs.Value, comparison.ActiveSamples, delta, localComparison)
                : String.Format(CultureInfo.InvariantCulture, "Observed comparison (not a controlled test): IPv4 ICMP median was {0:0.0} ms during quiet periods ({1} samples) and {2:0.0} ms while this PC exceeded 1 Mbps traffic ({3} samples), a {4:+0.0;-0.0;0.0} ms difference.{5} Correlation does not prove bufferbloat and excludes traffic from other devices.", comparison.QuietMedianMs.Value, comparison.QuietSamples, comparison.ActiveMedianMs.Value, comparison.ActiveSamples, delta, localComparison);
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
            string[] types = new string[] { "Wi-Fi", "Ethernet", "Tunnel", "Other", "Unavailable" };
            return String.Join(", ", types.Select(delegate(string type)
            {
                int count = samples.Count(delegate(Sample sample) { return String.Equals(sample.ActiveInterfaceType ?? "Unavailable", type, StringComparison.OrdinalIgnoreCase); });
                return type + " " + count.ToString(CultureInfo.InvariantCulture);
            }).ToArray());
        }

        private static string FormatInternetTargetStats(IList<Sample> samples)
        {
            string[] names = new string[] { "Cloudflare", "Google", "Quad9", "Cloudflare IPv6", "Google IPv6", "Quad9 IPv6" };
            List<string> formatted = new List<string>();
            foreach (string name in names)
            {
                int attempted = 0;
                int succeeded = 0;
                List<double?> latency = new List<double?>();
                foreach (Sample sample in samples)
                {
                    IList<InternetTargetObservation> targets = name.EndsWith("IPv6", StringComparison.Ordinal)
                        ? sample.Ipv6TargetResults : sample.InternetTargetResults;
                    if (targets == null) continue;
                    foreach (InternetTargetObservation result in targets.Where(delegate(InternetTargetObservation item) { return item != null && item.Name == name; }))
                    {
                        if (result.Status == "Unavailable") continue;
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
        private const string CsvHeader = "TimestampLocal,WifiState,WifiSignalPercent,ActiveInterfaceType,GatewayPingStatus,GatewayLatencyMs,GatewayIcmpNonResponsePctRolling60s,InternetPingStatus,InternetIpv4LatencyMs,InternetIpv4IcmpNonResponsePctRolling60s,InternetIpv4Targets,InternetIpv4TargetsSucceeded,InternetIpv4TargetsAttempted,InternetIpv6LatencyMs,InternetIpv6Targets,InternetIpv6TargetsSucceeded,InternetIpv6TargetsAttempted,HttpsStatus,HttpsLatencyMs,HttpsEndpoints,HttpsSucceeded,HttpsAttempted,HttpsUsedWindowsProxy,CaptivePortalSuspected,DnsStatus,DnsLatencyMs,DnsEndpoints,DnsSucceeded,DnsAttempted,Connectivity,Events,NetworkDownloadMbps,NetworkUploadMbps";
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
                File.AppendAllText(CsvPath, FormatSampleRow(sample) + "\r\n", Encoding.UTF8);
            }
        }

        internal static string FormatSampleRow(Sample sample)
        {
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
                    NetworkProbe.FormatInternetTargets(sample.InternetTargetResults, sample.Ipv6TargetResults, AppLanguage.English),
                    sample.InternetTargetSucceeded.HasValue ? sample.InternetTargetSucceeded.Value.ToString(CultureInfo.InvariantCulture) : "",
                    sample.InternetTargetAttempted.HasValue ? sample.InternetTargetAttempted.Value.ToString(CultureInfo.InvariantCulture) : "",
                    Number(sample.Ipv6LatencyMs),
                    NetworkProbe.FormatInternetTargets(sample.Ipv6TargetResults, AppLanguage.English),
                    sample.Ipv6TargetResults == null ? "" : sample.Ipv6TargetResults.Count(delegate(InternetTargetObservation result) { return result.Status == "Success"; }).ToString(CultureInfo.InvariantCulture),
                    sample.Ipv6TargetResults == null ? "" : sample.Ipv6TargetResults.Count(delegate(InternetTargetObservation result) { return result.Status != "Unavailable"; }).ToString(CultureInfo.InvariantCulture),
                    sample.HttpsStatus,
                    Number(sample.HttpsLatencyMs),
                    sample.HttpsEndpointResults,
                    sample.HttpsSucceeded.HasValue ? sample.HttpsSucceeded.Value.ToString(CultureInfo.InvariantCulture) : "",
                    sample.HttpsAttempted.HasValue ? sample.HttpsAttempted.Value.ToString(CultureInfo.InvariantCulture) : "",
                    sample.HttpsUsedProxy ? "true" : "false",
                    sample.CaptivePortalSuspected ? "true" : "false",
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
            return String.Join(",", cells.Select(CsvEscape).ToArray());
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
        internal static int CsvColumnCount { get { return CsvHeader.Split(',').Length; } }

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
        private readonly GlassButton startStopButton;
        private readonly GlassButton openLogsButton;
        private readonly GlassButton copyReportButton;
        private readonly GlassButton routeTraceButton;
        private readonly GlassButton aiButton;
        private readonly GlassButton disconnectAiButton;
        private readonly GlassButton aiSettingsButton;
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
        private readonly GlassComboBox languageSelector;
        private readonly Label headingLabel;
        private readonly Label footerLabel;
        private readonly Label aiStatusLabel;
        private readonly SoftSurfacePanel diagnosisGroup;
        private readonly Label diagnosisHeading;
        private readonly Label overviewSummary;
        private readonly Label viewTitle;
        private readonly GlassButton monitorViewButton;
        private readonly GlassButton reportViewButton;
        private readonly Panel monitorPage;
        private readonly Panel reportPage;
        private readonly Panel viewHost;
        private readonly Panel monitorStack;
        private readonly TableLayoutPanel metricsLayout;
        private readonly Label wifiTitle;
        private readonly Label signalTitle;
        private readonly Label gatewayTitle;
        private readonly Label internetTitle;
        private readonly Label dnsTitle;
        private readonly Label downloadTitle;
        private readonly Label uploadTitle;
        private readonly Label reportIntro;
        private readonly Label reportEyebrow;
        private AppLanguage language = AppLanguage.English;
        private readonly object samplesGate = new object();
        private readonly List<Sample> samples = new List<Sample>();
        private System.Threading.Timer timer;
        private int sampling;
        private long lastStartStopActionTicks;
        private volatile bool monitoring;
        private bool awaitingInitialSample;
        private DateTimeOffset nextDnsCheck;

        public MainForm()
        {
            try { this.Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch { }
            Text = Localization.Get(language, "AppTitle");
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(980, 700);
            ClientSize = new Size(1260, 860);
            Font = new Font("Segoe UI", 9F);
            BackColor = LiquidGlassTheme.Window;

            Panel header = new Panel { Dock = DockStyle.Top, Height = 118, BackColor = Color.White, Padding = new Padding(26, 12, 24, 0) };
            Controls.Add(header);

            headingLabel = new Label { Text = Localization.Get(language, "Heading"), Font = new Font("Segoe UI Semibold", 18F, FontStyle.Bold), AutoSize = true, Location = new Point(26, 14), ForeColor = Color.FromArgb(28, 31, 36) };
            header.Controls.Add(headingLabel);
            Label headerSubtitle = new Label { Text = language == AppLanguage.Romanian ? "Monitorizare clară. Pași practici." : "Clear monitoring. Practical next steps.", Font = new Font("Segoe UI", 9.5F), AutoSize = true, Location = new Point(28, 48), ForeColor = Color.FromArgb(111, 118, 128) };
            header.Controls.Add(headerSubtitle);

            languageSelector = new GlassComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Size = new Size(120, 30), Anchor = AnchorStyles.Top | AnchorStyles.Right };
            languageSelector.Items.Add(Localization.Get(AppLanguage.English, "English"));
            languageSelector.Items.Add(Localization.Get(AppLanguage.Romanian, "Romanian"));
            languageSelector.SelectedIndex = 0;
            header.Controls.Add(languageSelector);
            languageSelector.SelectedIndexChanged += LanguageChanged;
            header.Resize += delegate { languageSelector.Location = new Point(Math.Max(20, header.ClientSize.Width - languageSelector.Width - 26), 22); };
            languageSelector.Location = new Point(Math.Max(20, header.ClientSize.Width - languageSelector.Width - 26), 22);

            FlowLayoutPanel tools = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 48, WrapContents = false, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(0, 5, 0, 7), BackColor = Color.White };
            header.Controls.Add(tools);

            routeTraceButton = CreateToolbarButton(Localization.Get(language, "RouteTraceButton"), 132);
            routeTraceButton.Click += RouteTraceClicked;
            tools.Controls.Add(routeTraceButton);
            openLogsButton = CreateToolbarButton(Localization.Get(language, "OpenLogs"), 118);
            openLogsButton.Click += OpenLogsClicked;
            tools.Controls.Add(openLogsButton);
            copyReportButton = CreateToolbarButton(Localization.Get(language, "CopyReport"), 132);
            copyReportButton.Click += CopyReportClicked;
            tools.Controls.Add(copyReportButton);
            aiSettingsButton = CreateToolbarButton(Localization.Get(language, "AISettings"), 130);
            aiSettingsButton.Click += AiSettingsClicked;
            tools.Controls.Add(aiSettingsButton);
            aiButton = CreateToolbarButton(Localization.Get(language, "ContinueChatGPT"), 184);
            aiButton.Click += AiButtonClicked;
            tools.Controls.Add(aiButton);
            disconnectAiButton = CreateToolbarButton(Localization.Get(language, "DisconnectChatGPT"), 142);
            disconnectAiButton.Click += DisconnectAiClicked;
            tools.Controls.Add(disconnectAiButton);
            aiStatusLabel = new Label { AutoSize = false, Size = new Size(240, 30), TextAlign = ContentAlignment.MiddleLeft, Font = new Font("Segoe UI", 8.5F), ForeColor = Color.FromArgb(111, 118, 128), Margin = new Padding(6, 4, 0, 0), AutoEllipsis = true };
            tools.Controls.Add(aiStatusLabel);

            Panel navigation = new Panel { Dock = DockStyle.Top, Height = 48, BackColor = Color.White, Padding = new Padding(20, 4, 20, 0) };
            Controls.Add(navigation);
            viewTitle = new Label { Text = language == AppLanguage.Romanian ? "Monitor" : "Monitor", AutoSize = true, Font = new Font("Segoe UI Semibold", 10F, FontStyle.Bold), ForeColor = Color.FromArgb(112, 118, 128), Location = new Point(26, 14) };
            navigation.Controls.Add(viewTitle);
            monitorViewButton = CreateNavigationButton(language == AppLanguage.Romanian ? "Prezentare" : "Overview");
            monitorViewButton.Location = new Point(128, 6);
            monitorViewButton.Click += delegate { ShowMonitorView(); };
            navigation.Controls.Add(monitorViewButton);
            reportViewButton = CreateNavigationButton(language == AppLanguage.Romanian ? "Diagnostic" : "Diagnosis");
            reportViewButton.Location = new Point(238, 6);
            reportViewButton.Click += delegate { ShowReportView(); };
            navigation.Controls.Add(reportViewButton);

            footerLabel = new Label { Text = Localization.Get(language, "Footer"), Dock = DockStyle.Bottom, Height = 28, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(26, 0, 0, 0), ForeColor = Color.FromArgb(112, 118, 128), BackColor = Color.White, Font = new Font("Segoe UI", 8F) };
            Controls.Add(footerLabel);

            viewHost = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(246, 247, 249) };
            Controls.Add(viewHost);
            viewHost.BringToFront();

            monitorPage = new LiquidBackdropPanel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = LiquidGlassTheme.Window };
            reportPage = new LiquidBackdropPanel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = LiquidGlassTheme.Window, Visible = false };
            viewHost.Controls.Add(monitorPage);
            viewHost.Controls.Add(reportPage);

            monitorStack = new Panel { Location = new Point(24, 18), Height = 830, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right, BackColor = Color.Transparent };
            monitorPage.Controls.Add(monitorStack);
            monitorPage.Resize += delegate { monitorStack.Width = Math.Max(500, monitorPage.ClientSize.Width - 48 - (monitorPage.VerticalScroll.Visible ? SystemInformation.VerticalScrollBarWidth : 0)); };
            monitorStack.Width = ClientSize.Width - 60;

            SoftSurfacePanel hero = new SoftSurfacePanel { Location = new Point(0, 0), Size = new Size(monitorStack.Width, 144), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right, Padding = new Padding(24) };
            monitorStack.Controls.Add(hero);
            Label heroEyebrow = new Label { Text = language == AppLanguage.Romanian ? "STAREA CONEXIUNII" : "CONNECTION STATUS", AutoSize = true, Font = new Font("Segoe UI Semibold", 8F, FontStyle.Bold), ForeColor = Color.FromArgb(104, 112, 124), Location = new Point(24, 19) };
            hero.Controls.Add(heroEyebrow);
            statusLabel = new Label { Text = Localization.Get(language, "Stopped"), AutoSize = true, Font = new Font("Segoe UI Semibold", 18F, FontStyle.Bold), ForeColor = Color.FromArgb(30, 35, 43), Location = new Point(24, 43), AutoEllipsis = true };
            hero.Controls.Add(statusLabel);
            overviewSummary = new Label { Text = language == AppLanguage.Romanian ? "Pornește monitorizarea când apare problema. Diagnosticul local nu necesită cont sau cheie AI." : "Start monitoring when the issue happens. Local diagnosis needs no account or AI key.", Location = new Point(26, 82), Size = new Size(680, 42), Font = new Font("Segoe UI", 9.5F), ForeColor = Color.FromArgb(102, 110, 121), AutoEllipsis = true };
            hero.Controls.Add(overviewSummary);
            startStopButton = CreatePrimaryButton(Localization.Get(language, "Start"), 192, 42);
            startStopButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            startStopButton.Click += StartStopClicked;
            hero.Controls.Add(startStopButton);
            hero.Resize += delegate { startStopButton.Location = new Point(Math.Max(24, hero.ClientSize.Width - startStopButton.Width - 24), 53); overviewSummary.Width = Math.Max(300, hero.ClientSize.Width - startStopButton.Width - 78); };
            startStopButton.Location = new Point(Math.Max(24, hero.ClientSize.Width - startStopButton.Width - 24), 53);

            SoftSurfacePanel metricSurface = new SoftSurfacePanel { Location = new Point(0, 158), Size = new Size(monitorStack.Width, 164), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right, Padding = new Padding(18) };
            monitorStack.Controls.Add(metricSurface);
            Label metricHeading = new Label { Text = language == AppLanguage.Romanian ? "VERIFICĂRI" : "NETWORK CHECKS", AutoSize = true, Font = new Font("Segoe UI Semibold", 8F, FontStyle.Bold), ForeColor = Color.FromArgb(104, 112, 124), Location = new Point(20, 13) };
            metricSurface.Controls.Add(metricHeading);
            metricsLayout = new TableLayoutPanel { Location = new Point(14, 36), Size = new Size(metricSurface.Width - 28, 114), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right, ColumnCount = 4, RowCount = 2, BackColor = Color.Transparent, Padding = new Padding(0) };
            metricsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25F));
            metricsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25F));
            metricsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25F));
            metricsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25F));
            metricsLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));
            metricsLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));
            metricSurface.Controls.Add(metricsLayout);
            metricSurface.Resize += delegate { metricsLayout.Width = Math.Max(400, metricSurface.ClientSize.Width - 28); };

            wifiTitle = AddMetricCell(metricsLayout, 0, 0, language == AppLanguage.Romanian ? "Wi-Fi" : "Wi-Fi", out wifiLabel);
            signalTitle = AddMetricCell(metricsLayout, 1, 0, Localization.Get(language, "MetricSignal"), out signalLabel);
            gatewayTitle = AddMetricCell(metricsLayout, 2, 0, language == AppLanguage.Romanian ? "Gateway" : "Gateway", out gatewayLabel);
            internetTitle = AddMetricCell(metricsLayout, 3, 0, language == AppLanguage.Romanian ? "Internet" : "Internet", out internetLabel);
            dnsTitle = AddMetricCell(metricsLayout, 0, 1, language == AppLanguage.Romanian ? "DNS" : "DNS", out dnsLabel);
            downloadTitle = AddMetricCell(metricsLayout, 1, 1, Localization.Get(language, "MetricDownload"), out downloadLabel);
            uploadTitle = AddMetricCell(metricsLayout, 2, 1, Localization.Get(language, "MetricUpload"), out uploadLabel);
            Label metricNote = new Label { Text = language == AppLanguage.Romanian ? "Probe IPv4/IPv6 și HTTPS independente" : "Independent IPv4/IPv6 and HTTPS probes", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.FromArgb(111, 118, 128), Font = new Font("Segoe UI", 8F), Padding = new Padding(12, 0, 2, 0) };
            metricsLayout.Controls.Add(metricNote, 3, 1);

            SoftSurfacePanel chartSurface = new SoftSurfacePanel { Location = new Point(0, 326), Size = new Size(monitorStack.Width, 258), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right, Padding = new Padding(12) };
            monitorStack.Controls.Add(chartSurface);
            latencyChart = new LatencyChartPanel { Location = new Point(10, 10), Size = new Size(chartSurface.Width - 20, 238), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right, Language = language, BackColor = Color.White };
            chartSurface.Controls.Add(latencyChart);
            chartSurface.Resize += delegate { latencyChart.Width = Math.Max(300, chartSurface.ClientSize.Width - 20); latencyChart.Height = Math.Max(160, chartSurface.ClientSize.Height - 20); };

            SoftSurfacePanel activitySurface = new SoftSurfacePanel { Location = new Point(0, 598), Size = new Size(monitorStack.Width, 218), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right, Padding = new Padding(16) };
            monitorStack.Controls.Add(activitySurface);
            Label activityTitle = new Label { Text = language == AppLanguage.Romanian ? "ACTIVITATE RECENTĂ" : "RECENT ACTIVITY", AutoSize = true, Font = new Font("Segoe UI Semibold", 8F, FontStyle.Bold), ForeColor = Color.FromArgb(104, 112, 124), Location = new Point(18, 12) };
            activitySurface.Controls.Add(activityTitle);
            grid = new DataGridView { Location = new Point(12, 36), Size = new Size(activitySurface.Width - 24, 170), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false, RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, BackgroundColor = Color.White, BorderStyle = BorderStyle.None, CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal, GridColor = Color.FromArgb(239, 241, 244), EnableHeadersVisualStyles = false };
            grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(248, 249, 251);
            grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(97, 105, 116);
            grid.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI Semibold", 8F, FontStyle.Bold);
            grid.DefaultCellStyle.BackColor = Color.White;
            grid.DefaultCellStyle.ForeColor = Color.FromArgb(42, 48, 58);
            grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(232, 241, 253);
            grid.DefaultCellStyle.SelectionForeColor = Color.FromArgb(32, 44, 61);
            grid.RowTemplate.Height = 29;
            grid.Columns.Add("time", Localization.Get(language, "Time"));
            grid.Columns.Add("wifi", "Wi-Fi");
            grid.Columns.Add("signal", Localization.Get(language, "GridSignal"));
            grid.Columns.Add("gateway", "Gateway");
            grid.Columns.Add("internet", "Internet");
            grid.Columns.Add("targets", Localization.Get(language, "GridTargets"));
            grid.Columns.Add("dns", Localization.Get(language, "GridDNS"));
            grid.Columns.Add("state", Localization.Get(language, "State"));
            grid.Columns.Add("events", Localization.Get(language, "Events"));
            grid.Columns[0].FillWeight = 65;
            grid.Columns[1].FillWeight = 70;
            grid.Columns[2].FillWeight = 55;
            grid.Columns[3].FillWeight = 72;
            grid.Columns[4].FillWeight = 100;
            grid.Columns[5].FillWeight = 100;
            grid.Columns[6].FillWeight = 62;
            grid.Columns[7].FillWeight = 62;
            grid.Columns[8].FillWeight = 130;
            activitySurface.Controls.Add(grid);
            activitySurface.Resize += delegate { grid.Width = Math.Max(400, activitySurface.ClientSize.Width - 24); grid.Height = Math.Max(100, activitySurface.ClientSize.Height - 48); };

            reportEyebrow = new Label { Text = language == AppLanguage.Romanian ? "RAPORTUL SESIUNII" : "SESSION REPORT", AutoSize = true, Font = new Font("Segoe UI Semibold", 8F, FontStyle.Bold), ForeColor = Color.FromArgb(104, 112, 124), Location = new Point(28, 24) };
            reportPage.Controls.Add(reportEyebrow);
            reportIntro = new Label { Text = language == AppLanguage.Romanian ? "Constatări și pași următori" : "Findings and next steps", AutoSize = true, Font = new Font("Segoe UI Semibold", 21F, FontStyle.Bold), ForeColor = Color.FromArgb(28, 31, 36), Location = new Point(28, 45) };
            reportPage.Controls.Add(reportIntro);
            diagnosisGroup = new SoftSurfacePanel { Location = new Point(24, 104), Size = new Size(reportPage.ClientSize.Width - 48, 640), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right, Padding = new Padding(20) };
            reportPage.Controls.Add(diagnosisGroup);
            diagnosisHeading = new Label { Text = language == AppLanguage.Romanian ? "DOVEZI ȘI PAȘI URMĂTORI" : "EVIDENCE & NEXT STEPS", Location = new Point(24, 20), Size = new Size(600, 22), Font = new Font("Segoe UI Semibold", 8.5F, FontStyle.Bold), ForeColor = Color.FromArgb(94, 103, 116), AutoEllipsis = true };
            diagnosisGroup.Controls.Add(diagnosisHeading);
            diagnosisBox = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Location = new Point(22, 52), Size = new Size(diagnosisGroup.ClientSize.Width - 44, diagnosisGroup.ClientSize.Height - 74), Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right, BorderStyle = BorderStyle.None, Font = new Font("Segoe UI", 10F), BackColor = Color.White, ForeColor = Color.FromArgb(43, 49, 59), Text = Localization.Get(language, "InitialDiagnosis") + Environment.NewLine + Environment.NewLine + Localization.Get(language, "AIDataNotice") };
            diagnosisGroup.Controls.Add(diagnosisBox);
            reportPage.Resize += delegate { diagnosisGroup.Width = Math.Max(600, reportPage.ClientSize.Width - 48); };

            footerLabel.Text = Localization.Get(language, "Footer");
            aiStatusLabel.Text = Localization.Get(language, CurrentAiStatusKey());
            disconnectAiButton.Enabled = ChatGptPlanClient.IsConnected;
            FormClosing += MainFormClosing;
            ApplyLanguage();
            LiquidGlassTheme.ApplyTree(this, startStopButton);
            GlassWindowEffects.Attach(this, false);
            Shown += delegate { GlassWindowEffects.ApplyDarkScrollbars(this); };
            ShowMonitorView();
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
            startStopButton.Text = Localization.Get(language, monitoring ? (awaitingInitialSample ? "StartingMonitoring" : "Stop") : (samples.Count > 0 ? "Restart" : "Start"));
            startStopButton.Enabled = !awaitingInitialSample;
            startStopButton.BackColor = monitoring && !awaitingInitialSample ? LiquidGlassTheme.Danger : LiquidGlassTheme.Sapphire;
            startStopButton.ForeColor = Color.FromArgb(9, 15, 24);
            statusLabel.ForeColor = monitoring ? LiquidGlassTheme.Good : LiquidGlassTheme.Text;
            openLogsButton.Text = Localization.Get(language, "OpenLogs");
            copyReportButton.Text = Localization.Get(language, "CopyReport");
            routeTraceButton.Text = Localization.Get(language, "RouteTraceButton");
            aiButton.Text = Localization.Get(language, "ContinueChatGPT");
            aiSettingsButton.Text = Localization.Get(language, "AISettings");
            disconnectAiButton.Text = Localization.Get(language, "DisconnectChatGPT");
            aiStatusLabel.Text = Localization.Get(language, CurrentAiStatusKey());
            disconnectAiButton.Enabled = ChatGptPlanClient.IsConnected;
            statusLabel.Text = Localization.Get(language, monitoring ? "Running" : "Stopped");
            SetMetricTitle(wifiTitle, language == AppLanguage.Romanian ? "Wi-Fi" : "Wi-Fi");
            SetMetricTitle(signalTitle, Localization.Get(language, "MetricSignal"));
            SetMetricTitle(gatewayTitle, language == AppLanguage.Romanian ? "Gateway" : "Gateway");
            SetMetricTitle(internetTitle, language == AppLanguage.Romanian ? "Internet" : "Internet");
            SetMetricTitle(dnsTitle, language == AppLanguage.Romanian ? "DNS" : "DNS");
            SetMetricTitle(downloadTitle, Localization.Get(language, "MetricDownload"));
            SetMetricTitle(uploadTitle, Localization.Get(language, "MetricUpload"));
            viewTitle.Text = language == AppLanguage.Romanian ? "MONITORIZARE" : "MONITORING";
            monitorViewButton.Text = language == AppLanguage.Romanian ? "Prezentare" : "Overview";
            reportViewButton.Text = language == AppLanguage.Romanian ? "Diagnostic" : "Diagnosis";
            reportEyebrow.Text = language == AppLanguage.Romanian ? "RAPORTUL SESIUNII" : "SESSION REPORT";
            reportIntro.Text = language == AppLanguage.Romanian ? "Constatări și pași următori" : "Findings and next steps";
            overviewSummary.Text = monitoring
                ? (language == AppLanguage.Romanian ? "Monitorizarea rulează. Las-o activă până când apare sau trece problema." : "Monitoring is running. Leave it active until the issue appears or passes.")
                : samples.Count > 0
                    ? (language == AppLanguage.Romanian ? "Sesiune oprită. Deschide diagnosticul pentru dovezi și pașii următori." : "Session stopped. Open Diagnosis for evidence and next steps.")
                    : (language == AppLanguage.Romanian ? "Pornește monitorizarea când apare problema. Diagnosticul local nu necesită cont sau cheie AI." : "Start monitoring when the issue happens. Local diagnosis needs no account or AI key.");
            diagnosisHeading.Text = language == AppLanguage.Romanian ? "DOVEZI ȘI PAȘI URMĂTORI" : "EVIDENCE & NEXT STEPS";
            footerLabel.Text = Localization.Get(language, "Footer");
            latencyChart.Language = language;
            grid.Columns[0].HeaderText = Localization.Get(language, "Time");
            grid.Columns[1].HeaderText = Localization.Get(language, "GridWiFi");
            grid.Columns[2].HeaderText = Localization.Get(language, "GridSignal");
            grid.Columns[3].HeaderText = Localization.Get(language, "GridGateway");
            grid.Columns[4].HeaderText = Localization.Get(language, "GridInternet");
            grid.Columns[5].HeaderText = Localization.Get(language, "GridTargets");
            grid.Columns[6].HeaderText = Localization.Get(language, "GridDNS");
            grid.Columns[7].HeaderText = Localization.Get(language, "State");
            grid.Columns[8].HeaderText = Localization.Get(language, "Events");
            if (samples.Count == 0)
            {
                ResetMetrics();
                diagnosisBox.Text = Localization.Get(language, "InitialDiagnosis") + Environment.NewLine + Environment.NewLine + Localization.Get(language, "AIDataNotice");
            }
            else
            {
                List<Sample> copy;
                lock (samplesGate) copy = new List<Sample>(samples);
                UpdateMetricValues(copy[copy.Count - 1]);
                if (!monitoring)
                    diagnosisBox.Text = DiagnosticEngine.Analyze(copy, language) + Environment.NewLine + Environment.NewLine +
                    (language == AppLanguage.Romanian ? "Raport salvat local: " : "Report saved locally: ") + CsvLogger.ReportPath;
            }
        }

        private static GlassButton CreateToolbarButton(string text, int width)
        {
            return new GlassButton { Text = text, Size = new Size(width, 34), Margin = new Padding(0, 0, 8, 0), BackColor = LiquidGlassTheme.GlassInset, ForeColor = Color.FromArgb(255, 244, 246, 250), Font = new Font("Segoe UI Semibold", 9F, FontStyle.Bold), Cursor = Cursors.Hand };
        }

        private static GlassButton CreatePrimaryButton(string text, int width, int height)
        {
            return new GlassButton { Text = text, Size = new Size(width, height), BackColor = LiquidGlassTheme.Sapphire, ForeColor = Color.FromArgb(9, 15, 24), Font = new Font("Segoe UI Semibold", 9.5F, FontStyle.Bold), Cursor = Cursors.Hand };
        }

        private static GlassButton CreateNavigationButton(string text)
        {
            return new GlassButton { Text = text, Size = new Size(102, 34), BackColor = LiquidGlassTheme.GlassInset, ForeColor = LiquidGlassTheme.SecondaryText, Font = new Font("Segoe UI", 8.5F), Cursor = Cursors.Hand };
        }

        private static Label AddMetricCell(TableLayoutPanel layout, int column, int row, string title, out Label value)
        {
            SoftSurfacePanel card = new SoftSurfacePanel { Dock = DockStyle.Fill, Margin = new Padding(5), CornerRadius = 16, BorderColor = LiquidGlassTheme.Stroke, Padding = new Padding(11, 5, 8, 4) };
            Label caption = new Label { Text = title, Dock = DockStyle.Top, Height = 20, Font = new Font("Segoe UI", 8F), ForeColor = LiquidGlassTheme.MutedText, AutoEllipsis = true };
            value = new Label { Text = "—", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Font = new Font("Segoe UI Semibold", 10F, FontStyle.Bold), ForeColor = LiquidGlassTheme.Text, AutoEllipsis = true };
            card.Controls.Add(value);
            card.Controls.Add(caption);
            layout.Controls.Add(card, column, row);
            return caption;
        }

        private static void SetMetricTitle(Label label, string text)
        {
            if (label != null) label.Text = text;
        }

        private void ResetMetrics()
        {
            string noValue = Localization.Get(language, "NoValue");
            wifiLabel.Text = noValue;
            signalLabel.Text = noValue;
            gatewayLabel.Text = noValue;
            internetLabel.Text = noValue;
            dnsLabel.Text = noValue;
            downloadLabel.Text = noValue;
            uploadLabel.Text = noValue;
        }

        private void UpdateMetricValues(Sample sample)
        {
            wifiLabel.Text = Localization.Status(language, sample.WifiState);
            signalLabel.Text = sample.WifiSignalPercent.HasValue ? sample.WifiSignalPercent.Value.ToString(CultureInfo.InvariantCulture) + "%" : Localization.Get(language, "NoValue");
            gatewayLabel.Text = FormatProbe(sample.GatewayLatencyMs, sample.GatewayPingStatus, language);
            internetLabel.Text = FormatInternetProbe(sample, language);
            dnsLabel.Text = Localization.Status(language, sample.DnsStatus);
            downloadLabel.Text = sample.DownloadMbps.HasValue ? Format(sample.DownloadMbps) + " Mbps" : Localization.Get(language, "NoValue");
            uploadLabel.Text = sample.UploadMbps.HasValue ? Format(sample.UploadMbps) + " Mbps" : Localization.Get(language, "NoValue");
            gatewayLabel.ForeColor = sample.GatewayLatencyMs.HasValue ? LiquidGlassTheme.Text : LiquidGlassTheme.MutedText;
            internetLabel.ForeColor = NetworkProbe.CountExternalSuccesses(sample) > 0 || sample.HttpsStatus == "Success" ? LiquidGlassTheme.Good : LiquidGlassTheme.Danger;
            dnsLabel.ForeColor = sample.DnsStatus == "AllFailed" ? LiquidGlassTheme.Danger : LiquidGlassTheme.Text;
        }

        private void ShowMonitorView()
        {
            monitorPage.Visible = true;
            reportPage.Visible = false;
            monitorPage.BringToFront();
            StyleNavigationButton(monitorViewButton, true);
            StyleNavigationButton(reportViewButton, false);
        }

        private void ShowReportView()
        {
            monitorPage.Visible = false;
            reportPage.Visible = true;
            reportPage.BringToFront();
            StyleNavigationButton(monitorViewButton, false);
            StyleNavigationButton(reportViewButton, true);
        }

        private static void StyleNavigationButton(GlassButton button, bool active)
        {
            button.BackColor = active ? Color.FromArgb(255, 31, 48, 68) : Color.FromArgb(255, 17, 22, 31);
            button.ForeColor = active ? LiquidGlassTheme.Sapphire : LiquidGlassTheme.SecondaryText;
        }

        private void StartStopClicked(object sender, EventArgs e)
        {
            long now = Stopwatch.GetTimestamp();
            if (!MonitoringSessionPolicy.ShouldAcceptToggle(lastStartStopActionTicks, now, Stopwatch.Frequency, SystemInformation.DoubleClickTime)) return;
            lastStartStopActionTicks = now;
            if (!monitoring)
                StartMonitoring();
            else if (MonitoringSessionPolicy.CanStop(monitoring, !awaitingInitialSample))
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
            ResetMetrics();
            nextDnsCheck = DateTimeOffset.MinValue;
            monitoring = true;
            awaitingInitialSample = true;
            startStopButton.Enabled = false;
            startStopButton.Text = Localization.Get(language, "StartingMonitoring");
            startStopButton.BackColor = LiquidGlassTheme.Sapphire;
            statusLabel.ForeColor = LiquidGlassTheme.Good;
            statusLabel.Text = Localization.Get(language, "Running");
            overviewSummary.Text = language == AppLanguage.Romanian
                ? "Probează gateway-ul, DNS-ul, HTTPS și ținte IPv4/IPv6. Lasă aplicația pornită cât timp observi problema."
                : "Checking your gateway, DNS, HTTPS, and IPv4/IPv6 targets. Keep the app running while the issue occurs.";
            ShowMonitorView();
            timer = new System.Threading.Timer(SampleTick, null, 0, 5000);
        }

        private void StopMonitoring()
        {
            monitoring = false;
            awaitingInitialSample = false;
            if (timer != null)
            {
                timer.Dispose();
                timer = null;
            }
            startStopButton.Enabled = false;
            statusLabel.Text = Localization.Get(language, "Stopping");
            statusLabel.ForeColor = LiquidGlassTheme.MutedText;
            overviewSummary.Text = language == AppLanguage.Romanian ? "Pregătesc raportul din măsurătorile sesiunii…" : "Preparing your session report from the captured measurements…";

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
                    statusLabel.Text = reportLanguage == AppLanguage.Romanian ? "Sesiune analizată" : "Session analyzed";
                    statusLabel.ForeColor = LiquidGlassTheme.Sapphire;
                    overviewSummary.Text = reportLanguage == AppLanguage.Romanian
                        ? String.Format(CultureInfo.GetCultureInfo("ro-RO"), "Raport pregătit din {0} măsurători. Vezi concluzia și pașii următori.", copy.Count)
                        : String.Format(CultureInfo.GetCultureInfo("en-US"), "Report ready from {0} samples. Review the findings and next steps.", copy.Count);
                    startStopButton.Text = Localization.Get(language, "Restart");
                    startStopButton.BackColor = LiquidGlassTheme.Sapphire;
                    startStopButton.ForeColor = Color.FromArgb(9, 15, 24);
                    startStopButton.Enabled = true;
                    copyReportButton.Enabled = true;
                    ShowReportView();
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
            if (awaitingInitialSample && monitoring)
            {
                awaitingInitialSample = false;
                startStopButton.Text = Localization.Get(language, "Stop");
                startStopButton.BackColor = LiquidGlassTheme.Danger;
                startStopButton.Enabled = true;
            }
            UpdateMetricValues(sample);
            statusLabel.Text = ConnectivityLabel(sample.Connectivity, language);
            overviewSummary.Text = NextStepSummary(sample, language);

            string gateway = FormatProbe(sample.GatewayLatencyMs, sample.GatewayPingStatus, language);
            string internet = FormatInternetProbe(sample, language);
            string signal = sample.WifiSignalPercent.HasValue ? sample.WifiSignalPercent.Value.ToString(CultureInfo.InvariantCulture) + "%" : Localization.Get(language, "NoValue");
            string noReply = language == AppLanguage.Romanian ? "fara raspuns ICMP " : "ICMP no reply ";
            grid.Rows.Insert(0, sample.Timestamp.ToString("HH:mm:ss", CultureInfo.InvariantCulture), Localization.Status(language, sample.WifiState), signal,
                gateway + " / " + noReply + Format(sample.GatewayLossPct) + "%", internet + " / IPv4 " + noReply + Format(sample.InternetLossPct) + "%",
                NetworkProbe.FormatInternetTargets(sample.InternetTargetResults, sample.Ipv6TargetResults, language), Localization.Status(language, sample.DnsStatus), Localization.Status(language, sample.Connectivity), String.Join("; ", sample.Events.Split(new char[] { ';' }, StringSplitOptions.RemoveEmptyEntries).Select(delegate(string code) { return Localization.Event(language, code); }).ToArray()));
            if (grid.Rows.Count > 1000) grid.Rows.RemoveAt(grid.Rows.Count - 1);
            List<Sample> chartSamples;
            lock (samplesGate) chartSamples = new List<Sample>(samples);
            latencyChart.SetSamples(chartSamples);
        }

        private static string ConnectivityLabel(string connectivity, AppLanguage language)
        {
            bool ro = language == AppLanguage.Romanian;
            if (connectivity == "OutageSuspected") return ro ? "Conexiune întreruptă — verifică traseul" : "Connection interruption suspected";
            if (connectivity == "Degraded") return ro ? "Conexiune cu probleme" : "Connection needs attention";
            if (connectivity == "Partial") return ro ? "Răspuns parțial — verificările diferă" : "Partial reachability — checks disagree";
            return ro ? "Conexiune disponibilă" : "Connection available";
        }

        private static string NextStepSummary(Sample sample, AppLanguage language)
        {
            bool ro = language == AppLanguage.Romanian;
            string events = sample.Events ?? "";
            if (sample.Connectivity == "OutageSuspected")
                return ro ? "Nu au răspuns probele externe și HTTPS. Verifică dacă dispozitivele conectate la aceeași rețea au aceeași problemă." : "External targets and HTTPS did not respond. Check whether other devices on this network have the same issue.";
            if (sample.CaptivePortalSuspected)
                return ro ? "Răspunsurile HTTPS sugerează o redirecționare sau interceptare. Verifică autentificarea în rețea; indiciul nu confirmă un portal captiv." : "HTTPS responses suggest redirection or interception. Check network sign-in; this clue does not confirm a captive portal.";
            if (events.IndexOf("DNSFailure", StringComparison.Ordinal) >= 0)
                return ro ? "DNS nu a rezolvat toate numele de test. Dacă probele către IP răspund, verifică DNS-ul sau VPN-ul." : "DNS did not resolve every test name. If IP probes respond, check DNS or VPN settings.";
            if (sample.Connectivity == "Partial")
                return ro ? "Unele verificări răspund, altele nu. Lasă monitorul pornit pentru a vedea dacă diferența se repetă." : "Some checks respond and others do not. Keep monitoring to see whether the difference repeats.";
            if (sample.Connectivity == "Degraded")
                return ro ? "A apărut un indiciu de degradare. Păstrează monitorizarea până când problema se reproduce sau trece." : "A degradation clue was observed. Keep monitoring until the issue repeats or passes.";
            return ro ? "Probe independente către Internet au răspuns. Continuă monitorizarea dacă problema apare intermitent." : "Independent Internet checks responded. Keep monitoring if the issue is intermittent.";
        }

        private static string FormatProbe(double? latency, string status, AppLanguage language)
        {
            return latency.HasValue ? latency.Value.ToString("0.0", CultureInfo.InvariantCulture) + " ms" : Localization.Status(language, status);
        }

        private static string FormatInternetProbe(Sample sample, AppLanguage language)
        {
            if (sample.InternetTargetSucceeded.HasValue && sample.InternetTargetAttempted.HasValue)
            {
                int ipv6Attempted = sample.Ipv6TargetResults == null ? 0 : sample.Ipv6TargetResults.Count(delegate(InternetTargetObservation result) { return result.Status != "Unavailable"; });
                int ipv6Succeeded = sample.Ipv6TargetResults == null ? 0 : sample.Ipv6TargetResults.Count(delegate(InternetTargetObservation result) { return result.Status == "Success"; });
                string v4 = sample.InternetTargetSucceeded.Value.ToString(CultureInfo.InvariantCulture) + "/" + sample.InternetTargetAttempted.Value.ToString(CultureInfo.InvariantCulture) +
                    " IPv4; median " + FormatProbe(sample.InternetLatencyMs, sample.InternetPingStatus, language);
                string v6 = ipv6Attempted == 0 ? "IPv6 n/a" : "IPv6 " + ipv6Succeeded.ToString(CultureInfo.InvariantCulture) + "/" + ipv6Attempted.ToString(CultureInfo.InvariantCulture) +
                    "; median " + FormatProbe(sample.Ipv6LatencyMs, sample.Ipv6LatencyMs.HasValue ? "Success" : "Unavailable", language);
                return v4 + "; " + v6;
            }
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
            List<RouteTraceHop> hops = RouteTrace.ParseHops(result);
            string target = RouteTrace.SelectedTarget;
            string summary = RouteTrace.Summarize(hops, target, language);
            bool reachedTarget = hops.Count > 0 && hops[hops.Count - 1].Responded &&
                !String.IsNullOrWhiteSpace(hops[hops.Count - 1].Address) &&
                hops[hops.Count - 1].Address.IndexOf(target, StringComparison.OrdinalIgnoreCase) >= 0;
            bool anyResponse = hops.Any(delegate(RouteTraceHop hop) { return hop.Responded; });
            using (Form dialog = new Form())
            {
                dialog.Text = Localization.Get(language, "RouteTraceDialogTitle") + " - " + target;
                dialog.StartPosition = FormStartPosition.CenterParent;
                dialog.FormBorderStyle = FormBorderStyle.None;
                dialog.Size = new Size(930, 660);
                dialog.MinimumSize = new Size(740, 520);
                dialog.BackColor = LiquidGlassTheme.Window;
                dialog.Font = new Font("Segoe UI", 9F);

                TableLayoutPanel layout = new TableLayoutPanel
                {
                    Dock = DockStyle.Fill,
                    BackColor = LiquidGlassTheme.Window,
                    Padding = new Padding(12),
                    ColumnCount = 1,
                    RowCount = 5
                };
                layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 64F));
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 68F));
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 76F));
                layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 56F));
                dialog.Controls.Add(layout);

                Panel titleBar = new Panel { Dock = DockStyle.Fill, BackColor = LiquidGlassTheme.WindowRaised, Padding = new Padding(18, 8, 10, 4) };
                Label title = new Label
                {
                    Text = Localization.Get(language, "RouteTraceDialogTitle"),
                    Location = new Point(16, 6),
                    Size = new Size(610, 27),
                    Font = new Font("Segoe UI Semibold", 15F, FontStyle.Bold),
                    ForeColor = LiquidGlassTheme.Text,
                    AutoEllipsis = true
                };
                Label subtitle = new Label
                {
                    Text = Localization.Format(language, "RouteTraceSubtitle", target),
                    Location = new Point(18, 34),
                    Size = new Size(610, 19),
                    Font = new Font("Segoe UI", 8.5F),
                    ForeColor = LiquidGlassTheme.MutedText,
                    AutoEllipsis = true
                };
                GlassButton titleClose = new GlassButton
                {
                    Text = "×",
                    Size = new Size(38, 38),
                    Anchor = AnchorStyles.Top | AnchorStyles.Right,
                    BackColor = LiquidGlassTheme.GlassInset,
                    ForeColor = LiquidGlassTheme.Text,
                    Font = new Font("Segoe UI", 16F),
                    Cursor = Cursors.Hand
                };
                titleClose.Location = new Point(Math.Max(18, titleBar.ClientSize.Width - 52), 9);
                titleBar.Resize += delegate { titleClose.Location = new Point(Math.Max(18, titleBar.ClientSize.Width - 52), 9); };
                titleClose.Click += delegate { dialog.Close(); };
                titleBar.Controls.Add(title);
                titleBar.Controls.Add(subtitle);
                titleBar.Controls.Add(titleClose);
                titleBar.MouseDown += delegate(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) GlassWindowEffects.BeginWindowDrag(dialog); };
                title.MouseDown += delegate(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) GlassWindowEffects.BeginWindowDrag(dialog); };
                subtitle.MouseDown += delegate(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) GlassWindowEffects.BeginWindowDrag(dialog); };
                layout.Controls.Add(titleBar, 0, 0);

                SoftSurfacePanel privacy = new SoftSurfacePanel
                {
                    Dock = DockStyle.Fill,
                    CornerRadius = 15,
                    BorderColor = LiquidGlassTheme.Stroke,
                    Padding = new Padding(16, 8, 16, 8),
                    Margin = new Padding(0, 5, 0, 4)
                };
                Label privacyText = new Label
                {
                    Dock = DockStyle.Fill,
                    Text = Localization.Get(language, "RouteTracePrivacyNote"),
                    ForeColor = LiquidGlassTheme.SecondaryText,
                    Font = new Font("Segoe UI", 9F),
                    TextAlign = ContentAlignment.MiddleLeft,
                    AutoEllipsis = true
                };
                privacy.Controls.Add(privacyText);
                layout.Controls.Add(privacy, 0, 1);

                SoftSurfacePanel summaryCard = new SoftSurfacePanel
                {
                    Dock = DockStyle.Fill,
                    CornerRadius = 16,
                    BorderColor = LiquidGlassTheme.Stroke,
                    Padding = new Padding(15, 8, 15, 7),
                    Margin = new Padding(0, 3, 0, 4)
                };
                Label stateDot = new Label
                {
                    Text = "●",
                    Location = new Point(15, 20),
                    Size = new Size(20, 22),
                    Font = new Font("Segoe UI", 11F, FontStyle.Bold),
                    ForeColor = reachedTarget ? LiquidGlassTheme.Good : (anyResponse ? LiquidGlassTheme.Warning : LiquidGlassTheme.MutedText),
                    TextAlign = ContentAlignment.MiddleCenter
                };
                Label summaryLabel = new Label
                {
                    Text = summary,
                    Location = new Point(42, 11),
                    Size = new Size(650, 27),
                    Font = new Font("Segoe UI Semibold", 10F, FontStyle.Bold),
                    ForeColor = LiquidGlassTheme.Text,
                    AutoEllipsis = true
                };
                Label hopCount = new Label
                {
                    Text = Localization.Get(language, "RouteTraceHopCount") + "  ·  " + hops.Count.ToString(CultureInfo.InvariantCulture),
                    Location = new Point(43, 38),
                    Size = new Size(420, 17),
                    Font = new Font("Segoe UI", 8F),
                    ForeColor = LiquidGlassTheme.MutedText,
                    AutoEllipsis = true
                };
                summaryCard.Controls.Add(stateDot);
                summaryCard.Controls.Add(summaryLabel);
                summaryCard.Controls.Add(hopCount);
                layout.Controls.Add(summaryCard, 0, 2);

                SoftSurfacePanel resultsCard = new SoftSurfacePanel
                {
                    Dock = DockStyle.Fill,
                    CornerRadius = 18,
                    BorderColor = LiquidGlassTheme.Stroke,
                    Padding = new Padding(10),
                    Margin = new Padding(0, 3, 0, 4)
                };
                DataGridView grid = new DataGridView
                {
                    Dock = DockStyle.Fill,
                    BorderStyle = BorderStyle.None,
                    BackgroundColor = LiquidGlassTheme.WindowRaised,
                    GridColor = Color.FromArgb(42, 54, 70),
                    RowHeadersVisible = false,
                    ColumnHeadersVisible = true,
                    EnableHeadersVisualStyles = false,
                    ColumnHeadersHeight = 38,
                    ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
                    RowTemplate = { Height = 36 },
                    AllowUserToAddRows = false,
                    AllowUserToDeleteRows = false,
                    AllowUserToResizeRows = false,
                    AllowUserToResizeColumns = false,
                    ReadOnly = true,
                    MultiSelect = false,
                    SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                    AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None,
                    ScrollBars = ScrollBars.Vertical,
                    Font = new Font("Segoe UI", 9F),
                    CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
                    ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None,
                    DefaultCellStyle = new DataGridViewCellStyle
                    {
                        BackColor = LiquidGlassTheme.WindowRaised,
                        ForeColor = LiquidGlassTheme.Text,
                        SelectionBackColor = Color.FromArgb(255, 35, 54, 78),
                        SelectionForeColor = LiquidGlassTheme.Text,
                        Padding = new Padding(8, 2, 8, 2)
                    },
                    AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle { BackColor = Color.FromArgb(255, 19, 25, 35), ForeColor = LiquidGlassTheme.Text, SelectionBackColor = Color.FromArgb(255, 35, 54, 78), SelectionForeColor = LiquidGlassTheme.Text }
                };
                grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(255, 24, 31, 43);
                grid.ColumnHeadersDefaultCellStyle.ForeColor = LiquidGlassTheme.SecondaryText;
                grid.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI Semibold", 8.5F, FontStyle.Bold);
                grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "hop", HeaderText = Localization.Get(language, "RouteTraceColumnHop"), Width = 62, SortMode = DataGridViewColumnSortMode.NotSortable });
                grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "probe1", HeaderText = Localization.Get(language, "RouteTraceColumnProbe1"), Width = 92, SortMode = DataGridViewColumnSortMode.NotSortable });
                grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "probe2", HeaderText = Localization.Get(language, "RouteTraceColumnProbe2"), Width = 92, SortMode = DataGridViewColumnSortMode.NotSortable });
                grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "probe3", HeaderText = Localization.Get(language, "RouteTraceColumnProbe3"), Width = 92, SortMode = DataGridViewColumnSortMode.NotSortable });
                grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "address", HeaderText = Localization.Get(language, "RouteTraceColumnAddress"), AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = 120, SortMode = DataGridViewColumnSortMode.NotSortable });
                grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "response", HeaderText = Localization.Get(language, "RouteTraceColumnResponse"), Width = 112, SortMode = DataGridViewColumnSortMode.NotSortable });
                foreach (RouteTraceHop hop in hops)
                {
                    int row = grid.Rows.Add(hop.Number.ToString(CultureInfo.InvariantCulture), hop.Probe1, hop.Probe2, hop.Probe3,
                        String.IsNullOrWhiteSpace(hop.Address) ? "—" : hop.Address,
                        Localization.Get(language, hop.Responded ? "RouteTraceResponded" : "RouteTraceNoReply"));
                    grid.Rows[row].Cells[5].Style.ForeColor = hop.Responded ? LiquidGlassTheme.Good : LiquidGlassTheme.Warning;
                    if (!hop.Responded) grid.Rows[row].DefaultCellStyle.ForeColor = LiquidGlassTheme.MutedText;
                }
                grid.ClearSelection();
                Label emptyState = new Label
                {
                    Dock = DockStyle.Fill,
                    TextAlign = ContentAlignment.MiddleCenter,
                    ForeColor = LiquidGlassTheme.SecondaryText,
                    BackColor = LiquidGlassTheme.WindowRaised,
                    Font = new Font("Segoe UI", 9.5F),
                    Padding = new Padding(24),
                    AutoEllipsis = true,
                    Text = Localization.Get(language, "RouteTraceSummaryUnavailable") + Environment.NewLine + Environment.NewLine +
                        Localization.Get(language, "RouteTraceRawDetails") + " " + (result ?? String.Empty).Trim().Replace("\r\n", " ").Replace("\n", " ")
                };
                grid.Visible = hops.Count > 0;
                emptyState.Visible = hops.Count == 0;
                resultsCard.Controls.Add(grid);
                resultsCard.Controls.Add(emptyState);
                layout.Controls.Add(resultsCard, 0, 3);

                Panel footer = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };
                Label localLabel = new Label
                {
                    Text = Localization.Get(language, "RouteTraceLocalFooter"),
                    Location = new Point(8, 18),
                    Size = new Size(590, 24),
                    ForeColor = LiquidGlassTheme.MutedText,
                    Font = new Font("Segoe UI", 8F),
                    AutoEllipsis = true
                };
                GlassButton close = new GlassButton
                {
                    Text = Localization.Get(language, "RouteTraceClose"),
                    Size = new Size(112, 38),
                    Anchor = AnchorStyles.Top | AnchorStyles.Right,
                    BackColor = LiquidGlassTheme.Sapphire,
                    ForeColor = Color.FromArgb(9, 15, 24),
                    Font = new Font("Segoe UI Semibold", 9F, FontStyle.Bold),
                    Cursor = Cursors.Hand,
                    DialogResult = DialogResult.OK
                };
                close.Location = new Point(Math.Max(8, footer.ClientSize.Width - close.Width - 6), 5);
                footer.Resize += delegate
                {
                    close.Location = new Point(Math.Max(8, footer.ClientSize.Width - close.Width - 6), 5);
                    localLabel.Width = Math.Max(80, close.Left - localLabel.Left - 14);
                };
                footer.Controls.Add(localLabel);
                footer.Controls.Add(close);
                layout.Controls.Add(footer, 0, 4);

                dialog.AcceptButton = close;
                dialog.CancelButton = close;
                GlassWindowEffects.Attach(dialog, true);
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

    internal static class MonitoringSessionPolicy
    {
        internal static bool CanStop(bool isMonitoring, bool initialSampleCaptured)
        {
            return isMonitoring && initialSampleCaptured;
        }

        internal static bool ShouldAcceptToggle(long previousActionTicks, long currentActionTicks, long frequency, int doubleClickMilliseconds)
        {
            if (previousActionTicks <= 0 || currentActionTicks < previousActionTicks || frequency <= 0) return true;
            double minimumInterval = frequency * Math.Max(0, doubleClickMilliseconds) / 1000.0;
            return currentActionTicks - previousActionTicks >= minimumInterval;
        }
    }

    internal static class SelfTests
    {
        public static int Run(string outputPath)
        {
            List<string> results = new List<string>();
            bool stopGate = !MonitoringSessionPolicy.CanStop(true, false) &&
                MonitoringSessionPolicy.CanStop(true, true) && !MonitoringSessionPolicy.CanStop(false, true);
            bool duplicateClickGate = MonitoringSessionPolicy.ShouldAcceptToggle(0, 100, 1000, 500) &&
                !MonitoringSessionPolicy.ShouldAcceptToggle(1000, 1400, 1000, 500) &&
                MonitoringSessionPolicy.ShouldAcceptToggle(1000, 1600, 1000, 500);
            results.Add((stopGate && duplicateClickGate ? "PASS" : "FAIL") + " | monitoring start cannot immediately stop into an empty diagnosis | " +
                (stopGate && duplicateClickGate ? "first sample gate and double-click debounce prevent an empty report" : "startup protection mismatch"));
            Test(results, "healthy window", HealthyWindow(), "Nu s-au detectat intreruperi");
            Test(results, "wifi disconnect", WindowWith(delegate(Sample s) { s.WifiState = "Disconnected"; }), "Wi-Fi");
            Test(results, "weak signal", WindowWith(delegate(Sample s) { s.WifiSignalPercent = 20; }), "Semnal Wi-Fi");
            Test(results, "gateway ICMP non-response", WindowWith(delegate(Sample s) { s.GatewayPingStatus = "TimedOut"; s.GatewayLatencyMs = null; }), "probele ICMP nu au primit raspuns");
            Test(results, "all independent probes fail", WindowWith(delegate(Sample s) { s.InternetPingStatus = "TimedOut"; s.HttpsStatus = "AllFailed"; s.HttpsSucceeded = 0; s.HttpsAttempted = 3; }), "toate tintele ICMP IPv4/IPv6 si cele trei endpointuri HTTPS au esuat impreuna");
            Test(results, "one HTTPS endpoint fails but others work", WindowWith(delegate(Sample s) { s.InternetPingStatus = "TimedOut"; s.HttpsStatus = "Success"; s.HttpsSucceeded = 2; s.HttpsAttempted = 3; }), "nu confirma o cadere de Internet");
            Test(results, "dns failure", WindowWith(delegate(Sample s) { s.DnsStatus = "Failed"; }), "DNS");
            Test(results, "all DNS names fail", WindowWith(delegate(Sample s) { s.DnsStatus = "AllFailed"; s.DnsSucceeded = 0; s.DnsAttempted = 3; }), "toate cele trei nume de test");
            Test(results, "partial DNS results", WindowWith(delegate(Sample s) { s.DnsSucceeded = 2; s.DnsAttempted = 3; }), "raspunsuri diferite");
            Test(results, "per endpoint breakdown", WindowWith(delegate(Sample s) { s.HttpsEndpointResults = "www.msftconnecttest.com=Timeout;www.google.com=Success@24ms;cp.cloudflare.com=Failed"; s.HttpsAttempted = 3; s.HttpsSucceeded = 1; }), "www.google.com 6/6");
            Test(results, "icmp filtered but https works", WindowWith(delegate(Sample s) { s.InternetPingStatus = "Unavailable"; s.HttpsStatus = "Success"; }), "nu confirma o cadere de Internet");
            IList<Sample> gatewaySpikeWindow = HealthyWindow().Cast<Sample>().ToList();
            for (int i = 0; i < gatewaySpikeWindow.Count; i++) gatewaySpikeWindow[i].GatewayLatencyMs = i == gatewaySpikeWindow.Count - 1 ? 180 : 8;
            gatewaySpikeWindow[gatewaySpikeWindow.Count - 1].Events = String.Join(";", NetworkProbe.FindSampleEvents(gatewaySpikeWindow[gatewaySpikeWindow.Count - 1],
                gatewaySpikeWindow.Take(gatewaySpikeWindow.Count - 1).ToList()));
            Test(results, "gateway latency spike relative to recent baseline", gatewaySpikeWindow, "Spike relativ local");
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
            oneTargetNoHttps.Events = "InternetTargetDisagreement";
            Sample allUnavailable = BaseSample();
            allUnavailable.InternetTargetResults = new List<InternetTargetObservation>
            {
                new InternetTargetObservation { Name = "Cloudflare", Status = "TimedOut" },
                new InternetTargetObservation { Name = "Google", Status = "TimedOut" },
                new InternetTargetObservation { Name = "Quad9", Status = "TimedOut" }
            };
            allUnavailable.HttpsStatus = "AllFailed";
            bool connectivityConsensus = NetworkProbe.DetermineConnectivity(filteredTarget) == "Partial" &&
                NetworkProbe.DetermineConnectivity(oneTargetNoHttps) == "Partial" && NetworkProbe.DetermineConnectivity(allUnavailable) == "OutageSuspected";
            results.Add((connectivityConsensus ? "PASS" : "FAIL") + " | endpoint disagreement and outage states | " + (connectivityConsensus ? "partial endpoint reachability is separated from suspected total outage" : "connectivity consensus mismatch"));
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
            bool traceArgumentsSafe = RouteTrace.BuildArguments() == "/d /4 /h 8 /w 350 1.1.1.1" &&
                RouteTrace.BuildArguments(true) == "/d /6 /h 8 /w 350 2606:4700:4700::1111" &&
                RouteTrace.SelectIpv6Route(false, true) && !RouteTrace.SelectIpv6Route(true, true);
            results.Add((traceArgumentsSafe ? "PASS" : "FAIL") + " | bounded route trace arguments | " + (traceArgumentsSafe ? "fixed target; eight hops; 350 ms per probe" : "route trace options changed"));
            string traceFixture = "Tracing route to 1.1.1.1 over a maximum of 8 hops\r\n  1    1 ms    2 ms    3 ms  192.168.1.1\r\n  2     *       *       *     Request timed out.\r\n  3    8 ms    9 ms   10 ms  1.1.1.1\r\nTrace complete.";
            List<RouteTraceHop> parsedTrace = RouteTrace.ParseHops(traceFixture);
            bool traceParser = parsedTrace.Count == 3 && parsedTrace[0].Responded && parsedTrace[0].Address == "192.168.1.1" &&
                !parsedTrace[1].Responded && String.IsNullOrEmpty(parsedTrace[1].Address) && parsedTrace[2].Responded &&
                RouteTrace.Summarize(parsedTrace, "1.1.1.1", AppLanguage.English) == Localization.Get(AppLanguage.English, "RouteTraceSummaryReached");
            results.Add((traceParser ? "PASS" : "FAIL") + " | route trace hop parsing | " + (traceParser ? "latencies, addresses, and unanswered hops are separated" : "route trace rows were not parsed correctly"));
            List<RouteTraceHop> partialTrace = RouteTrace.ParseHops("  1    2 ms    3 ms    4 ms  192.168.1.1\r\n  2     *       *       *     Request timed out.");
            string partialTraceSummary = RouteTrace.Summarize(partialTrace, "1.1.1.1", AppLanguage.English);
            bool partialTraceIsCautious = partialTrace.Count == 2 && partialTraceSummary.IndexOf("not reached", StringComparison.OrdinalIgnoreCase) >= 0 &&
                partialTraceSummary.IndexOf("outage", StringComparison.OrdinalIgnoreCase) < 0;
            results.Add((partialTraceIsCautious ? "PASS" : "FAIL") + " | route trace partial result interpretation | " + (partialTraceIsCautious ? "timeouts are described without claiming an outage" : "partial trace was overinterpreted"));
            List<RouteTraceHop> noReplyTrace = RouteTrace.ParseHops("  1     *       *       *     Request timed out.");
            bool noReplyTraceIsCautious = noReplyTrace.Count == 1 &&
                RouteTrace.Summarize(noReplyTrace, "1.1.1.1", AppLanguage.Romanian) == Localization.Get(AppLanguage.Romanian, "RouteTraceSummaryNoReplies") &&
                RouteTrace.Summarize(new List<RouteTraceHop>(), "1.1.1.1", AppLanguage.English) == Localization.Get(AppLanguage.English, "RouteTraceSummaryUnavailable");
            results.Add((noReplyTraceIsCautious ? "PASS" : "FAIL") + " | route trace no-response and empty states | " + (noReplyTraceIsCautious ? "both states are explained without a false outage diagnosis" : "route trace empty-state handling mismatch"));
            bool interfaceTypes = NetworkProbe.DescribeInterfaceType(NetworkInterfaceType.Wireless80211) == "Wi-Fi" &&
                NetworkProbe.DescribeInterfaceType(NetworkInterfaceType.Ethernet) == "Ethernet" &&
                NetworkProbe.DescribeInterfaceType(NetworkInterfaceType.Tunnel) == "Tunnel" &&
                NetworkProbe.DescribeInterfaceType(NetworkInterfaceType.Loopback) == "Other";
            results.Add((interfaceTypes ? "PASS" : "FAIL") + " | active interface categories | " + (interfaceTypes ? "Wi-Fi, Ethernet, and other types distinguished without names" : "interface type mapping mismatch"));
            Sample ethernetWifiOff = BaseSample(); ethernetWifiOff.ActiveInterfaceType = "Ethernet"; ethernetWifiOff.WifiRelevant = false; ethernetWifiOff.WifiState = "Disconnected";
            ethernetWifiOff.Events = String.Join(";", NetworkProbe.FindSampleEvents(ethernetWifiOff, new List<Sample>()));
            bool ethernetIgnoresWifi = !NetworkProbe.HasWifiIssue(ethernetWifiOff) &&
                DiagnosticEngine.Analyze(new List<Sample> { ethernetWifiOff }).IndexOf("adaptorul deconectat", StringComparison.OrdinalIgnoreCase) < 0 &&
                NetworkProbe.DetermineConnectivity(ethernetWifiOff) == "OK";
            results.Add((ethernetIgnoresWifi ? "PASS" : "FAIL") + " | Ethernet with Wi-Fi disconnected | " + (ethernetIgnoresWifi ? "inactive Wi-Fi is not diagnosed as the active fault" : "inactive Wi-Fi created a false finding"));
            Sample wifiDisconnected = BaseSample(); wifiDisconnected.WifiState = "Disconnected"; wifiDisconnected.ActiveInterfaceType = "Wi-Fi"; wifiDisconnected.WifiRelevant = true;
            wifiDisconnected.Events = String.Join(";", NetworkProbe.FindSampleEvents(wifiDisconnected, new List<Sample>()));
            bool activeWifiRecognized = NetworkProbe.HasWifiIssue(wifiDisconnected) && wifiDisconnected.Events.Contains("WiFiDisconnected") &&
                NetworkProbe.DetermineConnectivity(wifiDisconnected) == "Degraded";
            results.Add((activeWifiRecognized ? "PASS" : "FAIL") + " | active Wi-Fi disconnect | " + (activeWifiRecognized ? "disconnect affects status only when Wi-Fi is the active route" : "active Wi-Fi disconnect not diagnosed"));
            Sample partialHttps = BaseSample(); partialHttps.HttpsStatus = "Success"; partialHttps.HttpsSucceeded = 2; partialHttps.HttpsAttempted = 3; partialHttps.Events = "HTTPSProbeDisagreement";
            bool partialHttpsIsPartial = NetworkProbe.DetermineConnectivity(partialHttps) == "Partial";
            results.Add((partialHttpsIsPartial ? "PASS" : "FAIL") + " | one HTTPS endpoint fails, others succeed | " + (partialHttpsIsPartial ? "reports partial endpoint evidence, not degraded Internet" : "partial HTTPS was overclassified"));
            Sample gatewayPingFiltered = BaseSample(); gatewayPingFiltered.GatewayPingStatus = "TimedOut"; gatewayPingFiltered.Events = "GatewayUnreachable";
            bool gatewayFilterIsNotOutage = NetworkProbe.DetermineConnectivity(gatewayPingFiltered) == "OK";
            results.Add((gatewayFilterIsNotOutage ? "PASS" : "FAIL") + " | gateway ICMP filtered but Internet works | " + (gatewayFilterIsNotOutage ? "gateway ping alone does not lower Internet status" : "gateway ICMP was overinterpreted"));
            Sample ipv6Only = BaseSample();
            ipv6Only.InternetTargetResults = new List<InternetTargetObservation>
            {
                new InternetTargetObservation { Name = "Cloudflare", Status = "TimedOut" },
                new InternetTargetObservation { Name = "Google", Status = "TimedOut" },
                new InternetTargetObservation { Name = "Quad9", Status = "TimedOut" }
            };
            ipv6Only.InternetPingStatus = "AllFailed"; ipv6Only.InternetLatencyMs = null;
            ipv6Only.Ipv6TargetResults = new List<InternetTargetObservation>
            {
                new InternetTargetObservation { Name = "Cloudflare IPv6", Status = "Success", LatencyMs = 31 },
                new InternetTargetObservation { Name = "Google IPv6", Status = "Success", LatencyMs = 35 },
                new InternetTargetObservation { Name = "Quad9 IPv6", Status = "Unavailable" }
            };
            ipv6Only.Ipv6LatencyMs = 33;
            ipv6Only.Events = String.Join(";", NetworkProbe.FindSampleEvents(ipv6Only, new List<Sample>()));
            bool ipv6Reachable = NetworkProbe.CountExternalSuccesses(ipv6Only) == 2 &&
                NetworkProbe.DetermineConnectivity(ipv6Only) == "OK" &&
                DiagnosticEngine.Analyze(new List<Sample> { ipv6Only }).Contains("Cloudflare IPv6");
            results.Add((ipv6Reachable ? "PASS" : "FAIL") + " | IPv6-only Internet reachability | " + (ipv6Reachable ? "IPv6 success prevents a false total outage" : "IPv6 reachability was lost or mislabeled"));
            bool scheduledHttps = NetworkProbe.ShouldCheckHttps(true, 3) && NetworkProbe.ShouldCheckHttps(false, 0) &&
                !NetworkProbe.ShouldCheckHttps(false, 2);
            results.Add((scheduledHttps ? "PASS" : "FAIL") + " | periodic HTTPS verification | " + (scheduledHttps ? "HTTPS runs every DNS interval and sooner on weak IP evidence" : "HTTPS schedule mismatch"));
            bool portalResponse = NetworkProbe.ClassifyUnexpectedHttpsResponse(302, "text/html", false) == "PortalOrIntercept" &&
                NetworkProbe.ClassifyUnexpectedHttpsResponse(200, "text/html; charset=utf-8", false) == "PortalOrIntercept" &&
                NetworkProbe.ClassifyUnexpectedHttpsResponse(200, "text/plain", false) == "HTTP_200" &&
                NetworkProbe.ClassifyUnexpectedHttpsResponse(204, "", true) == "Success";
            results.Add((portalResponse ? "PASS" : "FAIL") + " | captive portal/interception response classification | " + (portalResponse ? "redirect/HTML flagged as a clue without treating it as success" : "HTTP response classification mismatch"));
            bool portalConsensus = NetworkProbe.IsCaptivePortalSuspected(new ProbeResult[]
            {
                new ProbeResult { Status = "PortalOrIntercept" },
                new ProbeResult { Status = "PortalOrIntercept" },
                new ProbeResult { Status = "HTTP_403" }
            }) && !NetworkProbe.IsCaptivePortalSuspected(new ProbeResult[] { new ProbeResult { Status = "PortalOrIntercept" }, new ProbeResult { Status = "Success" } });
            Sample portalSample = BaseSample(); portalSample.InternetTargetResults = null; portalSample.InternetPingStatus = "AllFailed";
            portalSample.HttpsStatus = "AllFailed"; portalSample.CaptivePortalSuspected = true; portalSample.Events = "CaptivePortalSuspected";
            portalConsensus = portalConsensus && NetworkProbe.DetermineConnectivity(portalSample) == "Degraded" &&
                DiagnosticEngine.AssessLikelySegment(new List<Sample> { portalSample }, AppLanguage.Romanian).IndexOf("Portal captiv", StringComparison.OrdinalIgnoreCase) >= 0;
            results.Add((portalConsensus ? "PASS" : "FAIL") + " | portal needs multiple independent indications | " + (portalConsensus ? "single endpoint is not enough; portal is separated from total outage" : "portal evidence was over/under-classified"));
            bool proxyDetection = NetworkProbe.IsUsingProxy(new WebProxy("http://127.0.0.1:8888"), new Uri("https://example.com/")) &&
                !NetworkProbe.IsUsingProxy(new WebProxy(), new Uri("https://example.com/"));
            results.Add((proxyDetection ? "PASS" : "FAIL") + " | system proxy use detection | " + (proxyDetection ? "records only proxy use, not its address" : "proxy mode detection mismatch"));
            bool relativeSpike = NetworkProbe.IsLatencySpike(220, new double?[] { 78, 82, 80, 79 }) &&
                !NetworkProbe.IsLatencySpike(180, new double?[] { 178, 181, 179, 180 }) &&
                !NetworkProbe.IsLatencySpike(180, new double?[] { 25, 27 });
            results.Add((relativeSpike ? "PASS" : "FAIL") + " | relative latency spike baseline | " + (relativeSpike ? "detects a jump, not a stable high RTT or a short baseline" : "relative latency classification mismatch"));
            Sample ipv6Unavailable = BaseSample();
            ipv6Unavailable.InternetPingStatus = "AllFailed";
            ipv6Unavailable.InternetTargetResults = new List<InternetTargetObservation>
            {
                new InternetTargetObservation { Name = "Cloudflare", Status = "TimedOut" },
                new InternetTargetObservation { Name = "Google", Status = "TimedOut" },
                new InternetTargetObservation { Name = "Quad9", Status = "TimedOut" }
            };
            ipv6Unavailable.Ipv6TargetResults = new List<InternetTargetObservation>
            {
                new InternetTargetObservation { Name = "Cloudflare IPv6", Status = "Unavailable" },
                new InternetTargetObservation { Name = "Google IPv6", Status = "Unavailable" },
                new InternetTargetObservation { Name = "Quad9 IPv6", Status = "Unavailable" }
            };
            bool unsupportedIpv6IsNotLoss = NetworkProbe.CountExternalSuccesses(ipv6Unavailable) == 0 &&
                !ipv6Unavailable.Ipv6TargetResults.Any(delegate(InternetTargetObservation result) { return result.Status != "Unavailable"; }) &&
                NetworkProbe.Ipv6SockAddrSize() == 28;
            results.Add((unsupportedIpv6IsNotLoss ? "PASS" : "FAIL") + " | IPv6 unavailable and sockaddr layout | " + (unsupportedIpv6IsNotLoss ? "unsupported IPv6 targets are excluded; Windows sockaddr layout is correct" : "IPv6 unavailable status or native layout mismatch"));
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
            IList<Sample> privacySamples = HealthyWindow();
            privacySamples[0].Ipv6TargetResults = new List<InternetTargetObservation> { new InternetTargetObservation { Name = "Cloudflare IPv6", Status = "Success" } };
            string summary = ChatGptPlanClient.BuildAggregatedSummary(privacySamples, AppLanguage.English);
            bool sanitized = summary.IndexOf("192.0.2.45", StringComparison.Ordinal) < 0 && summary.IndexOf("MyPrivateWifi", StringComparison.Ordinal) < 0 &&
                summary.IndexOf("1.1.1.1", StringComparison.Ordinal) < 0 && summary.IndexOf("8.8.8.8", StringComparison.Ordinal) < 0 &&
                summary.IndexOf("2606:4700:4700::1111", StringComparison.Ordinal) < 0;
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
            bool csvSchema = CsvLogger.HeaderMatches("\uFEFF" + "TimestampLocal,WifiState,WifiSignalPercent,ActiveInterfaceType,GatewayPingStatus,GatewayLatencyMs,GatewayIcmpNonResponsePctRolling60s,InternetPingStatus,InternetIpv4LatencyMs,InternetIpv4IcmpNonResponsePctRolling60s,InternetIpv4Targets,InternetIpv4TargetsSucceeded,InternetIpv4TargetsAttempted,InternetIpv6LatencyMs,InternetIpv6Targets,InternetIpv6TargetsSucceeded,InternetIpv6TargetsAttempted,HttpsStatus,HttpsLatencyMs,HttpsEndpoints,HttpsSucceeded,HttpsAttempted,HttpsUsedWindowsProxy,CaptivePortalSuspected,DnsStatus,DnsLatencyMs,DnsEndpoints,DnsSucceeded,DnsAttempted,Connectivity,Events,NetworkDownloadMbps,NetworkUploadMbps") &&
                !CsvLogger.HeaderMatches("TimestampLocal,WifiState,Events");
            Sample csvPrivacy = BaseSample();
            csvPrivacy.InternetTargetResults = new List<InternetTargetObservation> { new InternetTargetObservation { Name = "Cloudflare", Status = "Success" } };
            csvPrivacy.Ipv6TargetResults = new List<InternetTargetObservation> { new InternetTargetObservation { Name = "Cloudflare IPv6", Status = "Success" } };
            string csvRow = CsvLogger.FormatSampleRow(csvPrivacy);
            csvSchema = csvSchema && CsvLogger.CsvColumnCount == csvRow.Split(',').Length &&
                csvRow.IndexOf("1.1.1.1", StringComparison.Ordinal) < 0 && csvRow.IndexOf("2606:4700:4700::1111", StringComparison.Ordinal) < 0;
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
                ActiveInterfaceType = "Wi-Fi",
                WifiRelevant = true,
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

