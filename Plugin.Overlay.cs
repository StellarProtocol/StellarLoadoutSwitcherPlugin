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
    // Over-provisioned above any realistic saved-loadout count (the hotkey path caps at 8, but the game
    // permits more loadouts). Rows are built ONCE; LoadoutsChanged only updates state + MarkDirty.
    private const int DsRowPoolSize = 24;

    private IWindowControl _dsWindow = null!;
    private IHotkeyAction _dsToggle = null!;
    private IReadOnlyList<LoadoutSlot> _dsSlots = Array.Empty<LoadoutSlot>();
    // loadoutId -> status text; PRESENCE means "bound" (drives the accent colour + Clear-enabled).
    private readonly Dictionary<int, string> _dsBoundStatus = new();

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
            OnClose: () => _dsWindow.SetVisiblePersist(false)));

        _dsToggle = _services.Hotkeys.DeclareAction(
            new HotkeyAction(
                Id:               "loadout.window.toggle",
                Description:      _loc.T("loadout.hotkey.windowToggle"),
                SuggestedDefault: null),
            callback: () => _dsWindow.SetVisiblePersist(!_dsWindow.IsShown));

        _services.Loadout.LoadoutsChanged += OnDsLoadoutsChanged;
    }

    private void DisposeOverlay()
    {
        _services.Loadout.LoadoutsChanged -= OnDsLoadoutsChanged;
        try { _dsToggle?.Dispose(); } catch { /* disposal must not throw */ }
        try { _dsWindow?.Remove(); } catch { /* disposal must not throw */ }
    }

    private void OnDsLoadoutsChanged() => RefreshDsRows();

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
            _dsBoundStatus[slot.Index] = setup.Areas.Count == 1
                ? _loc.TFormat("loadout.dsbindings.boundOneArea", setup.Areas[0].AreaId, factors)
                : _loc.TFormat("loadout.dsbindings.boundManyAreas", setup.Areas.Count, factors);
        }
        _dsWindow?.MarkDirty();
    }

    private LoadoutSlot? DsRowAt(int i) => i < _dsSlots.Count ? _dsSlots[i] : null;

    private bool DsIsBound(int i) => DsRowAt(i) is { } slot && _dsBoundStatus.ContainsKey(slot.Index);

    private string DsStatusText(int i)
    {
        if (DsRowAt(i) is not { } slot) return "";
        return _dsBoundStatus.TryGetValue(slot.Index, out var text) ? text : _loc.T("loadout.dsbindings.noBinding");
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

    private HudElement BuildDsRoot()
    {
        var pool = new HudElement[DsRowPoolSize];
        for (var i = 0; i < DsRowPoolSize; i++)
        {
            var idx = i;   // capture per row

            var nameCell = new CellElement(
                new TextElement(() => DsRowAt(idx)?.Name ?? "", NoWrap: true), Width: 130f);

            var statusCell = new CellElement(
                new TextElement(() => DsStatusText(idx),
                    () => DsIsBound(idx)
                        ? (ColorRgba?)_services.Theme.Colors.MenuAccent
                        : (ColorRgba?)_services.Theme.Colors.MenuMuted,
                    NoWrap: true), Width: 140f);

            // Present ONLY on the currently-active row; a same-width spacer on every other row keeps the
            // Clear column's x-position identical across rows (Then/Else are both built once, toggled).
            var bindCell = new ConditionalElement(
                () => DsRowAt(idx)?.IsCurrent == true,
                Then: new CellElement(new ButtonElement(
                    () => _loc.T("loadout.dsbindings.bindCurrent"),
                    () => OnDsBindCurrent(DsRowAt(idx)), Width: 96f), Width: 100f),
                Else: new CellElement(new SpacerElement(), Width: 100f));

            var clearCell = new CellElement(new ButtonElement(
                () => _loc.T("loadout.dsbindings.clear"),
                () => OnDsClear(DsRowAt(idx)),
                Enabled: () => DsIsBound(idx), Width: 56f), Width: 60f);

            var row = new RowElement(new HudElement[] { nameCell, statusCell, bindCell, clearCell }, Gap: 6f);
            pool[idx] = new SelectableElement(row, OnClick: () => { }, Selected: () => DsRowAt(idx)?.IsCurrent == true);
        }

        var header = new RowElement(new HudElement[]
        {
            new CellElement(new TextElement(() => _loc.T("loadout.dsbindings.colName"),
                () => (ColorRgba?)_services.Theme.Colors.MenuMuted), Width: 130f),
            new CellElement(new TextElement(() => _loc.T("loadout.dsbindings.colStatus"),
                () => (ColorRgba?)_services.Theme.Colors.MenuMuted), Width: 140f),
            new CellElement(new SpacerElement(), Width: 100f),
            new CellElement(new SpacerElement(), Width: 60f),
        }, Gap: 6f);

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

        return new ColumnElement(new HudElement[] { toggleRow, new SeparatorElement(), listOrEmpty }, Gap: 8f);
    }
}
