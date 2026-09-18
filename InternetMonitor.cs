using System.Diagnostics;
using System.Net.Http.Headers;
using Microsoft.Extensions.Options;

public sealed class InternetMonitor : BackgroundService
{
    private const string ProbeUrl = "https://speed.cloudflare.com/cdn-cgi/trace";
    private const string DownloadUrl = "https://speed.cloudflare.com/__down";
    private const string UploadUrl = "https://speed.cloudflare.com/__up";
    private readonly IHttpClientFactory _clients;
    private readonly SampleStore _store;
    private readonly MonitorOptions _options;
    private readonly ILogger<InternetMonitor> _log;
    private DateTimeOffset _nextSpeedTest = DateTimeOffset.MinValue;

    public InternetMonitor(IHttpClientFactory clients, SampleStore store,
        IOptions<MonitorOptions> options, ILogger<InternetMonitor> log)
    {
        _clients = clients;
        _store = store;
        _options = options.Value;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await RunConnectivityCheckAsync(stoppingToken);
            if (DateTimeOffset.UtcNow >= _nextSpeedTest)
            {
                await RunSpeedTestAsync(stoppingToken);
                _nextSpeedTest = DateTimeOffset.UtcNow.AddMinutes(Math.Max(1, _options.SpeedTestIntervalMinutes));
            }
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(2, _options.ConnectivityIntervalSeconds)), stoppingToken);
        }
    }

    private async Task RunConnectivityCheckAsync(CancellationToken token)
    {
        var started = Stopwatch.StartNew();
        try
        {
            using var response = await _clients.CreateClient("probe").GetAsync(ProbeUrl, token);
            response.EnsureSuccessStatusCode();
            await _store.AppendConnectivityAsync(new(DateTimeOffset.Now, true, started.Elapsed.TotalMilliseconds, null), token);
        }
        catch (Exception ex) when (!token.IsCancellationRequested)
        {
            var error = DescribeFailure(ex, "Connectivity check");
            _log.LogWarning("Connectivity check failed: {Message}", error);
            await _store.AppendConnectivityAsync(new(DateTimeOffset.Now, false, null, error), token);
        }
    }

    private async Task RunSpeedTestAsync(CancellationToken token)
    {
        var overall = Stopwatch.StartNew();
        double? latency = null, jitter = null, download = null, upload = null;
        string? failedPhase = null, error = null;

        try { (latency, jitter) = await MeasureLatencyAsync(token); }
        catch (Exception ex) when (!token.IsCancellationRequested) { failedPhase = "latency"; error = DescribeFailure(ex, "Latency test"); }

        if (failedPhase is null)
        {
            try { download = await MeasureDownloadAsync(token); }
            catch (Exception ex) when (!token.IsCancellationRequested) { failedPhase = "download"; error = DescribeFailure(ex, "Download test"); }
        }

        if (failedPhase is null)
        {
            try { upload = await MeasureUploadAsync(token); }
            catch (Exception ex) when (!token.IsCancellationRequested) { failedPhase = "upload"; error = DescribeFailure(ex, "Upload test"); }
        }

        var success = failedPhase is null;
        var sample = new SpeedSample(DateTimeOffset.Now, success, latency, jitter, download, upload,
            overall.Elapsed.TotalSeconds, failedPhase, error);
        await _store.AppendSpeedAsync(sample, token);
        if (success) _log.LogInformation("Speed test: {Down:F1} down / {Up:F1} up Mbps", download, upload);
        else _log.LogWarning("Speed test failed during {Phase}; completed phase results were retained: {Message}", failedPhase, error);
    }

    private async Task<(double Latency, double Jitter)> MeasureLatencyAsync(CancellationToken token)
    {
        var timings = new List<double>();
        for (var i = 0; i < 3; i++)
        {
            var timer = Stopwatch.StartNew();
            using var response = await _clients.CreateClient("speed").GetAsync(ProbeUrl, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            timings.Add(timer.Elapsed.TotalMilliseconds);
        }
        var latency = timings.Average();
        var jitter = timings.Zip(timings.Skip(1), (a, b) => Math.Abs(b - a)).DefaultIfEmpty(0).Average();
        return (latency, jitter);
    }

    private async Task<double> MeasureDownloadAsync(CancellationToken token)
    {
        var bytesRequested = Math.Max(1, _options.DownloadMegabytes) * 1_000_000L;
        var timer = Stopwatch.StartNew();
        using var response = await _clients.CreateClient("speed").GetAsync($"{DownloadUrl}?bytes={bytesRequested}", HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        var buffer = new byte[128 * 1024];
        long bytesRead = 0;
        int count;
        while ((count = await stream.ReadAsync(buffer, token)) > 0) bytesRead += count;
        return bytesRead * 8d / timer.Elapsed.TotalSeconds / 1_000_000d;
    }

    private async Task<double> MeasureUploadAsync(CancellationToken token)
    {
        var bytes = new byte[Math.Max(1, _options.UploadMegabytes) * 1_000_000];
        Random.Shared.NextBytes(bytes);
        using var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        var timer = Stopwatch.StartNew();
        using var response = await _clients.CreateClient("speed").PostAsync(UploadUrl, content, token);
        response.EnsureSuccessStatusCode();
        return bytes.LongLength * 8d / timer.Elapsed.TotalSeconds / 1_000_000d;
    }

    private static string DescribeFailure(Exception exception, string phase)
    {
        return exception is TaskCanceledException
            ? $"{phase} timed out"
            : $"{phase} failed: {exception.GetBaseException().Message}";
    }
}
