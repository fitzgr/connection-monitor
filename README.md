# Connection Monitor

A small Windows app that continuously records internet availability and periodically measures download speed, upload speed, latency, and jitter. Failed tests are written as explicit outage records and displayed in the local dashboard. If a test fails partway through—such as after download but before upload—the completed measurements are retained and the failed phase is identified.

## Initial monitoring schedule

- Connectivity check every **10 seconds**
- Full download/upload speed test every **5 minutes**, beginning immediately
- 25 MB download and 10 MB upload per full test (about 10.1 GB/day at the default interval)

All values are editable in `appsettings.json`. Frequent full tests are useful during initial diagnosis but consume bandwidth; after establishing the pattern, a 15-minute interval reduces usage to roughly 3.4 GB/day.

## Run from source

Install the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0), then:

```powershell
dotnet run
```

Open <http://127.0.0.1:5274>. Records are saved beside the app in `data/` as both CSV and JSON Lines files.

## Publish a standalone Windows executable

```powershell
dotnet publish -c Release -r win-x64 --self-contained true
```

The executable is placed under `bin\Release\net8.0\win-x64\publish\` and does not require .NET to be installed on the monitoring PC.

## Notes

Measurements use Cloudflare's public speed-test endpoints. Results are intended to document trends, failures, and degraded periods rather than reproduce Bell's exact server-to-modem test. Because this app runs on the tenant's computer, it measures the complete path—including Wi-Fi or Ethernet—and cannot isolate Bell's line from the landlord's router by itself.
