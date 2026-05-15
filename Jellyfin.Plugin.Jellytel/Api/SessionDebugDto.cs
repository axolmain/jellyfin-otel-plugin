using System.Collections.Generic;

namespace Jellyfin.Plugin.Jellytel.Api;

/// <summary>
/// Projection of <c>SessionInfo</c> state, returned by <c>/Jellytel/Debug/Sessions</c>.
/// Intended as a self-service "what does the plugin currently see?" tool —
/// admins can curl this when a dashboard card reads wrong to confirm what
/// the snapshotter's inputs actually look like.
/// </summary>
public sealed class SessionDebugDto
{
    /// <summary>
    /// Gets or sets the unix-ms timestamp when this snapshot was taken.
    /// </summary>
    public long ServerTimeMs { get; set; }

    /// <summary>
    /// Gets or sets the staleness threshold (in seconds) currently being
    /// applied to gauge filtering.
    /// </summary>
    public int StaleSessionSeconds { get; set; }

    /// <summary>
    /// Gets the per-session projections.
    /// </summary>
    public IList<SessionDebugRow> Sessions { get; } = new List<SessionDebugRow>();
}
