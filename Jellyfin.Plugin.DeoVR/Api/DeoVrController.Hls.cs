using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace Jellyfin.Plugin.DeoVR.Api;

/// <summary>
/// HLS stream with AAC audio, produced by Jellyfin's own transcoder (video is copied when possible).
/// The player talks only to /deovr/h/...; the plugin forwards to Jellyfin locally with a
/// plugin-owned API key that never leaves the server.
/// </summary>
public partial class DeoVrController
{
    private const string ApiKeyAppName = "DeoVR plugin (internal)";

    private static readonly SemaphoreSlim ApiKeyLock = new(1, 1);

    private static readonly Regex AllowedHlsPath = new(
        @"^(master\.m3u8|main\.m3u8|hls1/[A-Za-z0-9_-]{1,40}/-?[0-9]{1,7}\.(mp4|m4s|ts))$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ApiKeyParam = new(
        @"(?<=[?&])(api_key|ApiKey)=[^&""\s#]*&?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex AbsoluteVideosPath = new(
        @"(?:https?://[^/""\s]+)?(?:/[^""\s/]+)*?/Videos/[0-9a-fA-F-]{32,36}/",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static string? _apiKey;

    /// <summary>Containers and audio codecs players reliably handle when the original file is sent as is.</summary>
    private static bool OriginalIsSafe(Video video)
    {
        var ext = System.IO.Path.GetExtension(video.Path ?? string.Empty).ToLowerInvariant();
        if (ext is not (".mp4" or ".m4v" or ".mov"))
        {
            return false;
        }

        var audio = video.GetMediaStreams().Where(x => x.Type == MediaStreamType.Audio).ToList();
        var main = audio.FirstOrDefault(x => x.IsDefault) ?? audio.FirstOrDefault();
        return main is null || (main.Codec ?? string.Empty).ToLowerInvariant() is "aac" or "mp3";
    }

    private bool OfferHls(Video video) => (Config.AudioTranscoding ?? "Auto") switch
    {
        "Never" => false,
        "Always" => true,
        _ => !OriginalIsSafe(video)
    };

    /// <summary>
    /// Half side-by-side / half top-bottom 3D: each eye is squeezed to half width / height.
    /// DeoVR does not stretch it back, so the picture looks narrow.
    /// </summary>
    private static bool IsHalf3D(Video video, string stereoMode)
    {
        if (stereoMode == "off")
        {
            return false;
        }

        // Manual override per item: Jellyfin tags "deovr-half" / "deovr-full".
        foreach (var tag in video.Tags ?? Array.Empty<string>())
        {
            switch (tag.Trim().ToLowerInvariant())
            {
                case "deovr-half": return true;
                case "deovr-full": return false;
            }
        }

        if (video.Video3DFormat is Video3DFormat.HalfSideBySide or Video3DFormat.HalfTopAndBottom)
        {
            return true;
        }

        var name = System.IO.Path.GetFileNameWithoutExtension(video.Path ?? string.Empty).ToLowerInvariant();
        if (Regex.IsMatch(name, @"(^|[^a-z])(fsbs|ftab|fou|full[-_ ]?sbs|full[-_ ]?(tab|ou))([^a-z]|$)"))
        {
            return false;
        }

        if (Regex.IsMatch(name, @"(^|[^a-z])(hsbs|htab|hou|half[-_ ]?sbs|half[-_ ]?(tab|ou))([^a-z]|$)"))
        {
            return true;
        }

        // Plain "SBS"/"TB" without half/full: decide by frame size. A full-size side-by-side movie is
        // at least ~3840 wide (two full pictures); 1920x1080 or 1920x804 SBS means half width per eye.
        var vs = video.GetMediaStreams().FirstOrDefault(x => x.Type == MediaStreamType.Video);
        var w = vs?.Width ?? video.Width;
        var h = vs?.Height ?? video.Height;
        if (w <= 0 || h <= 0)
        {
            return false;
        }

        return stereoMode == "tb" ? h <= 1440 : w <= 2560;
    }

    /// <summary>Signed HLS address. "h" = normal, "x" = half 3D expanded to full size.</summary>
    private static string HlsUrl(string baseUrl, Guid itemId, Guid userId, long exp, string kind = "h")
    {
        var sig = LinkSig(kind, itemId, userId, exp);
        return $"{baseUrl}/deovr/{kind}/{itemId:N}/{userId:N}/{exp}/{sig}/master.m3u8";
    }

    [AllowAnonymous]
    [HttpGet("h/{itemId}/{u}/{e}/{sig}/{**path}")]
    [HttpHead("h/{itemId}/{u}/{e}/{sig}/{**path}")]
    public Task<IActionResult> Hls(
        [FromRoute] Guid itemId,
        [FromRoute] Guid u,
        [FromRoute] long e,
        [FromRoute] string sig,
        [FromRoute] string path,
        CancellationToken cancellationToken)
        => ProxyHls("h", itemId, u, e, sig, path, cancellationToken);

    [AllowAnonymous]
    [HttpGet("x/{itemId}/{u}/{e}/{sig}/{**path}")]
    [HttpHead("x/{itemId}/{u}/{e}/{sig}/{**path}")]
    public Task<IActionResult> HlsExpanded3D(
        [FromRoute] Guid itemId,
        [FromRoute] Guid u,
        [FromRoute] long e,
        [FromRoute] string sig,
        [FromRoute] string path,
        CancellationToken cancellationToken)
        => ProxyHls("x", itemId, u, e, sig, path, cancellationToken);

    private async Task<IActionResult> ProxyHls(string kind, Guid itemId, Guid u, long e, string sig, string path, CancellationToken cancellationToken)
    {
        if (!VerifyLink(kind, itemId, u, e, sig))
        {
            Trace(kind + "ls", "403 missing, wrong or expired signature");
            return StatusCode(403);
        }

        path ??= string.Empty;
        if (!AllowedHlsPath.IsMatch(path))
        {
            Trace(kind + "ls", "404 path not allowed");
            return NotFound();
        }

        var user = _userManager.GetUserById(u);
        var item = _libraryManager.GetItemById(itemId);
        if (user is null || !IsUserAllowed(user))
        {
            Trace(kind + "ls", "403 user not allowed");
            return StatusCode(403);
        }

        if (item is not Video video || !item.IsVisibleStandalone(user) || !IsPlayableFile(video))
        {
            Trace(kind + "ls", "404 item not found, not visible or file missing");
            return NotFound();
        }

        var mediaSourceId = video.GetMediaSources(false).FirstOrDefault()?.Id ?? itemId.ToString("N");
        var apiKey = await GetApiKey().ConfigureAwait(false);

        var query = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (path == "master.m3u8")
        {
            // Built here, not taken from the client.
            query["MediaSourceId"] = mediaSourceId;
            query["UserId"] = u.ToString("N");
            query["DeviceId"] = "deovr-" + u.ToString("N");
            query["PlaySessionId"] = Guid.NewGuid().ToString("N");
            query["VideoCodec"] = "h264,hevc,av1";
            query["AudioCodec"] = "aac";
            query["SegmentContainer"] = "mp4";
            query["EnableAutoStreamCopy"] = "true";
            query["AllowVideoStreamCopy"] = "true";
            query["AllowAudioStreamCopy"] = "true";
            query["TranscodingMaxAudioChannels"] = "2";
            query["EnableAdaptiveBitrateStreaming"] = "false";
            query["BreakOnNonKeyFrames"] = "false";

            if (kind == "x")
            {
                // Scale the squeezed half-3D picture back to full size (e.g. 1920x1080 HSBS -> 3840x1080).
                var vs = video.GetMediaStreams().FirstOrDefault(x => x.Type == MediaStreamType.Video);
                var w = vs?.Width ?? 1920;
                var h = vs?.Height ?? 1080;
                var topBottom = video.Video3DFormat == Video3DFormat.HalfTopAndBottom
                    || DetectProjection(video).StereoMode == "tb";
                var (tw, th) = topBottom ? (w, h * 2) : (w * 2, h);
                query["VideoCodec"] = "h264,hevc";
                query["EnableAutoStreamCopy"] = "false";
                query["AllowVideoStreamCopy"] = "false";
                query["Width"] = tw.ToString(System.Globalization.CultureInfo.InvariantCulture);
                query["Height"] = th.ToString(System.Globalization.CultureInfo.InvariantCulture);
                query["MaxWidth"] = query["Width"];
                query["MaxHeight"] = query["Height"];
                query["VideoBitRate"] = "25000000";
            }
        }
        else
        {
            // Follow-up requests carry the parameters Jellyfin put into its own playlists.
            foreach (var (key, value) in Request.Query)
            {
                query[key] = value.ToString();
            }

            query["MediaSourceId"] = mediaSourceId;
        }

        query.Remove("api_key");
        query.Remove("ApiKey");

        var target = QueryHelpers.AddQueryString($"{InternalBaseUrl()}/Videos/{itemId:N}/{path}", query);

        using var upstream = new HttpRequestMessage(Request.Method == "HEAD" ? HttpMethod.Head : HttpMethod.Get, target);
        // Jellyfin 12 only accepts the legacy api_key query parameter with legacy authorization enabled.
        upstream.Headers.TryAddWithoutValidation("Authorization", $"MediaBrowser Token=\"{apiKey}\"");
        if (Request.Headers.TryGetValue("Range", out var range))
        {
            upstream.Headers.TryAddWithoutValidation("Range", range.ToString());
        }

        var client = _httpClientFactory.CreateClient(NamedClient.Default);
        using var response = await client.SendAsync(upstream, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            Trace(kind + "ls " + path.Split('/')[0], $"Jellyfin answered {(int)response.StatusCode}");
            _logger.LogWarning("DeoVR: HLS request {Path} for {Item} failed with {Status}", path, itemId, (int)response.StatusCode);
            return StatusCode((int)response.StatusCode);
        }

        var contentType = response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";

        if (path.EndsWith(".m3u8", StringComparison.Ordinal))
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            text = AbsoluteVideosPath.Replace(text, string.Empty);
            text = ApiKeyParam.Replace(text, string.Empty);
            text = text.Replace("?&", "?", StringComparison.Ordinal)
                .Replace("&\r\n", "\r\n", StringComparison.Ordinal)
                .Replace("&\n", "\n", StringComparison.Ordinal)
                .Replace("&\"", "\"", StringComparison.Ordinal)
                .Replace("?\r\n", "\r\n", StringComparison.Ordinal)
                .Replace("?\n", "\n", StringComparison.Ordinal)
                .Replace("?\"", "\"", StringComparison.Ordinal);
            Trace(kind + "ls " + path, "playlist sent");
            Response.Headers["Cache-Control"] = "no-store";
            return Content(text, "application/vnd.apple.mpegurl", Encoding.UTF8);
        }

        Response.StatusCode = (int)response.StatusCode;
        Response.ContentType = contentType;
        if (response.Content.Headers.ContentLength is long length)
        {
            Response.ContentLength = length;
        }

        if (response.Content.Headers.ContentRange is not null)
        {
            Response.Headers["Content-Range"] = response.Content.Headers.ContentRange.ToString();
        }

        if (Request.Method != "HEAD")
        {
            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await body.CopyToAsync(Response.Body, cancellationToken).ConfigureAwait(false);
        }

        return new EmptyResult();
    }

