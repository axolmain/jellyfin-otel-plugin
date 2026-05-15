using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Jellytel.LocalBuffer;

/// <summary>
/// Periodically samples observable-gauge values (active sessions by play
/// method, aggregate outbound bitrate) and records one row per bucket per
/// tick. Lives in the local-buffer module rather than the OTLP panels
/// because OTLP gauges are pulled at export time and don't fire when no
/// exporter is configured.
/// </summary>
public sealed class GaugeSnapshotter : IAsyncDisposable
{
    private static readonly string[] PlayMethodBuckets = { "DirectPlay", "DirectStream", "Transcode", "Unknown" };

    private readonly ISessionManager _sessionManager;
    private readonly TimeSeriesStore _store;
    private readonly ILogger<GaugeSnapshotter> _logger;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;
    private int _intervalSeconds;

    /// <summary>
    /// Initializes a new instance of the <see cref="GaugeSnapshotter"/> class.
    /// </summary>
    /// <param name="sessionManager">Jellyfin session manager.</param>
    /// <param name="store">Time-series store sink.</param>
    /// <param name="logger">Diagnostic logger.</param>
    public GaugeSnapshotter(ISessionManager sessionManager, TimeSeriesStore store, ILogger<GaugeSnapshotter> logger)
    {
        _sessionManager = sessionManager;
        _store = store;
        _logger = logger;
    }

    /// <summary>
    /// Starts the sampling loop. Safe to call once per instance.
    /// </summary>
    /// <param name="intervalSeconds">Sampling interval. Clamped to a minimum of 5s.</param>
    public void Start(int intervalSeconds)
    {
        _intervalSeconds = Math.Max(5, intervalSeconds);
        _loop ??= Task.Run(() => RunAsync(_cts.Token));
    }

    /// <summary>
    /// Adjusts the sampling interval at runtime. Takes effect after the
    /// next tick wakes from its current delay.
    /// </summary>
    /// <param name="intervalSeconds">New interval. Clamped to a minimum of 5s.</param>
    public void UpdateInterval(int intervalSeconds)
        => Interlocked.Exchange(ref _intervalSeconds, Math.Max(5, intervalSeconds));

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // expected
            }
        }

        _cts.Dispose();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                Snapshot();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Jellytel buffer: gauge snapshot failed.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Volatile.Read(ref _intervalSeconds)), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void Snapshot()
    {
        var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var staleThreshold = ReadStaleThreshold();
        var now = DateTime.UtcNow;

        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        var bitrates = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var bucket in PlayMethodBuckets)
        {
            counts[bucket] = 0;
            bitrates[bucket] = 0;
        }

        foreach (var s in _sessionManager.Sessions)
        {
            if (!IsLiveSession(s, now, staleThreshold))
            {
                continue;
            }

            var bucket = NormalizePlayMethod(s.PlayState?.PlayMethod);
            counts[bucket] = counts[bucket] + 1;

            var bps = TryGetOutboundBitrate(s);
            if (bps > 0)
            {
                bitrates[bucket] = bitrates[bucket] + bps;
            }
        }

        foreach (var bucket in PlayMethodBuckets)
        {
            var tag = MetricSample.EncodeTags(new[]
            {
                new KeyValuePair<string, object?>("play_method", bucket),
            });

            _store.Write(new MetricSample(ts, "jellyfin.sessions.active", tag, counts[bucket]));
            _store.Write(new MetricSample(ts, "jellyfin.playback.bitrate.total", tag, bitrates[bucket]));
        }
    }

    private static long TryGetOutboundBitrate(SessionInfo s)
    {
        var transcoded = s.TranscodingInfo?.Bitrate;
        if (transcoded.HasValue && transcoded.Value > 0)
        {
            return transcoded.Value;
        }

        // SessionInfo.NowPlayingItem.MediaSources is empty in the live session
        // collection; use FullNowPlayingItem (controller-side BaseItem) for the
        // cached TotalBitrate.
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
}
