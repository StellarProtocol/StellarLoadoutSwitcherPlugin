using System.Linq;
using Stellar.Abstractions.Domain.DeepSlumber;
using Stellar.Abstractions.Services;

namespace Stellar.LoadoutSwitcher;

/// <summary>
/// Config-backed store for the per-loadout Deep-Slumber binding, plus capture-from-live. The
/// config section holds an <c>autoApply</c> toggle and one key per bound loadout
/// (<c>binding.&lt;loadoutId&gt;</c>), so clearing a binding is a single-key removal and there is
/// no separate index to keep in sync.
/// </summary>
public sealed partial class Plugin
{
    private IConfigSection _cfg = null!;
    private const string AutoApplyKey = "autoApply";
    private const string BindingPrefix = "binding.";

    private void InitBindings() => _cfg = _services.Config.GetSection("loadout-deepslumber");

    /// <summary>Master toggle for the auto-apply trigger (Task 7). Defaults on.</summary>
    internal bool AutoApply
    {
        get => _cfg.Get(AutoApplyKey, true);
        set { _cfg.Set(AutoApplyKey, value); _cfg.Save(); }
    }

    /// <summary>The Deep-Slumber setup bound to <paramref name="loadoutId"/>, or null if unbound.</summary>
    internal DeepSlumberSetup? GetBinding(int loadoutId)
        => _cfg.Get<BindingModel?>(BindingPrefix + loadoutId, null)?.ToSetup();

    /// <summary>Removes the binding for <paramref name="loadoutId"/>, if any.</summary>
    internal void ClearBinding(int loadoutId)
    {
        _cfg.RemoveByPrefix(BindingPrefix + loadoutId);
        _cfg.Save();
    }

    /// <summary>Snapshot the live active line + factors of the CURRENT loadout and store it.</summary>
    internal bool BindCurrent()
    {
        var idx = _services.Loadout.CurrentIndex;
        var state = _services.DeepSlumber.GetState();
        if (idx is null || state is null) return false;

        var prof = _services.Loadout.LiveState?.ProfessionId ?? 0;
        var areas = state.Lines
            .SelectMany(l => l.Areas)
            .Where(a => a.IsActive)
            .Select(a => new DeepSlumberAreaBinding(
                a.AreaId,
                a.MiddleNodes.Select(m => new[] { m[0], m[1] }).OrderBy(f => f[0]).ToList()))
            .ToList();

        var setup = new DeepSlumberSetup(prof, areas);
        _cfg.Set(BindingPrefix + idx.Value, BindingModel.From(setup));
        _cfg.Save();
        DiagBound(idx.Value, setup);
        return true;
    }
}
