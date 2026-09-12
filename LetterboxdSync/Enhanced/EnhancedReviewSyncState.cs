using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace LetterboxdSync.Enhanced;

/// <summary>What happened last time we tried a given JE review.</summary>
public sealed class EnhancedReviewSyncRecord
{
    /// <summary>Hash of the reviewed content+rating+edit time. See EnhancedReviewEntry.Fingerprint.</summary>
    public string Fingerprint { get; set; } = string.Empty;

    /// <summary>"synced" (posted), "skipped" (no destination exists), or "failed".</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>Machine-readable skip/failure reason; never review text.</summary>
    public string? Reason { get; set; }

    public int Attempts { get; set; }

    /// <summary>Provider error message. The providers don't echo review text back in errors
    /// the way Serializd's success body does, but any text is truncated before it lands here.</summary>
    public string? LastError { get; set; }

    public DateTime LastAttemptUtc { get; set; }
}

/// <summary>
/// Dedupe + outcome memory for the Jellyfin Enhanced review sync, persisted next to the
/// plugin's other state. Stores only a fingerprint and an outcome per JE review key — never
/// the review text — so editing a review re-syncs it without this file becoming a second copy
/// of the user's drafts.
/// </summary>
public static class EnhancedReviewSyncState
{
    public const string SyncedStatus = "synced";
    public const string SkippedStatus = "skipped";
    public const string FailedStatus = "failed";

    private static readonly object _lock = new();
    private static EnhancedReviewSyncStateFile? _state;
    private static ILogger? _logger;

    /// <summary>Test-only path override, mirroring <see cref="SyncHistory.DataPathOverride"/>.</summary>
    internal static string? PathOverrideForTesting { get; set; }

    /// <summary>Test hook: drop the in-memory copy so the next access re-reads from disk.</summary>
    internal static void ResetForTesting()
    {
        lock (_lock) { _state = null; }
    }

    public static void SetLogger(ILogger logger) => _logger = logger;

    internal static string DataPath
    {
        get
        {
            if (!string.IsNullOrEmpty(PathOverrideForTesting)) return PathOverrideForTesting!;

            var pluginDir = Path.GetDirectoryName(typeof(EnhancedReviewSyncState).Assembly.Location);
            if (!string.IsNullOrEmpty(pluginDir))
            {
                var configDir = Path.Combine(pluginDir!, "..", "configurations");
                if (Directory.Exists(configDir))
                    return Path.Combine(configDir, "jellyscribe-enhanced-review-state.json");
            }

            return Path.Combine(pluginDir ?? ".", "enhanced-review-state.json");
        }
    }

    private static EnhancedReviewSyncStateFile Load()
    {
        if (_state != null) return _state;

        _state = new EnhancedReviewSyncStateFile();

        try
        {
            var path = DataPath;
            if (File.Exists(path))
            {
                var parsed = JsonSerializer.Deserialize<EnhancedReviewSyncStateFile>(File.ReadAllText(path));
                if (parsed?.Entries != null) _state = parsed;
            }
        }
        catch (Exception ex)
        {
            // A corrupt state file must not stop the sync: start clean and re-derive. Worst
            // case we attempt an entry again, and the providers' own duplicate handling
            // decides the outcome.
            _logger?.LogWarning("Could not read the Enhanced review sync state: {Message}", ex.Message);
            _state = new EnhancedReviewSyncStateFile();
        }

        return _state;
    }

