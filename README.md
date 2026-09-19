# Connection Monitor

A small Windows app that continuously records internet availability and periodically measures download speed, upload speed, latency, and jitter. Failed tests are written as explicit outage records and displayed in the local dashboard. If a test fails partway through—such as after download but before upload—the completed measurements are retained and the failed phase is identified.

The dashboard also identifies the public connection using `ipinfo.io`: service provider/ASN, public IP, city, region, and country. A snapshot is taken at startup and every six hours. A timeout triggers background retries after 10 and 30 seconds, followed by retries every minute until successful. Provider lookup runs independently of connectivity checks. Results are saved locally so provider or public-IP changes can be documented.

The active local route is identified as Wi-Fi, wired Ethernet, or another interface type. The dashboard also reports the adapter name, local IP address, and negotiated link speed. Detection follows the interface Windows actually selects for outbound internet traffic, so an unused Wi-Fi adapter does not cause a wired connection to be mislabeled.

The default one-hour graph overlays adaptive connectivity checks on periodic speed measurements. Short outages between checks may go undetected. When an HTTPS probe fails, the monitor classifies the transport failure and separately tests DNS resolution and TCP port 443. These diagnostics, the probe endpoint, HTTP status, resolved addresses, and error details are retained in `connectivity-diagnostics.csv` and presented in the recent-outages table for ISP support. The original compact `connectivity.csv` remains available for simple analysis.

The combined chart plots download and upload speed against the left Mbps axis. Latency (solid amber) and jitter (dashed purple) use an independently scaled right millisecond axis, keeping transmission-delay spikes visible without distorting the bandwidth scale.

## Reading the connectivity timeline

The timeline heading identifies the interval selected in the graph dropdown (default: **1 hour — detailed**). Its coverage text shows how much of that interval has recorded observations. Periods when the app was not collecting data are unobserved gaps, not assumed uptime or outages.

- **Connected** and **Outage** show accumulated durations within the selected interval. Their percentages use covered time (including estimated scheduled sleeps) only: connected time divided by connected plus outage time, and outage time divided by the same total.
- **Estimated uptime** is the connected percentage. Coverage is a separate percentage of the entire selected interval.
- **Average uptime / Average outage** summarize completed observed connection periods. **Longest uptime / Longest outage** show the longest recorded periods in view; **Current streak** shows the ongoing up or down period when recent observations are available.
- Light mint sections indicate connected periods; striped coral sections indicate outages. Muted sections represent unobserved time.

Durations in seconds are displayed horizontally beneath the timeline sections without a unit suffix. Connected durations share the upper mint row; outage durations share a separate lower coral row. Numbers may extend beyond narrow sections and overlap in crowded views. **Hover over a section to see its duration in seconds**, including whether it is ongoing, a visible portion clipped by the selected range, or a partial observation. Durations are estimates from recorded checks, not exact packet-level transition times.

The **Connection-cycle durations** chart shows connected and outage periods over time, with bar height representing duration. Hover over its bars for details. The recent-outage table supplies failure details and available latency/jitter measurements before failure and at recovery for troubleshooting.

## Initial monitoring schedule

- Connectivity check immediately, then after **1, 2, 4, 8, 16, 32, and 60 minutes** of successive successful checks; further successes keep the one-hour interval.
- Any connectivity failure resets the delay to **1 minute**. The first recovery success waits one minute, then successive successes double the interval again.
- Full speed-test failures also request a connectivity check within one minute and reset its backoff.
- Full download/upload speed test every **5 minutes**, beginning immediately
- 25 MB download and 10 MB upload per full test (about 10.1 GB/day at the default interval)

All values are editable in `appsettings.json`. Frequent full tests are useful during initial diagnosis but consume bandwidth; after establishing the pattern, a 15-minute interval reduces usage to roughly 3.4 GB/day.

## Adaptive scheduling and graph gaps

The connectivity worker sleeps between HTTP GET checks. `ConnectivityIntervalSeconds` sets the minimum (default 60, capped at 60); `ConnectivityMaxIntervalSeconds` sets the maximum (default and hard cap 3600). The dashboard shows the next scheduled check. Speed tests and provider lookups retain independent schedules, so they can generate traffic while the connectivity worker sleeps.

Each new connectivity record saves its process/session ID and next scheduled check in `connectivity.jsonl`. The timeline, duration bars, outage bands, and summary metrics share the same period calculation. They bridge samples only within the same session and within the scheduled interval plus 30 seconds of completion tolerance. Restarts, detected computer suspension, and late/missing samples break the bridge. Legacy records without schedule metadata retain the previous 25-second continuity limit.

Bridged time is **estimated**, not continuously measured availability. A short outage entirely between checks can be missed; a failure may take up to an hour to be detected at maximum backoff. Timing transitions are placed at the check that detects the change. Latency and jitter remain actual measurements; no synthetic values are added during sleeps.

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

## Storage reliability

Dashboard reads and sample writes share one file-access lock. External Windows sharing/lock violations are retried five times with short delays (1.5 seconds total). Retries happen only when opening a file, avoiding duplicate records from replaying a partially completed write.

If saving still fails, the monitor logs a **Recording failed** storage error and continues. It does not convert a successful HTTP check into an outage. A failed connectivity save breaks timeline continuity and returns checking to the minimum interval. A record may be missing or partially saved across JSON/CSV files; these errors do not prove an internet failure. Existing history is retained.

The Windows build runs storage regression checks with concurrent dashboard reads/writes, a temporary exclusive file lock, and a persistent lock during a successful HTTP probe.

## Notes

Measurements use Cloudflare's public speed-test endpoints. Results are intended to document trends, failures, and degraded periods rather than reproduce Bell's exact server-to-modem test. Because this app runs on the tenant's computer, it measures the complete path—including Wi-Fi or Ethernet—and cannot isolate Bell's line from the landlord's router by itself.
