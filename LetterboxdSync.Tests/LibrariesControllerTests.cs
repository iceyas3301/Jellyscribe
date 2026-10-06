using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using LetterboxdSync.Api;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using Xunit;

namespace LetterboxdSync.Tests;

public class LibrariesControllerTests
{
    private static readonly Guid MoviesId = Guid.Parse("11111111111111111111111111111111");
    private static readonly Guid AnimeId = Guid.Parse("22222222222222222222222222222222");
    private static readonly Guid MusicId = Guid.Parse("33333333333333333333333333333333");
    private static readonly Guid UnsetId = Guid.Parse("44444444444444444444444444444444");

    private readonly IUserManager _userManager = Substitute.For<IUserManager>();
    private readonly ILibraryManager _libraryManager = Substitute.For<ILibraryManager>();

    public LibrariesControllerTests()
    {
        _libraryManager.GetVirtualFolders().Returns(new List<VirtualFolderInfo>
        {
            new() { Name = "Movies", ItemId = MoviesId.ToString(), CollectionType = CollectionTypeOptions.movies },
            new() { Name = "Anime", ItemId = AnimeId.ToString(), CollectionType = CollectionTypeOptions.tvshows },
            new() { Name = "Music", ItemId = MusicId.ToString(), CollectionType = CollectionTypeOptions.music },
            new() { Name = "Everything", ItemId = UnsetId.ToString(), CollectionType = null },
        });
    }

    private LibrariesController ControllerFor(User? user)
    {
        _userManager.GetUsers().Returns(user == null ? Array.Empty<User>() : new[] { user });
        var controller = new LibrariesController(_userManager, _libraryManager);
        var identity = user == null
            ? new ClaimsIdentity()
            : new ClaimsIdentity(new[] { new Claim("Jellyfin-UserId", user.Id.ToString("N")) }, "Test");
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };
        return controller;
    }

    private static List<(string Id, string Name, string Type)> Libraries(ActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var list = (IEnumerable)ok.Value!.GetType().GetProperty("libraries")!.GetValue(ok.Value)!;
        return list.Cast<object>().Select(o =>
        {
            var t = o.GetType();
            return ((string)t.GetProperty("id")!.GetValue(o)!,
                (string)t.GetProperty("name")!.GetValue(o)!,
                (string)t.GetProperty("collectionType")!.GetValue(o)!);
        }).ToList();
    }

    private static User MakeUser() => new("lachlan", "test-provider-id", "test-reset-id");

    [Fact]
    public void NoUser_ReturnsBadRequest()
    {
        Assert.IsType<BadRequestObjectResult>(ControllerFor(null).GetLibraries());
    }

    [Fact]
    public void Admin_SeesFilmTvAndMixedLibraries_NotMusic_SortedByName_WithNFormatIds()
    {
        var admin = MakeUser();
        admin.SetPermission(PermissionKind.IsAdministrator, true);

        var libs = Libraries(ControllerFor(admin).GetLibraries());

        Assert.Equal(new[] { "Anime", "Everything", "Movies" }, libs.Select(l => l.Name));
        Assert.DoesNotContain(libs, l => l.Name == "Music");
        Assert.Equal(AnimeId.ToString("N"), libs[0].Id);
        Assert.Equal("mixed", libs[1].Type);
        Assert.Equal("movies", libs[2].Type);
    }

    [Fact]
    public void UserWithAllFolderAccess_SeesEveryEligibleLibrary()
    {
        var user = MakeUser();
        user.SetPermission(PermissionKind.EnableAllFolders, true);

        Assert.Equal(3, Libraries(ControllerFor(user).GetLibraries()).Count);
    }

    [Fact]
    public void RestrictedUser_SeesOnlyLibrariesTheyCanAccess()
    {
        var user = MakeUser();
        user.SetPermission(PermissionKind.EnableAllFolders, false);
        user.SetPreference(PreferenceKind.EnabledFolders, new[] { MoviesId, MusicId });

        var libs = Libraries(ControllerFor(user).GetLibraries());

        // Anime is hidden (no access); Music is accessible but not a film/TV library.
        Assert.Equal(new[] { "Movies" }, libs.Select(l => l.Name));
    }
}
