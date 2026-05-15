using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Jellytel.Configuration;
using Jellyfin.Plugin.Jellytel.LocalBuffer;
using Jellyfin.Plugin.Jellytel.Metrics.Export;
using Jellyfin.Plugin.Jellytel.Traces.Export;
using MediaBrowser.Model.Plugins;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Jellytel.Traces;

/// <summary>
/// Hosted service that owns the trace collection + export pipeline. Mirrors
/// <see cref="Metrics.MetricsBootstrapper"/>: pluggable
/// <see cref="IActivityCollector"/> and <see cref="ITraceExporter"/>, rebuilt
/// on configuration changes so endpoint / source-allowlist edits take effect
/// without restarting Jellyfin.
/// </summary>
public class TracesBootstrapper : IHostedService, IAsyncDisposable
{
    private static readonly TimeSpan ExportInterval = TimeSpan.FromSeconds(15);

    private readonly ILogger<TracesBootstrapper> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ExportStatusTracker _exportStatus;
    private readonly IHttpClientFactory? _httpClientFactory;

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1859", Justification = "Interface is the swap point.")]
    private IActivityCollector? _collector;
    private ITraceExporter? _exporter;
    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;

    /// <summary>
    /// Initializes a new instance of the <see cref="TracesBootstrapper"/> class.
    /// </summary>
    /// <param name="logger">Diagnostic logger.</param>
    /// <param name="loggerFactory">Factory for component loggers.</param>
    /// <param name="exportStatus">Status tracker for the dashboard panel.</param>
    /// <param name="httpClientFactory">Optional <see cref="IHttpClientFactory"/> for the OTLP exporter.</param>
    public TracesBootstrapper(
        ILogger<TracesBootstrapper> logger,
        ILoggerFactory loggerFactory,
        ExportStatusTracker exportStatus,
        IHttpClientFactory? httpClientFactory = null)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
        _exportStatus = exportStatus;
        _httpClientFactory = httpClientFactory;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // A throw from IHostedService.StartAsync brings down the Jellyfin host.
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
            _logger.LogError(ex, "Jellytel traces: startup failed; trace export disabled for this session.");
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
            _logger.LogWarning(ex, "Jellytel traces: shutdown cleanup failed.");
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await TeardownAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private static HashSet<string> BuildSourceAllowlist(string? csv)
    {
        var set = new HashSet<string>(StringComparer.Ordinal) { JellytelActivitySource.Name };
        if (string.IsNullOrWhiteSpace(csv))
        {
            return set;
        }

        foreach (var entry in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            set.Add(entry);
        }

        return set;
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
                _logger.LogError(ex, "Jellytel traces: reconfigure failed.");
            }
        });
    }

    private void ApplyConfiguration(PluginConfiguration? config)
    {
        if (config is null || !config.EnableTraces)
        {
            _logger.LogInformation("Jellytel traces: disabled by configuration.");
            return;
        }

        var sources = BuildSourceAllowlist(config.TracedActivitySources);
        var collector = new ActivityCollector(sources, _loggerFactory.CreateLogger<ActivityCollector>());
        collector.Start();
        _collector = collector;

        _exporter = BuildExporter(config);

        _loopCts = new CancellationTokenSource();
        _loopTask = Task.Run(() => ExportLoopAsync(_loopCts.Token));

        _logger.LogInformation(
            "Jellytel traces: enabled. exporter={Exporter} sources={SourceCount}",
            _exporter.Name,
            sources.Count);
    }

    private ITraceExporter BuildExporter(PluginConfiguration config)
    {
        if (string.IsNullOrWhiteSpace(config.OtlpEndpoint))
        {
            _logger.LogInformation("Jellytel traces: OTLP endpoint not configured, trace export disabled.");
            return new NullTraceExporter();
        }

        try
        {
            var endpoint = new Uri(config.OtlpEndpoint);
            var serviceName = string.IsNullOrWhiteSpace(config.ServiceName) ? "jellyfin" : config.ServiceName;
            var context = new ExporterContext(serviceName, Array.Empty<KeyValuePair<string, string>>());
            var http = _httpClientFactory?.CreateClient("jellytel-otlp");
            return new OtlpHttpTraceExporter(endpoint, context, _exportStatus, http, _loggerFactory.CreateLogger<OtlpHttpTraceExporter>());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Jellytel traces: failed to build OTLP trace exporter; falling back to no-op.");
            _exportStatus.MarkFailure(ex.Message);
            return new NullTraceExporter();
        }
    }

    private async Task ExportLoopAsync(CancellationToken ct)
    {
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
                    var batch = collector.Drain();
                    if (batch.Count > 0)
                    {
                        await exporter.ExportAsync(batch, ct).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Jellytel traces: export tick failed.");
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

        if (_exporter is not null)
        {
            await _exporter.DisposeAsync().ConfigureAwait(false);
            _exporter = null;
        }

        _collector?.Dispose();
        _collector = null;
    }
}
