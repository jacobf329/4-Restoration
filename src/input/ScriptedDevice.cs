using Godot;

namespace HitboxClone;

/// <summary>
/// A device driven from code rather than hardware. This is what lets the headless test harness
/// assert the project's central premise — that every screen is reachable and exitable with nav,
/// attack and back alone — without a real pad plugged in.
///
/// It goes through the same <see cref="InputDevice"/> base as real hardware, so the edge and
/// key-repeat behaviour under test is the production code path, not a stand-in.
/// </summary>
public sealed class ScriptedDevice : InputDevice
{
    readonly string id;

    public ScriptedDevice(string id = "scripted") { this.id = id; }

    public override string Id => id;
    public override string Label => "Scripted";
    public override bool Connected => true;
    public override bool IsGamepad => true;
    public override PadKind Kind => PadKind.Generic;

    // Set these, then call Poll(dt). They persist until changed, exactly like a held button.
    public Vector2 NextMove, NextAim, NextLook;
    public bool HoldAttack, HoldSpecial, HoldDash, HoldStart, HoldBack;
    public bool HoldAds, HoldJump, HoldSprint, HoldCrouch;
    public bool HoldUse, HoldSwap, HoldMelee, HoldConfirm, HoldClassAbility;
    public int HoldNavX, HoldNavY;

    protected override bool TrySample(float dt, out Sample s)
    {
        s = new Sample
        {
            Move = NextMove,
            Aim = NextAim,
            Look = NextLook,
            Attack = HoldAttack,
            Special = HoldSpecial,
            Dash = HoldDash,
            Start = HoldStart,
            Back = HoldBack,
            Ads = HoldAds,
            Jump = HoldJump,
            Sprint = HoldSprint,
            Crouch = HoldCrouch,
            Use = HoldUse,
            Swap = HoldSwap,
            Melee = HoldMelee,
            ClassAbility = HoldClassAbility,
            Confirm = HoldConfirm,
            Cancel = HoldBack,
            RawNavX = HoldNavX,
            RawNavY = HoldNavY,
        };
        return true;
    }

    /// <summary>Clears every held input, so the following poll produces no press edges.</summary>
    public void Release()
    {
        HoldAttack = HoldSpecial = HoldDash = HoldStart = HoldBack = false;
        HoldAds = HoldJump = HoldSprint = HoldCrouch = false;
        HoldUse = HoldSwap = HoldMelee = HoldConfirm = HoldClassAbility = false;
        HoldNavX = HoldNavY = 0;
        NextMove = NextAim = NextLook = Vector2.Zero;
    }
}
