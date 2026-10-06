using System;
using System.Linq;
using System.Net.Mime;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace LetterboxdSync.Api;

/// <summary>
/// Lists the Jellyfin libraries an account can exclude from sync (issue #124). Its own controller
/// because both the Letterboxd and Serializd account cards consume it. Jellyfin's own
/// /Library/VirtualFolders is admin-only, and the per-user settings page runs as a normal user.
/// </summary>
[ApiController]
[Authorize]
[Route("Jellyfin.Plugin.LetterboxdSync/Libraries")]
[Produces(MediaTypeNames.Application.Json)]
public class LibrariesController : JellyfinUserApiController
{
    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;

    public LibrariesController(IUserManager userManager, ILibraryManager libraryManager)
        : base(userManager)
    {
        _userManager = userManager;
        _libraryManager = libraryManager;
    }

    /// <summary>
    /// Film, TV, and mixed libraries (collection type unset counts as mixed), with ids in the "N"
    /// format the account settings store. Non-admins only see libraries they can access, so the
    /// list never reveals a library name the caller could not otherwise see.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult GetLibraries()
    {
        var userId = GetCurrentUserId();
        var user = string.IsNullOrEmpty(userId)
            ? null
            : _userManager.GetUsers().FirstOrDefault(u => u.Id.ToString("N") == userId);
        if (user == null)
            return BadRequest(new { error = "Could not determine user" });

        var seesAll = user.HasPermission(PermissionKind.IsAdministrator)
            || user.HasPermission(PermissionKind.EnableAllFolders);
        var enabled = seesAll ? null : user.GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders).ToHashSet();

        var libraries = _libraryManager.GetVirtualFolders()
            .Where(f => IsSyncable(f.CollectionType))
            .Select(f => (Folder: f, Ok: Guid.TryParse(f.ItemId, out var id), Id: id))
            .Where(x => x.Ok && (enabled == null || enabled.Contains(x.Id)))
            .OrderBy(x => x.Folder.Name, StringComparer.OrdinalIgnoreCase)
            .Select(x => new
            {
                id = x.Id.ToString("N"),
                name = x.Folder.Name,
                collectionType = x.Folder.CollectionType?.ToString() ?? "mixed"
            })
            .ToList();

        return Ok(new { libraries });
    }

    // Only libraries that can produce the Movie or Episode items the plugin exports.
    private static bool IsSyncable(CollectionTypeOptions? type)
        => type is null or CollectionTypeOptions.movies or CollectionTypeOptions.tvshows or CollectionTypeOptions.mixed;
}
