using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;
using Serilog.Events;

namespace Jellyfin.Plugin.Jellytel.Logs;

/// <summary>
/// One-shot helper that reparses the most recent Jellyfin log file and
/// replays events whose timestamps fall before the plugin's process-start
/// time. Used so OTLP gets a (lossy) record of boot diagnostics that
/// happened before the Serilog swap could take effect.
/// </summary>
public sealed class BootLogReplayer
{
    // Matches lines beginning with "[YYYY-MM-DD HH:mm:ss.fff +zz:zz] [LVL] [thread] SourceContext: message"
    // The trailing portion is greedy so multi-line exceptions get appended to the previous event by the caller.
    private static readonly Regex LogLineRegex = new(
        @"^\[(?<ts>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} [+-]\d{2}:\d{2})\] \[(?<lvl>[A-Z]{3})\] \[(?<thread>\d+)\] (?<src>[^:]+): (?<msg>.*)$",
        RegexOptions.Compiled);

    private readonly IApplicationPaths _appPaths;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="BootLogReplayer"/> class.
    /// </summary>
    /// <param name="appPaths">Application paths used to locate the log directory.</param>
    /// <param name="logger">Diagnostic logger.</param>
    public BootLogReplayer(IApplicationPaths appPaths, ILogger logger)
    {
        _appPaths = appPaths;
        _logger = logger;
    }

    /// <summary>
    /// Reads the current log file and re-emits events older than <paramref name="processStart"/>
    /// to <paramref name="sink"/>.
    /// </summary>
    /// <param name="sink">Serilog sink (typically the OTLP-forwarding logger).</param>
    /// <param name="processStart">Cutoff timestamp; events at or before this are replayed.</param>
    public void Replay(Serilog.ILogger sink, DateTimeOffset processStart)
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
                    if (pending is not null && pending.Timestamp <= processStart)
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

            if (pending is not null && pending.Timestamp <= processStart)
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
