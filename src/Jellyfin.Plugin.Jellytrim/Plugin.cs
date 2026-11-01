using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Jellytrim;

/// <summary>Plugin entry point. Settings live in the jellytrim database rather than the XML config, because they need revisions.</summary>
public sealed class Plugin : BasePlugin<BasePluginConfiguration>, IHasWebPages
{
    public const string PluginGuid = "80b15e17-71cd-42e6-8c50-b077e7c8f0b7";

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
    }

    public override string Name => "jellytrim";

    public override Guid Id => Guid.Parse(PluginGuid);

    public override string Description => "Keep what matters. Reclaim the rest.";

    public IEnumerable<PluginPageInfo> GetPages() =>
    [
        new PluginPageInfo
        {
            Name = "jellytrim",
            DisplayName = "jellytrim",
            EmbeddedResourcePath = "Jellytrim.Web.jellytrim.html",
            EnableInMainMenu = true,
            MenuIcon = "cleaning_services",
        },
        new PluginPageInfo
        {
            Name = "jellytrim.js",
            EmbeddedResourcePath = "Jellytrim.Web.jellytrim.js",
        },
    ];
}
