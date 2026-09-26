using System.Collections.Generic;

namespace Stellar.LoadoutSwitcher;

/// <summary>What happens to the TARGET row's Deep-Slumber binding after a copy
/// (spec 2026-09-26-loadout-copy-design.md § 2.5, decision A "mirror").</summary>
internal enum BindingMirror
{
    /// <summary>Leave the target's binding exactly as it was (the save did not succeed).</summary>
    Untouched,

    /// <summary>The target's binding becomes an exact copy of the worn loadout's binding.</summary>
    CopySource,

    /// <summary>The worn loadout has no binding, so the target's binding is cleared (exact copy).</summary>
    ClearTarget,
}

/// <summary>Everything that can grey out the <c>← &lt;worn&gt;</c> button (spec § 2.7), as one value so the
/// decision stays a pure, testable function.</summary>
/// <param name="ApiAvailable">The loadout API (and the save API) is resolved.</param>
/// <param name="ListLoaded">The saved-loadout list has arrived.</param>
/// <param name="SwitchInFlight">A loadout switch started by this plugin has not finished.</param>
/// <param name="DsApplyInFlight">A Deep-Slumber apply is running.</param>
/// <param name="CopyInFlight">A copy (save) is running.</param>
/// <param name="ConfirmOpen">A copy confirm bar is open on some row (one confirm at a time).</param>
internal readonly record struct CopyGate(
    bool ApiAvailable, bool ListLoaded, bool SwitchInFlight, bool DsApplyInFlight, bool CopyInFlight, bool ConfirmOpen)
{
    /// <summary>True when the button accepts a click.</summary>
    public bool ButtonEnabled
        => ApiAvailable && ListLoaded && !SwitchInFlight && !DsApplyInFlight && !CopyInFlight && !ConfirmOpen;
}

/// <summary>Pure decision rules for the loadout copy feature — no game or framework calls, pinned by
/// <c>CopyRulesTests</c>.</summary>
internal static class CopyRules
{
    /// <summary>The third cell of a row shows <c>← &lt;worn&gt;</c> on every row EXCEPT the worn one (which
    /// keeps "Bind current"), and only when a worn loadout is known (the label names it).</summary>
    public static bool ShowCopyButton(bool rowExists, bool rowIsWorn, bool wornKnown)
        => rowExists && wornKnown && !rowIsWorn;

    /// <summary>"⚠ includes unsaved changes" — shown only when the game says the worn setup differs from
    /// its saved data.</summary>
    public static bool ShowUnsavedHint(bool hasUnsavedChanges) => hasUnsavedChanges;

    /// <summary>"⚠ changes &lt;T&gt; to &lt;class&gt;" — shown only when the target's saved class differs from
    /// the class being copied. An unresolved class (0) on either side never claims a change.</summary>
    public static bool ShowClassChangeHint(int targetProfessionId, int sourceProfessionId)
        => targetProfessionId != 0 && sourceProfessionId != 0 && targetProfessionId != sourceProfessionId;

    /// <summary>The binding mirror: nothing on a failed save; otherwise copy the source's binding, or clear
    /// the target's when the source has none.</summary>
    public static BindingMirror DecideMirror(bool saveSucceeded, bool sourceBound)
        => !saveSucceeded ? BindingMirror.Untouched
         : sourceBound    ? BindingMirror.CopySource
         :                  BindingMirror.ClearTarget;

    /// <summary>Applies <paramref name="mirror"/> to one character's bindings: <see cref="BindingMirror.CopySource"/>
    /// stores an independent copy of the source's binding under the target (a fresh model, never a shared
    /// reference — a later edit of one must not move the other); <see cref="BindingMirror.ClearTarget"/> (or a
    /// source that vanished meanwhile) removes the target's binding; <see cref="BindingMirror.Untouched"/>
    /// changes nothing.</summary>
    public static void ApplyMirror(IDictionary<int, BindingModel> bindings, int sourceId, int targetId, BindingMirror mirror)
    {
        if (mirror == BindingMirror.Untouched) return;
        if (mirror == BindingMirror.CopySource && bindings.TryGetValue(sourceId, out var source))
            bindings[targetId] = BindingModel.From(source.ToSetup());
        else
            bindings.Remove(targetId);
    }

    /// <summary>An open confirm stays valid only while the window is shown, the SAME loadout is worn as
    /// when it was armed (a switch changes what "the one you're wearing" is), and the SAME character is
    /// logged in (logout cancels). An unresolved character (0) is never valid.</summary>
    public static bool ConfirmStillValid(int armedWornId, int? currentWornId, long armedCharId, long currentCharId,
        bool windowShown)
        => windowShown && currentWornId == armedWornId && currentCharId != 0 && currentCharId == armedCharId;

    /// <summary>Cuts <paramref name="name"/> to <paramref name="maxChars"/> characters plus "…" when longer
    /// (the <c>← &lt;worn&gt;</c> button is a fixed 96 px; the confirm bar shows the full name).</summary>
    public static string Ellipsize(string name, int maxChars)
        => name.Length <= maxChars || maxChars <= 0 ? name : name.Substring(0, maxChars).TrimEnd() + "…";

    /// <summary>The always-on outcome log line body:
    /// <c>copy &lt;src&gt;→&lt;dst&gt; ok|refused(&lt;result&gt;)</c>.</summary>
    public static string OutcomeLine(int sourceId, int targetId, bool ok, string result)
        => ok ? $"copy {sourceId}→{targetId} ok" : $"copy {sourceId}→{targetId} refused({result})";
}
