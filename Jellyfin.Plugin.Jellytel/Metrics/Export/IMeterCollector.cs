using System;

namespace Jellyfin.Plugin.Jellytel.Metrics.Export;

/// <summary>
/// Drains a <c>System.Diagnostics.Metrics.MeterListener</c> for the
/// configured meter(s) and produces <see cref="MetricSnapshot"/> instances
/// on demand. The collector knows nothing about transport.
/// </summary>
public interface IMeterCollector : IDisposable
{
    /// <summary>
    /// Starts listening. Idempotent — calling twice is a no-op.
    /// </summary>
    void Start();

    /// <summary>
    /// Triggers measurement collection for observable instruments and
    /// snapshots accumulated state for synchronous instruments. Resets
    /// histogram aggregates between scrapes (delta semantics for histograms);
    /// counter / up-down / gauge state remains cumulative across scrapes.
    /// </summary>
    /// <returns>Snapshot covering the period since the previous scrape.</returns>
    MetricSnapshot Scrape();
}
