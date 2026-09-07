using System.Collections.Generic;
using Godot;

namespace HitboxClone;

/// <summary>
/// The controls reference.
///
/// Every binding shown is read live from <see cref="PadBindings"/> and labelled for the pad
/// actually connected, so it stays correct after a rebind and never drifts from reality the way a
/// hand-written list would. The rules at the bottom are the things the button names alone do not
/// tell you — that crouching mid-sprint slides, that dashing has invulnerability frames.
/// </summary>
public sealed class ControlsScreen : UiScreen
{
    public override string Title => "CONTROLS";

    static InputDevice? FirstPad()
    {
        foreach (var g in Devices.Gamepads) if (g.Connected) return g;
        return null;
    }

    protected override void UpdateScreen(float dt, IReadOnlyList<InputDevice> devices) { }

    public override void Draw(UiPainter p)
    {
        Chrome.Background(p, MenuBackdrop.Options);

        var pad = FirstPad();
        var kind = pad?.Kind ?? PadKind.Generic;

        float top = Chrome.Header(p, Title,
            pad != null ? $"Reading from {pad.Label}" : "Showing default gamepad layout");

        float colW = (p.Size.X - 56f * 2f - 60f) * 0.5f;
        float leftX = 56f;
        float rightX = leftX + colW + 60f;

        // ---- left column: movement ----
        float y = top;

        Chrome.SectionRule(p, "MOVEMENT", leftX, y, colW);
        y += 34f;
        y = Row(p, leftX, y, colW, "Move", "Left stick");
        y = Row(p, leftX, y, colW, "Look / aim", "Right stick");
        y = Row(p, leftX, y, colW, "Jump", PadBindings.Describe(PadAction.Jump, kind));
        y = Row(p, leftX, y, colW, "Sprint", PadBindings.Describe(PadAction.Sprint, kind));
        y = Row(p, leftX, y, colW, "Crouch / Slide", PadBindings.Describe(PadAction.Crouch, kind));
        y = Row(p, leftX, y, colW, "Dash", PadBindings.Describe(PadAction.Dash, kind));

        y += 22f;
        Chrome.SectionRule(p, "MENUS", leftX, y, colW);
        y += 34f;
        y = Row(p, leftX, y, colW, "Navigate", "Left stick / D-pad");
        // Confirm is the Jump binding, not Attack. A jumps in the match and confirms in the menu,
        // the way every console shooter does it.
        y = Row(p, leftX, y, colW, "Confirm", PadBindings.Describe(PadAction.Jump, kind));
        // Cancel is B — the Crouch binding — as well as the pad's own Back button. Both are shown,
        // because the small Back button still works and nobody would guess the other one from a
        // row that only said "Back".
        y = Row(p, leftX, y, colW, "Back",
                $"{PadBindings.Describe(PadAction.Crouch, kind)} / {PadBindings.Describe(PadAction.Back, kind)}");
        y = Row(p, leftX, y, colW, "Start / Pause", PadBindings.Describe(PadAction.Start, kind));

        // ---- right column: combat ----
        y = top;

        Chrome.SectionRule(p, "COMBAT", rightX, y, colW);
        y += 34f;
        y = Row(p, rightX, y, colW, "Attack", PadBindings.Describe(PadAction.Attack, kind));
        y = Row(p, rightX, y, colW, "Aim down sights", PadBindings.Describe(PadAction.Ads, kind));
        y = Row(p, rightX, y, colW, "Melee", PadBindings.Describe(PadAction.Melee, kind));
        y = Row(p, rightX, y, colW, "Faction special", PadBindings.Describe(PadAction.Special, kind));
        y = Row(p, rightX, y, colW, "Class ability", PadBindings.Describe(PadAction.ClassAbility, kind));
        y = Row(p, rightX, y, colW, "Interact / vehicle", PadBindings.Describe(PadAction.Use, kind));
        y = Row(p, rightX, y, colW, "Swap weapon", PadBindings.Describe(PadAction.SwapWeapon, kind));

        y += 22f;
        Chrome.SectionRule(p, "WORTH KNOWING", rightX, y, colW);
        y += 34f;

        string[] rules =
        {
            "Crouch while sprinting to slide. Jump cancels it.",
            "Dash goes where your crosshair points, up and down included.",
            "Dashing gives brief invulnerability — dodge through shots.",
            "Melee always works, whatever is in your hands.",
            "Aiming tightens spread and steadies your look, but slows you.",
            "You cannot fire while sprinting.",
            $"Headshots do {Match.HeadshotMultiplier:0.#}x damage — aim high.",
            "Green pads launch you. Crates on the map hold better guns.",
            "Walk up to a car or tank and interact to drive it.",
            "Explosives bring down skybridges. Mind what you are standing on.",
            "Hold interact to take a weapon. You carry two — swap between them.",
            "Green crates are health. Walk over them.",
            "Two abilities: your faction's special, and your class's.",
            "Falling into a pit kills you and costs a frag.",
        };

        foreach (var r in rules)
        {
            p.Rect(rightX, y + 9f, 4f, 4f, Pal.Accent);
            p.Text(r, rightX + 16f, y, 18, Pal.TextDim);
            y += 28f;
        }

        p.HintBar((Glyphs.For(Prompt.Cancel, pad), "Back"));
    }

    /// <summary>One label/binding pair, with a dotted leader so the eye tracks across the gap.</summary>
    static float Row(UiPainter p, float x, float y, float w, string label, string binding)
    {
        p.Text(label, x, y, 21, Pal.Text);

        float labelEnd = x + p.Measure(label, 21).X + 12f;
        float valueStart = x + w - p.Measure(binding, 21).X - 12f;

        for (float dx = labelEnd; dx < valueStart; dx += 9f)
            p.Rect(dx, y + 14f, 2f, 2f, Pal.PanelHi);

        p.TextRight(binding, x + w, y, 21, Pal.Accent);
        return y + 34f;
    }
}
