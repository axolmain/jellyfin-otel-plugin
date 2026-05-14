# Local Time-Series Buffer

The plugin ships an optional SQLite-backed ring buffer that records the same
metrics it would ship to an OTLP collector. Two purposes:

1. Let admins view recent metric history in the embedded dashboard page
   without configuring any external collector.
2. Give the plugin a way to demonstrate that its event handlers are firing
   correctly — independent of whether OTLP export is working.

The buffer is **independent of OTLP**. Both can run simultaneously; turning
one on or off does not affect the other.

---

## Architecture

```
                       PluginConfiguration
                              │
                              ▼
                  LocalBufferBootstrapper           MetricsBootstrapper
                              │                              │
              ┌───────────────┴───────────────┐              │
              ▼                               ▼              ▼
        EventRecorder                  GaugeSnapshotter   MeterProvider
              │                               │              │
              │  (PlaybackStart/Stopped,      │ (periodic    │
              │   TranscodeReasons, …)        │  sampling)   │
              ▼                               ▼              ▼
                       TimeSeriesStore                    OTLP / collector
                              │
                              ▼
                       timeseries.db (SQLite)
                              │
                              ▼
                       JellytelController (HTTP)
                              │
                              ▼
                       dashboard.html (sidebar entry)
```

`LocalBufferBootstrapper` is registered as both a singleton (so the API
controller can reach it) and a hosted service (so it participates in
startup/shutdown). It tears down and rebuilds on configuration change so
toggles take effect without a Jellyfin restart.

---

## Storage

File: `{ApplicationPaths.DataPath}/plugins/Jellytel/timeseries.db`

Single-table schema, flat tag encoding:

```sql
CREATE TABLE metric_samples (
    ts          INTEGER NOT NULL,   -- unix ms
    metric_name TEXT    NOT NULL,
    tag_key     TEXT    NOT NULL,   -- canonical sorted "k=v;k=v", "" when none
    value       REAL    NOT NULL
);
CREATE INDEX ix_metric_samples_metric_ts
    ON metric_samples (metric_name, ts);
```

Tags are stored as a single canonical string rather than normalized into a
separate table — cardinality is low (we already curate tag keys in
`SessionMetrics`) and aggregation across tag values is the dashboard's job.

**Journal mode:** WAL. **Synchronous:** NORMAL. Background writer runs on a
single thread draining an unbounded `Channel<MetricSample>`, so event handlers
never block on disk I/O and SQLite never sees concurrent writers.

---

## Sampling — hybrid policy

| Source | Style | Trigger |
|---|---|---|
| `jellyfin.playback.started` | Event | `ISessionManager.PlaybackStart` |
| `jellyfin.playback.stopped` | Event | `ISessionManager.PlaybackStopped` |
| `jellyfin.playback.duration` | Event | `PlaybackStopped` (value = elapsed seconds) |
| `jellyfin.transcode.reasons` | Event | `PlaybackStart` (one row per reason flag) |
| `jellyfin.sessions.active` | Periodic | Every `LocalBufferSampleIntervalSeconds` |

Counters are written as they happen — fidelity matches the OTel SDK. Gauges
are polled, because there is no "active sessions changed" event we could
react to without rolling our own diffing layer.

The dashboard's `Series` endpoint sums counters within each bucket and
averages gauges, matching their semantics.

---

## Retention

Configured via the plugin config page. All three knobs:

- `LocalBufferSampleIntervalSeconds` — default 30, minimum 5.
- `LocalBufferRetentionHours` — default 168 (7 days).
- `LocalBufferMaxRows` — default 500,000. Hard ceiling in case retention
  misses something (clock skew, event bursts).

Retention is enforced opportunistically:
- On startup (one pass).
- Every ~1,000 inserts (single `DELETE WHERE ts < ?`, then row-count check
  with `DELETE … ORDER BY ts LIMIT N` if we're over the ceiling).

No background timer — piggybacking on writes keeps the moving parts small
and means an idle server doesn't churn the DB.

---

## API surface

All endpoints require the `RequiresElevation` policy (admin only).

### `GET /Jellytel/Snapshot`

Latest value per tracked metric.

```json
{
  "BufferEnabled": true,
  "Metrics": [
    { "Metric": "jellyfin.sessions.active",  "TimestampMs": 1731534000123, "Value": 3.0 },
    { "Metric": "jellyfin.playback.started", "TimestampMs": 1731533998456, "Value": 1.0 }
  ]
}
```

Returns `503` with `BufferEnabled: false` when the buffer is disabled.

### `GET /Jellytel/Series?metric=…&fromMs=…&toMs=…&bucketMs=…`

Bucketed series for one metric. Aggregation is SUM for counters / AVG for
gauges (auto-selected by metric name). Default range is the last 24 hours
with 1-minute buckets.

```json
{
  "Metric": "jellyfin.playback.started",
  "Aggregation": "sum",
  "BucketMs": 60000,
  "Points": [
    { "TimestampMs": 1731530000000, "Value": 4.0 },
    { "TimestampMs": 1731530060000, "Value": 2.0 }
  ]
}
```

### `GET /Jellytel/ExportStatus`

OTLP exporter status — drives the status panel at the top of the dashboard.

```json
{
  "OtlpConfigured": true,
  "LastExportSuccessMs": 0,
  "LastExportFailureMs": 0,
  "LastError": null
}
```

`OtlpConfigured` is the bootstrapper's view of whether the OTLP exporter was
successfully built. Per-export success/failure tracking is wired through
`ExportStatusTracker` but not yet hooked into individual export callbacks;
that requires shimming the OTel exporter and is left for a follow-up.

---

## Dashboard page

Embedded HTML resource at `Jellyfin.Plugin.Jellytel.Configuration.dashboard.html`,
registered in `Plugin.GetPages()` with `EnableInMainMenu = true` so it
appears in the admin sidebar under the server section.

- One card per metric. Each card shows the latest value (top) and a
  sparkline (bottom) from the chosen time range.
- Time-range selector at the top: 1h / 6h / 24h / 7d.
- Charting uses **uPlot** loaded from jsDelivr — the page falls back to a
  text placeholder if the CDN load fails.
- Auto-refreshes every 30 seconds while the page is visible; teardown on
  `pagehide` cancels the timer and destroys the chart instances.

---

## What is intentionally not done

- **No tiered rollups.** Single resolution (one row per sample). Long
  retention windows mean many rows; the configurable max-row cap is the
  safety valve. Tiered rollups would buy 10–100× retention at the cost of a
  downsampling job — not worth it for a v1.
- **No per-export OTLP success/failure tracking.** The `ExportStatusTracker`
  surface exists, but the OTel SDK doesn't expose first-class export
  callbacks; wiring it up means a custom `BaseExporter<T>` shim. Status
  shows "configured" / "not configured" only.
- **No persistence of the OTLP send queue.** When OTLP is misconfigured the
  local buffer still records everything — that's the whole point — but the
  plugin does not retry-export buffered samples once OTLP comes online. The
  buffer is a viewer, not a store-and-forward exporter.
