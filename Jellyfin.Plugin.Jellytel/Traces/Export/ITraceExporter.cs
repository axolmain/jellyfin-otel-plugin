using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Jellytel.Traces.Export;

/// <summary>
/// Wire-format-agnostic sink for batches of completed <see cref="Activity"/>
/// instances. Implementations decide how to serialize and where to send. Swap
/// implementations to change transport (OTLP/HTTP, OTLP/gRPC, file, …)
/// without touching the collector or the bootstrapper.
/// </summary>
public interface ITraceExporter : IAsyncDisposable
{
    /// <summary>Gets the friendly name of this exporter (for diagnostic logging).</summary>
    string Name { get; }

    /// <summary>
    /// Sends one batch of completed activities. Implementations should
    /// swallow non-fatal errors and surface them via the return value rather
    /// than throwing.
    /// </summary>
    /// <param name="spans">Activities to send. Caller guarantees they are stopped.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>True if the batch was delivered, false if it was dropped or rejected.</returns>
    Task<bool> ExportAsync(IReadOnlyList<Activity> spans, CancellationToken cancellationToken);
}
