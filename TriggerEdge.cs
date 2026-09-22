namespace Stellar.LoadoutSwitcher;

/// <summary>Pure loadout-selection edge classifier for the auto-apply trigger (see
/// <c>Plugin.Trigger.cs</c>). BCL-only so it is unit-tested without the game or framework services.</summary>
internal static class TriggerEdge
{
    /// <summary>A loadout SWITCH is a move between two KNOWN selections. The FIRST resolution of the
    /// current index at login / character-select is <c>prev == null</c> (the index goes
    /// unresolved → resolved) and must NOT count as a switch: auto-applying there re-applies the bound
    /// setup over whatever the player changed in vanilla — "switches without switching loadouts"
    /// (Midokuni 2026-09-21). A no-move saved-list edit (<c>idx == prev</c>) is likewise not a switch.</summary>
    internal static bool IsLoadoutSwitch(int? prev, int? idx) => prev is not null && idx is not null && idx != prev;
}
