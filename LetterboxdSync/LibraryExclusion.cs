using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace LetterboxdSync;

/// <summary>
/// The one rule for "is this item in a library the account excludes" (issue #124). Every export
/// path (scheduled film sync, scheduled episode catch-up, real-time playback) calls this before
/// handing an item to a service client, so the API and scraping paths never see excluded items
/// and the scheduled and real-time paths cannot disagree.
/// </summary>
public static class LibraryExclusion
{
    /// <summary>
    /// True when any library containing <paramref name="item"/> is in <paramref name="excludedLibraryIds"/>.
    /// An item can sit in two libraries when they share a path; excluding either one excludes it,
    /// because the user's intent is "never post this content". Items Jellyfin places in no library
    /// are never excluded. An empty list short-circuits without touching the library manager, so
    /// accounts that exclude nothing pay nothing.
    /// </summary>
    public static bool IsExcluded(ILibraryManager libraryManager, BaseItem item, IReadOnlyCollection<string>? excludedLibraryIds, ILogger? logger = null)
    {
        var excluded = ParseIds(excludedLibraryIds);
        if (excluded.Count == 0)
            return false;

        try
        {
            // GetCollectionFolders maps the item's physical root to the user-facing library
            // (CollectionFolder) whose id the settings pages store. item.GetTopParent() would
            // return the physical folder instead, whose id never matches.
            return libraryManager.GetCollectionFolders(item).Any(folder => excluded.Contains(folder.Id));
        }
        catch (Exception ex)
        {
            // Fail closed: this list decides what leaves the server, so an item whose library
            // cannot be resolved (e.g. mid library scan) is held back for this run rather than
            // risk posting excluded content. The next scheduled run retries it.
            logger?.LogWarning(ex, "Could not resolve the library of {Title}; holding it back from this account for now", item.Name);
            return true;
        }
    }

    /// <summary>
    /// Canonical stored form for ids arriving from the settings pages: valid, non-empty Guids in
    /// "N" format, deduplicated, in submission order. Null (a client that did not send the field)
    /// yields an empty list, the "exclude nothing" default.
    /// </summary>
    public static List<string> Normalise(IEnumerable<string>? ids)
    {
        var result = new List<string>();
        if (ids == null)
            return result;

        var seen = new HashSet<Guid>();
        foreach (var id in ids)
        {
            if (Guid.TryParse(id, out var guid) && guid != Guid.Empty && seen.Add(guid))
                result.Add(guid.ToString("N"));
        }

        return result;
    }

    /// <summary>
    /// What an account-save endpoint stores: the normalised submitted list, or, when the client
    /// omitted the field entirely (null), a copy of the stored list. Omission must never clear an
    /// exclusion, because this list decides what leaves the server.
    /// </summary>
    public static List<string> ResolveForSave(IEnumerable<string>? submitted, IEnumerable<string>? stored)
        => submitted != null ? Normalise(submitted) : Normalise(stored);

    /// <summary>
    /// Parses stored ids into Guids, accepting both "N" (stored) and dashed forms and ignoring
    /// anything malformed, so a hand-edited config cannot break sync.
    /// </summary>
    internal static HashSet<Guid> ParseIds(IReadOnlyCollection<string>? ids)
    {
        var set = new HashSet<Guid>();
        if (ids == null)
            return set;

        foreach (var id in ids)
        {
            if (Guid.TryParse(id, out var guid) && guid != Guid.Empty)
                set.Add(guid);
        }

        return set;
    }
}
