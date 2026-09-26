using System;
using System.Linq;
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

    private sealed record CopyOutcome(LoadoutSlot Source, LoadoutSlot Target, long CharId, LoadoutResult Result);

    private LoadoutSlot? WornSlot() => _dsSlots.FirstOrDefault(s => s.IsCurrent);

    private CopyGate CurrentCopyGate() => new(
        ApiAvailable:    _services.Loadout.IsAvailable,
        ListLoaded:      _dsSlots.Count > 0,
        SwitchInFlight:  Volatile.Read(ref _inFlight) != 0,
        DsApplyInFlight: Volatile.Read(ref _applying) != 0,
        CopyInFlight:    Volatile.Read(ref _copyInFlight) != 0,
        ConfirmOpen:     CopyConfirmOpen());

    private bool CopyConfirmOpen()
        => _copyTargetId != NoCopyTarget
           && CopyRules.ConfirmStillValid(_copyArmedWornId, _services.Loadout.CurrentIndex, _copyArmedCharId,
               CurCharId, _dsWindow?.IsShown == true);

    private bool CopyButtonShown(int idx)
        => CopyRules.ShowCopyButton(DsRowAt(idx) is not null, DsRowAt(idx)?.IsCurrent == true, WornSlot() is not null);

    private bool IsCopyConfirming(int idx) => DsRowAt(idx) is { } slot && slot.Index == _copyTargetId && CopyConfirmOpen();

    private string CopyQuestion() => _loc.TFormat("loadout.copy.confirm", WornSlot()?.Name ?? "");

    // The class being copied is the LIVE class (the save takes what is worn now), falling back to the worn
    // plan's saved class while the live read has not resolved.
    private int SourceProfessionId() => _services.Loadout.LiveState?.ProfessionId ?? WornSlot()?.ProfessionId ?? 0;

    private bool ShowUnsavedHint() => CopyRules.ShowUnsavedHint(_services.LoadoutSave.HasUnsavedChanges);

    private bool ShowClassHint(int idx)
        => DsRowAt(idx) is { } target && CopyRules.ShowClassChangeHint(target.ProfessionId, SourceProfessionId());

    private string ClassHintText(int idx)
    {
        if (DsRowAt(idx) is not { } target) return "";
        var prof = SourceProfessionId();
        var className = _services.GameData.Combat.GetProfession(prof)?.Name ?? $"#{prof}";
        return _loc.TFormat("loadout.copy.hintClass", target.Name, className);
    }

    private void ArmCopy(int idx)
    {
        if (!CurrentCopyGate().ButtonEnabled) return;
        if (DsRowAt(idx) is not { } target || target.IsCurrent || WornSlot() is not { } worn) return;
        if (CurCharId == 0) return;
        _copyTargetId = target.Index;
        _copyArmedWornId = worn.Index;
        _copyArmedCharId = CurCharId;
        _dsWindow?.MarkDirty();
    }

    private void CancelCopy()
    {
        if (_copyTargetId == NoCopyTarget) return;
        _copyTargetId = NoCopyTarget;
        _dsWindow?.MarkDirty();
    }

    // Event-driven cancel points (✕ and ✓ clear it directly): a loadout switch / list change, logout, and
    // hiding the window. Stale state is harmless anyway (CopyConfirmOpen re-validates), this just tidies it.
    private void CancelCopyIfStale()
    {
        if (_copyTargetId != NoCopyTarget && !CopyConfirmOpen()) CancelCopy();
    }

    private void OnCopyLogout() => CancelCopy();

    private void ConfirmCopy()
    {
        var targetId = _copyTargetId;
        var valid = CopyConfirmOpen();
        CancelCopy();
        if (!valid || WornSlot() is not { } source) return;
        var target = _dsSlots.FirstOrDefault(s => s.Index == targetId);
        if (target is null || target.IsCurrent) return;
        // A switch or Deep-Slumber apply that started after the confirm opened: saving now would store a
        // half-applied setup (spec § 2.7). The framework also refuses a save during ITS switch.
        if (Volatile.Read(ref _inFlight) != 0 || Volatile.Read(ref _applying) != 0)
        {
            ReportCopyRefused(source, target, "Busy");
            return;
        }
        if (Interlocked.CompareExchange(ref _copyInFlight, 1, 0) != 0) return;
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
            RefreshDsRows();
        }
    }

    private void ApplyCopyOutcome(CopyOutcome o)
    {
        var ok = o.Result == LoadoutResult.Success;
        _services.Log.Info("[LoadoutSwitcher] " + CopyRules.OutcomeLine(o.Source.Index, o.Target.Index, ok, o.Result.ToString()));
        if (!ok)
        {
            // The game already showed its own refusal reason (the wrapper's showError) when IT refused.
            ShowCopyRefusedToast(o.Target);
            return;
        }

        // Decision A: mirror the worn loadout's binding ONLY after the save succeeded, and only for the
        // character the copy was made on (a logout mid-save must never write into another character).
        if (o.CharId == CurCharId)
            MirrorBinding(o.Source.Index, o.Target.Index, CopyRules.DecideMirror(true, GetBinding(o.Source.Index) is not null));
        _services.NoticeTips.Create(NoticeTipType.GreenBar)
            .WithContent(_loc.TFormat("loadout.copy.toastCopied", o.Source.Name, o.Target.Name) + "\n" + _loc.T("loadout.copy.toastParts"))
            .Show();
    }

    private void ReportCopyRefused(LoadoutSlot source, LoadoutSlot target, string reason)
    {
        _services.Log.Info("[LoadoutSwitcher] " + CopyRules.OutcomeLine(source.Index, target.Index, ok: false, reason));
        ShowCopyRefusedToast(target);
    }

    private void ShowCopyRefusedToast(LoadoutSlot target)
        => _services.NoticeTips.Create(NoticeTipType.RedBar)
            .WithContent(_loc.TFormat("loadout.copy.toastRefused", target.Name)).Show();
}
