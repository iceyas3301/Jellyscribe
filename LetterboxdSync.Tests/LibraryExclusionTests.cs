using System;
using System.Collections.Generic;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using NSubstitute;
using Xunit;

namespace LetterboxdSync.Tests;

public class LibraryExclusionTests
{
    private static readonly Guid AnimeId = Guid.Parse("0c5b2a1e9f3d4c7a8b6e5d4c3b2a1f0e");
    private static readonly Guid TvId = Guid.Parse("1d6c3b2f0a4e5d8b9c7f6e5d4c3b2a1f");

    private readonly ILibraryManager _libraryManager = Substitute.For<ILibraryManager>();
    private readonly Movie _item = new Movie { Name = "Perfect Blue" };

    private void PlaceIn(params Guid[] libraryIds)
    {
        var folders = new List<Folder>();
        foreach (var id in libraryIds)
            folders.Add(new CollectionFolder { Id = id });
        _libraryManager.GetCollectionFolders(_item).Returns(folders);
    }

    [Fact]
    public void EmptyList_NeverConsultsLibraryManager()
    {
        PlaceIn(AnimeId);

        Assert.False(LibraryExclusion.IsExcluded(_libraryManager, _item, new List<string>()));
        Assert.False(LibraryExclusion.IsExcluded(_libraryManager, _item, null));
        _libraryManager.DidNotReceive().GetCollectionFolders(Arg.Any<BaseItem>());
    }

    [Fact]
    public void ItemInExcludedLibrary_IsExcluded()
    {
        PlaceIn(AnimeId);

        Assert.True(LibraryExclusion.IsExcluded(_libraryManager, _item, new[] { AnimeId.ToString("N") }));
    }

    [Fact]
    public void ItemInOtherLibrary_IsNotExcluded()
    {
        PlaceIn(TvId);

        Assert.False(LibraryExclusion.IsExcluded(_libraryManager, _item, new[] { AnimeId.ToString("N") }));
    }

    [Fact]
    public void ItemInTwoLibraries_OneExcluded_IsExcluded()
    {
        PlaceIn(TvId, AnimeId);

        Assert.True(LibraryExclusion.IsExcluded(_libraryManager, _item, new[] { AnimeId.ToString("N") }));
    }

    [Fact]
    public void DeletedLibraryId_MatchesNothing()
    {
        PlaceIn(TvId);

        Assert.False(LibraryExclusion.IsExcluded(_libraryManager, _item, new[] { Guid.NewGuid().ToString("N") }));
    }

    [Fact]
    public void ItemInNoLibrary_IsNotExcluded()
    {
        PlaceIn();

        Assert.False(LibraryExclusion.IsExcluded(_libraryManager, _item, new[] { AnimeId.ToString("N") }));
    }

    [Theory]
    [InlineData("0C5B2A1E9F3D4C7A8B6E5D4C3B2A1F0E")]      // upper-case N
    [InlineData("0c5b2a1e-9f3d-4c7a-8b6e-5d4c3b2a1f0e")]  // dashed
    public void IdFormatVariants_StillMatch(string storedId)
    {
        PlaceIn(AnimeId);

        Assert.True(LibraryExclusion.IsExcluded(_libraryManager, _item, new[] { storedId }));
    }

    [Fact]
    public void MalformedIdsOnly_TreatedAsEmpty()
    {
        PlaceIn(AnimeId);

        Assert.False(LibraryExclusion.IsExcluded(_libraryManager, _item, new[] { "not-a-guid", "", "00000000000000000000000000000000" }));
        _libraryManager.DidNotReceive().GetCollectionFolders(Arg.Any<BaseItem>());
    }

    [Fact]
    public void LookupThrows_FailsClosed()
    {
        // The list decides what leaves the server: an unresolvable library holds the item back.
        _libraryManager.GetCollectionFolders(_item).Returns(_ => throw new InvalidOperationException("mid scan"));

        Assert.True(LibraryExclusion.IsExcluded(_libraryManager, _item, new[] { AnimeId.ToString("N") }));
    }

    [Fact]
    public void ResolveForSave_OmittedField_KeepsStoredList()
    {
        var stored = new List<string> { AnimeId.ToString("N") };

        Assert.Equal(stored, LibraryExclusion.ResolveForSave(null, stored));
        Assert.Empty(LibraryExclusion.ResolveForSave(null, null));
    }

    [Fact]
    public void ResolveForSave_SubmittedList_ReplacesStoredList()
    {
        var stored = new List<string> { AnimeId.ToString("N") };

        Assert.Equal(new[] { TvId.ToString("N") }, LibraryExclusion.ResolveForSave(new[] { TvId.ToString() }, stored));
        Assert.Empty(LibraryExclusion.ResolveForSave(new List<string>(), stored));
    }
}
