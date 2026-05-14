using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Jellytel.Api;

/// <summary>Snapshot of the most recent value per tracked metric.</summary>
public sealed class SnapshotDto
{
    /// <summary>Gets or sets a value indicating whether the local buffer is currently running.</summary>
    public bool BufferEnabled { get; set; }

    /// <summary>Gets or sets the latest value per metric.</summary>
    public IReadOnlyList<SnapshotEntry> Metrics { get; set; } = Array.Empty<SnapshotEntry>();
}
