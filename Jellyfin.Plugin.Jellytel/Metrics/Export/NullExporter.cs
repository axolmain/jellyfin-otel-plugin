using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Jellytel.Metrics.Export;

/// <summary>
/// No-op exporter. Used when OTLP export is disabled but the metric pipeline
/// still has to feed the local buffer.
/// </summary>
public sealed class NullExporter : IMetricExporter
{
    /// <inheritdoc />
    public string Name => "null";

    /// <inheritdoc />
    public Task<bool> ExportAsync(MetricSnapshot snapshot, CancellationToken cancellationToken)
        => Task.FromResult(true);

    /// <inheritdoc />
    public System.Threading.Tasks.ValueTask DisposeAsync() => System.Threading.Tasks.ValueTask.CompletedTask;
}
