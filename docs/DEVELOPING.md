# Developing Jellytel

A short guide for contributors. Covers code layout, the Jellyfin APIs you'll touch, and the gotchas the early code already paid for.

## Project layout

```
Jellyfin.Plugin.Jellytel/
├── Plugin.cs                       BasePlugin<T>, IHasWebPages — identity & config page
├── PluginServiceRegistrator.cs     Registers IHostedServices with Jellyfin's DI container
├── OpenTelemetryBootstrapper.cs    Logs pipeline (current implementation)
├── Configuration/
│   ├── PluginConfiguration.cs      Settings model (serialized to XML by Jellyfin)
│   └── configPage.html             Settings UI, embedded as a resource
└── (future)
    ├── Metrics/                    One file per signal family (Streams, Transcode, Library…)
    ├── Tracing/                    Activity sources, ASP.NET integration
    └── Sessions/                   Session-event listeners that fan into Metrics/Tracing
```

**Rule of thumb:** one `IHostedService` per long-lived subsystem (logs bootstrapper, metrics collector, session listener). Register them in `PluginServiceRegistrator`. Keep `Plugin.cs` for identity only — never put logic there.

## How a plugin actually loads

1. Jellyfin scans `<data>/plugins/<PluginName>/*.dll` and finds your `Plugin : BasePlugin<T>`.
2. It loads `IPluginServiceRegistrator` *before* the host builds — this is your one chance to inject `IHostedService`s, named `HttpClient`s, options, etc.
3. After the host starts, every `IHostedService.StartAsync` runs.

**Critical:** an exception from `StartAsync` brings down all of Jellyfin. Always wrap your start logic in try/catch and log + bail rather than throwing. The current `OpenTelemetryBootstrapper` learned this the hard way.

## Configuration

`BasePluginConfiguration` is XML-serialized to disk. Add new settings as `public` properties with defaults set in the constructor — Jellyfin will round-trip them. The config page in `configPage.html` reads/writes via `ApiClient.getPluginConfiguration` / `updatePluginConfiguration` keyed on the plugin GUID.

Subscribe to `Plugin.Instance!.ConfigurationChanged` in your `IHostedService` if you want hot-reload (no server restart on save).

## Jellyfin APIs worth knowing

Inject these into your hosted services / controllers — Jellyfin's DI provides them.

### Sessions & playback (Phase 1–3)

`MediaBrowser.Controller.Session.ISessionManager` — your main hook for stream telemetry.

Events:
- `PlaybackStart` / `PlaybackStopped` — emit stream-lifecycle log events, increment counters
- `PlaybackProgress` — fires periodically; sample bitrate / buffer here, don't allocate per call
- `SessionStarted` / `SessionEnded` — for connection-level signals
- `Sessions` collection — poll this for "currently active streams" gauges

Pattern — subscribe in an `IHostedService`, unsubscribe on stop:

```csharp
public class StreamMetrics : IHostedService
{
    private readonly ISessionManager _sessions;

    public StreamMetrics(ISessionManager sessions) => _sessions = sessions;

    public Task StartAsync(CancellationToken ct)
    {
        _sessions.PlaybackStart += OnStart;
        _sessions.PlaybackStopped += OnStop;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        _sessions.PlaybackStart -= OnStart;
        _sessions.PlaybackStopped -= OnStop;
        return Task.CompletedTask;
    }

    private void OnStart(object? sender, PlaybackProgressEventArgs e)
    {
        var method = e.Session.PlayState.PlayMethod;       // DirectPlay / Transcode / …
        var bitrate = e.Session.TranscodingInfo?.Bitrate;  // null if not transcoding
        // record to your Meter / log here
    }

    private void OnStop(object? sender, PlaybackStopEventArgs e) { /* … */ }
}
```

Register it in `PluginServiceRegistrator.RegisterServices` with `serviceCollection.AddHostedService<StreamMetrics>()`.

What you can read off `SessionInfo`:
- `PlayState.PlayMethod` — `DirectPlay` / `DirectStream` / `Transcode` (Phase 1)
- `PlayState.IsPaused` — pause/resume signal
- `PlayState.PositionTicks` — playhead, useful with `NowPlayingItem.RunTimeTicks`
- `TranscodingInfo.Bitrate` / `Framerate` / `CompletionPercentage` — per-stream bitrate, the encode-speed ratio
- `TranscodingInfo.TranscodeReasons` — flags enum (codec mismatch, container, bitrate cap…)
- `TranscodingInfo.HardwareAccelerationType` — qsv / nvenc / vaapi / none
- `Client`, `DeviceType`, `UserId`, `UserName`, `RemoteEndPoint` — attribution dimensions

Buffer-ahead is **not** directly exposed. Most likely you derive it from `PlaybackProgress` deltas plus the active transcode position; otherwise you need client-side reporting.

