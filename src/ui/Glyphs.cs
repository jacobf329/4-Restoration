namespace HitboxClone;

/// <summary>The abstract actions a prompt can refer to, independent of any physical button.</summary>
public enum Prompt { Confirm, Cancel, Special, ClassAbility, Dash, Melee, Use, Swap, Ads, Start, NavVert, NavHorz, NavAny }

/// <summary>
/// Turns an abstract <see cref="Prompt"/> into the label for the device actually in the player's
/// hands. Getting this wrong is one of the small things that makes a game feel like it does not
/// really support controllers — telling a DualSense owner to press "A" is a papercut we avoid.
///
/// Pad labels are read out of <see cref="PadBindings"/> rather than written out here. They used to
/// be a second hardcoded table, and it drifted: every screen prompted "X" for Special after
/// Special moved to LB, and every screen prompted "A" to confirm while the code was reading the
/// right trigger. A prompt that has to be kept in sync by hand eventually is not.
///
/// Note the Nintendo swap: the physical button in the east position is labelled A, and the south
/// button is B, the opposite of Xbox. Godot reports positional buttons, so only the label changes —
/// which <see cref="PadBinding.Label"/> already handles.
/// </summary>
public static class Glyphs
{
    /// <summary>
    /// The gamepad action behind a prompt, or null for the ones that are stick and d-pad
    /// directions rather than buttons.
    ///
    /// Confirm is the Jump binding. That is not an accident of implementation: A jumps in the
    /// match and confirms in the menu, which is the layout every console shooter uses.
    /// </summary>
    static PadAction? ActionFor(Prompt p) => p switch
    {
        Prompt.Confirm => PadAction.Jump,
        // The Crouch binding, which is B. Deriving it that way rather than naming a button gets the
        // label right on every pad for free: Circle on PlayStation, and A on a Nintendo pad, whose
        // east button is labelled the opposite way round from Xbox.
        Prompt.Cancel => PadAction.Crouch,
        Prompt.Special => PadAction.Special,
        Prompt.ClassAbility => PadAction.ClassAbility,
        Prompt.Dash => PadAction.Dash,
        Prompt.Melee => PadAction.Melee,
        Prompt.Use => PadAction.Use,
        Prompt.Swap => PadAction.SwapWeapon,
        // The aim trigger. A juggernaut has no sights to aim down, so the same button raises the
        // saber instead — one prompt, and it names whichever button the pad actually uses.
        Prompt.Ads => PadAction.Ads,
        Prompt.Start => PadAction.Start,
        _ => null,
    };

    public static string For(Prompt p, PadKind kind)
    {
        if (kind == PadKind.Keyboard)
            return p switch
            {
                Prompt.Confirm => "Space",
                Prompt.Cancel => "Backspace",
                Prompt.Special => "F",
                Prompt.ClassAbility => "T",
                Prompt.Dash => "X",
                Prompt.Melee => "B",
                Prompt.Use => "G",
                Prompt.Swap => "Z",
                Prompt.Ads => "Right Mouse",
                Prompt.Start => "Enter",
                Prompt.NavVert => "W/S",
                Prompt.NavHorz => "A/D",
                _ => "WASD",
            };

        if (ActionFor(p) is { } action)
        {
            // Start carries a second binding for the Guide button, which is a fallback rather than
            // something to prompt with — naming both would read as "press Start or Guide".
            var list = PadBindings.For(action);
            return list.Count > 0 ? list[0].Label(kind) : "?";
        }

        return p switch
        {
            Prompt.NavVert => "D-Pad U/D",
            Prompt.NavHorz => "D-Pad L/R",
            _ => "D-Pad",
        };
    }

    /// <summary>
    /// The prompt label for whichever device is most likely driving the menu right now, so hints
    /// track the pad the player just picked up rather than being hardcoded to one device.
    ///
    /// With nothing driving the menu yet, the fallback follows the hardware actually present:
    /// telling someone with no gamepad plugged in to "press A" is the exact papercut this system
    /// exists to avoid.
    /// </summary>
    public static string For(Prompt p, InputDevice? d)
    {
        if (d != null) return For(p, d.Kind);
        return For(p, Devices.ConnectedGamepadCount() > 0 ? PadKind.Generic : PadKind.Keyboard);
    }
}
