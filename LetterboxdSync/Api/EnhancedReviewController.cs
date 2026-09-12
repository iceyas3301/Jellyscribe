using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using LetterboxdSync.Enhanced;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace LetterboxdSync.Api;

/// <summary>Per-review sync state, as reported to the admin dashboard.</summary>
public sealed class EnhancedReviewStatusEntry
{
    public string Key { get; set; } = string.Empty;

    /// <summary>"synced", "skipped", or "failed".</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>Machine-readable reason for a skip/failure — never review text.</summary>
    public string? Reason { get; set; }

    public int Attempts { get; set; }

    public string? LastError { get; set; }

    public DateTime LastAttemptUtc { get; set; }
}

/// <summary>
/// Everything the dashboard needs to answer "is this integration doing anything?" — whether it
/// is switched on, whether JE's store is where we expect it, how many reviews are pending, and
/// what the last run did.
/// </summary>
public sealed class EnhancedReviewStatus
{
    public bool Enabled { get; set; }
    public bool Backfill { get; set; }
    public int MaxAttempts { get; set; }

    public string StorePath { get; set; } = string.Empty;
    public bool StorePresent { get; set; }
    public bool StoreReadable { get; set; }
    public string? StoreError { get; set; }
    public int StoreEntries { get; set; }

    public int Pending { get; set; }
    public int Synced { get; set; }
    public int Skipped { get; set; }
    public int Failed { get; set; }

    public bool IsRunning { get; set; }
    public DateTime? LastRunCompletedUtc { get; set; }
    public string? LastRun { get; set; }
    public List<string>? LastRunErrors { get; set; }

    public List<EnhancedReviewStatusEntry> Entries { get; set; } = new();
}

/// <summary>
/// Admin surface for the Jellyfin Enhanced review sync: what the integration can see, and a
/// manual trigger.
///
/// Both endpoints require elevation. JE's store path and the aggregate counts describe every
/// user's reviews on this server, so this is server-operator information rather than per-user
/// data (the same reasoning that makes the Logs endpoint admin-only).
/// </summary>
[ApiController]
[Authorize]
[Route("Jellyfin.Plugin.LetterboxdSync/EnhancedReviews")]
[Produces(MediaTypeNames.Application.Json)]
public class EnhancedReviewController : ControllerBase
{
    private readonly ILogger<EnhancedReviewController> _logger;
    private readonly EnhancedReviewSyncRunner _runner;

    /// <summary>
    /// Holds the fire-and-forget run started by <see cref="SyncNow"/> so tests can await it;
    /// it otherwise outlives the request. Production never reads it.
    /// </summary>
    internal Task? LastBackgroundSync { get; private set; }

    public EnhancedReviewController(ILogger<EnhancedReviewController> logger, EnhancedReviewSyncRunner runner)
    {
        _logger = logger;
        _runner = runner;
    }

    [HttpGet("Status")]
    [Authorize(Policy = "RequiresElevation")]
    public ActionResult<EnhancedReviewStatus> GetStatus()
    {
        var config = EnhancedReviewSyncRunner.ResolveConfiguration();
        var maxAttempts = config?.EnhancedReviewSyncMaxAttempts ?? 5;

        // Probe the store so the dashboard can distinguish "JE isn't installed", "no reviews
        // written yet", and "the integration is simply switched off".
        var load = EnhancedReviewStore.Load(config?.EnhancedReviewsPath, _logger);
        var counts = EnhancedReviewSyncState.GetCounts();

        var status = new EnhancedReviewStatus
        {
            Enabled = config?.EnhancedReviewSyncEnabled == true,
            Backfill = config?.EnhancedReviewSyncBackfill ?? true,
            MaxAttempts = maxAttempts,
            StorePath = load.Path,
            StorePresent = load.StorePresent,
            StoreReadable = load.StoreReadable,
            StoreError = load.Error,
            StoreEntries = load.Entries.Count,
            Pending = load.Entries.Count(e => EnhancedReviewSyncState.ShouldAttempt(
                e.Key.CompositeId, e.Fingerprint, maxAttempts)),
            Synced = counts.Synced,
            Skipped = counts.Skipped,
            Failed = counts.Failed,
            IsRunning = EnhancedReviewSyncRunner.IsRunning,
            LastRunCompletedUtc = EnhancedReviewSyncRunner.LastRunCompletedUtc,
            LastRun = EnhancedReviewSyncRunner.LastRun?.Describe(),
            LastRunErrors = EnhancedReviewSyncRunner.LastRun?.Errors
        };

        status.Entries = EnhancedReviewSyncState.Snapshot()
            .Select(kv => new EnhancedReviewStatusEntry
            {
                Key = kv.Key,
                Status = kv.Value.Status,
                Reason = kv.Value.Reason,
                Attempts = kv.Value.Attempts,
                LastError = kv.Value.LastError,
                LastAttemptUtc = kv.Value.LastAttemptUtc
            })
            .OrderByDescending(e => e.LastAttemptUtc)
            .Take(200)
            .ToList();

        return Ok(status);
    }

    /// <summary>
    /// Runs a sync pass now (202, in the background) instead of waiting for the scheduled task.
    /// 409 while a run is in flight; 400 when the integration is switched off.
    /// </summary>
    [HttpPost("SyncNow")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public ActionResult SyncNow()
    {
        if (EnhancedReviewSyncRunner.ResolveConfiguration()?.EnhancedReviewSyncEnabled != true)
            return BadRequest(new { error = "Jellyfin Enhanced review sync is disabled in the plugin settings." });

        if (EnhancedReviewSyncRunner.IsRunning)
            return Conflict(new { error = "A Jellyfin Enhanced review sync is already running." });

        LastBackgroundSync = Task.Run(async () =>
        {
            try
            {
                await _runner.RunAsync(null, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // RunAsync already swallows its own failures; this is belt-and-braces so a
                // background run can never surface as an unobserved task exception.
                _logger.LogError("Manual Jellyfin Enhanced review sync failed: {Message}", ex.Message);
            }
        });

        return Accepted(new { started = true });
    }
}
