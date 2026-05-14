namespace Jellyfin.Plugin.Jellytel.Api;

/// <summary>One bucket in a <see cref="SeriesDto"/>.</summary>
public sealed class SeriesPoint
{
    /// <summary>Gets or sets the bucket start as a unix-ms timestamp.</summary>
    public long TimestampMs { get; set; }

    /// <summary>Gets or sets the aggregated value for the bucket.</summary>
    public double Value { get; set; }
}
