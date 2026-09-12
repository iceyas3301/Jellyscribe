using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LetterboxdSync.Configuration;
using LetterboxdSync.Serializd;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace LetterboxdSync.Enhanced;

/// <summary>Result of one Enhanced review sync pass.</summary>
public sealed class EnhancedReviewSyncSummary
{
    public bool SkippedDisabled { get; set; }
    public bool AlreadyRunning { get; set; }
    public bool StorePresent { get; set; }
    public string? StorePath { get; set; }
    public string? Error { get; set; }

    /// <summary>Entries in JE's store that this run was responsible for (after the cutoff).</summary>
    public int Considered { get; set; }

    /// <summary>Entries posted to at least one linked account.</summary>
    public int Posted { get; set; }

    /// <summary>Entries where every linked account rejected the post.</summary>
    public int Failed { get; set; }

    /// <summary>Entries with nowhere to go (e.g. a season-level review — see the runner).</summary>
    public int Skipped { get; set; }

    /// <summary>Entries already posted at this exact content, so nothing to do.</summary>
    public int UpToDate { get; set; }

    /// <summary>Entries whose only linked account is missing. Left pending, not recorded, so
    /// they post the moment the user links an account.</summary>
    public int AwaitingAccount { get; set; }

    public DateTime StartedUtc { get; set; } = DateTime.UtcNow;
    public TimeSpan Duration { get; set; }

    public List<string> Errors { get; } = new();

    public string Describe() =>
        $"considered={Considered} posted={Posted} failed={Failed} skipped={Skipped} " +
        $"upToDate={UpToDate} awaitingAccount={AwaitingAccount} in {Duration.TotalSeconds:0.0}s";
}

/// <summary>
/// Posts reviews and ratings written in Jellyfin Enhanced to the linked Letterboxd (films) or
/// Serializd (TV) account of the Jellyfin user who wrote them.
///
/// Why this exists: JE keeps its own user reviews in reviews.json and never writes them into
/// Jellyfin's UserData, so neither the real-time playback sync nor the daily catch-up in this
/// plugin can see them. Films and shows rated in the JE UI therefore never reached a diary.
///
/// Shape of a run: read JE's store (read-only) → for each entry, resolve the JE author's own
/// linked accounts → post → remember the outcome. A JE entry only ever posts to the accounts
/// of the Jellyfin user who wrote it.
/// </summary>
public sealed class EnhancedReviewSyncRunner
{
    /// <summary>Source stamped on every SyncEvent this runner writes, so the activity feed and
    /// stats can attribute these rows to the JE integration rather than a playback sync.</summary>
    public const string SyncEventSource = "enhanced-review";

    /// <summary>
    /// Serialises runs against each other (scheduled task + manual button).
    /// Runs sequentially by design: Serializd's review endpoint 500s under a burst of parallel
    /// writes, so entries are posted one at a time.
    /// </summary>
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static bool IsRunning => Gate.CurrentCount == 0;

    /// <summary>Last completed run, for the status endpoint. Null until the first run.</summary>
    public static EnhancedReviewSyncSummary? LastRun { get; private set; }

    public static DateTime? LastRunCompletedUtc { get; private set; }

    /// <summary>
    /// Test-only configuration source, mirroring the factory OverrideForTesting convention.
    /// Lets a test drive a run from a config object it owns instead of depending on the global
    /// Plugin singleton (which other test classes rebuild). Production never sets it.
    /// </summary>
    internal static Func<PluginConfiguration?>? ConfigurationOverrideForTesting;

    internal static PluginConfiguration? ResolveConfiguration()
        => ConfigurationOverrideForTesting != null ? ConfigurationOverrideForTesting() : Plugin.Instance?.Configuration;

    /// <summary>
    /// Test-only service factories, taking precedence over the plugin-wide
    /// <see cref="LetterboxdServiceFactory.OverrideForTesting"/> hooks. Those hooks are process
    /// globals shared with every other test class in the suite, so a test that asserts on what
    /// was posted must use these instead — otherwise a parallel test class swapping the global
    /// override makes this runner post through someone else's fake. Production never sets them.
    /// </summary>
    internal static Func<string, string, string?, ILogger, string?, Task<ILetterboxdService>>? LetterboxdFactoryForTesting;

    internal static Func<string, string, ILogger, Task<ISerializdService>>? SerializdFactoryForTesting;

    private static Task<ILetterboxdService> CreateLetterboxdServiceAsync(
        string username, string password, string? rawCookies, ILogger logger, string? userAgent)
        => LetterboxdFactoryForTesting != null
            ? LetterboxdFactoryForTesting(username, password, rawCookies, logger, userAgent)
            : LetterboxdServiceFactory.CreateAuthenticatedAsync(username, password, rawCookies, logger, userAgent);

