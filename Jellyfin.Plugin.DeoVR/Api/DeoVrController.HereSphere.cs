using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.DeoVR.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.DeoVR.Api;

/// <summary>
/// HereSphere endpoints (HereSphere JSON API, version 1). Same library, login and streams as the
/// DeoVR endpoints, only the JSON format differs.
///   GET|POST /heresphere           -> library (login via HereSphere username/password)
///   GET|POST /heresphere/{id}?u&amp;e&amp;s -> single video JSON (signed)
/// </summary>
public partial class DeoVrController
{
    private void HereSphereHeader() => Response.Headers["HereSphere-JSON-Version"] = "1";

    [AllowAnonymous]
    [HttpHead("/heresphere")]
    public IActionResult HereSphereProbe()
    {
        HereSphereHeader();
        Trace("heresphere probe", "ok");
        return Json(new HereSphereLibrary { Access = 0 });
    }

    [AllowAnonymous]
    [HttpGet("/heresphere")]
    [HttpPost("/heresphere")]
    public async Task<IActionResult> GetHereSphereLibrary()
    {
        HereSphereHeader();
        var (userId, state) = await AuthenticateFromRequest().ConfigureAwait(false);
        if (userId is null)
        {
            Trace("heresphere library", state == "-1" ? "login failed" : "login required");
            return Json(new HereSphereLibrary
            {
                Access = state == "-1" ? -1 : 0,
                Library = new List<HereSphereList> { new() { Name = state == "-1" ? "Login failed" : "Login required" } }
            });
        }

        var feed = BuildFeed(userId.Value);
        var lib = new HereSphereLibrary { Access = feed.Authorized == "1" ? 1 : -1 };
        foreach (var scene in feed.Scenes)
        {
            lib.Library.Add(new HereSphereList
            {
                Name = scene.Name,
                // Same signed links as the DeoVR list, served by the HereSphere route.
                List = scene.List.Select(x => x.VideoUrl.Replace("/deovr/v/", "/heresphere/", StringComparison.Ordinal)).ToList()
            });
        }

        Trace("heresphere library", $"library sent: {lib.Library.Count} lists, {lib.Library.Sum(x => x.List.Count)} videos, access {lib.Access}");
        return Json(lib);
    }

    [AllowAnonymous]
    [HttpGet("/heresphere/{itemId:guid}")]
    [HttpPost("/heresphere/{itemId:guid}")]
    [HttpHead("/heresphere/{itemId:guid}")]
    public async Task<IActionResult> GetHereSphereVideo([FromRoute] Guid itemId, [FromQuery] Guid? u, [FromQuery] long? e, [FromQuery] string? s)
    {
        HereSphereHeader();
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
                Trace("heresphere video", "no valid link or login");
                return Json(new HereSphereVideo
                {
                    Access = state == "-1" ? -1 : 0,
                    Title = "Link expired or login required - reload the library"
                });
            }
        }

        var user = _userManager.GetUserById(userId.Value);
        var item = _libraryManager.GetItemById(itemId);
        if (user is null || !IsUserAllowed(user))
        {
            Trace("heresphere video", "403 user not allowed");
            return StatusCode(403);
        }

        if (item is not Video video || !item.IsVisibleStandalone(user) || !IsPlayableFile(video))
        {
            Trace("heresphere video", "404 item not found, not visible or file missing");
            return NotFound();
        }

        var (screenType, stereoMode) = DetectProjection(video);
        var (projection, fov, lens) = screenType switch
        {
            "dome" => ("equirectangular", 180.0, "Linear"),
            "sphere" => ("equirectangular360", 360.0, "Linear"),
            "fisheye" => ("fisheye", 190.0, "Linear"),
            "mkx200" => ("fisheye", 200.0, "MKX200"),
            _ => ("perspective", 180.0, "Linear")
        };
        var stereo = stereoMode == "off" ? "mono" : stereoMode;

        var videoStream = video.GetMediaStreams().FirstOrDefault(x => x.Type == MediaStreamType.Video);
        var width = videoStream?.Width ?? item.Width;
        var height = videoStream?.Height ?? item.Height;

        var result = new HereSphereVideo
        {
            Title = BuildTitle(item),
            Description = item.Overview ?? string.Empty,
            ThumbnailImage = ThumbUrl(GetBaseUrl(), item),
            DateReleased = (item.PremiereDate ?? item.DateCreated).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            DateAdded = item.DateCreated.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            Duration = (long)RuntimeSeconds(item) * 1000,
            Projection = projection,
            Stereo = stereo,
            Fov = fov,
            Lens = lens
        };

        foreach (var g in item.Genres ?? Array.Empty<string>())
        {
            result.Tags.Add(new HereSphereTag { Name = "Genre:" + g });
        }

        var expiry = StreamExpiry(item);
        var (baseUrl, viaLan) = StreamBaseUrl();
        if (OfferHls(video))
        {
            result.Media.Add(new HereSphereMedia
            {
                Name = "Jellyfin (AAC audio)",
                Sources = new List<HereSphereSource>
                {
                    new() { Resolution = height, Width = width, Height = height, Url = HlsUrl(baseUrl, item.Id, userId.Value, expiry) }
                }
            });
        }

        result.Media.Add(new HereSphereMedia
        {
            Name = "Original file",
            Sources = new List<HereSphereSource>
            {
                new()
                {
                    Resolution = height,
                    Width = width,
                    Height = height,
                    Size = item.Size ?? 0,
                    Url = SignedUrl(baseUrl, "s", item.Id, userId.Value, expiry, "/video" + StreamExtension(video))
                }
            }
        });

        Trace("heresphere video", $"video sent: {projection}/{stereo}, {result.Media[0].Name}, {height}p, streams via {(viaLan ? "LAN address" : "public address")}");
        return Json(result);
    }
}
