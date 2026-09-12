using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;

namespace LetterboxdSync.Enhanced;

/// <summary>
/// Scheduled half of the Jellyfin Enhanced review sync. JE writes reviews to a file and raises
/// no event this plugin can subscribe to, so a short interval is how a review posted in the JE
/// UI reaches Letterboxd/Serializd without the user asking. The interval is editable in
/// Dashboard → Scheduled Tasks like any other task.
/// </summary>
public class EnhancedReviewSyncTask : IScheduledTask
{
    private readonly EnhancedReviewSyncRunner _runner;

    public EnhancedReviewSyncTask(EnhancedReviewSyncRunner runner)
    {
        _runner = runner;
    }

    public string Name => "Sync Jellyfin Enhanced reviews";

    public string Key => "JellyscribeEnhancedReviews";

    public string Description =>
        "Posts reviews and ratings written in Jellyfin Enhanced to the matching Letterboxd (films) " +
        "or Serializd (TV) diary. No-op unless enabled in the plugin's Integrations settings.";

    public string Category => "Letterboxd";

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        => _runner.RunAsync(progress, cancellationToken);

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => new[]
    {
        new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromMinutes(15).Ticks
        }
    };
}
