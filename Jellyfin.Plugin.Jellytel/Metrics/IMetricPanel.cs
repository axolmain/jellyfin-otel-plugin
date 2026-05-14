using System;

namespace Jellyfin.Plugin.Jellytel.Metrics;

/// <summary>
/// One mirror of a Jellyfin dashboard panel as OpenTelemetry instruments.
/// Implementations register their instruments and any event subscriptions in
/// <see cref="Register"/>, and release subscriptions in <see cref="IDisposable.Dispose"/>.
/// Instruments themselves live on the shared <see cref="JellytelMeter.Instance"/>
/// and do not need explicit disposal.
/// </summary>
public interface IMetricPanel : IDisposable
{
    /// <summary>
    /// Gets the configuration flag name that gates this panel (e.g.
    /// <c>EnableSessionMetrics</c>). Used by <see cref="MetricsBootstrapper"/>
    /// for diagnostic logging.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Gets a value indicating whether the panel is currently enabled by the
    /// plugin configuration. The bootstrapper consults this before calling
    /// <see cref="Register"/>.
    /// </summary>
    /// <param name="config">Current plugin configuration.</param>
    /// <returns><c>true</c> if the panel should be registered.</returns>
    bool IsEnabled(Configuration.PluginConfiguration config);

    /// <summary>
    /// Subscribes to host events and creates instruments. Called once per
    /// MeterProvider lifetime, on the host's startup thread.
    /// </summary>
    void Register();
}
