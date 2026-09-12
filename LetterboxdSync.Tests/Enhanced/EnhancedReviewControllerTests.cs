using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using LetterboxdSync;
using LetterboxdSync.Api;
using LetterboxdSync.Configuration;
using LetterboxdSync.Enhanced;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace LetterboxdSync.Tests.Enhanced;

/// <summary>
/// Coverage for the admin surface: what the status endpoint reports about JE's store, and the
/// gating on the manual trigger.
///
/// Deliberately does NOT use ControllerTestHarness. That harness builds a real Plugin, and
/// Plugin.Instance is a process-wide singleton, so every extra class constructing one widens an
/// existing race with the other harness-based classes (they can observe each other's fresh
/// Plugin mid-test). This controller reads its config through
/// EnhancedReviewSyncRunner.ResolveConfiguration, which these tests override — so no Plugin is
/// needed here at all.
/// </summary>
[Collection("EnhancedReviewSync")]
public class EnhancedReviewControllerTests : IDisposable
{
    private const string UserN = "deadbeefdeadbeefdeadbeefdeadbeef";

    private readonly string _tempDir;
    private readonly string _storePath;
    private readonly PluginConfiguration _config;
    private readonly EnhancedReviewSyncRunner _runner;
    private readonly EnhancedReviewController _controller;

    public EnhancedReviewControllerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "lbs-enhanced-ctl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _storePath = Path.Combine(_tempDir, "reviews.json");

        _config = new PluginConfiguration();
        EnhancedReviewStore.PathOverrideForTesting = _storePath;
        EnhancedReviewSyncState.PathOverrideForTesting = Path.Combine(_tempDir, "state.json");
        EnhancedReviewSyncState.ResetForTesting();
        EnhancedReviewSyncRunner.ConfigurationOverrideForTesting = () => _config;
        EnhancedReviewSyncRunner.ResetLastRunForTesting();
        EnhancedReviewEntry.DiaryTimeZoneOverrideForTesting = EnhancedReviewUnitTests.UtcPlus8;
        EnhancedReviewSyncRunner.LetterboxdFactoryForTesting = (_, _, _, _, _) =>
            Task.FromResult<ILetterboxdService>(new NoopLetterboxdService());

        _runner = new EnhancedReviewSyncRunner(
            NullLogger<EnhancedReviewSyncRunner>.Instance, Substitute.For<IUserManager>());

        _controller = new EnhancedReviewController(
            NullLogger<EnhancedReviewController>.Instance, _runner);
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
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void Status_WhenDisabled_ReportsDisabledWithNothingPending()
    {
        _config.EnhancedReviewSyncEnabled = false;
        WriteStore(
            Entry($"{UserN}:movie:278", "278", "movie"),
            Entry($"{UserN}:tv:1396:s1", "1396:s1", "tv"));

        var status = Status();

        Assert.False(status.Enabled);
        Assert.True(status.StorePresent);
        Assert.Equal(2, status.StoreEntries);
        Assert.Equal(2, status.Pending);      // nothing attempted yet
        Assert.Equal(0, status.Synced);
    }

    [Fact]
    public void Status_WhenStoreIsAbsent_SaysSoInsteadOfErroring()
    {
        _config.EnhancedReviewSyncEnabled = true;

        var status = Status();

        Assert.True(status.Enabled);
        Assert.False(status.StorePresent);
        Assert.Null(status.StoreError);
        Assert.Equal(0, status.StoreEntries);
        Assert.Equal(_storePath, status.StorePath);
    }

