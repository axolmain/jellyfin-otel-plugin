using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using Jellyfin.Plugin.Jellytel.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Jellytel.Metrics.Panels;

/// <summary>
/// Transcode-specific metrics and structured logs. Split from
/// <see cref="SessionMetrics"/> so the admin's mental model (sessions vs.
/// transcodes are distinct concepts) is reflected in the code layout.
/// </summary>
/// <remarks>
/// Owns: transcode reasons counter, encode/source fps + speed-ratio
/// histograms, transcoded-bitrate histogram, transcode_started /
/// transcode_stopped structured log events.
/// </remarks>
public sealed class TranscodeEvents : IMetricPanel
{
    private readonly ISessionManager _sessionManager;
    private readonly ILogger<TranscodeEvents> _logger;

    private readonly ConcurrentDictionary<string, TranscodeStartRecord> _active = new(StringComparer.Ordinal);

    private Counter<long>? _reasons;
    private Histogram<double>? _encodeFps;
    private Histogram<double>? _sourceFps;
    private Histogram<double>? _speedRatio;
    private Histogram<long>? _bitrateTranscoded;

    private bool _subscribed;

    /// <summary>
    /// Initializes a new instance of the <see cref="TranscodeEvents"/> class.
    /// </summary>
    /// <param name="sessionManager">Jellyfin session manager.</param>
    /// <param name="logger">Diagnostic logger.</param>
    public TranscodeEvents(ISessionManager sessionManager, ILogger<TranscodeEvents> logger)
    {
        _sessionManager = sessionManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "TranscodeEvents";

    /// <inheritdoc />
    public bool IsEnabled(PluginConfiguration config) => config.EnableTranscodeEvents;

    /// <inheritdoc />
    public void Register()
    {
        _reasons = JellytelMeter.Instance.CreateCounter<long>(
            "jellyfin.transcode.reasons",
            unit: "{reason}",
            description: "Number of transcodes started, broken down by reason. One increment per reason flag set.");

        _encodeFps = JellytelMeter.Instance.CreateHistogram<double>(
            "jellyfin.transcode.encode_fps",
            unit: "{frame}/s",
            description: "ffmpeg encoder output frame rate sampled on PlaybackProgress ticks.");

        _sourceFps = JellytelMeter.Instance.CreateHistogram<double>(
            "jellyfin.transcode.source_fps",
            unit: "{frame}/s",
            description: "Source media frame rate (ReferenceFrameRate). Skipped when unresolvable; encode_fps still records.");

        _speedRatio = JellytelMeter.Instance.CreateHistogram<double>(
            "jellyfin.transcode.encode_speed_ratio",
            unit: "1",
            description: "encode_fps / source_fps. >1 = encoding faster than realtime; <1 = falling behind. Recorded only when both inputs are known.");

        _bitrateTranscoded = JellytelMeter.Instance.CreateHistogram<long>(
            "jellyfin.playback.bitrate.transcoded",
            unit: "bit/s",
            description: "Transcoded output bitrate, sampled on PlaybackProgress ticks for sessions where TranscodingInfo is present.");

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

        _active.Clear();
    }

    private void OnPlaybackStart(object? sender, PlaybackProgressEventArgs e)
    {
        try
        {
            var transcoding = e.Session?.TranscodingInfo;
            if (transcoding is null)
            {
                return;
            }

            EmitReasons(transcoding.TranscodeReasons);

            var key = e.PlaySessionId;
            if (!string.IsNullOrEmpty(key))
            {
                _active[key] = new TranscodeStartRecord(DateTime.UtcNow, ResolveSourceFps(e));
            }

            _logger.LogInformation(
                "transcode_started session={Session} user={User} item={Item} play_method={PlayMethod} hw_accel={HwAccel} source_codec={SrcVideo}/{SrcContainer} target_codec={DstVideo}/{DstContainer} source_bitrate={SrcBitrate} target_bitrate={DstBitrate} reasons={Reasons}",
                e.PlaySessionId ?? "?",
                e.Session?.UserName ?? "?",
                e.MediaInfo?.Name ?? "?",
                e.Session?.PlayState?.PlayMethod?.ToString() ?? "Unknown",
                transcoding.HardwareAccelerationType?.ToString() ?? "none",
                ResolveSourceVideoCodec(e),
                e.MediaInfo?.Container ?? "?",
                transcoding.VideoCodec ?? "?",
                transcoding.Container ?? "?",
                ResolveSourceBitrate(e),
                transcoding.Bitrate?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?",
                FormatReasons(transcoding.TranscodeReasons));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Jellytel transcode: PlaybackStart handler failed.");
        }
    }

    private void OnPlaybackProgress(object? sender, PlaybackProgressEventArgs e)
    {
        try
        {
            var transcoding = e.Session?.TranscodingInfo;
            if (transcoding is null)
            {
                return;
            }

            var hwAccelTag = new KeyValuePair<string, object?>(
                "hw_accel",
                transcoding.HardwareAccelerationType?.ToString() ?? "none");

            if (transcoding.Bitrate is { } bps && bps > 0)
            {
                _bitrateTranscoded?.Record(
                    bps,
                    hwAccelTag,
                    new KeyValuePair<string, object?>("play_method", "Transcode"));
            }

            var encodeFps = transcoding.Framerate;
            if (encodeFps is { } enc && enc > 0)
            {
                _encodeFps?.Record(enc, hwAccelTag);
            }

            var sourceFps = ResolveSourceFps(e);
            if (sourceFps is { } src && src > 0)
            {
                _sourceFps?.Record(src, hwAccelTag);

                if (encodeFps is { } enc2 && enc2 > 0)
                {
                    _speedRatio?.Record(enc2 / src, hwAccelTag);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Jellytel transcode: PlaybackProgress handler failed.");
        }
    }

    private void OnPlaybackStopped(object? sender, PlaybackStopEventArgs e)
    {
        try
        {
            var key = e.PlaySessionId;
            TranscodeStartRecord? start = null;
            if (!string.IsNullOrEmpty(key) && _active.TryRemove(key, out var rec))
            {
                start = rec;
            }

            // EventArgs may have cleared TranscodingInfo by stop time; the dict
            // entry is our evidence that this session was a transcode at start.
            if (start is null && e.Session?.TranscodingInfo is null)
            {
                return;
            }

            var elapsed = start is null
                ? (double?)null
                : (DateTime.UtcNow - start.StartedAtUtc).TotalSeconds;

            _logger.LogInformation(
                "transcode_stopped session={Session} user={User} item={Item} stop_reason={StopReason} elapsed_seconds={Elapsed}",
                e.PlaySessionId ?? "?",
                e.Session?.UserName ?? "?",
                e.MediaInfo?.Name ?? "?",
                e.PlayedToCompletion ? "completed" : "interrupted",
                elapsed?.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) ?? "?");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Jellytel transcode: PlaybackStopped handler failed.");
        }
    }

    private void EmitReasons(TranscodeReason reasons)
    {
        if (reasons == 0)
        {
            return;
        }

        foreach (TranscodeReason flag in Enum.GetValues<TranscodeReason>())
        {
            if (flag != 0 && reasons.HasFlag(flag))
            {
                _reasons?.Add(
                    1,
                    new KeyValuePair<string, object?>("reason", flag.ToString()));
            }
        }
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

    private static string ResolveSourceVideoCodec(PlaybackProgressEventArgs e)
    {
        var sources = e.MediaInfo?.MediaSources;
        if (sources is null)
        {
            return "?";
        }

        foreach (var src in sources)
        {
            if (src?.MediaStreams is null)
            {
                continue;
            }

            foreach (var s in src.MediaStreams)
            {
                if (s?.Type == MediaStreamType.Video && !string.IsNullOrEmpty(s.Codec))
                {
                    return s.Codec;
                }
            }
        }

        return "?";
    }

    private static string ResolveSourceBitrate(PlaybackProgressEventArgs e)
    {
        var sources = e.MediaInfo?.MediaSources;
        if (sources is null)
        {
            return "?";
        }

        foreach (var src in sources)
        {
            if (src?.Bitrate is { } b && b > 0)
            {
                return b.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        return "?";
    }

    private static string FormatReasons(TranscodeReason reasons)
    {
        if (reasons == 0)
        {
            return "none";
        }

        var parts = new List<string>();
        foreach (TranscodeReason flag in Enum.GetValues<TranscodeReason>())
        {
            if (flag != 0 && reasons.HasFlag(flag))
            {
                parts.Add(flag.ToString());
            }
        }

        return string.Join('|', parts);
    }

    private sealed record TranscodeStartRecord(DateTime StartedAtUtc, float? SourceFps);
}
