using System.Collections.Generic;
using Godot;

namespace HitboxClone;

public sealed class ResultsScreen : UiScreen
{
    public override string Title => "RESULTS";

    readonly Match match;
    readonly MatchSettings settings;
    readonly Main app;
    readonly Menu menu = new();
    readonly List<Pawn> standings;

    readonly LobbySlot[] slots;

    public ResultsScreen(Match match, MatchSettings settings, Main app, LobbySlot[] slots)
    {
        this.match = match;
        this.settings = settings;
        this.app = app;
        this.slots = slots;
        standings = match.Standings();

        // First and default, because it is what you want nine times out of ten. Going back through
        // the title screen, mode select and the lobby to play the same match again is four screens
        // of nothing.
        menu.Add("Play again", Rematch);
        menu.Add("Change setup", () => Stack.Reset(new ModeSelectScreen(settings, app)));
        menu.Add("Main menu", () => Stack.Reset(new TitleScreen(app.Settings, app)));
    }

    /// <summary>
    /// The same players, classes and settings, on a fresh match.
    ///
    /// A brand new <see cref="MatchScreen"/> rather than resetting the old one: it owns viewports,
    /// a World3D and a physics scene, and tearing all that down cleanly is exactly what its
    /// existing exit path already does.
    /// </summary>
    void Rematch() => Stack.Reset(new MatchScreen(settings, slots, app));

    /// <summary>Results is terminal for the match, so back goes the same place the menu does.</summary>
    protected override bool OnBack(InputDevice d)
    {
        Stack.Reset(new TitleScreen(app.Settings, app));
        return true;
    }

    protected override void UpdateScreen(float dt, IReadOnlyList<InputDevice> devices)
        => menu.Update(devices);

    public override void Draw(UiPainter p)
    {
        // The results screen keeps its own heavy wash rather than the shared scrim: it is a
        // full-screen overlay of standings, so nothing behind it needs to stay readable.
        if (MenuBackdrop.Get(MenuBackdrop.Results) is { } backdrop)
        {
            p.TextureCover(backdrop, 0.9f);
            p.Rect(0, 0, p.Size.X, p.Size.Y, new Color(0.04f, 0.05f, 0.07f, 0.72f));
        }
        else p.Rect(0, 0, p.Size.X, p.Size.Y, new Color(0.04f, 0.05f, 0.07f, 0.88f));

        float cx = p.Size.X * 0.5f;

        var winner = match.Winner;
        p.TextCentered(winner != null ? $"{winner.Name2} WINS" : "DRAW",
                       cx, p.Size.Y * 0.16f, 64, winner?.Tint ?? Pal.Text);
        p.TextCentered($"{settings.Def.Name}  ·  {match.Arena.Name}  ·  {MatchSettings.Clock(match.Elapsed)}",
                       cx, p.Size.Y * 0.16f + 78f, 22, Pal.TextDim);

        float w = 560f;
        float x = cx - w * 0.5f;
        float y = p.Size.Y * 0.36f;

        bool rounds = settings.Mode == GameMode.Elimination;

        p.Text("PLAYER", x, y, 16, Pal.TextDim);

        // What they finished the match *as*, which is not the same as the class they picked in the
        // lobby any more. Somebody who spent the last two lives as a Sinew and then a hero should
        // not be listed as a Trooper because that is what they started out.
        p.Text("FIGHTING AS", x + 190f, y, 16, Pal.TextDim);

        if (rounds) p.TextRight("ROUNDS", x + w - 270f, y, 16, Pal.TextDim);

        // Battle points earned, not the unspent balance. The balance rewards hoarding, and hoarding
        // is the one thing this economy should not be congratulating anyone for.
        p.TextRight("POINTS", x + w - 180f, y, 16, Pal.TextDim);
        p.TextRight("FRAGS", x + w - 90f, y, 16, Pal.TextDim);
        p.TextRight("DEATHS", x + w, y, 16, Pal.TextDim);
        y += 30f;

        foreach (var pawn in standings)
        {
            p.Text(pawn.Name2, x, y, 24, pawn.Tint);

            // A hero outranks the class underneath it — you were Achilles, not a Marksman wearing
            // a crown — so that is what the row says.
            string wearing = pawn.Crown is { } crown ? crown.Name : pawn.Class.Name;

            p.Text(wearing, x + 190f, y, 22, pawn.Crown != null ? pawn.Faction.Tint : Pal.Text);

            if (rounds) p.TextRight(pawn.RoundsWon.ToString(), x + w - 270f, y, 24, Pal.Accent);

            p.TextRight(pawn.PointsEarned.ToString(), x + w - 180f, y, 22, Pal.Ready);
            p.TextRight(pawn.Score.ToString(), x + w - 90f, y, 24, Pal.Text);
            p.TextRight(pawn.Deaths.ToString(), x + w, y, 24, Pal.TextDim);
            y += 34f;
        }

        menu.Draw(p, cx - 200f, y + 44f, 400f, 26, 52f);

        var d = menu.LastDevice;
        p.HintBar((Glyphs.For(Prompt.Confirm, d), "Continue"));
    }
}
