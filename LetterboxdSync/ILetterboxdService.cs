using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace LetterboxdSync;

public interface ILetterboxdService : IDisposable
{
    Task AuthenticateAsync(string username, string password, string? rawCookies = null);

    Task<FilmResult> LookupFilmByTmdbIdAsync(int tmdbId);

    Task<DiaryInfo> GetDiaryInfoAsync(string filmIdOrSlug, string username);

    Task MarkAsWatchedAsync(string filmSlug, string filmId, DateTime? date, bool liked,
        string? productionId = null, bool rewatch = false, double? rating = null);

    Task PostReviewAsync(string filmSlug, string? reviewText, bool containsSpoilers = false,
        bool isRewatch = false, string? date = null, double? rating = null, int? tmdbId = null);

    Task<List<int>> GetWatchlistTmdbIdsAsync(string username);

    Task<List<int>> GetDiaryTmdbIdsAsync(string username);

    Task<List<DiaryFilmEntry>> GetDiaryFilmEntriesAsync(string username);

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
}
