using System;
using System.Threading;

namespace Jellyfin.Plugin.Jellytel.LocalBuffer;

/// <summary>
/// Tracks whether the OTLP exporter is configured and the last time export
/// activity was observed. Read by the dashboard's export-status panel so
/// admins can tell at a glance whether telemetry is flowing.
/// </summary>
/// <remarks>
/// This is a thin status surface — it does not hook into the OTel exporter
/// pipeline directly. The bootstrappers update it on configuration changes
/// and on relevant lifecycle moments. A future improvement is to plug into
/// OTLP exporter callbacks for true per-export success/failure visibility.
/// </remarks>
public sealed class ExportStatusTracker
{
    private long _lastExportSuccessMs;
    private long _lastExportFailureMs;
    private string? _lastError;
    private int _otlpConfigured;

    /// <summary>Gets a value indicating whether the OTLP exporter is currently configured.</summary>
    public bool OtlpConfigured => Volatile.Read(ref _otlpConfigured) == 1;

    /// <summary>Gets the unix-ms timestamp of the last observed successful export, or 0.</summary>
    public long LastExportSuccessMs => Volatile.Read(ref _lastExportSuccessMs);

    /// <summary>Gets the unix-ms timestamp of the last observed failed export, or 0.</summary>
    public long LastExportFailureMs => Volatile.Read(ref _lastExportFailureMs);

    /// <summary>Gets the most recent export error message, or null.</summary>
    public string? LastError => Volatile.Read(ref _lastError);

    /// <summary>
    /// Records the OTLP exporter's configured state. Called by bootstrappers
    /// when configuration is applied or torn down.
    /// </summary>
    /// <param name="configured">Whether an OTLP endpoint is set and the exporter built successfully.</param>
    public void SetOtlpConfigured(bool configured)
        => Interlocked.Exchange(ref _otlpConfigured, configured ? 1 : 0);

    /// <summary>Marks a successful export at the current time.</summary>
    public void MarkSuccess()
    {
        Interlocked.Exchange(ref _lastExportSuccessMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Interlocked.Exchange(ref _lastError, null);
    }

    /// <summary>
    /// Marks a failed export.
    /// </summary>
    /// <param name="error">Short error message.</param>
    public void MarkFailure(string error)
    {
        Interlocked.Exchange(ref _lastExportFailureMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Interlocked.Exchange(ref _lastError, error);
    }
}
