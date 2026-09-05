using System.Collections.Generic;
using System.Linq;
using Stellar.Abstractions.Domain.DeepSlumber;
using Stellar.Abstractions.Services;

namespace Stellar.LoadoutSwitcher;

/// <summary>
/// Store for the per-loadout Deep-Slumber binding, plus capture-from-live.
///
/// <para><b>Bindings are DATA, the toggle is a SETTING (owner ruling 2026-09-05).</b> The bindings the
/// user captured live in the plugin's OWN data store — <c>stellar/plugindata/
/// stellar.loadoutswitcher.data/bindings.json</c> via <see cref="IPluginDataStore"/> and
/// <see cref="BindingPersistence"/>. The shared config section (<c>loadout-deepslumber</c>) keeps only
/// the <c>autoApply</c> toggle, and — frozen, read-only, never rewritten — the LEGACY
/// <c>binding.&lt;loadoutId&gt;#</c> keys a pre-2.4.0 build wrote, so a rollback still finds them.</para>
///
/// <para><b>Why the legacy keys are migrated lazily.</b> The plugin surface can read a config key but
/// cannot ENUMERATE one, so the candidate loadout ids have to come from the game
/// (<see cref="ILoadout.GetSlots"/>), which is empty until the loadout API resolves in-world. Each id is
/// therefore consulted the first time it is SEEN, and recorded in the document's ledger so it is never
/// consulted twice — that is what stops a binding the user has since cleared from being resurrected out
/// of the frozen config, and what lets a loadout id first seen in a later session still be picked up.</para>
///
/// <para>The trailing <c>#</c> terminator on the legacy key is load-bearing and preserved here for
/// reading: loadout ids are the game's Role-Plan ids (sparse after delete/recreate, not guaranteed
/// single-digit), so without a terminator <c>binding.1</c> would be a string-prefix of
/// <c>binding.10</c>. <c>"#"</c> can never appear inside an id.</para>
/// </summary>
public sealed partial class Plugin
{
    private IConfigSection _cfg = null!;
    private BindingsDocument _doc = new();
    private const string AutoApplyKey = "autoApply";
    private const string BindingPrefix = "binding.";

    private void InitBindings()
    {
        _cfg = _services.Config.GetSection("loadout-deepslumber");

        var stored = _services.Data.Read(BindingPersistence.FileName);
        _doc = BindingPersistence.Deserialize(stored, out var corrupt);
        if (corrupt && stored is not null)
        {
            // Never destroy user bytes we could not parse: park them beside the live file. The empty
            // document that replaces them re-consults the frozen config keys, so a corrupt file
            // degrades to "fall back to the pre-migration copy", not to "lost".
            _services.Data.Write(BindingPersistence.CorruptFileName, stored);
            _services.Log.Warning(
                $"[LoadoutSwitcher] {BindingPersistence.FileName} could not be parsed — parked as {BindingPersistence.CorruptFileName}; falling back to the config copy");
        }
    }

    /// <summary>The delimiter-terminated per-binding LEGACY config key for <paramref name="loadoutId"/>.
    /// Read-only now — <see cref="MigrateLegacyBindings"/> is its only caller.</summary>
    private static string BindingKey(int loadoutId) => BindingPrefix + loadoutId + "#";

    /// <summary>Master toggle for the auto-apply trigger. Defaults on. A SETTING — stays in the shared
    /// config file.</summary>
    internal bool AutoApply
    {
        get => _cfg.Get(AutoApplyKey, true);
        set { _cfg.Set(AutoApplyKey, value); _cfg.Save(); }
    }

    /// <summary>The Deep-Slumber setup bound to <paramref name="loadoutId"/>, or null if unbound.</summary>
    internal DeepSlumberSetup? GetBinding(int loadoutId)
    {
        MigrateLegacyBindings();
        return _doc.Bindings.TryGetValue(loadoutId, out var model) ? model.ToSetup() : null;
    }

    /// <summary>Removes the binding for <paramref name="loadoutId"/>, if any. The frozen legacy config
    /// key is NOT touched (rollback safety); the id is recorded as consulted so the cleared binding can
    /// never be migrated back in.</summary>
    internal void ClearBinding(int loadoutId)
    {
        MigrateLegacyBindings();
        var removed = _doc.Bindings.Remove(loadoutId);
        var marked = _doc.MarkMigrated(loadoutId);
        if (removed || marked) PersistBindings();
    }

