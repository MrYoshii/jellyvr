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

    /// <summary>HMAC secret for links. Changing it invalidates all feed links.</summary>
    public string SigningSecret { get; set; } = string.Empty;
}
