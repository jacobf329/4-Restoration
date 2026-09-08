using System.Collections.Generic;
using Godot;

namespace HitboxClone;

public sealed class TitleScreen : UiScreen
{
    public override string Title => "HITBOX";

    readonly MatchSettings settings;
    readonly Main app;
    readonly Menu menu = new();

    public TitleScreen(MatchSettings settings, Main app)
    {
        this.settings = settings;
        this.app = app;

        menu.Add("Play", () => Stack.Push(new ModeSelectScreen(settings, app)));

        // Second, not first. The versus game is what this project is and what somebody launching it
        // is most likely here for; the story is the thing you go and find.
        menu.Add("Story", () => Stack.Push(new StoryScreen(app)));
        menu.Add("Controls", () => Stack.Push(new ControlsScreen()));
        menu.Add("Options", () => Stack.Push(new OptionsScreen()));
        menu.Add("Devices", () => Stack.Push(new DeviceTestScreen()));

        // The four peoples and what each of them can do, side by side. Reachable from the title
        // because the specials are the least discoverable thing in the game, and the lobby only
        // ever shows you the one you currently have selected.
        //
        // Deliberately below Devices rather than beside Controls: the menu-navigation tests walk
        // this list by row index, and inserting into the middle of it would break assertions that
        // have nothing to do with factions.
        menu.Add("Factions", () => Stack.Push(new FactionLineupScreen(app)));
        menu.Add("Quit", () => Stack.QuitRequested = true);
    }

    /// <summary>
    /// The root screen has nowhere to retreat to, so back is a no-op here rather than a quit.
    /// Quitting stays an explicit menu choice — tapping back once too many times should never
    /// close the game out from under four people on a couch.
    /// </summary>
    protected override bool OnBack(InputDevice d) => true;

    public override string? SelectedLabel => menu.Current?.Label;

    protected override void UpdateScreen(float dt, IReadOnlyList<InputDevice> devices)
        => menu.Update(devices);

    public override void Draw(UiPainter p)
    {
        Chrome.Background(p, MenuBackdrop.Title);

        float cx = p.Size.X * 0.5f;
        float titleY = p.Size.Y * 0.13f;

        // The wordmark gets a slab behind it and a rule under it, so it reads as a logo rather
        // than as the largest line of text on the screen.
        var slab = p.Measure("HITBOX", 96);
        p.Rect(cx - slab.X * 0.5f - 30f, titleY - 12f, 10f, slab.Y + 24f, Pal.Accent);

        p.TextCentered("HITBOX", cx, titleY, 96, Pal.Text);
        p.Rect(cx - slab.X * 0.5f, titleY + slab.Y + 14f, slab.X, 2f, Pal.PanelHi);
        p.TextCentered("a controller-first arena shooter", cx, titleY + slab.Y + 28f, 22, Pal.TextDim);

        float mw = 420f;
        menu.Draw(p, cx - mw * 0.5f, p.Size.Y * 0.46f, mw, 30, 62f);

        int pads = Devices.ConnectedGamepadCount();

        // Says what is actually true given the settings. Claiming "keyboard works" while keyboard
        // players are switched off would send someone into a lobby they cannot join.
        (string padLine, Color padColour) = pads > 0
            ? ($"{pads} gamepad{(pads == 1 ? "" : "s")} ready", Pal.Ready)
            : UserSettings.KeyboardAndMouse
                ? ("No gamepads detected — keyboard players are enabled, so you can still play", Pal.TextDim)
                : ("No gamepads detected — plug one in, or enable keyboard players in Options", Pal.Warn);

        p.TextCentered(padLine, cx, p.Size.Y - 108f, 18, padColour);

        // The build, bottom left, always.
        //
        // Small, dim, and never worth reading until the one moment it is worth everything:
        // "did my update actually take?" was unanswerable from inside the game for several
        // rounds across two machines, and the honest answer each time was that nobody could
        // tell. A commit and a date settle it in a glance, and an asterisk says the folder has
        // edits in it that no update will overwrite.
        string build = BuildInfo.LocalEdits
            ? $"build {BuildInfo.Commit}* — {BuildInfo.Date}"
            : $"build {BuildInfo.Commit} — {BuildInfo.Date}";

        p.Text(build, 22f, p.Size.Y - 74f, 15, Pal.TextDim);

        var d = menu.LastDevice;
        p.HintBar(
            (Glyphs.For(Prompt.NavVert, d), "Navigate"),
            (Glyphs.For(Prompt.Confirm, d), "Select"));
    }
}
