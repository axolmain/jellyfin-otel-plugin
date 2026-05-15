# Telemetry Export Architecture

The plugin does not depend on the OpenTelemetry SDK. This document explains
the constraint that drives that choice and how the metrics and traces
pipelines are structured to keep swapping in the SDK (or a different wire
format) a single-file change.

---

## Why the SDK is not used

OpenTelemetry SDK 1.13+ takes a transitive dependency on
`System.Diagnostics.DiagnosticSource >= 10.x`. The `MeterListener` base
class' virtual signatures changed between the 9.x and 10.x versions of
that library. Inside a net9 host (current Jellyfin 10.11.x), the runtime
resolves `System.*` assemblies against the host's loaded copies, gets the
9.x `MeterListener`, then tries to load the SDK's
`OpenTelemetryMetricsListener` — and fails:

```
System.TypeLoadException: Method 'InstrumentPublished' in type
'OpenTelemetry.Metrics.OpenTelemetryMetricsListener' from assembly
'OpenTelemetry, Version=1.0.0.0' does not have an implementation.
```

Older OTel SDK versions don't have this conflict but carry unresolved CVEs
that the consumer can't accept. So:

- Metric collection runs directly off `System.Diagnostics.Metrics.MeterListener`
  in the BCL — no SDK, no DiagnosticSource version skew.
- Trace collection runs off BCL `ActivityListener` for the same reason.
- Export goes through small OTLP/HTTP exporters built on the protobuf
  message types generated from vendored `.proto` files.
- `Serilog.Sinks.OpenTelemetry` is fine to use for logs — its pipeline
  never touches `MeterListener` and loads cleanly on net9.

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
v1.3.2 — only the files our exporters actually need:

- `common/v1/common.proto`
- `resource/v1/resource.proto`
- `metrics/v1/metrics.proto`
- `collector/metrics/v1/metrics_service.proto`
- `trace/v1/trace.proto`
- `collector/trace/v1/trace_service.proto`

These are stable wire-format definitions. Re-vendor only when
upstream introduces a field we want to populate.

C# is generated at build time by `Grpc.Tools` (`<Protobuf Include="...">`
items in the csproj). Generated types land under
`OpenTelemetry.Proto.{Common,Resource,Metrics,Trace,Collector}.V1` and are
used *only* inside the OTLP exporters — they never appear on the public
exporter interfaces or DTOs.

---

## Trace export pipeline

The trace pipeline mirrors the metrics one closely but skips the DTO layer:
`Activity` is already a stable BCL type and spans are 1:1 with stopped
activities (no aggregation), so the exporter takes
`IReadOnlyList<Activity>` directly.

```
                       PluginConfiguration (EnableTraces, TracedActivitySources, OtlpEndpoint, …)
                              │
                              ▼
                      TracesBootstrapper      ─┐
                              │                 │  reconfigure on save,
                              ▼                 │  swallow exceptions
                      IActivityCollector ──→ IReadOnlyList<Activity>
                              │
                              ▼
                       ITraceExporter           ITraceExporter
                       (NullTraceExporter when    (OtlpHttpTraceExporter when
                       no endpoint set)            endpoint is set)
                              │
                              ▼
                       OTLP collector / Aspire / Tempo / …
```

Three pieces:

1. **`IActivityCollector`** (`Traces/Export/IActivityCollector.cs`)
   The only file that touches `ActivityListener`. `Start()` attaches a
   single listener whose `ShouldListenTo` matches the plugin's own
   `JellytelActivitySource` plus each entry of the
   `TracedActivitySources` CSV. Completed activities flow into a bounded
   channel (10 000 capacity, `DropOldest`). Drops are surfaced via a
   `jellytel.traces.dropped` counter on `JellytelMeter` — i.e. the trace
   pipeline self-instruments through the metrics pipeline.

2. **`ITraceExporter`** (`Traces/Export/ITraceExporter.cs`)
   Single-method async sink, mirroring `IMetricExporter`. Implementations:
   - `OtlpHttpTraceExporter` — serializes to OTLP protobuf, POSTs to
     `{endpoint}/v1/traces`. Updates the shared `ExportStatusTracker`.
   - `NullTraceExporter` — drops everything. Used when no endpoint is
     configured. The collector still runs (zero-allocation when nothing
     is listening because no `ActivitySource.AddActivityListener` is in
     effect anyway).

3. **`TracesBootstrapper`** (`Traces/TracesBootstrapper.cs`)
   Hosted service that wires the two together. Owns the 15s export loop
   matching the metrics cadence. Rebuilds the pipeline on plugin
   configuration change. Never throws into the Jellyfin host.

Why this exists: the Serilog OTel sink stamps `trace_id`/`span_id` on every
exported log record when an ambient `Activity` is present. Jellyfin's
request pipeline emits activities for every request, so the logs always
carry trace IDs — but without a trace exporter, clicking a trace link in
Aspire 404s. This pipeline closes that hole.

---

## What's intentionally still missing

- **Per-export retry / queue.** A failed export is logged and dropped.
  If you need durable buffering, the SQLite local-buffer (see
  [[local-buffer]]) is the path — it records everything regardless of
  exporter health and can be drained externally. (Traces are not
  buffered to SQLite — only metric samples.)
- **Sampling.** All recorded activities are exported. Head-based ratio
  sampling is a follow-up if a single user-added source produces too much
  volume; for now the `jellytel.traces.dropped` counter is the safety
  signal.
- **Bucket configuration per histogram.** Default bucket set is the OTel
  SDK default (5/10/25/.../10000). Tunable bounds per instrument is a
  follow-up if a user needs them.
