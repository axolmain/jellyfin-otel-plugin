using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Jellytel.Configuration;

/// <summary>
/// Plugin configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PluginConfiguration"/> class.
    /// </summary>
    public PluginConfiguration()
    {
        OtlpEndpoint = string.Empty;
        ServiceName = "jellyfin";
        BackfillBootLogs = false;
    }

    /// <summary>
    /// Gets or sets the OTLP HTTP endpoint base URL (e.g. http://localhost:4318).
    /// When null or empty, OpenTelemetry log export is disabled.
    /// </summary>
    public string OtlpEndpoint { get; set; }

    /// <summary>
    /// Gets or sets the service.name resource attribute reported to the collector.
    /// </summary>
    public string ServiceName { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the plugin should read the
    /// current log file on startup and replay events that occurred before
    /// the plugin loaded. Useful for capturing boot diagnostics; lossy on
    /// structured properties since events are reparsed from text.
    /// </summary>
    public bool BackfillBootLogs { get; set; }
}
