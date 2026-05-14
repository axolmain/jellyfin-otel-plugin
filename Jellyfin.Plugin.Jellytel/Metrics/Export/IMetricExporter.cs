using System;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Jellytel.Metrics.Export;

/// <summary>
/// Wire-format-agnostic sink for metric snapshots. Implementations decide
/// how to serialize and where to send. Swap implementations to change
/// transport (OTLP/HTTP, OTLP/gRPC, Prometheus, file, …) without touching
/// the collector or the bootstrapper.
/// </summary>
public interface IMetricExporter : IAsyncDisposable
{
    /// <summary>Gets the friendly name of this exporter (for diagnostic logging).</summary>
    string Name { get; }

    /// <summary>
    /// Sends one snapshot. Implementations should swallow non-fatal errors
    /// and surface them via the return value rather than throwing.
    /// </summary>
    /// <param name="snapshot">Snapshot to send.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>True if the snapshot was delivered, false if it was dropped or rejected.</returns>
    Task<bool> ExportAsync(MetricSnapshot snapshot, CancellationToken cancellationToken);
}
