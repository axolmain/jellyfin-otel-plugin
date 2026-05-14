using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Jellytel.LocalBuffer;

/// <summary>
/// SQLite-backed ring buffer for plugin metrics. Single writer, multi-reader.
/// </summary>
/// <remarks>
/// Writes are queued through an unbounded channel and drained by one
/// background task — keeps event handlers off the disk path and avoids
/// SQLite write contention. Reads open a fresh connection per query; SQLite
/// in WAL mode allows them to proceed concurrently with the writer.
/// Retention is enforced opportunistically (every N writes) rather than on a
/// timer to keep the moving parts small.
/// </remarks>
public sealed class TimeSeriesStore : IAsyncDisposable
{
    private const string CreateSchemaSql = """
        PRAGMA journal_mode = WAL;
        PRAGMA synchronous = NORMAL;

        CREATE TABLE IF NOT EXISTS metric_samples (
            ts          INTEGER NOT NULL,
            metric_name TEXT    NOT NULL,
            tag_key     TEXT    NOT NULL,
            value       REAL    NOT NULL
        );

        CREATE INDEX IF NOT EXISTS ix_metric_samples_metric_ts
            ON metric_samples (metric_name, ts);
        """;

    private readonly string _connectionString;
    private readonly ILogger<TimeSeriesStore> _logger;
    private readonly Channel<MetricSample> _writes;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _writerTask;

    private int _writesSinceRetention;
    private int _retentionHours;
    private int _maxRows;

    /// <summary>
    /// Initializes a new instance of the <see cref="TimeSeriesStore"/> class
    /// and starts the background writer.
    /// </summary>
    /// <param name="dbPath">Absolute path to the SQLite file. Parent directory is created if missing.</param>
    /// <param name="retentionHours">Initial retention window in hours.</param>
    /// <param name="maxRows">Initial row ceiling.</param>
    /// <param name="logger">Diagnostic logger.</param>
    public TimeSeriesStore(string dbPath, int retentionHours, int maxRows, ILogger<TimeSeriesStore> logger)
    {
        _logger = logger;
        _retentionHours = retentionHours;
        _maxRows = maxRows;

        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        }.ToString();

        InitializeSchema();
        EnforceRetention();

