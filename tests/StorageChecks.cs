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
Console.WriteLine("Storage checks passed.");

sealed class SuccessFactory : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(new SuccessHandler());
}
sealed class SuccessHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") });
}