    private static void Save(EnhancedReviewSyncStateFile state)
    {
        try
        {
            var path = DataPath;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllText(path, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            _logger?.LogWarning("Could not save the Enhanced review sync state: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// The moment the sync should treat as "now" for a run without backfill. Established on
    /// first use and persisted, so turning backfill off later never re-posts history.
    /// </summary>
    public static DateTimeOffset EnsureSinceUtc(DateTimeOffset nowUtc)
    {
        lock (_lock)
        {
            var state = Load();
            if (state.SinceUtc == null)
            {
                state.SinceUtc = nowUtc;
                Save(state);
            }

            return state.SinceUtc.Value;
        }
    }

    /// <summary>
    /// The persisted no-backfill cutoff, or null when none has been established yet. Read-only —
    /// unlike <see cref="EnsureSinceUtc"/> it never creates one, so merely looking at the dashboard
    /// can't decide when the integration's first run happened.
    /// </summary>
    public static DateTimeOffset? SinceUtcOrNull()
    {
        lock (_lock)
        {
            return Load().SinceUtc;
        }
    }

    public static EnhancedReviewSyncRecord? Get(string key)
    {
        lock (_lock)
        {
            var state = Load();
            return state.Entries.TryGetValue(key, out var record) ? record : null;
        }
    }

    /// <summary>
    /// True when this review still needs a post: never seen, edited since the last post, or a
    /// previous attempt failed and hasn't exhausted its retries. A permanent skip (e.g. a
    /// season-level entry with no Serializd destination) is never retried.
    /// </summary>
    public static bool ShouldAttempt(string key, string fingerprint, int maxAttempts)
    {
        var existing = Get(key);
        if (existing == null) return true;

        if (!string.Equals(existing.Fingerprint, fingerprint, StringComparison.Ordinal))
            return true;

        if (string.Equals(existing.Status, SyncedStatus, StringComparison.Ordinal)) return false;
        if (string.Equals(existing.Status, SkippedStatus, StringComparison.Ordinal)) return false;

        return existing.Attempts < Math.Max(1, maxAttempts);
    }

    /// <summary>
    /// Record an attempt. <paramref name="reason"/> and <paramref name="error"/> are persisted,
    /// so callers must pass only machine-readable reasons or provider errors — never review text.
    /// </summary>
    public static void Record(string key, string fingerprint, string status, string? reason, string? error)
    {
        lock (_lock)
        {
            var state = Load();

            var attempts = 1;
            if (state.Entries.TryGetValue(key, out var existing))
            {
                // A changed fingerprint is a new post (an edit), so its retry budget restarts.
                attempts = string.Equals(existing.Fingerprint, fingerprint, StringComparison.Ordinal)
                    ? existing.Attempts + 1
                    : 1;
            }

            state.Entries[key] = new EnhancedReviewSyncRecord
            {
                Fingerprint = fingerprint,
                Status = status,
                Reason = reason,
                Attempts = attempts,
                LastError = Truncate(error, 300),
                LastAttemptUtc = DateTime.UtcNow
            };

            Save(state);
        }
    }

    /// <summary>Counts by recorded status, for the dashboard/status endpoint.</summary>
    public static (int Synced, int Skipped, int Failed) GetCounts()
    {
        lock (_lock)
        {
            var entries = Load().Entries.Values;
            return (
                entries.Count(e => e.Status == SyncedStatus),
                entries.Count(e => e.Status == SkippedStatus),
                entries.Count(e => e.Status == FailedStatus));
        }
    }

    /// <summary>Statuses keyed by review key, for the status endpoint's per-entry view.</summary>
    public static IReadOnlyDictionary<string, EnhancedReviewSyncRecord> Snapshot()
    {
        lock (_lock)
        {
            return new Dictionary<string, EnhancedReviewSyncRecord>(Load().Entries, StringComparer.Ordinal);
        }
    }

    public static void Clear()
    {
        lock (_lock)
        {
            _state = new EnhancedReviewSyncStateFile();
            Save(_state);
        }
    }

    private static string? Truncate(string? value, int max)
        => string.IsNullOrEmpty(value) || value.Length <= max ? value : value.Substring(0, max);

    private sealed class EnhancedReviewSyncStateFile
    {
        public DateTimeOffset? SinceUtc { get; set; }

        public Dictionary<string, EnhancedReviewSyncRecord> Entries { get; set; }
            = new(StringComparer.Ordinal);
    }
}
