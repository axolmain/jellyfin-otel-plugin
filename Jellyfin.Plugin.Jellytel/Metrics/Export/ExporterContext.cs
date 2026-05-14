using System.Collections.Generic;

namespace Jellyfin.Plugin.Jellytel.Metrics.Export;

/// <summary>
/// Resource-level metadata passed to an exporter on construction. Kept tiny
/// on purpose: anything bigger should be configured directly on the exporter.
/// </summary>
/// <param name="ServiceName">Value reported as the <c>service.name</c> resource attribute.</param>
/// <param name="ResourceAttributes">Additional resource attributes (may be empty).</param>
public sealed record ExporterContext(
    string ServiceName,
    IReadOnlyList<KeyValuePair<string, string>> ResourceAttributes);
