using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);
builder.Services.Configure<MonitorOptions>(builder.Configuration.GetSection("Monitor"));
builder.Services.AddSingleton<SampleStore>();
builder.Services.AddHostedService<InternetMonitor>();
builder.Services.AddHttpClient("probe", client => client.Timeout = TimeSpan.FromSeconds(5));
builder.Services.AddHttpClient("speed", client => client.Timeout = TimeSpan.FromSeconds(90));

var app = builder.Build();
var options = app.Services.GetRequiredService<IOptions<MonitorOptions>>().Value;
app.Urls.Add(options.DashboardUrl);
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/connectivity", (SampleStore store, CancellationToken token) =>
    store.ReadRecentAsync<ConnectivitySample>("connectivity.jsonl", 8640, token));
app.MapGet("/api/speed-tests", (SampleStore store, CancellationToken token) =>
    store.ReadRecentAsync<SpeedSample>("speed-tests.jsonl", 2016, token));
app.MapGet("/api/status", async (SampleStore store, CancellationToken token) => new
{
    connectivity = (await store.ReadRecentAsync<ConnectivitySample>("connectivity.jsonl", 1, token)).LastOrDefault(),
    speedTest = (await store.ReadRecentAsync<SpeedSample>("speed-tests.jsonl", 1, token)).LastOrDefault(),
    intervals = new { options.ConnectivityIntervalSeconds, options.SpeedTestIntervalMinutes }
});

await app.RunAsync();
