using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.DeoVR.Configuration;
using Jellyfin.Plugin.DeoVR.Models;
using MediaBrowser.Common.Api;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DeoVR.Api;

/// <summary>
/// DeoVR endpoints.
///   GET|POST /deovr                 -> library (login via DeoVR POST login/password, or anonymous user)
///   GET|POST /deovr/f/{key}         -> library via personal feed key
///   GET|POST /deovr/v/{id}?u&amp;e&amp;s  -> single video JSON (signed)
///   GET|HEAD /deovr/s/{id}?u&amp;e&amp;s  -> direct file stream with range support (signed)
///   GET      /deovr/admin/links     -> feed links per user (admin only)
/// </summary>
[ApiController]
[Route("deovr")]
public class DeoVrController : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    private static readonly char[] TokenSplit = { '_', '.', '-', ' ', '[', ']', '(', ')' };

    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly INetworkManager _networkManager;
    private readonly ILogger<DeoVrController> _logger;

    public DeoVrController(ILibraryManager libraryManager, IUserManager userManager, INetworkManager networkManager, ILogger<DeoVrController> logger)
    {
        _networkManager = networkManager;
        _libraryManager = libraryManager;
        _userManager = userManager;
        _logger = logger;
    }

    private static PluginConfiguration Config => Plugin.Instance!.Configuration;

    // ------------------------------------------------------------------ library

    [AllowAnonymous]
    [HttpGet]
    [HttpPost]
    public async Task<IActionResult> GetLibrary()
    {
        Guid? userId = await AuthenticateFromRequest().ConfigureAwait(false);
        if (userId is null)
        {
            return Json(new DeoVrFeed { Authorized = "-1" });
        }

        return Json(BuildFeed(userId.Value));
    }

    [AllowAnonymous]
    [HttpGet("f/{key}")]
    [HttpPost("f/{key}")]
    public IActionResult GetLibraryByKey([FromRoute] string key)
    {
        var user = _userManager.GetUsers().FirstOrDefault(u => FixedEquals(FeedKey(u.Id), key));
        if (user is null)
        {
            return Json(new DeoVrFeed { Authorized = "-1" });
        }

        return Json(BuildFeed(user.Id));
    }

    // ------------------------------------------------------------------ single video

    [AllowAnonymous]
    [HttpGet("v/{itemId}")]
    [HttpPost("v/{itemId}")]
    public async Task<IActionResult> GetVideo([FromRoute] Guid itemId, [FromQuery] Guid? u, [FromQuery] long? e, [FromQuery] string? s)
    {
        Guid? userId = null;
        if (u.HasValue && e.HasValue && !string.IsNullOrEmpty(s) && VerifyLink("v", itemId, u.Value, e.Value, s))
        {
            userId = u.Value;
        }
        else
        {
            userId = await AuthenticateFromRequest().ConfigureAwait(false);
        }

        if (userId is null)
        {
            return StatusCode(403);
        }

        var user = _userManager.GetUserById(userId.Value);
        var item = _libraryManager.GetItemById(itemId);
        if (user is null || !IsUserAllowed(user))
        {
            return StatusCode(403);
        }

        if (item is not Video video || !item.IsVisible(user) || !IsPlayableFile(video))
        {
            return NotFound();
        }

        var baseUrl = GetBaseUrl();
        var (screenType, stereoMode) = DetectProjection(video);
        var videoStream = video.GetMediaStreams().FirstOrDefault(x => x.Type == MediaStreamType.Video);

        var result = new DeoVrVideo
        {
            Title = BuildTitle(item),
            Id = StableIntId(item.Id),
            VideoLength = RuntimeSeconds(item),
            ScreenType = screenType,
            StereoMode = stereoMode,
            Is3d = stereoMode != "off",
            ThumbnailUrl = ThumbUrl(baseUrl, item),
            Encodings = new List<DeoVrEncoding>
            {
                new()
                {
                    Name = NormalizeCodec(videoStream?.Codec),
                    VideoSources = new List<DeoVrVideoSource>
                    {
                        new()
                        {
                            Resolution = videoStream?.Height ?? item.Height,
                            Url = SignedUrl(baseUrl, "s", item.Id, userId.Value, StreamExpiry(item))
                        }
                    }
                }
            }
        };

        return Json(result);
    }

    // ------------------------------------------------------------------ stream

    [AllowAnonymous]
    [HttpGet("s/{itemId}")]
    [HttpHead("s/{itemId}")]
    public IActionResult Stream([FromRoute] Guid itemId, [FromQuery] Guid u, [FromQuery] long e, [FromQuery] string s)
    {
        if (!VerifyLink("s", itemId, u, e, s))
        {
            return StatusCode(403);
        }

        var user = _userManager.GetUserById(u);
        var item = _libraryManager.GetItemById(itemId);
        if (user is null || !IsUserAllowed(user))
        {
            return StatusCode(403);
        }

        if (item is not Video video || !item.IsVisible(user) || !IsPlayableFile(video))
        {
            return NotFound();
        }

        return PhysicalFile(video.Path, MimeTypes.GetMimeType(video.Path) ?? "application/octet-stream", enableRangeProcessing: true);
    }

    // ------------------------------------------------------------------ admin

    [HttpGet("admin/links")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public IActionResult GetFeedLinks()
    {
        var baseUrl = GetBaseUrl();
        var links = _userManager.GetUsers()
            .Select(u => new { user = u.Username, url = $"{baseUrl}/deovr/f/{FeedKey(u.Id)}" })
            .OrderBy(x => x.user, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return Json(links);
    }

    // ------------------------------------------------------------------ feed building

    private DeoVrFeed BuildFeed(Guid userId)
    {
        var feed = new DeoVrFeed();
        var user = _userManager.GetUserById(userId);
        if (user is null || !IsUserAllowed(user))
        {
            feed.Authorized = "-1";
            return feed;
        }

        var baseUrl = GetBaseUrl();
        var max = Math.Max(1, Config.MaxItemsPerLibrary);
        var all3d = new List<BaseItem>();
        var seen = new HashSet<Guid>();

        foreach (var view in _libraryManager.GetUserRootFolder().GetChildren(user, true).OfType<Folder>())
        {
            List<Video> videos;
            try
            {
                videos = view.GetItemList(new InternalItemsQuery(user)
                    {
                        Recursive = true,
                        IsFolder = false,
                        IsVirtualItem = false
                    })
                    .OfType<Video>()
                    .Where(IsPlayableFile)
                    .OrderByDescending(v => v.DateCreated)
                    .ToList();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "DeoVR: failed to query library {Name}", view.Name);
                continue;
            }

            if (videos.Count == 0)
            {
                continue;
            }

            foreach (var v in videos)
            {
                if (DetectProjection(v).StereoMode != "off" && seen.Add(v.Id))
                {
                    all3d.Add(v);
                }
            }

            feed.Scenes.Add(new DeoVrScene
            {
                Name = view.Name,
                List = videos.Take(max).Select(v => ToListEntry(baseUrl, v, userId)).ToList()
            });
        }

        if (Config.Show3DScene && all3d.Count > 0)
        {
            feed.Scenes.Insert(0, new DeoVrScene
            {
                Name = "3D",
                List = all3d.OrderBy(v => v.SortName ?? v.Name, StringComparer.OrdinalIgnoreCase)
                    .Take(max)
                    .Select(v => ToListEntry(baseUrl, v, userId))
                    .ToList()
            });
        }

        return feed;
    }

    private DeoVrListEntry ToListEntry(string baseUrl, BaseItem item, Guid userId) => new()
    {
        Title = BuildTitle(item),
        VideoLength = RuntimeSeconds(item),
        ThumbnailUrl = ThumbUrl(baseUrl, item),
        VideoUrl = SignedUrl(baseUrl, "v", item.Id, userId, ListExpiry())
    };

    // ------------------------------------------------------------------ projection detection

    /// <summary>
    /// Priority: item tags (deovr-*) &gt; Jellyfin Video3DFormat &gt; filename tokens.
    /// 2D movie  -> flat/off (cinema screen)
    /// 3D SBS    -> flat/sbs (3D cinema screen)
    /// 3D TB     -> flat/tb
    /// VR        -> dome|sphere|fisheye|mkx200 + sbs|tb|off
    /// </summary>
    internal static (string ScreenType, string StereoMode) DetectProjection(Video video)
    {
        string? screen = null;
        string? stereo = null;

        foreach (var tag in video.Tags ?? Array.Empty<string>())
        {
            switch (tag.Trim().ToLowerInvariant())
            {
                case "deovr-flat": screen = "flat"; break;
                case "deovr-180": screen = "dome"; break;
                case "deovr-360": screen = "sphere"; break;
                case "deovr-fisheye": screen = "fisheye"; break;
                case "deovr-mkx200": screen = "mkx200"; break;
                case "deovr-sbs": stereo = "sbs"; break;
                case "deovr-tb": stereo = "tb"; break;
                case "deovr-2d": stereo = "off"; break;
            }
        }

        if (stereo is null && video.Video3DFormat.HasValue)
        {
            stereo = video.Video3DFormat.Value switch
            {
                Video3DFormat.HalfSideBySide or Video3DFormat.FullSideBySide => "sbs",
                Video3DFormat.HalfTopAndBottom or Video3DFormat.FullTopAndBottom => "tb",
                _ => null // MVC: not supported by DeoVR -> plays as 2D
            };
        }

        var tokens = new HashSet<string>(
            Path.GetFileNameWithoutExtension(video.Path ?? string.Empty)
                .ToLowerInvariant()
                .Split(TokenSplit, StringSplitOptions.RemoveEmptyEntries));

        if (screen is null)
        {
            // plain "180"/"360" only counts together with a VR hint, so "Movie 180 (2020)" stays flat
            var vrHint = tokens.Overlaps(new[] { "vr", "lr", "sbs", "tb", "ou", "3dh", "3dv", "mono" });
            if (tokens.Contains("vr360") || (tokens.Contains("360") && vrHint)) screen = "sphere";
            else if (tokens.Contains("vr180") || (tokens.Contains("180") && vrHint)) screen = "dome";
            else if (tokens.Contains("fisheye") || tokens.Contains("fisheye190")) screen = "fisheye";
            else if (tokens.Contains("mkx200")) screen = "mkx200";
            else screen = "flat";
        }

        if (stereo is null)
        {
            if (tokens.Overlaps(new[] { "sbs", "hsbs", "fsbs", "3dh" })) stereo = "sbs";
            else if (tokens.Overlaps(new[] { "tab", "htab", "ftab", "3dv" })) stereo = "tb";
            else if (screen != "flat" && tokens.Overlaps(new[] { "lr", "rl" })) stereo = "sbs";
            else if (screen != "flat" && tokens.Overlaps(new[] { "tb", "ou", "bt" })) stereo = "tb";
            else if (screen is "dome" or "fisheye" or "mkx200") stereo = "sbs"; // 180° VR is almost always SBS
            else stereo = "off";
        }

        return (screen, stereo);
    }

    // ------------------------------------------------------------------ auth / signing

    private async Task<Guid?> AuthenticateFromRequest()
    {
        string? login = null;
        string? password = null;

        try
        {
            if (Request.HasFormContentType)
            {
                var form = await Request.ReadFormAsync().ConfigureAwait(false);
                login = form["login"].FirstOrDefault();
                password = form["password"].FirstOrDefault();
            }
            else if (Request.ContentLength is > 0 && (Request.ContentType ?? string.Empty).Contains("json", StringComparison.OrdinalIgnoreCase))
            {
                using var doc = await JsonDocument.ParseAsync(Request.Body).ConfigureAwait(false);
                if (doc.RootElement.TryGetProperty("login", out var l)) login = l.GetString();
                if (doc.RootElement.TryGetProperty("password", out var p)) password = p.GetString();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "DeoVR: could not parse request body");
        }

        if (!string.IsNullOrEmpty(login))
        {
            try
            {
                var remote = HttpContext.Connection.RemoteIpAddress?.ToString() ?? string.Empty;
                var user = await _userManager.AuthenticateUser(login, password ?? string.Empty, remote, false).ConfigureAwait(false);
                if (user is not null)
                {
                    return user.Id;
                }
            }
            catch (Exception ex)
            {
                _logger.LogInformation("DeoVR: login failed for {Login}: {Message}", login, ex.Message);
            }

            return null;
        }

        if (!string.IsNullOrWhiteSpace(Config.AnonymousUserName))
        {
            var anon = _userManager.GetUserByName(Config.AnonymousUserName.Trim());
            if (anon is not null)
            {
                return anon.Id;
            }
        }

        return null;
    }

    private static byte[] Hmac(string data)
    {
        using var h = new HMACSHA256(Encoding.UTF8.GetBytes(Config.SigningSecret));
        return h.ComputeHash(Encoding.UTF8.GetBytes(data));
    }

    private static string FeedKey(Guid userId)
        => Convert.ToHexString(Hmac("feed|" + userId.ToString("N"))).Substring(0, 20).ToLowerInvariant();

    private static string LinkSig(string purpose, Guid itemId, Guid userId, long exp)
        => Convert.ToHexString(Hmac($"{purpose}|{itemId:N}|{userId:N}|{exp}")).ToLowerInvariant();

    private static bool VerifyLink(string purpose, Guid itemId, Guid userId, long exp, string sig)
    {
        if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > exp)
        {
            return false;
        }

        return FixedEquals(LinkSig(purpose, itemId, userId, exp), sig);
    }

    private static bool FixedEquals(string a, string b)
        => CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(a ?? string.Empty),
            Encoding.ASCII.GetBytes((b ?? string.Empty).ToLowerInvariant()));

    /// <summary>Expiry for list entries (video JSON links): configured link lifetime.</summary>
    private static long ListExpiry()
        => DateTimeOffset.UtcNow.AddHours(Math.Max(1, Config.LinkLifetimeHours)).ToUnixTimeSeconds();

    /// <summary>
    /// Expiry for stream links: runtime + 2 h (room for pauses and seeking).
    /// Falls back to the configured link lifetime if the runtime is unknown.
    /// </summary>
    private static long StreamExpiry(BaseItem item)
    {
        var runtime = RuntimeSeconds(item);
        return runtime > 0
            ? DateTimeOffset.UtcNow.AddSeconds(runtime).AddHours(2).ToUnixTimeSeconds()
            : ListExpiry();
    }

    private static string SignedUrl(string baseUrl, string purpose, Guid itemId, Guid userId, long exp)
    {
        var sig = LinkSig(purpose, itemId, userId, exp);
        return $"{baseUrl}/deovr/{purpose}/{itemId:N}?u={userId:N}&e={exp}&s={sig}";
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// Enforce the Jellyfin user policy the normal login would enforce:
    /// disabled users, "allow media playback" and "allow remote access".
    /// </summary>
    private bool IsUserAllowed(User user)
    {
        if (user.HasPermission(PermissionKind.IsDisabled) || !user.HasPermission(PermissionKind.EnableMediaPlayback))
        {
            return false;
        }

        if (user.HasPermission(PermissionKind.EnableRemoteAccess))
        {
            return true;
        }

        var ip = HttpContext.Connection.RemoteIpAddress;
        return ip is not null && _networkManager.IsInLocalNetwork(ip);
    }

    private string GetBaseUrl()
    {
        if (!string.IsNullOrWhiteSpace(Config.PublicBaseUrl))
        {
            return Config.PublicBaseUrl.Trim().TrimEnd('/');
        }

        return $"{Request.Scheme}://{Request.Host}{Request.PathBase}".TrimEnd('/');
    }

    private static bool IsPlayableFile(Video v)
        => v.VideoType == VideoType.VideoFile
           && !string.IsNullOrEmpty(v.Path)
           && !v.Path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase)
           && System.IO.File.Exists(v.Path);

    private static string ThumbUrl(string baseUrl, BaseItem item)
        => $"{baseUrl}/Items/{item.Id:N}/Images/Primary?maxWidth=480&quality=85";

    private static int RuntimeSeconds(BaseItem item)
        => item.RunTimeTicks.HasValue ? (int)TimeSpan.FromTicks(item.RunTimeTicks.Value).TotalSeconds : 0;

    private static int StableIntId(Guid id)
        => BitConverter.ToInt32(id.ToByteArray(), 0) & 0x7FFFFFFF;

    private static string NormalizeCodec(string? codec) => (codec ?? "h264").ToLowerInvariant() switch
    {
        "hevc" or "h265" => "h265",
        "av1" => "av1",
        "vp9" => "vp9",
        _ => "h264"
    };

    private static string BuildTitle(BaseItem item)
    {
        if (item is Episode ep)
        {
            var s = ep.ParentIndexNumber?.ToString("00", CultureInfo.InvariantCulture) ?? "00";
            var e = ep.IndexNumber?.ToString("00", CultureInfo.InvariantCulture) ?? "00";
            return $"{ep.SeriesName} – S{s}E{e} – {ep.Name}";
        }

        return item.ProductionYear.HasValue ? $"{item.Name} ({item.ProductionYear})" : item.Name;
    }

    private ContentResult Json(object value)
        => Content(JsonSerializer.Serialize(value, value.GetType(), JsonOpts), "application/json; charset=utf-8");
}
