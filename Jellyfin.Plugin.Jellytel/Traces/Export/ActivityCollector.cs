using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Threading;
using System.Threading.Channels;
using Jellyfin.Plugin.Jellytel.Metrics;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Jellytel.Traces.Export;

/// <summary>
/// Subscribes to an allowlist of <c>ActivitySource</c>s via a single
/// <see cref="ActivityListener"/> and buffers each completed
/// <see cref="Activity"/> into a bounded channel. The export loop drains
/// the channel on its cadence; if the buffer fills before the next drain,
/// the oldest activities are dropped and the drop count surfaces on the
/// metrics pipeline via <c>jellytel.traces.dropped</c>.
/// </summary>
public sealed class ActivityCollector : IActivityCollector
{
    private const int ChannelCapacity = 10_000;

    private static readonly Counter<long> SpansDropped = JellytelMeter.Instance.CreateCounter<long>(
        "jellytel.traces.dropped",
        unit: "{span}",
        description: "Activities dropped because the trace export buffer was full.");

    private readonly IReadOnlySet<string> _sources;
    private readonly ILogger<ActivityCollector> _logger;
    private readonly Channel<Activity> _buffer;

    private ActivityListener? _listener;
    private bool _disposed;
    private int _approximateDepth;

    /// <summary>
    /// Initializes a new instance of the <see cref="ActivityCollector"/> class.
    /// </summary>
    /// <param name="sourceNames">Set of <c>ActivitySource</c> names to listen to. The plugin's own source is included by the bootstrapper.</param>
    /// <param name="logger">Diagnostic logger.</param>
    public ActivityCollector(IReadOnlySet<string> sourceNames, ILogger<ActivityCollector> logger)
    {
        _sources = sourceNames;
        _logger = logger;
        _buffer = Channel.CreateBounded<Activity>(new BoundedChannelOptions(ChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    /// <inheritdoc />
    public void Start()
    {
        if (_listener is not null || _disposed)
        {
            return;
        }

        _listener = new ActivityListener
        {
            ShouldListenTo = source => _sources.Contains(source.Name),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = OnActivityStopped,
        };

        ActivitySource.AddActivityListener(_listener);
        _logger.LogInformation(
            "Jellytel traces: listening on {Count} source(s): {Sources}",
            _sources.Count,
            string.Join(", ", _sources));
    }

    /// <inheritdoc />
    public IReadOnlyList<Activity> Drain()
    {
        var list = new List<Activity>();
        while (_buffer.Reader.TryRead(out var activity))
        {
            list.Add(activity);
        }

        if (list.Count > 0)
        {
            Interlocked.Add(ref _approximateDepth, -list.Count);
        }

        return list;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _listener?.Dispose();
        _listener = null;
        _buffer.Writer.TryComplete();
    }

    private void OnActivityStopped(Activity activity)
    {
        // BoundedChannelFullMode.DropOldest evicts silently — TryWrite returns
        // true even when an older item was dropped to make room. We track an
        // approximate depth ourselves so we can surface the drop count: if the
        // depth has reached capacity at write time, the next write is going to
        // evict, so we count it. Accept small skew vs. exact-count instead of
        // synchronizing the listener.
        var depth = Interlocked.Increment(ref _approximateDepth);
        if (depth > ChannelCapacity)
        {
            SpansDropped.Add(1);
            Interlocked.Decrement(ref _approximateDepth);
        }

        if (!_buffer.Writer.TryWrite(activity))
        {
            // Channel completed (disposing). Undo the depth bump.
            Interlocked.Decrement(ref _approximateDepth);
        }
    }
}