    private static Task<ISerializdService> CreateSerializdServiceAsync(string email, string password, ILogger logger)
        => SerializdFactoryForTesting != null
            ? SerializdFactoryForTesting(email, password, logger)
            : SerializdServiceFactory.CreateAuthenticatedAsync(email, password, logger);

    /// <summary>Test hook, mirroring the controller OverrideForTesting convention: lets a test
    /// await the summary of a run it did not start.</summary>
    internal static void ResetLastRunForTesting()
    {
        LastRun = null;
        LastRunCompletedUtc = null;
    }

    private readonly ILogger<EnhancedReviewSyncRunner> _logger;
    private readonly IUserManager _userManager;

    public EnhancedReviewSyncRunner(ILogger<EnhancedReviewSyncRunner> logger, IUserManager userManager)
    {
        _logger = logger;
        _userManager = userManager;
        EnhancedReviewSyncState.SetLogger(logger);
    }

    /// <summary>
    /// Whether a run with this configuration is responsible for the given entry. Backfill posts
    /// everything JE has; without it, only reviews written at or after the cutoff the integration
    /// established on its first run. A null cutoff with backfill off means no run has happened yet,
    /// and the next one sets the cutoff to "now" — so nothing already in the store can qualify.
    ///
    /// Shared with the status endpoint so the dashboard can never report a scope the runner does
    /// not actually use.
    /// </summary>
    internal static bool IsInScope(EnhancedReviewEntry entry, bool backfill, DateTimeOffset? cutoff)
    {
        if (backfill) return true;
        if (cutoff == null) return false;
        return (entry.LastWriteUtc ?? DateTimeOffset.MinValue) >= cutoff.Value;
    }