        _writes = Channel.CreateUnbounded<MetricSample>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });

        _writerTask = Task.Run(() => WriterLoopAsync(_shutdown.Token));
    }

    /// <summary>
    /// Updates retention parameters at runtime. Takes effect on the next
    /// opportunistic retention pass — there is no immediate purge.
    /// </summary>
    /// <param name="retentionHours">New retention window.</param>
    /// <param name="maxRows">New row ceiling.</param>
    public void UpdateRetention(int retentionHours, int maxRows)
    {
        Interlocked.Exchange(ref _retentionHours, retentionHours);
        Interlocked.Exchange(ref _maxRows, maxRows);
    }

    /// <summary>
    /// Enqueues a sample for the background writer. Non-blocking.
    /// </summary>
    /// <param name="sample">Sample to record.</param>
    public void Write(MetricSample sample)
    {
        if (!_writes.Writer.TryWrite(sample))
        {
            _logger.LogWarning("Jellytel buffer: write channel rejected sample {Metric}", sample.MetricName);
        }
    }

    /// <summary>
    /// Reads the most recent value for each <paramref name="metricNames"/>.
    /// </summary>
    /// <param name="metricNames">Metric names to look up.</param>
    /// <returns>Map of metric name → (timestamp, value). Metrics with no samples are absent.</returns>
    public IReadOnlyDictionary<string, (long TimestampMs, double Value)> ReadLatest(IReadOnlyCollection<string> metricNames)
    {
        var result = new Dictionary<string, (long, double)>(StringComparer.Ordinal);
        if (metricNames.Count == 0)
        {
            return result;
        }

        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        foreach (var name in metricNames)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT ts, value FROM metric_samples WHERE metric_name = $m ORDER BY ts DESC LIMIT 1";
            cmd.Parameters.AddWithValue("$m", name);
            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                result[name] = (reader.GetInt64(0), reader.GetDouble(1));
            }
        }

        return result;
    }

    /// <summary>
    /// Reads bucketed series data for a single metric over a time range.
    /// Buckets are SUM-aggregated for counters and AVG-aggregated for gauges;
    /// the caller picks via <paramref name="aggregation"/>.
    /// </summary>
    /// <param name="metricName">Metric to read.</param>
    /// <param name="fromMs">Start (inclusive) unix ms.</param>
    /// <param name="toMs">End (inclusive) unix ms.</param>
    /// <param name="bucketMs">Bucket width in ms. Must be &gt; 0.</param>
    /// <param name="aggregation">"sum" or "avg".</param>
    /// <returns>Ordered list of <c>(bucketStartMs, aggregatedValue)</c>.</returns>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Aggregation function selected from an internal allowlist (SUM/AVG); all user inputs are bound parameters.")]
    public IReadOnlyList<(long TimestampMs, double Value)> ReadSeries(
        string metricName,
        long fromMs,
        long toMs,
        long bucketMs,
        string aggregation)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bucketMs);

        var agg = aggregation.Equals("avg", StringComparison.OrdinalIgnoreCase) ? "AVG(value)" : "SUM(value)";

        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = string.Format(
            CultureInfo.InvariantCulture,
            """
            SELECT (ts / $b) * $b AS bucket, {0} AS v
            FROM metric_samples
            WHERE metric_name = $m AND ts >= $from AND ts <= $to
            GROUP BY bucket
            ORDER BY bucket
            """,
            agg);
        cmd.Parameters.AddWithValue("$m", metricName);
        cmd.Parameters.AddWithValue("$from", fromMs);
        cmd.Parameters.AddWithValue("$to", toMs);
        cmd.Parameters.AddWithValue("$b", bucketMs);

        var rows = new List<(long, double)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add((reader.GetInt64(0), reader.GetDouble(1)));
        }

        return rows;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _writes.Writer.TryComplete();
        await _shutdown.CancelAsync().ConfigureAwait(false);
        try
        {
            await _writerTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // expected on shutdown
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Jellytel buffer: writer task ended abnormally.");
        }

        _shutdown.Dispose();
        SqliteConnection.ClearAllPools();
    }

    private void InitializeSchema()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = CreateSchemaSql;
        cmd.ExecuteNonQuery();
    }

    private async Task WriterLoopAsync(CancellationToken ct)
    {
        // Batch reads from the channel so a burst of events turns into one
        // transaction rather than one transaction per row.
        var batch = new List<MetricSample>(capacity: 64);
        var reader = _writes.Reader;

        while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
        {
            batch.Clear();
            while (batch.Count < 256 && reader.TryRead(out var sample))
            {
                batch.Add(sample);
            }

            if (batch.Count == 0)
            {
                continue;
            }

            try
            {
                FlushBatch(batch);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Jellytel buffer: failed to flush {Count} samples.", batch.Count);
            }
        }
    }

    private void FlushBatch(List<MetricSample> batch)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var tx = conn.BeginTransaction();
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO metric_samples (ts, metric_name, tag_key, value) VALUES ($t, $m, $k, $v)";
            var pTs = cmd.Parameters.Add("$t", SqliteType.Integer);
            var pName = cmd.Parameters.Add("$m", SqliteType.Text);
            var pKey = cmd.Parameters.Add("$k", SqliteType.Text);
            var pVal = cmd.Parameters.Add("$v", SqliteType.Real);

            foreach (var s in batch)
            {
                pTs.Value = s.TimestampMs;
                pName.Value = s.MetricName;
                pKey.Value = s.TagKey;
                pVal.Value = s.Value;
                cmd.ExecuteNonQuery();
            }
        }

        tx.Commit();

        var since = Interlocked.Add(ref _writesSinceRetention, batch.Count);
        if (since >= 1000)
        {
            Interlocked.Exchange(ref _writesSinceRetention, 0);
            try
            {
                EnforceRetention();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Jellytel buffer: retention pass failed.");
            }
        }
    }

    private void EnforceRetention()
    {
        var retentionHours = Volatile.Read(ref _retentionHours);
        var maxRows = Volatile.Read(ref _maxRows);

        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        if (retentionHours > 0)
        {
            var cutoff = DateTimeOffset.UtcNow.AddHours(-retentionHours).ToUnixTimeMilliseconds();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM metric_samples WHERE ts < $c";
            cmd.Parameters.AddWithValue("$c", cutoff);
            cmd.ExecuteNonQuery();
        }

        if (maxRows > 0)
        {
            using var count = conn.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM metric_samples";
            var rows = Convert.ToInt64(count.ExecuteScalar(), CultureInfo.InvariantCulture);
            if (rows > maxRows)
            {
                using var trim = conn.CreateCommand();
                trim.CommandText = """
                    DELETE FROM metric_samples
                    WHERE rowid IN (SELECT rowid FROM metric_samples ORDER BY ts ASC LIMIT $n)
                    """;
                trim.Parameters.AddWithValue("$n", rows - maxRows);
                trim.ExecuteNonQuery();
            }
        }
    }
}
