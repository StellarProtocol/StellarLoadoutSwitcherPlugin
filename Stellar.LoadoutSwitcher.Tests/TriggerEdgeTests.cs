using Xunit;

namespace Stellar.LoadoutSwitcher.Tests;

/// <summary>
/// Pins the auto-apply edge rule (Plugin.Trigger.cs OnLoadoutsChanged → TriggerEdge.IsLoadoutSwitch):
/// only a move between two KNOWN loadout selections arms an apply. Regression guard for "Loadout
/// Switching, Switches without Switching Loadouts" (Midokuni 2026-09-21) — the first resolution of the
/// current index at login re-applied the bound setup over the player's vanilla changes.
/// </summary>
public sealed class TriggerEdgeTests
{
    [Fact]
    public void FirstResolveAtLogin_IsNotASwitch()
    {
        // prev==null: the index resolving for the first time at login/char-select. Must NOT arm.
        Assert.False(TriggerEdge.IsLoadoutSwitch(prev: null, idx: 0));
        Assert.False(TriggerEdge.IsLoadoutSwitch(prev: null, idx: 3));
    }

    [Fact]
    public void RealInWorldSwitch_IsASwitch()
    {
        // Both selections known and different: a genuine user switch.
        Assert.True(TriggerEdge.IsLoadoutSwitch(prev: 2, idx: 5));
        Assert.True(TriggerEdge.IsLoadoutSwitch(prev: 0, idx: 1));
    }

    [Fact]
    public void SameSelection_IsNotASwitch()
    {
        // A saved-list edit that doesn't move the selection re-fires the event with the same index.
        Assert.False(TriggerEdge.IsLoadoutSwitch(prev: 4, idx: 4));
    }

    [Fact]
    public void ResolvedToUnresolved_IsNotASwitch()
    {
        // Logout drops the index back to null — not a switch (and idx.Value would be invalid anyway).
        Assert.False(TriggerEdge.IsLoadoutSwitch(prev: 3, idx: null));
        Assert.False(TriggerEdge.IsLoadoutSwitch(prev: null, idx: null));
    }
}
