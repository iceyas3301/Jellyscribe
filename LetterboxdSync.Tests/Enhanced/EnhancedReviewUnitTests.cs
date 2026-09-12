using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using LetterboxdSync;
using LetterboxdSync.Enhanced;
using Xunit;

namespace LetterboxdSync.Tests.Enhanced;

/// <summary>
/// Pure unit coverage for the Jellyfin Enhanced review sync: key parsing, the rating/date
/// mapping, tolerant store reads, and the dedupe state machine.
///
/// Serialised into one collection: these tests mutate the statics the sync uses for file
/// paths, so they must not interleave.
/// </summary>
[Collection("EnhancedReviewSync")]
public class EnhancedReviewUnitTests : IDisposable
{
    private const string UserN = "deadbeefdeadbeefdeadbeefdeadbeef";
    private const string OtherUserN = "feedfacefeedfacefeedfacefeedface";

    private readonly string _tempDir;

    public EnhancedReviewUnitTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "lbs-enhanced-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        EnhancedReviewStore.PathOverrideForTesting = null;
        EnhancedReviewSyncState.PathOverrideForTesting = Path.Combine(_tempDir, "state.json");
        EnhancedReviewSyncState.ResetForTesting();
    }

    public void Dispose()
    {
        EnhancedReviewStore.PathOverrideForTesting = null;
        EnhancedReviewSyncState.PathOverrideForTesting = null;
        EnhancedReviewSyncState.ResetForTesting();
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    // ── Key parsing ─────────────────────────────────────────────────────────────

    [Fact]
    public void TryParse_MovieKey_YieldsMovieScope()
    {
        var key = EnhancedReviewKey.TryParse($"{UserN}:movie:278");

        Assert.NotNull(key);
        Assert.Equal(EnhancedReviewScope.Movie, key!.Scope);
        Assert.True(key.IsMovie);
        Assert.Equal(278, key.TmdbId);
        Assert.Equal(UserN, key.UserIdN);
        Assert.Equal($"{UserN}:movie:278", key.CompositeId);
    }

    [Fact]
    public void TryParse_ShowKey_YieldsShowScope()
    {
        var key = EnhancedReviewKey.TryParse($"{UserN}:tv:94605");

        Assert.NotNull(key);
        Assert.Equal(EnhancedReviewScope.Show, key!.Scope);
        Assert.Null(key.SeasonNumber);
        Assert.Null(key.EpisodeNumber);
        Assert.False(key.IsMovie);
    }

    [Fact]
    public void TryParse_SeasonKey_YieldsSeasonScope()
    {
        var key = EnhancedReviewKey.TryParse($"{UserN}:tv:262375:s1");

        Assert.NotNull(key);
        Assert.Equal(EnhancedReviewScope.Season, key!.Scope);
        Assert.Equal(1, key.SeasonNumber);
        Assert.Null(key.EpisodeNumber);
        Assert.Equal($"{UserN}:tv:262375:s1", key.CompositeId);
    }

    [Fact]
    public void TryParse_EpisodeKey_YieldsEpisodeScope()
    {
        var key = EnhancedReviewKey.TryParse($"{UserN}:tv:1396:s2:e7");

        Assert.NotNull(key);
        Assert.Equal(EnhancedReviewScope.Episode, key!.Scope);
        Assert.Equal(2, key.SeasonNumber);
        Assert.Equal(7, key.EpisodeNumber);
        Assert.Equal($"{UserN}:tv:1396:s2:e7", key.CompositeId);
    }

    [Theory]
    [InlineData("")]                                           // empty
    [InlineData("   ")]                                        // whitespace
    [InlineData("not-a-key")]                                  // no separators
    [InlineData("movie:278")]                                  // too few parts
    [InlineData("abc:movie:278")]                              // user id is not a GUID
    [InlineData($"{UserN}:book:278")]                          // unsupported media type
    [InlineData($"{UserN}:movie:0")]                           // non-positive TMDb id
    [InlineData($"{UserN}:movie:abc")]                         // non-numeric TMDb id
    [InlineData($"{UserN}:movie:278:s1")]                      // a film cannot have a season
    [InlineData($"{UserN}:tv:1396:s1:e2:e3")]                  // too many parts
    [InlineData($"{UserN}:tv:1396:x1")]                        // wrong season prefix
    [InlineData($"{UserN}:tv:1396:s")]                         // season number missing
    [InlineData($"{UserN}:tv:1396:0")]                         // season zero
    [InlineData($"{UserN}:tv:1396:s1:e")]                      // episode number missing
    [InlineData($"{UserN}:tv:1396:e5")]                        // episode without a season
    public void TryParse_RejectsMalformedKeys(string raw)
        => Assert.Null(EnhancedReviewKey.TryParse(raw));

    [Fact]
    public void NormalizeUserKey_AcceptsDashedAndNForms()
    {
        var dashed = "deadbeef-dead-beef-dead-beefdeadbeef";

        Assert.Equal(UserN, EnhancedReviewKey.NormalizeUserKey(dashed));
        Assert.Equal(UserN, EnhancedReviewKey.NormalizeUserKey(UserN.ToUpperInvariant()));
        Assert.Equal(UserN, EnhancedReviewKey.NormalizeUserKey($"  {UserN}  "));
        Assert.Null(EnhancedReviewKey.NormalizeUserKey("not-a-guid"));
        Assert.Null(EnhancedReviewKey.NormalizeUserKey(null));
    }

    // ── Rating / date mapping ───────────────────────────────────────────────────

    [Theory]
    [InlineData(5.0, 5.0, 10)]
    [InlineData(3.5, 3.5, 7)]
    [InlineData(2.0, 2.0, 4)]
    [InlineData(0.5, 0.5, 1)]
    [InlineData(4.5, 4.5, 9)]
    public void Entry_MapsJeRatingToBothServices(double je, double letterboxd, int serializd)
    {
        var entry = Entry(rating: je);

        Assert.Equal(letterboxd, entry.LetterboxdRating);
        Assert.Equal(serializd, entry.SerializdRating);
    }

    [Fact]
    public void Entry_Unrated_MapsToNull()
    {
        var entry = Entry(rating: null);

        Assert.Null(entry.LetterboxdRating);
        Assert.Null(entry.SerializdRating);
        Assert.False(entry.HasText);
    }

    [Fact]
    public void Entry_DiaryDate_UsesJeCreationDateNotNow()
    {
        var entry = Entry(created: "2026-07-16T20:51:13.0000000Z");

        Assert.Equal(new DateTime(2026, 7, 16), entry.DiaryDateUtc);
    }

    [Fact]
    public void Entry_Fingerprint_ChangesWhenContentRatingOrEditTimeChange()
    {
        var baseline = Entry(content: "Classic", rating: 5, updated: "2026-07-16T20:51:13.0000000Z");

        Assert.Equal(baseline.Fingerprint, Entry(content: "Classic", rating: 5, updated: "2026-07-16T20:51:13.0000000Z").Fingerprint);
        Assert.NotEqual(baseline.Fingerprint, Entry(content: "Classic!", rating: 5, updated: "2026-07-16T20:51:13.0000000Z").Fingerprint);
        Assert.NotEqual(baseline.Fingerprint, Entry(content: "Classic", rating: 3, updated: "2026-07-16T20:51:13.0000000Z").Fingerprint);
        Assert.NotEqual(baseline.Fingerprint, Entry(content: "Classic", rating: 5, updated: "2026-08-01T00:00:00.0000000Z").Fingerprint);
    }

    // ── Store reads ─────────────────────────────────────────────────────────────

    [Fact]
    public void Store_ParsesBothRootFieldsAndPerEntryFields()
    {
        var json = JeStore(
            JeEntry($"{UserN}:movie:278", UserN, "278", "movie", "Classic", 5, "2026-07-16T01:50:06.0000000Z"),
            JeEntry($"{UserN}:tv:1396:s2:e7", UserN, "1396:s2:e7", "tv", "", 4, "2026-08-08T02:42:04.0000000Z"));

        var entries = EnhancedReviewStore.Parse(json);

        Assert.Equal(2, entries.Count);

        var movie = entries.Single(e => e.Key.IsMovie);
        Assert.Equal(278, movie.Key.TmdbId);
        Assert.Equal("Classic", movie.Content);
        Assert.Equal(5, movie.Rating);

        var episode = entries.Single(e => e.Key.Scope == EnhancedReviewScope.Episode);
        Assert.Equal(1396, episode.Key.TmdbId);
        Assert.Equal(2, episode.Key.SeasonNumber);
        Assert.Equal(7, episode.Key.EpisodeNumber);
    }

    [Fact]
    public void Store_SkipsUnrecognisedKeysWithoutDroppingTheRest()
    {
        // Identity comes from JE's key, never from the entry's own fields — so a key we can't
        // parse is dropped even when its fields look plausible.
        var json = JeStore(
            JeEntry($"{UserN}:movie:278", UserN, "278", "movie", "", 5, "2026-07-16T01:50:06.0000000Z"),
            JeEntry("garbage-key", UserN, "9", "movie", "", 3, "2026-07-16T01:50:06.0000000Z"),
            JeEntry($"{UserN}:book:5", UserN, "5", "book", "", 3, "2026-07-16T01:50:06.0000000Z"));

        var entries = EnhancedReviewStore.Parse(json);

        Assert.Single(entries);
        Assert.Equal(278, entries[0].Key.TmdbId);
    }

    [Fact]
    public void Store_AcceptsLowercasePayloadFieldNames()
    {
        // JE serialises PascalCase today; the payload fields (text, rating, timestamps) are read
        // case-insensitively so a future JE naming-policy change can't silently blank them out.
        var json = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["reviews"] = new Dictionary<string, object>
            {
                [$"{UserN}:movie:550"] = new Dictionary<string, object>
                {
                    ["userId"] = UserN,
                    ["tmdbId"] = "550",
                    ["mediaType"] = "movie",
                    ["content"] = "Great",
                    ["rating"] = 4,
                    ["createdAt"] = "2026-07-16T01:50:06Z",
                    ["updatedAt"] = "2026-07-16T01:50:06Z"
                }
            }
        });

        var entries = EnhancedReviewStore.Parse(json);

        var entry = Assert.Single(entries);
        Assert.Equal(550, entry.Key.TmdbId);
        Assert.Equal("Great", entry.Content);
        Assert.Equal(4, entry.Rating);
    }

    [Fact]
    public void Store_MissingFile_IsReportedAsAbsentNotBroken()
    {
        EnhancedReviewStore.PathOverrideForTesting = Path.Combine(_tempDir, "does-not-exist.json");

        var result = EnhancedReviewStore.Load(null);

        Assert.False(result.StorePresent);
        Assert.True(result.StoreReadable);
        Assert.Null(result.Error);
        Assert.Empty(result.Entries);
    }

    [Fact]
    public void Store_CorruptFile_ReportsErrorInsteadOfThrowing()
    {
        var path = Path.Combine(_tempDir, "corrupt.json");
        File.WriteAllText(path, "{ this is not json");
        EnhancedReviewStore.PathOverrideForTesting = path;

        var result = EnhancedReviewStore.Load(null);

        Assert.True(result.StorePresent);
        Assert.False(result.StoreReadable);
        Assert.NotNull(result.Error);
        Assert.Empty(result.Entries);
    }

    [Fact]
    public void Store_EmptyFile_YieldsNoEntries()
    {
        var path = Path.Combine(_tempDir, "empty.json");
        File.WriteAllText(path, "");
        EnhancedReviewStore.PathOverrideForTesting = path;

        var result = EnhancedReviewStore.Load(null);

        Assert.True(result.StoreReadable);
        Assert.Empty(result.Entries);
    }

    // ── Dedupe state ────────────────────────────────────────────────────────────

    [Fact]
    public void State_UnseenReview_ShouldBeAttempted()
        => Assert.True(EnhancedReviewSyncState.ShouldAttempt($"{UserN}:movie:278", "fp1", 5));

    [Fact]
    public void State_SyncedReview_IsNotRetried_UntilItsContentChanges()
    {
        var key = $"{UserN}:movie:278";
        EnhancedReviewSyncState.Record(key, "fp1", EnhancedReviewSyncState.SyncedStatus, "letterboxd", null);

        Assert.False(EnhancedReviewSyncState.ShouldAttempt(key, "fp1", 5));
        Assert.True(EnhancedReviewSyncState.ShouldAttempt(key, "fp2", 5));
    }

    [Fact]
    public void State_PermanentSkip_IsNeverRetried()
    {
        var key = $"{UserN}:tv:262375:s1";
        EnhancedReviewSyncState.Record(
            key, "fp1", EnhancedReviewSyncState.SkippedStatus, "serializd-has-no-season-reviews", null);

        Assert.False(EnhancedReviewSyncState.ShouldAttempt(key, "fp1", 5));
    }

    [Fact]
    public void State_FailedReview_RetriesOnlyUpToTheConfiguredCap()
    {
        var key = $"{UserN}:movie:278";

        for (var attempt = 1; attempt <= 5; attempt++)
        {
            Assert.True(EnhancedReviewSyncState.ShouldAttempt(key, "fp1", 5));
            EnhancedReviewSyncState.Record(key, "fp1", EnhancedReviewSyncState.FailedStatus, "letterboxd", "boom");
        }

        Assert.False(EnhancedReviewSyncState.ShouldAttempt(key, "fp1", 5));
        Assert.Equal(5, EnhancedReviewSyncState.Get(key)!.Attempts);
    }

    [Fact]
    public void State_EditAfterFailures_RestartsTheRetryBudget()
    {
        var key = $"{UserN}:movie:278";
        EnhancedReviewSyncState.Record(key, "fp1", EnhancedReviewSyncState.FailedStatus, "letterboxd", "boom");

        Assert.True(EnhancedReviewSyncState.ShouldAttempt(key, "fp2", 5));
        EnhancedReviewSyncState.Record(key, "fp2", EnhancedReviewSyncState.FailedStatus, "letterboxd", "boom");

        Assert.Equal(1, EnhancedReviewSyncState.Get(key)!.Attempts);
    }

    [Fact]
    public void State_PersistsAcrossReloads()
    {
        var key = $"{UserN}:movie:278";
        EnhancedReviewSyncState.Record(key, "fp1", EnhancedReviewSyncState.SyncedStatus, "letterboxd", null);
        EnhancedReviewSyncState.ResetForTesting();

        var reloaded = EnhancedReviewSyncState.Get(key);

        Assert.NotNull(reloaded);
        Assert.Equal(EnhancedReviewSyncState.SyncedStatus, reloaded!.Status);

        var counts = EnhancedReviewSyncState.GetCounts();
        Assert.Equal(1, counts.Synced);
        Assert.Equal(0, counts.Failed);
    }

    [Fact]
    public void State_SinceUtc_IsEstablishedOnceAndKept()
    {
        var first = new DateTimeOffset(2026, 9, 12, 10, 0, 0, TimeSpan.Zero);
        Assert.Equal(first, EnhancedReviewSyncState.EnsureSinceUtc(first));

        EnhancedReviewSyncState.ResetForTesting();

        // A later call must return the stored value, not the new one — otherwise turning
        // backfill off and on would re-post history.
        Assert.Equal(first, EnhancedReviewSyncState.EnsureSinceUtc(first.AddDays(30)));
    }

    [Fact]
    public void State_CorruptStateFile_DoesNotBlockTheSync()
    {
        var path = EnhancedReviewSyncState.PathOverrideForTesting!;
        File.WriteAllText(path, "{ nonsense");
        EnhancedReviewSyncState.ResetForTesting();

        Assert.True(EnhancedReviewSyncState.ShouldAttempt($"{UserN}:movie:278", "fp1", 5));
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static EnhancedReviewEntry Entry(
        string content = "",
        double? rating = 5,
        string? created = "2026-07-16T01:50:06.0000000Z",
        string? updated = "2026-07-16T01:50:06.0000000Z")
        => new()
        {
            Key = EnhancedReviewKey.TryParse($"{UserN}:movie:278")!,
            Content = content,
            Rating = rating,
            CreatedAt = created == null ? null : DateTimeOffset.Parse(created, CultureInfo.InvariantCulture),
            UpdatedAt = updated == null ? null : DateTimeOffset.Parse(updated, CultureInfo.InvariantCulture)
        };

    /// <summary>Builds a JE-shaped store document: { "Reviews": { key: entry, … } }.</summary>
    internal static string JeStore(params string[] entries)
        => "{\"Reviews\":{" + string.Join(",", entries) + "}}";

    internal static string JeEntry(
        string key, string userId, string tmdbId, string mediaType, string content, double? rating,
        string created, string updated = "")
    {
        if (string.IsNullOrEmpty(updated)) updated = created;

        return $"\"{key}\":{{" +
               $"\"UserId\":\"{userId}\"," +
               $"\"TmdbId\":\"{tmdbId}\"," +
               $"\"MediaType\":\"{mediaType}\"," +
               $"\"Content\":{JsonSerializer.Serialize(content)}," +
               $"\"Rating\":{(rating.HasValue ? rating.Value.ToString(CultureInfo.InvariantCulture) : "null")}," +
               $"\"CreatedAt\":\"{created}\"," +
               $"\"UpdatedAt\":\"{updated}\"}}";
    }

    internal static string UserIdN => UserN;

    internal static string OtherUserIdN => OtherUserN;
}
