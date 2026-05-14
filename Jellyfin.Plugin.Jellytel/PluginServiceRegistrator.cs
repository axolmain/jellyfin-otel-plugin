using Jellyfin.Plugin.Jellytel.LocalBuffer;
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
        serviceCollection.AddSingleton<ExportStatusTracker>();
        serviceCollection.AddSingleton<LocalBufferBootstrapper>();

        serviceCollection.AddHostedService<LogsBootstrapper>();
        serviceCollection.AddHostedService<MetricsBootstrapper>();
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<LocalBufferBootstrapper>());

        serviceCollection.AddSingleton<IMetricPanel, SessionMetrics>();
    }
}
