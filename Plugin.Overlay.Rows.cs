using System;
using System.Collections.Generic;
using System.Linq;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Domain.Loadout;
using Stellar.Abstractions.Services;

namespace Stellar.LoadoutSwitcher;

/// <summary>
/// The LOADOUTS list: one raised 26 px tile per saved loadout (+ the list's 2 px gap = the 28 px pitch), in a fixed
/// 8-row viewport that scrolls past 8 (house rules 14/15). A row is
///   [3 px accent bar (worn only) | name 150 ("● " + bold when worn) | binding 150 | 116×24 pill | 24 px ✕]
/// — "Bind current" (Primary) on the worn row, "Copy here" (Neutral) on every other row; the ✕ only where a binding
/// exists (slot reserved otherwise) and clears it at once (option A). An armed copy swaps the whole tile for the inline
/// confirm (<c>Plugin.Copy.Overlay.cs</c>) and recesses every other row's pill (rows locked, CopyFreezesRows).
/// <para>Rows are built ONCE into a fixed pool and read live via <see cref="DsRowAt"/>; LoadoutsChanged only updates
/// cached state + MarkDirty. Row status is cached in <see cref="_dsBoundStatus"/> / <see cref="_dsStatusTexts"/> and
/// rebuilt on LoadoutsChanged / bind / clear / language change — never re-deserialized or formatted inside a poll.</para>
/// </summary>
public sealed partial class Plugin
{
    // Over-provisioned above any realistic saved-loadout count (the hotkey path caps at 10, but the game
    // permits more loadouts). Rows are built ONCE; LoadoutsChanged only updates state + MarkDirty.
    private const int DsRowPoolSize = 24;
    private const int ListVisibleRows = 8;

    // List geometry (prototype .list / .r): 26 px tiles, the ListElement's 2 px gap → a 28 px pitch. The viewport shows
    // exactly 8 tiles (the last tile's gap falls outside) and is pinned (ScrollElement.FixedHeight), so 0, 3 or 20
    // loadouts all give the same window height.
    private const float ListTileHeight = 26f;
    private const float ListRowGap     = 2f;
    private const float ListHeight     = ListVisibleRows * (ListTileHeight + ListRowGap) - ListRowGap;   // 222

    // Row columns (prototype .nm / .bd / .pill / .xb): 3 + 5 + 150 + 6 + 150 + 6 + 116 + 6 + 24 = 466.
    private const float RowLeadIn   = 5f;     // accent bar + lead-in = the prototype's 8 px left padding
    private const float RowGap      = 6f;
    private const float NameColW    = 150f;
    private const float BindingColW = 150f;
    private const float RowPillW    = 116f;
    private const float RowPillPadX = 8f;     // label box 100: "Bind current" ≈ 90 px of bold 12; every locale is shorter
    private const string WornDot    = "●";    // marks the worn loadout (the copy SOURCE): row name + LOADOUTS hint
    private const float WornDotW    = 10f;    // the plain 14 px "●" cell
    private const float WornDotGap  = 4f;

