using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Jellytel.Configuration;
using Jellyfin.Plugin.Jellytel.LocalBuffer;
using MediaBrowser.Model.Plugins;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;

namespace Jellyfin.Plugin.Jellytel.Metrics;

/// <summary>
/// Hosted service that owns the <see cref="MeterProvider"/> for the plugin
/// and registers configured <see cref="IMetricPanel"/> instances. Rebuilds
/// the provider when the plugin configuration changes so endpoint / panel
/// toggles take effect without restarting Jellyfin.
/// </summary>
public class MetricsBootstrapper : IHostedService
{
    private readonly ILogger<MetricsBootstrapper> _logger;
    private readonly IEnumerable<IMetricPanel> _panels;
    private readonly ExportStatusTracker _exportStatus;
    private readonly List<IMetricPanel> _registered = new();
    private MeterProvider? _meterProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="MetricsBootstrapper"/> class.
    /// </summary>
    /// <param name="logger">Diagnostic logger.</param>
    /// <param name="panels">Panels resolved from DI.</param>
    /// <param name="exportStatus">Status tracker for the dashboard panel.</param>
    public MetricsBootstrapper(
        ILogger<MetricsBootstrapper> logger,
        IEnumerable<IMetricPanel> panels,
        ExportStatusTracker exportStatus)
    {
        _logger = logger;
        _panels = panels;
        _exportStatus = exportStatus;
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
    public Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (Plugin.Instance is { } plugin)
            {
                plugin.ConfigurationChanged -= OnConfigurationChanged;
            }

            Teardown();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Jellytel metrics: shutdown cleanup failed.");
        }

        return Task.CompletedTask;
    }

    private void OnConfigurationChanged(object? sender, BasePluginConfiguration newConfig)
    {
        if (newConfig is PluginConfiguration cfg)
        {
            ApplyConfiguration(cfg);
        }
    }

    private void ApplyConfiguration(PluginConfiguration? config)
    {
        Teardown();

        if (config is null)
        {
            return;
        }

        if (!config.EnableMetrics)
        {
            _logger.LogInformation("Jellytel metrics: disabled by configuration.");
            return;
        }

        if (string.IsNullOrWhiteSpace(config.OtlpEndpoint))
        {
            _logger.LogInformation("Jellytel metrics: OTLP endpoint not configured, metric export disabled.");
            return;
        }

        var serviceName = string.IsNullOrWhiteSpace(config.ServiceName) ? "jellyfin" : config.ServiceName;
        var metricsEndpoint = config.OtlpEndpoint.TrimEnd('/') + "/v1/metrics";

        try
        {
            _meterProvider = Sdk.CreateMeterProviderBuilder()
                .AddMeter(JellytelMeter.Name)
                .ConfigureResource(r => r.AddService(serviceName))
                .AddOtlpExporter(o =>
                {
                    o.Endpoint = new Uri(metricsEndpoint);
                    o.Protocol = OtlpExportProtocol.HttpProtobuf;
                })
                .Build();

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

            _exportStatus.SetOtlpConfigured(true);

            _logger.LogInformation(
                "Jellytel metrics: enabled. endpoint={Endpoint} service.name={ServiceName} panels={PanelCount}",
                metricsEndpoint,
                serviceName,
                _registered.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Jellytel metrics: failed to initialize MeterProvider.");
            _exportStatus.MarkFailure(ex.Message);
            Teardown();
        }
    }

    private void Teardown()
    {
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

        _meterProvider?.Dispose();
        _meterProvider = null;
        _exportStatus.SetOtlpConfigured(false);
    }
}
