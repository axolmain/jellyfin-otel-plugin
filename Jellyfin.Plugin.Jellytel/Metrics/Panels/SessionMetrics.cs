using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using Jellyfin.Plugin.Jellytel.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Jellytel.Metrics.Panels;

/// <summary>
/// Mirrors the "Active Sessions" / "Now Playing" surface of the Jellyfin
/// dashboard as OpenTelemetry instruments. Driven by <see cref="ISessionManager"/>
/// events so it reacts in real time and never polls.
/// </summary>
/// <remarks>
/// Attribute policy: only attributes with bounded, admin-relevant cardinality
/// (play method, media type, client app, hardware accel) are emitted on
/// metrics. User identity, device IDs, and item IDs stay in the logs pipeline.
/// </remarks>
public sealed class SessionMetrics : IMetricPanel
{
    private readonly ISessionManager _sessionManager;
    private readonly ILogger<SessionMetrics> _logger;

    private Counter<long>? _playbackStarted;
    private Counter<long>? _playbackStopped;
    private Histogram<double>? _playbackDuration;
    private Counter<long>? _transcodeReasons;
    private ObservableGauge<long>? _activeSessions;

    private bool _subscribed;

    /// <summary>
    /// Initializes a new instance of the <see cref="SessionMetrics"/> class.
    /// </summary>
    /// <param name="sessionManager">Jellyfin session manager.</param>
    /// <param name="logger">Diagnostic logger.</param>
    public SessionMetrics(ISessionManager sessionManager, ILogger<SessionMetrics> logger)
    {
        _sessionManager = sessionManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "SessionMetrics";

    /// <inheritdoc />
    public bool IsEnabled(PluginConfiguration config) => config.EnableSessionMetrics;

    /// <inheritdoc />
    public void Register()
    {
        _playbackStarted = JellytelMeter.Instance.CreateCounter<long>(
            "jellyfin.playback.started",
            unit: "{playback}",
            description: "Number of playback sessions started.");

        _playbackStopped = JellytelMeter.Instance.CreateCounter<long>(
            "jellyfin.playback.stopped",
            unit: "{playback}",
            description: "Number of playback sessions stopped.");

        _playbackDuration = JellytelMeter.Instance.CreateHistogram<double>(
            "jellyfin.playback.duration",
            unit: "s",
            description: "Duration of completed playback sessions in seconds.");

        _transcodeReasons = JellytelMeter.Instance.CreateCounter<long>(
            "jellyfin.transcode.reasons",
            unit: "{reason}",
            description: "Number of transcodes started, broken down by reason. One increment per reason flag set.");

        _activeSessions = JellytelMeter.Instance.CreateObservableGauge<long>(
            "jellyfin.sessions.active",
            observeValue: ObserveActiveSessions,
            unit: "{session}",
            description: "Currently active Jellyfin sessions.");

        _sessionManager.PlaybackStart += OnPlaybackStart;
        _sessionManager.PlaybackStopped += OnPlaybackStopped;
        _subscribed = true;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_subscribed)
        {
            _sessionManager.PlaybackStart -= OnPlaybackStart;
            _sessionManager.PlaybackStopped -= OnPlaybackStopped;
            _subscribed = false;
        }
    }

    private long ObserveActiveSessions()
    {
        try
        {
            return _sessionManager.Sessions.Count(s => s.IsActive);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Jellytel metrics: failed to read active session count.");
            return 0;
        }
    }

    private void OnPlaybackStart(object? sender, PlaybackProgressEventArgs e)
    {
        try
        {
            var tags = BuildPlaybackTags(e);
            _playbackStarted?.Add(1, tags);

            var transcoding = e.Session?.TranscodingInfo;
            if (transcoding is not null)
            {
                EmitTranscodeReasons(transcoding.TranscodeReasons);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Jellytel metrics: PlaybackStart handler failed.");
        }
    }

    private void OnPlaybackStopped(object? sender, PlaybackStopEventArgs e)
    {
        try
        {
            var tags = BuildPlaybackTags(e);
            _playbackStopped?.Add(1, tags.Concat(new[]
            {
                new KeyValuePair<string, object?>("completed", e.PlayedToCompletion)
            }).ToArray());

            var runtimeTicks = e.MediaInfo?.RunTimeTicks;
            var positionTicks = e.PlaybackPositionTicks;
            if (positionTicks.HasValue && positionTicks.Value > 0)
            {
                var seconds = TimeSpan.FromTicks(positionTicks.Value).TotalSeconds;
                _playbackDuration?.Record(seconds, tags);
            }
            else if (runtimeTicks.HasValue && e.PlayedToCompletion)
            {
                var seconds = TimeSpan.FromTicks(runtimeTicks.Value).TotalSeconds;
                _playbackDuration?.Record(seconds, tags);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Jellytel metrics: PlaybackStopped handler failed.");
        }
    }

    private void EmitTranscodeReasons(TranscodeReason reasons)
    {
        if (reasons == 0)
        {
            return;
        }

        foreach (TranscodeReason flag in Enum.GetValues<TranscodeReason>())
        {
            if (flag != 0 && reasons.HasFlag(flag))
            {
                _transcodeReasons?.Add(
                    1,
                    new KeyValuePair<string, object?>("reason", flag.ToString()));
            }
        }
    }

    private static KeyValuePair<string, object?>[] BuildPlaybackTags(PlaybackProgressEventArgs e)
    {
        var session = e.Session;
        var playState = session?.PlayState;
        var transcoding = session?.TranscodingInfo;

        var playMethod = playState?.PlayMethod?.ToString() ?? "Unknown";
        var mediaType = e.MediaInfo?.MediaType.ToString() ?? "Unknown";
        var client = string.IsNullOrEmpty(session?.Client) ? "Unknown" : session.Client;
        var isTranscoding = transcoding is not null;
        var hwAccel = transcoding?.HardwareAccelerationType?.ToString() ?? "none";

        return new[]
        {
            new KeyValuePair<string, object?>("play_method", playMethod),
            new KeyValuePair<string, object?>("media_type", mediaType),
            new KeyValuePair<string, object?>("client", client),
            new KeyValuePair<string, object?>("is_transcoding", isTranscoding),
            new KeyValuePair<string, object?>("hw_accel", hwAccel),
        };
    }
}
