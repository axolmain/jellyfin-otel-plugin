using System.Collections.Generic;

namespace Jellyfin.Plugin.Jellytel.Api;

/// <summary>One row in <see cref="SnapshotDto"/>.</summary>
public sealed class SnapshotEntry
{
    /// <summary>Gets or sets the metric name.</summary>
    public string Metric { get; set; } = string.Empty;

    /// <summary>Gets or sets the unix-ms timestamp of the latest sample.</summary>
    public long TimestampMs { get; set; }

    /// <summary>Gets or sets the latest sample's value (summed across tags for tagged-sum metrics).</summary>
    public double Value { get; set; }

    /// <summary>
    /// Gets the per-bucket breakdown for tagged-sum metrics
    /// (e.g. play_method → count). Null for flat metrics; assigned
    /// once when the snapshot is built.
    /// </summary>
    public IReadOnlyDictionary<string, double>? Breakdown { get; init; }
}
