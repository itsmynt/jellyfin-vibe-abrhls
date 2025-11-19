using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Jellyfin.ABRHls.Services;

namespace Jellyfin.ABRHls;

public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        // Packager als Singleton (wird überall verwendet)
        serviceCollection.AddSingleton<HlsPackager>();
        
        // Watcher als Hosted Service (läuft im Hintergrund mit)
        serviceCollection.AddHostedService<LibraryWatcher>();
    }
}
