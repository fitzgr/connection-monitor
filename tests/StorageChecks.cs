using System.Net;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

var store = new SampleStore(null!);
var file = Path.Combine(AppContext.BaseDirectory, "data", "connectivity.jsonl");
var before = (await store.ReadRecentAsync<ConnectivitySample>("connectivity.jsonl", int.MaxValue, default)).Count;
var tasks = Enumerable.Range(0, 100).Select(async i =>
{
    await store.AppendConnectivityAsync(new(DateTimeOffset.Now, true, i, null), default);
    var samples = await store.ReadRecentAsync<ConnectivitySample>("connectivity.jsonl", int.MaxValue, default);
    if (samples.Count == 0) throw new Exception("Missing saved sample.");
}).ToArray();
await Task.WhenAll(tasks);
var after = await store.ReadRecentAsync<ConnectivitySample>("connectivity.jsonl", int.MaxValue, default);
if (after.Count != before + 100) throw new Exception("Concurrent reads/writes lost or duplicated records.");

// Windows sharing semantics are the regression being tested here.
if (OperatingSystem.IsWindows())
{
    var held = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    var pending = store.AppendConnectivityAsync(new(DateTimeOffset.Now, true, 1, null), default);
    await Task.Delay(250);
    held.Dispose();
    await pending;
    if ((await store.ReadRecentAsync<ConnectivitySample>("connectivity.jsonl", int.MaxValue, default)).Count != after.Count + 1)
        throw new Exception("Transient lock retry lost or duplicated the sample.");

    using (var locked = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
    {
        var monitor = new InternetMonitor(new SuccessFactory(), store, Options.Create(new MonitorOptions()), NullLogger<InternetMonitor>.Instance);
        var session = monitor.SessionId;
        var method = typeof(InternetMonitor).GetMethod("RunConnectivityCheckAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task<DateTimeOffset>)method.Invoke(monitor, new object[] { CancellationToken.None })!;
        if (session == monitor.SessionId) throw new Exception("Failed persistence must break timeline continuity.");
    }
    var saved = await store.ReadRecentAsync<ConnectivitySample>("connectivity.jsonl", int.MaxValue, default);
    if (saved.Count != after.Count + 1 || saved.Any(s => !s.Online))
        throw new Exception("Storage failure created a false outage.");
}
else Console.WriteLine("Windows-only file sharing checks skipped on this OS.");
// Range selection must not truncate at the old API limits or trim the files.
var end = DateTimeOffset.UtcNow;
var name = $"range-{Guid.NewGuid():N}.jsonl";
var rangeFile = Path.Combine(AppContext.BaseDirectory, "data", name);
var json = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
try
{
    var records = Enumerable.Range(0, 10001)
        .Select(i => new ConnectivitySample(end.AddMinutes(i - 10000), true, 1, null));
    var original = string.Join("\n", records.Select(s => System.Text.Json.JsonSerializer.Serialize(s, json))) + "\n";
    await File.WriteAllTextAsync(rangeFile, original);
    var hour = await store.ReadRangeAsync<ConnectivitySample>(name, end.AddHours(-1), end, s => s.Timestamp, true, default);
    if (hour.Count != 62 || hour[0].Timestamp != end.AddMinutes(-61) || hour[^1].Timestamp != end)
        throw new Exception("Incorrect hour range or boundary sample.");
    var month = await store.ReadRangeAsync<ConnectivitySample>(name, end.AddDays(-30), end, s => s.Timestamp, true, default);
    if (month.Count != 10001) throw new Exception("History was truncated at a record limit.");
    var withoutBoundary = await store.ReadRangeAsync<ConnectivitySample>(name, end.AddHours(-1), end, s => s.Timestamp, false, default);
    if (withoutBoundary.Count != 61) throw new Exception("Unexpected records outside range.");
    var empty = await store.ReadRangeAsync<ConnectivitySample>(name, end.AddDays(1), end.AddDays(2), s => s.Timestamp, false, default);
    if (empty.Count != 0) throw new Exception("Empty range returned old records.");
    var latest = await store.ReadRecentAsync<ConnectivitySample>(name, 1, default);
    if (latest.Count != 1 || latest[0].Timestamp != end) throw new Exception("Latest sample lookup regressed.");
    if (await File.ReadAllTextAsync(rangeFile) != original) throw new Exception("Reading modified saved history.");
}
finally { File.Delete(rangeFile); }
Console.WriteLine("Storage and history-range checks passed.");

sealed class SuccessFactory : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(new SuccessHandler());
}
sealed class SuccessHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") });
}
