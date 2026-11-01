using Jellyfin.Plugin.Jellytrim.Adapter;
using Jellytrim.Core.Abstractions;
using Jellytrim.Core.Cleanup;
using Jellytrim.Core.FileSystem;
using Jellytrim.Core.Inventory;
using Jellytrim.Core.Persistence;
using Jellytrim.Core.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Jellytrim.Runtime;

public sealed class ServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection services, IServerApplicationHost applicationHost)
    {
        services.AddSingleton(sp =>
        {
            // In the server's data directory rather than the versioned plugin folder, so upgrades keep it and it is in server backups.
            var paths = sp.GetRequiredService<IApplicationPaths>();
            return new JellytrimDb(Path.Combine(paths.DataPath, "jellytrim", "jellytrim.db"), sp.GetRequiredService<ILogger<JellytrimDb>>());
        });
        services.AddSingleton<IMediaHost, JellyfinMediaHost>();
        services.AddSingleton<IFileProbe, NativeFileProbe>();
        services.AddSingleton<IFileDeleter, FileDeleter>();
        services.AddSingleton<IVolumeProbe, NativeVolumeProbe>();
        services.AddSingleton(_ => new RootChecker());
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<MaintenanceGate>();
        services.AddSingleton<SettingsStore>();
        services.AddSingleton<AuditStore>();
        services.AddSingleton<PolicyStore>();
        services.AddSingleton<InventoryStore>();
        services.AddSingleton<ExplorerStore>();
        services.AddSingleton<CleanupStore>();
        services.AddSingleton<PolicyEvaluationService>();
        services.AddSingleton<InventoryScanner>();
        services.AddSingleton<CleanupPlanner>();
        services.AddSingleton<CleanupCoordinator>();
        services.AddSingleton<JellytrimRuntime>();
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<JellytrimRuntime>());
    }
}
