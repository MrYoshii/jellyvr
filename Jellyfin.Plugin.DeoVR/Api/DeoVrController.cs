using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.DeoVR.Configuration;
using Jellyfin.Plugin.DeoVR.Models;
using MediaBrowser.Common.Api;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Security;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DeoVR.Api;

/// <summary>
/// DeoVR endpoints.
///   GET|POST /deovr                 -> library (login via DeoVR POST login/password, or anonymous user)
///   GET|POST /deovr/f/{key}         -> library via personal feed key
///   GET|POST /deovr/v/{id}?u&amp;e&amp;s  -> single video JSON (signed)
///   GET|HEAD /deovr/s/{id}/video.ext?u&amp;e&amp;s -> direct file stream with range support (signed)
///   GET|POST /deovr/login           -> placeholder video shown while not logged in
///   GET      /deovr/img/login.png   -> thumbnail of that placeholder
///   GET      /deovr/admin/links     -> feed links per user (admin only)
///   GET      /deovr/admin/log       -> recent requests, for troubleshooting (admin only)
/// </summary>
[ApiController]
[Route("deovr")]
public partial class DeoVrController : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    private static readonly char[] TokenSplit = { '_', '.', '-', ' ', '[', ']', '(', ')' };

    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly INetworkManager _networkManager;
    private readonly ILogger<DeoVrController> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IAuthenticationManager _authenticationManager;
    private readonly IServerConfigurationManager _serverConfigurationManager;

    /// <summary>What kind of credentials the current request carried (for the request log; never the values).</summary>
    private string _credentials = "none";

    public DeoVrController(
        ILibraryManager libraryManager,
        IUserManager userManager,
        INetworkManager networkManager,
        IHttpClientFactory httpClientFactory,
        IAuthenticationManager authenticationManager,
        IServerConfigurationManager serverConfigurationManager,
        ILogger<DeoVrController> logger)
    {
        _httpClientFactory = httpClientFactory;
        _authenticationManager = authenticationManager;
        _serverConfigurationManager = serverConfigurationManager;
        _networkManager = networkManager;
        _libraryManager = libraryManager;
        _userManager = userManager;
        _logger = logger;
    }

    private static PluginConfiguration Config => Plugin.Instance!.Configuration;

    // ------------------------------------------------------------------ library

    /// <summary>Answers HEAD probes of the library addresses.</summary>
    [AllowAnonymous]
    [HttpHead]
    [HttpHead("f/{key}")]
    public IActionResult Probe()
    {
        Trace("probe", "ok");
        return Json(PlaceholderFeed("0"));
    }

    [AllowAnonymous]
    [HttpGet]
    [HttpPost]
    public async Task<IActionResult> GetLibrary()
    {
        var (userId, state) = await AuthenticateFromRequest().ConfigureAwait(false);
        if (userId is null)
        {
            Trace("library", state == "-1" ? "login failed (placeholder sent)" : "login required (placeholder sent)");
            return Json(PlaceholderFeed(state));
        }

        var feed = BuildFeed(userId.Value);
        Trace("library", Describe(feed));
        return Json(feed);
    }

    [AllowAnonymous]
    [HttpGet("f/{key}")]
    [HttpPost("f/{key}")]
    public IActionResult GetLibraryByKey([FromRoute] string key)
    {
        var user = _userManager.GetUsers().FirstOrDefault(u => FixedEquals(FeedKey(u.Id), key));
        if (user is null)
        {
            Trace("library by feed key", "unknown feed key (placeholder sent)");
            return Json(PlaceholderFeed("-1", "Unknown feed link", "Unknown feed link - copy it again from the Jellyfin dashboard (Plugins > DeoVR)"));
        }

        var feed = BuildFeed(user.Id);
        Trace("library by feed key", Describe(feed));
        return Json(feed);
    }

    /// <summary>
    /// Placeholder shown to DeoVR while not logged in. DeoVR only opens its video list when the
    /// answer contains at least one video, so the placeholder is a single "login required" entry.
    /// authorized: "0" = no login sent yet, "-1" = login failed.
    /// </summary>
    private DeoVrFeed PlaceholderFeed(string state, string? tab = null, string? title = null)
    {
        var baseUrl = GetBaseUrl();
        var failed = state == "-1";
        return new DeoVrFeed
        {
            Authorized = state,
            Scenes = new List<DeoVrScene>
            {
                new()
                {
                    Name = tab ?? (failed ? "Login failed" : "Login required"),
                    List = new List<DeoVrListEntry>
                    {
                        new()
                        {
                            Title = title ?? (failed
                                ? "Login failed - check your Jellyfin user name and password"
                                : "Login required - use LOGIN in DeoVR with your Jellyfin user name and password"),
                            VideoLength = 0,
                            ThumbnailUrl = baseUrl + "/deovr/img/login.png",
                            VideoUrl = baseUrl + "/deovr/login"
                        }
                    }
                }
            }
        };
    }

    /// <summary>Video answer for the placeholder entry and for requests without a valid login or link.</summary>
    private DeoVrVideo PlaceholderVideo(string state, string title) => new()
    {
        Title = title,
        Authorized = state,
        FullAccess = false,
        FullVideoReady = false,
        ThumbnailUrl = GetBaseUrl() + "/deovr/img/login.png"
    };

    [AllowAnonymous]
    [HttpGet("login")]
    [HttpPost("login")]
    [HttpHead("login")]
    public IActionResult GetLoginPlaceholder()
    {
        Trace("login placeholder", "ok");
        return Json(PlaceholderVideo("0", "Login required - use LOGIN in DeoVR with your Jellyfin user name and password"));
    }

    [AllowAnonymous]
    [HttpGet("img/login.png")]
    [HttpHead("img/login.png")]
    public IActionResult GetLoginImage()
    {
        var stream = typeof(Plugin).Assembly.GetManifestResourceStream("Jellyfin.Plugin.DeoVR.Resources.login.png");
        if (stream is null)
        {
            return NotFound();
        }

        Response.Headers["Cache-Control"] = "public, max-age=86400";
        return File(stream, "image/png");
    }

    private static string Describe(DeoVrFeed feed)
        => $"feed sent: {feed.Scenes.Count} tabs, {feed.Scenes.Sum(x => x.List.Count)} videos, authorized {feed.Authorized}";

    // ------------------------------------------------------------------ single video

    [AllowAnonymous]
    [HttpGet("v/{itemId}")]
    [HttpPost("v/{itemId}")]
    [HttpHead("v/{itemId}")]
    public async Task<IActionResult> GetVideo([FromRoute] Guid itemId, [FromQuery] Guid? u, [FromQuery] long? e, [FromQuery] string? s)
    {
        Guid? userId;
        if (u.HasValue && e.HasValue && !string.IsNullOrEmpty(s) && VerifyLink("v", itemId, u.Value, e.Value, s))
        {
            userId = u.Value;
        }
        else
        {
            string state;
            (userId, state) = await AuthenticateFromRequest().ConfigureAwait(false);
            if (userId is null)
            {
                Trace("video", "no valid link or login (placeholder sent)");
                return Json(PlaceholderVideo(state, "Link expired or login required - reload the library"));
            }
        }

        var user = _userManager.GetUserById(userId.Value);
        var item = _libraryManager.GetItemById(itemId);
        if (user is null || !IsUserAllowed(user))
        {
            Trace("video", "403 user not allowed");
            return StatusCode(403);
        }

        if (item is not Video video || !item.IsVisibleStandalone(user) || !IsPlayableFile(video))
        {
            Trace("video", "404 item not found, not visible or file missing");
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
            ThumbnailUrl = ThumbUrl(baseUrl, item)
        };

        var height = videoStream?.Height ?? item.Height;
        var expiry = StreamExpiry(item);
        var (streamBase, viaLan) = StreamBaseUrl();
        baseUrl = streamBase;
        if (OfferHls(video))
        {
            result.Encodings.Add(new DeoVrEncoding
            {
                Name = "Jellyfin (AAC audio)",
                VideoSources = new List<DeoVrVideoSource>
                {
                    new() { Resolution = height, Url = HlsUrl(baseUrl, item.Id, userId.Value, expiry) }
                }
            });
        }

        result.Encodings.Add(new DeoVrEncoding
        {
            Name = OfferHls(video) ? "Original file" : NormalizeCodec(videoStream?.Codec),
            VideoSources = new List<DeoVrVideoSource>
            {
                new() { Resolution = height, Url = SignedUrl(baseUrl, "s", item.Id, userId.Value, expiry, "/video" + StreamExtension(video)) }
            }
        });

        Trace("video", $"video sent: {screenType}/{stereoMode}, {result.Encodings[0].Name}, {result.Encodings[0].VideoSources[0].Resolution}p, streams via {(viaLan ? "LAN address" : "public address")}");
        return Json(result);
    }

    // ------------------------------------------------------------------ stream

    [AllowAnonymous]
    [HttpGet("s/{itemId}")]
    [HttpHead("s/{itemId}")]
    [HttpGet("s/{itemId}/{fileName}")]
    [HttpHead("s/{itemId}/{fileName}")]
    public IActionResult Stream([FromRoute] Guid itemId, [FromQuery] Guid? u, [FromQuery] long? e, [FromQuery] string? s)
    {
        if (!u.HasValue || !e.HasValue || string.IsNullOrEmpty(s) || !VerifyLink("s", itemId, u.Value, e.Value, s))
        {
            Trace("stream", "403 missing, wrong or expired signature");
            return StatusCode(403);
        }

        var user = _userManager.GetUserById(u.Value);
        var item = _libraryManager.GetItemById(itemId);
        if (user is null || !IsUserAllowed(user))
        {
            Trace("stream", "403 user not allowed");
            return StatusCode(403);
        }

        if (item is not Video video || !item.IsVisibleStandalone(user) || !IsPlayableFile(video))
        {
            Trace("stream", "404 item not found, not visible or file missing");
            return NotFound();
        }

        var mime = MimeTypes.GetMimeType(video.Path) ?? "application/octet-stream";
        Trace("stream", "file sent as " + mime);
        return PhysicalFile(video.Path, mime, enableRangeProcessing: true);
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

    [HttpGet("admin/log")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public IActionResult GetRequestLog() => Json(RequestLog.Snapshot());

    /// <summary>Anything else below /deovr: answer 404, but record it so unexpected requests are visible.</summary>
    [AllowAnonymous]
    [ApiExplorerSettings(IgnoreApi = true)]
    [AcceptVerbs("GET", "POST", "HEAD", "PUT", "DELETE", "PATCH", "OPTIONS", Route = "{**rest}", Order = 1000)]
    public IActionResult NotHandled()
    {
        var parts = (Request.Path.Value ?? string.Empty).Split('/', StringSplitOptions.RemoveEmptyEntries);
        var first = parts.Length > 1 ? Clip(parts[1], 20) : string.Empty;
        Trace("unknown: /" + first + (parts.Length > 2 ? "/..." : string.Empty), "404");
        return NotFound();
    }

    // ------------------------------------------------------------------ feed building

    private DeoVrFeed BuildFeed(Guid userId)
    {
        var feed = new DeoVrFeed();
        var user = _userManager.GetUserById(userId);
        if (user is null || !IsUserAllowed(user))
        {
            return PlaceholderFeed(
                "-1",
                "Access denied",
                "Access denied - user is disabled, may not play media, is outside the access schedule or may not connect remotely");
        }

        var baseUrl = GetBaseUrl();
        var max = Math.Clamp(Config.MaxItemsPerLibrary, 1, 5000);
        var all3d = new List<Video>();
        var seen = new HashSet<Guid>();

        // Work per request is bounded by `max`: sorting and limiting happen in the library query,
        // and no per-file filesystem access is done while building the feed.
        foreach (var view in _libraryManager.GetUserRootFolder().GetChildren(user, true).OfType<Folder>())
        {
            List<Video> episodes = new();
            List<Video> videos;
            try
            {
                if (Config.ShowTvShows)
                {
                    // DeoVR has no folders: all episodes in one tab, in series / season / episode order.
                    var epQuery = VideoQuery(user, max);
                    epQuery.IncludeItemTypes = new[] { BaseItemKind.Episode };
                    epQuery.OrderBy = new[]
                    {
                        (ItemSortBy.SeriesSortName, SortOrder.Ascending),
                        (ItemSortBy.ParentIndexNumber, SortOrder.Ascending),
                        (ItemSortBy.IndexNumber, SortOrder.Ascending)
                    };
                    episodes = view.GetItemList(epQuery).OfType<Video>().Where(IsListable).ToList();
                }

                // Everything else (movies, videos): one tab per library, newest first.
                var query = VideoQuery(user, max);
                query.IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Video, BaseItemKind.MusicVideo };
                query.OrderBy = new[] { (ItemSortBy.DateCreated, SortOrder.Descending) };
                videos = view.GetItemList(query).OfType<Video>().Where(IsListable).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "DeoVR: failed to query library {Name}", view.Name);
                continue;
            }

            foreach (var v in episodes.Concat(videos))
            {
                if (DetectProjection(v).StereoMode != "off" && seen.Add(v.Id))
                {
                    all3d.Add(v);
                }
            }

            if (episodes.Count > 0)
            {
                // One tab per library, sorted by series, season and episode (one tab per series gets unusable).
                feed.Scenes.Add(new DeoVrScene
                {
                    Name = view.Name,
                    List = episodes.Select(ep => ToListEntry(baseUrl, ep, userId)).ToList()
                });
            }

            if (videos.Count > 0)
            {
                feed.Scenes.Add(new DeoVrScene
                {
                    Name = episodes.Count > 0 ? view.Name + " – Videos" : view.Name,
                    List = videos.Select(v => ToListEntry(baseUrl, v, userId)).ToList()
                });
            }
        }

        if (Config.Show3DScene)
        {
            try
            {
                // Items Jellyfin itself flags as 3D (bounded), in addition to the ones detected above.
                var query = VideoQuery(user, max);
                query.Is3D = true;
                query.OrderBy = new[] { (ItemSortBy.SortName, SortOrder.Ascending) };
                foreach (var v in _libraryManager.GetItemList(query).OfType<Video>().Where(IsListable))
                {
                    if (v.IsVisibleStandalone(user) && seen.Add(v.Id))
                    {
                        all3d.Add(v);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "DeoVR: failed to query 3D items");
            }

            if (all3d.Count > 0)
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
        }

        return feed;
    }

    private DeoVrListEntry ToListEntry(string baseUrl, BaseItem item, Guid userId, bool inSeriesTab = false) => new()
    {
        Title = BuildTitle(item, inSeriesTab),
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

    /// <summary>
    /// Returns the authenticated user and the DeoVR auth state:
    /// "1" = ok, "0" = no credentials sent, "-1" = login failed.
    /// </summary>
    private async Task<(Guid? UserId, string State)> AuthenticateFromRequest()
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
                _credentials = string.IsNullOrEmpty(login) ? "form body without login" : "login sent (form)";
            }
            else if (Request.ContentLength is > 0 and <= 8192)
            {
                // Players do not always label the body; accept JSON or URL-encoded fields either way.
                using var reader = new StreamReader(Request.Body, Encoding.UTF8);
                var text = (await reader.ReadToEndAsync().ConfigureAwait(false)).Trim();
                if (text.StartsWith('{'))
                {
                    using var doc = JsonDocument.Parse(text);
                    if (doc.RootElement.TryGetProperty("login", out var l)) login = l.GetString();
                    if (doc.RootElement.TryGetProperty("password", out var p)) password = p.GetString();
                    _credentials = string.IsNullOrEmpty(login) ? "json body without login" : "login sent (json)";
                }
                else
                {
                    var fields = QueryHelpers.ParseQuery(text);
                    if (fields.TryGetValue("login", out var fl)) login = fl.ToString();
                    if (fields.TryGetValue("password", out var fp)) password = fp.ToString();
                    _credentials = string.IsNullOrEmpty(login) ? "body without login" : "login sent (unlabelled body)";
                }
            }
            else if (Request.ContentLength is > 8192)
            {
                _credentials = "body too large (ignored)";
            }
        }
        catch (Exception ex)
        {
            _credentials = "unreadable body";
            _logger.LogDebug(ex, "DeoVR: could not parse request body");
        }

        // Only noted for troubleshooting; credentials in the URL or in other headers are never used.
        if (Request.Query.ContainsKey("login"))
        {
            _credentials += ", login in query string (ignored)";
        }

        if (Request.Headers.ContainsKey("Authorization"))
        {
            _credentials += ", Authorization header (ignored)";
        }

        if (!string.IsNullOrEmpty(login))
        {
            try
            {
                var remote = HttpContext.Connection.RemoteIpAddress?.ToString() ?? string.Empty;
                var user = await _userManager.AuthenticateUser(login, password ?? string.Empty, remote, false).ConfigureAwait(false);
                if (user is not null)
                {
                    return (user.Id, "1");
                }
            }
            catch (Exception ex)
            {
                _logger.LogInformation("DeoVR: login failed for {Login}: {Message}", login, ex.Message);
            }

            return (null, "-1");
        }

        if (!string.IsNullOrWhiteSpace(Config.AnonymousUserName))
        {
            var anon = _userManager.GetUserByName(Config.AnonymousUserName.Trim());
            if (anon is not null)
            {
                return (anon.Id, "1");
            }
        }

        return (null, "0");
    }

    private static byte[] Hmac(string data)
    {
        Plugin.Instance!.EnsureSigningSecret();
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
        => DateTimeOffset.UtcNow.AddHours(Math.Clamp(Config.LinkLifetimeHours, 1, 24 * 30)).ToUnixTimeSeconds();

    /// <summary>
    /// Expiry for stream links: runtime + 2 h (room for pauses and seeking).
    /// Falls back to the configured link lifetime if the runtime is unknown.
    /// </summary>
    private static long StreamExpiry(BaseItem item)
    {
        var runtime = Math.Min(RuntimeSeconds(item), 24 * 3600);
        return runtime > 0
            ? DateTimeOffset.UtcNow.AddSeconds(runtime).AddHours(2).ToUnixTimeSeconds()
            : ListExpiry();
    }

    private static string SignedUrl(string baseUrl, string purpose, Guid itemId, Guid userId, long exp, string suffix = "")
    {
        var sig = LinkSig(purpose, itemId, userId, exp);
        return $"{baseUrl}/deovr/{purpose}/{itemId:N}{suffix}?u={userId:N}&e={exp}&s={sig}";
    }

    /// <summary>File extension for the stream address, so players can tell the container from the URL.</summary>
    private static string StreamExtension(Video video)
    {
        var ext = Path.GetExtension(video.Path ?? string.Empty).ToLowerInvariant();
        if (ext.Length < 2 || ext.Length > 6)
        {
            return ".mp4";
        }

        for (var i = 1; i < ext.Length; i++)
        {
            if (!char.IsAsciiLetterOrDigit(ext[i]))
            {
                return ".mp4";
            }
        }

        return ext;
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// Enforce the Jellyfin user policy the normal login would enforce:
    /// disabled users, "allow media playback", parental access schedule and "allow remote access".
    /// </summary>
    private bool IsUserAllowed(User user)
    {
        if (user.HasPermission(PermissionKind.IsDisabled) || !user.HasPermission(PermissionKind.EnableMediaPlayback))
        {
            return false;
        }

        // Parental access schedule, as enforced by Jellyfin's login and per-request authorization.
        if (!user.HasPermission(PermissionKind.IsAdministrator) && !user.IsParentalScheduleAllowed())
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

    /// <summary>
    /// Address for the video streams: the LAN address for clients in the local network
    /// (or always, depending on the setting), otherwise the normal base URL.
    /// </summary>
    private (string Url, bool ViaLan) StreamBaseUrl()
    {
        var lan = (Config.LanBaseUrl ?? string.Empty).Trim().TrimEnd('/');
        if (lan.Length == 0 || Config.LanMode == "Off")
        {
            return (GetBaseUrl(), false);
        }

        if (Config.LanMode == "Always" || IsLanClient())
        {
            return (lan, true);
        }

        return (GetBaseUrl(), false);
    }

    private bool IsLanClient()
    {
        var ip = HttpContext.Connection.RemoteIpAddress;
        if (ip is null)
        {
            return false;
        }

        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        if (_networkManager.IsInLocalNetwork(ip))
        {
            return true;
        }

        foreach (var entry in (Config.LanExtraNetworks ?? string.Empty).Split(new[] { ',', ';', ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (entry.Contains('/', StringComparison.Ordinal))
            {
                if (System.Net.IPNetwork.TryParse(entry, out var net) && net.Contains(ip))
                {
                    return true;
                }
            }
            else if (System.Net.IPAddress.TryParse(entry, out var single) && single.Equals(ip))
            {
                return true;
            }
        }

        return false;
    }

    private string GetBaseUrl()
    {
        if (!string.IsNullOrWhiteSpace(Config.PublicBaseUrl))
        {
            return Config.PublicBaseUrl.Trim().TrimEnd('/');
        }

        // Behind a reverse proxy that is not listed under "Known proxies", Request.Scheme is "http"
        // although the client used https. X-Forwarded-Proto only changes links in this same answer.
        var scheme = Request.Scheme;
        var forwarded = Request.Headers["X-Forwarded-Proto"].ToString().Split(',')[0].Trim().ToLowerInvariant();
        if (forwarded is "https" or "http")
        {
            scheme = forwarded;
        }

        return $"{scheme}://{Request.Host}{Request.PathBase}".TrimEnd('/');
    }

    /// <summary>Records the current request in the in-memory request log (no keys, signatures or passwords).</summary>
    private void Trace(string route, string result)
    {
        var forwarded = Request.Headers["X-Forwarded-Proto"].ToString();
        RequestLog.Add(new RequestLogEntry
        {
            Time = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " UTC",
            Method = Request.Method,
            Route = route,
            Scheme = string.IsNullOrEmpty(forwarded) ? Request.Scheme : $"{Request.Scheme} (forwarded: {Clip(forwarded, 10)})",
            Client = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "?",
            UserAgent = Clip(Request.Headers["User-Agent"].ToString(), 120),
            ContentType = Clip(Request.ContentType ?? string.Empty, 60),
            Credentials = _credentials,
            Range = Clip(Request.Headers["Range"].ToString(), 40),
            Result = result
        });
    }

    private static string Clip(string value, int max) => value.Length > max ? value.Substring(0, max) : value;

    private static InternalItemsQuery VideoQuery(User user, int limit) => new(user)
    {
        Recursive = true,
        IsFolder = false,
        IsVirtualItem = false,
        IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Episode, BaseItemKind.Video, BaseItemKind.MusicVideo },
        VideoTypes = new[] { VideoType.VideoFile },
        Limit = limit
    };

    /// <summary>Cheap check for feed entries (no filesystem access). The file itself is checked on playback.</summary>
    private static bool IsListable(Video v)
        => v.VideoType == VideoType.VideoFile
           && !string.IsNullOrEmpty(v.Path)
           && !v.Path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase);

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

    private static string BuildTitle(BaseItem item, bool inSeriesTab = false)
    {
        if (item is Episode ep)
        {
            var s = ep.ParentIndexNumber?.ToString("00", CultureInfo.InvariantCulture) ?? "00";
            var e = ep.IndexNumber?.ToString("00", CultureInfo.InvariantCulture) ?? "00";
            return inSeriesTab ? $"S{s}E{e} – {ep.Name}" : $"{ep.SeriesName} – S{s}E{e} – {ep.Name}";
        }

        return item.ProductionYear.HasValue ? $"{item.Name} ({item.ProductionYear})" : item.Name;
    }

    private ContentResult Json(object value)
        => Content(JsonSerializer.Serialize(value, value.GetType(), JsonOpts), "application/json; charset=utf-8");
}
