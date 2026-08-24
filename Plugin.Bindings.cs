using System.Linq;
using Stellar.Abstractions.Domain.DeepSlumber;
using Stellar.Abstractions.Services;

namespace Stellar.LoadoutSwitcher;

/// <summary>
/// Config-backed store for the per-loadout Deep-Slumber binding, plus capture-from-live. The
/// config section holds an <c>autoApply</c> toggle and one key per bound loadout
/// (<c>binding.&lt;loadoutId&gt;#</c>), so clearing a binding is a single-key removal and there is
/// no separate index to keep in sync. The trailing <c>#</c> terminator is load-bearing: loadout ids
/// are the game's Role-Plan ids (not guaranteed single-digit/non-colliding), so without a
/// terminator <c>binding.1</c> would be a string-prefix of <c>binding.10</c>, <c>binding.12</c>,
/// etc., and <see cref="IConfigSection.RemoveByPrefix"/>'s raw <c>StartsWith</c> would wipe every
/// one of them when clearing loadout 1. <c>"#"</c> can never appear inside an id, so no binding key
/// is ever a prefix of another.
/// </summary>
public sealed partial class Plugin
{
    private IConfigSection _cfg = null!;
    private const string AutoApplyKey = "autoApply";
    private const string BindingPrefix = "binding.";

    private void InitBindings() => _cfg = _services.Config.GetSection("loadout-deepslumber");

    /// <summary>The delimiter-terminated per-binding config key for <paramref name="loadoutId"/>.
    /// Used by <see cref="GetBinding"/>, <see cref="ClearBinding"/>, and <see cref="BindCurrent"/>
    /// so the three can never drift apart.</summary>
    private static string BindingKey(int loadoutId) => BindingPrefix + loadoutId + "#";

    /// <summary>Master toggle for the auto-apply trigger (Task 7). Defaults on.</summary>
    internal bool AutoApply
    {
        get => _cfg.Get(AutoApplyKey, true);
        set { _cfg.Set(AutoApplyKey, value); _cfg.Save(); }
    }

    /// <summary>The Deep-Slumber setup bound to <paramref name="loadoutId"/>, or null if unbound.</summary>
    internal DeepSlumberSetup? GetBinding(int loadoutId)
        => _cfg.Get<BindingModel?>(BindingKey(loadoutId), null)?.ToSetup();

    /// <summary>Removes the binding for <paramref name="loadoutId"/>, if any.</summary>
    internal void ClearBinding(int loadoutId)
    {
        _cfg.RemoveByPrefix(BindingKey(loadoutId));
        _cfg.Save();
    }

    /// <summary>Snapshot the live active line + factors of the CURRENT loadout and store it.
    /// Returns false without storing anything if the current loadout or Deep-Slumber state isn't
    /// available yet, or if the live profession id hasn't resolved (a binding stored with
    /// <c>ProfessionId == 0</c> could never pass the auto-apply class guard, since a real
    /// profession id is never 0 — it would just silently never apply).</summary>
    internal bool BindCurrent()
    {
        var idx = _services.Loadout.CurrentIndex;
        var state = _services.DeepSlumber.GetState();
        if (idx is null || state is null) return false;

        var prof = _services.Loadout.LiveState?.ProfessionId ?? 0;
        if (prof == 0)
        {
            DiagBindAborted(idx.Value, "live class unresolved");
            return false;
        }

        // Scope to the CURRENT season only — the live container carries EVERY season the character
        // ever touched (last season's fully-built psychoscope bleeds in otherwise: owner bound an
        // empty Tank and got "4 areas (19 factors)" from the prior Dreambloom season). The current
        // season is the newest line id present (old seasons persist but never exceed it); this mirrors
        // the logs site's current-season scoping (services/stellar-logs/site/src/lib/deepslumber.ts,
        // owner design 2026-08-23). Within it, only the ENABLED area per sub-type (1-of-N), and only
        // REAL socketed factors (itemId != 0 — an unlocked-but-empty middle socket is not a factor).
        var currentLine = state.Lines.Count == 0 ? int.MinValue : state.Lines.Max(l => l.LineId);
        var areas = state.Lines
            .Where(l => l.LineId == currentLine)
            .SelectMany(l => l.Areas)
            .Where(a => a.IsActive)
            .Select(a => new DeepSlumberAreaBinding(
                a.AreaId,
                a.MiddleNodes
                    .Where(m => m.Length >= 2 && m[1] != 0)
                    .Select(m => new[] { m[0], m[1] })
                    .OrderBy(f => f[0]).ToList()))
            .ToList();

        var setup = new DeepSlumberSetup(prof, areas);
        _cfg.Set(BindingKey(idx.Value), BindingModel.From(setup));
        _cfg.Save();
        DiagBound(idx.Value, setup);
        return true;
    }
}
