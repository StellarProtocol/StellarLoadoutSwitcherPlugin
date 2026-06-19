using Stellar.Abstractions.Diagnostics;
using Stellar.Abstractions.Domain.Loadout;

namespace Stellar.LoadoutSwitcher;

/// <summary>
/// Diagnostic sibling partial for <see cref="Plugin"/>. Per-event hotkey trace lines are
/// gated on <see cref="StellarDiagnostics.IsEnabled"/> so normal play stays quiet; the
/// user-facing switch outcomes (in <c>Plugin.cs</c>) always log.
/// </summary>
public sealed partial class Plugin
{
    private void DiagApplying(int slotNumber, LoadoutSlot slot)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _services.Log.Info($"[LoadoutSwitcher] hotkey {slotNumber} -> apply id={slot.Index} '{slot.Name}'");
    }

    private void DiagSkipped(int slotNumber, string reason)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _services.Log.Info($"[LoadoutSwitcher] hotkey {slotNumber} skipped: {reason}");
    }
}
