using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace LetterboxdSync;

/// <remarks>
/// The optional <see cref="CancellationToken"/> on the methods the sync runners call cancels the
/// waits (pacing, rate-limit and backoff) and the reads. It never cuts off a write already sent,
/// since that write may have landed.
/// </remarks>
public interface ILetterboxdService : IDisposable
{
    Task AuthenticateAsync(string username, string password, string? rawCookies = null);

    Task<FilmResult> LookupFilmByTmdbIdAsync(int tmdbId, CancellationToken cancellationToken = default);

    /// <summary>
    /// True for the website (scraping) session. Its film lookup makes page requests of its own,
    /// so callers pace the next request after the lookup rather than alongside it.
    /// </summary>
    bool IsWebsiteSession => false;

    Task<DiaryInfo> GetDiaryInfoAsync(string filmIdOrSlug, string username, CancellationToken cancellationToken = default);

    Task MarkAsWatchedAsync(string filmSlug, string filmId, DateTime? date, bool liked,
        string? productionId = null, bool rewatch = false, double? rating = null,
        CancellationToken cancellationToken = default);

    Task PostReviewAsync(string filmSlug, string? reviewText, bool containsSpoilers = false,
        bool isRewatch = false, string? date = null, double? rating = null, int? tmdbId = null);

    /// <summary>
    /// Puts a review, and the rating when one is given, on the member's existing diary entry for
    /// this film dated <paramref name="diaryDate"/>, instead of logging another watch. Never logs
    /// a new entry itself: on <see cref="ReviewAttachResult.NoEntry"/> or
    /// <see cref="ReviewAttachResult.Unsupported"/> the caller decides what to post. The default is
    /// Unsupported, which is the website session's answer: the site flow it uses can only add
    /// entries.
    /// </summary>
    Task<ReviewAttachResult> AddReviewToDiaryEntryAsync(int tmdbId, DateTime diaryDate, string reviewText,
        bool containsSpoilers, double? rating)
        => Task.FromResult(ReviewAttachResult.Unsupported);

    Task<List<int>> GetWatchlistTmdbIdsAsync(string username, CancellationToken cancellationToken = default);

    Task<List<int>> GetDiaryTmdbIdsAsync(string username);

    Task<List<DiaryFilmEntry>> GetDiaryFilmEntriesAsync(string username, CancellationToken cancellationToken = default);

    /// <summary>
    /// True when this service can find an existing diary entry and edit it in place. The API
    /// client can; the scraping fallback has no entry identifiers, so it cannot.
    /// </summary>
    bool SupportsLogEntryEditing { get; }

    /// <summary>
    /// The id of the member's own diary entry for this film on this date, or null when there
    /// isn't one (or the service can't tell). Lets a review attach to a viewing that is already
    /// on the diary rather than logging the film a second time — Letterboxd's POST never
    /// reconciles with an existing entry, it just adds another one.
    /// </summary>
    Task<string?> FindLogEntryIdAsync(string filmIdOrSlug, DateTime date);

    /// <summary>
    /// Edit an existing diary entry in place. Partial by design: only the rating and/or review
    /// text are sent, so the entry's date and its membership of the diary are left alone.
    /// </summary>
    Task UpdateLogEntryAsync(string logEntryId, string? reviewText, bool containsSpoilers, double? rating);

    /// <summary>
    /// Sets the member's film rating (the one on the film page and profile, not a diary entry).
    /// <paramref name="rating"/> is Letterboxd half-stars, 0.5 to 5.0, already mapped by the caller
    /// with <see cref="Helpers.MapRating"/>; each implementation converts to its own wire scale.
    /// <paramref name="filmId"/> must come from this instance's <see cref="LookupFilmByTmdbIdAsync"/>,
    /// since the two implementations use different id forms. Letterboxd also marks a rated film
    /// watched, which removes it from the member's watchlist.
    /// </summary>
    Task SetFilmRatingAsync(string filmSlug, string filmId, double rating, CancellationToken cancellationToken = default);
}

/// <summary>Outcome of <see cref="ILetterboxdService.AddReviewToDiaryEntryAsync"/>.</summary>
public enum ReviewAttachResult
{
    /// <summary>This session cannot edit a diary entry. Nothing was changed.</summary>
    Unsupported = 0,

    /// <summary>The review is now on the existing entry.</summary>
    Attached,

    /// <summary>The member has no entry for the film on that date. Nothing was changed.</summary>
    NoEntry,

    /// <summary>Every entry for the film on that date already has a review, left as it is.</summary>
    AlreadyReviewed,
}
