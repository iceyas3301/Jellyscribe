using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using LetterboxdSync;
using LetterboxdSync.Configuration;
using LetterboxdSync.Enhanced;
using LetterboxdSync.Serializd;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace LetterboxdSync.Tests.Enhanced;

/// <summary>
/// End-to-end coverage of the Jellyfin Enhanced review sync with fake Letterboxd/Serializd
/// services: routing by scope, rating/date mapping, dedupe on re-run, the privacy boundary
/// between Jellyfin users, and failure/retry behaviour.
/// </summary>
[Collection("EnhancedReviewSync")]
public class EnhancedReviewSyncRunnerTests : IDisposable
{
    private const string UserN = "deadbeefdeadbeefdeadbeefdeadbeef";
    private const string OtherUserN = "feedfacefeedfacefeedfacefeedface";

    private readonly string _tempDir;
    private readonly string _storePath;
    private readonly PluginConfiguration _config;
    private readonly EnhancedReviewSyncRunner _runner;
    private readonly FakeLetterboxdService _letterboxd = new();
    private readonly FakeSerializdService _serializd = new();
    private readonly List<string> _letterboxdLogins = new();
    private readonly List<string> _serializdLogins = new();

    public EnhancedReviewSyncRunnerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "lbs-enhanced-run-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _storePath = Path.Combine(_tempDir, "reviews.json");

        _config = new PluginConfiguration
        {
            EnhancedReviewSyncEnabled = true,
            EnhancedReviewSyncBackfill = true,
            EnhancedReviewSyncMaxAttempts = 5
        };

        EnhancedReviewStore.PathOverrideForTesting = _storePath;
        EnhancedReviewSyncState.PathOverrideForTesting = Path.Combine(_tempDir, "state.json");
        EnhancedReviewSyncState.ResetForTesting();

        EnhancedReviewSyncRunner.ConfigurationOverrideForTesting = () => _config;
        EnhancedReviewSyncRunner.ResetLastRunForTesting();

        EnhancedReviewSyncRunner.LetterboxdFactoryForTesting = (username, _, _, _, _) =>
        {
            _letterboxdLogins.Add(username);
            return Task.FromResult<ILetterboxdService>(_letterboxd);
        };
        EnhancedReviewSyncRunner.SerializdFactoryForTesting = (email, _, _) =>
        {
            _serializdLogins.Add(email);
            return Task.FromResult<ISerializdService>(_serializd);
        };

