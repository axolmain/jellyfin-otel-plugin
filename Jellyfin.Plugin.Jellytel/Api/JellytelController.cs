using System;
using System.Linq;
using Jellyfin.Plugin.Jellytel.LocalBuffer;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Jellytel.Api;

/// <summary>
/// REST surface that the embedded dashboard page consumes.
/// </summary>
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("Jellytel")]
[Produces("application/json")]
public class JellytelController : ControllerBase
{
    private static readonly string[] CounterMetrics =
    {
        "jellyfin.playback.started",
        "jellyfin.playback.stopped",
        "jellyfin.transcode.reasons",
        "jellyfin.playback.duration",
    };

    private static readonly string[] GaugeMetrics =
    {
        "jellyfin.sessions.active",
    };

    private readonly LocalBufferBootstrapper _buffer;
    private readonly ExportStatusTracker _exportStatus;

    /// <summary>
    /// Initializes a new instance of the <see cref="JellytelController"/> class.
    /// </summary>
    /// <param name="buffer">Local buffer bootstrapper (used to reach the live store).</param>
    /// <param name="exportStatus">OTLP export status tracker.</param>
    public JellytelController(LocalBufferBootstrapper buffer, ExportStatusTracker exportStatus)
    {
        _buffer = buffer;
        _exportStatus = exportStatus;
    }

    /// <summary>
    /// Returns the most recent value for every tracked metric.
    /// </summary>
    /// <returns>Snapshot DTO with one entry per metric.</returns>
    [HttpGet("Snapshot")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public ActionResult<SnapshotDto> GetSnapshot()
    {
        var store = _buffer.Store;
        if (store is null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new SnapshotDto
            {
                BufferEnabled = false,
                Metrics = Array.Empty<SnapshotEntry>(),
            });
        }

        var all = CounterMetrics.Concat(GaugeMetrics).ToArray();
        var latest = store.ReadLatest(all);

        var entries = all
            .Where(latest.ContainsKey)
            .Select(name => new SnapshotEntry
            {
                Metric = name,
                TimestampMs = latest[name].TimestampMs,
                Value = latest[name].Value,
            })
            .ToArray();

        return new SnapshotDto { BufferEnabled = true, Metrics = entries };
    }

    /// <summary>
    /// Returns a bucketed time series for a single metric.
    /// </summary>
    /// <param name="metric">Metric name (must be one of the tracked metrics).</param>
    /// <param name="fromMs">Range start in unix ms. Defaults to 24h ago.</param>
    /// <param name="toMs">Range end in unix ms. Defaults to now.</param>
    /// <param name="bucketMs">Bucket width in ms. Defaults to 60000 (1 min).</param>
    /// <returns>Ordered list of <c>(ts, value)</c> pairs.</returns>
    [HttpGet("Series")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public ActionResult<SeriesDto> GetSeries(
        [FromQuery] string metric,
        [FromQuery] long? fromMs = null,
        [FromQuery] long? toMs = null,
        [FromQuery] long? bucketMs = null)
    {
        if (string.IsNullOrWhiteSpace(metric))
        {
            return BadRequest("metric is required");
        }

        var store = _buffer.Store;
        if (store is null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var from = fromMs ?? (now - (TimeSpan.FromHours(24).Ticks / TimeSpan.TicksPerMillisecond));
        var to = toMs ?? now;
        var bucket = bucketMs ?? 60_000;

        var aggregation = Array.IndexOf(GaugeMetrics, metric) >= 0 ? "avg" : "sum";
        var rows = store.ReadSeries(metric, from, to, bucket, aggregation);

        return new SeriesDto
        {
            Metric = metric,
            Aggregation = aggregation,
            BucketMs = bucket,
            Points = rows.Select(r => new SeriesPoint { TimestampMs = r.TimestampMs, Value = r.Value }).ToArray(),
        };
    }

    /// <summary>
    /// Returns OTLP export status — whether an exporter is configured and
    /// the last observed success/failure timestamps.
    /// </summary>
    /// <returns>Export status DTO.</returns>
    [HttpGet("ExportStatus")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<ExportStatusDto> GetExportStatus()
        => new ExportStatusDto
        {
            OtlpConfigured = _exportStatus.OtlpConfigured,
            LastExportSuccessMs = _exportStatus.LastExportSuccessMs,
            LastExportFailureMs = _exportStatus.LastExportFailureMs,
            LastError = _exportStatus.LastError,
        };
}