    [Fact]
    public void Status_ReportsPendingSyncedAndPerEntryDetail()
    {
        _config.EnhancedReviewSyncEnabled = true;
        WriteStore(
            Entry($"{UserN}:movie:278", "278", "movie"),
            Entry($"{UserN}:movie:142", "142", "movie"));

        // Record a real post using the store's own fingerprint, so this also proves the
        // fingerprint the state dedupes on is the one the store produces.
        var movieKey = $"{UserN}:movie:278";
        var parsed = EnhancedReviewStore.Load(null).Entries
            .Single(e => e.Key.CompositeId == movieKey);
        EnhancedReviewSyncState.Record(
            movieKey, parsed.Fingerprint, EnhancedReviewSyncState.SyncedStatus, "letterboxd", null);

        var status = Status();

        Assert.Equal(2, status.StoreEntries);
        Assert.Equal(1, status.Synced);
        Assert.Equal(1, status.Pending);      // the second entry has never been attempted
        Assert.Single(status.Entries);
        Assert.Equal(movieKey, status.Entries[0].Key);
        Assert.Equal("letterboxd", status.Entries[0].Reason);
    }

    [Fact]
    public void SyncNow_WhenDisabled_IsRejected()
    {
        _config.EnhancedReviewSyncEnabled = false;

        Assert.IsType<BadRequestObjectResult>(_controller.SyncNow());
    }

    [Fact]
    public async Task SyncNow_WhenEnabled_AcceptsAndRunsTheSync()
    {
        _config.EnhancedReviewSyncEnabled = true;
        _config.Accounts.Add(new Account
        {
            UserJellyfinId = UserN,
            LetterboxdUsername = "sample-user",
            LetterboxdPassword = "secret",
            Enabled = true
        });

        WriteStore(Entry($"{UserN}:movie:278", "278", "movie"));

        Assert.IsType<AcceptedResult>(_controller.SyncNow());

        await _controller.LastBackgroundSync!;

        Assert.NotNull(EnhancedReviewSyncRunner.LastRun);
        Assert.Equal(1, EnhancedReviewSyncRunner.LastRun!.Posted);
    }

    [Fact]
    public void StatusPayload_UsesThePropertyNamesTheDashboardReads()
    {
        // configPage.html's loadEnhancedReviewStatus reads these names verbatim, and Jellyfin's
        // MVC pipeline does not camel-case this model. A rename, or a naming policy added later,
        // would silently blank the dashboard panel — so assert the wire contract, not just the
        // C# property names.
        var json = JsonSerializer.Serialize(Status());
        using var doc = JsonDocument.Parse(json);
        var names = doc.RootElement.EnumerateObject().Select(p => p.Name).ToList();

        foreach (var expected in new[]
                 {
                     "Enabled", "Backfill", "MaxAttempts", "StorePath", "StorePresent", "StoreReadable",
                     "StoreError", "StoreEntries", "DiaryTimeZone", "DiaryTimeZoneOffsetMinutes",
                     "OutOfScope", "SinceUtc", "Pending", "Synced", "Skipped", "Failed",
                     "IsRunning", "LastRunCompletedUtc", "LastRun", "LastRunErrors", "Entries"
                 })
        {
            Assert.Contains(expected, names);
        }
    }

    [Fact]
    public void Status_WithBackfillOff_LeavesPreCutoffReviewsOutOfScope()
    {
        _config.EnhancedReviewSyncEnabled = true;
        _config.EnhancedReviewSyncBackfill = false;

        // The first run establishes the cutoff at "now"; these entries were written in July.
        EnhancedReviewSyncState.EnsureSinceUtc(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));

        WriteStore(
            Entry($"{UserN}:movie:278", "278", "movie"),
            Entry($"{UserN}:movie:142", "142", "movie"));

        var status = Status();

