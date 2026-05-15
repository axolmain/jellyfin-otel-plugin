using System.Diagnostics;

namespace Jellyfin.Plugin.Jellytel.Traces;

/// <summary>
/// Shared <see cref="System.Diagnostics.ActivitySource"/> for spans produced
/// by the plugin itself. The trace collector always listens to this source
/// in addition to the user-configured allowlist; new in-plugin instrumentation
/// should create activities here rather than introducing additional sources.
/// </summary>
public static class JellytelActivitySource
{
    /// <summary>
    /// Source name. Matches <see cref="Metrics.JellytelMeter.Name"/> so a
    /// single service-wide search prefix surfaces both signals.
    /// </summary>
    public const string Name = "Jellyfin.Plugin.Jellytel";

    /// <summary>
    /// Singleton activity source used by all plugin-side instrumentation.
    /// </summary>
    public static readonly ActivitySource Instance = new(
        Name,
        typeof(JellytelActivitySource).Assembly.GetName().Version?.ToString() ?? "0.0.0");
}
