using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using LetterboxdSync.Serializd;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using NSubstitute;
using Xunit;

namespace LetterboxdSync.Tests.Serializd;

public class SerializdSeasonFallbackTests
{
    private const int Show = 220542;

    private static readonly IReadOnlyDictionary<int, int> TwoSeasonsOf24 = new Dictionary<int, int> { [1] = 24, [2] = 24 };

    private static ISerializdService ServiceWithSeasons(params int[] seasonNumbers)
        => ServiceWithFirstSeasonOf(48, seasonNumbers);

    private static ISerializdService ServiceWithFirstSeasonOf(int? firstSeasonCount, params int[] seasonNumbers)
    {
        var service = Substitute.For<ISerializdService>();
        service.GetSeasonEpisodeCountAsync(Show, 1).Returns(Task.FromResult(firstSeasonCount));
        service.ResolveSeasonIdAsync(Arg.Any<int>(), Arg.Any<int>()).Returns(Task.FromResult<int?>(null));
        foreach (var n in seasonNumbers)
            service.ResolveSeasonIdAsync(Show, n).Returns(Task.FromResult<int?>(9000 + n));
        return service;
    }

    [Fact]
    public async Task SeasonSerializdKnows_IsUsedAsIs()
    {
        var target = await SerializdSeasonFallback.ResolveAsync(ServiceWithSeasons(1, 2), Show, 2, () => TwoSeasonsOf24);

        Assert.Equal(new SerializdSeasonTarget(9002, 0), target);
    }

    [Fact]
    public async Task ShowSerializdKeepsAsOneSeason_LogsToSeasonOneAtTheAbsoluteNumber()
    {
        var target = await SerializdSeasonFallback.ResolveAsync(ServiceWithSeasons(1), Show, 2, () => TwoSeasonsOf24);

        Assert.Equal(new SerializdSeasonTarget(9001, 24, 48), target);
    }

    [Fact]
    public async Task ThirdSeason_CountsBothEarlierSeasons()
    {
        var lengths = new Dictionary<int, int> { [1] = 24, [2] = 23, [3] = 12 };

        var target = await SerializdSeasonFallback.ResolveAsync(ServiceWithSeasons(0, 1), Show, 3, () => lengths);

        Assert.Equal(new SerializdSeasonTarget(9001, 47, 48), target);
    }

    [Fact]
    public async Task ShowSerializdSplitsIntoSeasons_NeverFallsBack()
    {
        var target = await SerializdSeasonFallback.ResolveAsync(ServiceWithSeasons(1, 2), Show, 3,
            () => new Dictionary<int, int> { [1] = 24, [2] = 23 });

        Assert.Null(target);
    }

    [Fact]
    public async Task UnknownEarlierSeasonLength_DoesNotGuess()
    {
        var target = await SerializdSeasonFallback.ResolveAsync(ServiceWithSeasons(1), Show, 3,
            () => new Dictionary<int, int> { [2] = 23 });

        Assert.Null(target);
    }

    [Fact]
    public async Task MissingSeasonOneOrSpecials_HaveNothingToFallBackTo()
    {
        var service = ServiceWithSeasons();

        Assert.Null(await SerializdSeasonFallback.ResolveAsync(service, Show, 2, () => TwoSeasonsOf24));
        Assert.Null(await SerializdSeasonFallback.ResolveAsync(service, Show, 1, () => TwoSeasonsOf24));
        Assert.Null(await SerializdSeasonFallback.ResolveAsync(service, Show, 0, () => TwoSeasonsOf24));
    }

    [Fact]
    public async Task SeasonLengths_AreOnlyReadWhenFallingBack()
    {
        var read = false;

        await SerializdSeasonFallback.ResolveAsync(ServiceWithSeasons(1, 2), Show, 2, () =>
        {
            read = true;
            return TwoSeasonsOf24;
        });

        Assert.False(read);
    }

    [Fact]
    public async Task NewSeasonSerializdHasNotAddedYet_IsSkipped()
    {
        var target = await SerializdSeasonFallback.ResolveAsync(ServiceWithFirstSeasonOf(24, 1), Show, 2, () => TwoSeasonsOf24);

        Assert.Null(target);
    }

    [Fact]
    public async Task EarlierSeasonsLongerThanSerializdsSeasonOne_AreSkipped()
    {
        var target = await SerializdSeasonFallback.ResolveAsync(ServiceWithFirstSeasonOf(30, 1), Show, 3,
            () => new Dictionary<int, int> { [1] = 24, [2] = 12 });

        Assert.Null(target);
    }

    [Fact]
    public async Task UnknownSerializdEpisodeCount_DoesNotGuess()
    {
        var target = await SerializdSeasonFallback.ResolveAsync(ServiceWithFirstSeasonOf(null, 1), Show, 2, () => TwoSeasonsOf24);

        Assert.Null(target);
    }

    [Theory]
    [InlineData(1, 25)]
    [InlineData(23, 47)]
    [InlineData(24, 48)]
    [InlineData(25, null)]
    public void EpisodeFor_StopsAtTheEndOfSerializdsSeason(int episode, int? expected)
    {
        Assert.Equal(expected, new SerializdSeasonTarget(9001, 24, 48).EpisodeFor(episode));
    }

    [Fact]
    public void EpisodeFor_WithoutACount_OnlyAddsTheOffset()
    {
        Assert.Equal(500, new SerializdSeasonTarget(9001, 0).EpisodeFor(500));
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 24)]
    [InlineData(3, 48)]
    public void EpisodeOffset_SumsTheEarlierSeasons(int season, int expected)
    {
        var lengths = new Dictionary<int, int> { [1] = 24, [2] = 24, [3] = 12 };

        Assert.Equal(expected, SerializdSeasonFallback.EpisodeOffset(season, lengths));
    }

    private static Episode Ep(int? season, int? number, int? numberEnd = null)
        => new() { Id = Guid.NewGuid(), ParentIndexNumber = season, IndexNumber = number, IndexNumberEnd = numberEnd };

    private static Season SeasonOf(params Episode[] episodes)
        => new() { Id = Guid.NewGuid(), Children = episodes };

    [Fact]
    public void SeasonLengths_ComeFromTheSeriesEpisodes()
    {
        var series = new Series
        {
            Id = Guid.NewGuid(),
            Children = new BaseItem[]
            {
                SeasonOf(Ep(0, 1), Ep(0, 30)),
                SeasonOf(Ep(1, 1), Ep(1, 2), Ep(1, 22, numberEnd: 24)),
                SeasonOf(Ep(2, 1), Ep(2, 12), Ep(2, null)),
                SeasonOf(Ep(null, 5)),
            },
        };

        var lengths = SerializdSeasonFallback.ReadSeasonLengths(series);

        Assert.Equal(new Dictionary<int, int> { [1] = 24, [2] = 12 }, lengths);
    }

    [Fact]
    public void SeasonLengths_WithoutASeries_AreEmpty()
    {
        Assert.Empty(SerializdSeasonFallback.ReadSeasonLengths(null));
    }
}
