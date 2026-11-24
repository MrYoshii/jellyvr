using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.DeoVR.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Public base URL, e.g. https://jellyfin.example.com (empty = derived from request).</summary>
    public string PublicBaseUrl { get; set; } = string.Empty;

    /// <summary>Jellyfin user used for requests without login (empty = login/feed key required).</summary>
    public string AnonymousUserName { get; set; } = string.Empty;

    /// <summary>Lifetime of signed video/stream links in hours.</summary>
    public int LinkLifetimeHours { get; set; } = 24;

    /// <summary>Max items per library tab (newest first).</summary>
    public int MaxItemsPerLibrary { get; set; } = 1000;

    /// <summary>Show an extra "3D" tab with all stereoscopic items.</summary>
    public bool Show3DScene { get; set; } = true;

    /// <summary>
    /// Extra stream with AAC audio via Jellyfin's transcoder: "Auto" (when the original container or audio
    /// cannot be played by DeoVR, e.g. MKV or EAC3/DTS/TrueHD), "Always" or "Never".
    /// </summary>
    public string AudioTranscoding { get; set; } = "Auto";

    /// <summary>Local Jellyfin address the plugin uses for transcoding (empty = http://127.0.0.1:&lt;port&gt;).</summary>
    public string InternalJellyfinUrl { get; set; } = string.Empty;

    /// <summary>"List": all episodes in one tab, sorted by series/season/episode. "Off": no TV shows.</summary>
    public string SeriesView { get; set; } = "List";

    /// <summary>Offer half side-by-side / top-bottom 3D scaled to full size (needs video transcoding).</summary>
    public bool ExpandHalf3D { get; set; } = true;

    /// <summary>Address of this server inside the LAN, e.g. http://192.168.1.10:8096 (empty = off).</summary>
    public string LanBaseUrl { get; set; } = string.Empty;

    /// <summary>"Auto": LAN address for clients in the local network; "Always"; "Off".</summary>
    public string LanMode { get; set; } = "Auto";

    /// <summary>Extra client addresses or networks (CIDR) treated as local, e.g. your home's public IP.</summary>
    public string LanExtraNetworks { get; set; } = string.Empty;

    /// <summary>HMAC secret for links. Changing it invalidates all feed links.</summary>
    public string SigningSecret { get; set; } = string.Empty;
}
