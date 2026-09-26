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

    /// <summary>A RE-APPLY is a hotkey press of the loadout that was already equipped before the press
    /// and still is after the game's switch returned. The index never changes, so
    /// <see cref="IsLoadoutSwitch"/> never fires; this lets the player re-attempt a Deep-Slumber apply
    /// without bouncing through another loadout (Toir 2026-09-25). An unresolved selection (login /
    /// char-select) is never a re-apply.</summary>
    internal static bool IsReapply(int pressedId, int? currentBefore, int? currentAfter)
        => currentBefore == pressedId && currentAfter == pressedId;
}
