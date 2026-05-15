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
/// Per-session_id is forbidden — unbounded over time.
/// </remarks>
public sealed class SessionMetrics : IMetricPanel
{
    private static readonly string[] PlayMethodBuckets = { "DirectPlay", "DirectStream", "Transcode", "Unknown" };

    private readonly ISessionManager _sessionManager;
    private readonly ILogger<SessionMetrics> _logger;

    private Counter<long>? _playbackStarted;
    private Counter<long>? _playbackStopped;
    private Histogram<double>? _playbackDuration;
    private Histogram<long>? _bitrateNative;
    private ObservableGauge<long>? _activeSessions;
    private ObservableGauge<long>? _bitrateTotal;

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

        _bitrateNative = JellytelMeter.Instance.CreateHistogram<long>(
            "jellyfin.playback.bitrate.native",
            unit: "bit/s",
            description: "Native (file) bitrate of DirectPlay/DirectStream sessions, sampled on each PlaybackProgress tick.");

        _activeSessions = JellytelMeter.Instance.CreateObservableGauge(
            "jellyfin.sessions.active",
            observeValues: ObserveActiveSessions,
            unit: "{session}",
            description: "Currently active Jellyfin sessions, split by play_method.");

        _bitrateTotal = JellytelMeter.Instance.CreateObservableGauge(
            "jellyfin.playback.bitrate.total",
            observeValues: ObserveBitrateTotal,
            unit: "bit/s",
            description: "Aggregate outbound bitrate across active sessions, split by play_method.");