    /// <summary>Local address of this Jellyfin server (the plugin calls itself).</summary>
    private string InternalBaseUrl()
    {
        if (!string.IsNullOrWhiteSpace(Config.InternalJellyfinUrl))
        {
            return Config.InternalJellyfinUrl.Trim().TrimEnd('/');
        }

        var net = _serverConfigurationManager.GetNetworkConfiguration();
        var baseUrl = (net.BaseUrl ?? string.Empty).Trim().TrimEnd('/');
        if (baseUrl.Length > 0 && !baseUrl.StartsWith('/'))
        {
            baseUrl = "/" + baseUrl;
        }

        return $"http://127.0.0.1:{net.InternalHttpPort}{baseUrl}";
    }

    /// <summary>API key owned by the plugin, created once in Jellyfin (Dashboard → API keys).</summary>
    private async Task<string> GetApiKey()
    {
        if (_apiKey is not null)
        {
            return _apiKey;
        }

        await ApiKeyLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_apiKey is not null)
            {
                return _apiKey;
            }

            var key = (await _authenticationManager.GetApiKeys().ConfigureAwait(false))
                .FirstOrDefault(k => k.AppName == ApiKeyAppName);
            if (key is null)
            {
                await _authenticationManager.CreateApiKey(ApiKeyAppName).ConfigureAwait(false);
                key = (await _authenticationManager.GetApiKeys().ConfigureAwait(false))
                    .First(k => k.AppName == ApiKeyAppName);
            }

            _apiKey = key.AccessToken;
            return _apiKey;
        }
        finally
        {
            ApiKeyLock.Release();
        }
    }
}
