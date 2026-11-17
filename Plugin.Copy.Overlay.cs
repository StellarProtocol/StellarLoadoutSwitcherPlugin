using Stellar.Abstractions.Domain.Loadout;
using Stellar.Abstractions.Services;

namespace Stellar.LoadoutSwitcher;

/// <summary>Presentation for the copy feature (<c>Plugin.Copy.cs</c> owns state + actions): the cached row texts and
/// the INLINE confirm that replaces a row's whole tile (approved redesign 2026-11-17, option A): an amber-washed tile
/// with the bold question "Overwrite &lt;target&gt; with &lt;worn&gt;?" and a Danger "Overwrite" + Neutral "Cancel" pill
/// (84×24). The unsaved / class-change warnings moved to the footer line (amber, Plugin.Overlay.Feedback.cs), so the
/// row never grows.</summary>
public sealed partial class Plugin
{
    // Confirm tile geometry: 8 px lead-in + question + 2 × (6 + 84) = 464 of the 466 px row (2 px slack).
    private const float ConfirmLeadIn    = 8f;
    private const float ConfirmPillW     = 84f;
    private const float ConfirmPillPadX  = 6f;     // label box 72: "Overwrite" ≈ 68 px of bold 12; every locale fits
    private const float ConfirmQuestionW = 276f;

    // Rebuilt in RefreshRowTexts (LoadoutsChanged via RefreshDsRows, language change) — read per frame.
    private LoadoutSlot? _wornSlot;

    private void RefreshRowTexts()
    {
        _wornSlot = null;
        _dsStatusTexts.Clear();
        foreach (var slot in _dsSlots)
        {
            if (slot.IsCurrent) _wornSlot = slot;
            _dsStatusTexts.Add(BindingText(slot.Index));
        }
        InvalidateCopyGate();
    }

    // The slot's own Name (a stable string instance) — the worn row's "●" is drawn by the row itself.
    private string DsRowName(int idx) => DsRowAt(idx)?.Name ?? "";

    // Rows freeze while a copy confirm is open (one at a time) or a copy is running.
    private bool CopyFreezesRows() { var g = CurrentCopyGate(); return g.ConfirmOpen || g.CopyInFlight; }

    private HudElement BuildCopyConfirmTile(int idx)
    {
        var dangerLabel = new SolidLabelCache();
        var cancelLabel = new ContrastCache();
        return new BubbleElement(new RowElement(new HudElement[]
            {
                new SpacerElement(ConfirmLeadIn, ListTileHeight),   // lead-in + the 26 px height strut
                new RowElement(new HudElement[]
                {
                    // The question names BOTH loadouts in full (one line, cut with "…" only if it can't fit).
                    new TextElement(() => _copyQuestion, Color: () => TextColor(), Width: ConfirmQuestionW, NoWrap: true,
                        FontSize: 13) { Bold = true, Ellipsize = true },
                    RowPill(() => _loc.T(KeyOverwrite), DangerFill, () => dangerLabel.Get(DangerFill()), ConfirmCopy,
                        ConfirmPillW, ConfirmPillPadX, () => ConfirmHover(_rowHover.Yes, idx),
                        over => _rowHover.Yes[idx] = over, ConfirmTileColor),
                    RowPill(() => _loc.T(KeyCancel), NeutralFill, () => cancelLabel.Get(TextColor(), NeutralFill(), PillMinContrast),
                        CancelCopy, ConfirmPillW, ConfirmPillPadX, () => ConfirmHover(_rowHover.No, idx),
                        over => _rowHover.No[idx] = over, ConfirmTileColor),
                }, Gap: RowGap),
            }, Gap: 0.01f))
        {
            FillColorFn = () => Fade(ConfirmTileColor()), CornerRadius = TileRadius, PaddingX = 0f, PaddingY = 0f,
        };
    }

    private bool ConfirmHover(bool[] flags, int idx) { SyncRowHover(idx); return flags[idx]; }
}
