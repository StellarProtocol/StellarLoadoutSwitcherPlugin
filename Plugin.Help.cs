using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.LoadoutSwitcher;

/// <summary>
/// Section help, house style (Maestro Plugin.Help*.cs → AutoFishing Plugin.Help.cs). The window framework has no hover
/// tooltip, so each section caption ends in a round 24 px "?" (Neutral, lighter on hover, accent-tinted while its tip is
/// open) that opens ONE shared, title-bar-less GlassMenu popover anchored under it:
///   [32 px accent tile with a white bold "?"] [bold section name / muted "SECTION · HELP"] … [round ✕]
///   then the help text in one Tile box, 360 wide.
/// These replace the two long help paragraphs the window used to carry (approved redesign 2026-11-17). The popover's
/// text Funcs resolve the open <see cref="HelpTopic"/> live through _loc, so an open tip follows a language switch;
/// clicking the other "?" while open swaps the text and re-anchors it, re-clicking the open "?" closes it.
/// </summary>
public sealed partial class Plugin
{
    private const float HelpSize     = 24f;    // the round "?" — exactly 24 × 24 (RoundRadius)
    private const float HelpTipWidth = 360f;
    private const float HelpIconSize = 32f;

    private enum HelpTopic : byte { None, Restore, Loadouts }

    private IWindowControl _helpWindow = null!;   // registered in InitOverlay, before the main window
    private WindowRect _helpRect;                 // where the popover should sit (re-applied for a few ticks after opening)
    private int _helpRepositionTicks;
    private readonly HoverFlag _helpCloseHover = new();
    private HelpTopic _helpTopic;                 // None = closed

    // Content = [24 px strut, the "?" in a 24 px box] side by side in ONE Row, so the circle is exactly its side (a
    // stacked spacer drew Maestro's "?" 24 × 26). CenterGlyphs centres the drawn "?" in its box.
    private HudElement HelpDot(HelpTopic topic)
    {
        var hover = new HoverFlag();
        var text = new ContrastCache();
        return new BubbleElement(new SelectableElement(
            new RowElement(new HudElement[]
            {
                new SpacerElement(0f, HelpSize),
                new TextElement(() => "?", Color: () => text.Get(TextColor(), NeutralFill(), PillMinContrast), Width: HelpSize,
                    Align: TextAlign.Center, NoWrap: true, FontSize: 12) { Bold = true, CenterGlyphs = true },
            }, Gap: 0.01f, Justify: RowJustify.Center),
            OnClick: () => { })                       // the real work is in OnClickWithRect (needs the button's rect)
            {
                Bare = true,
                OnHover = over => hover.On = over,
                OnClickWithRect = r => ToggleHelp(r, topic),
            })
        {
            // Accent-tinted only while THIS dot's tip is the one open.
            FillColorFn = () => Fade(_helpWindow.IsShown && _helpTopic == topic
                                     ? Over(AccentColor() with { A = 0.35f }, NeutralFill())
                                     : hover.On ? HoverFill(NeutralFill())
                                     : NeutralFill()),
            CornerRadius = RoundRadius(HelpSize), PaddingX = 0f, PaddingY = 0f, MaxWidth = HelpSize,
        };
    }

    private IWindowControl RegisterHelpWindow() => _services.Windows.Register(new WindowRegistration(
        new WindowSpec(
            Id:          "loadoutswitcher.help",
            Title:       "Help",
            DefaultRect: new WindowRect(_services.Framework.ScreenWidth - HelpTipWidth - 20f, 20f, HelpTipWidth, 0f),
            Category:    WindowCategory.Tools,
            Style:       WindowPanelStyle.GlassMenu)
        {
            Draggable = true, Closable = true, StartVisible = false,
            // No title bar: the header carries the title and a close; the whole frame is the drag handle.
            ShowTitleBar = false,
            // Not drawn (no title bar), but the Settings layout editor lists the window by its live-localized title.
            TitleProvider = () => _loc.T(KeyHelpTitle),
            // Help for the in-world Loadout Switcher window — only while in the world and not on a loading screen.
            ShouldRender = () => _services.ClientState.Phase == GamePhase.World
                                 && (_services.ClientState.UiState & GameUIState.Loading) == 0,
        },
        BuildHelpRoot(),
        OnClose: CloseHelp));

    private HudElement BuildHelpRoot() => new ColumnElement(new HudElement[]
    {
        new RowElement(new HudElement[]
        {
            HelpIcon(),
            new CellElement(new ColumnElement(new HudElement[]
            {
                new TextElement(HelpTitleText, Color: () => TextColor(), NoWrap: true, FontSize: 14) { Bold = true, Ellipsize = true },
                new TextElement(HelpSubtitleText, Color: () => MutedColor(), NoWrap: true, FontSize: 11) { Bold = true, Ellipsize = true },
            }, Gap: 1f), Weight: 1f),
            HelpCloseButton(),
        }, Gap: 10f),
        // The help text: one raised tile (house Tile colour, theme-live), wrapping inside the 360 px popover.
        new BubbleElement(new TextElement(HelpBodyText, Color: () => TextColor()))
        {
            FillColorFn = () => Fade(TileColor()), CornerRadius = TileRadius, PaddingX = 10f, PaddingY = 8f,
        },
    }, Gap: 10f);

