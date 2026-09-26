using System.Collections.Generic;
using Xunit;

namespace Stellar.LoadoutSwitcher.Tests;

/// <summary>
/// Pins the loadout-copy decisions (CopyRules): which rows show the "← &lt;worn&gt;" button, when it is
/// greyed, which confirm hints show, when an armed confirm is still valid, and the Deep-Slumber binding
/// mirror (decision A). Origin: owner-approved spec docs/superpowers/specs/2026-09-26-loadout-copy-design.md
/// (Discord "Copy Loadout", Midokuni 2026-09-17).
/// </summary>
public sealed class CopyRulesTests
{
    // ── Button visibility (spec § 2.1) ──────────────────────────────────────────────────────────

    [Fact]
    public void Every_non_worn_row_shows_the_button()
        => Assert.True(CopyRules.ShowCopyButton(rowExists: true, rowIsWorn: false, wornKnown: true));

    [Fact]
    public void The_worn_row_keeps_Bind_current_and_never_shows_the_button()
        => Assert.False(CopyRules.ShowCopyButton(rowExists: true, rowIsWorn: true, wornKnown: true));

    [Fact]
    public void No_button_when_the_worn_loadout_is_unknown_or_the_row_is_empty()
    {
        Assert.False(CopyRules.ShowCopyButton(rowExists: true, rowIsWorn: false, wornKnown: false));
        Assert.False(CopyRules.ShowCopyButton(rowExists: false, rowIsWorn: false, wornKnown: true));
    }

    // ── Disabled states (spec § 2.7) ────────────────────────────────────────────────────────────

    private static readonly CopyGate Ready = new(true, true, false, false, false, false);

    [Fact]
    public void Ready_gate_enables_the_button() => Assert.True(Ready.ButtonEnabled);

    [Fact]
    public void Each_blocking_condition_greys_the_button()
    {
        Assert.False((Ready with { ApiAvailable = false }).ButtonEnabled);
        Assert.False((Ready with { ListLoaded = false }).ButtonEnabled);
        Assert.False((Ready with { SwitchInFlight = true }).ButtonEnabled);
        Assert.False((Ready with { DsApplyInFlight = true }).ButtonEnabled);
        Assert.False((Ready with { CopyInFlight = true }).ButtonEnabled);
        Assert.False((Ready with { ConfirmOpen = true }).ButtonEnabled);   // one confirm at a time
    }

    // ── Hint selection (spec § 2.3): unsaved × class change ─────────────────────────────────────

    [Theory]
    [InlineData(false, 9, 9, false, false)]   // plain "Overwrite with X?"
    [InlineData(true, 9, 9, true, false)]     // unsaved only
    [InlineData(false, 9, 2, false, true)]    // class change only (Heavy Guardian ← Frost Mage)
    [InlineData(true, 9, 2, true, true)]      // both
    public void Hints_show_only_when_true(bool unsaved, int targetProf, int sourceProf, bool expectUnsaved, bool expectClass)
    {
        Assert.Equal(expectUnsaved, CopyRules.ShowUnsavedHint(unsaved));
        Assert.Equal(expectClass, CopyRules.ShowClassChangeHint(targetProf, sourceProf));
    }

    [Fact]
    public void An_unresolved_class_never_claims_a_class_change()
    {
        Assert.False(CopyRules.ShowClassChangeHint(0, 2));
        Assert.False(CopyRules.ShowClassChangeHint(9, 0));
    }

    // ── Confirm cancel rules (spec § 2.3) ───────────────────────────────────────────────────────

    [Fact]
    public void An_armed_confirm_is_valid_while_nothing_changed()
        => Assert.True(CopyRules.ConfirmStillValid(4, 4, 77, 77, windowShown: true));

