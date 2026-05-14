using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Jellytel.Metrics.Export;

/// <summary>
/// <see cref="IMeterCollector"/> backed by <c>System.Diagnostics.Metrics.MeterListener</c>.
/// This is the only file in the plugin that touches <c>MeterListener</c>; the
/// rest of the metrics pipeline consumes <see cref="MetricSnapshot"/>.
/// </summary>
/// <remarks>
/// Threading model: callbacks from <c>MeterListener</c> are invoked on the
/// recording thread for synchronous instruments and on whoever drives
/// <c>RecordObservableInstruments()</c> for observable ones. Aggregation state
/// is protected by a single lock; contention is fine because the scrape
/// cadence (seconds) keeps writes serialized anyway.
/// </remarks>
public sealed class MeterCollector : IMeterCollector
{
    // OTel SDK's default histogram bucket boundaries (seconds-friendly).
    private static readonly double[] DefaultBuckets =
    {
        0, 5, 10, 25, 50, 75, 100, 250, 500, 750, 1000, 2500, 5000, 7500, 10000
    };

    private readonly string _meterName;
    private readonly ILogger<MeterCollector> _logger;
    private readonly object _gate = new();
    private readonly Dictionary<InstrumentKey, NumberAggregate> _numbers = new();
    private readonly Dictionary<InstrumentKey, HistogramAggregate> _histograms = new();
    private readonly Dictionary<Instrument, InstrumentKind> _kinds = new();
    private readonly DateTimeOffset _startTime = DateTimeOffset.UtcNow;

    private MeterListener? _listener;
    private DateTimeOffset _lastScrape;

    /// <summary>
    /// Initializes a new instance of the <see cref="MeterCollector"/> class.
    /// </summary>
    /// <param name="meterName">Meter name to subscribe to (typically <see cref="JellytelMeter.Name"/>).</param>
    /// <param name="logger">Diagnostic logger.</param>
    public MeterCollector(string meterName, ILogger<MeterCollector> logger)
    {
        _meterName = meterName;
        _logger = logger;
        _lastScrape = _startTime;
    }

    /// <inheritdoc />
    public void Start()
    {
        if (_listener is not null)
        {
            return;
        }

        var listener = new MeterListener
        {
            InstrumentPublished = OnInstrumentPublished
        };
        listener.SetMeasurementEventCallback<long>(RecordLong);
        listener.SetMeasurementEventCallback<int>((i, m, t, s) => RecordLong(i, m, t, s));
        listener.SetMeasurementEventCallback<short>((i, m, t, s) => RecordLong(i, m, t, s));
        listener.SetMeasurementEventCallback<byte>((i, m, t, s) => RecordLong(i, m, t, s));
        listener.SetMeasurementEventCallback<double>(RecordDouble);
        listener.SetMeasurementEventCallback<float>((i, m, t, s) => RecordDouble(i, m, t, s));
        listener.SetMeasurementEventCallback<decimal>((i, m, t, s) => RecordDouble(i, (double)m, t, s));
        listener.Start();
        _listener = listener;
    }

    /// <inheritdoc />
    public MetricSnapshot Scrape()
    {
        if (_listener is { } listener)
        {
            try
            {
                listener.RecordObservableInstruments();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Jellytel metrics: RecordObservableInstruments failed.");
            }
        }

        var now = DateTimeOffset.UtcNow;
        var families = new List<MetricFamily>();

        lock (_gate)
        {
            var grouped = new Dictionary<(Instrument Instrument, InstrumentKind Kind), MetricFamily>();

            foreach (var (key, agg) in _numbers)
            {
                if (!_kinds.TryGetValue(key.Instrument, out var kind))
                {
                    continue;
                }

                var family = GetOrCreate(grouped, key.Instrument, kind);
                ((List<NumberPoint>)family.NumberPoints).Add(new NumberPoint(
                    StartTime: _lastScrape,
                    Time: now,
                    Value: agg.Value,
                    Tags: agg.Tags));
            }

            foreach (var (key, agg) in _histograms)
            {
                if (!_kinds.TryGetValue(key.Instrument, out var kind))
                {
                    continue;
                }

                var family = GetOrCreate(grouped, key.Instrument, kind);
                ((List<HistogramPoint>)family.HistogramPoints).Add(new HistogramPoint(
                    StartTime: _lastScrape,
                    Time: now,
                    Count: agg.Count,
                    Sum: agg.Sum,
                    Min: agg.Count > 0 ? agg.Min : null,
                    Max: agg.Count > 0 ? agg.Max : null,
                    ExplicitBounds: agg.Bounds,
                    BucketCounts: (long[])agg.BucketCounts.Clone(),
                    Tags: agg.Tags));

                // Histograms reset per scrape (delta).
                agg.Reset();
            }

            families.AddRange(grouped.Values);
        }

        var snapshot = new MetricSnapshot(_lastScrape, now, families);
        _lastScrape = now;
        return snapshot;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _listener?.Dispose();
        _listener = null;
    }

    private static MetricFamily GetOrCreate(
        Dictionary<(Instrument Instrument, InstrumentKind Kind), MetricFamily> dict,
        Instrument instrument,
        InstrumentKind kind)
    {
        if (dict.TryGetValue((instrument, kind), out var existing))
        {
            return existing;
        }

        var family = new MetricFamily(
            Name: instrument.Name,
            Unit: instrument.Unit ?? string.Empty,
            Description: instrument.Description ?? string.Empty,
            Kind: kind,
            NumberPoints: new List<NumberPoint>(),
            HistogramPoints: new List<HistogramPoint>());
        dict[(instrument, kind)] = family;
        return family;
    }

