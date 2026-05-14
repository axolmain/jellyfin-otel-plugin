using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Jellytel.Configuration;
using Jellyfin.Plugin.Jellytel.LocalBuffer;
using Jellyfin.Plugin.Jellytel.Metrics.Export;
using MediaBrowser.Model.Plugins;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Jellytel.Metrics;

/// <summary>
/// Hosted service that owns the metric collection + export pipeline. Built
/// on top of <see cref="IMeterCollector"/> and <see cref="IMetricExporter"/>
/// so the wire format / SDK can be swapped without changing this class.
/// Rebuilds the pipeline on configuration changes so endpoint / panel
/// toggles take effect without restarting Jellyfin.
/// </summary>
public class MetricsBootstrapper : IHostedService, IAsyncDisposable
{
    private static readonly TimeSpan ExportInterval = TimeSpan.FromSeconds(15);

    private readonly ILogger<MetricsBootstrapper> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IEnumerable<IMetricPanel> _panels;
    private readonly ExportStatusTracker _exportStatus;
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly List<IMetricPanel> _registered = new();

    // Intentionally typed as the interface — swapping collectors is the whole
    // point of the abstraction, so the CA1859 "use the concrete type" hint
    // would defeat the design here.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1859", Justification = "Interface is the swap point.")]
    private IMeterCollector? _collector;
    private IMetricExporter? _exporter;
    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;

    /// <summary>
    /// Initializes a new instance of the <see cref="MetricsBootstrapper"/> class.
    /// </summary>
    /// <param name="logger">Diagnostic logger.</param>
    /// <param name="loggerFactory">Factory for component loggers.</param>
    /// <param name="panels">Panels resolved from DI.</param>
    /// <param name="exportStatus">Status tracker for the dashboard panel.</param>
    /// <param name="httpClientFactory">Optional <see cref="IHttpClientFactory"/> for the OTLP exporter.</param>
    public MetricsBootstrapper(
        ILogger<MetricsBootstrapper> logger,
        ILoggerFactory loggerFactory,
        IEnumerable<IMetricPanel> panels,
        ExportStatusTracker exportStatus,
        IHttpClientFactory? httpClientFactory = null)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
        _panels = panels;
        _exportStatus = exportStatus;
        _httpClientFactory = httpClientFactory;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // A throw from an IHostedService.StartAsync brings down the entire Jellyfin host.
        // Swallow everything: a broken telemetry plugin must never crash the media server.
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
            _logger.LogError(ex, "Jellytel metrics: startup failed; metrics disabled for this session.");
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
            _logger.LogWarning(ex, "Jellytel metrics: shutdown cleanup failed.");
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await TeardownAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private void OnConfigurationChanged(object? sender, BasePluginConfiguration newConfig)
    {
        if (newConfig is not PluginConfiguration cfg)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await TeardownAsync().ConfigureAwait(false);
                ApplyConfiguration(cfg);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Jellytel metrics: reconfigure failed.");
            }
        });
    }

    private void ApplyConfiguration(PluginConfiguration? config)
    {
        if (config is null || !config.EnableMetrics)
        {
            _logger.LogInformation("Jellytel metrics: disabled by configuration.");
            return;
        }

        var collector = new MeterCollector(JellytelMeter.Name, _loggerFactory.CreateLogger<MeterCollector>());
        collector.Start();
        _collector = collector;

        // Panels must register AFTER the collector starts so MeterListener.InstrumentPublished
        // fires for newly-created instruments.
        foreach (var panel in _panels)
        {
            if (!panel.IsEnabled(config))
            {
                continue;
            }

            try
            {
                panel.Register();
                _registered.Add(panel);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Jellytel metrics: panel {Panel} failed to register; skipping.", panel.Name);
            }
        }

        _exporter = BuildExporter(config);
        _exportStatus.SetOtlpConfigured(_exporter is OtlpHttpExporter);

        _loopCts = new CancellationTokenSource();
        _loopTask = Task.Run(() => ExportLoopAsync(_loopCts.Token));

        _logger.LogInformation(
            "Jellytel metrics: enabled. exporter={Exporter} panels={PanelCount}",
            _exporter.Name,
            _registered.Count);
    }

    private IMetricExporter BuildExporter(PluginConfiguration config)
    {
        if (string.IsNullOrWhiteSpace(config.OtlpEndpoint))
        {
            _logger.LogInformation("Jellytel metrics: OTLP endpoint not configured, metric export disabled (collector still feeds local buffer).");
            return new NullExporter();
        }

        try
        {
            var endpoint = new Uri(config.OtlpEndpoint);
            var serviceName = string.IsNullOrWhiteSpace(config.ServiceName) ? "jellyfin" : config.ServiceName;
            var context = new ExporterContext(serviceName, Array.Empty<KeyValuePair<string, string>>());
            var http = _httpClientFactory?.CreateClient("jellytel-otlp");
            return new OtlpHttpExporter(endpoint, context, _exportStatus, http, _loggerFactory.CreateLogger<OtlpHttpExporter>());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Jellytel metrics: failed to build OTLP exporter; falling back to no-op.");
            _exportStatus.MarkFailure(ex.Message);
            return new NullExporter();
        }
    }

    private async Task ExportLoopAsync(CancellationToken ct)
    {
        // Initial delay so the first scrape captures at least one window of data.
        try
        {
            await Task.Delay(ExportInterval, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_collector is { } collector && _exporter is { } exporter)
                {
                    var snapshot = collector.Scrape();
                    await exporter.ExportAsync(snapshot, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Jellytel metrics: export tick failed.");
            }

            try
            {
                await Task.Delay(ExportInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task TeardownAsync()
    {
        if (_loopCts is not null)
        {
            await _loopCts.CancelAsync().ConfigureAwait(false);
            if (_loopTask is not null)
            {
                try
                {
                    await _loopTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // expected
                }
            }

            _loopCts.Dispose();
            _loopCts = null;
            _loopTask = null;
        }

        foreach (var panel in _registered)
        {
            try
            {
                panel.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Jellytel metrics: panel {Panel} dispose failed.", panel.Name);
            }
        }

        _registered.Clear();

        if (_exporter is not null)
        {
            await _exporter.DisposeAsync().ConfigureAwait(false);
            _exporter = null;
        }

        _collector?.Dispose();
        _collector = null;

        _exportStatus.SetOtlpConfigured(false);
    }
}
