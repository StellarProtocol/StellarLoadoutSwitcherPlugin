using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.LoadoutSwitcher;

/// <summary>
/// Click feedback (house rule 13, approved redesign 2026-11-17 state 3 / 5) and the footer line.
/// <list type="number">
/// <item>The row that changed flashes for <see cref="FlashShowS"/>: a green-washed tile with a bold green "Bound" /
/// "Copied" in its binding column. Keyed by the LOADOUT ID + the time (not the pool slot), so a list refresh or a scroll
/// keeps it on the right row.</item>
/// <item>The footer shows "✓ Bound &lt;areas&gt; to &lt;name&gt;" / "✓ Copied &lt;worn&gt; to &lt;target&gt;" in green
/// for <see cref="FooterShowS"/>.</item>
/// </list>
/// The footer is ONE fixed-height line whatever it says, in priority order: the open copy confirm's warnings (amber
/// "⚠ Includes unsaved changes · changes …"), the feedback above, the season-blocked note, the standing hotkey hint.
/// The event records WHAT happened (kind + ids + names); the text is composed on the next poll and again only if the
/// language switches while it shows. The NoticeTips toasts are unchanged.
/// </summary>
public sealed partial class Plugin
{
    private const float FlashShowS  = 1f;
    private const float FooterShowS = 3f;
    private const string CheckGlyph = "✓";   // plain 14 px text: the regular TMP face lacks ✓ (KB Stellar-Design-Language)
    private const string WarnGlyph  = "⚠";

    private enum FeedbackKind : byte { None, Bound, Copied }
    private enum FooterMode : byte { Default, SeasonBlocked, Feedback, ConfirmHint }

    private FeedbackKind _fbKind;
    private int _fbLoadoutId;
    private string? _fbSource;   // Copied: the worn loadout's name
    private string _fbTarget = string.Empty;
    private float _fbAt = float.NegativeInfinity;
    private LocComposed? _fbComposed;

    private float FwNow => _services.Framework.TimeNow;

    private void ShowFeedback(FeedbackKind kind, int loadoutId, string? source, string target)
    {
        _fbKind = kind;
        _fbLoadoutId = loadoutId;
        _fbSource = source;
        _fbTarget = target;
        _fbAt = FwNow;
        _fbComposed?.Invalidate();
        _dsWindow?.MarkDirty();
    }

    private bool RowFlashing(int i)
        => _fbKind != FeedbackKind.None && FwNow - _fbAt < FlashShowS && DsRowAt(i)?.Index == _fbLoadoutId;

    private string FlashWord() => _loc.T(_fbKind == FeedbackKind.Copied ? KeyFlashCopied : KeyFlashBound);

    private bool FeedbackShowing() => _fbKind != FeedbackKind.None && FwNow - _fbAt < FooterShowS;

    // ── footer ────────────────────────────────────────────────────────────────────────────────────────────────────
    private FooterMode CurrentFooterMode()
    {
        if (CopyConfirmOpen() && (ShowUnsavedHint() || _copyShowClassHint)) return FooterMode.ConfirmHint;
        if (FeedbackShowing()) return FooterMode.Feedback;
        return SeasonBlocked() ? FooterMode.SeasonBlocked : FooterMode.Default;
    }

    private string FooterText() => CurrentFooterMode() switch
    {
        FooterMode.ConfirmHint   => ConfirmHintText(),
        FooterMode.Feedback      => (_fbComposed ??= new LocComposed(ComposeFeedback)).Get(_langGen),
        FooterMode.SeasonBlocked => _loc.T(KeyFooterBlocked),
        _                        => _loc.T(KeyFooterDefault),
    };

    // Picks one of the strings pre-composed at arm time; only "unsaved" is read live (it can flip while open).
    private string ConfirmHintText() => !_copyShowClassHint ? _loc.T("loadout.copy.hintUnsaved")
                                      : ShowUnsavedHint() ? _copyHintBoth : _copyHintClass;

    private string ComposeFeedback() => _fbKind == FeedbackKind.Copied
        ? _loc.TFormat(KeyFooterCopied, _fbSource ?? string.Empty, _fbTarget)
        : _loc.TFormat(KeyFooterBound, BindingText(_fbLoadoutId), _fbTarget);

    private string FooterGlyph() => CurrentFooterMode() switch
    {
        FooterMode.ConfirmHint => WarnGlyph,
        FooterMode.Feedback    => CheckGlyph,
        _                      => string.Empty,
    };

    private readonly ContrastCache _footerInk = new();

    private ColorRgba FooterColor() => CurrentFooterMode() switch
    {
        FooterMode.ConfirmHint => _footerInk.Get(CautionColor(), WindowBg(), SolidMinContrast),
        FooterMode.Feedback    => _footerInk.Get(SuccessColor(), WindowBg(), SolidMinContrast),
        _                      => MutedColor(),
    };

    // [plain glyph][12 px text] while there is a glyph, the 12 px text alone otherwise — the glyph is a separate PLAIN
    // text because the 12 px RichEmoji (regular TMP) face draws "✓" as nothing (AutoFishing, 2026-11-17); the plain
    // menu font draws it (Maestro's "✓ Added" footer). The strut pins the line, so neither variant moves the window.
    private HudElement BuildFooter()
    {
        HudElement Line() => new TextElement(FooterText, Color: () => FooterColor(), NoWrap: true, FontSize: 12)
            { RichEmoji = true, Ellipsize = true };
        return FixedLine(FooterLineH, new ConditionalElement(() => FooterGlyph().Length > 0,
            new RowElement(new HudElement[]
            {
                new TextElement(FooterGlyph, Color: () => FooterColor(), NoWrap: true),
                new CellElement(Line(), Weight: 1f),
            }, Gap: 4f),
            Line()));
    }
}