    /// <summary>Snapshot the live active line + factors of the CURRENT loadout and store it.
    /// Returns false without storing anything if the current loadout or Deep-Slumber state isn't
    /// available yet, or if the live profession id hasn't resolved (a binding stored with
    /// <c>ProfessionId == 0</c> could never pass the auto-apply class guard, since a real
    /// profession id is never 0 — it would just silently never apply).</summary>
    internal bool BindCurrent()
    {
        MigrateLegacyBindings();

        var idx = _services.Loadout.CurrentIndex;
        var state = _services.DeepSlumber.GetState();
        if (idx is null || state is null) return false;

        var prof = _services.Loadout.LiveState?.ProfessionId ?? 0;
        if (prof == 0)
        {
            DiagBindAborted(idx.Value, "live class unresolved");
            return false;
        }

        var setup = new DeepSlumberSetup(prof, CaptureCurrentSeasonAreas(state));
        _doc.Bindings[idx.Value] = BindingModel.From(setup);
        _doc.MarkMigrated(idx.Value);
        PersistBindings();
        DiagBound(idx.Value, setup);
        return true;
    }

    // Scope to the CURRENT season only — the live container carries EVERY season the character ever
    // touched (last season's fully-built psychoscope bleeds in otherwise: owner bound an empty Tank and
    // got "4 areas (19 factors)" from the prior Dreambloom season). The current season is the newest
    // line id present (old seasons persist but never exceed it); this mirrors the logs site's
    // current-season scoping (services/stellar-logs/site/src/lib/deepslumber.ts, owner design
    // 2026-08-23). Within it, only the ENABLED area per sub-type (1-of-N), and only REAL socketed
    // factors (itemId != 0 — an unlocked-but-empty middle socket is not a factor). Also capture the TREE
    // (activated Anchor node ids) so a switch between builds on the SAME line but a DIFFERENT tree
    // resets + rebuilds the tree before socketing (owner 2026-09-01 — the game has no per-node anchor
    // removal). A captured (non-null) tree, possibly empty, is the exact target; legacy bindings with no
    // tree stay factor-only.
    private static List<DeepSlumberAreaBinding> CaptureCurrentSeasonAreas(DeepSlumberState state)
    {
        var currentLine = state.Lines.Count == 0 ? int.MinValue : state.Lines.Max(l => l.LineId);
        return state.Lines
            .Where(l => l.LineId == currentLine)
            .SelectMany(l => l.Areas)
            .Where(a => a.IsActive)
            .Select(a => new DeepSlumberAreaBinding(
                a.AreaId,
                a.MiddleNodes
                    .Where(m => m.Length >= 2 && m[1] != 0)
                    .Select(m => new[] { m[0], m[1] })
                    .OrderBy(f => f[0]).ToList())
            {
                NormalNodes = a.NormalNodes
                    .Where(n => n.Length >= 1)
                    .Select(n => n[0])
                    .OrderBy(x => x).ToList(),
            })
            .ToList();
    }

    private void PersistBindings()
        => _services.Data.Write(BindingPersistence.FileName, BindingPersistence.Serialize(_doc));

    // One-time-per-loadout-id copy of the legacy config bindings into plugindata. Runs off whatever
    // loadout ids the game currently reports; a no-op (one HashSet probe per slot) once they have all
    // been consulted, and a hard no-op before the loadout API resolves. The config keys are only READ.
    private void MigrateLegacyBindings()
    {
        if (!_services.Loadout.IsAvailable) return;
        var slots = _services.Loadout.GetSlots();
        if (slots.Count == 0) return;

        var adopted = new List<int>();
        var consulted = false;
        foreach (var slot in slots)
        {
            if (!_doc.MarkMigrated(slot.Index)) continue;   // this id was already consulted
            consulted = true;
            var legacy = _cfg.Get<BindingModel?>(BindingKey(slot.Index), null);
            if (BindingPersistence.Decide(_doc.Bindings.ContainsKey(slot.Index), legacy is not null)
                != MigrationDecision.MigrateFromConfig) continue;
            _doc.Bindings[slot.Index] = legacy!;
            adopted.Add(slot.Index);
        }

        if (!consulted) return;
        PersistBindings();
        if (adopted.Count > 0)
        {
            _services.Log.Info(
                $"[LoadoutSwitcher] migrated {adopted.Count} Deep-Slumber binding(s) for loadout(s) {string.Join(",", adopted)} from config to plugindata "
                + "(config keys left in place, frozen, so a rollback still finds them)");
        }
    }
}