    [Fact]
    public void Switching_loadout_closing_the_window_or_logging_out_cancels()
    {
        Assert.False(CopyRules.ConfirmStillValid(4, 3, 77, 77, windowShown: true));     // worn plan changed
        Assert.False(CopyRules.ConfirmStillValid(4, null, 77, 77, windowShown: true));  // worn plan unknown
        Assert.False(CopyRules.ConfirmStillValid(4, 4, 77, 77, windowShown: false));    // window closed
        Assert.False(CopyRules.ConfirmStillValid(4, 4, 77, 0, windowShown: true));      // logged out
        Assert.False(CopyRules.ConfirmStillValid(4, 4, 77, 78, windowShown: true));     // another character
    }

    // ── Binding mirror (decision A, spec § 2.5) ─────────────────────────────────────────────────

    [Fact]
    public void Mirror_decision_follows_the_save_and_the_source_binding()
    {
        Assert.Equal(BindingMirror.CopySource, CopyRules.DecideMirror(saveSucceeded: true, sourceBound: true));
        Assert.Equal(BindingMirror.ClearTarget, CopyRules.DecideMirror(saveSucceeded: true, sourceBound: false));
        Assert.Equal(BindingMirror.Untouched, CopyRules.DecideMirror(saveSucceeded: false, sourceBound: true));
        Assert.Equal(BindingMirror.Untouched, CopyRules.DecideMirror(saveSucceeded: false, sourceBound: false));
    }

    private static BindingModel Model(int prof, int area, params int[] factorPairs)
    {
        var a = new BindingModel.AreaModel { AreaId = area, NormalNodes = new List<int> { 1, 2 } };
        for (var i = 0; i + 1 < factorPairs.Length; i += 2) a.Factors.Add(new[] { factorPairs[i], factorPairs[i + 1] });
        return new BindingModel { ProfessionId = prof, Areas = new List<BindingModel.AreaModel> { a } };
    }

    [Fact]
    public void Source_bound_copies_an_independent_binding_onto_the_target()
    {
        var b = new Dictionary<int, BindingModel> { [4] = Model(2, 5, 10, 100), [3] = Model(9, 7, 20, 200) };

        CopyRules.ApplyMirror(b, sourceId: 4, targetId: 3, BindingMirror.CopySource);

        Assert.Equal(2, b[3].ProfessionId);
        Assert.Equal(5, b[3].Areas[0].AreaId);
        Assert.Equal(new[] { 10, 100 }, b[3].Areas[0].Factors[0]);
        Assert.NotSame(b[4], b[3]);                                   // not a shared reference
        b[3].Areas[0].Factors.Clear();
        Assert.Single(b[4].Areas[0].Factors);                         // editing the copy leaves the source alone
    }

    [Fact]
    public void Source_unbound_clears_the_target_binding()
    {
        var b = new Dictionary<int, BindingModel> { [3] = Model(9, 7, 20, 200) };

        CopyRules.ApplyMirror(b, sourceId: 4, targetId: 3, BindingMirror.ClearTarget);

        Assert.False(b.ContainsKey(3));
        Assert.False(b.ContainsKey(4));
    }

    [Fact]
    public void A_refused_copy_changes_no_binding()
    {
        var b = new Dictionary<int, BindingModel> { [4] = Model(2, 5, 10, 100), [3] = Model(9, 7, 20, 200) };
        var target = b[3];

        CopyRules.ApplyMirror(b, sourceId: 4, targetId: 3, BindingMirror.Untouched);

        Assert.Same(target, b[3]);
        Assert.Equal(2, b.Count);
    }

    // ── Label + log line ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Long_worn_names_ellipsize_short_ones_do_not()
    {
        Assert.Equal("Ici-LF", CopyRules.Ellipsize("Ici-LF", 10));
        Assert.Equal("Frost Mage…", CopyRules.Ellipsize("Frost Mage Lightning", 10));
    }

    [Fact]
    public void Outcome_line_matches_the_spec_grammar()
    {
        Assert.Equal("copy 4→3 ok", CopyRules.OutcomeLine(4, 3, ok: true, "Success"));
        Assert.Equal("copy 4→3 refused(Rejected)", CopyRules.OutcomeLine(4, 3, ok: false, "Rejected"));
    }
}
