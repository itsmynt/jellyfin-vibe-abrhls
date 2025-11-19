using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Jellyfin.ABRHls.Services;

namespace Jellyfin.ABRHls;

public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        // Wir registrieren alles sauber für Dependency Injection
        serviceCollection.AddSingleton<HlsPackager>();
        serviceCollection.AddHostedService<LibraryWatcher>();
    }
}
