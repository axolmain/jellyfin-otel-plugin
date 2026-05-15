using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Jellytel.Traces.Export;

/// <summary>
/// No-op trace exporter. Used when OTLP trace export is disabled or the
/// endpoint is empty; lets the collector keep running without buffering
/// activities forever.
/// </summary>
public sealed class NullTraceExporter : ITraceExporter
{
    /// <inheritdoc />
    public string Name => "null";

    /// <inheritdoc />
    public Task<bool> ExportAsync(IReadOnlyList<Activity> spans, CancellationToken cancellationToken)
        => Task.FromResult(true);

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
