using System;
using System.Collections.Concurrent;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.DeoVR.Api;

/// <summary>
/// One request handled by the plugin, for troubleshooting.
/// Never contains feed keys, signatures or passwords.
/// </summary>
public sealed class RequestLogEntry
{
    [JsonPropertyName("time")]
    public string Time { get; set; } = string.Empty;

    [JsonPropertyName("method")]
    public string Method { get; set; } = string.Empty;

    [JsonPropertyName("route")]
    public string Route { get; set; } = string.Empty;

    [JsonPropertyName("scheme")]
    public string Scheme { get; set; } = string.Empty;

    [JsonPropertyName("client")]
    public string Client { get; set; } = string.Empty;

    [JsonPropertyName("userAgent")]
    public string UserAgent { get; set; } = string.Empty;

    [JsonPropertyName("contentType")]
    public string ContentType { get; set; } = string.Empty;

    [JsonPropertyName("credentials")]
    public string Credentials { get; set; } = string.Empty;

    [JsonPropertyName("range")]
    public string Range { get; set; } = string.Empty;

    [JsonPropertyName("result")]
    public string Result { get; set; } = string.Empty;
}

/// <summary>Small in-memory ring buffer of recent requests (shown to administrators only).</summary>
internal static class RequestLog
{
    private const int MaxEntries = 100;

    private static readonly ConcurrentQueue<RequestLogEntry> Entries = new();

    public static void Add(RequestLogEntry entry)
    {
        Entries.Enqueue(entry);
        while (Entries.Count > MaxEntries && Entries.TryDequeue(out _))
        {
        }
    }

    /// <summary>Returns the entries, newest first.</summary>
    public static RequestLogEntry[] Snapshot()
    {
        var items = Entries.ToArray();
        Array.Reverse(items);
        return items;
    }
}