### Library (Phase 4)

`MediaBrowser.Controller.Library.ILibraryManager`:
- `ItemAdded` / `ItemUpdated` / `ItemRemoved` — change events with `BaseItem` payload
- Use `GetItemList(InternalItemsQuery)` for periodic count gauges by type

`Jellyfin.Data.Enums.BaseItemKind` — the enum to bucket counts by (Movie, Episode, Audio, Series, Book, etc.).

### Scheduled tasks (Phase 4)

`MediaBrowser.Model.Tasks.ITaskManager`:
- `TaskExecuting` / `TaskCompleted` — fire on every scheduled task; the completion args carry duration and outcome.

### Logs (current)

The plugin wraps Serilog's static `Log.Logger` with a fan-out logger that writes to both the host pipeline and the OTLP sink. Reapply on `ConfigurationChanged`. Always restore the original on `StopAsync` so a disable doesn't silence Jellyfin.

## Recommended OTel libraries

For the planned metrics/traces work, use the standard OpenTelemetry .NET stack:

- `OpenTelemetry.Extensions.Hosting` — registers the SDK against Jellyfin's `IServiceCollection`
- `OpenTelemetry.Exporter.OpenTelemetryProtocol` — OTLP/gRPC and OTLP/HTTP
- `OpenTelemetry.Instrumentation.AspNetCore` — free request traces and HTTP metrics for the Jellyfin REST API
- `OpenTelemetry.Instrumentation.Runtime` — GC, threadpool, exception counters
- `System.Diagnostics.Metrics.Meter` (BCL) — define your own counters/histograms; OTel picks them up automatically

Jellyfin already exposes a number of `System.Diagnostics.Metrics.Meter` instruments internally. Listing the host meter (e.g. via `MeterProviderBuilder.AddMeter("Jellyfin.*")`) often gets you metrics for free before you write any custom ones.

## Dependency hygiene (read before adding a NuGet)

Jellyfin loads each plugin into its own `AssemblyLoadContext`, but **type identity is shared for assemblies the host already provides**. Bundling a different version of one of those dlls causes silent failures (or, worse, host crashes — see the v1.0.0.0 incident).

Rules:
- **Match the host's version exactly** for anything Jellyfin ships: `Serilog`, `Microsoft.Extensions.*`, `Newtonsoft.Json`, `Jellyfin.*`. Pin to the same `Version` Jellyfin uses; you can find these in [`Directory.Packages.props`](https://github.com/jellyfin/jellyfin/blob/master/Directory.Packages.props).
- **Add `<ExcludeAssets>runtime</ExcludeAssets>`** to those package references so the dll doesn't get copied into the publish output.
- The publish workflow's packaging step is the second line of defense — only the dlls Jellyfin doesn't have should land in the release zip.

If the plugin loads but blows up on a method call with `FileNotFoundException` for an assembly that exists, that's almost always a version-mismatch problem.

## Build, package, install

```
dotnet build -c Release                          # compile
dotnet publish Jellyfin.Plugin.Jellytel -c Release -o publish   # full output
```

VS Code: `tasks.json` ships a `build-and-copy` task that drops the dll straight into your local Jellyfin plugin dir. Update `.vscode/settings.json` to point at your install paths.

Releases are tag-driven (`v1.2.3.4`) — see [`RELEASING.md`](RELEASING.md). The workflow zips the published output, attaches it to a GitHub Release, and updates `manifest.json` for the catalog feed.

## Testing locally without a release

```
rm -rf <jellyfin-data>/plugins/Jellytel
mkdir -p <jellyfin-data>/plugins/Jellytel
cp publish/Jellyfin.Plugin.Jellytel.dll publish/Serilog.Sinks.OpenTelemetry.dll \
   publish/Google.Protobuf.dll publish/Grpc.*.dll \
   <jellyfin-data>/plugins/Jellytel/
systemctl restart jellyfin    # or your platform equivalent
journalctl -u jellyfin -f | grep -i jellytel
```

Watch the host log on every restart — assembly load errors and `IHostedService` failures are loud and obvious.

## Things to be careful with

- **Static state.** `Plugin.Instance` is singleton-per-process; that's fine. But don't cache injected services on static fields — they're scoped to the host lifetime.
- **`PlaybackProgress` cardinality.** It fires every few seconds per session. Keep allocation-free; record into pre-built histograms keyed by stable dimensions, not strings.
- **Resource attributes.** Set `service.name`, `service.version`, and `host.name` once on the OTel `ResourceBuilder` — don't repeat them on every event.
- **Hot-reload.** Configuration changes must idempotently rebuild the exporter pipeline. Track and dispose previous resources before swapping.
- **Privacy.** `RemoteEndPoint` and `UserName` are PII. Make their inclusion opt-in via configuration.
