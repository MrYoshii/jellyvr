using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using Jellyfin.Plugin.DeoVR.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.DeoVR;

/// <summary>
/// DeoVR plugin: serves a DeoVR-compatible JSON library (2D/3D cinema screen + VR).
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public const string PluginGuid = "9a2a70e4-4266-4a9e-b694-ea6036068502";

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;

        if (string.IsNullOrWhiteSpace(Configuration.SigningSecret))
        {
            Configuration.SigningSecret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            SaveConfiguration();
        }
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "DeoVR";

    public override string Description => "Watch Jellyfin in DeoVR: 2D and 3D (SBS/TB) movies on a cinema screen, VR180/360 videos.";

    public override Guid Id => Guid.Parse(PluginGuid);

    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html"
            }
        };
    }
}
