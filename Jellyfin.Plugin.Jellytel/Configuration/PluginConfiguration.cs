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
        MinimumLevel = LogEventLevel.Warning;
        EnableMetrics = true;
        EnableSessionMetrics = true;
        EnableTraces = true;
        TracedActivitySources = string.Empty;
        LocalBufferEnabled = false;
        LocalBufferSampleIntervalSeconds = 30;
        LocalBufferRetentionHours = 168;
        LocalBufferMaxRows = 500_000;
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

    /// <summary>
    /// Gets or sets a value indicating whether OTLP trace export is enabled.
    /// Master switch — when false, the trace listener is not attached and no
    /// spans are collected regardless of <see cref="TracedActivitySources"/>.
    /// </summary>
    public bool EnableTraces { get; set; }

    /// <summary>
    /// Gets or sets a comma-separated list of additional <c>ActivitySource</c>
    /// names to listen on, in addition to the plugin's own source. Example:
    /// <c>Microsoft.AspNetCore</c> to capture Jellyfin's request activities so
    /// trace IDs already attached to exported log records resolve to real
    /// spans in the backend. High-volume sources may produce a lot of spans;
    /// start with one source and watch the <c>jellytel.traces.dropped</c>
    /// counter.
    /// </summary>
    public string TracedActivitySources { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the local SQLite time-series
    /// buffer is enabled. Independent of OTLP export — both can run together.
    /// When on, the plugin records the same metrics it ships to OTLP into a
    /// local database so admins can view recent history in the embedded
    /// dashboard page without configuring an external collector.
    /// </summary>
    public bool LocalBufferEnabled { get; set; }

    /// <summary>
    /// Gets or sets how often the gauge snapshotter records active-session
    /// and other observable-gauge values. Counter-style events (playback
    /// start/stop, transcode reasons) are recorded as they happen and are
    /// not gated by this interval.
    /// </summary>
    public int LocalBufferSampleIntervalSeconds { get; set; }

    /// <summary>
    /// Gets or sets the retention window in hours. Samples older than this
    /// are deleted opportunistically on writes and at startup.
    /// </summary>
    public int LocalBufferRetentionHours { get; set; }

    /// <summary>
    /// Gets or sets the hard ceiling on rows. Acts as a safety cap in case
    /// retention misses something (e.g. clock skew or a burst of events).
    /// </summary>
    public int LocalBufferMaxRows { get; set; }
}
