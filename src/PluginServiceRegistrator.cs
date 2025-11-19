using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Jellyfin.ABRHls.Services;

namespace Jellyfin.ABRHls;

public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        // WICHTIG: Wir registrieren den Packager und den Watcher
        serviceCollection.AddSingleton<HlsPackager>();
        
        // Der Watcher ist ein Hosted Service (läuft im Hintergrund)
        serviceCollection.AddHostedService<LibraryWatcher>();
    }
}
