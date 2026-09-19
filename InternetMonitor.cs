using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Options;

public sealed class InternetMonitor : BackgroundService
{
    private const string ProbeUrl = "https://speed.cloudflare.com/cdn-cgi/trace";
    private const string DownloadUrl = "https://speed.cloudflare.com/__down";
    private const string UploadUrl = "https://speed.cloudflare.com/__up";
    private const string ConnectionIdentityUrl = "https://ipinfo.io/json";
    private readonly IHttpClientFactory _clients;
    private readonly SampleStore _store;
    private readonly MonitorOptions _options;
    private readonly ILogger<InternetMonitor> _log;
    private DateTimeOffset _nextSpeedTest = DateTimeOffset.MinValue;
    private DateTimeOffset _nextIdentityCheck = DateTimeOffset.MinValue;
    private Task? _identityCheckTask;
    private double? _lastConnectivityLatency;
    private DateTimeOffset? _lastConnectivitySuccess;
    public string SessionId { get; private set; } = Guid.NewGuid().ToString("N");
    private int _successes;
    private volatile bool _requestFastCheck;
    private DateTimeOffset _heartbeat = DateTimeOffset.UtcNow;
    private readonly object _sessionGate = new();
    private DateTimeOffset? _scheduledCheck;
    public DateTimeOffset? NextConnectivityCheck
    {
        get { lock (_sessionGate) return _scheduledCheck; }
        private set { lock (_sessionGate) _scheduledCheck = value; }
    }

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
        await Task.WhenAll(ConnectivityLoopAsync(stoppingToken),
            AuxiliaryLoopAsync(stoppingToken), WatchForSuspensionAsync(stoppingToken));
    }

    private async Task WatchForSuspensionAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), token);
            RefreshSession();
        }
    }

    private void RefreshSession()
    {
        lock (_sessionGate)
        {
            var now = DateTimeOffset.UtcNow;
            if (now - _heartbeat > TimeSpan.FromSeconds(20) || now < _heartbeat)
            {
                SessionId = Guid.NewGuid().ToString("N");
                _requestFastCheck = true;
            }
            _heartbeat = now;
        }
    }

    private async Task ConnectivityLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var due = await RunConnectivityCheckAsync(token);
            NextConnectivityCheck = due;
            while (DateTimeOffset.Now < due)
            {
                if (_requestFastCheck)
                {
                    _requestFastCheck = false;
                    _successes = 0;
                    var fastDue = DateTimeOffset.Now.AddSeconds(Math.Min(60, Math.Max(1, _options.ConnectivityIntervalSeconds)));
                    if (fastDue < due) due = fastDue;
                    NextConnectivityCheck = due;
                    break;
                }
                await Task.Delay(TimeSpan.FromSeconds(1), token);
            }
            while (DateTimeOffset.Now < due)
                await Task.Delay(TimeSpan.FromSeconds(1), token);
        }
    }

    private DateTimeOffset ScheduleNext(bool success, DateTimeOffset timestamp)
    {
        _successes = success ? Math.Min(_successes + 1, 16) : 0;
        var minimum = Math.Clamp(_options.ConnectivityIntervalSeconds, 1, 60);
        var maximum = Math.Clamp(_options.ConnectivityMaxIntervalSeconds, minimum, 3600);
        var seconds = success ? Math.Min(maximum, minimum * Math.Pow(2, Math.Max(0, _successes - 1))) : minimum;
        return timestamp.AddSeconds(seconds);
    }

    private async Task AuxiliaryLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (DateTimeOffset.UtcNow >= _nextIdentityCheck && (_identityCheckTask is null || _identityCheckTask.IsCompleted))
            {
                _nextIdentityCheck = DateTimeOffset.UtcNow.AddMinutes(1);
                _identityCheckTask = RunConnectionIdentityWithRetriesAsync(stoppingToken);
            }
            if (DateTimeOffset.UtcNow >= _nextSpeedTest)
            {
                await RunSpeedTestAsync(stoppingToken);
                _nextSpeedTest = DateTimeOffset.UtcNow.AddMinutes(Math.Max(1, _options.SpeedTestIntervalMinutes));
            }
            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }
    }

    private async Task RunConnectionIdentityWithRetriesAsync(CancellationToken token)
    {
        var retryDelays = new[] { TimeSpan.Zero, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30) };
        foreach (var delay in retryDelays)
        {
            if (delay > TimeSpan.Zero) await Task.Delay(delay, token);
            if (await RunConnectionIdentityCheckAsync(token))
            {
                _nextIdentityCheck = DateTimeOffset.UtcNow.AddHours(6);
                return;
            }
        }

        _nextIdentityCheck = DateTimeOffset.UtcNow.AddMinutes(1);
    }

    private async Task<bool> RunConnectionIdentityCheckAsync(CancellationToken token)
    {
        try
        {
            using var response = await _clients.CreateClient("identity").GetAsync(ConnectionIdentityUrl, token);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(token);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: token);
            var root = json.RootElement;
            string? Read(string property) => root.TryGetProperty(property, out var value) ? value.GetString() : null;
            var identity = new ConnectionIdentity(DateTimeOffset.Now, true, Read("ip"), Read("org"),
                Read("city"), Read("region"), Read("country"), "ipinfo.io", null);
            await _store.AppendConnectionIdentityAsync(identity, token);
            _log.LogInformation("Connection provider: {Organization}; public IP: {PublicIp}", identity.Organization, identity.PublicIp);
            return true;
        }
        catch (Exception ex) when (!token.IsCancellationRequested)
        {
            var error = DescribeFailure(ex, "Provider lookup");
            await _store.AppendConnectionIdentityAsync(new(DateTimeOffset.Now, false, null, null, null, null, null,
                "ipinfo.io", error), token);
            _log.LogWarning("Connection provider lookup failed: {Message}", error);
            return false;
        }
    }

    private async Task<DateTimeOffset> RunConnectivityCheckAsync(CancellationToken token)
    {
        RefreshSession();
        var session = SessionId;
        var started = Stopwatch.StartNew();
        try
        {
            using var response = await _clients.CreateClient("probe").GetAsync(ProbeUrl, token);
            response.EnsureSuccessStatusCode();
            var timestamp = DateTimeOffset.Now;
            var latency = started.Elapsed.TotalMilliseconds;
            var nextCheck = ScheduleNext(true, timestamp);
            var maximumJitterGap = TimeSpan.FromSeconds(Math.Max(2, _options.ConnectivityIntervalSeconds) * 3);
            double? jitter = _lastConnectivityLatency.HasValue && _lastConnectivitySuccess.HasValue &&
                timestamp - _lastConnectivitySuccess.Value <= maximumJitterGap
                    ? Math.Abs(latency - _lastConnectivityLatency.Value)
                    : null;
            await _store.AppendConnectivityAsync(new(timestamp, true, latency, null,
                ProbeEndpoint: ProbeUrl, HttpStatus: (int)response.StatusCode, JitterMs: jitter,
                SessionId: session, NextCheckAt: nextCheck), token);
            _lastConnectivityLatency = latency;
            _lastConnectivitySuccess = timestamp;
            return nextCheck;
        }
        catch (Exception ex) when (!token.IsCancellationRequested)
        {
            var error = DescribeFailure(ex, "Connectivity check");
            var diagnostics = await DiagnoseFailureAsync(token);
            _log.LogWarning("Connectivity check failed: {Message}", error);
            var timestamp = DateTimeOffset.Now;
            var nextCheck = ScheduleNext(false, timestamp);
            _lastConnectivityLatency = null;
            _lastConnectivitySuccess = null;
            await _store.AppendConnectivityAsync(new(timestamp, false, null, error,
                ClassifyFailure(ex), diagnostics.DnsResolved, diagnostics.DnsAddresses,
                diagnostics.Tcp443Connected, ProbeUrl, SessionId: session, NextCheckAt: nextCheck), token);
            return nextCheck;
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
        if (!success) _requestFastCheck = true;
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

    private static string ClassifyFailure(Exception exception)
    {
        var root = exception.GetBaseException();
        if (exception is TaskCanceledException or TimeoutException) return "timeout";
        if (root is SocketException socket)
        {
            return socket.SocketErrorCode switch
            {
                SocketError.HostNotFound or SocketError.TryAgain or SocketError.NoData => "dns",
                SocketError.ConnectionRefused => "connection-refused",
                SocketError.NetworkDown or SocketError.NetworkUnreachable or SocketError.HostUnreachable => "network-unreachable",
                _ => $"socket-{socket.SocketErrorCode.ToString().ToLowerInvariant()}"
            };
        }
        if (exception is HttpRequestException) return "http-transport";
        return exception.GetType().Name.ToLowerInvariant();
    }

    private static async Task<(bool DnsResolved, string? DnsAddresses, bool Tcp443Connected)> DiagnoseFailureAsync(CancellationToken token)
    {
        const string host = "speed.cloudflare.com";
        try
        {
            using var dnsTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            dnsTimeout.CancelAfter(TimeSpan.FromSeconds(3));
            var addresses = await Dns.GetHostAddressesAsync(host, dnsTimeout.Token);
            var addressText = string.Join(";", addresses.Select(address => address.ToString()));
            try
            {
                using var tcp = new TcpClient();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(3));
                await tcp.ConnectAsync(host, 443, timeout.Token);
                return (addresses.Length > 0, addressText, true);
            }
            catch when (!token.IsCancellationRequested)
            {
                return (addresses.Length > 0, addressText, false);
            }
        }
        catch when (!token.IsCancellationRequested)
        {
            return (false, null, false);
        }
    }
}
