# Metrics Export Architecture

The plugin no longer depends on the OpenTelemetry SDK. This document explains
why, and how the replacement is structured to keep swapping back to the SDK
(or to a different wire format) a single-file change.

---

## Why the SDK was dropped

OpenTelemetry SDK 1.13+ took a transitive dependency on
`System.Diagnostics.DiagnosticSource >= 10.x`. The `MeterListener` base
class' virtual signatures changed between the 9.x and 10.x versions of
that library. When the SDK loads inside a net9 host (current Jellyfin
10.11.x), the runtime resolves `System.*` assemblies against the host's
loaded copies, gets the 9.x `MeterListener`, then tries to load the SDK's
`OpenTelemetryMetricsListener` — and fails:

```
System.TypeLoadException: Method 'InstrumentPublished' in type
'OpenTelemetry.Metrics.OpenTelemetryMetricsListener' from assembly
'OpenTelemetry, Version=1.0.0.0' does not have an implementation.
```

Older OTel SDK versions don't have this conflict but carry unresolved CVEs
that the consumer can't accept. We were stuck. So:

- We drive metric collection directly from `System.Diagnostics.Metrics.MeterListener`
  in the BCL — no SDK, no DiagnosticSource version skew.
- We ship a small OTLP/HTTP exporter built on the protobuf message types
  generated from vendored `.proto` files.

---

## Layering

```
                       PluginConfiguration (OtlpEndpoint, ServiceName, …)
                              │
                              ▼
                      MetricsBootstrapper       ─┐
                              │                  │  reconfigure on save,
                              ▼                  │  swallow exceptions
              ┌───────────────┴───────────────┐  │
              │                               │  │
              ▼                               ▼  │
        IMetricPanel registrations     IMeterCollector ──→ MetricSnapshot (data only)
        (instruments on JellytelMeter)     │
                              ┌────────────┴────────────┐
                              ▼                         ▼
                       IMetricExporter           IMetricExporter
                       (NullExporter when            (OtlpHttpExporter when
                       no endpoint set)              endpoint is set)
                              │
                              ▼
                       OTLP collector / Grafana Alloy / Tempo / …
```

Three pieces:

1. **`IMeterCollector`** (`Metrics/Export/IMeterCollector.cs`)
   The only file that touches `MeterListener`. Subscribes to the Jellytel
   meter, accumulates per-(instrument, tag set) aggregates, returns a
   `MetricSnapshot` on demand. Histograms reset between scrapes; counters,
   up-down counters, and gauges remain cumulative. Implemented by
   `MeterCollector`.

2. **`IMetricExporter`** (`Metrics/Export/IMetricExporter.cs`)
   Single-method async sink. Knows nothing about MeterListener or about
   the plugin's panels. Implementations:
   - `OtlpHttpExporter` — serializes to OTLP protobuf, POSTs to
     `{endpoint}/v1/metrics`. Updates `ExportStatusTracker` on each call.
   - `NullExporter` — drops everything. Used when no endpoint is configured.
     The collector still runs so the local SQLite buffer still gets data.

3. **`MetricsBootstrapper`** (`Metrics/MetricsBootstrapper.cs`)
   Hosted service that wires the two together. Owns the periodic export
   timer (15s by default). Rebuilds the pipeline on plugin configuration
   change. Never throws into the Jellyfin host.

The DTOs in `Metrics/Export/` (`MetricSnapshot`, `MetricFamily`,
`NumberPoint`, `HistogramPoint`, `InstrumentKind`, `ExporterContext`) are
**pure data — no protobuf types, no OTel SDK types, no MeterListener types**.
That's the seam. A new exporter only needs to consume `MetricSnapshot`.

---

## Adding a new exporter

Implement `IMetricExporter`. Register it in `PluginServiceRegistrator` (or
make the choice configurable). That's it. The collector, the panels, and
the bootstrapper don't change.

Examples of what would fit cleanly:

- **`PrometheusTextExporter`** — write `/metrics` endpoint that returns the
  Prometheus text format generated from `MetricSnapshot`.
- **`OtlpGrpcExporter`** — re-use `Grpc.Net.Client` (already in the
  dependency closure thanks to Serilog) and use OTLP/gRPC.
- **`FileExporter`** — append JSON-serialized snapshots to a debug log.
- **`OtelSdkExporter`** — once Jellyfin runs on net10, restoring the OTel
  SDK becomes possible. Implement this exporter to delegate to a
  `MeterProvider` and the rest of the codebase remains untouched.

---

## Vendored OTLP protos

`Proto/opentelemetry/proto/` mirrors a subset of
[opentelemetry-proto](https://github.com/open-telemetry/opentelemetry-proto)
v1.3.2 — only the files our exporter actually needs:

- `common/v1/common.proto`
- `resource/v1/resource.proto`
- `metrics/v1/metrics.proto`
- `collector/metrics/v1/metrics_service.proto`

These are stable wire-format definitions. Re-vendor only when
upstream introduces a field we want to populate.

C# is generated at build time by `Grpc.Tools` (`<Protobuf Include="...">`
items in the csproj). Generated types land under
`OpenTelemetry.Proto.{Common,Resource,Metrics,Collector}.V1` and are used
*only* inside `OtlpHttpExporter` — they never appear on `IMetricExporter`
or `MetricSnapshot`.

---

## What this lets us delete

- `<PackageReference Include="OpenTelemetry" Version="..." />`
- `<PackageReference Include="OpenTelemetry.Exporter.OpenTelemetryProtocol" Version="..." />`
- The CI workflow bundling of `OpenTelemetry.dll`, `OpenTelemetry.Api.dll`,
  `OpenTelemetry.Api.ProviderBuilderExtensions.dll`,
  `OpenTelemetry.Exporter.OpenTelemetryProtocol.dll`, and
  `System.Diagnostics.DiagnosticSource.dll` (which was bundled to try to
  paper over the underlying conflict).

`Serilog.Sinks.OpenTelemetry` stays — its log pipeline never touched the
broken `MeterListener` and works fine on net9.

---

## What's intentionally still missing

- **Per-export retry / queue.** A failed export is logged and dropped.
  If you need durable buffering, the SQLite local-buffer (see
  [[local-buffer]]) is the path — it records everything regardless of
  exporter health and can be drained externally.
- **Trace export.** Out of scope for v1. The `IMetricExporter` interface
  is metrics-only.
- **Bucket configuration per histogram.** Default bucket set is the OTel
  SDK default (5/10/25/.../10000). Tunable bounds per instrument is a
  follow-up if a user needs them.
