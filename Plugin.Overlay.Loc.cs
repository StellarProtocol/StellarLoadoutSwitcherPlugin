using System;

namespace Stellar.LoadoutSwitcher;

// Localized text for the house-style window (CLAUDE.md Rule 10: every user-facing string is a key in ALL Lang/*.json).
// Plain labels are `() => _loc.T(key)` Funcs — T hands back the catalog's own string instance, so a poll allocates nothing
// and a live language switch shows on the next refresh. Text COMPOSED from catalog strings (upper-cased captions, the
// header status line, footer messages, the Loadouts help body) is built once and cached against _langGen, which
// ILocalization.LanguageChanged bumps (OnCopyLanguageChanged) — rebuilt once per language switch, never per refresh
// (Plugin-Performance-Rules §2).
public sealed partial class Plugin
{
    private const string KeyTitle              = "loadout.dsbindings.title";
    private const string KeyOn                 = "loadout.ui.on";
    private const string KeyOff                = "loadout.ui.off";
    private const string KeyStatusOn           = "loadout.ui.status.on";
    private const string KeyStatusOff          = "loadout.ui.status.off";
    private const string KeyStatusWearing      = "loadout.ui.status.wearing";
    private const string KeyStatusBlocked      = "loadout.ui.status.seasonBlocked";
    private const string KeyRestoreCaption     = "loadout.ui.restore.caption";
    private const string KeyRestoreHint        = "loadout.ui.restore.hint";
    private const string KeyRestoreHintBlocked = "loadout.ui.restore.hintBlocked";
    private const string KeyRestoreLabel       = "loadout.ui.restore.label";
    private const string KeyLoadoutsCaption    = "loadout.ui.loadouts.caption";
    private const string KeyLoadoutsHint       = "loadout.ui.loadouts.hint";
    private const string KeyBindCurrent        = "loadout.dsbindings.bindCurrent";
    private const string KeyCopyHere           = "loadout.ui.copyHere";
    private const string KeyFlashBound         = "loadout.ui.flash.bound";
    private const string KeyFlashCopied        = "loadout.ui.flash.copied";
    private const string KeyOverwrite          = "loadout.ui.overwrite";
    private const string KeyCancel             = "loadout.ui.cancel";
    private const string KeyEmpty              = "loadout.dsbindings.empty";
    private const string KeyFooterDefault      = "loadout.ui.footer.default";
    private const string KeyFooterBlocked      = "loadout.ui.footer.seasonBlocked";
    private const string KeyFooterBound        = "loadout.ui.footer.bound";
    private const string KeyFooterCopied       = "loadout.ui.footer.copied";
    private const string KeyHelpTitle          = "loadout.ui.help.title";
    private const string KeyHelpRestoreSub     = "loadout.ui.help.restore.sub";
    private const string KeyHelpRestoreBody    = "loadout.ui.help.restore.body";
    private const string KeyHelpLoadoutsSub    = "loadout.ui.help.loadouts.sub";
    private const string KeyHelpBind           = "loadout.ui.help.bindCurrent";
    private const string KeyHelpCopy           = "loadout.ui.help.copyHere";
    private const string KeyHelpClear          = "loadout.ui.help.clear";
    private const string KeyHelpTermSep        = "loadout.ui.help.termSep";

    private int _langGen;   // bumped on every live language switch; LocComposed caches compare against it

    // A string composed from catalog text, rebuilt only when _langGen moves (or Invalidate is called) — a poll hands back
    // the same instance.
    private sealed class LocComposed
    {
        private readonly Func<string> _build;
        private int _gen = -1;
        private string _s = string.Empty;

        public LocComposed(Func<string> build) => _build = build;

        public string Get(int gen)
        {
            if (gen != _gen) { _gen = gen; _s = _build(); }
            return _s;
        }

        public void Invalidate() => _gen = -1;
    }

    // Section captions are drawn UPPERCASE (house style); upper-case the catalog text once per language.
    private Func<string> UpperLoc(string key)
    {
        var cache = new LocComposed(() => _loc.T(key).ToUpperInvariant());
        return () => cache.Get(_langGen);
    }

    // ── header status line ────────────────────────────────────────────────────────────────────────────────────────
    // "Restore on · wearing <name>" / "Restore off · wearing <name>" / (season-blocked) "Restore off · this season isn't
    // supported yet". Rebuilt only when one of its inputs moves (the state, the worn loadout's name, the language).
    private enum HeaderState : byte { On, Off, Blocked }

    private HeaderState _statusState;
    private string? _statusWorn;
    private int _statusGen = -1;
    private string _statusText = string.Empty;

    private HeaderState CurrentHeaderState() => SeasonBlocked() ? HeaderState.Blocked
                                              : AutoApply ? HeaderState.On : HeaderState.Off;

    private string StatusText()
    {
        var state = CurrentHeaderState();
        var worn = _wornSlot?.Name;
        if (state == _statusState && ReferenceEquals(worn, _statusWorn) && _statusGen == _langGen) return _statusText;
        _statusState = state;
        _statusWorn = worn;
        _statusGen = _langGen;
        _statusText = state == HeaderState.Blocked ? _loc.T(KeyStatusBlocked)
            : (state == HeaderState.On ? _loc.T(KeyStatusOn) : _loc.T(KeyStatusOff))
              + (worn is null ? string.Empty : " · " + _loc.TFormat(KeyStatusWearing, worn));
        return _statusText;
    }

    // ── Loadouts help body ────────────────────────────────────────────────────────────────────────────────────────
    // Three lines, each led by the BOLD control name exactly as the row shows it ("Bind current", "Copy here", "✕"),
    // via inline <b> rich text in the plain help text (uGUI Text keeps rich text on; the plain path draws "✕"). The ✕
    // itself is left regular: a bold ✕ has drawn blank before (KB Stellar-Design-Language §6). The term → text
    // separator is a catalog string, so each language joins them its own way (" " in English, "：" in Japanese).
    private LocComposed? _loadoutsHelpBody;

    private string LoadoutsHelpBody() => (_loadoutsHelpBody ??= new LocComposed(() =>
    {
        var sep = _loc.T(KeyHelpTermSep);
        return "<b>" + _loc.T(KeyBindCurrent) + "</b>" + sep + _loc.T(KeyHelpBind) + "\n"
             + "<b>" + _loc.T(KeyCopyHere) + "</b>" + sep + _loc.T(KeyHelpCopy) + "\n"
             + "✕" + sep + _loc.T(KeyHelpClear);
    })).Get(_langGen);
}
