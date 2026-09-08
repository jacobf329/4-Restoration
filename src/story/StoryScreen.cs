using System.Collections.Generic;
using Godot;

namespace HitboxClone;

/// <summary>
/// Story mode: chapter card, then the act's script, then on to the next act.
///
/// The campaign shell rather than the campaign. It owns the flow every act needs — where you are,
/// what you chose, what she thinks of you, and a save file that survives the game closing — and it
/// plays the script as text over a chapter card.
///
/// What it deliberately does not do yet is put you in Fairview. A playable act needs a mission
/// layer that <see cref="MatchScreen"/> does not have: a match is built around ending on a score
/// limit, and a scene ends when its last line is read. Building the shell first means the
/// sequencing, the branch and the save are all exercised by the harness before a single objective
/// exists, and the missions get hung on something already known to work.
/// </summary>
public sealed class StoryScreen : UiScreen
{
    readonly Main app;
    CampaignState state;

    /// <summary>The beats of the current act that this playthrough actually sees.</summary>
    readonly List<Beat> beats = new();

    int at;

    /// <summary>True while the chapter card is up and the script has not started.</summary>
    bool card = true;

    /// <summary>Set while the harvest question is on screen and the answer is outstanding.</summary>
    Menu? choice;

    /// <summary>
    /// The device that last pressed confirm here, and therefore the one that is playing.
    ///
    /// Remembered because a scene has to be handed to a specific device and there is no lobby in
    /// story mode to claim one. Picking the first registered device instead is what broke this:
    /// the keyboard is registered before any gamepad, so the scene bound itself to the keyboard
    /// while the player was holding a pad, and a pawn whose view belongs to another device gets
    /// no input at all.
    /// </summary>
    InputDevice? driver;

    public StoryScreen(Main app)
    {
        this.app = app;
        state = CampaignState.Load();
        BeginAct();
    }

    public override string Title => $"Story — {Acts.Get(state.Act).Name}";

    /// <summary>
    /// Load the current act's beats, filtered to this playthrough.
    ///
    /// Filtered once on entry rather than tested per beat while reading. A beat that plays only for
    /// one answer is skipped here, so everything downstream — the counter, the end of the act, the
    /// harness — deals with a plain list and cannot get the arithmetic wrong at the last line.
    /// </summary>
    void BeginAct()
    {
        beats.Clear();
        at = 0;
        card = true;
        choice = null;

        foreach (var b in Scripts.For(state.Act).Beats)
            if (b.PlaysFor(state.Choice)) beats.Add(b);
    }

    protected override void UpdateScreen(float dt, IReadOnlyList<InputDevice> devices)
    {
        if (choice != null) { choice.Update(devices); return; }

        foreach (var d in devices)
        {
            if (!d.Connected) continue;
            if (!d.ConfirmPressed && !d.StartPressed) continue;

            driver = d;
            Sfx.Play(Sound.MenuMove, -6f);
            Advance();
            return;
        }
    }

    void Advance()
    {
        if (card)
        {
            card = false;

            // An act with a mission is played rather than read. The script for that act stays in
            // Scripts as the reading version and is not used here — the beats a player hears are
            // the ones attached to the places they hear them in, which is what makes it a scene in
            // a town instead of a wall of text with a town behind it.
            if (Missions.For(state.Act, state) is { } mission) Launch(mission);
            return;
        }

        if (at < beats.Count - 1) { at++; return; }

        // The end of the act's script. The harvest act asks its question here rather than at a
        // beat, because the question is not a line of dialogue — it is the only thing in the game
        // that changes what the rest of it means, and it should not be possible to walk past it.
        if (state.Act == Act.Harvest && state.Choice == HarvestChoice.Undecided)
        {
            OpenChoice();
            return;
        }

        if (!state.Advance())
        {
            // The last act. Nothing after the ruling yet, so the campaign ends where the writing
            // does rather than pretending there is more.
            Stack.Pop();
            return;
        }

        state.Save();
        BeginAct();
    }

    /// <summary>
    /// Drop into the town and run the scene there.
    ///
    /// The match screen is pushed on top of this one, so when the scene ends and it pops, the
    /// campaign is still underneath holding the act, the choice and her — which is the whole
    /// reason the shell was built before the missions were.
    /// </summary>
    void Launch(IMission mission)
    {
        var settings = Missions.SettingsFor(state.Act);

        // One player, no bots. A slot counts as claimed by having a device on it, and the device
        // is whoever pressed the button to get here rather than whichever happens to be first in
        // the registry.
        //
        // Deliberately not filtered through Devices.CanClaimSlot. That rule keeps keyboards out of
        // the lobby unless the player opts in, and the reason is real - a pad being translated into
        // keypresses by Steam Input would otherwise claim a second slot and act twice. Story mode
        // has exactly one slot, so there is no second claim to make, and refusing to let somebody
        // play the story on the keyboard they just navigated the menu with would be its own bug.
        var slots = new LobbySlot[LobbyScreen.MaxPlayers];
        for (int i = 0; i < slots.Length; i++) slots[i] = new LobbySlot();

        // John is a Vessel by upbringing and nothing else. He has no faction of his own and the
        // faction axis has no "human" on it yet - see STORY.md, where his kit is the open
        // question. Wearing the colours of the people who raised him is the least wrong answer
        // available today and is not meant to survive contact with a decision about it.
        slots[0].DeviceId = (driver ?? FirstUsable())?.Id;
        slots[0].FactionIndex = 0;
        slots[0].ClassIndex = 0;

        var screen = new MatchScreen(settings, slots, app) { Mission = mission };
        screen.OnMissionDone = SceneFinished;

        Stack.Push(screen);
    }

