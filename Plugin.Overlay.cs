using System;
using System.Collections.Generic;
using System.Linq;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Domain.Loadout;
using Stellar.Abstractions.Plugins;
using Stellar.Abstractions.Services;

namespace Stellar.LoadoutSwitcher;

/// <summary>
/// The Deep-Slumber binding-manager overlay: one row per saved loadout showing its bound setup
/// (line area + factor count) or "no binding", with a per-row <b>Clear</b>, a <b>Bind current</b>
/// control that is only present on the currently-active loadout row (you can only snapshot the LIVE
/// Deep-Slumber, which belongs to the current class), and a master <b>Auto-apply on switch</b> toggle.
/// Storage/logic live in <see cref="Plugin"/>'s <c>GetBinding</c>/<c>ClearBinding</c>/<c>BindCurrent</c>/
/// <c>AutoApply</c> (Tasks 6/7); this partial is presentation only. Row status is cached in
/// <see cref="_dsBoundStatus"/> and refreshed on <see cref="ILoadout.LoadoutsChanged"/> / bind / clear —
/// never re-deserialized inside a poll-diffed row <c>Func</c>.
/// </summary>
public sealed partial class Plugin
{
    // Over-provisioned above any realistic saved-loadout count (the hotkey path caps at 10, but the game
    // permits more loadouts). Rows are built ONCE; LoadoutsChanged only updates state + MarkDirty.
    private const int DsRowPoolSize = 24;

    private IWindowControl _dsWindow = null!;
    private IHotkeyAction _dsToggle = null!;
    private IDisposable _dsLauncherEntry = null!;
    private IReadOnlyList<LoadoutSlot> _dsSlots = Array.Empty<LoadoutSlot>();
    // loadoutId -> bound-setup NUMBERS (area count, the single area's id for the 1-area label, factor
    // count); PRESENCE means "bound" (drives the accent colour + Clear-enabled). Cache the NUMBERS, not
    // a localized string, so the status re-localizes LIVE when the language changes — DsStatusText
    // formats it each frame. A cached string would stay in whatever language was active when the loadout
    // last changed (owner smoke 2026-08-25: switching Thai→English left bound rows reading Thai).
    private readonly Dictionary<int, (int areaCount, int firstAreaId, int factors)> _dsBoundStatus = new();

    private void InitOverlay()
    {
        RefreshDsRows();

        _dsWindow = _services.Windows.Register(new WindowRegistration(
            new WindowSpec(
                Id:          "loadoutswitcher.dsbindings",
                Title:       _loc.T("loadout.dsbindings.title"),
                // Width 500 = the 448px column budget (130+140+100+60 + 3×6 gaps) + GlassMenu body
                // padding (24) + scrollbar inset (9), with margin — a narrower window clips the Clear
                // column past the ScrollElement RectMask2D (ux-ui review, computed from WindowBuilder
                // constants). X/Y = offset from the top-right anchor; 0 height = content-sized.
                DefaultRect: new WindowRect(20f, 120f, 500f, 0f),
                Category:    WindowCategory.Tools,
                Style:       WindowPanelStyle.GlassMenu)
            {
                StartVisible = false, Closable = true, Draggable = true,
                Anchor = WindowAnchor.TopRight,
                ShouldRender = () => _services.ClientState.Phase == GamePhase.World
                                     && (_services.ClientState.UiState & GameUIState.Loading) == 0,
            },
            BuildDsRoot(),
            OnClose: () => { CancelCopy(); _dsWindow.SetVisiblePersist(false); }));

        _dsToggle = _services.Hotkeys.DeclareAction(
            new HotkeyAction(
                Id:               "loadout.window.toggle",
                Description:      _loc.T("loadout.hotkey.windowToggle"),
                SuggestedDefault: null),
            callback: ToggleDsWindow);

        // Launcher-rail tile so the window is discoverable (not hotkey-only) — the hotkey has no
        // default binding, so without this the overlay is invisible to a new user. TitleProvider makes
        // the tile re-localize LIVE on a language change (Title alone is a captured string); Title stays
        // the stable pinned-state identity.
        _dsLauncherEntry = _services.Launcher.Register(new LauncherEntry(
            _loc.T("loadout.dsbindings.title"), IconPng: LoadLauncherIcon(), IconKey: null,
            OnOpen: ToggleDsWindow)
        {
            ShouldShow = () => _services.ClientState.Phase == GamePhase.World,
            TitleProvider = () => _loc.T("loadout.dsbindings.title"),
        });

        _services.Loadout.LoadoutsChanged += OnDsLoadoutsChanged;
        _services.ClientState.Logout += OnCopyLogout;
    }

