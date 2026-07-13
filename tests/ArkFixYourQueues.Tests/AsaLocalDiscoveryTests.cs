using ArkFixYourQueues;

namespace ArkFixYourQueues.Tests;

public sealed class AsaLocalDiscoveryTests
{
    [Fact]
    public void ReadsLastJoinedServerAndRemovesVersionSuffix()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllLines(path,
            [
                "LastJoinedSessionPerCategory=OldServer - (v1.0)",
                "LastJoinedSessionPerCategory=NA-PVE-GenOne6446 - (v91.17)"
            ]);

            var found = AsaLocalDiscovery.FindLastJoined([path]);

            Assert.NotNull(found);
            Assert.Equal("NA-PVE-GenOne6446", found.Name);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void MissingOrUnreadableSettingsAreIgnored()
    {
        Assert.Null(AsaLocalDiscovery.FindLastJoined([Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".ini")]));
    }

    [Fact]
    public void ReadsFavoriteServerMetadataFromMenuProfile()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "FavoriteServersNames\0ArrayProperty\0NA-PVE-GenOne6446 - (v91.17) Map=Genesis_WP MaxPlayers=70 PVE=1 Modded=0 Official=1 HasPassword=0");
            var favorite = Assert.Single(AsaLocalDiscovery.FindFavorites([path]));
            Assert.Equal("NA-PVE-GenOne6446", favorite.Name);
            Assert.Equal("Genesis_WP", favorite.Map);
            Assert.True(favorite.Official);
        }
        finally { File.Delete(path); }
    }
}
