using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization; // <--- Das hier hat gefehlt!

namespace Jellyfin.ABRHls;

public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public override string Name => "ABR HLS Cinema";
    public override Guid Id { get; } = Guid.Parse("b91b3f1d-6c74-4f2e-9e9d-6a27b9a2f3d1");

    // IXmlSerializer wird hier benutzt, braucht oben das using
    public Plugin(IApplicationPaths appPaths, IXmlSerializer xmlSerializer) : base(appPaths, xmlSerializer) { }

    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return new PluginPageInfo 
        { 
            Name = "abr-player.html", 
            EmbeddedResourcePath = GetType().Namespace + ".web.abr-player.html" 
        };
        
        yield return new PluginPageInfo 
        { 
            Name = "abr-player.js", 
            EmbeddedResourcePath = GetType().Namespace + ".web.abr-player.js" 
        };
    }
}
