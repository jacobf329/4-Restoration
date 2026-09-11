using System;
using Godot;

namespace HitboxClone;

/// <summary>
/// A gamepad, addressed by Godot's joypad index.
///
/// Ported from <c>H:\BattleArena\Input.cs</c>, keeping the two behaviours that make pads work
/// without configuration: per-pad trigger rest calibration, and the d-pad overriding the stick so
/// digital-only pads are fully playable.
/// </summary>
public sealed class GamepadDevice : InputDevice
{
    public readonly int Index;

    // Triggers report either [-1..1] or [0..1] depending on driver; learn the resting value
    // over the first fraction of a second after the pad appears.
    float restLT = float.NaN, restRT = float.NaN;
    bool calibrated;
    float calibTime;

    public GamepadDevice(int index) { Index = index; }

    public override string Id => "pad" + Index;
    public override bool IsGamepad => true;
    public override bool Connected => Devices.IsPadConnected(Index);

    public override string Label
    {
        get
        {
            if (!Connected) return $"Gamepad {Index + 1} (disconnected)";
            string name = RawName();
            if (name.Length > 22) name = name.Substring(0, 22).TrimEnd() + "...";
            return $"P{Index + 1}: {name}";
        }
    }

    string RawName()
    {
        string n = Input.GetJoyName(Index);
        return string.IsNullOrWhiteSpace(n) ? "Gamepad" : n;
    }

    public override PadKind Kind
    {
        get
        {
            if (!Connected) return PadKind.Generic;
            string n = RawName().ToLowerInvariant();
            if (n.Contains("xbox") || n.Contains("xinput") || n.Contains("x-box")) return PadKind.Xbox;
            if (n.Contains("playstation") || n.Contains("dualshock") || n.Contains("dualsense")
                || n.Contains("sony") || n.Contains("ps3") || n.Contains("ps4") || n.Contains("ps5"))
                return PadKind.PlayStation;
            if (n.Contains("nintendo") || n.Contains("switch") || n.Contains("joy-con")
                || n.Contains("joycon") || n.Contains("pro controller"))
                return PadKind.Nintendo;
            return PadKind.Generic;
        }
    }

    bool Btn(JoyButton b) => Input.IsJoyButtonPressed(Index, b);
    float Axis(JoyAxis a) => Input.GetJoyAxis(Index, a);

    /// <summary>Normalised 0..1 trigger value that works with both axis conventions.</summary>
    float Trigger(JoyAxis axis, float rest)
    {
        float raw = Axis(axis);
        if (float.IsNaN(rest)) return 0f;
        float span = 1f - rest;
        if (MathF.Abs(span) < 0.05f) return 0f;
        return MathU.Clamp01((raw - rest) / span);
    }

