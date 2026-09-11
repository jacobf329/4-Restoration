using System;
using System.Collections.Generic;
using Godot;

namespace HitboxClone;

/// <summary>The remappable gamepad actions. Sticks are not remappable — see <see cref="PadBindings"/>.</summary>
public enum PadAction { Attack, Ads, Jump, Sprint, Crouch, Special, ClassAbility, Dash, Melee, Use, SwapWeapon, CameraToggle, Start, Back }

/// <summary>
/// One physical input: either a button, or a trigger treated as a button past its threshold.
/// </summary>
public readonly struct PadBinding : IEquatable<PadBinding>
{
    public readonly bool IsAxis;
    public readonly JoyButton Button;
    public readonly JoyAxis Axis;

    public PadBinding(JoyButton b) { IsAxis = false; Button = b; Axis = JoyAxis.Invalid; }
    public PadBinding(JoyAxis a) { IsAxis = true; Axis = a; Button = JoyButton.Invalid; }

    public bool Equals(PadBinding o)
        => IsAxis == o.IsAxis && Button == o.Button && Axis == o.Axis;

    public override bool Equals(object? o) => o is PadBinding b && Equals(b);
    public override int GetHashCode() => HashCode.Combine(IsAxis, (int)Button, (int)Axis);

    public string Serialise() => IsAxis ? $"axis:{(int)Axis}" : $"btn:{(int)Button}";

    public static bool TryParse(string s, out PadBinding b)
    {
        b = default;
        if (string.IsNullOrWhiteSpace(s)) return false;

        var parts = s.Split(':');
        if (parts.Length != 2 || !int.TryParse(parts[1], out int v)) return false;

        if (parts[0] == "axis") { b = new PadBinding((JoyAxis)v); return true; }
        if (parts[0] == "btn") { b = new PadBinding((JoyButton)v); return true; }
        return false;
    }

    /// <summary>Human name for the physical control, in the vocabulary of the pad in hand.</summary>
    public string Label(PadKind kind)
    {
        if (IsAxis)
            return Axis switch
            {
                JoyAxis.TriggerLeft => kind == PadKind.PlayStation ? "L2" : "LT",
                JoyAxis.TriggerRight => kind == PadKind.PlayStation ? "R2" : "RT",
                _ => Axis.ToString(),
            };

        return kind switch
        {
            PadKind.PlayStation => Button switch
            {
                JoyButton.A => "Cross",
                JoyButton.B => "Circle",
                JoyButton.X => "Square",
                JoyButton.Y => "Triangle",
                JoyButton.LeftShoulder => "L1",
                JoyButton.RightShoulder => "R1",
                JoyButton.Start => "Options",
                JoyButton.Back => "Share",
                JoyButton.LeftStick => "L3",
                JoyButton.RightStick => "R3",
                _ => Button.ToString(),
            },

            // Nintendo's east button is labelled A and its south button B — the opposite of Xbox.
            // Godot reports positions, so only the label changes.
            PadKind.Nintendo => Button switch
            {
                JoyButton.A => "B",
                JoyButton.B => "A",
                JoyButton.X => "Y",
                JoyButton.Y => "X",
                JoyButton.LeftShoulder => "L",
                JoyButton.RightShoulder => "R",
                JoyButton.Start => "+",
                JoyButton.Back => "-",
                _ => Button.ToString(),
            },

            _ => Button switch
            {
                JoyButton.A => "A",
                JoyButton.B => "B",
                JoyButton.X => "X",
                JoyButton.Y => "Y",
                JoyButton.LeftShoulder => "LB",
                JoyButton.RightShoulder => "RB",
                JoyButton.Start => "Start",
                JoyButton.Back => "Back",
                JoyButton.LeftStick => "LS",
                JoyButton.RightStick => "RS",
                _ => Button.ToString(),
            },
        };
    }
}

/// <summary>
/// Gamepad button mapping, remappable by the player and persisted alongside the other settings.
///
/// Sticks are deliberately fixed: left moves, right looks. Remapping those would mean handling
/// axis inversion, sensitivity and swapped pairs, and no couch player has ever needed it badly
/// enough to justify the surface area. Buttons are where the real variation is.
///
/// Two invariants hold at all times, both of which exist so a player can never strand themselves
/// in a menu they cannot leave:
///   * every action keeps at least one binding — clearing the last one restores its default
///   * an input bound to a new action is removed from whatever else held it, so one press never
///     silently means two things
/// </summary>
public static class PadBindings
{
    public static readonly PadAction[] Actions =
    {
        PadAction.Attack, PadAction.Ads, PadAction.Jump, PadAction.Sprint, PadAction.Crouch,
        PadAction.Special, PadAction.ClassAbility, PadAction.Dash, PadAction.Melee,
        PadAction.Use, PadAction.SwapWeapon, PadAction.CameraToggle,
        PadAction.Start, PadAction.Back,
    };

    public static string DisplayName(PadAction a) => a switch
    {
        PadAction.Attack => "Attack",
        PadAction.Ads => "Aim down sights",
        PadAction.Jump => "Jump",
        PadAction.Sprint => "Sprint",
        PadAction.Crouch => "Crouch / Slide",
        PadAction.Special => "Faction special",
        PadAction.ClassAbility => "Class ability",
        PadAction.Dash => "Dash",
        PadAction.Melee => "Melee",
        PadAction.Use => "Interact / enter vehicle",
        PadAction.SwapWeapon => "Swap weapon",
        PadAction.CameraToggle => "First / third person",
        PadAction.Start => "Start / Pause",
        _ => "Back / Cancel",
    };

