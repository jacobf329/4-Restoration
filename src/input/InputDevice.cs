using System;
using Godot;

namespace HitboxClone;

/// <summary>Which button glyphs to draw for a device, so prompts match the pad in the player's hands.</summary>
public enum PadKind { Keyboard, Xbox, PlayStation, Nintendo, Generic }

/// <summary>
/// One controllable input source. Concrete devices report raw state once per frame in
/// <see cref="TrySample"/>; this base class derives the press edges and menu-nav repeat, so every
/// device has identical timing semantics and gameplay code never learns whether it is driving a
/// keyboard or a gamepad.
///
/// Ported from <c>H:\BattleArena\Input.cs</c>. The one structural change: Godot's Input only
/// reports held state, never "just pressed", so edge detection moved out of the concrete devices
/// and into here. That removes the duplicated prev-state bookkeeping the Raylib version needed.
/// </summary>
public abstract class InputDevice
{
    public abstract string Id { get; }
    public abstract string Label { get; }
    public abstract bool Connected { get; }
    public abstract bool IsGamepad { get; }
    public abstract PadKind Kind { get; }

    // ---- Per-frame state. Everything downstream reads only these. ----

    /// <summary>Movement intent, magnitude 0..1.</summary>
    public Vector2 Move;

    /// <summary>Unit aim vector, or Zero when the device gives no explicit aim this frame.</summary>
    public Vector2 Aim;

    /// <summary>
    /// Raw look axis, magnitude preserved rather than normalised: X turns, Y pitches. First-person
    /// aiming needs partial stick deflection to mean a slower turn, which a unit vector throws away.
    /// </summary>
    public Vector2 Look;

    public bool AttackHeld, AttackPressed;
    public bool SpecialHeld, SpecialPressed;
    public bool DashPressed;
    public bool StartPressed;
    public bool BackPressed;

    /// <summary>
    /// Menu confirm, which is a different input from Attack.
    ///
    /// It used to be the same one, and every hint bar in the game said "A" while the code was
    /// reading the right trigger — the prompt was simply wrong. On a pad this is the Jump binding,
    /// so it follows a rebind; on a keyboard it stays on the attack key, which is Space, which is
    /// also what the prompts have always claimed.
    /// </summary>
    public bool ConfirmPressed;

    /// <summary>
    /// Menu back, which is a different input from the pad's Back button.
    ///
    /// On a pad this is B — the Crouch binding — as well as Back itself, because B is where every
    /// console player's thumb goes to leave a screen and the small Back button is not. Deriving it
    /// from Crouch rather than naming a button also means the prompt comes out right on every pad
    /// without a second table: Circle on PlayStation, and A on a Nintendo pad, whose east button
    /// is labelled the opposite way round.
    ///
    /// Crouching in a match cannot back out of anything — the match screen absorbs back entirely.
    /// </summary>
    public bool CancelPressed;

    /// <summary>Melee swing. Always available, whatever is in your hands.</summary>
    public bool MeleePressed;

    /// <summary>The class ability, on its own button and its own cooldown.</summary>
    public bool ClassAbilityPressed;

    /// <summary>Aim down sights. Held, not toggled.</summary>
    public bool AdsHeld;

    public bool JumpPressed;

    /// <summary>Jump held, as distinct from pressed. Ground jumps use the edge; the jetpack burns
    /// while it is held down.</summary>
    public bool JumpHeld;

    /// <summary>Sprint is held; crouch is reported both ways, since sliding keys off the press.</summary>
    public bool SprintHeld;
    public bool CrouchHeld, CrouchPressed;

    /// <summary>Interact: a press boards a vehicle, a hold takes a weapon off the floor.</summary>
    public bool UsePressed, UseHeld;

    /// <summary>Switch to the other weapon slot.</summary>
    public bool SwapPressed;

    /// <summary>
    /// Flip between first and third person.
    ///
    /// Deliberately NOT latched, unlike the gameplay edges. The camera mode is owned by the screen
    /// and flipped in the render frame, on the same clock this edge is raised on - exactly like
    /// pause. Latching it would mean the flag stayed up across several rendered frames until a
    /// physics step cleared it, and the screen would toggle once per frame for the whole of that:
    /// one press, three or four flips, landing wherever the arithmetic left it.
    /// </summary>
    public bool CameraTogglePressed;

