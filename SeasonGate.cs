namespace Stellar.LoadoutSwitcher;

/// <summary>Pure season gate for the Deep-Slumber apply (see <c>Plugin.Trigger.cs</c> TryApplyPending).
/// BCL-only so it is unit-tested without the game or framework services.</summary>
internal static class SeasonGate
{
    /// <summary>The only season whose Deep-Slumber (Psychoscope) request shape this plugin was built and
    /// validated against: Season 3 ("Echoes of Ember", SeasonGlobalTable id 3).</summary>
    internal const int SupportedSeasonId = 3;

    /// <summary>True only when the game's current season id is EXACTLY <see cref="SupportedSeasonId"/>.
    /// Fail-safe by design: a later season may change the Deep-Slumber system server-side, so an
    /// unreadable id (<c>null</c> — Lua not ready / global missing / not a number), the pre-sync
    /// <c>0</c> (season_data.CurSeasonId before the server's SyncSeason arrives), or any other season
    /// all BLOCK the apply rather than risk sending an outdated request.</summary>
    internal static bool AllowsApply(double? seasonId) => seasonId == SupportedSeasonId;
}
