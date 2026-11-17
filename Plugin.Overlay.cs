using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Domain.Loadout;
using Stellar.Abstractions.Plugins;
using Stellar.Abstractions.Services;

namespace Stellar.LoadoutSwitcher;

/// <summary>
/// The Loadout Switcher window, house style (approved redesign 2026-11-17,
/// <c>_scratch\prototypes\loadoutswitcher-redesign-APPROVED.md</c>, all recommended A options). Top → bottom:
///   ToolHeader [40 px accent tile + swap-arrows glyph | bold title over ONE live status line]
///   RESTORE FACTORS  hint "Season 3" + "?" · [label | 64×24 On/Off chip] (= the AutoApply setting)
///   LOADOUTS         hint "● = wearing" + "?" · a fixed 8-row list (28 px pitch), scrolling past 8
///   footer           one fixed-height line: feedback / copy-confirm warnings / a standing hint
/// Every line is pinned (struts, fixed cells, every pill/chip variant the same size), so the window keeps ONE height in
/// every state. No per-frame work of its own: the framework's scheduled refresh polls the Funcs, which only hand back
/// cached strings (Plugin-Performance-Rules §2/§5). Rows: <c>Plugin.Overlay.Rows.cs</c>; copy confirm:
/// <c>Plugin.Copy.Overlay.cs</c>; feedback + footer: <c>Plugin.Overlay.Feedback.cs</c>; help: <c>Plugin.Help.cs</c>.
///
/// <para><b>Season gate in the UI.</b> The restore only runs in Season 3 (<see cref="SeasonGate"/>; the apply path
/// re-reads the season itself at apply time). The window shows the gate from a CACHED season id — read when the window
/// opens, on <see cref="ILoadout.LoadoutsChanged"/>, and at most every <see cref="SeasonPollS"/> while visible — never
/// per frame (a Lua round-trip). Outside Season 3 (incl. unknown / pre-sync 0) the header and the hint turn amber and
/// the chip is recessed showing Off; the saved AutoApply value is NOT modified. Copy is not season-gated.</para>
/// </summary>
public sealed partial class Plugin
{
    private const float WindowWidth  = 520f;
    private const float StatusLineH  = 16f;
    private const float SettingRowH  = 28f;
    private const float FooterLineH  = 20f;   // ≥ the plain 14 px "✓"/"⚠" glyph line, so feedback never grows the window
    private const float SeasonPollS  = 2f;

    private IWindowControl _dsWindow = null!;
    private IHotkeyAction _dsToggle = null!;
    private IDisposable _dsLauncherEntry = null!;

    private void InitOverlay()
    {
        RefreshDsRows();

        // The help popover first: the main window's "?" dots read _helpWindow while it is (pre)built.
        _helpWindow = RegisterHelpWindow();

        _dsWindow = _services.Windows.Register(new WindowRegistration(
            new WindowSpec(
                Id:          "loadoutswitcher.dsbindings",
                Title:       _loc.T(KeyTitle),
                // 520 = the 466 px row (3 + 5 + 150 + 6 + 150 + 6 + 116 + 6 + 24) + the 9 px scroll inset + the panel's
                // 2 × 10 padding + GlassMenu's 2 × 12 body padding (+1 px slack). X/Y = offset from the top-right anchor;
                // 0 height = content-sized (the content itself never changes height). A non-resizable anchored window
                // takes its size from DefaultRect, so a 500 px rect saved by 2.9 does not clip the new layout.
                DefaultRect: new WindowRect(20f, 120f, WindowWidth, 0f),
                Category:    WindowCategory.Tools,
                Style:       WindowPanelStyle.GlassMenu)
            {
                StartVisible = false, Closable = true, Draggable = true,
                Anchor = WindowAnchor.TopRight,
                // Live-localized chrome title (same text as the header and the launcher tile); Title above is the fallback.
                TitleProvider = () => _loc.T(KeyTitle),
                // Built while hidden so the first open doesn't pay for the 24-row pool (Plugin-Performance-Rules §5).
                // Every Func is safe without game state (it only reads cached plugin fields), and the list reports 0
                // rows while hidden (DsVisibleRows), so the hidden build binds nothing.
                Prebuild = true,
                ShouldRender = () => _services.ClientState.Phase == GamePhase.World
                                     && (_services.ClientState.UiState & GameUIState.Loading) == 0,
            },
            BuildDsRoot(),
            OnClose: () => { CancelCopy(); CloseHelp(); _dsWindow.SetVisiblePersist(false); }));

        _dsToggle = _services.Hotkeys.DeclareAction(
            new HotkeyAction(
                Id:               "loadout.window.toggle",
                Description:      _loc.T("loadout.hotkey.windowToggle"),
                SuggestedDefault: null),
            callback: ToggleDsWindow);

        // Launcher-rail tile so the window is discoverable (not hotkey-only) — the hotkey has no
        // default binding, so without this the overlay is invisible to a new user. TitleProvider makes
        // the tile re-localize LIVE on a language change (Title alone is a captured string); Title stays
        // the fixed literal "Loadout Switcher" — the stable pin-identity key (ILauncher.cs:49-50) — so a
        // pinned tile survives a language change.
        _dsLauncherEntry = _services.Launcher.Register(new LauncherEntry(
            "Loadout Switcher", IconPng: LoadLauncherIcon(), IconKey: null,
            OnOpen: ToggleDsWindow)
        {
            ShouldShow = () => _services.ClientState.Phase == GamePhase.World,
            TitleProvider = () => _loc.T(KeyTitle),
        });

        _services.Loadout.LoadoutsChanged += OnDsLoadoutsChanged;
        _services.ClientState.Logout += OnOverlayLogout;
        _loc.LanguageChanged += OnCopyLanguageChanged;
    }

