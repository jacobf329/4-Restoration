using System.Collections.Generic;
using Godot;

namespace HitboxClone;

public sealed class ModeSelectScreen : UiScreen
{
    public override string Title => "MODE";

    readonly MatchSettings settings;
    readonly Main app;
    readonly Menu menu = new();

    public ModeSelectScreen(MatchSettings settings, Main app)
    {
        this.settings = settings;
        this.app = app;

        menu.AddSetting("Mode", () => settings.Def.Name, dx => Cycle(dx));

        // Which half of Portal mode, and nothing at all in any other mode.
        //
        // Shown as a dash rather than hidden, because a row that appears and disappears as you
        // scroll past Mode moves everything under it and makes the menu feel unstable. A row that
        // is plainly not applicable costs one line and surprises nobody.
        menu.AddSetting("Portal mode",
                        () => settings.Mode == GameMode.Portal
                            ? (settings.Portal == PortalVariant.Puzzle ? "Puzzle (co-op)" : "Elimination")
                            : "—",
                        dx => CyclePortalVariant(dx));

        // Cycles through the arenas and then Random, which is stored as -1. Random is the default
        // so the arenas actually get seen without anyone having to go looking for them.
        menu.AddSetting("Arena", () => settings.ArenaName, CycleArena);
        menu.AddSetting("Score limit", () => $"{settings.ScoreLimit} {settings.Def.LimitNoun}",
                        dx => settings.ScoreLimit = Mathf.Clamp(
                            settings.ScoreLimit + dx * settings.Def.LimitStep,
                            settings.Def.MinLimit, settings.Def.MaxLimit));
        menu.AddSetting("Time limit", () => settings.TimeLimitName, CycleTimeLimit);
        menu.AddSetting("Bots", () => settings.BotCount.ToString(),
                        dx => settings.BotCount = Mathf.Clamp(settings.BotCount + dx, 0, LobbyScreen.MaxFighters - 1));
        menu.AddSetting("CPU difficulty", () => settings.BotSkillName,
                        dx => settings.BotSkill = Mathf.Clamp(settings.BotSkill + dx, 0, BotBrain.Skills.Length - 1));
        menu.Add("Continue to lobby", () => Stack.Push(new LobbyScreen(settings, app)));
    }


    /// <summary>
    /// Swap between the two halves of Portal mode, and snap the map to match.
    ///
    /// The two halves want different maps and cannot share one: the puzzle chambers have no floor
    /// between the islands and would be an instant loss for a deathmatch, and the combat arenas
    /// have no checkpoints and would be a puzzle with nothing to solve. Rather than let the menu
    /// hold a combination the match then has to quietly override, changing the variant resets the
    /// map to Random, which picks correctly for whichever half is now selected.
    /// </summary>
    void CyclePortalVariant(int dx)
    {
        if (settings.Mode != GameMode.Portal || dx == 0) return;

        settings.Portal = settings.Portal == PortalVariant.Puzzle
            ? PortalVariant.Elimination
            : PortalVariant.Puzzle;

        settings.ArenaIndex = -1;
    }

    /// <summary>
    /// Cycle the map, over the half of the list that suits the mode.
    ///
    /// The arena list now holds combat arenas and puzzle chambers together, and offering an
    /// Antechamber for a deathmatch would be offering a map with no floor on it. The match already
    /// refuses such a pairing, but a menu that lets you choose something that is then silently
    /// ignored is worse than one that does not offer it.
    /// </summary>
    void CycleArena(int dx)
    {
        int combat = Arena.Names.Length - Arena.PuzzleLayouts;
        bool puzzle = settings.IsPuzzle;

        int first = puzzle ? combat : 0;
        int count = puzzle ? Arena.PuzzleLayouts : combat;

        // Random is stored as -1 and sits at the end of the ring, so the order reads
        // map, map, …, Random, and back round.
        int at = settings.ArenaIndex < 0 ? count : Mathf.Clamp(settings.ArenaIndex - first, 0, count);

        at = Mathf.PosMod(at + dx, count + 1);
        settings.ArenaIndex = at == count ? -1 : first + at;
    }

    void CycleTimeLimit(int dx)
    {
        var choices = MatchSettings.TimeLimitChoices;

        int i = 0;
        for (int k = 0; k < choices.Length; k++)
            if (choices[k] == settings.TimeLimitSeconds) { i = k; break; }

        settings.TimeLimitSeconds = choices[Mathf.PosMod(i + dx, choices.Length)];
    }

    void Cycle(int dx)
    {
        int i = 0;
        for (int k = 0; k < Modes.All.Length; k++)
            if (Modes.All[k].Mode == settings.Mode) { i = k; break; }

        i = (i + dx + Modes.All.Length) % Modes.All.Length;
        settings.Mode = Modes.All[i].Mode;

        // Snap the limit to the new mode's own default. The modes do not count the same thing, so
        // carrying a number across them is meaningless — fifteen is a normal deathmatch and
        // fifteen seconds of holding a zone.
        settings.ScoreLimit = settings.Def.DefaultLimit;

        // A map chosen for the previous mode may be the wrong kind entirely now.
        if (settings.ArenaIndex >= 0 && Arena.IsPuzzle(settings.ArenaIndex) != settings.IsPuzzle)
            settings.ArenaIndex = -1;
    }

    /// <summary>Row count, so the self-test can walk to the last row without hardcoding a number.</summary>
    public int RowCount => menu.Items.Count;

    protected override void UpdateScreen(float dt, IReadOnlyList<InputDevice> devices)
        => menu.Update(devices);

    public override void Draw(UiPainter p)
    {
        Chrome.Background(p, MenuBackdrop.ModeSelect);
        Chrome.Header(p, Title, "Set up the match, then pick your slots in the lobby");

        float cx = p.Size.X * 0.5f;
        float mw = 620f;
        float top = p.Size.Y * 0.28f;
        menu.Draw(p, cx - mw * 0.5f, top, mw);

        // Describe whatever the cursor is on, so neither the mode nor the difficulty is just a
        // bare name the player has to guess the meaning of.
        string blurb = menu.Current?.Label == "CPU difficulty"
            ? settings.Bots.Blurb
            : settings.Def.Blurb;

        p.TextCentered(blurb, cx, top + RowCount * 52f + 30f, 20, Pal.TextDim);

        var d = menu.LastDevice;
        p.HintBar(
            (Glyphs.For(Prompt.NavVert, d), "Navigate"),
            (Glyphs.For(Prompt.NavHorz, d), "Change"),
            (Glyphs.For(Prompt.Confirm, d), "Select"),
            (Glyphs.For(Prompt.Cancel, d), "Back"));
    }
}
