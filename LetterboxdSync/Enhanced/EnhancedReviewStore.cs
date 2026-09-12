using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace LetterboxdSync.Enhanced;

/// <summary>Outcome of trying to read JE's review store. Never throws — a missing or
/// unreadable store is a reportable state, not a plugin failure.</summary>
public sealed record EnhancedReviewLoadResult(
    bool StorePresent,
    bool StoreReadable,
    string Path,
    IReadOnlyList<EnhancedReviewEntry> Entries,
    string? Error)
{
    public static EnhancedReviewLoadResult Ok(string path, IReadOnlyList<EnhancedReviewEntry> entries)
        => new(true, true, path, entries, null);

    /// <summary>The JE plugin isn't installed, or has never had a review written to it.</summary>
    public static EnhancedReviewLoadResult Missing(string path)
        => new(false, true, path, Array.Empty<EnhancedReviewEntry>(), null);

    public static EnhancedReviewLoadResult Failed(string path, string error)
        => new(true, false, path, Array.Empty<EnhancedReviewEntry>(), error);
}

/// <summary>
/// Reads the shared review store owned by the Jellyfin Enhanced plugin.
///
/// JE writes one file, <c>reviews.json</c>, in its own plugin configuration directory
/// (<c>&lt;config&gt;/plugins/configurations/Jellyfin.Plugin.JellyfinEnhanced/reviews.json</c>),
/// keyed <c>{userIdN}:{mediaType}:{tmdbId}[:s{n}[:e{n}]]</c>. This plugin only ever reads it.
/// Reading the file rather than calling JE's HTTP API keeps the sync headless — no API key,
/// no session, and it works while nobody is using the web UI.
/// </summary>
public static class EnhancedReviewStore
{
    /// <summary>Directory name JE uses under <c>plugins/configurations</c>.</summary>
    internal const string JellyfinEnhancedConfigFolder = "Jellyfin.Plugin.JellyfinEnhanced";

    internal const string StoreFileName = "reviews.json";

    /// <summary>Test-only. Production resolves the path from the plugin assembly location.</summary>
    internal static string? PathOverrideForTesting { get; set; }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        // JE serialises PascalCase today; accept any casing so a future JE that switches
        // naming policies doesn't silently drop every review.
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    /// <summary>
    /// Resolve the store path: an explicit admin override wins, otherwise JE's location
    /// relative to our own plugin directory (both live under <c>plugins/</c>).
    /// </summary>
    public static string ResolvePath(string? configuredPath)
    {
        if (!string.IsNullOrEmpty(PathOverrideForTesting)) return PathOverrideForTesting!;
        if (!string.IsNullOrWhiteSpace(configuredPath)) return configuredPath!.Trim();

        var pluginDir = Path.GetDirectoryName(typeof(EnhancedReviewStore).Assembly.Location);
        if (!string.IsNullOrEmpty(pluginDir))
        {
            return Path.GetFullPath(Path.Combine(
                pluginDir!, "..", "configurations", JellyfinEnhancedConfigFolder, StoreFileName));
        }

        return Path.Combine("configurations", JellyfinEnhancedConfigFolder, StoreFileName);
    }

    public static EnhancedReviewLoadResult Load(string? configuredPath, ILogger? logger = null)
    {
        var path = ResolvePath(configuredPath);

        try
        {
            if (!File.Exists(path)) return EnhancedReviewLoadResult.Missing(path);

            return EnhancedReviewLoadResult.Ok(path, Parse(File.ReadAllText(path)));
        }
        catch (Exception ex)
        {
            // JE writes under a lock (and swaps the file), so a read can legitimately collide
            // with a write. Treat it as a transient reportable error, never a crash.
            logger?.LogWarning(
                "Jellyfin Enhanced review store could not be read from {Path}: {Message}", path, ex.Message);
            return EnhancedReviewLoadResult.Failed(path, ex.Message);
        }
    }

    /// <summary>
    /// Parse JE's store JSON into entries, skipping anything with an unrecognised key.
    ///
    /// The dictionary key is the identity: it is the same string JE itself reads back by
    /// (JE filters the store on <c>{userIdN}:{mediaType}:{tmdbId}</c>), and deriving identity
    /// from the per-entry fields instead would mean a store whose key and fields disagree could
    /// post to the wrong title. An entry whose key doesn't parse is dropped and reported via the
    /// store count rather than guessed at. The per-entry fields supply only the payload
    /// (text, rating, timestamps).
    /// </summary>
    internal static List<EnhancedReviewEntry> Parse(string json)
    {
        var entries = new List<EnhancedReviewEntry>();
        if (string.IsNullOrWhiteSpace(json)) return entries;

        var store = JsonSerializer.Deserialize<EnhancedReviewStoreFile>(json, JsonOptions);
        if (store?.Reviews == null) return entries;

        foreach (var pair in store.Reviews)
        {
            var key = EnhancedReviewKey.TryParse(pair.Key);
            if (key == null) continue;

            var dto = pair.Value;

            entries.Add(new EnhancedReviewEntry
            {
                Key = key,
                Content = dto?.Content ?? string.Empty,
                Rating = dto?.Rating,
                CreatedAt = ParseTimestamp(dto?.CreatedAt),
                UpdatedAt = ParseTimestamp(dto?.UpdatedAt)
            });
        }

        return entries;
    }

    private static DateTimeOffset? ParseTimestamp(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        return DateTimeOffset.TryParse(
            value.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;
    }

    private sealed class EnhancedReviewStoreFile
    {
        public Dictionary<string, EnhancedReviewJsonEntry?>? Reviews { get; set; }
    }

    /// <summary>
    /// The payload half of JE's on-disk record (PascalCase as JE writes it). JE also stores
    /// UserId/TmdbId/MediaType inside each entry, but identity comes from the dictionary key,
    /// so those fields are deliberately not read.
    /// </summary>
    private sealed class EnhancedReviewJsonEntry
    {
        public string? Content { get; set; }
        public double? Rating { get; set; }
        public string? CreatedAt { get; set; }
        public string? UpdatedAt { get; set; }
    }
}