    /// <summary>Menu navigation, already rate-limited. Non-zero only on the frames a step should fire.</summary>
    public int NavX, NavY;

    // ---- Gameplay edges, latched until the simulation consumes them ----

    /// <summary>
    /// The press edges above, held until <see cref="ConsumeGameplayEdges"/> clears them.
    ///
    /// Polling happens in <c>_Process</c>, once per rendered frame. The match runs in
    /// <c>_PhysicsProcess</c>, at a fixed sixty steps a second. Those are two different clocks,
    /// and the plain edges above live for exactly one poll - so on any machine that does not
    /// render at exactly sixty frames a second, a press and the step that should act on it are
    /// only sometimes the same moment.
    ///
    /// On a 144Hz display there are 2.4 rendered frames per physics step. An edge raised on one
    /// of them is gone by the next, and a step lands inside that window less than half the time:
    /// most presses were being dropped before the game ever saw them. Below sixty the fault
    /// inverts - two steps read one press and a vehicle was boarded and left again on the same
    /// tap, which looks identical from the player's side.
    ///
    /// That is why boarding failed about half the time and why holding the button longer seemed
    /// to help. It was never the vehicle code: every earlier fix went looking at the hull, and the
    /// press had already been lost upstream.
    ///
    /// Latching makes the contract "one press, one action" independent of frame rate. A poll can
    /// only ever raise a latch; only the simulation lowers it, and it lowers it having acted.
    /// </summary>
    public bool UseLatched, JumpLatched, DashLatched, MeleeLatched;
    public bool ClassAbilityLatched, SpecialLatched, CrouchLatched, SwapLatched;

    /// <summary>
    /// Clears the latched edges. Called by the simulation once per physics step, after it has had
    /// its look at them - and on the steps it declines to run, so a press made during a pause is
    /// dropped rather than banked and spent the instant play resumes.
    /// </summary>
    public void ConsumeGameplayEdges()
    {
        UseLatched = JumpLatched = DashLatched = MeleeLatched = false;
        ClassAbilityLatched = SpecialLatched = CrouchLatched = SwapLatched = false;
    }

    /// <summary>Any button a player would naturally mash to claim a slot.</summary>
    public bool JoinPressed => ConfirmPressed || AttackPressed || StartPressed;

    /// <summary>True on any frame the device shows deliberate activity — used to spot a live pad.</summary>
    public bool AnyActivity => AttackPressed || ConfirmPressed || SpecialPressed || DashPressed
                               || StartPressed || BackPressed || CancelPressed
                               || NavX != 0 || NavY != 0 || Move.LengthSquared() > 0.2f;

    /// <summary>Raw per-frame reading from a concrete device, before edges and repeat are applied.</summary>
    protected struct Sample
    {
        public Vector2 Move, Aim, Look;
        public bool Attack, Special, Dash, Start, Back;
        public bool Ads, Jump, Sprint, Crouch, Use, Swap, Melee, ClassAbility;

        /// <summary>Toggle between the first- and third-person camera.</summary>
        public bool CameraToggle;

        /// <summary>Menu back. Separate from the pad's Back button — see <see cref="CancelPressed"/>.</summary>
        public bool Cancel;

        /// <summary>Menu confirm. Separate from Attack — see <see cref="ConfirmPressed"/>.</summary>
        public bool Confirm;
        public int RawNavX, RawNavY;
    }

    /// <summary>Fill <paramref name="s"/> with this frame's raw state. Return false if unavailable.</summary>
    protected abstract bool TrySample(float dt, out Sample s);

    bool pAttack, pSpecial, pDash, pStart, pBack, pJump, pCrouch, pUse, pSwap, pMelee, pConfirm, pClass, pCancel;
    bool pCamera;
    float repeatTimer;
    int lastNavX, lastNavY;

