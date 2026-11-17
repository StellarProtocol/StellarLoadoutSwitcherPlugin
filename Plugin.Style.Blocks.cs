using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.LoadoutSwitcher;

// More house-kit building blocks (Stellar Design Language §4; ported from AutoFishing / Maestro Plugin.Style.Blocks.cs):
// embedded art + the per-size header glyph, the accent icon tile, the clickable On/Off chip and its recessed twin, the
// status dot, the worn row's accent bar, the row ✕ and the per-pool-slot hover flags.
public sealed partial class Plugin
{
    // ── embedded PNGs ─────────────────────────────────────────────────────────────────────────────────────────────
    // Read once per resource (csproj EmbeddedResource LogicalName "Stellar.LoadoutSwitcher.<file>") and kept:
    // ImageElement Funcs are polled. A missing resource caches null (the element then draws nothing) instead of
    // re-reading every poll.
    private static readonly Dictionary<string, byte[]?> _pngCache = new();

    private static byte[]? CachedPng(string file)
    {
        if (_pngCache.TryGetValue(file, out var png)) return png;
        try
        {
            using var s = typeof(Plugin).Assembly.GetManifestResourceStream("Stellar.LoadoutSwitcher." + file);
            if (s != null) { using var ms = new System.IO.MemoryStream(); s.CopyTo(ms); png = ms.ToArray(); }
        }
        catch { png = null; }
        _pngCache[file] = png;
        return png;
    }

    // The header glyph ships one pixel-hinted bitmap per size (house icon rule: never a master scaled down —
    // Resources/icons, drawn by tools/icons/gen-header-icon.py), so the tile asks for the PIXEL size it draws at.
    // The launcher tile keeps its own 128 px icon (Plugin.Overlay.cs LoadLauncherIcon).
    private static readonly int[] HeaderIconSizes = { 16, 18, 24, 32, 36, 48 };   // keep in sync with Resources/icons

    // `units` canvas units × the live canvas→screen scale (1.0 at 1440p, 0.75 at 1080p). Same rule as the framework's
    // IconSizePicker: an exact size (±0.05 px) draws 1:1, else the smallest LARGER drawing (downscaling stays crisper
    // than blowing a smaller one up), else the largest. Cached until the scale changes, so the per-refresh Func is two
    // property reads + a float compare and hands back the SAME array (ImageElement.Live compares by reference).
    private float  _iconScale = -1f;
    private byte[]? _iconPicked;

    private byte[]? HeaderIconPng(int units)
    {
        var fw = _services.Framework;
        float scale = fw.CanvasWidth > 1 && fw.ScreenWidth > 0 ? (float)fw.ScreenWidth / fw.CanvasWidth : 1f;
        if (scale == _iconScale && _iconPicked != null) return _iconPicked;
        _iconScale = scale;
        float want = units * scale;
        int px = HeaderIconSizes[HeaderIconSizes.Length - 1];
        for (int i = 0; i < HeaderIconSizes.Length; i++)
            if (HeaderIconSizes[i] >= want - 0.05f) { px = HeaderIconSizes[i]; break; }
        return _iconPicked = CachedPng("loadoutswitcher-" + px + ".png") ?? _iconPicked;
    }

    // ── accent icon tile ──────────────────────────────────────────────────────────────────────────────────────────
    // A rounded tile in the LIVE theme accent with the flat white glyph centred (art can't follow the theme, so the colour
    // lives in the tile). 40 px tile → 24 px glyph, 8 px all round; radius 9 (≈ 22 % of the side). The fixed-width Cell
    // pins the column (Cell / Selectable force-expand a child's width — the icon-squish gotcha); the Row keeps it square.
    private const float ToolIconSize  = 40f;
    private const int   ToolGlyphSize = 24;

    private HudElement IconBox(Func<byte[]?> png, float size, int glyph)
    {
        float pad = (size - glyph) / 2f;
        return new CellElement(new BubbleElement(
            // Live: re-pull per refresh so a resolution / UI-scale change swaps in the matching pre-drawn size.
            new RowElement(new HudElement[] { new ImageElement(png, glyph, glyph) { Live = true } }, Gap: 0.01f, Justify: RowJustify.Center))
        {
            FillColorFn = () => Fade(AccentColor()), CornerRadius = MathF.Round(size * 0.22f), PaddingX = pad, PaddingY = pad,
        }, Width: size);
    }

