using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using LetterboxdSync;
using LetterboxdSync.Configuration;
using LetterboxdSync.Enhanced;
using LetterboxdSync.Serializd;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
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
        EnhancedReviewEntry.DiaryTimeZoneOverrideForTesting = EnhancedReviewUnitTests.UtcPlus8;

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
        EnhancedReviewEntry.DiaryTimeZoneOverrideForTesting = null;
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

    [Fact]
    public async Task Review_ForAFilmAlreadyOnTheDiary_AttachesInsteadOfPostingASecondEntry()
    {
        AddLetterboxdAccount(UserN, "sample-user");
        WriteStore(Entry($"{UserN}:movie:278", UserN, "278", "movie", "Classic", 5, "2026-07-16T01:50:06.0000000Z"));

        // The film is already on the diary for the day the review carries.
        _letterboxd.ExistingEntries[new DateTime(2026, 7, 16)] = "entry-1";

        var summary = await _runner.RunAsync();

        Assert.Equal(1, summary.Attached);
        Assert.Equal(0, summary.Posted);
        Assert.Empty(_letterboxd.Posts);                 // no second diary entry
        var update = Assert.Single(_letterboxd.Updates);
        Assert.Equal("entry-1", update.EntryId);
        Assert.Equal("Classic", update.Text);
        Assert.Equal(5, update.Rating);
        Assert.Equal(EnhancedReviewSyncState.SyncedStatus,
            EnhancedReviewSyncState.Get($"{UserN}:movie:278")!.Status);
    }

    [Fact]
    public async Task Review_ForAFilmNotOnTheDiary_StillCreatesAnEntry()
    {
        AddLetterboxdAccount(UserN, "sample-user");
        WriteStore(Entry($"{UserN}:movie:278", UserN, "278", "movie", "Classic", 5, "2026-07-16T01:50:06.0000000Z"));

        var summary = await _runner.RunAsync();

        Assert.Equal(1, summary.Posted);
        Assert.Equal(0, summary.Attached);
        Assert.Empty(_letterboxd.Updates);
        Assert.Equal("2026-07-16", Assert.Single(_letterboxd.Posts).Date);
    }

    [Fact]
    public async Task DiaryDate_PrefersTheJellyfinWatchDateOverTheDateTheReviewWasWritten()
    {
        AddLetterboxdAccount(UserN, "sample-user");
        WriteStore(Entry($"{UserN}:movie:278", UserN, "278", "movie", "Classic", 5, "2026-07-16T01:50:06.0000000Z"));

        // Watched on the 22nd, reviewed on the 16th: the diary entry belongs on the 22nd, or the
        // review lands on its own day and the film reads as watched twice.
        var summary = await RunnerWithWatchDate(278, new DateTime(2026, 7, 22)).RunAsync();

        Assert.Equal(1, summary.Posted);
        Assert.Equal("2026-07-22", Assert.Single(_letterboxd.Posts).Date);
    }

    [Fact]
    public async Task WatchDate_ThatIsAlreadyOnTheDiary_AttachesToThatViewing()
    {
        AddLetterboxdAccount(UserN, "sample-user");
        WriteStore(Entry($"{UserN}:movie:278", UserN, "278", "movie", "Classic", 5, "2026-07-16T01:50:06.0000000Z"));
        _letterboxd.ExistingEntries[new DateTime(2026, 7, 22)] = "entry-22";

        var summary = await RunnerWithWatchDate(278, new DateTime(2026, 7, 22)).RunAsync();

        Assert.Equal(1, summary.Attached);
        Assert.Equal("entry-22", Assert.Single(_letterboxd.Updates).EntryId);
    }

    [Fact]
    public async Task ServiceThatCannotEditEntries_SkipsRatherThanLoggingTheFilmTwice()
    {
        AddLetterboxdAccount(UserN, "sample-user");
        WriteStore(Entry($"{UserN}:movie:278", UserN, "278", "movie", "Classic", 5, "2026-07-16T01:50:06.0000000Z"));

        // The scraping fallback can't edit an entry, but it can see the date is already logged.
        _letterboxd.SupportsLogEntryEditing = false;
        _letterboxd.DiaryInfoResult = new DiaryInfo(new DateTime(2026, 7, 16), true);

        var summary = await _runner.RunAsync();

        Assert.Equal(1, summary.AlreadyLogged);
        Assert.Equal(0, summary.Posted);
        Assert.Empty(_letterboxd.Posts);
        Assert.Empty(_letterboxd.Updates);
        Assert.Equal(EnhancedReviewSyncState.SkippedStatus,
            EnhancedReviewSyncState.Get($"{UserN}:movie:278")!.Status);
    }

    [Fact]
    public async Task DiaryDate_UsesTheLocalDayOfTheWatch()
    {
        AddLetterboxdAccount(UserN, "sample-user");
        WriteStore(Entry($"{UserN}:movie:558", UserN, "558", "movie", "", 5, "2026-07-16T01:50:06.0000000Z"));

        // 23:54 UTC on Aug 7 is 07:54 on Aug 8 in UTC+08 — the day the film was actually watched.
        var summary = await RunnerWithWatchDate(558, new DateTime(2026, 8, 7, 23, 54, 0, DateTimeKind.Utc))
            .RunAsync();

        Assert.Equal(1, summary.Posted);
        Assert.Equal("2026-08-08", Assert.Single(_letterboxd.Posts).Date);
    }

    [Fact]
    public async Task EntryOnTheUtcDateOfALateNightWatch_IsRecognisedAndAttachedTo()
    {
        AddLetterboxdAccount(UserN, "sample-user");
        WriteStore(Entry($"{UserN}:movie:558", UserN, "558", "movie", "", 5, "2026-07-16T01:50:06.0000000Z"));

        // The plugin's daily catch-up logs viewings by the UTC date, which for a late-night watch
        // is the day before the local one. Without checking both, this review would post a second
        // entry for a viewing that is already on the diary.
        _letterboxd.ExistingEntries[new DateTime(2026, 8, 7)] = "entry-utc";

        var summary = await RunnerWithWatchDate(558, new DateTime(2026, 8, 7, 23, 54, 0, DateTimeKind.Utc))
            .RunAsync();

        Assert.Equal(1, summary.Attached);
        Assert.Equal(0, summary.Posted);
        Assert.Empty(_letterboxd.Posts);
        Assert.Equal("entry-utc", Assert.Single(_letterboxd.Updates).EntryId);
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

    /// <summary>
    /// A runner that can see Jellyfin watch dates: one played movie with the given TMDb id,
    /// watched on the given date. Everything else about the run is unchanged.
    /// </summary>
    private EnhancedReviewSyncRunner RunnerWithWatchDate(int tmdbId, DateTime watchDate)
    {
        var user = new User("sample-user", "test-provider-id", "test-reset-id");

        var movie = new Movie { Name = $"movie-{tmdbId}" };
        movie.SetProviderId(MetadataProvider.Tmdb, tmdbId.ToString(CultureInfo.InvariantCulture));

        var userManager = Substitute.For<IUserManager>();
        userManager.GetUserById(Arg.Any<Guid>()).Returns(user);

        var library = Substitute.For<ILibraryManager>();
        library.GetItemList(Arg.Any<MediaBrowser.Controller.Entities.InternalItemsQuery>())
            .Returns(new List<BaseItem> { movie });

        var userData = Substitute.For<IUserDataManager>();
        userData.GetUserData(Arg.Any<User>(), Arg.Any<BaseItem>())
            .Returns(new UserItemData { Key = $"test-{tmdbId}", LastPlayedDate = watchDate });

        return new EnhancedReviewSyncRunner(
            NullLogger<EnhancedReviewSyncRunner>.Instance, userManager, library, userData);
    }

    private static string Entry(
        string key, string userId, string tmdbId, string mediaType, string content, double? rating,
        string created, string updated = "")
        => EnhancedReviewUnitTests.JeEntry(key, userId, tmdbId, mediaType, content, rating, created, updated);

    internal sealed record PostedReview(string Slug, string? Text, double? Rating, string? Date, int? TmdbId);

    internal sealed record UpdatedLogEntry(string EntryId, string? Text, double? Rating, bool ContainsSpoilers);

    internal sealed record PostedShowReview(int TmdbId, int? Rating, string? Text, bool ContainsSpoiler);

    internal sealed record PostedEpisodeReview(
        int TmdbId, int SeasonNumber, int EpisodeNumber, int? Rating, string? Text, bool ContainsSpoiler);

    internal sealed class FakeLetterboxdService : ILetterboxdService
    {
        public List<PostedReview> Posts { get; } = new();

        /// <summary>Edits made to existing diary entries, in call order.</summary>
        public List<UpdatedLogEntry> Updates { get; } = new();

        /// <summary>Entry ids the fake reports as already on the diary, keyed by diary date.</summary>
        public Dictionary<DateTime, string> ExistingEntries { get; } = new();

        /// <summary>What GetDiaryInfoAsync reports — used by the can't-edit fallback path.</summary>
        public DiaryInfo DiaryInfoResult { get; set; } = new(null, false);

        /// <summary>The API client can edit; the scraping fallback cannot.</summary>
        public bool SupportsLogEntryEditing { get; set; } = true;

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
            => Task.FromResult(DiaryInfoResult);

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

        public Task<string?> FindLogEntryIdAsync(string filmIdOrSlug, DateTime date)
            => Task.FromResult(ExistingEntries.TryGetValue(date.Date, out var id) ? id : null);

        public Task UpdateLogEntryAsync(string logEntryId, string? reviewText, bool containsSpoilers, double? rating)
        {
            Updates.Add(new UpdatedLogEntry(logEntryId, reviewText, rating, containsSpoilers));
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
