using Xunit;

namespace Stellar.LoadoutSwitcher.Tests;

/// <summary>
/// Pins the Deep-Slumber season gate (Plugin.Trigger.cs TryApplyPending → SeasonGate.AllowsApply):
/// factors are only restored in Season 3. Every other season, the pre-sync 0 and an unreadable id must
/// block, so a later season's server never receives a request built for the Season 3 system.
/// </summary>
public sealed class SeasonGateTests
{
    [Fact]
    public void Season3_IsAllowed()
    {
        Assert.True(SeasonGate.AllowsApply(3));
    }

    [Theory]
    [InlineData(0)]   // season_data.CurSeasonId before the server's SyncSeason arrives
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]   // next season — the case this gate exists for
    [InlineData(3.5)] // never a real id; only an exact 3 passes
    public void OtherSeasons_AreBlocked(double seasonId)
    {
        Assert.False(SeasonGate.AllowsApply(seasonId));
    }

    [Fact]
    public void UnreadableSeason_IsBlocked()
    {
        // Lua not ready, global missing, Lua error, or a non-number value: fail safe.
        Assert.False(SeasonGate.AllowsApply(null));
    }
}