    // ── On/Off chip ───────────────────────────────────────────────────────────────────────────────────────────────
    // A clickable 64×24 chip in a fixed-width cell: Cell → round Bubble (fill + hover) → Bare Selectable → Row[24 px
    // strut, label]. COMPLETE chips swapped by a ConditionalElement wrapping the whole chip (bold can't toggle live; never
    // a Conditional inside a bubble's measured child), all the SAME width and sharing one HoverFlag, so a click never
    // moves the row. Bubble.MaxWidth pins the width (a MaxWidth-0 bubble under a Conditional caps itself at the 100 px
    // default — MinimalNameplate, measured 64×24 in both states in-game 2026-11-07).
    // On = bold status green on a 22 % green wash; Off = muted on a faint text wash (approved prototype .chip.on/.off);
    // `recessed` true = the season-blocked third state (.chip.dis): a sunken, NON-clickable chip showing "Off" — the
    // saved setting underneath is not touched.
    private const float ChipHeight = 24f;
    private const float OnOffChipW = 64f;

    private HudElement ChipShell(HudElement label, Func<ColorRgba> fill, HoverFlag hover, Action onClick, float width)
        => new BubbleElement(new SelectableElement(
            new RowElement(new HudElement[]
            {
                new SpacerElement(0f, ChipHeight),   // height strut = the side (house rule 4: no stacked spacers)
                label,
            }, Gap: 0.01f, Justify: RowJustify.Center),
            OnClick: onClick) { Bare = true, OnHover = over => hover.On = over })
        {
            FillColorFn = () => fill(),
            CornerRadius = RoundRadius(ChipHeight), PaddingX = 0f, PaddingY = 0f, MaxWidth = width,
        };

    private HudElement OnOffChip(Func<bool> get, Action<bool> set, Func<bool> recessed)
    {
        var hover = new HoverFlag();
        var onText = new ContrastCache();
        var offLabel = new DisabledLabelCache();
        void Click() => set(!get());
        ColorRgba OnFill()  => Over(SuccessColor() with { A = ChipWashAlpha }, PanelColor());
        ColorRgba OffFill() => Over(TextColor() with { A = OffChipTextMix }, PanelColor());
        ColorRgba Hovered(ColorRgba c) => Fade(hover.On ? HoverFill(c) : c);
        // Localized On/Off: the label box is the whole 64 px chip, and Ellipsize keeps a long translation inside it.
        var on = ChipShell(new TextElement(() => _loc.T(KeyOn), Color: () => onText.Get(SuccessColor(), OnFill(), SolidMinContrast),
                Width: OnOffChipW, Align: TextAlign.Center, NoWrap: true, FontSize: 13) { Bold = true, Ellipsize = true, CenterCaps = true },
            () => Hovered(OnFill()), hover, Click, OnOffChipW);
        var off = ChipShell(new TextElement(() => _loc.T(KeyOff), Color: () => MutedColor(),
                Width: OnOffChipW, Align: TextAlign.Center, NoWrap: true) { Ellipsize = true },
            () => Hovered(OffFill()), hover, Click, OnOffChipW);
        // Recessed: same shell, a no-op click, the sunken fill (translucent → fades with the window by itself).
        var dis = ChipShell(new TextElement(() => _loc.T(KeyOff), Color: () => DisabledLabel(PanelColor(), offLabel),
                Width: OnOffChipW, Align: TextAlign.Center, NoWrap: true) { Ellipsize = true },
            () => DisabledShade, new HoverFlag(), () => { }, OnOffChipW);
        return new CellElement(new ConditionalElement(recessed, dis, new ConditionalElement(get, on, off)), Width: OnOffChipW);
    }

    // ── status dot ────────────────────────────────────────────────────────────────────────────────────────────────
    // A 7 px round dot (radius 7/2 − 1 keeps the sliced bubble exactly 7 — RoundRadius) in a live colour.
    private const float DotSize = 7f;

    private HudElement StatusDot(Func<ColorRgba> color) => new BubbleElement(new SpacerElement(DotSize, DotSize))
    {
        FillColorFn = () => Fade(color()), CornerRadius = RoundRadius(DotSize), PaddingX = 0f, PaddingY = 0f,
    };