    // Hotkey + launcher tile: hiding the window cancels an open copy confirm (spec § 2.3) and closes the help tip;
    // opening it reads the season first, so the gate shows correctly on the very first frame.
    private void ToggleDsWindow()
    {
        if (_dsWindow.IsShown)
        {
            CancelCopy();
            CloseHelp();
            _dsWindow.SetVisiblePersist(false);
            return;
        }
        RefreshSeason();
        _dsWindow.SetVisiblePersist(true);
    }

    private void DisposeOverlay()
    {
        _services.Loadout.LoadoutsChanged -= OnDsLoadoutsChanged;
        _services.ClientState.Logout -= OnOverlayLogout;
        _loc.LanguageChanged -= OnCopyLanguageChanged;
        try { _dsLauncherEntry?.Dispose(); } catch { /* disposal must not throw */ }
        try { _dsToggle?.Dispose(); } catch { /* disposal must not throw */ }
        try { _helpWindow?.Remove(); } catch { /* disposal must not throw */ }
        try { _dsWindow?.Remove(); } catch { /* disposal must not throw */ }
    }

    private void OnDsLoadoutsChanged()
    {
        RefreshDsRows();
        CancelCopyIfStale();   // a switch changes the worn loadout → an armed copy no longer means the same thing
        RefreshSeason();       // a switch / list change is a natural moment to re-check (rare, never per frame)
    }

    // Logout: synchronously (Update is world-gated, so no Post) drop an armed copy and close both windows, so nothing
    // stays up through the loading screen (house rule, MultiTool 3554a1e). SetVisible, not SetVisiblePersist: logging
    // out is not the player closing the window.
    private void OnOverlayLogout()
    {
        CancelCopy();
        CloseHelp();
        if (_dsWindow.IsShown) _dsWindow.SetVisible(false);
    }

    // The launcher-rail tile icon (embedded swap-arrows PNG, unchanged). Null → the launcher's default glyph.
    private static byte[]? LoadLauncherIcon() => CachedPng("loadoutswitcher-icon.png");

    // ── season cache ──────────────────────────────────────────────────────────────────────────────────────────────
    private double? _seasonCached;
    private float _seasonReadAt = float.NegativeInfinity;

    private bool SeasonBlocked() => !SeasonGate.AllowsApply(_seasonCached);

    // Main thread only (ReadCurrentSeasonId → ILua): the window toggle, LoadoutsChanged and the Update tick all are.
    private void RefreshSeason()
    {
        var season = ReadCurrentSeasonId();
        _seasonReadAt = _services.Framework.TimeNow;
        if (season == _seasonCached) return;
        _seasonCached = season;
        _dsWindow?.MarkDirty();
    }

