using System;
using System.Collections.Generic;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Jellytel.LocalBuffer;

/// <summary>
/// Subscribes to the same Jellyfin events the OTLP metric panels use and
/// records one <see cref="MetricSample"/> per event into the local buffer.
/// Parallel path — does not depend on the OTel SDK.
/// </summary>
public sealed class EventRecorder : IDisposable
{
    private readonly ISessionManager _sessionManager;
    private readonly TimeSeriesStore _store;
    private readonly ILogger<EventRecorder> _logger;
    private bool _subscribed;

    /// <summary>
    /// Initializes a new instance of the <see cref="EventRecorder"/> class.
    /// </summary>
    /// <param name="sessionManager">Jellyfin session manager.</param>
    /// <param name="store">Time-series store sink.</param>
    /// <param name="logger">Diagnostic logger.</param>
    public EventRecorder(ISessionManager sessionManager, TimeSeriesStore store, ILogger<EventRecorder> logger)
    {
        _sessionManager = sessionManager;
        _store = store;
        _logger = logger;
    }

    /// <summary>
    /// Subscribes to session events. Safe to call once per recorder instance.
    /// </summary>
    public void Start()
    {
        if (_subscribed)
        {
            return;
        }

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

    private void OnPlaybackStart(object? sender, PlaybackProgressEventArgs e)
    {
        try
        {
            var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var tags = MetricSample.EncodeTags(BuildTags(e));
            _store.Write(new MetricSample(ts, "jellyfin.playback.started", tags, 1));

            var transcoding = e.Session?.TranscodingInfo;
            if (transcoding is not null)
            {
                RecordTranscodeReasons(ts, transcoding.TranscodeReasons);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Jellytel buffer: PlaybackStart recorder failed.");
        }
    }

    private void OnPlaybackProgress(object? sender, PlaybackProgressEventArgs e)
    {
        try
        {
            var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var transcoding = e.Session?.TranscodingInfo;

            if (transcoding is null)
            {
                var native = TryGetNativeBitrate(e);
                if (native > 0)
                {
                    _store.Write(new MetricSample(
                        ts,
                        "jellyfin.playback.bitrate.native",
                        MetricSample.EncodeTags(BuildTags(e)),
                        native));
                }

                return;
            }

            var hwAccel = transcoding.HardwareAccelerationType?.ToString() ?? "none";
            var hwTag = new[]
            {
                new KeyValuePair<string, object?>("hw_accel", hwAccel),
            };
            var hwAndPlayTag = new[]
            {
                new KeyValuePair<string, object?>("hw_accel", hwAccel),
                new KeyValuePair<string, object?>("play_method", "Transcode"),
            };

            if (transcoding.Bitrate is { } bps && bps > 0)
            {
                _store.Write(new MetricSample(
                    ts,
                    "jellyfin.playback.bitrate.transcoded",
                    MetricSample.EncodeTags(hwAndPlayTag),
                    bps));
            }

            var encodeFps = transcoding.Framerate;
            if (encodeFps is { } enc && enc > 0)
            {
                _store.Write(new MetricSample(
                    ts,
                    "jellyfin.transcode.encode_fps",
                    MetricSample.EncodeTags(hwTag),
                    enc));
            }

            var sourceFps = ResolveSourceFps(e);
            if (sourceFps is { } src && src > 0)
            {
                _store.Write(new MetricSample(
                    ts,
                    "jellyfin.transcode.source_fps",
                    MetricSample.EncodeTags(hwTag),
                    src));

                if (encodeFps is { } enc2 && enc2 > 0)
                {
                    _store.Write(new MetricSample(
                        ts,
                        "jellyfin.transcode.encode_speed_ratio",
                        MetricSample.EncodeTags(hwTag),
                        enc2 / src));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Jellytel buffer: PlaybackProgress recorder failed.");
        }
    }

    private void OnPlaybackStopped(object? sender, PlaybackStopEventArgs e)
    {
        try
        {
            var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var baseTags = BuildTags(e);

            var stoppedTags = new List<KeyValuePair<string, object?>>(baseTags)
            {
                new("completed", e.PlayedToCompletion)
            };
            _store.Write(new MetricSample(ts, "jellyfin.playback.stopped", MetricSample.EncodeTags(stoppedTags), 1));

            var runtimeTicks = e.MediaInfo?.RunTimeTicks;
            var positionTicks = e.PlaybackPositionTicks;
            if (positionTicks.HasValue && positionTicks.Value > 0)
            {
                var seconds = TimeSpan.FromTicks(positionTicks.Value).TotalSeconds;
                _store.Write(new MetricSample(ts, "jellyfin.playback.duration", MetricSample.EncodeTags(baseTags), seconds));
            }
            else if (runtimeTicks.HasValue && e.PlayedToCompletion)
            {
                var seconds = TimeSpan.FromTicks(runtimeTicks.Value).TotalSeconds;
                _store.Write(new MetricSample(ts, "jellyfin.playback.duration", MetricSample.EncodeTags(baseTags), seconds));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Jellytel buffer: PlaybackStopped recorder failed.");
        }
    }

    private void RecordTranscodeReasons(long ts, TranscodeReason reasons)
    {
        if (reasons == 0)
        {
            return;
        }

        foreach (TranscodeReason flag in Enum.GetValues<TranscodeReason>())
        {
            if (flag != 0 && reasons.HasFlag(flag))
            {
                var tags = MetricSample.EncodeTags(new[]
                {
                    new KeyValuePair<string, object?>("reason", flag.ToString())
                });
                _store.Write(new MetricSample(ts, "jellyfin.transcode.reasons", tags, 1));
            }
        }
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

        // Args' MediaSources can be empty (depends on how the playback was
        // initialized). Reach to the controller-side item — its cached
        // TotalBitrate is populated for everything that has a parsed video.
        var full = e.Item;
        if (full is not null)
        {
            if (full.TotalBitrate is { } total && total > 0)
            {
                return total;
            }
        }

        return 0;
    }

    private static float? ResolveSourceFps(PlaybackProgressEventArgs e)
    {
        var sources = e.MediaInfo?.MediaSources;
        if (sources is null)
        {
            return null;
        }

        var preferredId = e.MediaSourceId;
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

            var fps = PickFpsFromStreams(src.MediaStreams);
            if (fps.HasValue)
            {
                return fps;
            }
        }

        foreach (var src in sources)
        {
            var fps = PickFpsFromStreams(src?.MediaStreams);
            if (fps.HasValue)
            {
                return fps;
            }
        }

        return null;
    }

    private static float? PickFpsFromStreams(IReadOnlyList<MediaStream>? streams)
    {
        if (streams is null)
        {
            return null;
        }

        foreach (var s in streams)
        {
            if (s is null || s.Type != MediaStreamType.Video)
            {
                continue;
            }

            return s.ReferenceFrameRate ?? s.RealFrameRate ?? s.AverageFrameRate;
        }

        return null;
    }

    private static KeyValuePair<string, object?>[] BuildTags(PlaybackProgressEventArgs e)
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
