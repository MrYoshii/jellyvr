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

    private static string HlsUrl(string baseUrl, Guid itemId, Guid userId, long exp)
    {
        var sig = LinkSig("h", itemId, userId, exp);
        return $"{baseUrl}/deovr/h/{itemId:N}/{userId:N}/{exp}/{sig}/master.m3u8";
    }

    [AllowAnonymous]
    [HttpGet("h/{itemId}/{u}/{e}/{sig}/{**path}")]
    [HttpHead("h/{itemId}/{u}/{e}/{sig}/{**path}")]
    public async Task<IActionResult> Hls(
        [FromRoute] Guid itemId,
        [FromRoute] Guid u,
        [FromRoute] long e,
        [FromRoute] string sig,
        [FromRoute] string path,
        CancellationToken cancellationToken)
    {
        if (!VerifyLink("h", itemId, u, e, sig))
        {
            Trace("hls", "403 missing, wrong or expired signature");
            return StatusCode(403);
        }

        path ??= string.Empty;
        if (!AllowedHlsPath.IsMatch(path))
        {
            Trace("hls", "404 path not allowed");
            return NotFound();
        }

        var user = _userManager.GetUserById(u);
        var item = _libraryManager.GetItemById(itemId);
        if (user is null || !IsUserAllowed(user))
        {
            Trace("hls", "403 user not allowed");
            return StatusCode(403);
        }

        if (item is not Video video || !item.IsVisibleStandalone(user) || !IsPlayableFile(video))
        {
            Trace("hls", "404 item not found, not visible or file missing");
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
        query["api_key"] = apiKey;

        var target = QueryHelpers.AddQueryString($"{InternalBaseUrl()}/Videos/{itemId:N}/{path}", query);

        using var upstream = new HttpRequestMessage(Request.Method == "HEAD" ? HttpMethod.Head : HttpMethod.Get, target);
        if (Request.Headers.TryGetValue("Range", out var range))
        {
            upstream.Headers.TryAddWithoutValidation("Range", range.ToString());
        }

        var client = _httpClientFactory.CreateClient(NamedClient.Default);
        using var response = await client.SendAsync(upstream, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            Trace("hls " + path.Split('/')[0], $"Jellyfin answered {(int)response.StatusCode}");
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
            Trace("hls " + path, "playlist sent");
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
