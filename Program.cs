using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);
builder.Services.Configure<MonitorOptions>(builder.Configuration.GetSection("Monitor"));
builder.Services.AddSingleton<SampleStore>();
builder.Services.AddSingleton<InternetMonitor>();
builder.Services.AddHostedService<InternetMonitor>(services => services.GetRequiredService<InternetMonitor>());
builder.Services.AddHttpClient("probe", client => client.Timeout = TimeSpan.FromSeconds(5));
builder.Services.AddHttpClient("speed", client => client.Timeout = TimeSpan.FromSeconds(90));
builder.Services.AddHttpClient("identity", client => client.Timeout = TimeSpan.FromSeconds(8));

var app = builder.Build();
var options = app.Services.GetRequiredService<IOptions<MonitorOptions>>().Value;
app.Urls.Add(options.DashboardUrl);
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/connectivity", (SampleStore store, CancellationToken token) =>
    store.ReadRecentAsync<ConnectivitySample>("connectivity.jsonl", 8640, token));
app.MapGet("/api/speed-tests", (SampleStore store, CancellationToken token) =>
    store.ReadRecentAsync<SpeedSample>("speed-tests.jsonl", 2016, token));
app.MapGet("/api/connection-identity", (SampleStore store, CancellationToken token) =>
    store.ReadRecentAsync<ConnectionIdentity>("connection-identity.jsonl", 100, token));
app.MapGet("/api/status", async (SampleStore store, InternetMonitor monitor, CancellationToken token) =>
{
    var identities = await store.ReadRecentAsync<ConnectionIdentity>("connection-identity.jsonl", 100, token);
    return new
    {
        connectivity = (await store.ReadRecentAsync<ConnectivitySample>("connectivity.jsonl", 1, token)).LastOrDefault(),
        speedTest = (await store.ReadRecentAsync<SpeedSample>("speed-tests.jsonl", 1, token)).LastOrDefault(),
        connectionIdentity = identities.LastOrDefault(identity => identity.Success) ?? identities.LastOrDefault(),
        localConnection = LocalConnectionInfo.Detect(),
        monitorSessionId = monitor.SessionId,
        nextConnectivityCheck = monitor.NextConnectivityCheck,
        intervals = new { options.ConnectivityIntervalSeconds, options.SpeedTestIntervalMinutes }
    };
});

await app.RunAsync();