    /// <summary>
    /// Something to play with when we have not seen a button pressed yet.
    ///
    /// A connected gamepad first, because this is a controller-first game and somebody with a pad
    /// plugged in meant to use it. Any connected device otherwise, so the scene is playable rather
    /// than inert.
    /// </summary>
    static InputDevice? FirstUsable()
    {
        foreach (var d in Devices.All) if (d.Connected && d.IsGamepad) return d;
        foreach (var d in Devices.All) if (d.Connected) return d;
        return null;
    }

    /// <summary>The scene is over. Move the campaign on, exactly as reading to the end would.</summary>
    void SceneFinished()
    {
        if (state.Act == Act.Harvest && state.Choice == HarvestChoice.Undecided)
        {
            OpenChoice();
            return;
        }

        if (!state.Advance()) { Stack.Pop(); return; }

        state.Save();
        BeginAct();
    }

    void OpenChoice()
    {
        choice = new Menu();

        choice.Add("Allow the harvest", () => Answer(HarvestChoice.Harvested));
        choice.Add("Wait for someone to want you", () => Answer(HarvestChoice.Waited));
    }

    void Answer(HarvestChoice picked)
    {
        state.Decide(picked);
        state.Save();
        choice = null;

        state.Advance();
        state.Save();
        BeginAct();
    }

    /// <summary>Abandon the run. The save stays, so backing out is not losing your place.</summary>
    protected override bool OnBack(InputDevice d)
    {
        state.Save();
        return base.OnBack(d);
    }

    public override void Draw(UiPainter p)
    {
        p.Clear(Pal.Bg);

        if (card) { DrawCard(p); return; }
        if (choice != null) { DrawChoice(p); return; }

        DrawBeat(p);
    }

    void DrawCard(UiPainter p)
    {
        var def = Acts.Get(state.Act);
        float cx = p.Size.X * 0.5f;
        float cy = p.Size.Y * 0.42f;

        var tint = def.Host?.Tint ?? Pal.Accent;

        p.TextCentered($"ACT {(int)state.Act + 1}", cx, cy - 74f, 20, Pal.TextDim);
        p.TextCentered(def.Name.ToUpperInvariant(), cx, cy - 34f, 54, tint);
        p.TextCentered(def.Blurb, cx, cy + 34f, 20, Pal.TextDim);

        if (def.Host is { } host)
            p.TextCentered(host.Name, cx, cy + 74f, 17, tint * new Color(1, 1, 1, 0.7f));

        p.HintBar(("A", "Begin"), ("B", "Back"));
    }

    void DrawBeat(UiPainter p)
    {
        // An act with nothing written in it still gets a screen rather than falling through, so an
        // unwritten chapter reads as unwritten instead of as a bug.
        if (beats.Count == 0)
        {
            p.TextCentered("This act is not written yet.", p.Size.X * 0.5f, p.Size.Y * 0.45f,
                           22, Pal.TextDim);
            p.HintBar(("A", "Continue"), ("B", "Back"));
            return;
        }

        var beat = beats[at];

        float w = MathF.Min(920f, p.Size.X - 160f);
        float x = p.Size.X * 0.5f - w * 0.5f;
        float y = p.Size.Y * 0.58f;

        p.Panel(x, y, w, 190f, Pal.Panel, Scripts.TintOf(beat.Who) * new Color(1, 1, 1, 0.5f));

        string name = Scripts.NameOf(beat.Who);
        if (name.Length > 0)
            p.Text(name, x + 26f, y + 30f, 18, Scripts.TintOf(beat.Who));

        // Wrapped by measuring, because the painter draws a string where it is told and has no
        // opinion about the edge of the panel.
        float textY = y + (name.Length > 0 ? 66f : 46f);
        foreach (string line in Wrap(p, beat.Line, w - 52f, 22))
        {
            p.Text(line, x + 26f, textY, 22, beat.Who == Speaker.Narrator ? Pal.TextDim : Pal.Text);
            textY += 30f;
        }

        p.TextRight($"{at + 1} / {beats.Count}", x + w - 26f, y + 30f, 15, Pal.TextDim);
        p.HintBar(("A", "Continue"), ("B", "Leave"));
    }

    void DrawChoice(UiPainter p)
    {
        float cx = p.Size.X * 0.5f;

        p.TextCentered("IT IS YOUR BODY", cx, p.Size.Y * 0.26f, 40, Pal.Text);
        p.TextCentered("The only fact in the room none of them can argue with.",
                       cx, p.Size.Y * 0.26f + 52f, 19, Pal.TextDim);

        float w = MathF.Min(620f, p.Size.X - 200f);
        choice!.Draw(p, cx - w * 0.5f, p.Size.Y * 0.5f, w);
        p.HintBar(("A", "Choose"));
    }

    /// <summary>Greedy word wrap against the real measured width of the font in use.</summary>
    static List<string> Wrap(UiPainter p, string text, float width, int size)
    {
        var lines = new List<string>();
        string line = "";

        foreach (string word in text.Split(' '))
        {
            string next = line.Length == 0 ? word : line + " " + word;

            if (p.Measure(next, size).X <= width) { line = next; continue; }

            if (line.Length > 0) lines.Add(line);
            line = word;
        }

        if (line.Length > 0) lines.Add(line);
        return lines;
    }
}