    // OnUpdate: one bool + one float compare while hidden or between polls. A window restored visible at login (its
    // persisted state) is read on the first tick (_seasonReadAt starts at -∞).
    private void SeasonPollTick()
    {
        if (_dsWindow?.IsShown != true) return;
        if (_services.Framework.TimeNow - _seasonReadAt >= SeasonPollS) RefreshSeason();
    }

    // ── layout ────────────────────────────────────────────────────────────────────────────────────────────────────
    private HudElement BuildDsRoot() => new ColumnElement(new HudElement[]
    {
        BuildHeader(),
        SectionPanel(KeyRestoreCaption, SectionHint(RestoreHintText, RestoreHintColor), HelpDot(HelpTopic.Restore), new[]
        {
            FixedLine(SettingRowH, new RowElement(new HudElement[]
            {
                new CellElement(new TextElement(() => _loc.T(KeyRestoreLabel),
                    Color: () => SeasonBlocked() ? MutedColor() : TextColor(), NoWrap: true) { Ellipsize = true }, Weight: 1f),
                OnOffChip(() => AutoApply, v => { AutoApply = v; _dsWindow.MarkDirty(); }, SeasonBlocked),
            }, Gap: 8f)),
        }),
        SectionPanel(KeyLoadoutsCaption, WearingHint(), HelpDot(HelpTopic.Loadouts), new[] { BuildListOrEmpty() }),
        BuildFooter(),
    }, Gap: 8f);

    // "● = wearing", right-aligned: the "●" is PLAIN text (the plain menu font draws it, as the worn row's name always
    // has) because it is a Geometric Shapes glyph — the block the TMP faces lack ("■" / "▾" draw blank, KB §6); the rest
    // is the usual 12 px hint. The flexible spacer pushes both to the right edge.
    private HudElement WearingHint() => new RowElement(new HudElement[]
    {
        new SpacerElement(0f),
        new TextElement(() => WornDot, Color: () => MutedColor(), NoWrap: true),
        SectionHint(() => _loc.T(KeyLoadoutsHint), MutedColor),
    }, Gap: 4f);

    // ── header ────────────────────────────────────────────────────────────────────────────────────────────────────
    // The text column sits in a weighted cell, so a long status (a long loadout name) ellipsizes.
    private HudElement BuildHeader() => new RowElement(new HudElement[]
    {
        IconBox(() => HeaderIconPng(ToolGlyphSize), ToolIconSize, ToolGlyphSize),
        new CellElement(new ColumnElement(new HudElement[]
        {
            new TextElement(() => _loc.T(KeyTitle), Color: () => TextColor(), Emphasis: true, NoWrap: true) { Ellipsize = true },
            FixedLine(StatusLineH, new RowElement(new HudElement[]
            {
                StatusDot(StatusDotColor),
                new CellElement(new TextElement(StatusText, Color: () => StatusTextColor(), NoWrap: true, FontSize: 12)
                    { RichEmoji = true, Ellipsize = true }, Weight: 1f),
            }, Gap: 5f)),
        }, Gap: 2f), Weight: 1f),
    }, Gap: 10f);

    private ColorRgba StatusDotColor() => CurrentHeaderState() switch
    {
        HeaderState.On      => SuccessColor(),
        HeaderState.Blocked => CautionColor(),
        _                   => MutedColor(),
    };

    private readonly ContrastCache _statusInk = new();

    // Amber text only when blocked (a solid status colour on the window background keeps 3:1, like AutoFishing's
    // status word); the on / off lines stay muted with only the dot coloured (prototype .st).
    private ColorRgba StatusTextColor() => CurrentHeaderState() == HeaderState.Blocked
        ? _statusInk.Get(CautionColor(), WindowBg(), SolidMinContrast)
        : MutedColor();

    // ── RESTORE FACTORS hint ──────────────────────────────────────────────────────────────────────────────────────
    private readonly ContrastCache _hintInk = new();

    private string RestoreHintText() => _loc.T(SeasonBlocked() ? KeyRestoreHintBlocked : KeyRestoreHint);

    private ColorRgba RestoreHintColor() => SeasonBlocked()
        ? _hintInk.Get(CautionColor(), PanelColor(), PillMinContrast)
        : MutedColor();
}
