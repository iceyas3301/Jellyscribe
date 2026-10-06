using System.IO;
using System.Linq;
using System.Text.Json;
using System.Xml.Serialization;
using LetterboxdSync.Configuration;
using Xunit;

namespace LetterboxdSync.Tests;

/// <summary>
/// Persistence contract for the per-account excluded library list: configs saved before the
/// setting existed must load as "exclude nothing", and a populated list must survive both the
/// on-disk XML round trip and the admin page's JSON GET/PUT echo.
/// </summary>
public class ExcludedLibraryIdsConfigTests
{
    private const string AnimeId = "0c5b2a1e9f3d4c7a8b6e5d4c3b2a1f0e";
    private const string KidsId = "1d6c3b2f0a4e5d8b9c7f6e5d4c3b2a1f";

    [Fact]
    public void Account_LegacyXmlWithoutElement_LoadsEmptyList()
    {
        const string legacyXml = """
            <?xml version="1.0" encoding="utf-16"?>
            <Account xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
              <UserJellyfinId>user1</UserJellyfinId>
              <LetterboxdUsername>8bitproxy</LetterboxdUsername>
              <Enabled>true</Enabled>
            </Account>
            """;

        var account = Deserialize<Account>(legacyXml);

        Assert.NotNull(account.ExcludedLibraryIds);
        Assert.Empty(account.ExcludedLibraryIds);
    }

    [Fact]
    public void SerializdAccount_LegacyXmlWithoutElement_LoadsEmptyList()
    {
        const string legacyXml = """
            <?xml version="1.0" encoding="utf-16"?>
            <SerializdAccount xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
              <UserJellyfinId>user1</UserJellyfinId>
              <Email>8bitproxy@example.com</Email>
            </SerializdAccount>
            """;

        var account = Deserialize<SerializdAccount>(legacyXml);

        Assert.NotNull(account.ExcludedLibraryIds);
        Assert.Empty(account.ExcludedLibraryIds);
    }

    [Fact]
    public void PluginConfiguration_XmlRoundTrip_KeepsEachAccountsListExactly()
    {
        // Exact equality (not Contains) also guards against XmlSerializer appending to the
        // initializer's list and doubling entries on load.
        var config = new PluginConfiguration();
        config.Accounts.Add(new Account { UserJellyfinId = "u1", ExcludedLibraryIds = { AnimeId, KidsId } });
        config.Accounts.Add(new Account { UserJellyfinId = "u1" });
        config.SerializdAccounts.Add(new SerializdAccount { UserJellyfinId = "u1", ExcludedLibraryIds = { AnimeId } });

        var loaded = Deserialize<PluginConfiguration>(Serialize(config));

        Assert.Equal(new[] { AnimeId, KidsId }, loaded.Accounts[0].ExcludedLibraryIds);
        Assert.Empty(loaded.Accounts[1].ExcludedLibraryIds);
        Assert.Equal(new[] { AnimeId }, loaded.SerializdAccounts.Single().ExcludedLibraryIds);
    }

    [Fact]
    public void PluginConfiguration_JsonEcho_KeepsList()
    {
        // configPage.html does getPluginConfiguration -> mutate -> updatePluginConfiguration.
        var config = new PluginConfiguration();
        config.Accounts.Add(new Account { UserJellyfinId = "u1", ExcludedLibraryIds = { AnimeId } });
        config.SerializdAccounts.Add(new SerializdAccount { UserJellyfinId = "u1", ExcludedLibraryIds = { KidsId } });

        var echoed = JsonSerializer.Deserialize<PluginConfiguration>(JsonSerializer.Serialize(config))!;

        Assert.Equal(new[] { AnimeId }, echoed.Accounts.Single().ExcludedLibraryIds);
        Assert.Equal(new[] { KidsId }, echoed.SerializdAccounts.Single().ExcludedLibraryIds);
    }

    private static string Serialize<T>(T value)
    {
        var serializer = new XmlSerializer(typeof(T));
        using var writer = new StringWriter();
        serializer.Serialize(writer, value);
        return writer.ToString();
    }

    private static T Deserialize<T>(string xml)
    {
        var serializer = new XmlSerializer(typeof(T));
        using var reader = new StringReader(xml);
        return (T)serializer.Deserialize(reader)!;
    }
}
