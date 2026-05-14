using Jellyfin.Plugin.Jellytel.Logs;
using Jellyfin.Plugin.Jellytel.Metrics;
using Jellyfin.Plugin.Jellytel.Metrics.Panels;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Jellytel;

/// <summary>
/// Registers plugin services with the host DI container.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc/>
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddHostedService<LogsBootstrapper>();
        serviceCollection.AddHostedService<MetricsBootstrapper>();
        serviceCollection.AddSingleton<IMetricPanel, SessionMetrics>();
    }
}
