using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
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

namespace Jellyfin.Plugin.Jellytel;

/// <summary>
/// Wraps the global Serilog logger with a forwarding logger that fans events
/// to both the host's existing pipeline (console + file) and an OTLP sink, so
/// plugin-configured OpenTelemetry export runs alongside Jellyfin's normal
/// logging. Reapplies the swap when the plugin configuration changes so the
/// user does not need to restart.
/// </summary>
public class OpenTelemetryBootstrapper : IHostedService
{
    // Matches lines beginning with "[YYYY-MM-DD HH:mm:ss.fff +zz:zz] [LVL] [thread] SourceContext: message"
    // The trailing portion is greedy so multi-line exceptions get appended to the previous event by the caller.
    private static readonly Regex LogLineRegex = new(
        @"^\[(?<ts>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} [+-]\d{2}:\d{2})\] \[(?<lvl>[A-Z]{3})\] \[(?<thread>\d+)\] (?<src>[^:]+): (?<msg>.*)$",
        RegexOptions.Compiled);

    private readonly IApplicationPaths _appPaths;
    private readonly ILogger<OpenTelemetryBootstrapper> _logger;
    private readonly DateTimeOffset _processStart = DateTimeOffset.UtcNow;
    private Serilog.ILogger? _hostOriginalLogger;
    private Serilog.Core.Logger? _ourLogger;
    private bool _backfillRan;

    /// <summary>
    /// Initializes a new instance of the <see cref="OpenTelemetryBootstrapper"/> class.
    /// </summary>
    /// <param name="appPaths">Application paths used to locate the log directory.</param>
    /// <param name="logger">Diagnostic logger.</param>
    public OpenTelemetryBootstrapper(
        IApplicationPaths appPaths,
        ILogger<OpenTelemetryBootstrapper> logger)
    {
        _appPaths = appPaths;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _hostOriginalLogger = Log.Logger;

        if (Plugin.Instance is { } plugin)
        {
            plugin.ConfigurationChanged += OnConfigurationChanged;
        }

        ApplyConfiguration(Plugin.Instance?.Configuration);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (Plugin.Instance is { } plugin)
        {
            plugin.ConfigurationChanged -= OnConfigurationChanged;
        }

        RestoreHostLogger();
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
            var newLogger = new LoggerConfiguration()
                .MinimumLevel.Verbose()
                .WriteTo.Logger(hostLogger)
                .WriteTo.OpenTelemetry(opts =>
                {
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
                "Jellytel: OpenTelemetry log export enabled. endpoint={Endpoint} service.name={ServiceName}",
                logsEndpoint,
                serviceName);

            if (!_backfillRan && config!.BackfillBootLogs)
            {
                _backfillRan = true;
                Task.Run(() => ReplayBootLogs(newLogger, serviceName));
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

    private void ReplayBootLogs(Serilog.ILogger sink, string serviceName)
    {
        try
        {
            var logDir = _appPaths.LogDirectoryPath;
            if (!Directory.Exists(logDir))
            {
                return;
            }

            // log_YYYYMMDD.log files written by Serilog.Sinks.File rolling by day.
            var today = DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            var candidate = Path.Combine(logDir, $"log_{today}.log");
            if (!File.Exists(candidate))
            {
                candidate = new DirectoryInfo(logDir)
                    .EnumerateFiles("log_*.log")
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .FirstOrDefault()?.FullName ?? string.Empty;
            }

            if (string.IsNullOrEmpty(candidate) || !File.Exists(candidate))
            {
                _logger.LogInformation("Jellytel: no log file found to backfill from.");
                return;
            }

            var replayed = 0;
            using var stream = new FileStream(candidate, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            ParsedLine? pending = null;
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                var match = LogLineRegex.Match(line);
                if (match.Success)
                {
                    if (pending is not null && pending.Timestamp <= _processStart)
                    {
                        Emit(sink, pending);
                        replayed++;
                    }
                    else if (pending is not null)
                    {
                        // Reached events newer than process start; stop.
                        pending = null;
                        break;
                    }

                    var ts = DateTimeOffset.ParseExact(
                        match.Groups["ts"].Value,
                        "yyyy-MM-dd HH:mm:ss.fff zzz",
                        CultureInfo.InvariantCulture);

                    pending = new ParsedLine(
                        ts,
                        match.Groups["lvl"].Value,
                        match.Groups["src"].Value,
                        new StringBuilder(match.Groups["msg"].Value));
                }
                else if (pending is not null)
                {
                    pending.Body.Append('\n').Append(line);
                }
            }

            if (pending is not null && pending.Timestamp <= _processStart)
            {
                Emit(sink, pending);
                replayed++;
            }

            _logger.LogInformation(
                "Jellytel: backfilled {Count} pre-init log events from {File}.",
                replayed,
                candidate);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Jellytel: boot log backfill failed.");
        }
    }

    private static void Emit(Serilog.ILogger sink, ParsedLine entry)
    {
        var level = entry.Level switch
        {
            "VRB" => LogEventLevel.Verbose,
            "DBG" => LogEventLevel.Debug,
            "INF" => LogEventLevel.Information,
            "WRN" => LogEventLevel.Warning,
            "ERR" => LogEventLevel.Error,
            "FTL" => LogEventLevel.Fatal,
            _ => LogEventLevel.Information
        };

        // ForContext gives us the original SourceContext; the message body is treated as a literal string
        // since we no longer have its original template or properties.
        sink.ForContext("SourceContext", entry.Source)
            .ForContext("Backfilled", true)
            .ForContext("OriginalTimestamp", entry.Timestamp)
            .Write(level, "{Body}", entry.Body.ToString());
    }

    private sealed record ParsedLine(DateTimeOffset Timestamp, string Level, string Source, StringBuilder Body);
}
