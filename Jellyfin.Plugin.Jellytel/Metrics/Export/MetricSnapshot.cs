using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Jellytel.Metrics.Export;

/// <summary>
/// Pure-data snapshot of all metrics collected during a single scrape window.
/// Decouples the collector from the exporter: a snapshot can be serialized to
/// OTLP, Prometheus text, JSON, or anything else without the collector
/// knowing the wire format.
/// </summary>
/// <param name="StartTime">Start of the window the snapshot covers (UTC).</param>
/// <param name="EndTime">End of the window (UTC).</param>
/// <param name="Metrics">One entry per instrument with data points in this window.</param>
public sealed record MetricSnapshot(
    DateTimeOffset StartTime,
    DateTimeOffset EndTime,
    IReadOnlyList<MetricFamily> Metrics);
