using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Jellyfin.ABRHls.Services;

namespace Jellyfin.ABRHls;

public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public override string Name => "ABR HLS Cinema";
    public override Guid Id { get; } = Guid.Parse("b91b3f1d-6c74-4f2e-9e9d-6a27b9a2f3d1");

    public static Plugin? Instance { get; private set; }

    public Plugin(IApplicationPaths appPaths, IXmlSerializer xmlSerializer) : base(appPaths, xmlSerializer) 
    {
        Instance = this;
    }

    public IEnumerable<PluginPageInfo> GetPages()
    {
        // Wir nutzen den exakten Namespace der Klasse, um die Ressourcen zu finden
        var prefix = GetType().Namespace;

        yield return new PluginPageInfo 
        { 
            Name = "abr-player.html", 
            EmbeddedResourcePath = $"{prefix}.web.abr-player.html" 
        };
        yield return new PluginPageInfo 
        { 
            Name = "abr-player.js", 
            EmbeddedResourcePath = $"{prefix}.web.abr-player.js" 
        };
        yield return new PluginPageInfo 
        { 
            Name = "configPage.html", 
            EmbeddedResourcePath = $"{prefix}.Configuration.configPage.html" 
        };
    }
}
