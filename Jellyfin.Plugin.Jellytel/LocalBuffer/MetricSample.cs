using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Jellyfin.Plugin.Jellytel.LocalBuffer;

/// <summary>
/// One point in the local time-series buffer.
/// </summary>
/// <param name="TimestampMs">Unix epoch milliseconds.</param>
/// <param name="MetricName">Dotted metric name (e.g. <c>jellyfin.playback.started</c>).</param>
/// <param name="TagKey">
/// Canonical tag string — sorted <c>k1=v1;k2=v2</c>, empty when there are no tags.
/// Stored as a single column so the schema stays flat; aggregation across tag
/// values is the dashboard's job, not the storage layer's.
/// </param>
/// <param name="Value">Sample value (count = 1 for discrete events, seconds for durations, etc.).</param>
public readonly record struct MetricSample(long TimestampMs, string MetricName, string TagKey, double Value)
{
    /// <summary>
    /// Builds a canonical tag key from a flat list of key/value pairs. Keys are
    /// sorted alphabetically so equivalent tag sets produce identical strings.
    /// </summary>
    /// <param name="tags">Tag pairs. Null values render as <c>null</c>.</param>
    /// <returns>Encoded tag string, empty when <paramref name="tags"/> is null or empty.</returns>
    public static string EncodeTags(IEnumerable<KeyValuePair<string, object?>>? tags)
    {
        if (tags is null)
        {
            return string.Empty;
        }

        var ordered = tags
            .Where(t => !string.IsNullOrEmpty(t.Key))
            .OrderBy(t => t.Key, StringComparer.Ordinal)
            .ToArray();

        if (ordered.Length == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder(ordered.Length * 16);
        for (var i = 0; i < ordered.Length; i++)
        {
            if (i > 0)
            {
                sb.Append(';');
            }

            sb.Append(ordered[i].Key);
            sb.Append('=');
            sb.Append(Convert.ToString(ordered[i].Value, CultureInfo.InvariantCulture) ?? "null");
        }

        return sb.ToString();
    }
}
