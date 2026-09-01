using System;
using System.Linq;
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
/// binding captured under one class is never applied to another. The DS apply is single-flighted
/// on its OWN latch, <see cref="_applying"/> — NOT the hotkey path's <see cref="_inFlight"/>
/// (<c>Plugin.cs</c>). The two guard different, independent operations (a Deep-Slumber apply vs. a
/// loadout switch): the hotkey switch that arms a pending apply holds <see cref="_inFlight"/> for
/// its whole in-flight <c>await</c>, so if the settle signal fires before that continuation
/// releases it, sharing the guard would make the DS apply lose the race and drop silently. On an
/// <see cref="_applying"/> collision (a DS apply is already running) the pending apply RE-ARMS
/// instead of dropping.
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
        if (Interlocked.CompareExchange(ref _applying, 0, 0) != 0) return;   // ignore the event our own apply provokes
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
        if (!settled && _tick < _pendingDeadline) return;          // keep waiting (pending stays armed)
        if (!settled)                                              // deadline hit with wrong/again class → drop + log
        {
            _pendingIndex = null;
            DiagSkippedApply(idx.Value, $"class {liveProf} != binding class {setup.ProfessionId}");
            return;
        }
        // settled + class matches: try to take the DS-apply latch (NOT _inFlight)
        if (Interlocked.CompareExchange(ref _applying, 1, 0) != 0)
        {
            _pendingDeadline = _tick + SettleTimeoutTicks;        // a DS apply is already running → retry next tick
            return;                                               // keep _pendingIndex armed, do NOT drop
        }
        _pendingIndex = null;                                     // committed
        _ = ApplyDeepSlumberAsync(setup);
    }

    private async Task ApplyDeepSlumberAsync(DeepSlumberSetup setup)
    {
        try
        {
            var result = await _services.DeepSlumber.ApplySetupAsync(setup).ConfigureAwait(false);
            ReportDeepSlumber(setup, result);
        }
        catch (Exception ex) { _services.Log.Warning($"[LoadoutSwitcher] DS apply threw: {ex.Message}"); }
        finally
        {
            // Only our own latch — the DS apply never touches the hotkey path's _inFlight.
            Interlocked.Exchange(ref _applying, 0);
        }
    }

    private void ReportDeepSlumber(DeepSlumberSetup setup, DeepSlumberApplyResult result)
    {
        // AlreadyMatched/Cancelled: silent. Success: green. Partial/Refused/Unavailable: red (the
        // game also toasts the specific per-op reason itself). Loc keys land with the Task 8 overlay;
        // ILocalization.T falls back to the key literal until then.
        // Always-on shape probe: `anchors=legacy` means the applied binding predates tree capture (no
        // tree → factor-only, no reset) — the owner must re-Bind to capture the tree. `anchors=N` means
        // a tree is bound. This shows WHY an apply did/didn't reset without needing STELLAR_DIAGNOSTICS.
        var factors = setup.Areas.Sum(a => a.Factors.Count);
        var anchors = setup.Areas.Any(a => a.NormalNodes is not null)
            ? setup.Areas.Sum(a => a.NormalNodes?.Count ?? 0).ToString()
            : "legacy";
        _services.Log.Info($"[LoadoutSwitcher] DS apply -> {result} (areas={setup.Areas.Count} factors={factors} anchors={anchors})");
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
