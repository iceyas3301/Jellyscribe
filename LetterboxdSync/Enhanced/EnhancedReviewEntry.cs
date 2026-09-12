using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace LetterboxdSync.Enhanced;

/// <summary>
/// One review as written in Jellyfin Enhanced (its shared reviews.json store), plus the
/// rating/date mapping this plugin needs. JE owns the source data; nothing here mutates it.
/// </summary>
public sealed class EnhancedReviewEntry
{
    public required EnhancedReviewKey Key { get; init; }

    /// <summary>Review body. Empty for a rating-only entry, which is the common JE case.</summary>
    public string Content { get; init; } = string.Empty;

    /// <summary>JE's rating, 1–5 in 0.5 increments (null when the entry is text-only).</summary>
    public double? Rating { get; init; }

    public DateTimeOffset? CreatedAt { get; init; }

    /// <summary>JE bumps this when the user edits their review — the change signal for re-sync.</summary>
    public DateTimeOffset? UpdatedAt { get; init; }

    public bool HasText => !string.IsNullOrWhiteSpace(Content);

    /// <summary>JE stores 1–5 in 0.5 steps, which is already Letterboxd's scale.</summary>
    public double? LetterboxdRating
    {
        get
        {
            if (Rating is not > 0) return null;
            return Math.Clamp(Math.Round(Rating.Value * 2, MidpointRounding.AwayFromZero) / 2.0, 0.5, 5.0);
        }
    }

    /// <summary>JE's 1–5 stars on Serializd's 1–10 scale (10 = 5★). Null when unrated.</summary>
    public int? SerializdRating
    {
        get
        {
            if (Rating is not > 0) return null;
            return (int)Math.Clamp(Math.Round(Rating.Value * 2, MidpointRounding.AwayFromZero), 1, 10);
        }
    }

    /// <summary>
    /// The date the resulting diary entry should carry: JE's creation date, so a backfill of
    /// older reviews lands on the day each review was written rather than the day the sync
    /// first ran.
    /// </summary>
    public DateTime? DiaryDateUtc => (CreatedAt ?? UpdatedAt)?.UtcDateTime.Date;

    /// <summary>Most recent write JE recorded — the no-backfill cutoff compares against this.</summary>
    public DateTimeOffset? LastWriteUtc => UpdatedAt ?? CreatedAt;

    /// <summary>
    /// Change detector for dedupe. Deliberately hashes the text rather than storing it, so the
    /// sync-state file never becomes a second copy of a user's private review drafts.
    /// </summary>
    public string Fingerprint => ComputeFingerprint(Content, Rating, UpdatedAt);

    internal static string ComputeFingerprint(string? content, double? rating, DateTimeOffset? updatedAt)
    {
        var input = string.Join(
            '\u001f',
            content ?? string.Empty,
            rating?.ToString("0.###", CultureInfo.InvariantCulture) ?? string.Empty,
            updatedAt?.UtcDateTime.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty);

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();
    }
}
