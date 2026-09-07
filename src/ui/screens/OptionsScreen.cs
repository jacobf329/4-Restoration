using System.Collections.Generic;
using Godot;

namespace HitboxClone;

/// <summary>
/// Input options.
///
/// There is one switch, because there is one decision worth exposing: whether keyboard and mouse
/// count as a player. This is a controller game, so they do not by default.
/// </summary>
public sealed class OptionsScreen : UiScreen
{
    public override string Title => "OPTIONS";

    readonly Menu menu = new();

    public OptionsScreen()
    {
        menu.AddSetting("Keyboard & mouse players",
            () => UserSettings.KeyboardAndMouse ? "On" : "Off",
            _ => { UserSettings.KeyboardAndMouse = !UserSettings.KeyboardAndMouse; UserSettings.Save(); });

        menu.AddSetting("Aim assist",
            () => UserSettings.AimAssistName,
            dx =>
            {
                UserSettings.AimAssist = Mathf.PosMod(UserSettings.AimAssist + dx, 4);
                UserSettings.Save();
            });

        menu.AddSetting("Graphics quality",
            () => Graphics.QualityNames[(int)UserSettings.Quality],
            dx =>
            {
                UserSettings.Quality = (GraphicsQuality)Mathf.PosMod((int)UserSettings.Quality + dx, 3);
                UserSettings.Save();
            });

        menu.Add("Controller bindings", () => Stack.Push(new RebindScreen()));
    }

    protected override void UpdateScreen(float dt, IReadOnlyList<InputDevice> devices)
        => menu.Update(devices);

    public override void Draw(UiPainter p)
    {
        Chrome.Background(p, MenuBackdrop.Options);
        Chrome.Header(p, Title);

        float cx = p.Size.X * 0.5f;
        float mw = 720f;
        float top = p.Size.Y * 0.32f;

        menu.Draw(p, cx - mw * 0.5f, top, mw);

        // Explain what the switch protects against rather than making the player guess — the
        // reason it defaults off is not self-evident.
        string[] help =
        {
            "Off means only gamepads can claim a slot and fight.",
            "Keyboards can always navigate menus either way.",
            "",
            "Leaving this off also stops a controller being translated into",
            "keypresses or mouse clicks from acting as a second player —",
            "the usual cause is Steam Input running a desktop layout.",
        };

        // Measured from the bottom of the menu, not from a hardcoded two rows down. Adding the aim
        // assist row pushed the menu into help text that had been pinned at a fixed height, and the
        // two drew on top of each other.
        float hy = top + menu.Items.Count * 52f + 26f;
        foreach (var line in help)
        {
            p.TextCentered(line, cx, hy, 19, Pal.TextDim);
            hy += 27f;
        }

        int pads = Devices.ConnectedGamepadCount();
        if (pads == 0 && !UserSettings.KeyboardAndMouse)
            p.TextCentered("No gamepads connected — turn this on to play on the keyboard.",
                           cx, hy + 24f, 20, Pal.Warn);

        var d = menu.LastDevice;
        p.HintBar(
            (Glyphs.For(Prompt.NavHorz, d), "Toggle"),
            (Glyphs.For(Prompt.Cancel, d), "Back"));
    }
}
