using System;
using System.Globalization;

namespace LetterboxdSync.Enhanced;

/// <summary>What a Jellyfin Enhanced review key points at.</summary>
public enum EnhancedReviewScope
{
    /// <summary>A film. Syncs to Letterboxd.</summary>
    Movie,

    /// <summary>A whole TV show. Syncs to Serializd.</summary>
    Show,

    /// <summary>A single season. Serializd has no season-level review endpoint (see the runner).</summary>
    Season,

    /// <summary>A single TV episode. Syncs to Serializd.</summary>
    Episode
}

/// <summary>
/// A parsed key from Jellyfin Enhanced's shared <c>reviews.json</c>. JE keys that store as
/// <c>{userIdN}:{mediaType}:{tmdbId}</c> with an optional season/episode suffix —
/// <c>{tmdbId}:s{n}</c> or <c>{tmdbId}:s{n}:e{n}</c> (the same extended form
/// JellyfinEnhancedController.IsValidTmdbKey accepts on the API side).
/// </summary>
public sealed record EnhancedReviewKey(
    string UserIdN,
    string MediaType,
    int TmdbId,
    int? SeasonNumber,
    int? EpisodeNumber,
    EnhancedReviewScope Scope)
{
    public const string MovieMediaType = "movie";
    public const string TvMediaType = "tv";

    public bool IsMovie => Scope == EnhancedReviewScope.Movie;

    /// <summary>
    /// Stable identity for this review, used as the dedupe key in
    /// <see cref="EnhancedReviewSyncState"/>. Rebuilt from the parsed parts rather than the
    /// raw string so a key that JE re-orders or case-changes still maps to one identity.
    /// </summary>
    public string CompositeId => Scope switch
    {
        EnhancedReviewScope.Movie => $"{UserIdN}:{MovieMediaType}:{TmdbId}",
        EnhancedReviewScope.Show => $"{UserIdN}:{TvMediaType}:{TmdbId}",
        EnhancedReviewScope.Season => $"{UserIdN}:{TvMediaType}:{TmdbId}:s{SeasonNumber}",
        _ => $"{UserIdN}:{TvMediaType}:{TmdbId}:s{SeasonNumber}:e{EpisodeNumber}"
    };

    /// <summary>
    /// Parse a JE store key. Returns null for anything not well-formed — the caller logs and
    /// skips rather than guessing, because a wrong guess would file a review against the
    /// wrong title on a real Letterboxd/Serializd account.
    /// </summary>
    public static EnhancedReviewKey? TryParse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var parts = raw.Trim().Split(':');
        if (parts.Length < 3 || parts.Length > 5) return null;

        var userIdN = NormalizeUserKey(parts[0]);
        if (userIdN == null) return null;

        var mediaType = parts[1]?.Trim().ToLowerInvariant();
        if (mediaType != MovieMediaType && mediaType != TvMediaType) return null;

        if (!int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var tmdbId) || tmdbId <= 0)
            return null;

        int? season = null;
        int? episode = null;

        if (parts.Length >= 4)
        {
            season = ParseSuffixedNumber(parts[3], 's');
            if (season == null || season <= 0) return null;
        }

        if (parts.Length == 5)
        {
            episode = ParseSuffixedNumber(parts[4], 'e');
            if (episode == null || episode < 0) return null;
        }

        // A film carries no season/episode; an episode needs its season to address it.
        if (mediaType == MovieMediaType && season != null) return null;
        if (episode != null && season == null) return null;

        var scope = season == null
            ? (mediaType == MovieMediaType ? EnhancedReviewScope.Movie : EnhancedReviewScope.Show)
            : (episode == null ? EnhancedReviewScope.Season : EnhancedReviewScope.Episode);

        return new EnhancedReviewKey(userIdN, mediaType, tmdbId, season, episode, scope);
    }

    /// <summary>Parse the "s3"/"e12" half of an extended key. Returns null when malformed.</summary>
    private static int? ParseSuffixedNumber(string value, char prefix)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length < 2) return null;
        if (char.ToLowerInvariant(trimmed[0]) != prefix) return null;

        return int.TryParse(trimmed.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            ? number
            : null;
    }

    /// <summary>
    /// Normalise a Jellyfin user id to GUID "N" form (32 hex chars, no dashes) so a JE key can
    /// be matched against <c>Account.UserJellyfinId</c> whichever form either side stores.
    /// Returns null when the input is not a GUID — callers treat that as "no match", never as
    /// a fuzzy match.
    /// </summary>
    public static string? NormalizeUserKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return Guid.TryParse(value.Trim(), out var guid) ? guid.ToString("N") : null;
    }
}
