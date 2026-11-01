using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace NetworkMonitorApp
{
    internal sealed class NativeWifiSnapshot
    {
        public int? SignalPercent;
        public int? Channel;
        public string Band;
        public string RadioType;
        public string SignalSource;
        public string SignalPrecision;
        public string ChannelSource;
        public string ChannelPrecision;
        public string BandSource;
        public string BandPrecision;
        public string RadioSource;
        public string RadioPrecision;
        public string Ssid;
        public string Bssid;
        public bool AccessPointChanged;
    }

    internal static class NativeWifiReader
    {
        private const uint CurrentConnectionOpcode = 7;
        private const uint ChannelNumberOpcode = 8;
        private const uint RealtimeConnectionQualityOpcode = 19;
        private const int ConnectedState = 1;
        private const int BssInfrastructure = 1;
        private static readonly object Gate = new object();
        private static string lastBssidHash;

        internal static void BeginSession()
        {
            lock (Gate) lastBssidHash = null;
        }

        internal static string BandFromCenterFrequencyMHz(uint frequencyMHz)
        {
            if (frequencyMHz >= 2400 && frequencyMHz < 2500) return "2.4 GHz";
            if (frequencyMHz >= 4900 && frequencyMHz < 5925) return "5 GHz";
            if (frequencyMHz >= 5925 && frequencyMHz <= 7125) return "6 GHz";
            return null;
        }

        internal static string BandFromCenterFrequencyKHz(uint frequencyKHz)
        {
            if (frequencyKHz == 0) return null;
            return BandFromCenterFrequencyMHz(frequencyKHz / 1000);
        }

        internal static string PhyName(uint phy)
        {
            switch (phy)
            {
                case 1: return "802.11 FHSS";
                case 2: return "802.11 DSSS";
                case 3: return "802.11 IR";
                case 4: return "802.11a / OFDM";
                case 5: return "802.11b / HRDSSS";
                case 6: return "802.11g / ERP";
                case 7: return "802.11n / HT";
                case 8: return "802.11ac / VHT";
                case 9: return "802.11ad / DMG";
                case 10: return "802.11ax / HE";
                case 11: return "802.11be / EHT";
                default: return null;
            }
        }

        internal static NativeWifiSnapshot Read(string interfaceId)
        {
            NativeWifiSnapshot empty = new NativeWifiSnapshot();
            IntPtr client = IntPtr.Zero;
            IntPtr interfaceList = IntPtr.Zero;
            try
            {
                uint negotiatedVersion;
                if (WlanOpenHandle(2, IntPtr.Zero, out negotiatedVersion, out client) != 0 || client == IntPtr.Zero) return empty;
                if (WlanEnumInterfaces(client, IntPtr.Zero, out interfaceList) != 0 || interfaceList == IntPtr.Zero) return empty;

                uint count = unchecked((uint)Marshal.ReadInt32(interfaceList));
                if (count == 0 || count > 64) return empty;
                int itemSize = Marshal.SizeOf(typeof(WlanInterfaceInfo));
                IntPtr selected = IntPtr.Zero;
                Guid requested;
                bool hasRequested = Guid.TryParse(interfaceId, out requested);
                for (uint i = 0; i < count; i++)
                {
                    IntPtr item = IntPtr.Add(interfaceList, 8 + checked((int)i * itemSize));
                    WlanInterfaceInfo info = (WlanInterfaceInfo)Marshal.PtrToStructure(item, typeof(WlanInterfaceInfo));
                    if (info.State != ConnectedState) continue;
                    if (hasRequested && info.InterfaceGuid == requested) { selected = item; break; }
                    if (!hasRequested && selected == IntPtr.Zero) selected = item;
                }
                if (selected == IntPtr.Zero) return empty;
                WlanInterfaceInfo selectedInfo = (WlanInterfaceInfo)Marshal.PtrToStructure(selected, typeof(WlanInterfaceInfo));

                IntPtr connectionData = IntPtr.Zero;
                IntPtr qualityData = IntPtr.Zero;
                IntPtr channelData = IntPtr.Zero;
                try
                {
                    uint size;
                    if (WlanQueryInterface(client, ref selectedInfo.InterfaceGuid, CurrentConnectionOpcode, IntPtr.Zero, out size, out connectionData, IntPtr.Zero) != 0 ||
                        connectionData == IntPtr.Zero || size < Marshal.SizeOf(typeof(WlanConnectionAttributes))) return empty;

                    WlanConnectionAttributes connection = (WlanConnectionAttributes)Marshal.PtrToStructure(connectionData, typeof(WlanConnectionAttributes));
                    NativeWifiSnapshot result = new NativeWifiSnapshot();
                    WlanAssociationAttributes association = connection.Association;
                    if (association.SignalQuality <= 100)
                    {
                        result.SignalPercent = (int)association.SignalQuality;
                        result.SignalSource = "Windows Native Wi-Fi API (WlanQueryInterface/current_connection)";
                        result.SignalPrecision = "Driver-reported integer quality, 0–100%; not a direct interference or throughput measurement.";
                    }
                    result.RadioType = PhyName(association.PhyType);
                    if (!String.IsNullOrEmpty(result.RadioType))
                    {
                        result.RadioSource = "Windows Native Wi-Fi API (WLAN_ASSOCIATION_ATTRIBUTES.dot11PhyType)";
                        result.RadioPrecision = "Exact PHY enum reported by Windows/driver; it does not guarantee negotiated application throughput.";
                    }
                    byte[] mac = association.Bssid ?? new byte[0];
                    if (mac.Length == 6) result.Bssid = BitConverter.ToString(mac);
                    uint ssidLength = Math.Min(association.Ssid.Length, 32u);
                    if (ssidLength > 0 && association.Ssid.Bytes != null && association.Ssid.Bytes.Length >= ssidLength)
                        result.Ssid = Encoding.UTF8.GetString(association.Ssid.Bytes, 0, (int)ssidLength);

                    uint channel;
                    if (QueryUInt32(client, ref selectedInfo.InterfaceGuid, ChannelNumberOpcode, out channel) && channel > 0 && channel <= 233)
                    {
                        result.Channel = (int)channel;
                        result.ChannelSource = "Windows Native Wi-Fi API (WlanQueryInterface/channel_number)";
                        result.ChannelPrecision = "Integer channel reported for the active interface; exact channel number, not a measure of signal quality.";
                    }

                    uint phy = association.PhyType;
                    if (WlanQueryInterface(client, ref selectedInfo.InterfaceGuid, RealtimeConnectionQualityOpcode, IntPtr.Zero, out size, out qualityData, IntPtr.Zero) == 0 &&
                        qualityData != IntPtr.Zero && size >= 24)
                    {
                        phy = unchecked((uint)Marshal.ReadInt32(qualityData, 0));
                        uint links = unchecked((uint)Marshal.ReadInt32(qualityData, 20));
                        if (links > 0 && links <= 16 && size >= 32)
                        {
                            uint frequencyKHz = unchecked((uint)Marshal.ReadInt32(qualityData, 28));
                            result.Band = BandFromCenterFrequencyKHz(frequencyKHz);
                            if (result.Band != null)
                            {
                                result.BandSource = "Windows Native Wi-Fi API (WLAN_REALTIME_CONNECTION_QUALITY_LINK_INFO.ulChannelCenterFrequencyMhz)";
                                result.BandPrecision = "Band mapped from the reported center frequency, converted from Windows-reported kHz; no inference from channel number.";
                            }
                        }
                    }
                    if (String.IsNullOrEmpty(result.RadioType)) result.RadioType = PhyName(phy);
                    if (String.IsNullOrEmpty(result.RadioSource) && !String.IsNullOrEmpty(result.RadioType))
                    {
                        result.RadioSource = "Windows Native Wi-Fi API (WLAN_REALTIME_CONNECTION_QUALITY.dot11PhyType)";
                        result.RadioPrecision = "Exact PHY enum reported by Windows/driver; does not guarantee negotiated application throughput.";
                    }
                    // The native API documents rates without specifying their unit.
                    // Leave Mbps fields unset; the caller may use its explicit-Mbps netsh fallback.

                    if (!String.IsNullOrWhiteSpace(result.Bssid))
                    {
                        string hash;
                        using (SHA256 sha = SHA256.Create()) hash = Convert.ToBase64String(sha.ComputeHash(Encoding.ASCII.GetBytes(result.Bssid)));
                        lock (Gate)
                        {
                            result.AccessPointChanged = !String.IsNullOrEmpty(lastBssidHash) && !String.Equals(lastBssidHash, hash, StringComparison.Ordinal);
                            lastBssidHash = hash;
                        }
                    }
                    return result;
                }
                finally
                {
                    if (connectionData != IntPtr.Zero) WlanFreeMemory(connectionData);
                    if (qualityData != IntPtr.Zero) WlanFreeMemory(qualityData);
                    if (channelData != IntPtr.Zero) WlanFreeMemory(channelData);
                }
            }
            catch { return empty; }
            finally
            {
                if (interfaceList != IntPtr.Zero) WlanFreeMemory(interfaceList);
                if (client != IntPtr.Zero) WlanCloseHandle(client, IntPtr.Zero);
            }
        }

        private static bool QueryUInt32(IntPtr client, ref Guid guid, uint opcode, out uint value)
        {
            value = 0;
            IntPtr data = IntPtr.Zero;
            try
            {
                uint size;
                if (WlanQueryInterface(client, ref guid, opcode, IntPtr.Zero, out size, out data, IntPtr.Zero) != 0 || data == IntPtr.Zero || size < 4) return false;
                value = unchecked((uint)Marshal.ReadInt32(data));
                return true;
            }
            finally { if (data != IntPtr.Zero) WlanFreeMemory(data); }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WlanInterfaceInfo
        {
            public Guid InterfaceGuid;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Description;
            public int State;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Dot11Ssid
        {
            public uint Length;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] Bytes;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WlanAssociationAttributes
        {
            public Dot11Ssid Ssid;
            public int BssType;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)] public byte[] Bssid;
            public uint PhyType;
            public uint PhyIndex;
            public uint SignalQuality;
            public uint ReceiveRate;
            public uint TransmitRate;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WlanSecurityAttributes
        {
            [MarshalAs(UnmanagedType.Bool)] public bool SecurityEnabled;
            [MarshalAs(UnmanagedType.Bool)] public bool OneXEnabled;
            public uint AuthenticationAlgorithm;
            public uint CipherAlgorithm;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WlanConnectionAttributes
        {
            public int State;
            public int ConnectionMode;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string ProfileName;
            public WlanAssociationAttributes Association;
            public WlanSecurityAttributes Security;
        }

        [DllImport("wlanapi.dll", ExactSpelling = true)]
        private static extern uint WlanOpenHandle(uint clientVersion, IntPtr reserved, out uint negotiatedVersion, out IntPtr clientHandle);
        [DllImport("wlanapi.dll", ExactSpelling = true)]
        private static extern uint WlanEnumInterfaces(IntPtr clientHandle, IntPtr reserved, out IntPtr interfaceList);
        [DllImport("wlanapi.dll", ExactSpelling = true)]
        private static extern uint WlanQueryInterface(IntPtr clientHandle, ref Guid interfaceGuid, uint opcode, IntPtr reserved, out uint dataSize, out IntPtr data, IntPtr valueType);
        [DllImport("wlanapi.dll", ExactSpelling = true)]
        private static extern void WlanFreeMemory(IntPtr memory);
        [DllImport("wlanapi.dll", ExactSpelling = true)]
        private static extern uint WlanCloseHandle(IntPtr clientHandle, IntPtr reserved);
    }
}
