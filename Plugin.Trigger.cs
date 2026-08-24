using System;
using System.Threading;
using System.Threading.Tasks;
using Stellar.Abstractions.Domain.DeepSlumber;
using Stellar.Abstractions.Services;

namespace Stellar.LoadoutSwitcher;

/// <summary>
/// Auto-applies a bound Deep-Slumber setup when the player's loadout changes. Edge-triggers on
/// <see cref="ILoadout.CurrentIndex"/> change (covers both the hotkey path and any external
/// switch), arms a pending apply, and fires it once the build has settled — signalled by
/// <see cref="ILoadout.LiveStateChanged"/> or, failing that, a tick-count fallback — guarded so a
/// binding captured under one class is never applied to another, and so it never overlaps a
/// switch in flight (<see cref="_inFlight"/>, shared with the hotkey path in <c>Plugin.cs</c>).
/// </summary>
public sealed partial class Plugin
{
    private int? _lastIndex;
    private int? _pendingIndex;
    private long _tick;
    private long _pendingDeadline;
    private int _applying;                       // 1 while our own apply runs (ignore its LiveStateChanged)
    private const long SettleTimeoutTicks = 120;  // ~2 s fallback (60 fps) if LiveStateChanged never fires

    private void InitTrigger()
    {
        _lastIndex = _services.Loadout.CurrentIndex;
        _services.Loadout.LoadoutsChanged += OnLoadoutsChanged;
        _services.Loadout.LiveStateChanged += OnLiveStateChanged;
        _services.Framework.Update += OnUpdate;
    }

    private void DisposeTrigger()
    {
        _services.Loadout.LoadoutsChanged -= OnLoadoutsChanged;
        _services.Loadout.LiveStateChanged -= OnLiveStateChanged;
        _services.Framework.Update -= OnUpdate;
    }

    // Edge-trigger: only act when CurrentIndex actually changed (this event also fires for
    // saved-loadout-list edits that don't move the selection).
    private void OnLoadoutsChanged()
    {
        var idx = _services.Loadout.CurrentIndex;
        if (idx == _lastIndex) return;
        _lastIndex = idx;
        if (idx is null || !AutoApply) return;
        if (GetBinding(idx.Value) is null) return;
        _pendingIndex = idx;
        _pendingDeadline = _tick + SettleTimeoutTicks;
        DiagArmed(idx.Value);
    }

    private void OnLiveStateChanged()
    {
        if (_applying != 0) return;      // ignore the event our own apply provokes
        if (_pendingIndex is not null) TryApplyPending();
    }

    // IFramework.Update ticks every game frame with deltaTime; only the frame COUNT drives the
    // settle-timeout fallback here, so deltaTime itself is unused.
    private void OnUpdate(float deltaTime)
    {
        _tick++;
        if (_pendingIndex is not null && _tick >= _pendingDeadline) TryApplyPending();
    }

    private void TryApplyPending()
    {
        var idx = _pendingIndex;
        if (idx is null) return;
        var setup = GetBinding(idx.Value);
        if (setup is null) { _pendingIndex = null; return; }

        var liveProf = _services.Loadout.LiveState?.ProfessionId;
        var settled = liveProf == setup.ProfessionId;
        if (!settled && _tick < _pendingDeadline) return;   // keep waiting until settle or deadline

        _pendingIndex = null;
        if (!settled)
        {
            DiagSkippedApply(idx.Value, $"class {liveProf} != binding class {setup.ProfessionId}");
            return;                                          // stale binding — do not apply to wrong class
        }
        if (Interlocked.CompareExchange(ref _inFlight, 1, 0) != 0) return;
        Interlocked.Exchange(ref _applying, 1);
        _ = ApplyDeepSlumberAsync(setup);
    }

    private async Task ApplyDeepSlumberAsync(DeepSlumberSetup setup)
    {
        try
        {
            var result = await _services.DeepSlumber.ApplySetupAsync(setup).ConfigureAwait(false);
            ReportDeepSlumber(result);
        }
        catch (Exception ex) { _services.Log.Warning($"[LoadoutSwitcher] DS apply threw: {ex.Message}"); }
        finally
        {
            Interlocked.Exchange(ref _applying, 0);
            Interlocked.Exchange(ref _inFlight, 0);
        }
    }

    private void ReportDeepSlumber(DeepSlumberApplyResult result)
    {
        // AlreadyMatched/Cancelled: silent. Success: green. Partial/Refused/Unavailable: red (the
        // game also toasts the specific per-op reason itself). Loc keys land with the Task 8 overlay;
        // ILocalization.T falls back to the key literal until then.
        _services.Log.Info($"[LoadoutSwitcher] DS apply -> {result}");
        var (type, key) = result switch
        {
            DeepSlumberApplyResult.Success        => (NoticeTipType.GreenBar, "ds.toast.applied"),
            DeepSlumberApplyResult.PartialFailure => (NoticeTipType.RedBar,   "ds.toast.partial"),
            DeepSlumberApplyResult.Refused        => (NoticeTipType.RedBar,   "ds.toast.refused"),
            DeepSlumberApplyResult.Unavailable    => (NoticeTipType.RedBar,   "ds.toast.unavailable"),
            _                                      => (NoticeTipType.GreenBar, ""), // AlreadyMatched/Cancelled: silent
        };
        if (key.Length > 0)
            _services.NoticeTips.Create(type).WithContent(_loc.T(key)).Show();
    }
}