    private string HelpTitleText() => _helpTopic switch
    {
        HelpTopic.Restore  => _loc.T(KeyRestoreCaption),
        HelpTopic.Loadouts => _loc.T(KeyLoadoutsCaption),
        _                  => string.Empty,
    };

    private string HelpSubtitleText() => _helpTopic switch
    {
        HelpTopic.Restore  => _loc.T(KeyHelpRestoreSub),
        HelpTopic.Loadouts => _loc.T(KeyHelpLoadoutsSub),
        _                  => string.Empty,
    };

    private string HelpBodyText() => _helpTopic switch
    {
        HelpTopic.Restore  => _loc.T(KeyHelpRestoreBody),
        HelpTopic.Loadouts => LoadoutsHelpBody(),
        _                  => string.Empty,
    };

    // 32 px accent tile with a white bold "?" (the icon-tile look, drawn with text instead of a PNG).
    private HudElement HelpIcon()
    {
        var text = new SolidLabelCache();
        return new CellElement(new BubbleElement(new RowElement(new HudElement[]
            {
                new SpacerElement(0f, HelpIconSize),
                new TextElement(() => "?", Color: () => text.Get(AccentColor()),
                    Width: HelpIconSize, Align: TextAlign.Center, NoWrap: true, FontSize: 18) { Bold = true, CenterGlyphs = true },
            }, Gap: 0.01f, Justify: RowJustify.Center))
        {
            FillColorFn = () => Fade(AccentColor()), CornerRadius = MathF.Round(HelpIconSize * 0.22f), PaddingX = 0f, PaddingY = 0f,
        }, Width: HelpIconSize);
    }

    // Round 24 px ✕ that closes the tip (plain text — a bold ✕ can draw blank): muted glyph at rest, a soft round wash +
    // text colour on hover. Rest fill is an explicit transparent (a null FillColorFn = the accent default blob).
    private HudElement HelpCloseButton() => new BubbleElement(new SelectableElement(new RowElement(new HudElement[]
        {
            new SpacerElement(0f, HelpSize),
            new TextElement(() => "✕", Color: () => _helpCloseHover.On ? TextColor() : MutedColor(), Width: HelpSize,
                Align: TextAlign.Center, NoWrap: true),
        }, Gap: 0.01f, Justify: RowJustify.Center),
        OnClick: CloseHelp) { Bare = true, OnHover = over => _helpCloseHover.On = over })
    {
        FillColorFn = () => _helpCloseHover.On ? Fade(Over(TextColor() with { A = HoverMix }, WindowBg())) : Transparent,
        CornerRadius = RoundRadius(HelpSize), PaddingX = 0f, PaddingY = 0f, MaxWidth = HelpSize,
    };

    // Toggle the tip: re-clicking the same "?" closes it; otherwise (closed, or the other "?") show this tip's text
    // anchored under the "?" that was clicked.
    private void ToggleHelp(WindowRect r, HelpTopic topic)
    {
        if (_helpWindow.IsShown && _helpTopic == topic) { CloseHelp(); return; }
        _helpTopic = topic;
        // OnClickWithRect reports SCREEN PIXELS; SetRect takes CANVAS units — scale first, or the tip drifts off its
        // "?" whenever the UI scale isn't 1 (WindowBuilder-Patterns.md, click-to-open tooltip).
        var fw = _services.Framework;
        float k = fw.ScreenWidth > 0 && fw.CanvasWidth > 1f ? (float)fw.CanvasWidth / fw.ScreenWidth : 1f;
        r = new WindowRect(r.X * k, r.Y * k, r.Width * k, r.Height * k);
        float canvasH = fw.CanvasHeight > 1f ? fw.CanvasHeight : fw.ScreenHeight;
        // Below the "?", right-aligned to it, so the popover opens to the LEFT over the section instead of off the window.
        float x = MathF.Max(10f, r.X + r.Width - HelpTipWidth);
        float y = MathF.Min(r.Y + r.Height + 4f, canvasH - 40f);
        _helpRect = new WindowRect(x, y, HelpTipWidth, 0f);
        _helpWindow.SetVisible(true);
        _helpWindow.SetRect(_helpRect);
        // Front: the main window may have been raised after the tip, and when switching between the "?"s the tip is
        // already visible (no remount → no fresh stacking position) — CLAUDE.md Rule 5 exception.
        _helpWindow.BringToFront();
        _helpWindow.MarkDirty();   // re-pull title / body for the new tip this frame
        // The first-ever open mounts the window and applies its DefaultRect AFTER this SetRect → re-apply the anchored
        // rect for the next few ticks so it lands under the "?" on the first open too.
        _helpRepositionTicks = 4;
        _dsWindow.MarkDirty();     // the dots' "open" tint
    }

    private void CloseHelp()
    {
        _helpRepositionTicks = 0;
        _helpTopic = HelpTopic.None;
        if (_helpWindow is { IsShown: true }) _helpWindow.SetVisible(false);
        _dsWindow?.MarkDirty();
    }

    // OnUpdate: re-assert the tip position while the reposition window is open (early-out otherwise).
    private void HelpRepositionTick()
    {
        if (_helpRepositionTicks <= 0) return;
        _helpRepositionTicks--;
        if (_helpWindow.IsShown) _helpWindow.SetRect(_helpRect);
    }
}
