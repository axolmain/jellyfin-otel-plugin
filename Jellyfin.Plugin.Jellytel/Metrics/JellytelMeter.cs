using System.Diagnostics.Metrics;
using System.Reflection;

namespace Jellyfin.Plugin.Jellytel.Metrics;

/// <summary>
/// Shared <see cref="System.Diagnostics.Metrics.Meter"/> used by every metric
/// panel. The name is registered with the <c>MeterProvider</c> built by
/// <see cref="MetricsBootstrapper"/> so all instruments created from this
/// meter are exported via a single OTLP connection.
/// </summary>
public static class JellytelMeter
{
    /// <summary>
    /// Meter name. Panels create instruments named <c>jellyfin.*</c> so admins
    /// instrumenting their Jellyfin server can search Grafana by that prefix.
    /// </summary>
    public const string Name = "Jellyfin.Plugin.Jellytel";

    /// <summary>
    /// Singleton meter instance used by all panels.
    /// </summary>
    public static readonly Meter Instance = new(
        Name,
        typeof(JellytelMeter).Assembly.GetName().Version?.ToString() ?? "0.0.0");
}
