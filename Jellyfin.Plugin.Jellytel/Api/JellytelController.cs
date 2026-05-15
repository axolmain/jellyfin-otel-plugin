using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Jellytel.LocalBuffer;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Session;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Jellytel.Api;

/// <summary>
/// REST surface that the embedded dashboard page consumes.
/// </summary>
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("Jellytel")]
[Produces("application/json")]
public class JellytelController : ControllerBase
{
    private static readonly string[] CounterMetrics =
    {
        "jellyfin.playback.started",
        "jellyfin.playback.stopped",
        "jellyfin.transcode.reasons",
        "jellyfin.playback.duration",
    };

    private static readonly string[] GaugeMetrics =
    {
        "jellyfin.sessions.active",
        "jellyfin.playback.bitrate.total",
        "jellyfin.playback.bitrate.native",
        "jellyfin.playback.bitrate.transcoded",
        "jellyfin.transcode.encode_fps",
        "jellyfin.transcode.source_fps",
        "jellyfin.transcode.encode_speed_ratio",
    };

    /// <summary>
    /// Metrics that are emitted with a <c>play_method</c> tag per scrape.
    /// The dashboard sums across tags for these.
    /// </summary>
    private static readonly HashSet<string> TaggedSumMetrics = new(StringComparer.Ordinal)
    {
        "jellyfin.sessions.active",
        "jellyfin.playback.bitrate.total",
    };

    private readonly LocalBufferBootstrapper _buffer;
    private readonly ExportStatusTracker _exportStatus;
    private readonly ISessionManager _sessionManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="JellytelController"/> class.
    /// </summary>
    /// <param name="buffer">Local buffer bootstrapper (used to reach the live store).</param>
    /// <param name="exportStatus">OTLP export status tracker.</param>
    /// <param name="sessionManager">Jellyfin session manager (for the debug endpoint).</param>
    public JellytelController(LocalBufferBootstrapper buffer, ExportStatusTracker exportStatus, ISessionManager sessionManager)
    {
        _buffer = buffer;
        _exportStatus = exportStatus;
        _sessionManager = sessionManager;
    }

