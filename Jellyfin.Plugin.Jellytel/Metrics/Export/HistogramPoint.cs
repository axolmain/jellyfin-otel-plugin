using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Jellytel.Metrics.Export;

/// <summary>
/// One histogram measurement aggregate for a single (instrument, tags) series.
/// Bucket boundaries are the cumulative OTel default set.
/// </summary>
/// <param name="StartTime">Start of the cumulative window.</param>
/// <param name="Time">End of the window.</param>
/// <param name="Count">Total number of recorded values.</param>
/// <param name="Sum">Sum of recorded values.</param>
/// <param name="Min">Minimum recorded value, or null when count == 0.</param>
/// <param name="Max">Maximum recorded value, or null when count == 0.</param>
/// <param name="ExplicitBounds">Bucket upper bounds. Length N → N+1 bucket counts.</param>
/// <param name="BucketCounts">Counts per bucket; trailing bucket holds values &gt; last bound.</param>
/// <param name="Tags">Curated, low-cardinality attribute set.</param>
public sealed record HistogramPoint(
    DateTimeOffset StartTime,
    DateTimeOffset Time,
    long Count,
    double Sum,
    double? Min,
    double? Max,
    IReadOnlyList<double> ExplicitBounds,
    IReadOnlyList<long> BucketCounts,
    IReadOnlyList<KeyValuePair<string, object?>> Tags);
