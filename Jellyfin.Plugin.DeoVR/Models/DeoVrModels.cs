using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.DeoVR.Models;

public sealed class DeoVrFeed
{
    [JsonPropertyName("scenes")]
    public List<DeoVrScene> Scenes { get; set; } = new();

    [JsonPropertyName("authorized")]
    public string Authorized { get; set; } = "1";
}

public sealed class DeoVrScene
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("list")]
    public List<DeoVrListEntry> List { get; set; } = new();
}

/// <summary>Shortened list format: DeoVR loads the full video JSON from video_url on selection.</summary>
public sealed class DeoVrListEntry
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("videoLength")]
    public int VideoLength { get; set; }

    [JsonPropertyName("thumbnailUrl")]
    public string ThumbnailUrl { get; set; } = string.Empty;

    [JsonPropertyName("video_url")]
    public string VideoUrl { get; set; } = string.Empty;
}

public sealed class DeoVrVideo
{
    [JsonPropertyName("encodings")]
    public List<DeoVrEncoding> Encodings { get; set; } = new();

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("videoLength")]
    public int VideoLength { get; set; }

    [JsonPropertyName("is3d")]
    public bool Is3d { get; set; }

    [JsonPropertyName("screenType")]
    public string ScreenType { get; set; } = "flat";

    [JsonPropertyName("stereoMode")]
    public string StereoMode { get; set; } = "off";

    [JsonPropertyName("thumbnailUrl")]
    public string ThumbnailUrl { get; set; } = string.Empty;

    [JsonPropertyName("authorized")]
    public string Authorized { get; set; } = "1";

    [JsonPropertyName("fullAccess")]
    public bool FullAccess { get; set; } = true;

    [JsonPropertyName("fullVideoReady")]
    public bool FullVideoReady { get; set; } = true;
}

public sealed class DeoVrEncoding
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "h264";

    [JsonPropertyName("videoSources")]
    public List<DeoVrVideoSource> VideoSources { get; set; } = new();
}

public sealed class DeoVrVideoSource
{
    [JsonPropertyName("resolution")]
    public int Resolution { get; set; }

    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;
}