    protected override bool TrySample(float dt, out Sample s)
    {
        s = default;

        if (!Connected)
        {
            // Force a fresh calibration when this pad comes back.
            calibrated = false;
            calibTime = 0f;
            restLT = restRT = float.NaN;
            return false;
        }

        if (!calibrated)
        {
            calibTime += dt;
            restLT = Axis(JoyAxis.TriggerLeft);
            restRT = Axis(JoyAxis.TriggerRight);
            if (calibTime > 0.25f) calibrated = true;
        }

        Vector2 stick = new(Axis(JoyAxis.LeftX), Axis(JoyAxis.LeftY));
        Vector2 move = MathU.Deadzone(stick, 0.24f);

        // Read for menu navigation further down, but no longer for movement. The d-pad used to
        // override the stick so digital-only pads worked; the cost was that brushing it mid-fight
        // snapped you to eight directions at full speed, and every pad this game targets has two
        // analogue sticks anyway.
        int dx = 0, dy = 0;
        if (Btn(JoyButton.DpadLeft)) dx--;
        if (Btn(JoyButton.DpadRight)) dx++;
        if (Btn(JoyButton.DpadUp)) dy--;
        if (Btn(JoyButton.DpadDown)) dy++;

        s.Move = MathU.ClampLen(move, 1f);

        Vector2 rstick = new(Axis(JoyAxis.RightX), Axis(JoyAxis.RightY));
        Vector2 aim = MathU.Deadzone(rstick, 0.24f);
        s.Look = aim;
        s.Aim = aim.LengthSquared() > 0.02f ? MathU.Norm(aim) : Vector2.Zero;

        s.Attack = ActionHeld(PadAction.Attack);
        s.Special = ActionHeld(PadAction.Special);
        s.Dash = ActionHeld(PadAction.Dash);
        s.Start = ActionHeld(PadAction.Start);
        s.Back = ActionHeld(PadAction.Back);
        s.Ads = ActionHeld(PadAction.Ads);
        s.Jump = ActionHeld(PadAction.Jump);
        s.Sprint = ActionHeld(PadAction.Sprint);
        s.Crouch = ActionHeld(PadAction.Crouch);
        s.Use = ActionHeld(PadAction.Use);
        s.Swap = ActionHeld(PadAction.SwapWeapon);
        s.Melee = ActionHeld(PadAction.Melee);
        s.ClassAbility = ActionHeld(PadAction.ClassAbility);
        s.CameraToggle = ActionHeld(PadAction.CameraToggle);

        // Menus confirm on whatever Jump is bound to, which is A out of the box. Every prompt in
        // the game already said A; only the code disagreed.
        s.Confirm = s.Jump;

        // And leave on B, which is the Crouch binding. The pad's own Back button still works, but
        // nobody reaches for it — every console shooter puts cancel on the east face button.
        s.Cancel = s.Back || s.Crouch;

        // Nav uses a higher threshold than movement so menus don't drift on a worn stick.
        int nx = MathF.Abs(stick.X) > 0.55f ? MathF.Sign(stick.X) : 0;
        int ny = MathF.Abs(stick.Y) > 0.55f ? MathF.Sign(stick.Y) : 0;
        if (dx != 0) nx = dx;
        if (dy != 0) ny = dy;
        s.RawNavX = nx;
        s.RawNavY = ny;

        return true;
    }

    /// <summary>Whether any input bound to <paramref name="a"/> is currently active.</summary>
    bool ActionHeld(PadAction a)
    {
        foreach (var b in PadBindings.For(a))
            if (BindingHeld(b)) return true;
        return false;
    }

    bool BindingHeld(PadBinding b)
    {
        if (!b.IsAxis) return Btn(b.Button);

        // Triggers go through the calibrated reading so both axis conventions work; any other
        // axis bound as a button is read raw.
        return b.Axis switch
        {
            JoyAxis.TriggerLeft => Trigger(JoyAxis.TriggerLeft, restLT) > 0.45f,
            JoyAxis.TriggerRight => Trigger(JoyAxis.TriggerRight, restRT) > 0.45f,
            _ => MathF.Abs(Axis(b.Axis)) > 0.6f,
        };
    }

    /// <summary>
    /// Candidate inputs a player could bind. Sticks are excluded deliberately — binding "left
    /// stick up" to Attack would fight with movement.
    /// </summary>
    static readonly PadBinding[] Bindable =
    {
        new(JoyAxis.TriggerLeft), new(JoyAxis.TriggerRight),
        new(JoyButton.A), new(JoyButton.B), new(JoyButton.X), new(JoyButton.Y),
        new(JoyButton.LeftShoulder), new(JoyButton.RightShoulder),
        new(JoyButton.LeftStick), new(JoyButton.RightStick),
        new(JoyButton.Start), new(JoyButton.Back), new(JoyButton.Guide),
        new(JoyButton.DpadUp), new(JoyButton.DpadDown), new(JoyButton.DpadLeft), new(JoyButton.DpadRight),
    };

    /// <summary>True while the player is holding anything bindable — used to wait for release.</summary>
    public bool AnyBindableHeld
    {
        get
        {
            if (!Connected) return false;
            foreach (var b in Bindable) if (BindingHeld(b)) return true;
            return false;
        }
    }

    /// <summary>The first bindable input currently active, for the rebinding screen to capture.</summary>
    public PadBinding? CapturePressed()
    {
        if (!Connected) return null;
        foreach (var b in Bindable) if (BindingHeld(b)) return b;
        return null;
    }

    public override void Rumble(float weak, float strong, float seconds)
    {
        if (Connected) Input.StartJoyVibration(Index, weak, strong, seconds);
    }
}