        Assert.Equal(2, status.StoreEntries);
        Assert.Equal(2, status.OutOfScope);
        Assert.Equal(0, status.Pending);
        Assert.Equal(0, status.Synced);
    }

    [Fact]
    public void Status_WithBackfillOffAndNoCutoffYet_PromisesNothing()
    {
        // No run has happened, so no cutoff exists. The next run sets one to "now", which means
        // nothing already in the store can post — the dashboard must not claim otherwise.
        _config.EnhancedReviewSyncEnabled = true;
        _config.EnhancedReviewSyncBackfill = false;

        WriteStore(Entry($"{UserN}:movie:278", "278", "movie"));

        var status = Status();

        Assert.Null(status.SinceUtc);
        Assert.Equal(1, status.OutOfScope);
        Assert.Equal(0, status.Pending);
    }

    [Fact]
    public void Status_WithBackfillOn_BringsTheCutoffEntriesBackIntoScope()
    {
        // Turning backfill on later is the whole point of the cutoff: the reviews held back while
        // proving the integration must become pending again, not stay stranded.
        _config.EnhancedReviewSyncEnabled = true;
        _config.EnhancedReviewSyncBackfill = true;
        EnhancedReviewSyncState.EnsureSinceUtc(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));

        WriteStore(
            Entry($"{UserN}:movie:278", "278", "movie"),
            Entry($"{UserN}:tv:1396", "1396", "tv"));

        var status = Status();

        Assert.Equal(0, status.OutOfScope);
        Assert.Equal(2, status.Pending);
        Assert.Equal(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), status.SinceUtc);
    }

    [Fact]
    public void Status_ReportsTheZoneDiaryDatesAreComputedIn()
    {
        _config.EnhancedReviewSyncEnabled = true;

        var status = Status();

        // A diary date is a local-calendar concept. If this reports the wrong zone, entries land on
        // the wrong day and nothing else in the plugin's own output would show it.
        Assert.Equal(EnhancedReviewUnitTests.UtcPlus8.Id, status.DiaryTimeZone);
        Assert.Equal(480, status.DiaryTimeZoneOffsetMinutes);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private EnhancedReviewStatus Status()
    {
        var result = Assert.IsType<OkObjectResult>(_controller.GetStatus().Result);
        return Assert.IsType<EnhancedReviewStatus>(result.Value);
    }

    private void WriteStore(params string[] entries)
        => File.WriteAllText(_storePath, EnhancedReviewUnitTests.JeStore(entries));

    private static string Entry(string key, string tmdbId, string mediaType)
        => EnhancedReviewUnitTests.JeEntry(
            key, UserN, tmdbId, mediaType, string.Empty, 5, "2026-07-16T01:50:06.0000000Z");

    private sealed class NoopLetterboxdService : ILetterboxdService
    {
        public Task AuthenticateAsync(string username, string password, string? rawCookies = null)
            => Task.CompletedTask;

        public Task<FilmResult> LookupFilmByTmdbIdAsync(int tmdbId)
            => Task.FromResult(new FilmResult($"film-{tmdbId}", $"filmId-{tmdbId}", null));

        public Task<DiaryInfo> GetDiaryInfoAsync(string filmIdOrSlug, string username)
            => Task.FromResult(new DiaryInfo(null, false));

        public bool SupportsLogEntryEditing => true;

        public Task<string?> FindLogEntryIdAsync(string filmIdOrSlug, DateTime date)
            => Task.FromResult<string?>(null);

        public Task UpdateLogEntryAsync(string logEntryId, string? reviewText, bool containsSpoilers, double? rating)
            => Task.CompletedTask;

        public Task MarkAsWatchedAsync(string filmSlug, string filmId, DateTime? date, bool liked,
            string? productionId = null, bool rewatch = false, double? rating = null)
            => Task.CompletedTask;

        public Task PostReviewAsync(string filmSlug, string? reviewText, bool containsSpoilers = false,
            bool isRewatch = false, string? date = null, double? rating = null, int? tmdbId = null)
            => Task.CompletedTask;

        public Task<System.Collections.Generic.List<int>> GetWatchlistTmdbIdsAsync(string username)
            => Task.FromResult(new System.Collections.Generic.List<int>());

        public Task<System.Collections.Generic.List<int>> GetDiaryTmdbIdsAsync(string username)
            => Task.FromResult(new System.Collections.Generic.List<int>());

        public Task<System.Collections.Generic.List<DiaryFilmEntry>> GetDiaryFilmEntriesAsync(string username)
            => Task.FromResult(new System.Collections.Generic.List<DiaryFilmEntry>());

        public void Dispose() { }
    }
}
