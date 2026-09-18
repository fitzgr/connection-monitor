using System.Text.Json.Serialization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

public sealed record ConnectivitySample(
    DateTimeOffset Timestamp,
    bool Online,
    double? LatencyMs,
    string? Error,
    string? FailureType = null,
    bool? DnsResolved = null,
    string? DnsAddresses = null,
    bool? Tcp443Connected = null,
    string? ProbeEndpoint = null,
    int? HttpStatus = null,
    double? JitterMs = null);

public sealed record SpeedSample(
    DateTimeOffset Timestamp,
    bool Success,
    double? LatencyMs,
    double? JitterMs,
    double? DownloadMbps,
    double? UploadMbps,
    double DurationSeconds,
    string? FailedPhase,
    string? Error);

public sealed record ConnectionIdentity(
    DateTimeOffset Timestamp,
    bool Success,
    string? PublicIp,
    string? Organization,
    string? City,
    string? Region,
    string? Country,
    string Source,
    string? Error);

public sealed class MonitorOptions
{
    public int ConnectivityIntervalSeconds { get; set; } = 10;
    public int SpeedTestIntervalMinutes { get; set; } = 5;
    public int DownloadMegabytes { get; set; } = 25;
    public int UploadMegabytes { get; set; } = 10;
    public string DashboardUrl { get; set; } = "http://127.0.0.1:5274";
}

public sealed record LocalConnectionInfo(
    string Type,
    string? InterfaceName,
    string? LocalIp,
    long? LinkSpeedMbps)
{
    public static LocalConnectionInfo Detect()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect("1.1.1.1", 53);
            var localIp = (socket.LocalEndPoint as IPEndPoint)?.Address;
            var networkInterface = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(item => item.OperationalStatus == OperationalStatus.Up &&
                    item.GetIPProperties().UnicastAddresses.Any(address => address.Address.Equals(localIp)));

            if (networkInterface is null)
                return new("Unknown", null, localIp?.ToString(), null);

            var type = networkInterface.NetworkInterfaceType switch
            {
                NetworkInterfaceType.Wireless80211 => "Wi-Fi",
                NetworkInterfaceType.Ethernet or NetworkInterfaceType.Ethernet3Megabit or
                    NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.FastEthernetT or
                    NetworkInterfaceType.GigabitEthernet => "Wired Ethernet",
                NetworkInterfaceType.Ppp => "PPP / cellular",
                _ => networkInterface.NetworkInterfaceType.ToString()
            };

            return new(type, networkInterface.Name, localIp?.ToString(),
                networkInterface.Speed > 0 ? networkInterface.Speed / 1_000_000 : null);
        }
        catch
        {
            return new("Unavailable", null, null, null);
        }
    }
}
