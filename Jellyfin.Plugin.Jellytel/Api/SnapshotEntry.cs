namespace Jellyfin.Plugin.Jellytel.Api;

/// <summary>One row in <see cref="SnapshotDto"/>.</summary>
public sealed class SnapshotEntry
{
    /// <summary>Gets or sets the metric name.</summary>
    public string Metric { get; set; } = string.Empty;

    /// <summary>Gets or sets the unix-ms timestamp of the latest sample.</summary>
    public long TimestampMs { get; set; }

    /// <summary>Gets or sets the latest sample's value.</summary>
    public double Value { get; set; }
}
