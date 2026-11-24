using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.DeoVR.Models;

/// <summary>HereSphere library: access 1 = ok, 0 = login required, -1 = login failed.</summary>
public sealed class HereSphereLibrary
{
    [JsonPropertyName("access")]
    public int Access { get; set; } = 1;

    [JsonPropertyName("library")]
    public List<HereSphereList> Library { get; set; } = new();
}

public sealed class HereSphereList
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Addresses of the video JSONs.</summary>
    [JsonPropertyName("list")]
    public List<string> List { get; set; } = new();
}

public sealed class HereSphereVideo
{
    [JsonPropertyName("access")]
    public int Access { get; set; } = 1;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("thumbnailImage")]
    public string ThumbnailImage { get; set; } = string.Empty;

    [JsonPropertyName("dateReleased")]
    public string DateReleased { get; set; } = string.Empty;

    [JsonPropertyName("dateAdded")]
    public string DateAdded { get; set; } = string.Empty;

    /// <summary>Duration in milliseconds.</summary>
    [JsonPropertyName("duration")]
    public long Duration { get; set; }

    [JsonPropertyName("isFavorite")]
    public bool IsFavorite { get; set; }

    /// <summary>perspective (flat screen), equirectangular (180°), equirectangular360, fisheye.</summary>
    [JsonPropertyName("projection")]
    public string Projection { get; set; } = "perspective";

    /// <summary>mono, sbs or tb.</summary>
    [JsonPropertyName("stereo")]
    public string Stereo { get; set; } = "mono";

    [JsonPropertyName("fov")]
    public double Fov { get; set; } = 180;

    [JsonPropertyName("lens")]
    public string Lens { get; set; } = "Linear";

    [JsonPropertyName("tags")]
    public List<HereSphereTag> Tags { get; set; } = new();

    [JsonPropertyName("media")]
    public List<HereSphereMedia> Media { get; set; } = new();

    [JsonPropertyName("writeFavorite")]
    public bool WriteFavorite { get; set; }

    [JsonPropertyName("writeRating")]
    public bool WriteRating { get; set; }

    [JsonPropertyName("writeTags")]
    public bool WriteTags { get; set; }

    [JsonPropertyName("writeHSP")]
    public bool WriteHsp { get; set; }
}

public sealed class HereSphereTag
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
}

public sealed class HereSphereMedia
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("sources")]
    public List<HereSphereSource> Sources { get; set; } = new();
}

public sealed class HereSphereSource
{
    [JsonPropertyName("resolution")]
    public int Resolution { get; set; }

    [JsonPropertyName("height")]
    public int Height { get; set; }

    [JsonPropertyName("width")]
    public int Width { get; set; }

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;
}