    public async Task<EnhancedReviewSyncSummary> RunAsync(
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var summary = new EnhancedReviewSyncSummary();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var config = ResolveConfiguration();
        if (config == null || !config.EnhancedReviewSyncEnabled)
        {
            summary.SkippedDisabled = true;
            stopwatch.Stop();
            summary.Duration = stopwatch.Elapsed;
            return summary;
        }

        if (!await Gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            summary.AlreadyRunning = true;
            stopwatch.Stop();
            summary.Duration = stopwatch.Elapsed;
            return summary;
        }

        try
        {
            var load = EnhancedReviewStore.Load(config.EnhancedReviewsPath, _logger);
            summary.StorePresent = load.StorePresent;
            summary.StorePath = load.Path;
            summary.Error = load.Error;

            if (load.Error != null)
            {
                _logger.LogWarning(
                    "Jellyfin Enhanced review sync skipped: {Path} could not be read ({Error})",
                    load.Path, load.Error);
                return summary;
            }

            if (!load.StorePresent)
            {
                _logger.LogDebug(
                    "Jellyfin Enhanced review store not found at {Path}; nothing to sync", load.Path);
                return summary;
            }

            var cutoff = config.EnhancedReviewSyncBackfill
                ? (DateTimeOffset?)null
                : EnhancedReviewSyncState.EnsureSinceUtc(DateTimeOffset.UtcNow);

            var entries = load.Entries
                .Where(e => IsInScope(e, config.EnhancedReviewSyncBackfill, cutoff))
                .OrderBy(e => e.LastWriteUtc ?? DateTimeOffset.MinValue)
                .ToList();

            summary.Considered = entries.Count;

            if (entries.Count == 0) return summary;

            var index = 0;
            var maxAttempts = config.EnhancedReviewSyncMaxAttempts;

            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                index++;
                progress?.Report(Math.Min(100.0, index * 100.0 / entries.Count));

                if (!EnhancedReviewSyncState.ShouldAttempt(entry.Key.CompositeId, entry.Fingerprint, maxAttempts))
                {
                    summary.UpToDate++;
                    continue;
                }

                await SyncEntryAsync(config, entry, summary, cancellationToken).ConfigureAwait(false);
            }

            return summary;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The scheduled task must never fault the server's task scheduler.
            summary.Error = ex.Message;
            summary.Errors.Add(ex.Message);
            _logger.LogError(ex, "Jellyfin Enhanced review sync failed");
            return summary;
        }
        finally
        {
            stopwatch.Stop();
            summary.Duration = stopwatch.Elapsed;
            LastRun = summary;
            LastRunCompletedUtc = DateTime.UtcNow;
            _logger.LogInformation("Jellyfin Enhanced review sync: {Summary}", summary.Describe());
            Gate.Release();
        }
    }

    private async Task SyncEntryAsync(
        PluginConfiguration config,
        EnhancedReviewEntry entry,
        EnhancedReviewSyncSummary summary,
        CancellationToken cancellationToken)
    {
        var key = entry.Key;

        // Serializd's API exposes reviews for a whole show and for individual episodes only —
        // there is no season-level review endpoint. A JE season rating has nowhere to go, so
        // it is recorded as a permanent skip (logged once, never retried) instead of being
        // silently promoted to a show-level rating, which would overwrite a real show rating.
        if (key.Scope == EnhancedReviewScope.Season)
        {
            RecordFailedOrSkipped(entry, EnhancedReviewSyncState.SkippedStatus, "serializd-has-no-season-reviews", null);
            summary.Skipped++;
            _logger.LogDebug(
                "Skipping Enhanced review {Key}: Serializd has no season-level review endpoint", key.CompositeId);
            return;
        }

        var username = ResolveUsername(key.UserIdN);
        var title = DescribeTitle(entry);

        if (key.IsMovie)
        {
            var accounts = SelectLetterboxdAccounts(config, key.UserIdN);
            if (accounts.Count == 0)
            {
                // Not recorded: a user who links Letterboxd tomorrow should get their backlog.
                summary.AwaitingAccount++;
                return;
            }

            var posted = await PostToLetterboxdAsync(entry, accounts, summary).ConfigureAwait(false);
            RecordOutcome(entry, posted, summary, username, title, "letterboxd");
            return;
        }

        var serializdAccounts = SelectSerializdAccounts(config, key.UserIdN);
        if (serializdAccounts.Count == 0)
        {
            summary.AwaitingAccount++;
            return;
        }

        var postedTv = await PostToSerializdAsync(entry, serializdAccounts, summary).ConfigureAwait(false);
        RecordOutcome(entry, postedTv, summary, username, title, "serializd");
    }

    private async Task<bool> PostToLetterboxdAsync(
        EnhancedReviewEntry entry,
        List<Account> accounts,
        EnhancedReviewSyncSummary summary)
    {
        var key = entry.Key;
        var anySuccess = false;
        Exception? lastError = null;

        foreach (var account in accounts)
        {
            try
            {
                using var service = await CreateLetterboxdServiceAsync(
                    account.LetterboxdUsername, account.LetterboxdPassword, account.RawCookies, _logger, account.UserAgent)
                    .ConfigureAwait(false);

                // The official API resolves the film from the TMDb id itself, but the scraping
                // fallback posts by slug — resolve once here so both paths work.
                var film = await service.LookupFilmByTmdbIdAsync(key.TmdbId).ConfigureAwait(false);

                await service.PostReviewAsync(
                    film.Slug,
                    entry.HasText ? entry.Content : null,
                    containsSpoilers: false,
                    isRewatch: false,
                    date: entry.DiaryDate?.ToString("yyyy-MM-dd"),
                    rating: entry.LetterboxdRating,
                    tmdbId: key.TmdbId).ConfigureAwait(false);

                anySuccess = true;
                _logger.LogInformation(
                    "Enhanced review posted to Letterboxd: tmdb={TmdbId} as={LetterboxdUsername} " +
                    "hasText={HasText} rating={Rating} date={Date}",
                    key.TmdbId, account.LetterboxdUsername, entry.HasText, entry.LetterboxdRating, entry.DiaryDate);
            }
            catch (Exception ex)
            {
                lastError = ex;
                _logger.LogWarning(
                    "Enhanced review failed for Letterboxd tmdb={TmdbId} as={LetterboxdUsername}: {Message}",
                    key.TmdbId, account.LetterboxdUsername, ex.Message);
            }
        }

        if (!anySuccess && lastError != null)
            summary.Errors.Add($"letterboxd tmdb={key.TmdbId}: {lastError.Message}");

        return anySuccess;
    }

    private async Task<bool> PostToSerializdAsync(
        EnhancedReviewEntry entry,
        List<SerializdAccount> accounts,
        EnhancedReviewSyncSummary summary)
    {
        var key = entry.Key;
        var anySuccess = false;
        Exception? lastError = null;

        foreach (var account in accounts)
        {
            try
            {
                using var service = await CreateSerializdServiceAsync(account.Email, account.Password, _logger)
                    .ConfigureAwait(false);

                if (key.Scope == EnhancedReviewScope.Episode)
                {
                    await service.CreateEpisodeReviewAsync(
                        key.TmdbId, key.SeasonNumber!.Value, key.EpisodeNumber!.Value,
                        entry.SerializdRating, entry.HasText ? entry.Content : null, containsSpoiler: false)
                        .ConfigureAwait(false);
                }
                else
                {
                    await service.CreateShowReviewAsync(
                        key.TmdbId, entry.SerializdRating, entry.HasText ? entry.Content : null, containsSpoiler: false)
                        .ConfigureAwait(false);
                }

                anySuccess = true;
                _logger.LogInformation(
                    "Enhanced review posted to Serializd: tmdb={TmdbId} scope={Scope} season={Season} episode={Episode} " +
                    "as={Email} hasText={HasText} rating={Rating}",
                    key.TmdbId, key.Scope, key.SeasonNumber, key.EpisodeNumber,
                    account.Email, entry.HasText, entry.SerializdRating);
            }
            catch (Exception ex)
            {
                lastError = ex;
                _logger.LogWarning(
                    "Enhanced review failed for Serializd tmdb={TmdbId} scope={Scope} as={Email}: {Message}",
                    key.TmdbId, key.Scope, account.Email, ex.Message);
            }
        }

        if (!anySuccess && lastError != null)
            summary.Errors.Add($"serializd tmdb={key.TmdbId}: {lastError.Message}");

        return anySuccess;
    }

    /// <summary>
    /// Fold one entry's provider outcome into the summary, the sync state, and the activity
    /// feed. "Failed" here means every linked account rejected it, so a partially-posted entry
    /// (two accounts, one rejecting) counts as posted and is not retried blindly.
    /// </summary>
    private void RecordOutcome(
        EnhancedReviewEntry entry,
        bool anySuccess,
        EnhancedReviewSyncSummary summary,
        string username,
        string title,
        string destination)
    {
        if (anySuccess)
        {
            summary.Posted++;
            RecordFailedOrSkipped(entry, EnhancedReviewSyncState.SyncedStatus, destination, null);
        }
        else
        {
            summary.Failed++;
            var error = summary.Errors.Count > 0 ? summary.Errors[^1] : "unknown error";
            RecordFailedOrSkipped(entry, EnhancedReviewSyncState.FailedStatus, destination, error);
        }

        var key = entry.Key;
        var label = key.Scope switch
        {
            EnhancedReviewScope.Episode => $" · S{key.SeasonNumber}E{key.EpisodeNumber}",
            EnhancedReviewScope.Season => $" · S{key.SeasonNumber}",
            _ => string.Empty
        };

        SyncHistory.Record(new SyncEvent
        {
            FilmTitle = title + label,
            TmdbId = key.TmdbId,
            Username = username,
            Timestamp = DateTime.UtcNow,
            ViewingDate = entry.DiaryDate,
            Status = anySuccess ? SyncStatus.Success : SyncStatus.Failed,
            Error = anySuccess ? null : summary.Errors.LastOrDefault(),
            Source = SyncEventSource
        });
    }

    private static void RecordFailedOrSkipped(
        EnhancedReviewEntry entry, string status, string reason, string? error)
        => EnhancedReviewSyncState.Record(entry.Key.CompositeId, entry.Fingerprint, status, reason, error);

    /// <summary>
    /// Enabled Letterboxd accounts for the JE author, primary first. Compares user ids in GUID
    /// "N" form on both sides, because JE keys its store with "N" ids while a hand-edited config
    /// can hold the dashed form — a raw string compare would silently sync nothing.
    /// </summary>
    internal static List<Account> SelectLetterboxdAccounts(PluginConfiguration config, string userIdN)
    {
        if (config?.Accounts == null) return new List<Account>();

        return config.Accounts
            .Where(a => a.Enabled
                && !string.IsNullOrWhiteSpace(a.LetterboxdUsername)
                && MatchesJellyfinUser(a.UserJellyfinId, userIdN))
            .OrderByDescending(a => a.IsPrimary)
            .ToList();
    }

    /// <summary>Enabled Serializd accounts for the JE author. See
    /// <see cref="SelectLetterboxdAccounts"/> for why the id compare is normalised.</summary>
    internal static List<SerializdAccount> SelectSerializdAccounts(PluginConfiguration config, string userIdN)
    {
        if (config?.SerializdAccounts == null) return new List<SerializdAccount>();

        return config.SerializdAccounts
            .Where(a => a.Enabled
                && !string.IsNullOrWhiteSpace(a.Email)
                && MatchesJellyfinUser(a.UserJellyfinId, userIdN))
            .OrderByDescending(a => a.IsPrimary)
            .ToList();
    }

    internal static bool MatchesJellyfinUser(string? configuredId, string userIdN)
    {
        var left = EnhancedReviewKey.NormalizeUserKey(configuredId);
        var right = EnhancedReviewKey.NormalizeUserKey(userIdN);
        return left != null && right != null && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Activity-feed title. JE's store carries no title, and rather than couple this runner to
    /// the library we label rows by TMDb id; the source column marks them as JE reviews.
    /// </summary>
    internal static string DescribeTitle(EnhancedReviewEntry entry)
        => $"Jellyfin Enhanced review · TMDb {entry.Key.TmdbId}";

    private string ResolveUsername(string userIdN)
    {
        try
        {
            if (Guid.TryParseExact(userIdN, "N", out var guid))
            {
                var user = _userManager?.GetUserById(guid);
                if (user != null && !string.IsNullOrWhiteSpace(user.Username)) return user.Username;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Could not resolve Jellyfin username for {UserIdN}: {Message}", userIdN, ex.Message);
        }

        return userIdN;
    }
}
