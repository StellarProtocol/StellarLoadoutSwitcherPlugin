using System;
using System.Threading;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.LoadoutSwitcher;

/// <summary>Presentation for the copy feature (<c>Plugin.Copy.cs</c> owns state + actions): the inline confirm
/// bar that replaces a row's Binding + action cells, the worn-row marker, and the ✓/✕ icon chips (the same
/// PNGs as Wardrobe's confirm, embedded here — the overlay font draws the plain glyphs poorly).</summary>
public sealed partial class Plugin
{
    // Confirm-bar geometry: the 312 px it replaces = question column + two 32 px chip cells + 2 × 6 px gaps.
    private const float ConfirmBarWidth = 312f;
    private const float ConfirmChipCell = 32f;
    private const float ConfirmGap = 6f;

    // The button's 96 px cannot measure glyphs, so a long worn name is cut by characters (spec § 2.1 —
    // the confirm bar always shows the full name).
    private const int CopyButtonNameChars = 10;

    private static readonly byte[]? ConfirmYesIcon = LoadResource("Stellar.LoadoutSwitcher.confirm-yes.png");
    private static readonly byte[]? ConfirmNoIcon = LoadResource("Stellar.LoadoutSwitcher.confirm-no.png");

    // The worn row's name carries a "●" so it reads as the copy SOURCE (mockup screen 1).
    private string DsRowName(int idx)
        => DsRowAt(idx) is not { } slot ? "" : slot.IsCurrent ? "● " + slot.Name : slot.Name;

    // Rows freeze while a copy confirm is open (one at a time) or a copy is running.
    private bool CopyFreezesRows() => CopyConfirmOpen() || Volatile.Read(ref _copyInFlight) != 0;

    private string CopyButtonText() => _loc.TFormat("loadout.copy.button",
        CopyRules.Ellipsize(WornSlot()?.Name ?? "", CopyButtonNameChars));

    private HudElement BuildCopyConfirmBar(int idx)
    {
        Func<ColorRgba?> warn = () => _services.Theme.Colors.Warning;
        var text = new ColumnElement(new HudElement[]
        {
            new TextElement(CopyQuestion, warn, NoWrap: true),
            new ConditionalElement(ShowUnsavedHint,
                Then: new TextElement(() => _loc.T("loadout.copy.hintUnsaved"), warn, NoWrap: true)),
            new ConditionalElement(() => ShowClassHint(idx),
                Then: new TextElement(() => ClassHintText(idx), warn, NoWrap: true)),
        }, Gap: 2f);

        var questionWidth = ConfirmBarWidth - 2 * (ConfirmChipCell + ConfirmGap);
        return new RowElement(new HudElement[]
        {
            new CellElement(text, Width: questionWidth),
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
