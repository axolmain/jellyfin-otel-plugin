using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Jellytel.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Plugins;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Jellytel.LocalBuffer;

/// <summary>
/// Hosted service that owns the local SQLite buffer lifecycle. Independent
/// of <c>MetricsBootstrapper</c> — toggling OTLP and the local buffer is
/// orthogonal. Rebuilds on configuration changes so retention / interval /
/// enabled flips take effect without a Jellyfin restart.
/// </summary>
public sealed class LocalBufferBootstrapper : IHostedService, IAsyncDisposable
{
    private readonly ILogger<LocalBufferBootstrapper> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IApplicationPaths _paths;
    private readonly ISessionManager _sessionManager;

    private TimeSeriesStore? _store;
    private EventRecorder? _events;
    private GaugeSnapshotter? _gauges;

    /// <summary>
    /// Initializes a new instance of the <see cref="LocalBufferBootstrapper"/> class.
    /// </summary>
    /// <param name="logger">Diagnostic logger for the bootstrapper.</param>
    /// <param name="loggerFactory">Factory for component loggers.</param>
    /// <param name="paths">Jellyfin application paths (for DB location).</param>
    /// <param name="sessionManager">Session manager for events and gauges.</param>
    public LocalBufferBootstrapper(
        ILogger<LocalBufferBootstrapper> logger,
        ILoggerFactory loggerFactory,
        IApplicationPaths paths,
        ISessionManager sessionManager)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
        _paths = paths;
        _sessionManager = sessionManager;
    }

    /// <summary>
    /// Gets the live store instance, or null when the buffer is disabled.
    /// Exposed for the dashboard API controller.
    /// </summary>
    public TimeSeriesStore? Store => _store;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Same defensive policy as MetricsBootstrapper: never let a telemetry
        // failure surface as a Jellyfin host crash.
        try
        {
            if (Plugin.Instance is { } plugin)
            {
                plugin.ConfigurationChanged += OnConfigurationChanged;
            }

            ApplyConfiguration(Plugin.Instance?.Configuration);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Jellytel buffer: startup failed; local buffer disabled for this session.");
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (Plugin.Instance is { } plugin)
            {
                plugin.ConfigurationChanged -= OnConfigurationChanged;
            }

            await TeardownAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Jellytel buffer: shutdown cleanup failed.");
        }
    }

    private void OnConfigurationChanged(object? sender, BasePluginConfiguration newConfig)
    {
        if (newConfig is not PluginConfiguration cfg)
        {
            return;
        }

        // Fast path: only the retention/interval changed → mutate in place.
        if (_store is not null && cfg.LocalBufferEnabled)
        {
            _store.UpdateRetention(cfg.LocalBufferRetentionHours, cfg.LocalBufferMaxRows);
            _gauges?.UpdateInterval(cfg.LocalBufferSampleIntervalSeconds);
            return;
        }

        // Otherwise rebuild.
        _ = Task.Run(async () =>
        {
            try
            {
                await TeardownAsync().ConfigureAwait(false);
                ApplyConfiguration(cfg);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Jellytel buffer: reconfigure failed.");
            }
        });
    }

    private void ApplyConfiguration(PluginConfiguration? config)
    {
        if (config is null || !config.LocalBufferEnabled)
        {
            _logger.LogInformation("Jellytel buffer: disabled by configuration.");
            return;
        }

        var dbPath = Path.Combine(_paths.DataPath, "plugins", "Jellytel", "timeseries.db");
        _store = new TimeSeriesStore(
            dbPath,
            config.LocalBufferRetentionHours,
            config.LocalBufferMaxRows,
            _loggerFactory.CreateLogger<TimeSeriesStore>());

        _events = new EventRecorder(_sessionManager, _store, _loggerFactory.CreateLogger<EventRecorder>());
        _events.Start();

        _gauges = new GaugeSnapshotter(_sessionManager, _store, _loggerFactory.CreateLogger<GaugeSnapshotter>());
        _gauges.Start(config.LocalBufferSampleIntervalSeconds);

        _logger.LogInformation(
            "Jellytel buffer: enabled. path={Path} interval={Interval}s retention={Retention}h maxRows={MaxRows}",
            dbPath,
            config.LocalBufferSampleIntervalSeconds,
            config.LocalBufferRetentionHours,
            config.LocalBufferMaxRows);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await TeardownAsync().ConfigureAwait(false);

    private async Task TeardownAsync()
    {
        _events?.Dispose();
        _events = null;

        if (_gauges is not null)
        {
            await _gauges.DisposeAsync().ConfigureAwait(false);
            _gauges = null;
        }

        if (_store is not null)
        {
            await _store.DisposeAsync().ConfigureAwait(false);
            _store = null;
        }
    }
}
