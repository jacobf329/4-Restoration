using System.Collections.Generic;
using Godot;

namespace HitboxClone;

/// <summary>
/// Pause. Also where a mid-match controller disconnect lands, naming the player who dropped so the
/// couch knows what happened rather than watching a body freeze in place.
/// </summary>
public sealed class PauseScreen : UiScreen
{
    public override string Title => "PAUSED";

    readonly Match match;
    readonly Main app;
    readonly string? note;
    readonly Menu menu = new();

    public PauseScreen(Match match, string? note, Main app)
    {
        this.match = match;
        this.app = app;
        this.note = note;

        menu.Add("Resume", Resume);

        // Reachable mid-match, because the moment you need the controls is usually the moment you
        // are already playing and have forgotten which button slides.
        menu.Add("Controls", () => Stack.Push(new ControlsScreen()));
        menu.Add("Quit to title", () =>
        {
            match.Paused = false;
            Stack.Reset(new TitleScreen(app.Settings, app));
        });
    }

    void Resume()
    {
        match.Paused = false;
        Stack.Pop();
    }

    protected override bool OnBack(InputDevice d) { Resume(); return true; }

    protected override void UpdateScreen(float dt, IReadOnlyList<InputDevice> devices)
        => menu.Update(devices);

    public override void Draw(UiPainter p)
    {
        // Dim rather than clear: the arena stays visible behind, which makes pause feel like a
        // pause rather than a scene change.
        p.Rect(0, 0, p.Size.X, p.Size.Y, new Color(0.04f, 0.05f, 0.07f, 0.78f));

        float cx = p.Size.X * 0.5f;
        p.TextCentered(Title, cx, p.Size.Y * 0.24f, 60, Pal.Text);

        if (note != null)
            p.TextCentered(note, cx, p.Size.Y * 0.24f + 76f, 22, Pal.Warn);

        float mw = 380f;
        menu.Draw(p, cx - mw * 0.5f, p.Size.Y * 0.46f, mw, 28, 58f);

        var d = menu.LastDevice;

        // A reminder of what each player's special does, because pause is exactly when someone
        // asks "what does my left bumper do again?" and the lobby is several screens behind them.
        float y = p.Size.Y * 0.46f + 3f * 58f + 40f;
        string special = Glyphs.For(Prompt.Special, d);

        foreach (var pawn in match.Pawns)
        {
            if (pawn.IsBot) continue;

            p.TextCentered($"{pawn.Name2} · {pawn.Faction.Name} · {special} {pawn.Faction.SpecialName}",
                           cx, y, 18, pawn.Faction.Tint);
            p.TextCentered(pawn.Faction.SpecialBlurb, cx, y + 22f, 15, Pal.TextDim);
            y += 52f;
        }
        p.HintBar(
            (Glyphs.For(Prompt.NavVert, d), "Navigate"),
            (Glyphs.For(Prompt.Confirm, d), "Select"),
            (Glyphs.For(Prompt.Cancel, d), "Resume"));
    }
}