    private IReadOnlyList<LoadoutSlot> _dsSlots = Array.Empty<LoadoutSlot>();
    // loadoutId -> bound-setup NUMBERS (area count, the single area's id for the 1-area label, factor
    // count); PRESENCE means "bound" (drives the accent colour + the ✕). Cache the NUMBERS, not a
    // localized string, so the status re-localizes on a language change (_dsStatusTexts is rebuilt then)
    // (owner smoke 2026-08-25: switching Thai→English left bound rows reading Thai).
    private readonly Dictionary<int, (int areaCount, int firstAreaId, int factors)> _dsBoundStatus = new();
    private readonly List<string> _dsStatusTexts = new();
    private readonly RowHover _rowHover = new(DsRowPoolSize);

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
        RefreshRowTexts();   // worn slot, binding texts — cached, never built per frame
        _dsWindow?.MarkDirty();
    }

    private LoadoutSlot? DsRowAt(int i) => i < _dsSlots.Count ? _dsSlots[i] : null;

    private bool DsIsBound(int i) => DsRowAt(i) is { } slot && _dsBoundStatus.ContainsKey(slot.Index);

    private bool DsIsWorn(int i) => DsRowAt(i)?.IsCurrent == true;

    private string DsStatusText(int i) => i < _dsStatusTexts.Count ? _dsStatusTexts[i] : "";

    // "Area N · F factors" / "N areas · F factors" / "Not bound" for one loadout id — formatted only from
    // RefreshRowTexts (list change / language change) and the footer compose, never per poll.
    private string BindingText(int loadoutId)
    {
        if (!_dsBoundStatus.TryGetValue(loadoutId, out var d)) return _loc.T("loadout.dsbindings.noBinding");
        return d.areaCount == 1
            ? _loc.TFormat("loadout.dsbindings.boundOneArea", d.firstAreaId, d.factors)
            : _loc.TFormat("loadout.dsbindings.boundManyAreas", d.areaCount, d.factors);
    }

    // 0 rows while the window is hidden: the Prebuild fill (and any hidden refresh) binds nothing; the list fills on
    // the first real open (Plugin-Performance-Rules §7, the CooldownBar prebuild hitch). Capped at the pool size.
    private int DsVisibleRows() => _dsWindow?.IsShown == true ? Math.Min(_dsSlots.Count, DsRowPoolSize) : 0;

    private void OnDsBindCurrent(LoadoutSlot? slot)
    {
        if (slot is null) return;   // Bind-current is only offered on the active row; BindCurrent() keys off CurrentIndex.
        var ok = BindCurrent();
        _services.NoticeTips.Create(ok ? NoticeTipType.GreenBar : NoticeTipType.RedBar)
            .WithContent(ok ? _loc.TFormat("loadout.dsbindings.bound", slot.Name) : _loc.T("loadout.dsbindings.bindFailed"))
            .Show();
        RefreshDsRows();
        if (ok) ShowFeedback(FeedbackKind.Bound, slot.Index, null, slot.Name);   // after the refresh: the area text is new
    }

    private void OnDsClear(LoadoutSlot? slot)
    {
        if (slot is null) return;
        ClearBinding(slot.Index);
        _rowHover.Reset();   // the ✕ disappears under the pointer; a later binding must not bring it back "hot"
        RefreshDsRows();
    }

    // ── list / empty state ────────────────────────────────────────────────────────────────────────────────────────
    // The empty state is a muted sentence centred in the SAME 222 px area (prototype state 7), so the window keeps its
    // height with 0 loadouts.
    private HudElement BuildListOrEmpty() => new ConditionalElement(
        () => _dsSlots.Count > 0,
        Then: new ScrollElement(new ListElement(DsVisibleRows, BuildDsRowPool(), Columns: 1) { Gap = ListRowGap },
            Height: ListHeight) { FixedHeight = true },
        Else: FixedLine(ListHeight, new TextElement(() => _loc.T(KeyEmpty), Color: () => MutedColor(),
            Align: TextAlign.Center, NoWrap: true) { Ellipsize = true }));

    private HudElement[] BuildDsRowPool()
    {
        var pool = new HudElement[DsRowPoolSize];
        for (var i = 0; i < DsRowPoolSize; i++)
        {
            var idx = i;   // capture per row
            // Two COMPLETE tiles swapped around the bubble (never a Conditional inside a bubble's measured child).
            pool[idx] = new ConditionalElement(() => IsCopyConfirming(idx), BuildCopyConfirmTile(idx), BuildRowTile(idx));
        }
        return pool;
    }

    // Re-key the slot's hover flags to the loadout it shows now (RowHover.Bind) before any of them is read.
    private void SyncRowHover(int i) => _rowHover.Bind(i, DsRowAt(i)?.Index ?? int.MinValue);

    private bool RowHovered(int i)    { SyncRowHover(i); return _rowHover.Row[i]; }
    private bool PillHovered(int i)   { SyncRowHover(i); return _rowHover.Pill[i]; }
    private bool RemoveHovered(int i) { SyncRowHover(i); return _rowHover.Remove[i]; }

    private ColorRgba RowFill(int i)
    {
        if (RowFlashing(i)) return FlashTileColor();
        var tile = DsIsWorn(i) ? WornTileColor() : TileColor();
        return RowHovered(i) ? RowHoverColor(tile) : tile;
    }

    private HudElement BuildRowTile(int idx)
    {
        var bindLabel = new SolidLabelCache();
        var copyLabel = new ContrastCache();
        var bindingInk = new ContrastCache();
        var flashInk = new ContrastCache();

        // Name: "● " + bold on the worn row (the copy SOURCE), regular elsewhere — two complete variants (bold can't
        // toggle live) in the same fixed 150 px box, ellipsized. The "●" is its own PLAIN text: a Geometric Shapes glyph
        // draws blank in the bold TMP face ("■" did, KB Stellar-Design-Language §6).
        var name = new CellElement(new ConditionalElement(() => DsIsWorn(idx),
            new RowElement(new HudElement[]
            {
                new CellElement(new TextElement(() => WornDot, Color: () => TextColor(), NoWrap: true), Width: WornDotW),
                new TextElement(() => DsRowName(idx), Color: () => TextColor(), Width: NameColW - WornDotW - WornDotGap,
                    NoWrap: true, FontSize: 14) { Bold = true, Ellipsize = true },
            }, Gap: WornDotGap),
            new TextElement(() => DsRowName(idx), Color: () => TextColor(), Width: NameColW, NoWrap: true) { Ellipsize = true }),
            Width: NameColW);

        // Binding: accent when bound, muted "Not bound"; for ~1 s after a bind / copy a bold green "Bound" / "Copied".
        var binding = new CellElement(new ConditionalElement(() => RowFlashing(idx),
            new TextElement(FlashWord, Color: () => flashInk.Get(SuccessColor(), FlashTileColor(), PillMinContrast),
                Width: BindingColW, NoWrap: true, FontSize: 14) { Bold = true, Ellipsize = true },
            new TextElement(() => DsStatusText(idx),
                Color: () => DsIsBound(idx) ? bindingInk.Get(AccentColor(), TileColor(), PillMinContrast) : MutedColor(),
                Width: BindingColW, NoWrap: true) { Ellipsize = true }),
            Width: BindingColW);

        // Pill: "Bind current" on the worn row; "Copy here" on the others while a worn loadout is known (the copy needs a
        // source); else the slot stays reserved. Both recess while rows are locked (an armed confirm / a running copy).
        var pill = new CellElement(new ConditionalElement(() => DsIsWorn(idx),
            RowPill(() => _loc.T(KeyBindCurrent), PrimaryFill, () => bindLabel.Get(PrimaryFill()),
                () => OnDsBindCurrent(DsRowAt(idx)), RowPillW, RowPillPadX,
                () => PillHovered(idx), over => _rowHover.Pill[idx] = over, WornTileColor, () => !CopyFreezesRows()),
            new ConditionalElement(() => CopyButtonShown(idx),
                RowPill(() => _loc.T(KeyCopyHere), NeutralFill, () => copyLabel.Get(TextColor(), NeutralFill(), PillMinContrast),
                    () => ArmCopy(idx), RowPillW, RowPillPadX,
                    () => PillHovered(idx), over => _rowHover.Pill[idx] = over, TileColor, () => CurrentCopyGate().ButtonEnabled),
                new SpacerElement(RowPillW, RowPillH))),
            Width: RowPillW);

        var remove = new CellElement(new ConditionalElement(() => DsIsBound(idx),
            RemoveIcon(() => RemoveHovered(idx), over => _rowHover.Remove[idx] = over,
                () => OnDsClear(DsRowAt(idx)), () => !CopyFreezesRows()),
            new SpacerElement(ChipHeight, ChipHeight)),
            Width: ChipHeight);

        // The tile draws its OWN hover (a Bare selectable has no wash), so the whole row — pill and ✕ included — lights
        // up as one piece (Maestro ListTile). The strut in the lead-in pins the tile to 26 px.
        return new BubbleElement(new SelectableElement(
            new RowElement(new HudElement[]
            {
                AccentBar(() => DsIsWorn(idx)),
                new SpacerElement(RowLeadIn, ListTileHeight),
                new RowElement(new HudElement[] { name, binding, pill, remove }, Gap: RowGap),
            }, Gap: 0.01f),
            OnClick: () => { })
            { Bare = true, OnHover = over => _rowHover.Row[idx] = over })
        {
            FillColorFn = () => Fade(RowFill(idx)), CornerRadius = TileRadius, PaddingX = 0f, PaddingY = 0f,
        };
    }
}
