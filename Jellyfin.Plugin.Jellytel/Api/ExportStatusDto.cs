namespace Jellyfin.Plugin.Jellytel.Api;

/// <summary>OTLP exporter status snapshot.</summary>
public sealed class ExportStatusDto
{
    /// <summary>Gets or sets a value indicating whether the OTLP exporter is built.</summary>
    public bool OtlpConfigured { get; set; }

    /// <summary>Gets or sets the unix-ms timestamp of the last observed successful export, or 0.</summary>
    public long LastExportSuccessMs { get; set; }

    /// <summary>Gets or sets the unix-ms timestamp of the last observed failed export, or 0.</summary>
    public long LastExportFailureMs { get; set; }

    /// <summary>Gets or sets the most recent error message, or null.</summary>
    public string? LastError { get; set; }
}