        _runner = new EnhancedReviewSyncRunner(
            NullLogger<EnhancedReviewSyncRunner>.Instance, Substitute.For<IUserManager>());
    }

    public void Dispose()
    {
        EnhancedReviewStore.PathOverrideForTesting = null;
        EnhancedReviewSyncState.PathOverrideForTesting = null;
        EnhancedReviewSyncState.ResetForTesting();
        EnhancedReviewSyncRunner.ConfigurationOverrideForTesting = null;
        EnhancedReviewSyncRunner.ResetLastRunForTesting();
        EnhancedReviewSyncRunner.LetterboxdFactoryForTesting = null;
        EnhancedReviewSyncRunner.SerializdFactoryForTesting = null;
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    // ── Films → Letterboxd ──────────────────────────────────────────────────────

    [Fact]
    public async Task MovieReview_WithText_PostsTextRatingAndJeDateToLetterboxd()
    {
        AddLetterboxdAccount(UserN, "sample-user");
        WriteStore(Entry($"{UserN}:movie:278", UserN, "278", "movie", "Classic", 5, "2026-07-16T01:50:06.0000000Z"));

        var summary = await _runner.RunAsync();

        var post = Assert.Single(_letterboxd.Posts);
        Assert.Equal("film-278", post.Slug);
        Assert.Equal("Classic", post.Text);
        Assert.Equal(5.0, post.Rating);
        Assert.Equal("2026-07-16", post.Date);
        Assert.Equal(278, post.TmdbId);
        Assert.Equal(1, summary.Posted);
        Assert.Equal(0, summary.Failed);
        Assert.Equal(new[] { "sample-user" }, _letterboxdLogins);
    }

    [Fact]
    public async Task MovieRatingWithoutText_StillPostsAsARatedDiaryEntry()
    {
        AddLetterboxdAccount(UserN, "sample-user");
        WriteStore(Entry($"{UserN}:movie:142", UserN, "142", "movie", "", 3.5, "2026-08-02T10:00:00.0000000Z"));

        var summary = await _runner.RunAsync();

        var post = Assert.Single(_letterboxd.Posts);
        Assert.Null(post.Text);
        Assert.Equal(3.5, post.Rating);
        Assert.Equal("2026-08-02", post.Date);
        Assert.Equal(1, summary.Posted);
    }

    // ── TV → Serializd ──────────────────────────────────────────────────────────

    [Fact]
    public async Task ShowReview_PostsToSerializdWithRatingOnItsTenPointScale()
    {
        AddSerializdAccount(UserN, "iven@example.com");
        WriteStore(Entry($"{UserN}:tv:1396", UserN, "1396", "tv", "Best season of TV", 4.5, "2026-07-30T20:50:27.0000000Z"));

        var summary = await _runner.RunAsync();

        var post = Assert.Single(_serializd.ShowReviews);
        Assert.Equal(1396, post.TmdbId);
        Assert.Equal(9, post.Rating);
        Assert.Equal("Best season of TV", post.Text);
        Assert.Equal(1, summary.Posted);
        Assert.Equal(new[] { "iven@example.com" }, _serializdLogins);
    }

    [Fact]
    public async Task EpisodeReview_PostsToTheEpisodeNotTheShow()
    {
        AddSerializdAccount(UserN, "iven@example.com");
        WriteStore(Entry($"{UserN}:tv:1396:s2:e7", UserN, "1396:s2:e7", "tv", "Hell of an episode", 5, "2026-08-08T02:42:04.0000000Z"));

        var summary = await _runner.RunAsync();

        Assert.Empty(_serializd.ShowReviews);
        var post = Assert.Single(_serializd.EpisodeReviews);
        Assert.Equal(1396, post.TmdbId);
        Assert.Equal(2, post.SeasonNumber);
        Assert.Equal(7, post.EpisodeNumber);
        Assert.Equal(10, post.Rating);
        Assert.Equal(1, summary.Posted);
    }

    [Fact]
    public async Task SeasonReview_IsSkippedBecauseSerializdHasNoSeasonDestination()
    {
        AddSerializdAccount(UserN, "iven@example.com");
        WriteStore(Entry($"{UserN}:tv:262375:s1", UserN, "262375:s1", "tv", "", 5, "2026-07-16T20:51:13.0000000Z"));

        var summary = await _runner.RunAsync();

        Assert.Empty(_serializd.ShowReviews);
        Assert.Empty(_serializd.EpisodeReviews);
        Assert.Equal(1, summary.Skipped);
        Assert.Equal(0, summary.Failed);

        var record = EnhancedReviewSyncState.Get($"{UserN}:tv:262375:s1")!;
        Assert.Equal(EnhancedReviewSyncState.SkippedStatus, record.Status);
        Assert.Equal("serializd-has-no-season-reviews", record.Reason);

        // And a later run must not keep re-reporting it.
        var second = await _runner.RunAsync();
        Assert.Equal(1, second.UpToDate);
        Assert.Equal(0, second.Skipped);
    }

    // ── Account routing and the privacy boundary ────────────────────────────────

    [Fact]
    public async Task ReviewOnlyPostsToItsAuthorsAccounts()
    {
        AddLetterboxdAccount(UserN, "sample-user");
        AddLetterboxdAccount(OtherUserN, "someoneelse");
        WriteStore(Entry($"{UserN}:movie:278", UserN, "278", "movie", "", 5, "2026-07-16T01:50:06.0000000Z"));

        await _runner.RunAsync();

        Assert.Equal(new[] { "sample-user" }, _letterboxdLogins);
    }

    [Fact]
    public async Task ReviewWhoseAuthorHasNoLinkedAccount_StaysPending()
    {
        AddLetterboxdAccount(OtherUserN, "someoneelse");
        WriteStore(Entry($"{UserN}:movie:278", UserN, "278", "movie", "", 5, "2026-07-16T01:50:06.0000000Z"));

        var summary = await _runner.RunAsync();

        Assert.Equal(1, summary.AwaitingAccount);
        Assert.Equal(0, summary.Posted);
        Assert.Empty(_letterboxd.Posts);

        // Nothing was recorded, so linking an account later must still post it.
        Assert.Null(EnhancedReviewSyncState.Get($"{UserN}:movie:278"));

        AddLetterboxdAccount(UserN, "sample-user");
        var afterLinking = await _runner.RunAsync();

        Assert.Equal(1, afterLinking.Posted);
        Assert.Single(_letterboxd.Posts);
    }

    [Fact]
    public async Task ConfigHoldingADashedUserGuid_StillMatches()
    {
        AddLetterboxdAccount("deadbeef-dead-beef-dead-beefdeadbeef", "sample-user");
        WriteStore(Entry($"{UserN}:movie:278", UserN, "278", "movie", "", 5, "2026-07-16T01:50:06.0000000Z"));

        var summary = await _runner.RunAsync();

        Assert.Equal(1, summary.Posted);
        Assert.Single(_letterboxd.Posts);
    }

    [Fact]
    public async Task DisabledAccount_IsNotPostedTo()
    {
        AddLetterboxdAccount(UserN, "sample-user", enabled: false);
        WriteStore(Entry($"{UserN}:movie:278", UserN, "278", "movie", "", 5, "2026-07-16T01:50:06.0000000Z"));

        var summary = await _runner.RunAsync();

        Assert.Equal(1, summary.AwaitingAccount);
        Assert.Empty(_letterboxd.Posts);
    }

    // ── Idempotency ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task SecondRun_DoesNotRepostAnUnchangedReview()
    {
        AddLetterboxdAccount(UserN, "sample-user");
        WriteStore(Entry($"{UserN}:movie:278", UserN, "278", "movie", "Classic", 5, "2026-07-16T01:50:06.0000000Z"));

        await _runner.RunAsync();
        var second = await _runner.RunAsync();

        Assert.Single(_letterboxd.Posts);
        Assert.Equal(1, second.UpToDate);
        Assert.Equal(0, second.Posted);
    }

    [Fact]
    public async Task EditedReview_IsReposted()
    {
        AddLetterboxdAccount(UserN, "sample-user");
        WriteStore(Entry($"{UserN}:movie:278", UserN, "278", "movie", "Classic", 5, "2026-07-16T01:50:06.0000000Z"));
        await _runner.RunAsync();

        // Same key, new text and a bumped UpdatedAt — what JE writes on an edit.
        WriteStore(Entry($"{UserN}:movie:278", UserN, "278", "movie", "Even better the second time", 4.5,
            "2026-07-16T01:50:06.0000000Z", "2026-09-01T12:00:00.0000000Z"));
        var second = await _runner.RunAsync();

        Assert.Equal(2, _letterboxd.Posts.Count);
        Assert.Equal("Even better the second time", _letterboxd.Posts[1].Text);
        Assert.Equal(4.5, _letterboxd.Posts[1].Rating);
        Assert.Equal(1, second.Posted);
    }

    [Fact]
    public async Task EditingOnlyTheRating_IsReposted()
    {
        AddLetterboxdAccount(UserN, "sample-user");
        WriteStore(Entry($"{UserN}:movie:278", UserN, "278", "movie", "Classic", 5, "2026-07-16T01:50:06.0000000Z"));
        await _runner.RunAsync();

        WriteStore(Entry($"{UserN}:movie:278", UserN, "278", "movie", "Classic", 2, "2026-07-16T01:50:06.0000000Z", "2026-09-02T12:00:00.0000000Z"));
        await _runner.RunAsync();

        Assert.Equal(2, _letterboxd.Posts.Count);
        Assert.Equal(2.0, _letterboxd.Posts[1].Rating);
    }

    // ── Failure handling ────────────────────────────────────────────────────────

    [Fact]
    public async Task ProviderFailure_IsRecordedAndRetriedUpToTheCap()
    {
        _config.EnhancedReviewSyncMaxAttempts = 2;
        AddLetterboxdAccount(UserN, "sample-user");
        _letterboxd.PostException = new Exception("Letterboxd said no");
        WriteStore(Entry($"{UserN}:movie:278", UserN, "278", "movie", "", 5, "2026-07-16T01:50:06.0000000Z"));

        var first = await _runner.RunAsync();
        Assert.Equal(1, first.Failed);
        Assert.Equal(0, first.Posted);
        Assert.NotEmpty(first.Errors);
        Assert.Equal(1, _letterboxd.PostAttempts);

        var second = await _runner.RunAsync();
        Assert.Equal(1, second.Failed);
        Assert.Equal(2, _letterboxd.PostAttempts);

        // Budget exhausted: a third run leaves it alone rather than hammering the provider.
        var third = await _runner.RunAsync();
        Assert.Equal(1, third.UpToDate);
        Assert.Equal(0, third.Failed);
        Assert.Equal(2, _letterboxd.PostAttempts);
        Assert.Equal(2, EnhancedReviewSyncState.Get($"{UserN}:movie:278")!.Attempts);
    }

    [Fact]
    public async Task TwoAccounts_BothReceiveTheSameReview()
    {
        AddLetterboxdAccount(UserN, "primary");
        AddLetterboxdAccount(UserN, "secondary");
        WriteStore(Entry($"{UserN}:movie:278", UserN, "278", "movie", "", 5, "2026-07-16T01:50:06.0000000Z"));

        var summary = await _runner.RunAsync();

        Assert.Equal(1, summary.Posted);
        Assert.Equal(2, _letterboxd.Posts.Count);
        Assert.Equal(new[] { "primary", "secondary" }, _letterboxdLogins);
    }

    [Fact]
    public async Task OneAccountRejectingTheOtherSucceeding_CountsAsPostedAndIsNotRetried()
    {
        AddLetterboxdAccount(UserN, "primary");
        AddLetterboxdAccount(UserN, "secondary");
        // Second account in the fan-out fails.
        _letterboxd.FailOnAttempt = 2;
        WriteStore(Entry($"{UserN}:movie:278", UserN, "278", "movie", "", 5, "2026-07-16T01:50:06.0000000Z"));

        var summary = await _runner.RunAsync();

        Assert.Equal(1, summary.Posted);
        Assert.Equal(0, summary.Failed);
        Assert.Equal(2, _letterboxd.PostAttempts);

        // Not retried: the review did land, so a re-post would only risk a duplicate.
        var second = await _runner.RunAsync();
        Assert.Equal(1, second.UpToDate);
        Assert.Equal(2, _letterboxd.PostAttempts);
    }

    // ── Configuration gates ─────────────────────────────────────────────────────

    [Fact]
    public async Task DisabledIntegration_DoesNothing()
    {
        _config.EnhancedReviewSyncEnabled = false;
        AddLetterboxdAccount(UserN, "sample-user");
        WriteStore(Entry($"{UserN}:movie:278", UserN, "278", "movie", "", 5, "2026-07-16T01:50:06.0000000Z"));

        var summary = await _runner.RunAsync();

        Assert.True(summary.SkippedDisabled);
        Assert.Empty(_letterboxd.Posts);
        Assert.Empty(_letterboxdLogins);
    }

    [Fact]
    public async Task NoBackfill_OnlyConsidersReviewsWrittenAfterTheCutoff()
    {
        _config.EnhancedReviewSyncBackfill = false;
        AddLetterboxdAccount(UserN, "sample-user");

        // A cutoff already captured on an earlier run.
        EnhancedReviewSyncState.EnsureSinceUtc(new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));

        WriteStore(
            Entry($"{UserN}:movie:278", UserN, "278", "movie", "Old", 5, "2026-01-05T00:00:00.0000000Z"),
            Entry($"{UserN}:movie:142", UserN, "142", "movie", "New", 4, "2026-08-05T00:00:00.0000000Z"));

        var summary = await _runner.RunAsync();

        Assert.Equal(1, summary.Considered);
        var post = Assert.Single(_letterboxd.Posts);
        Assert.Equal(142, post.TmdbId);
    }

    [Fact]
    public async Task BackfillOn_ConsidersEveryReview()
    {
        AddLetterboxdAccount(UserN, "sample-user");
        WriteStore(
            Entry($"{UserN}:movie:278", UserN, "278", "movie", "Old", 5, "2026-01-05T00:00:00.0000000Z"),
            Entry($"{UserN}:movie:142", UserN, "142", "movie", "New", 4, "2026-08-05T00:00:00.0000000Z"));

        var summary = await _runner.RunAsync();

        Assert.Equal(2, summary.Considered);
        Assert.Equal(2, summary.Posted);
    }

    [Fact]
    public async Task MissingStore_IsReportedAsAbsentAndDoesNotFail()
    {
        // No store written at all.
        var summary = await _runner.RunAsync();

        Assert.False(summary.StorePresent);
        Assert.Null(summary.Error);
        Assert.Equal(0, summary.Considered);
    }

    [Fact]
    public async Task UnreadableStore_IsReportedAndDoesNotThrow()
    {
        File.WriteAllText(_storePath, "not json at all");

        var summary = await _runner.RunAsync();

        Assert.True(summary.StorePresent);
        Assert.NotNull(summary.Error);
        Assert.Equal(0, summary.Posted);
    }

    [Fact]
    public async Task UnsupportedEntries_DoNotBlockTheSupportedOnes()
    {
        AddLetterboxdAccount(UserN, "sample-user");
        AddSerializdAccount(UserN, "iven@example.com");
        WriteStore(
            EnhancedReviewUnitTests.JeEntry("nonsense", UserN, "1", "movie", "", 3, "2026-07-16T01:50:06.0000000Z"),
            EnhancedReviewUnitTests.JeEntry($"{UserN}:movie:278", UserN, "278", "movie", "", 5, "2026-07-16T01:50:06.0000000Z"),
            EnhancedReviewUnitTests.JeEntry($"{UserN}:tv:1396:s1", UserN, "1396:s1", "tv", "", 5, "2026-07-16T01:50:06.0000000Z"));

        var summary = await _runner.RunAsync();

        Assert.Equal(2, summary.Considered);   // the unparseable key was dropped at parse time
        Assert.Equal(1, summary.Posted);
        Assert.Equal(1, summary.Skipped);
        Assert.Single(_letterboxd.Posts);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private void AddLetterboxdAccount(string userId, string username, bool enabled = true)
        => _config.Accounts.Add(new Account
        {
            UserJellyfinId = userId,
            LetterboxdUsername = username,
            LetterboxdPassword = "secret",
            Enabled = enabled
        });

    private void AddSerializdAccount(string userId, string email, bool enabled = true)
        => _config.SerializdAccounts.Add(new SerializdAccount
        {
            UserJellyfinId = userId,
            Email = email,
            Password = "secret",
            Enabled = enabled
        });

    private void WriteStore(params string[] entries)
        => File.WriteAllText(_storePath, EnhancedReviewUnitTests.JeStore(entries));

    private static string Entry(
        string key, string userId, string tmdbId, string mediaType, string content, double? rating,
        string created, string updated = "")
        => EnhancedReviewUnitTests.JeEntry(key, userId, tmdbId, mediaType, content, rating, created, updated);

    internal sealed record PostedReview(string Slug, string? Text, double? Rating, string? Date, int? TmdbId);

    internal sealed record PostedShowReview(int TmdbId, int? Rating, string? Text, bool ContainsSpoiler);

    internal sealed record PostedEpisodeReview(
        int TmdbId, int SeasonNumber, int EpisodeNumber, int? Rating, string? Text, bool ContainsSpoiler);

    internal sealed class FakeLetterboxdService : ILetterboxdService
    {
        public List<PostedReview> Posts { get; } = new();

        /// <summary>Every call to PostReviewAsync, including ones that were made to throw —
        /// the retry-budget tests need attempts, not just successes.</summary>
        public int PostAttempts { get; private set; }

        public Exception? PostException { get; set; }

        /// <summary>1-based attempt number that should throw, for partial-fan-out tests.</summary>
        public int? FailOnAttempt { get; set; }

        public Task AuthenticateAsync(string username, string password, string? rawCookies = null)
            => Task.CompletedTask;

        public Task<FilmResult> LookupFilmByTmdbIdAsync(int tmdbId)
            => Task.FromResult(new FilmResult($"film-{tmdbId}", $"filmId-{tmdbId}", null));

        public Task<DiaryInfo> GetDiaryInfoAsync(string filmIdOrSlug, string username)
            => Task.FromResult<DiaryInfo>(null!);

        public Task MarkAsWatchedAsync(string filmSlug, string filmId, DateTime? date, bool liked,
            string? productionId = null, bool rewatch = false, double? rating = null)
            => Task.CompletedTask;

        public Task PostReviewAsync(string filmSlug, string? reviewText, bool containsSpoilers = false,
            bool isRewatch = false, string? date = null, double? rating = null, int? tmdbId = null)
        {
            PostAttempts++;

            if (PostException != null) throw PostException;
            if (FailOnAttempt.HasValue && PostAttempts == FailOnAttempt.Value)
                throw new Exception("simulated per-account failure");

            Posts.Add(new PostedReview(filmSlug, reviewText, rating, date, tmdbId));
            return Task.CompletedTask;
        }

        public Task<List<int>> GetWatchlistTmdbIdsAsync(string username)
            => Task.FromResult(new List<int>());

        public Task<List<int>> GetDiaryTmdbIdsAsync(string username)
            => Task.FromResult(new List<int>());

        public Task<List<DiaryFilmEntry>> GetDiaryFilmEntriesAsync(string username)
            => Task.FromResult(new List<DiaryFilmEntry>());

        public void Dispose() { }
    }

    internal sealed class FakeSerializdService : ISerializdService
    {
        public List<PostedShowReview> ShowReviews { get; } = new();
        public List<PostedEpisodeReview> EpisodeReviews { get; } = new();
        public Exception? PostException { get; set; }

        public Task<int?> ResolveSeasonIdAsync(int showTmdbId, int seasonNumber)
            => Task.FromResult<int?>(1);

        public Task LogEpisodesAsync(int showTmdbId, int seasonId, IReadOnlyList<int> episodeNumbers)
            => Task.CompletedTask;

        public Task CreateEpisodeLogAsync(int showTmdbId, int seasonId, int episodeNumber,
            DateTime watchedAtUtc, int? rating, bool isRewatch)
            => Task.CompletedTask;

        public Task UnlogEpisodesAsync(int showTmdbId, int seasonId, IReadOnlyList<int> episodeNumbers)
            => Task.CompletedTask;

        public Task SetShowMetaAsync(int showTmdbId, int? rating, bool like)
            => Task.CompletedTask;

        public Task<System.Collections.Generic.List<SerializdWatchlistEntry>> GetWatchlistAsync()
            => Task.FromResult(new System.Collections.Generic.List<SerializdWatchlistEntry>());

        public Task CreateShowReviewAsync(int showTmdbId, int? rating, string? reviewText, bool containsSpoiler)
        {
            if (PostException != null) throw PostException;
            ShowReviews.Add(new PostedShowReview(showTmdbId, rating, reviewText, containsSpoiler));
            return Task.CompletedTask;
        }

        public Task CreateEpisodeReviewAsync(int showTmdbId, int seasonNumber, int episodeNumber,
            int? rating, string? reviewText, bool containsSpoiler)
        {
            if (PostException != null) throw PostException;
            EpisodeReviews.Add(new PostedEpisodeReview(
                showTmdbId, seasonNumber, episodeNumber, rating, reviewText, containsSpoiler));
            return Task.CompletedTask;
        }

        public Task<System.Collections.Generic.List<SerializdDiaryEpisode>> GetDiaryEpisodesAsync()
            => Task.FromResult(new System.Collections.Generic.List<SerializdDiaryEpisode>());

        public void Dispose() { }
    }
}
