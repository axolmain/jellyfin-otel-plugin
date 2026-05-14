using System.Collections.Generic;

namespace Jellyfin.Plugin.Jellytel.Metrics.Export;

/// <summary>
/// All data points produced by a single instrument during a scrape window.
/// </summary>
/// <param name="Name">Instrument name (e.g. <c>jellyfin.playback.started</c>).</param>
/// <param name="Unit">UCUM unit string (may be empty).</param>
/// <param name="Description">Free-text description (may be empty).</param>
/// <param name="Kind">Instrument kind — drives aggregation semantics on the exporter side.</param>
/// <param name="NumberPoints">Counter / UpDownCounter / Gauge data points (empty when this is a histogram).</param>
/// <param name="HistogramPoints">Histogram data points (empty when this is a non-histogram instrument).</param>
public sealed record MetricFamily(
    string Name,
    string Unit,
    string Description,
    InstrumentKind Kind,
    IReadOnlyList<NumberPoint> NumberPoints,
    IReadOnlyList<HistogramPoint> HistogramPoints);
