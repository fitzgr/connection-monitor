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
        await AppendCsvAsync("connectivity-diagnostics.csv",
            "timestamp,online,latency_ms,failure_type,dns_resolved,dns_addresses,tcp_443_connected,probe_endpoint,http_status,error",
            $"{sample.Timestamp:O},{sample.Online.ToString().ToLowerInvariant()},{Number(sample.LatencyMs)},{Csv(sample.FailureType)},{Boolean(sample.DnsResolved)},{Csv(sample.DnsAddresses)},{Boolean(sample.Tcp443Connected)},{Csv(sample.ProbeEndpoint)},{sample.HttpStatus?.ToString(CultureInfo.InvariantCulture) ?? ""},{Csv(sample.Error)}", token);
        await AppendCsvAsync("connectivity-quality.csv",
            "timestamp,online,latency_ms,jitter_ms",
            $"{sample.Timestamp:O},{sample.Online.ToString().ToLowerInvariant()},{Number(sample.LatencyMs)},{Number(sample.JitterMs)}", token);
    }

    public async Task AppendSpeedAsync(SpeedSample sample, CancellationToken token)
    {
        await AppendJsonLineAsync("speed-tests.jsonl", sample, token);
        await AppendCsvAsync("speed-tests.csv",
            "timestamp,success,latency_ms,jitter_ms,download_mbps,upload_mbps,duration_seconds,failed_phase,error",
            $"{sample.Timestamp:O},{sample.Success.ToString().ToLowerInvariant()},{Number(sample.LatencyMs)},{Number(sample.JitterMs)},{Number(sample.DownloadMbps)},{Number(sample.UploadMbps)},{sample.DurationSeconds.ToString("0.00", CultureInfo.InvariantCulture)},{Csv(sample.FailedPhase)},{Csv(sample.Error)}", token);
    }

    public async Task AppendConnectionIdentityAsync(ConnectionIdentity sample, CancellationToken token)
    {
        await AppendJsonLineAsync("connection-identity.jsonl", sample, token);
        await AppendCsvAsync("connection-identity.csv",
            "timestamp,success,public_ip,organization,city,region,country,source,error",
            $"{sample.Timestamp:O},{sample.Success.ToString().ToLowerInvariant()},{Csv(sample.PublicIp)},{Csv(sample.Organization)},{Csv(sample.City)},{Csv(sample.Region)},{Csv(sample.Country)},{Csv(sample.Source)},{Csv(sample.Error)}", token);
    }

    public async Task<IReadOnlyList<T>> ReadRecentAsync<T>(string fileName, int limit, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            var path = Path.Combine(_dataDirectory, fileName);
            if (!File.Exists(path)) return [];
            await using var stream = await OpenWithRetryAsync(path, FileMode.Open, FileAccess.Read, token);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var lines = new Queue<string>();
            while (await reader.ReadLineAsync(token) is { } line)
            {
                lines.Enqueue(line);
                if (lines.Count > Math.Max(0, limit)) lines.Dequeue();
            }
            return lines
                .Select(line => JsonSerializer.Deserialize<T>(line, _json))
                .Where(item => item is not null).Cast<T>().ToArray();
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<T>> ReadRangeAsync<T>(string fileName,
        DateTimeOffset from, DateTimeOffset to, Func<T, DateTimeOffset> timestamp,
        bool includePrevious, CancellationToken token) where T : class
    {
        await _gate.WaitAsync(token);
        try
        {
            var path = Path.Combine(_dataDirectory, fileName);
            if (!File.Exists(path)) return [];
            await using var stream = await OpenWithRetryAsync(path, FileMode.Open, FileAccess.Read, token);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var selected = new List<T>();
            T? previous = null;
            while (await reader.ReadLineAsync(token) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var sample = JsonSerializer.Deserialize<T>(line, _json);
                if (sample is null) continue;
                var at = timestamp(sample);
                if (at >= from && at <= to) selected.Add(sample);
                else if (includePrevious && at < from &&
                    (previous is null || at > timestamp(previous))) previous = sample;
            }
            // One boundary sample lets the graph clip a scheduled sleep at the range start.
            // Existing session/schedule checks still decide whether it can be bridged.
            if (previous is not null) selected.Add(previous);
            return selected.OrderBy(timestamp).ToArray();
        }
        finally { _gate.Release(); }
    }

    private async Task AppendJsonLineAsync<T>(string fileName, T value, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            await using var stream = await OpenWithRetryAsync(Path.Combine(_dataDirectory, fileName),
                FileMode.Append, FileAccess.Write, token);
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, _json) + Environment.NewLine);
            await stream.WriteAsync(bytes, token);
        }
        finally { _gate.Release(); }
    }

    private async Task AppendCsvAsync(string fileName, string header, string row, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            var path = Path.Combine(_dataDirectory, fileName);
            await using var stream = await OpenWithRetryAsync(path, FileMode.Append, FileAccess.Write, token);
            var text = (stream.Length == 0 ? header + Environment.NewLine : "") + row + Environment.NewLine;
            await stream.WriteAsync(Encoding.UTF8.GetBytes(text), token);
        }
        finally { _gate.Release(); }
    }

    private static string Number(double? value) => value?.ToString("0.00", CultureInfo.InvariantCulture) ?? "";
    private static async Task<FileStream> OpenWithRetryAsync(string path, FileMode mode,
        FileAccess access, CancellationToken token)
    {
        // Retry opening only: replaying a write could duplicate a partially saved record.
        for (var attempt = 0; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(path, mode, access, FileShare.Read,
                    4096, FileOptions.Asynchronous);
            }
            catch (IOException ex) when (attempt < 5 && (ex.HResult & 0xffff) is 32 or 33)
            {
                await Task.Delay(100 * (attempt + 1), token);
            }
        }
    }
    private static string Boolean(bool? value) => value?.ToString().ToLowerInvariant() ?? "";
    private static string Csv(string? value) => value is null ? "" : $"\"{value.Replace("\"", "\"\"")}\"";
}