    // Hotkey + launcher tile: hiding the window cancels an open copy confirm (spec § 2.3).
    private void ToggleDsWindow()
    {
        if (_dsWindow.IsShown) CancelCopy();
        _dsWindow.SetVisiblePersist(!_dsWindow.IsShown);
    }

    private void DisposeOverlay()
    {
        _services.Loadout.LoadoutsChanged -= OnDsLoadoutsChanged;
        _services.ClientState.Logout -= OnCopyLogout;
        try { _dsLauncherEntry?.Dispose(); } catch { /* disposal must not throw */ }
        try { _dsToggle?.Dispose(); } catch { /* disposal must not throw */ }
        try { _dsWindow?.Remove(); } catch { /* disposal must not throw */ }
    }

    private void OnDsLoadoutsChanged()
    {
        RefreshDsRows();
        CancelCopyIfStale();   // a switch changes the worn loadout → an armed copy no longer means the same thing
    }

    // The launcher-rail tile icon (embedded swap-arrows PNG). Null → the launcher's default glyph.
    private static byte[]? LoadLauncherIcon()
    {
        try
        {
            using var s = typeof(Plugin).Assembly
                .GetManifestResourceStream("Stellar.LoadoutSwitcher.loadoutswitcher-icon.png");
            if (s is null) return null;
            using var ms = new System.IO.MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }
        catch { return null; }
    }

    // Re-read the loadout list + rebuild the per-loadout status cache (a JSON deserialize per bound
    // loadout — done here on change, NOT per poll tick), then repaint.
    private void RefreshDsRows()
    {
        _dsSlots = _services.Loadout.GetSlots();
        _dsBoundStatus.Clear();
        foreach (var slot in _dsSlots)
        {
            var setup = GetBinding(slot.Index);
            if (setup is null) continue;
            var factors = setup.Areas.Sum(a => a.Factors.Count);
            _dsBoundStatus[slot.Index] = (
                setup.Areas.Count,
                setup.Areas.Count == 1 ? setup.Areas[0].AreaId : 0,
                factors);
        }
        _dsWindow?.MarkDirty();
    }

    private LoadoutSlot? DsRowAt(int i) => i < _dsSlots.Count ? _dsSlots[i] : null;

    private bool DsIsBound(int i) => DsRowAt(i) is { } slot && _dsBoundStatus.ContainsKey(slot.Index);

    private string DsStatusText(int i)
    {
        if (DsRowAt(i) is not { } slot) return "";
        if (!_dsBoundStatus.TryGetValue(slot.Index, out var d)) return _loc.T("loadout.dsbindings.noBinding");
        // Format LIVE (not from a cached string) so a language switch re-localizes it immediately.
        return d.areaCount == 1
            ? _loc.TFormat("loadout.dsbindings.boundOneArea", d.firstAreaId, d.factors)
            : _loc.TFormat("loadout.dsbindings.boundManyAreas", d.areaCount, d.factors);
    }

    private void OnDsBindCurrent(LoadoutSlot? slot)
    {
        if (slot is null) return;   // Bind-current is only offered on the active row; BindCurrent() keys off CurrentIndex.
        var ok = BindCurrent();
        _services.NoticeTips.Create(ok ? NoticeTipType.GreenBar : NoticeTipType.RedBar)
            .WithContent(ok ? _loc.TFormat("loadout.dsbindings.bound", slot.Name) : _loc.T("loadout.dsbindings.bindFailed"))
            .Show();
        RefreshDsRows();
    }

    private void OnDsClear(LoadoutSlot? slot)
    {
        if (slot is null) return;
        ClearBinding(slot.Index);
        RefreshDsRows();
    }