        _sessionManager.PlaybackStart += OnPlaybackStart;
        _sessionManager.PlaybackProgress += OnPlaybackProgress;
        _sessionManager.PlaybackStopped += OnPlaybackStopped;
        _subscribed = true;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_subscribed)
        {
            _sessionManager.PlaybackStart -= OnPlaybackStart;
            _sessionManager.PlaybackProgress -= OnPlaybackProgress;
            _sessionManager.PlaybackStopped -= OnPlaybackStopped;
            _subscribed = false;
        }
    }

    /// <summary>
    /// Emits one measurement per <c>play_method</c> bucket so a stacked
    /// sparkline / Grafana query both work without losing the total. Empty
    /// buckets emit 0 to keep the series continuous.
    /// </summary>
    private IEnumerable<Measurement<long>> ObserveActiveSessions()
    {
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var bucket in PlayMethodBuckets)
        {
            counts[bucket] = 0;
        }

        try
        {
            var staleThreshold = ReadStaleThreshold();
            var now = DateTime.UtcNow;
            foreach (var s in _sessionManager.Sessions)
            {
                if (!IsLiveSession(s, now, staleThreshold))
                {
                    continue;
                }

                var bucket = NormalizePlayMethod(s.PlayState?.PlayMethod);
                counts[bucket] = counts[bucket] + 1;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Jellytel metrics: failed to read active session counts.");
        }

        foreach (var kv in counts)
        {
            yield return new Measurement<long>(
                kv.Value,
                new KeyValuePair<string, object?>("play_method", kv.Key));
        }
    }

    /// <summary>
    /// Sum of <c>TranscodingInfo.Bitrate ?? MediaSource.Bitrate ?? 0</c> across
    /// live sessions, grouped by <c>play_method</c>. Real-time view of total
    /// outbound bandwidth on the server.
    /// </summary>
    private IEnumerable<Measurement<long>> ObserveBitrateTotal()
    {
        var totals = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var bucket in PlayMethodBuckets)
        {
            totals[bucket] = 0;
        }

        try
        {
            var staleThreshold = ReadStaleThreshold();
            var now = DateTime.UtcNow;
            foreach (var s in _sessionManager.Sessions)
            {
                if (!IsLiveSession(s, now, staleThreshold))
                {
                    continue;
                }

                var bps = TryGetOutboundBitrate(s);
                if (bps <= 0)
                {
                    continue;
                }

                var bucket = NormalizePlayMethod(s.PlayState?.PlayMethod);
                totals[bucket] = totals[bucket] + bps;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Jellytel metrics: failed to compute aggregate bitrate.");
        }

        foreach (var kv in totals)
        {
            yield return new Measurement<long>(
                kv.Value,
                new KeyValuePair<string, object?>("play_method", kv.Key));
        }
    }

    private void OnPlaybackStart(object? sender, PlaybackProgressEventArgs e)
    {
        try
        {
            var tags = BuildPlaybackTags(e);
            _playbackStarted?.Add(1, tags);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Jellytel metrics: PlaybackStart handler failed.");
        }
    }

    private void OnPlaybackProgress(object? sender, PlaybackProgressEventArgs e)
    {
        try
        {
            // Only DirectPlay / DirectStream contribute to the native histogram —
            // transcoded sessions have their bitrate recorded in TranscodeEvents
            // with the transcoded codec/container as the meaningful dimension.
            var transcoding = e.Session?.TranscodingInfo;
            if (transcoding is not null)
            {
                return;
            }

            var bps = TryGetNativeBitrate(e);
            if (bps <= 0)
            {
                return;
            }

            _bitrateNative?.Record(bps, BuildPlaybackTags(e));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Jellytel metrics: PlaybackProgress handler failed.");
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

    private static long TryGetOutboundBitrate(SessionInfo s)
    {
        var transcoded = s.TranscodingInfo?.Bitrate;
        if (transcoded.HasValue && transcoded.Value > 0)
        {
            return transcoded.Value;
        }

        // SessionInfo.NowPlayingItem is a lightweight BaseItemDto with
        // MediaSources unpopulated — that's the trap. Reach to
        // FullNowPlayingItem (the controller-side BaseItem) for the cached
        // TotalBitrate field; fall back to GetMediaSources only if needed.
        var full = s.FullNowPlayingItem;
        if (full is not null)
        {
            if (full.TotalBitrate is { } total && total > 0)
            {
                return total;
            }

            try
            {
                var sources = full.GetMediaSources(false);
                if (sources is not null)
                {
                    foreach (var src in sources)
                    {
                        if (src?.Bitrate is { } b && b > 0)
                        {
                            return b;
                        }
                    }
                }
            }
            catch
            {
                // GetMediaSources is a virtual; some item types can throw.
                // Bitrate aggregation is best-effort, so swallow.
            }
        }

        // Final fallback: the dto's MediaSources, which is usually empty here
        // but free to consult.
        var dtoSources = s.NowPlayingItem?.MediaSources;
        if (dtoSources is null)
        {
            return 0;
        }

        foreach (var src in dtoSources)
        {
            if (src?.Bitrate is { } b && b > 0)
            {
                return b;
            }
        }

        return 0;
    }

    private static long TryGetNativeBitrate(PlaybackProgressEventArgs e)
    {
        var sources = e.MediaInfo?.MediaSources;
        var preferredId = e.MediaSourceId;
        if (sources is not null)
        {
            foreach (var src in sources)
            {
                if (src is null)
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(preferredId) && !string.Equals(src.Id, preferredId, StringComparison.Ordinal))
                {
                    continue;
                }

                if (src.Bitrate is { } b && b > 0)
                {
                    return b;
                }
            }

            foreach (var src in sources)
            {
                if (src?.Bitrate is { } b && b > 0)
                {
                    return b;
                }
            }
        }

        // Args MediaSources can be empty; fall back to the controller-side
        // item's cached TotalBitrate.
        if (e.Item?.TotalBitrate is { } total && total > 0)
        {
            return total;
        }

        return 0;
    }

    private static bool IsLiveSession(SessionInfo s, DateTime now, TimeSpan staleThreshold)
    {
        if (!s.IsActive || s.NowPlayingItem is null)
        {
            return false;
        }

        var lastCheckIn = s.LastPlaybackCheckIn;
        if (lastCheckIn == default)
        {
            return true;
        }

        return now - lastCheckIn <= staleThreshold;
    }

    private static TimeSpan ReadStaleThreshold()
    {
        var seconds = Plugin.Instance?.Configuration.StaleSessionSeconds ?? 90;
        if (seconds < 15)
        {
            seconds = 15;
        }

        return TimeSpan.FromSeconds(seconds);
    }

    private static string NormalizePlayMethod(PlayMethod? method)
    {
        if (method is null)
        {
            return "Unknown";
        }

        return method.Value switch
        {
            PlayMethod.DirectPlay => "DirectPlay",
            PlayMethod.DirectStream => "DirectStream",
            PlayMethod.Transcode => "Transcode",
            _ => "Unknown"
        };
    }

    private static KeyValuePair<string, object?>[] BuildPlaybackTags(PlaybackProgressEventArgs e)
    {
        var session = e.Session;
        var playState = session?.PlayState;
        var transcoding = session?.TranscodingInfo;

        var playMethod = NormalizePlayMethod(playState?.PlayMethod);
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
