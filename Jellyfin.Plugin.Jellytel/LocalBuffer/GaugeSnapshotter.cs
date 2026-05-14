using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Jellytel.LocalBuffer;

/// <summary>
/// Periodically samples observable-gauge values (active sessions, future:
/// item counts, storage) and records one row per gauge per tick. Lives in
/// the local-buffer module rather than the OTLP panels because OTLP gauges
/// are pulled at export time and don't fire when no exporter is configured.
/// </summary>
public sealed class GaugeSnapshotter : IAsyncDisposable
{
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
        var active = _sessionManager.Sessions.Count(s => s.IsActive);
        _store.Write(new MetricSample(ts, "jellyfin.sessions.active", string.Empty, active));
    }
}
