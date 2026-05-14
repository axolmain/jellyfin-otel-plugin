namespace Jellyfin.Plugin.Jellytel.Metrics.Export;

/// <summary>
/// Subset of <c>System.Diagnostics.Metrics</c> instrument kinds we surface
/// through the exporter abstraction. Maps onto OTLP aggregation choices but
/// is wire-format agnostic.
/// </summary>
public enum InstrumentKind
{
    /// <summary>Monotonic cumulative counter (Counter&lt;T&gt;).</summary>
    Counter,

    /// <summary>Non-monotonic cumulative counter (UpDownCounter&lt;T&gt;).</summary>
    UpDownCounter,

    /// <summary>Last-value observation (ObservableGauge&lt;T&gt;).</summary>
    Gauge,

    /// <summary>Histogram of recorded values (Histogram&lt;T&gt;).</summary>
    Histogram,
}
