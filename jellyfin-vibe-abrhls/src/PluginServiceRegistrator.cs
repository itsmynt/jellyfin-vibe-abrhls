using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Tasks; // Für Scheduled Tasks
using Microsoft.Extensions.DependencyInjection;
using Jellyfin.ABRHls.Services; // Namespace beachten

namespace Jellyfin.ABRHls;

public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        // Registriert den Packager (der die Videos konvertiert)
        serviceCollection.AddSingleton<HlsPackager>();

        // Registriert den Wächter (der neue Filme bemerkt)
        // WICHTIG: Als IHostedService registrieren, damit er automatisch startet!
        serviceCollection.AddHostedService<LibraryWatcher>();
    }
}