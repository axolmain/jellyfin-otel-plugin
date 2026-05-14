using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Jellytel.Api;

/// <summary>Bucketed time series for one metric.</summary>
public sealed class SeriesDto
{
    /// <summary>Gets or sets the metric name.</summary>
    public string Metric { get; set; } = string.Empty;

    /// <summary>Gets or sets the aggregation applied within each bucket ("sum" or "avg").</summary>
    public string Aggregation { get; set; } = string.Empty;

    /// <summary>Gets or sets the bucket width in milliseconds.</summary>
    public long BucketMs { get; set; }

    /// <summary>Gets or sets the ordered points within the requested range.</summary>
    public IReadOnlyList<SeriesPoint> Points { get; set; } = Array.Empty<SeriesPoint>();
}
