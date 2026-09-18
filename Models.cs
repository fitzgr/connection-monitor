using System.Text.Json.Serialization;

public sealed record ConnectivitySample(
    DateTimeOffset Timestamp,
    bool Online,
    double? LatencyMs,
    string? Error);

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

public sealed class MonitorOptions
{
    public int ConnectivityIntervalSeconds { get; set; } = 10;
    public int SpeedTestIntervalMinutes { get; set; } = 5;
    public int DownloadMegabytes { get; set; } = 25;
    public int UploadMegabytes { get; set; } = 10;
    public string DashboardUrl { get; set; } = "http://127.0.0.1:5274";
}
