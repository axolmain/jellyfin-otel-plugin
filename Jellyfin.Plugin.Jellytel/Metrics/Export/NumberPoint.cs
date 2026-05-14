using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Jellytel.Metrics.Export;

/// <summary>
/// One scalar measurement of a counter / up-down counter / gauge.
/// </summary>
/// <param name="StartTime">Start of the cumulative window for the (instrument, tags) series.</param>
/// <param name="Time">Time the point was observed.</param>
/// <param name="Value">Numeric value. Doubles cover both long and double sources.</param>
/// <param name="Tags">Curated, low-cardinality attribute set.</param>
public sealed record NumberPoint(
    DateTimeOffset StartTime,
    DateTimeOffset Time,
    double Value,
    IReadOnlyList<KeyValuePair<string, object?>> Tags);
