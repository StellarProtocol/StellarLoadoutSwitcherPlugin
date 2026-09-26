using System;
using System.Threading;
using System.Threading.Tasks;
using Stellar.Abstractions.Domain.Loadout;
using Stellar.Abstractions.Services;

namespace Stellar.LoadoutSwitcher;

/// <summary>
/// "Copy loadout" — the <c>← &lt;worn loadout&gt;</c> button on every non-worn row copies the setup the
/// player is WEARING (including unsaved edits) into that row through the game's own Save
/// (<see cref="ILoadoutSave.SaveCurrentToAsync"/>), then mirrors the worn loadout's Deep-Slumber binding onto
/// it. Spec: <c>docs/superpowers/specs/2026-09-26-loadout-copy-design.md</c> (owner-approved 2026-09-26).
///
/// <para><b>Confirm.</b> The overlay has no modal, so a click arms an inline confirm bar on the target row
/// (Wardrobe's recipe). One confirm at a time; it is cancelled by ✕, closing the window, a loadout switch
/// (the worn loadout changed, so the armed source is no longer what would be copied) and logout. Validity is
/// ALSO re-checked at render and on ✓ (<see cref="CopyRules.ConfirmStillValid"/>), so a missed event can never
/// fire a stale copy.</para>
///
/// <para><b>Per-frame cost.</b> Everything the row Funcs read is cached: the worn slot + button label + row
/// names are rebuilt in <c>RefreshDsRows</c> (on <see cref="ILoadout.LoadoutsChanged"/>), the confirm texts
/// once in <see cref="ArmCopy"/> (and on a language change), and the gate is memoized per Update tick.</para>
///
/// <para><b>Threading.</b> The save's continuation may resume off the main thread, so it only POSTS the
/// outcome; <see cref="ConsumeCopyOutcome"/> (the Update tick) mirrors the binding, logs, toasts and releases
/// the in-flight latch — bindings and the window are main-thread state.</para>
/// </summary>
public sealed partial class Plugin
{
    private const int NoCopyTarget = int.MinValue;

    private int _copyTargetId = NoCopyTarget;   // loadout id of the row whose confirm bar is open
    private int _copyArmedWornId;               // the worn loadout when the confirm was armed
    private long _copyArmedCharId;              // the character when the confirm was armed
    private int _copyInFlight;                  // 1 while a save runs (any-thread latch)
    private CopyOutcome? _copyOutcome;          // posted by the save continuation, consumed on the tick

    // Confirm texts, formatted once per arm (and on a language change) — never per frame.
    private string _copyQuestion = "";
    private string _copyClassHint = "";
    private bool _copyShowClassHint;

    // Gate memo: computed at most once per Update tick however many rows poll it.
    private long _copyGateTick = -1;
    private CopyGate _copyGate;

    private sealed record CopyOutcome(LoadoutSlot Source, LoadoutSlot Target, long CharId, LoadoutResult Result);

    private CopyGate CurrentCopyGate()
    {
        if (_copyGateTick == _tick) return _copyGate;
        _copyGateTick = _tick;
        _copyGate = new CopyGate(
            ApiAvailable:    _services.Loadout.IsAvailable,
            ListLoaded:      _dsSlots.Count > 0,
            SwitchInFlight:  Volatile.Read(ref _inFlight) != 0,
            DsApplyInFlight: Volatile.Read(ref _applying) != 0,
            CopyInFlight:    Volatile.Read(ref _copyInFlight) != 0,
            ConfirmOpen:     ComputeConfirmOpen());
        return _copyGate;
    }

    private bool CopyConfirmOpen() => CurrentCopyGate().ConfirmOpen;

    private bool ComputeConfirmOpen()
        => _copyTargetId != NoCopyTarget
           && CopyRules.ConfirmStillValid(_copyArmedWornId, _services.Loadout.CurrentIndex, _copyArmedCharId,
               CurCharId, _dsWindow?.IsShown == true);

    // Invalidate the memo after a local state change so the same tick's repaint sees it.
    private void InvalidateCopyGate() => _copyGateTick = -1;

    private bool CopyButtonShown(int idx)
        => CopyRules.ShowCopyButton(DsRowAt(idx) is not null, DsRowAt(idx)?.IsCurrent == true, _wornSlot is not null);

    private bool IsCopyConfirming(int idx) => DsRowAt(idx) is { } slot && slot.Index == _copyTargetId && CopyConfirmOpen();

    // The class being copied is the LIVE class (the save takes what is worn now), falling back to the worn
    // plan's saved class while the live read has not resolved.
    private int SourceProfessionId() => _services.Loadout.LiveState?.ProfessionId ?? _wornSlot?.ProfessionId ?? 0;

    private bool ShowUnsavedHint() => CopyRules.ShowUnsavedHint(_services.LoadoutSave.HasUnsavedChanges);

    // Format the confirm bar's question + class hint for the armed target. Called on arm and on a language
    // change; the Funcs just return the cached strings.
    private void FormatCopyConfirmTexts()
    {
        LoadoutSlot? target = null;
        foreach (var s in _dsSlots) if (s.Index == _copyTargetId) { target = s; break; }
        _copyQuestion = _loc.TFormat("loadout.copy.confirm", _wornSlot?.Name ?? "");
        var prof = SourceProfessionId();
        _copyShowClassHint = target is not null && CopyRules.ShowClassChangeHint(target.ProfessionId, prof);
        _copyClassHint = _copyShowClassHint
            ? _loc.TFormat("loadout.copy.hintClass", target!.Name, _services.GameData.Combat.GetProfession(prof)?.Name ?? $"#{prof}")
            : "";
    }

