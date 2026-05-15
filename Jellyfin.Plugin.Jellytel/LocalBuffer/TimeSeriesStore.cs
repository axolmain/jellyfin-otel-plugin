using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Jellyfin.Plugin.Jellytel.Metrics;
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

    private static readonly Counter<long> WritesEnqueued = JellytelMeter.Instance.CreateCounter<long>(
        "jellytel.buffer.writes.enqueued",
        unit: "{sample}",
        description: "Samples accepted by the local buffer's write channel.");

    private static readonly Counter<long> WritesDropped = JellytelMeter.Instance.CreateCounter<long>(
        "jellytel.buffer.writes.dropped",
        unit: "{sample}",
        description: "Samples rejected by the local buffer's write channel (channel closed or full).");

    private static readonly Counter<long> FlushBatches = JellytelMeter.Instance.CreateCounter<long>(
        "jellytel.buffer.flush.batches",
        unit: "{batch}",
        description: "SQLite flush batches committed by the background writer.");

    private static readonly Histogram<double> FlushDurationMs = JellytelMeter.Instance.CreateHistogram<double>(
        "jellytel.buffer.flush.duration",
        unit: "ms",
        description: "Wall-clock duration of a single SQLite flush batch (open → commit).");

    private static readonly Histogram<long> FlushBatchSize = JellytelMeter.Instance.CreateHistogram<long>(
        "jellytel.buffer.flush.batch_size",
        unit: "{sample}",
        description: "Number of samples per flushed batch.");

    private static readonly Histogram<double> RetentionDurationMs = JellytelMeter.Instance.CreateHistogram<double>(
        "jellytel.buffer.retention.duration",
        unit: "ms",
        description: "Wall-clock duration of an opportunistic retention pass.");

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Performance",
        "CA1823:Avoid unused private fields",
        Justification = "Field exists to keep the ObservableGauge registered on the Meter; the SDK pulls values via its callback.")]
    private static readonly ObservableGauge<int> ChannelDepth = JellytelMeter.Instance.CreateObservableGauge<int>(
        "jellytel.buffer.channel.depth",
        observeValue: ObserveChannelDepth,
        unit: "{sample}",
        description: "Backlog of samples queued for the SQLite writer.");

    // Tracks the active store so the static channel-depth gauge can read it.
    // The store is recreated on config change; only the most recently constructed
    // instance is observed. Earlier instances clear this field on dispose.
    private static TimeSeriesStore? _current;

    private readonly string _connectionString;
    private readonly ILogger<TimeSeriesStore> _logger;
    private readonly Channel<MetricSample> _writes;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _writerTask;

    private int _writesSinceRetention;
    private int _retentionHours;
    private int _maxRows;
    private int _channelDepth;

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

        Interlocked.Exchange(ref _current, this);
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
        if (_writes.Writer.TryWrite(sample))
        {
            Interlocked.Increment(ref _channelDepth);
            WritesEnqueued.Add(1);
        }
        else
        {
            WritesDropped.Add(1);
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
    /// Reads the most recent value for each <c>tag_key</c> of a single
    /// tagged-sum metric (e.g. <c>jellyfin.sessions.active</c>). Used by the
    /// snapshot endpoint to sum across play_method buckets while keeping the
    /// per-bucket breakdown available for the dashboard.
    /// </summary>
    /// <param name="metricName">Metric to look up.</param>
    /// <returns>Map of tag_key → (latest ts, latest value). Empty when no samples.</returns>
    public IReadOnlyDictionary<string, (long TimestampMs, double Value)> ReadLatestByTag(string metricName)
    {
        var result = new Dictionary<string, (long, double)>(StringComparer.Ordinal);

        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        // Most-recent row per tag_key. Correlated subquery is fine here —
        // tag_key cardinality is small (4 play_method buckets) and the
        // ix_metric_samples_metric_ts index covers the lookup.
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT tag_key, ts, value
            FROM metric_samples AS m
            WHERE metric_name = $m
              AND ts = (
                  SELECT MAX(ts) FROM metric_samples
                  WHERE metric_name = $m AND tag_key = m.tag_key
              )
            GROUP BY tag_key
            """;
        cmd.Parameters.AddWithValue("$m", metricName);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result[reader.GetString(0)] = (reader.GetInt64(1), reader.GetDouble(2));
        }

        return result;
    }

    /// <summary>
    /// Reads bucketed series data for a single metric over a time range.
    /// Aggregation modes:
    /// <list type="bullet">
    ///   <item><c>sum</c> — sum within each bucket (counters).</item>
    ///   <item><c>avg</c> — average within each bucket (gauges, histograms).</item>
    ///   <item><c>sum_of_avg_by_tag</c> — average per <c>tag_key</c> within
    ///   each bucket, then sum across tags. Correct semantics for tagged
    ///   gauges where each tag is sampled multiple times per bucket and the
    ///   dashboard wants the total across tags.</item>
    /// </list>
    /// </summary>
    /// <param name="metricName">Metric to read.</param>
    /// <param name="fromMs">Start (inclusive) unix ms.</param>
    /// <param name="toMs">End (inclusive) unix ms.</param>
    /// <param name="bucketMs">Bucket width in ms. Must be &gt; 0.</param>
    /// <param name="aggregation">"sum", "avg", or "sum_of_avg_by_tag".</param>
    /// <returns>Ordered list of <c>(bucketStartMs, aggregatedValue)</c>.</returns>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Aggregation function selected from an internal allowlist; all user inputs are bound parameters.")]
    public IReadOnlyList<(long TimestampMs, double Value)> ReadSeries(
        string metricName,
        long fromMs,
        long toMs,
        long bucketMs,
        string aggregation)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bucketMs);

        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();

        if (aggregation.Equals("sum_of_avg_by_tag", StringComparison.OrdinalIgnoreCase))
        {
            cmd.CommandText = """
                SELECT bucket, SUM(v) AS total
                FROM (
                    SELECT (ts / $b) * $b AS bucket, tag_key, AVG(value) AS v
                    FROM metric_samples
                    WHERE metric_name = $m AND ts >= $from AND ts <= $to
                    GROUP BY bucket, tag_key
                )
                GROUP BY bucket
                ORDER BY bucket
                """;
        }
        else
        {
            var agg = aggregation.Equals("avg", StringComparison.OrdinalIgnoreCase) ? "AVG(value)" : "SUM(value)";
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
        }

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
        Interlocked.CompareExchange(ref _current, null, this);

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

    // Channel<T>.Reader.Count throws NotSupportedException on unbounded
    // channels, so we keep our own depth counter: incremented on successful
    // TryWrite, decremented when the writer loop drains a batch. Volatile
    // read is sufficient — we accept a brief skew vs. the writer drain
    // because this is an observability signal, not a control input.
    private static int ObserveChannelDepth()
    {
        var store = _current;
        return store is null ? 0 : Volatile.Read(ref store._channelDepth);
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

            Interlocked.Add(ref _channelDepth, -batch.Count);

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
        var start = Stopwatch.GetTimestamp();

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

        FlushBatches.Add(1);
        FlushBatchSize.Record(batch.Count);
        FlushDurationMs.Record(Stopwatch.GetElapsedTime(start).TotalMilliseconds);

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
        var start = Stopwatch.GetTimestamp();
        try
        {
            EnforceRetentionCore();
        }
        finally
        {
            RetentionDurationMs.Record(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }
    }

    private void EnforceRetentionCore()
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