    // The fixed per-row element pool (one row per loadout slot, capped at DsRowPoolSize). Rows are
    // built ONCE and read live via DsRowAt(idx); LoadoutsChanged only updates state + MarkDirty.
    private HudElement[] BuildDsRowPool()
    {
        var pool = new HudElement[DsRowPoolSize];
        for (var i = 0; i < DsRowPoolSize; i++)
        {
            var idx = i;   // capture per row

            var nameCell = new CellElement(
                new TextElement(() => DsRowName(idx), NoWrap: true), Width: 130f);

            var statusCell = new CellElement(
                new TextElement(() => DsStatusText(idx),
                    () => DsIsBound(idx)
                        ? (ColorRgba?)_services.Theme.Colors.MenuAccent
                        : (ColorRgba?)_services.Theme.Colors.MenuMuted,
                    NoWrap: true), Width: 140f);

            var bindCell = BuildBindOrCopyCell(idx);

            var clearCell = new CellElement(new ButtonElement(
                () => _loc.T("loadout.dsbindings.clear"),
                () => OnDsClear(DsRowAt(idx)),
                Enabled: () => DsIsBound(idx) && !CopyFreezesRows(), Width: 56f), Width: 60f);

            // Binding + both action cells (140+6+100+6+60 = 312 px) swap for the inline copy confirm bar.
            var actions = new CellElement(new ConditionalElement(
                () => IsCopyConfirming(idx),
                Then: BuildCopyConfirmBar(idx),
                Else: new RowElement(new HudElement[] { statusCell, bindCell, clearCell }, Gap: 6f)), Width: 312f);

            var row = new RowElement(new HudElement[] { nameCell, actions }, Gap: 6f);
            pool[idx] = new SelectableElement(row, OnClick: () => { }, Selected: () => DsRowAt(idx)?.IsCurrent == true);
        }
        return pool;
    }

    // Third cell: "Bind current" on the worn row, "← <worn>" (copy into this row) on every other row.
    private HudElement BuildBindOrCopyCell(int idx)
    {
        // "Bind current" is present ONLY on the currently-active row; every branch is the same 100 px cell so
        // the Clear column's x-position stays identical across rows (Then/Else are both built once, toggled).
        return new ConditionalElement(
            () => DsRowAt(idx)?.IsCurrent == true,
            Then: new CellElement(new ButtonElement(
                () => _loc.T("loadout.dsbindings.bindCurrent"),
                () => OnDsBindCurrent(DsRowAt(idx)), Enabled: () => !CopyFreezesRows(), Width: 96f), Width: 100f),
            // Every OTHER row: "← <worn>" copies the worn loadout into this row (spec § 2.1). Same 100px
            // cell; a long worn name is cut with "…" (CopyButtonText) — the confirm bar shows it in full.
            Else: new CellElement(new ConditionalElement(
                () => CopyButtonShown(idx),
                Then: new ButtonElement(CopyButtonText, () => ArmCopy(idx),
                    Enabled: () => CurrentCopyGate().ButtonEnabled, Width: 96f),
                Else: new SpacerElement()), Width: 100f));
    }

    private HudElement BuildDsRoot()
    {
        var pool = BuildDsRowPool();

        var header = new RowElement(new HudElement[]
        {
            new CellElement(new TextElement(() => _loc.T("loadout.dsbindings.colName"),
                () => (ColorRgba?)_services.Theme.Colors.MenuMuted), Width: 130f),
            new CellElement(new TextElement(() => _loc.T("loadout.dsbindings.colStatus"),
                () => (ColorRgba?)_services.Theme.Colors.MenuMuted), Width: 140f),
            new CellElement(new SpacerElement(), Width: 100f),
            new CellElement(new SpacerElement(), Width: 60f),
        }, Gap: 6f);

        // Muted intro paragraph (wraps to the window width — Width 0 + default NoWrap=false) explaining
        // what a binding is, what Auto-apply does, and what "Bind current" captures. Deep-Slumber is
        // named explicitly so the window is self-describing.
        var helpText = new ColumnElement(new HudElement[]
        {
            new TextElement(() => _loc.T("loadout.dsbindings.help"), () => (ColorRgba?)_services.Theme.Colors.MenuMuted),
            new TextElement(() => _loc.T("loadout.copy.help"), () => (ColorRgba?)_services.Theme.Colors.MenuMuted),
        }, Gap: 4f);

        var toggleRow = new RowElement(new HudElement[]
        {
            new ToggleElement(() => "", () => AutoApply, v => AutoApply = v),
            new TextElement(() => _loc.T("loadout.dsbindings.autoApply")),
        }, Gap: 6f);

        // Empty state swaps BOTH the header and the list for a single muted sentence (per design).
        var listOrEmpty = new ConditionalElement(
            () => _dsSlots.Count > 0,
            Then: new ColumnElement(new HudElement[]
            {
                header,
                new ScrollElement(new ListElement(() => _dsSlots.Count, pool, Columns: 1), Height: 260f),
            }, Gap: 8f),
            Else: new TextElement(() => _loc.T("loadout.dsbindings.empty"),
                () => (ColorRgba?)_services.Theme.Colors.MenuMuted));

        return new ColumnElement(new HudElement[]
        {
            helpText, new SeparatorElement(), toggleRow, new SeparatorElement(), listOrEmpty,
        }, Gap: 8f);
    }
}