    private void ArmCopy(int idx)
    {
        if (!CurrentCopyGate().ButtonEnabled) return;
        if (DsRowAt(idx) is not { } target || target.IsCurrent || _wornSlot is not { } worn) return;
        if (CurCharId == 0) return;
        _copyTargetId = target.Index;
        _copyArmedWornId = worn.Index;
        _copyArmedCharId = CurCharId;
        FormatCopyConfirmTexts();
        InvalidateCopyGate();
        _dsWindow?.MarkDirty();
    }

    private void CancelCopy()
    {
        if (_copyTargetId == NoCopyTarget) return;
        _copyTargetId = NoCopyTarget;
        InvalidateCopyGate();
        _dsWindow?.MarkDirty();
    }

    // Event-driven cancel points (✕ and ✓ clear it directly): a loadout switch / list change, logout, and
    // hiding the window. Stale state is harmless anyway (the gate re-validates), this just tidies it.
    private void CancelCopyIfStale()
    {
        InvalidateCopyGate();
        if (_copyTargetId != NoCopyTarget && !ComputeConfirmOpen()) CancelCopy();
    }

    private void OnCopyLogout() => CancelCopy();

    private void OnCopyLanguageChanged()
    {
        RefreshCopyTexts();
        if (_copyTargetId != NoCopyTarget) FormatCopyConfirmTexts();
        _dsWindow?.MarkDirty();
    }

    private void ConfirmCopy()
    {
        var targetId = _copyTargetId;
        var valid = ComputeConfirmOpen();
        CancelCopy();
        if (!valid || _wornSlot is not { } source) return;
        LoadoutSlot? target = null;
        foreach (var s in _dsSlots) if (s.Index == targetId) { target = s; break; }
        if (target is null || target.IsCurrent) return;
        // A switch or Deep-Slumber apply that started after the confirm opened: saving now would store a
        // half-applied setup (spec § 2.7). The framework also refuses a save during ITS switch.
        if (Volatile.Read(ref _inFlight) != 0 || Volatile.Read(ref _applying) != 0)
        {
            ReportCopyRefused(source, target, "Busy", CopyRules.BusyReasonKey);
            return;
        }
        if (Interlocked.CompareExchange(ref _copyInFlight, 1, 0) != 0) return;
        InvalidateCopyGate();
        _dsWindow?.MarkDirty();   // grey the buttons while the save runs
        _ = CopyAsync(source, target, CurCharId);
    }

    private async Task CopyAsync(LoadoutSlot source, LoadoutSlot target, long charId)
    {
        var result = LoadoutResult.Rejected;
        try
        {
            result = await _services.LoadoutSave.SaveCurrentToAsync(target.Index).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _services.Log.Warning($"[LoadoutSwitcher] copy {source.Index}→{target.Index} threw: {ex.Message}");
        }
        Volatile.Write(ref _copyOutcome, new CopyOutcome(source, target, charId, result));
    }

    // Update tick (main thread): apply the posted outcome.
    private void ConsumeCopyOutcome()
    {
        var outcome = Interlocked.Exchange(ref _copyOutcome, null);
        if (outcome is null) return;
        try { ApplyCopyOutcome(outcome); }
        finally
        {
            Interlocked.Exchange(ref _copyInFlight, 0);
            InvalidateCopyGate();
            RefreshDsRows();
        }
    }

    private void ApplyCopyOutcome(CopyOutcome o)
    {
        if (o.Result != LoadoutResult.Success)
        {
            // A server refusal: the game already showed its own reason (the wrapper's showError).
            ReportCopyRefused(o.Source, o.Target, o.Result.ToString(), CopyRules.RefusalReasonKey(o.Result));
            return;
        }

        _services.Log.Info("[LoadoutSwitcher] " + CopyRules.OutcomeLine(o.Source.Index, o.Target.Index, ok: true, "Success"));
        // Decision A: mirror the worn loadout's binding ONLY after the save succeeded, and only for the
        // character the copy was made on (a logout mid-save must never write into another character).
        if (o.CharId == CurCharId)
            MirrorBinding(o.Source.Index, o.Target.Index, CopyRules.DecideMirror(true, GetBinding(o.Source.Index) is not null));
        _services.NoticeTips.Create(NoticeTipType.GreenBar)
            .WithContent(_loc.TFormat("loadout.copy.toastCopied", o.Source.Name, o.Target.Name) + "\n" + _loc.T("loadout.copy.toastParts"))
            .Show();
    }

    // One always-on outcome line + the red toast with a second line saying WHY nothing changed.
    private void ReportCopyRefused(LoadoutSlot source, LoadoutSlot target, string result, string reasonKey)
    {
        _services.Log.Info("[LoadoutSwitcher] " + CopyRules.OutcomeLine(source.Index, target.Index, ok: false, result));
        _services.NoticeTips.Create(NoticeTipType.RedBar)
            .WithContent(_loc.TFormat("loadout.copy.toastRefused", target.Name) + "\n" + _loc.T(reasonKey))
            .Show();
    }
}
