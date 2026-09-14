using System.Collections.Generic;
using Godot;

namespace HitboxClone;

/// <summary>
/// The mixer, as four sliders.
///
/// Split off the options screen rather than added to it because these are the only settings in the
/// game you cannot judge by reading - you have to hear them - and a row you have to hear wants the
/// screen's whole attention: a bar wide enough to see, and a cue firing as you move it. The test
/// row at the bottom is the point of the screen, not a convenience. Setting effects against music
/// with nothing playing is guesswork.
/// </summary>
public sealed class AudioScreen : UiScreen
{
    public override string Title => "AUDIO";

    readonly Menu menu = new();

    /// <summary>Which gun the test row fires next, so it is obvious the row did something.</summary>
    int testShot;

    static readonly string[] TestKeys =
    {
        "w_assault_rifle", "w_shotgun", "x_large", "w_sniper_rifle", "v_tank_cannon",
    };

    public AudioScreen()
    {
        menu.AddSetting("Master volume",
            () => Bar(UserSettings.MasterVolume),
            dx => { UserSettings.MasterVolume = Nudge(UserSettings.MasterVolume, dx); Commit(); });

        menu.AddSetting("Music volume",
            () => Bar(UserSettings.MusicVolume),
            dx => { UserSettings.MusicVolume = Nudge(UserSettings.MusicVolume, dx); Commit(); });

        menu.AddSetting("Effects volume",
            () => Bar(UserSettings.SfxVolume),
            dx =>
            {
                UserSettings.SfxVolume = Nudge(UserSettings.SfxVolume, dx);
                Commit();

                // The slider you are moving is the one you should hear. Nudging effects with
                // nothing playing is the reason a mixer feels broken when it is not.
                Sfx.PlayKey("ui_claim");
            });

        menu.AddSetting("Ambience volume",
            () => Bar(UserSettings.AmbienceVolume),
            dx => { UserSettings.AmbienceVolume = Nudge(UserSettings.AmbienceVolume, dx); Commit(); });

        menu.Add("Test the mix", () =>
        {
            Sfx.PlayKey(TestKeys[testShot % TestKeys.Length]);
            testShot++;
        });
    }

    static int Nudge(int step, int dx) => Mathf.Clamp(step + dx, 0, Audio.Steps);

    /// <summary>
    /// Takes the change live and writes it down.
    ///
    /// Both halves matter. Applying without saving means the setting is gone next launch; saving
    /// without applying means the bar moves and the sound does not, which reads as a broken screen
    /// rather than as a delayed one.
    /// </summary>
    static void Commit()
    {
        Audio.Apply();
        UserSettings.Save();
    }

    public override string? SelectedLabel => menu.Current?.Label;

    protected override void UpdateScreen(float dt, IReadOnlyList<InputDevice> devices)
        => menu.Update(devices);

    /// <summary>A ten-segment bar. Easier to aim at than a number, and readable across a room.</summary>
    static string Bar(int step)
    {
        step = Mathf.Clamp(step, 0, Audio.Steps);
        var s = new System.Text.StringBuilder(Audio.Steps + 6);
        for (int i = 0; i < Audio.Steps; i++) s.Append(i < step ? '|' : '.');
        return s.ToString();
    }

    public override void Draw(UiPainter p)
    {
        Chrome.Background(p, MenuBackdrop.Options);
        Chrome.Header(p, Title);

        float cx = p.Size.X * 0.5f;
        float mw = 720f;
        float top = p.Size.Y * 0.34f;

        menu.Draw(p, cx - mw * 0.5f, top, mw);

        string[] help =
        {
            "Nine of ten is unity — the level the game was mixed at.",
            "Each step is three and a half decibels; zero mutes.",
            "",
            "Effects sit a step under music by default because every",
            "effect is mastered to full scale and there can be sixteen",
            "of them at once. Raise it if the guns feel thin.",
            "",
            "Ambience is the room itself — wind, water, machinery.",
        };

        float hy = top + menu.Items.Count * 52f + 26f;
        foreach (var line in help)
        {
            p.TextCentered(line, cx, hy, 19, Pal.TextDim);
            hy += 27f;
        }

        var d = menu.LastDevice;
        p.HintBar(
            (Glyphs.For(Prompt.NavHorz, d), "Adjust"),
            (Glyphs.For(Prompt.Confirm, d), "Test"),
            (Glyphs.For(Prompt.Cancel, d), "Back"));
    }
}