    private void OnInstrumentPublished(Instrument instrument, MeterListener listener)
    {
        if (!string.Equals(instrument.Meter.Name, _meterName, StringComparison.Ordinal))
        {
            return;
        }

        var kind = ClassifyKind(instrument);
        lock (_gate)
        {
            _kinds[instrument] = kind;
        }

        listener.EnableMeasurementEvents(instrument);
    }

    private static InstrumentKind ClassifyKind(Instrument instrument)
    {
        // System.Diagnostics.Metrics doesn't expose a Kind property; classify
        // by reflecting on the concrete type. We don't need exhaustive support —
        // only the four kinds the panels use.
        var typeName = instrument.GetType().Name;
        if (typeName.StartsWith("Counter", StringComparison.Ordinal))
        {
            return InstrumentKind.Counter;
        }

        if (typeName.StartsWith("UpDownCounter", StringComparison.Ordinal)
            || typeName.StartsWith("ObservableUpDownCounter", StringComparison.Ordinal))
        {
            return InstrumentKind.UpDownCounter;
        }

        if (typeName.StartsWith("ObservableGauge", StringComparison.Ordinal)
            || typeName.StartsWith("Gauge", StringComparison.Ordinal))
        {
            return InstrumentKind.Gauge;
        }

        if (typeName.StartsWith("Histogram", StringComparison.Ordinal))
        {
            return InstrumentKind.Histogram;
        }

        // Default: treat unknown observable counters as cumulative counters.
        return InstrumentKind.Counter;
    }

    private void RecordLong(Instrument instrument, long measurement, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
        => RecordDouble(instrument, measurement, tags, state);

    private void RecordDouble(Instrument instrument, double measurement, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
    {
        if (!_kinds.TryGetValue(instrument, out var kind))
        {
            return;
        }

        var tagArray = MaterializeTags(tags);
        var key = new InstrumentKey(instrument, BuildTagKey(tagArray));

        lock (_gate)
        {
            if (kind == InstrumentKind.Histogram)
            {
                if (!_histograms.TryGetValue(key, out var hist))
                {
                    hist = new HistogramAggregate(DefaultBuckets, tagArray);
                    _histograms[key] = hist;
                }

                hist.Record(measurement);
                return;
            }

            if (!_numbers.TryGetValue(key, out var num))
            {
                num = new NumberAggregate(tagArray);
                _numbers[key] = num;
            }

            if (kind == InstrumentKind.Gauge)
            {
                num.Set(measurement);
            }
            else
            {
                num.Add(measurement);
            }
        }
    }

    private static KeyValuePair<string, object?>[] MaterializeTags(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        if (tags.Length == 0)
        {
            return Array.Empty<KeyValuePair<string, object?>>();
        }

        var arr = new KeyValuePair<string, object?>[tags.Length];
        tags.CopyTo(arr);
        return arr;
    }

    private static string BuildTagKey(KeyValuePair<string, object?>[] tags)
    {
        if (tags.Length == 0)
        {
            return string.Empty;
        }

        // Order-stable join. We don't sort: the panels supply consistent
        // tag ordering for the same instrument, so this is cheap and correct
        // for the cardinality we have.
        var sb = new System.Text.StringBuilder(tags.Length * 16);
        for (var i = 0; i < tags.Length; i++)
        {
            if (i > 0)
            {
                sb.Append(';');
            }

            sb.Append(tags[i].Key).Append('=').Append(tags[i].Value);
        }

        return sb.ToString();
    }

    private readonly record struct InstrumentKey(Instrument Instrument, string TagKey);

    private sealed class NumberAggregate
    {
        public NumberAggregate(KeyValuePair<string, object?>[] tags)
        {
            Tags = tags;
        }

        public double Value { get; private set; }

        public KeyValuePair<string, object?>[] Tags { get; }

        public void Add(double v) => Value += v;

        public void Set(double v) => Value = v;
    }

    private sealed class HistogramAggregate
    {
        public HistogramAggregate(double[] bounds, KeyValuePair<string, object?>[] tags)
        {
            Bounds = bounds;
            BucketCounts = new long[bounds.Length + 1];
            Tags = tags;
            Min = double.PositiveInfinity;
            Max = double.NegativeInfinity;
        }

        public double[] Bounds { get; }

        public long[] BucketCounts { get; }

        public long Count { get; private set; }

        public double Sum { get; private set; }

        public double Min { get; private set; }

        public double Max { get; private set; }

        public KeyValuePair<string, object?>[] Tags { get; }

        public void Record(double value)
        {
            Count++;
            Sum += value;
            if (value < Min)
            {
                Min = value;
            }

            if (value > Max)
            {
                Max = value;
            }

            for (var i = 0; i < Bounds.Length; i++)
            {
                if (value <= Bounds[i])
                {
                    BucketCounts[i]++;
                    return;
                }
            }

            BucketCounts[Bounds.Length]++;
        }

        public void Reset()
        {
            Count = 0;
            Sum = 0;
            Min = double.PositiveInfinity;
            Max = double.NegativeInfinity;
            Array.Clear(BucketCounts, 0, BucketCounts.Length);
        }
    }
}