    // ── worn-row accent bar ───────────────────────────────────────────────────────────────────────────────────────
    // The worn row's 3 px accent bar at the tile's left edge (prototype .r.cur inset shadow); transparent on every other
    // row, so nothing moves. Inset 3 px top and bottom of the 26 px tile, like Maestro's selected row. Radius
    // RoundRadius(3): any larger and the sliced sprite's min size (2r + 2) would widen the bar.
    private const float AccentBarW = 3f;

    private HudElement AccentBar(Func<bool> shown) => new BubbleElement(new SpacerElement(AccentBarW, ListTileHeight - 6f))
    {
        FillColorFn = () => shown() ? Fade(AccentColor()) : Transparent,
        CornerRadius = RoundRadius(AccentBarW), PaddingX = 0f, PaddingY = 0f,
    };

    // ── per-pool-slot hover ───────────────────────────────────────────────────────────────────────────────────────
    // The row pool is fixed (DsRowPoolSize) and its slots show whatever loadout sits at that index, so every hover flag
    // is keyed per SLOT and dropped when the slot shows a different loadout (a delete / a copy slides another row under
    // the pointer — it must not inherit the hover until the pointer re-enters) or when the row's state flips (an armed
    // confirm replaces the pill; the pill coming back must not be stuck "hovered" — Reset()).
    private sealed class RowHover
    {
        public readonly bool[] Row, Pill, Remove, Yes, No;
        private readonly int[] _id;

        public RowHover(int n)
        {
            Row = new bool[n]; Pill = new bool[n]; Remove = new bool[n]; Yes = new bool[n]; No = new bool[n];
            _id = new int[n];
            for (int i = 0; i < n; i++) _id[i] = int.MinValue;
        }

        // Re-key slot `i` to loadout `id`; a different loadout clears every flag of the slot.
        public void Bind(int i, int id)
        {
            if (_id[i] == id) return;
            _id[i] = id;
            Row[i] = Pill[i] = Remove[i] = Yes[i] = No[i] = false;
        }

        public void Reset()
        {
            Array.Clear(Row, 0, Row.Length); Array.Clear(Pill, 0, Pill.Length); Array.Clear(Remove, 0, Remove.Length);
            Array.Clear(Yes, 0, Yes.Length); Array.Clear(No, 0, No.Length);
        }
    }

    // ── row ✕ (remove binding) ────────────────────────────────────────────────────────────────────────────────────
    // A round 24 px icon button (prototype .xb): a muted plain "✕" at rest (a BOLD ✕ can draw blank — KB §6), the house
    // "Remove hover" on hover (a soft red round wash + a red glyph kept readable on it). `enabled` false (rows locked by a
    // copy) = no hover and a swallowed click; the glyph stays as the prototype's locked state 4 shows it.
    private static readonly ColorRgba RemoveGlyph = new(1f, 0x8a / 255f, 0x8b / 255f);   // #ff8a8b, the house remove glyph
    private const float RemoveWashAlpha = 0.22f;

    private HudElement RemoveIcon(Func<bool> hover, Action<bool> setHover, Action onClick, Func<bool> enabled)
    {
        var glyph = new ContrastCache();
        bool Hot() => hover() && enabled();
        ColorRgba Wash() => Over(Menu.Danger with { A = RemoveWashAlpha }, TileColor());
        return new BubbleElement(new SelectableElement(new RowElement(new HudElement[]
            {
                new SpacerElement(0f, ChipHeight),
                new TextElement(() => "✕", Color: () => Hot() ? glyph.Get(RemoveGlyph, Wash(), SolidMinContrast) : MutedColor(),
                    Width: ChipHeight, Align: TextAlign.Center, NoWrap: true),
            }, Gap: 0.01f, Justify: RowJustify.Center),
            OnClick: () => { if (enabled()) onClick(); })
            { Bare = true, OnHover = setHover })
        {
            FillColorFn = () => Hot() ? Fade(Wash()) : Transparent,
            CornerRadius = RoundRadius(ChipHeight), PaddingX = 0f, PaddingY = 0f, MaxWidth = ChipHeight,
        };
    }
}
