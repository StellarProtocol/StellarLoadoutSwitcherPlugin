using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Domain.Loadout;
using Stellar.Abstractions.Services;

namespace Stellar.LoadoutSwitcher;

/// <summary>Presentation for the copy feature (<c>Plugin.Copy.cs</c> owns state + actions): the cached row
/// texts, the inline confirm bar that replaces a row's Binding + action cells, the worn-row marker, and the
/// ✓/✕ icon chips (the same PNGs as Wardrobe's confirm, embedded here — the overlay font draws the plain
/// glyphs poorly).</summary>
public sealed partial class Plugin
{
    /// <summary>The copy arrow, in ONE place: the button label and the help sentence both use it. The owner
    /// is checking whether "←" renders in-game; the fallback is "◀" (Geometric Shapes, known to render).</summary>
    internal const string CopyArrow = "←";

    // Confirm-bar geometry: the 312 px it replaces = question column + two 32 px chip cells + 2 × 6 px gaps.
    private const float ConfirmBarWidth = 312f;
    private const float ConfirmChipCell = 32f;
    private const float ConfirmGap = 6f;
    private const float ConfirmTextWidth = ConfirmBarWidth - 2 * (ConfirmChipCell + ConfirmGap);   // 236
    private const int ConfirmHintFontSize = 12;

    // The button's 96 px cannot measure glyphs, so the worn name is cut by DISPLAY UNITS (CJK/emoji = 2,
    // others 1 — CopyRules.Ellipsize). The confirm bar always shows the full name.
    private const int CopyButtonNameUnits = 16;

    private static readonly byte[]? ConfirmYesIcon = LoadResource("Stellar.LoadoutSwitcher.confirm-yes.png");
    private static readonly byte[]? ConfirmNoIcon = LoadResource("Stellar.LoadoutSwitcher.confirm-no.png");

    // Rebuilt in RefreshCopyTexts (LoadoutsChanged via RefreshDsRows, language change) — read per frame.
    private LoadoutSlot? _wornSlot;
    private string _copyButtonLabel = "";
    private string _copyHelpText = "";
    private readonly List<string> _dsRowNames = new();

    private void RefreshCopyTexts()
    {
        _wornSlot = null;
        _dsRowNames.Clear();
        foreach (var slot in _dsSlots)
        {
            if (slot.IsCurrent) _wornSlot = slot;
            _dsRowNames.Add(slot.IsCurrent ? "● " + slot.Name : slot.Name);   // ● marks the copy SOURCE
        }
        _copyButtonLabel = CopyArrow + " " + CopyRules.Ellipsize(_wornSlot?.Name ?? "", CopyButtonNameUnits);
        _copyHelpText = _loc.TFormat("loadout.copy.help", CopyArrow);
        InvalidateCopyGate();
    }

    private string DsRowName(int idx) => idx < _dsRowNames.Count ? _dsRowNames[idx] : "";

    private string CopyButtonText() => _copyButtonLabel;

    private string CopyHelpText() => _copyHelpText;

    // Rows freeze while a copy confirm is open (one at a time) or a copy is running.
    private bool CopyFreezesRows() { var g = CurrentCopyGate(); return g.ConfirmOpen || g.CopyInFlight; }

    private HudElement BuildCopyConfirmBar(int idx)
    {
        Func<ColorRgba?> warn = () => _services.Theme.Colors.Warning;
        var text = new ColumnElement(new HudElement[]
        {
            // Question: default menu text colour (Wardrobe's confirm), one line, clipped at the column edge.
            new TextElement(() => _copyQuestion, Width: ConfirmTextWidth, NoWrap: true),
            // Hints: warning colour, smaller, and they WRAP (the row grows) — never overflow onto ✓/✕.
            new ConditionalElement(ShowUnsavedHint,
                Then: new TextElement(() => _loc.T("loadout.copy.hintUnsaved"), warn,
                    Width: ConfirmTextWidth, FontSize: ConfirmHintFontSize)),
            new ConditionalElement(() => _copyShowClassHint,
                Then: new TextElement(() => _copyClassHint, warn,
                    Width: ConfirmTextWidth, FontSize: ConfirmHintFontSize)),
        }, Gap: 2f);

        return new RowElement(new HudElement[]
        {
            new CellElement(text, Width: ConfirmTextWidth),
            new CellElement(ConfirmChip(ConfirmYesIcon, ConfirmCopy), Width: ConfirmChipCell),
            new CellElement(ConfirmChip(ConfirmNoIcon, CancelCopy), Width: ConfirmChipCell),
        }, Gap: ConfirmGap);
    }

    // Wardrobe's icon chip: outline style so the light icon keeps contrast on the dark row.
    private static ButtonElement ConfirmChip(byte[]? png, Action onClick)
        => new(() => "", onClick, Style: MenuButtonStyle.Outline, Width: 30f, Icon: () => png);

    private static byte[]? LoadResource(string name)
    {
        try
        {
            using var s = typeof(Plugin).Assembly.GetManifestResourceStream(name);
            if (s is null) return null;
            using var ms = new System.IO.MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }
        catch { return null; }
    }
}