    /// <summary>
    /// Bumped whenever the action set changes. A saved layout from an older version is discarded
    /// rather than merged: the old defaults put Attack on A, which is Jump now, so keeping them
    /// would leave one button doing two jobs.
    /// </summary>
    const int Version = 9;

    // A conventional first-person layout. Triggers fire and aim; the face buttons and stick clicks
    // carry movement, which is where a shooter player's thumbs expect them.
    //
    // Melee is on the right stick click and Swap weapon is on Y, which is the layout Halo, Call of
    // Duty and Apex all converged on. Melee briefly had Y and swap the stick click, on the theory
    // that swapping is the rarer action; that was the wrong way round. You swap once or twice a
    // fight and you melee whenever someone closes, so the input you can hit without taking your
    // thumb off the aim stick has to be the melee.
    static readonly Dictionary<PadAction, List<PadBinding>> defaults = new()
    {
        [PadAction.Attack] = new() { new(JoyAxis.TriggerRight) },
        [PadAction.Ads] = new() { new(JoyAxis.TriggerLeft) },
        [PadAction.Jump] = new() { new(JoyButton.A) },
        [PadAction.Sprint] = new() { new(JoyButton.LeftStick) },
        [PadAction.Crouch] = new() { new(JoyButton.B) },
        [PadAction.Special] = new() { new(JoyButton.LeftShoulder) },

        // The one thing on the d-pad, and the only seat left for it. Every input a thumb can reach
        // without leaving a stick was already spoken for — both triggers, both bumpers, all four
        // face buttons, both stick clicks — so a second ability had nowhere else to go.
        //
        // Bearable because of what these four are: a lobbed grenade, a stand-and-deliver accuracy
        // buff, a speed burst, a panic shove. Three of them are things you press when you have a
        // beat. It is the first binding anyone should move if it does not suit them, and it is
        // rebindable like everything else.
        [PadAction.ClassAbility] = new() { new(JoyButton.DpadUp) },

        [PadAction.Dash] = new() { new(JoyButton.RightShoulder) },
        [PadAction.Melee] = new() { new(JoyButton.RightStick) },
        [PadAction.Use] = new() { new(JoyButton.X) },
        [PadAction.SwapWeapon] = new() { new(JoyButton.Y) },

        // The other half of the d-pad. ClassAbility already owns up, and nothing in a match reads
        // down - menu nav is the only other consumer and no menu is open while you are playing.
        [PadAction.CameraToggle] = new() { new(JoyButton.DpadDown) },
        [PadAction.Start] = new() { new(JoyButton.Start), new(JoyButton.Guide) },
        [PadAction.Back] = new() { new(JoyButton.Back) },
    };

    static readonly Dictionary<PadAction, List<PadBinding>> current = new();

    static PadBindings() => ResetAll();

    public static IReadOnlyList<PadBinding> For(PadAction a) => current[a];

    public static IReadOnlyList<PadBinding> DefaultsFor(PadAction a) => defaults[a];

    public static bool IsDefault(PadAction a)
    {
        var c = current[a];
        var d = defaults[a];
        if (c.Count != d.Count) return false;
        for (int i = 0; i < c.Count; i++) if (!c[i].Equals(d[i])) return false;
        return true;
    }

    public static bool AllDefault()
    {
        foreach (var a in Actions) if (!IsDefault(a)) return false;
        return true;
    }

    public static void ResetAll()
    {
        current.Clear();
        foreach (var kv in defaults) current[kv.Key] = new List<PadBinding>(kv.Value);
    }

    /// <summary>
    /// Makes <paramref name="b"/> the only binding for <paramref name="a"/>, taking it away from
    /// any other action that held it.
    /// </summary>
    public static void Rebind(PadAction a, PadBinding b)
    {
        foreach (var other in Actions)
        {
            if (other == a) continue;

            current[other].RemoveAll(x => x.Equals(b));

            // Never leave an action unreachable. Restoring the default is a blunt fix, but the
            // alternative is a pad that cannot pause or back out of a menu.
            if (current[other].Count == 0)
                current[other] = new List<PadBinding>(defaults[other]);
        }

        current[a] = new List<PadBinding> { b };
    }

    public static string Describe(PadAction a, PadKind kind)
    {
        var list = current[a];
        var parts = new string[list.Count];
        for (int i = 0; i < list.Count; i++) parts[i] = list[i].Label(kind);
        return string.Join(" / ", parts);
    }

    // ---- persistence ----

    public static void WriteTo(ConfigFile cfg)
    {
        cfg.SetValue("bindings", "version", Version);

        foreach (var a in Actions)
        {
            var list = current[a];
            var parts = new string[list.Count];
            for (int i = 0; i < list.Count; i++) parts[i] = list[i].Serialise();
            cfg.SetValue("bindings", a.ToString(), string.Join(",", parts));
        }
    }

    public static void ReadFrom(ConfigFile cfg)
    {
        // A layout saved before the action set changed is not partially usable — discard it and
        // start from the current defaults.
        if (cfg.GetValue("bindings", "version", 0).AsInt32() != Version)
        {
            ResetAll();
            return;
        }

        foreach (var a in Actions)
        {
            string raw = cfg.GetValue("bindings", a.ToString(), "").AsString();
            if (string.IsNullOrWhiteSpace(raw)) continue;

            var list = new List<PadBinding>();
            foreach (var token in raw.Split(','))
                if (PadBinding.TryParse(token, out var b)) list.Add(b);

            // A malformed or empty entry falls back to the default rather than leaving the action
            // with nothing bound.
            current[a] = list.Count > 0 ? list : new List<PadBinding>(defaults[a]);
        }
    }
}
