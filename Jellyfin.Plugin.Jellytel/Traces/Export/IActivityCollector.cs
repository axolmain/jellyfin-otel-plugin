using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Jellyfin.Plugin.Jellytel.Traces.Export;

/// <summary>
/// Subscribes to one or more <c>System.Diagnostics.ActivitySource</c>s and
/// buffers completed <see cref="Activity"/> instances for the exporter to
/// drain. The collector knows nothing about transport.
/// </summary>
public interface IActivityCollector : IDisposable
{
    /// <summary>
    /// Attaches the underlying <c>ActivityListener</c>. Idempotent — calling
    /// twice is a no-op.
    /// </summary>
    void Start();

    /// <summary>
    /// Returns and removes every <see cref="Activity"/> currently buffered.
    /// Non-blocking; safe to call repeatedly from the export loop.
    /// </summary>
    /// <returns>Activities buffered since the previous drain (possibly empty).</returns>
    IReadOnlyList<Activity> Drain();
}