    public void Poll(float dt)
    {
        if (!TrySample(dt, out Sample s))
        {
            Clear();
            return;
        }

        Move = s.Move;
        Aim = s.Aim;
        Look = s.Look;

        AttackHeld = s.Attack;
        SpecialHeld = s.Special;
        AttackPressed = s.Attack && !pAttack;
        SpecialPressed = s.Special && !pSpecial;
        DashPressed = s.Dash && !pDash;
        StartPressed = s.Start && !pStart;
        BackPressed = s.Back && !pBack;

        AdsHeld = s.Ads;
        SprintHeld = s.Sprint;
        CrouchHeld = s.Crouch;
        JumpHeld = s.Jump;
        JumpPressed = s.Jump && !pJump;
        CrouchPressed = s.Crouch && !pCrouch;
        UsePressed = s.Use && !pUse;
        UseHeld = s.Use;
        SwapPressed = s.Swap && !pSwap;
        CameraTogglePressed = s.CameraToggle && !pCamera;
        MeleePressed = s.Melee && !pMelee;
        ClassAbilityPressed = s.ClassAbility && !pClass;
        ConfirmPressed = s.Confirm && !pConfirm;
        CancelPressed = s.Cancel && !pCancel;

        // Raised here, lowered only by the simulation. See the latched fields.
        UseLatched |= UsePressed;
        JumpLatched |= JumpPressed;
        DashLatched |= DashPressed;
        MeleeLatched |= MeleePressed;
        ClassAbilityLatched |= ClassAbilityPressed;
        SpecialLatched |= SpecialPressed;
        CrouchLatched |= CrouchPressed;
        SwapLatched |= SwapPressed;

        pAttack = s.Attack; pSpecial = s.Special; pDash = s.Dash;
        pStart = s.Start; pBack = s.Back;
        pJump = s.Jump; pCrouch = s.Crouch; pUse = s.Use; pSwap = s.Swap;
        pCamera = s.CameraToggle;
        pMelee = s.Melee; pConfirm = s.Confirm; pClass = s.ClassAbility; pCancel = s.Cancel;

        UpdateNav(s.RawNavX, s.RawNavY, dt);
    }

    /// <summary>
    /// Zeroes all state. Called when a device drops, so a pad that disconnects mid-hold does not
    /// leave a button latched down, and so its edges re-fire cleanly when it comes back.
    /// </summary>
    public void Clear()
    {
        Move = Aim = Look = Vector2.Zero;
        AttackHeld = SpecialHeld = false;
        AttackPressed = SpecialPressed = DashPressed = StartPressed = BackPressed = false;
        AdsHeld = SprintHeld = CrouchHeld = false;
        JumpPressed = CrouchPressed = false;
        JumpHeld = false;
        pAttack = pSpecial = pDash = pStart = pBack = pJump = pCrouch = pUse = pSwap = false;
        pCamera = false;
        pMelee = pConfirm = pClass = pCancel = false;
        UsePressed = UseHeld = SwapPressed = MeleePressed = ConfirmPressed = false;
        CameraTogglePressed = false;
        ClassAbilityPressed = CancelPressed = false;
        ConsumeGameplayEdges();
        NavX = NavY = 0;
        lastNavX = lastNavY = 0;
        repeatTimer = 0f;
    }

    /// <summary>
    /// Menu nav with key-repeat: a direction change fires immediately then waits
    /// <c>FirstDelay</c>; holding repeats every <c>RepeatDelay</c>. Same timings as BattleArena,
    /// which are tuned so a stick held to scroll a long list feels neither sticky nor runaway.
    /// </summary>
    void UpdateNav(int rawX, int rawY, float dt)
    {
        const float FirstDelay = 0.40f, RepeatDelay = 0.13f;

        if (rawX == 0 && rawY == 0)
        {
            repeatTimer = 0f;
            lastNavX = lastNavY = 0;
            NavX = NavY = 0;
            return;
        }

        if (rawX != lastNavX || rawY != lastNavY)
        {
            NavX = rawX; NavY = rawY;
            lastNavX = rawX; lastNavY = rawY;
            repeatTimer = FirstDelay;
            return;
        }

        repeatTimer -= dt;
        if (repeatTimer <= 0f)
        {
            NavX = rawX; NavY = rawY;
            repeatTimer = RepeatDelay;
        }
        else
        {
            NavX = NavY = 0;
        }
    }

    /// <summary>Rumble, where the device supports it. No-op otherwise.</summary>
    public virtual void Rumble(float weak, float strong, float seconds) { }
}
