using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Jellytel.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Plugins;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using Serilog.Sinks.OpenTelemetry;

namespace Jellyfin.Plugin.Jellytel.Logs;

/// <summary>
/// Wraps the global Serilog logger with a forwarding logger that fans events
/// to both the host's existing pipeline (console + file) and an OTLP sink, so
/// plugin-configured OpenTelemetry export runs alongside Jellyfin's normal
/// logging. Reapplies the swap when the plugin configuration changes so the
/// user does not need to restart.
/// </summary>
public class LogsBootstrapper : IHostedService
{
    private readonly IApplicationPaths _appPaths;
    private readonly ILogger<LogsBootstrapper> _logger;
    private readonly DateTimeOffset _processStart = DateTimeOffset.UtcNow;
    private Serilog.ILogger? _hostOriginalLogger;
    private Serilog.Core.Logger? _ourLogger;
    private bool _backfillRan;

    /// <summary>
    /// Initializes a new instance of the <see cref="LogsBootstrapper"/> class.
    /// </summary>
    /// <param name="appPaths">Application paths used to locate the log directory.</param>
    /// <param name="logger">Diagnostic logger.</param>
    public LogsBootstrapper(
        IApplicationPaths appPaths,
        ILogger<LogsBootstrapper> logger)
    {
        _appPaths = appPaths;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // A throw from an IHostedService.StartAsync brings down the entire Jellyfin host.
        // Swallow everything: a broken telemetry plugin must never crash the media server.
        try
        {
            _hostOriginalLogger = Log.Logger;

            if (Plugin.Instance is { } plugin)
            {
                plugin.ConfigurationChanged += OnConfigurationChanged;
            }

            ApplyConfiguration(Plugin.Instance?.Configuration);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Jellytel: startup failed; plugin disabled for this session.");
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

            RestoreHostLogger();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Jellytel: shutdown cleanup failed.");
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
        var endpoint = config?.OtlpEndpoint;

        if (string.IsNullOrWhiteSpace(endpoint))
        {
            RestoreHostLogger();
            _logger.LogInformation("Jellytel: OTLP endpoint not configured, OpenTelemetry log export disabled.");
            return;
        }

        var serviceName = string.IsNullOrWhiteSpace(config?.ServiceName) ? "jellyfin" : config!.ServiceName;
        var logsEndpoint = endpoint.TrimEnd('/') + "/v1/logs";

        if (_hostOriginalLogger is null)
        {
            _logger.LogWarning("Jellytel: host logger not captured, cannot enable OpenTelemetry export.");
            return;
        }

        try
        {
            var hostLogger = _hostOriginalLogger;
            var minimumLevel = config?.MinimumLevel ?? LogEventLevel.Debug;
            var newLogger = new LoggerConfiguration()
                .MinimumLevel.Verbose()
                .WriteTo.Logger(hostLogger)
                .WriteTo.OpenTelemetry(opts =>
                {
                    opts.RestrictedToMinimumLevel = minimumLevel;
                    opts.Endpoint = logsEndpoint;
                    opts.Protocol = OtlpProtocol.HttpProtobuf;
                    opts.ResourceAttributes = new Dictionary<string, object>
                    {
                        ["service.name"] = serviceName
                    };
                })
                .CreateLogger();

            var previous = _ourLogger;
            Log.Logger = newLogger;
            _ourLogger = newLogger;
            previous?.Dispose();

            _logger.LogInformation(
                "Jellytel: OpenTelemetry log export enabled. endpoint={Endpoint} service.name={ServiceName} minimumLevel={MinimumLevel}",
                logsEndpoint,
                serviceName,
                minimumLevel);

            if (!_backfillRan && config!.BackfillBootLogs)
            {
                _backfillRan = true;
                var replayer = new BootLogReplayer(_appPaths, _logger);
                Task.Run(() => replayer.Replay(newLogger, _processStart));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Jellytel: failed to initialize OpenTelemetry log export, leaving existing logger in place.");
        }
    }

    private void RestoreHostLogger()
    {
        if (_ourLogger is null)
        {
            return;
        }

        if (_hostOriginalLogger is not null)
        {
            Log.Logger = _hostOriginalLogger;
        }

        _ourLogger.Dispose();
        _ourLogger = null;
    }
}