    /// <summary>
    /// Returns the most recent value for every tracked metric. For
    /// <see cref="TaggedSumMetrics"/> entries the value is summed across all
    /// tag combinations in the latest scrape, and per-bucket breakdowns are
    /// returned in <see cref="SnapshotEntry.Breakdown"/>.
    /// </summary>
    /// <returns>Snapshot DTO with one entry per metric.</returns>
    [HttpGet("Snapshot")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<SnapshotDto> GetSnapshot()
    {
        // Buffer disabled is a normal "the user didn't turn this on" state,
        // not a service failure — return 200 with BufferEnabled=false so the
        // dashboard page can render its empty-state without catching errors.
        var store = _buffer.Store;
        if (store is null)
        {
            return new SnapshotDto
            {
                BufferEnabled = false,
                Metrics = Array.Empty<SnapshotEntry>(),
            };
        }

        var all = CounterMetrics.Concat(GaugeMetrics).ToArray();
        var entries = new List<SnapshotEntry>(all.Length);

        foreach (var name in all)
        {
            if (TaggedSumMetrics.Contains(name))
            {
                var bucketed = store.ReadLatestByTag(name);
                if (bucketed.Count == 0)
                {
                    continue;
                }

                var sum = 0.0;
                var latestTs = 0L;
                var breakdown = new Dictionary<string, double>(StringComparer.Ordinal);
                foreach (var kv in bucketed)
                {
                    sum += kv.Value.Value;
                    if (kv.Value.TimestampMs > latestTs)
                    {
                        latestTs = kv.Value.TimestampMs;
                    }

                    breakdown[ExtractPlayMethod(kv.Key)] = kv.Value.Value;
                }

                entries.Add(new SnapshotEntry
                {
                    Metric = name,
                    TimestampMs = latestTs,
                    Value = sum,
                    Breakdown = breakdown,
                });
            }
            else
            {
                var latest = store.ReadLatest(new[] { name });
                if (!latest.ContainsKey(name))
                {
                    continue;
                }

                entries.Add(new SnapshotEntry
                {
                    Metric = name,
                    TimestampMs = latest[name].TimestampMs,
                    Value = latest[name].Value,
                });
            }
        }

        return new SnapshotDto { BufferEnabled = true, Metrics = entries.ToArray() };
    }

    /// <summary>
    /// Returns a bucketed time series for a single metric. Tagged-sum metrics
    /// are aggregated as the sum across all tag combinations within each bucket.
    /// </summary>
    /// <param name="metric">Metric name (must be one of the tracked metrics).</param>
    /// <param name="fromMs">Range start in unix ms. Defaults to 24h ago.</param>
    /// <param name="toMs">Range end in unix ms. Defaults to now.</param>
    /// <param name="bucketMs">Bucket width in ms. Defaults to 60000 (1 min).</param>
    /// <returns>Ordered list of <c>(ts, value)</c> pairs.</returns>
    [HttpGet("Series")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<SeriesDto> GetSeries(
        [FromQuery] string metric,
        [FromQuery] long? fromMs = null,
        [FromQuery] long? toMs = null,
        [FromQuery] long? bucketMs = null)
    {
        if (string.IsNullOrWhiteSpace(metric))
        {
            return BadRequest("metric is required");
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var from = fromMs ?? (now - (TimeSpan.FromHours(24).Ticks / TimeSpan.TicksPerMillisecond));
        var to = toMs ?? now;
        var bucket = bucketMs ?? 60_000;

        // Tagged sums need a different per-bucket aggregation: within each
        // time bucket, AVG across samples-per-tag (gauge semantics), then SUM
        // across tags. That's "sum-of-avg" — implemented in the store.
        string aggregation;
        if (TaggedSumMetrics.Contains(metric))
        {
            aggregation = "sum_of_avg_by_tag";
        }
        else
        {
            aggregation = Array.IndexOf(GaugeMetrics, metric) >= 0 ? "avg" : "sum";
        }

        // Buffer disabled → empty series, not an error. Dashboard renders the
        // empty-state in the chart card.
        var store = _buffer.Store;
        if (store is null)
        {
            return new SeriesDto
            {
                Metric = metric,
                Aggregation = aggregation,
                BucketMs = bucket,
                Points = Array.Empty<SeriesPoint>(),
            };
        }

        var rows = store.ReadSeries(metric, from, to, bucket, aggregation);

        return new SeriesDto
        {
            Metric = metric,
            Aggregation = aggregation,
            BucketMs = bucket,
            Points = rows.Select(r => new SeriesPoint { TimestampMs = r.TimestampMs, Value = r.Value }).ToArray(),
        };
    }

    /// <summary>
    /// Returns OTLP export status — whether an exporter is configured and
    /// the last observed success/failure timestamps.
    /// </summary>
    /// <returns>Export status DTO.</returns>
    [HttpGet("ExportStatus")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<ExportStatusDto> GetExportStatus()
        => new ExportStatusDto
        {
            OtlpConfigured = _exportStatus.OtlpConfigured,
            LastExportSuccessMs = _exportStatus.LastExportSuccessMs,
            LastExportFailureMs = _exportStatus.LastExportFailureMs,
            LastError = _exportStatus.LastError,
        };

    /// <summary>
    /// Diagnostic endpoint that projects the current <see cref="ISessionManager.Sessions"/>
    /// state. Use when a dashboard card disagrees with reality — the response
    /// shows exactly what the snapshotter is seeing per session, including
    /// the staleness-filter verdict and the resolved outbound bitrate.
    /// </summary>
    /// <returns>Projected session state.</returns>
    [HttpGet("Debug/Sessions")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<SessionDebugDto> GetDebugSessions()
    {
        var stale = Plugin.Instance?.Configuration.StaleSessionSeconds ?? 90;
        if (stale < 15)
        {
            stale = 15;
        }

        var now = DateTime.UtcNow;
        var staleThreshold = TimeSpan.FromSeconds(stale);

        var dto = new SessionDebugDto
        {
            ServerTimeMs = new DateTimeOffset(now).ToUnixTimeMilliseconds(),
            StaleSessionSeconds = stale,
        };

        foreach (var s in _sessionManager.Sessions)
        {
            dto.Sessions.Add(Project(s, now, staleThreshold));
        }

        return dto;
    }

    private static SessionDebugRow Project(SessionInfo s, DateTime now, TimeSpan staleThreshold)
    {
        var item = s.NowPlayingItem;
        var sources = item?.MediaSources;
        var matchedSource = ResolveSource(sources, s.PlayState?.MediaSourceId);

        var sourceVideoStream = matchedSource?.MediaStreams?
            .FirstOrDefault(stream => stream?.Type == MediaStreamType.Video);

        var lastCheckIn = s.LastPlaybackCheckIn;
        var secondsSince = lastCheckIn == default
            ? 0.0
            : (now - lastCheckIn).TotalSeconds;

        var transcoding = s.TranscodingInfo;
        return new SessionDebugRow
        {
            SessionId = s.Id,
            UserName = s.UserName,
            Client = s.Client,
            DeviceName = s.DeviceName,
            RemoteEndPoint = s.RemoteEndPoint,
            PlayMethod = s.PlayState?.PlayMethod?.ToString(),
            NowPlayingItem = item?.Name,
            MediaSourceId = s.PlayState?.MediaSourceId,
            MediaSourcesCount = sources?.Length ?? 0,
            MediaSourceBitrate = matchedSource?.Bitrate,
            SourceFps = sourceVideoStream?.ReferenceFrameRate ?? sourceVideoStream?.RealFrameRate ?? sourceVideoStream?.AverageFrameRate,
            SourceVideoCodec = sourceVideoStream?.Codec,
            TranscodingBitrate = transcoding?.Bitrate,
            TranscodingFramerate = transcoding?.Framerate,
            TranscodingHwAccel = transcoding?.HardwareAccelerationType?.ToString(),
            TranscodingVideoCodec = transcoding?.VideoCodec,
            TranscodeReasons = transcoding?.TranscodeReasons.ToString(),
            IsActive = s.IsActive,
            LastActivityMs = new DateTimeOffset(s.LastActivityDate, TimeSpan.Zero).ToUnixTimeMilliseconds(),
            LastPlaybackCheckInMs = lastCheckIn == default ? 0L : new DateTimeOffset(lastCheckIn, TimeSpan.Zero).ToUnixTimeMilliseconds(),
            SecondsSinceLastCheckIn = secondsSince,
            PassesStaleFilter = s.IsActive
                && item is not null
                && (lastCheckIn == default || now - lastCheckIn <= staleThreshold),
            ResolvedOutboundBitrate = ResolveOutboundBitrate(s, matchedSource),
        };
    }

    private static MediaSourceInfo? ResolveSource(MediaSourceInfo[]? sources, string? preferredId)
    {
        if (sources is null || sources.Length == 0)
        {
            return null;
        }

        if (!string.IsNullOrEmpty(preferredId))
        {
            foreach (var src in sources)
            {
                if (src is not null && string.Equals(src.Id, preferredId, StringComparison.Ordinal))
                {
                    return src;
                }
            }
        }

        return sources[0];
    }

    private static long ResolveOutboundBitrate(SessionInfo s, MediaSourceInfo? matched)
    {
        var transcoded = s.TranscodingInfo?.Bitrate;
        if (transcoded.HasValue && transcoded.Value > 0)
        {
            return transcoded.Value;
        }

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
                // best-effort
            }
        }

        if (matched?.Bitrate is { } mb && mb > 0)
        {
            return mb;
        }

        var dtoSources = s.NowPlayingItem?.MediaSources;
        if (dtoSources is null)
        {
            return 0;
        }

        foreach (var src in dtoSources)
        {
            if (src?.Bitrate is { } sb && sb > 0)
            {
                return sb;
            }
        }

        return 0;
    }

    private static string ExtractPlayMethod(string tagKey)
    {
        // tagKey format: "play_method=DirectPlay" or "play_method=DirectPlay;something=else"
        if (string.IsNullOrEmpty(tagKey))
        {
            return "Unknown";
        }

        const string prefix = "play_method=";
        var idx = tagKey.IndexOf(prefix, StringComparison.Ordinal);
        if (idx < 0)
        {
            return "Unknown";
        }

        var start = idx + prefix.Length;
        var end = tagKey.IndexOf(';', start);
        return end < 0 ? tagKey.Substring(start) : tagKey.Substring(start, end - start);
    }
}
