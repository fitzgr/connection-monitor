using System.Globalization;
using System.Text;
using System.Text.Json;

public sealed class SampleStore
{
    private readonly string _dataDirectory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public SampleStore(IHostEnvironment environment)
    {
        _dataDirectory = Path.Combine(AppContext.BaseDirectory, "data");
        Directory.CreateDirectory(_dataDirectory);
    }

    public async Task AppendConnectivityAsync(ConnectivitySample sample, CancellationToken token)
    {
        await AppendJsonLineAsync("connectivity.jsonl", sample, token);
        await AppendCsvAsync("connectivity.csv",
            "timestamp,online,latency_ms,error",
            $"{sample.Timestamp:O},{sample.Online.ToString().ToLowerInvariant()},{Number(sample.LatencyMs)},{Csv(sample.Error)}", token);
    }

    public async Task AppendSpeedAsync(SpeedSample sample, CancellationToken token)
    {
        await AppendJsonLineAsync("speed-tests.jsonl", sample, token);
        await AppendCsvAsync("speed-tests.csv",
            "timestamp,success,latency_ms,jitter_ms,download_mbps,upload_mbps,duration_seconds,failed_phase,error",
            $"{sample.Timestamp:O},{sample.Success.ToString().ToLowerInvariant()},{Number(sample.LatencyMs)},{Number(sample.JitterMs)},{Number(sample.DownloadMbps)},{Number(sample.UploadMbps)},{sample.DurationSeconds.ToString("0.00", CultureInfo.InvariantCulture)},{Csv(sample.FailedPhase)},{Csv(sample.Error)}", token);
    }

    public async Task<IReadOnlyList<T>> ReadRecentAsync<T>(string fileName, int limit, CancellationToken token)
    {
        var path = Path.Combine(_dataDirectory, fileName);
        if (!File.Exists(path)) return [];
        var lines = await File.ReadAllLinesAsync(path, token);
        return lines.TakeLast(limit)
            .Select(line => JsonSerializer.Deserialize<T>(line, _json))
            .Where(item => item is not null).Cast<T>().ToArray();
    }

    private async Task AppendJsonLineAsync<T>(string fileName, T value, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            await File.AppendAllTextAsync(Path.Combine(_dataDirectory, fileName),
                JsonSerializer.Serialize(value, _json) + Environment.NewLine, Encoding.UTF8, token);
        }
        finally { _gate.Release(); }
    }

    private async Task AppendCsvAsync(string fileName, string header, string row, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            var path = Path.Combine(_dataDirectory, fileName);
            if (!File.Exists(path)) await File.WriteAllTextAsync(path, header + Environment.NewLine, token);
            await File.AppendAllTextAsync(path, row + Environment.NewLine, token);
        }
        finally { _gate.Release(); }
    }

    private static string Number(double? value) => value?.ToString("0.00", CultureInfo.InvariantCulture) ?? "";
    private static string Csv(string? value) => value is null ? "" : $"\"{value.Replace("\"", "\"\"")}\"";
}
