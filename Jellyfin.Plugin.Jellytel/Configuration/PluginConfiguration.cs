using MediaBrowser.Model.Plugins;
using Serilog.Events;

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
        MinimumLevel = LogEventLevel.Debug;
        EnableMetrics = false;
        EnableSessionMetrics = true;
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

    /// <summary>
    /// Gets or sets the minimum Serilog level for events forwarded to the
    /// OpenTelemetry sink. Events below this level are still written to
    /// Jellyfin's normal console/file pipeline but are not exported.
    /// </summary>
    public LogEventLevel MinimumLevel { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether OpenTelemetry metric export is
    /// enabled. Master switch — when false, no panels register regardless of
    /// their individual toggles.
    /// </summary>
    public bool EnableMetrics { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the session metrics panel
    /// (active sessions, playback start/stop counters, playback duration
    /// histogram, transcode reason counter) is enabled.
    /// </summary>
    public bool EnableSessionMetrics { get; set; }
}
