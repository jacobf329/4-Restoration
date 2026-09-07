using System;
using Godot;

namespace HitboxClone;

/// <summary>
/// One of two keyboard schemes, so two players can share a keyboard in couch play.
///
/// Physical key codes are used throughout, so WASD stays under the same fingers on AZERTY
/// and other non-QWERTY layouts.
///
/// The bindings are declared as data rather than inline conditions so the self-test can assert
/// the two schemes share no key. They used to overlap on Shift, which meant one keypress drove
/// two players at once.
/// </summary>
public sealed class KeyboardDevice : InputDevice
{
    /// <summary>
    /// Every key one scheme owns. Two schemes must never intersect.
    ///
    /// Turn is separate from strafe because the game is first-person: without a dedicated turn
    /// axis a keyboard player could only ever face the way they were walking, which is unusable
    /// when you need to strafe around cover while keeping your aim on someone.
    /// </summary>
    public sealed class Bindings
    {
        public Key[] Forward = Array.Empty<Key>();
        public Key[] Backward = Array.Empty<Key>();
        public Key[] StrafeLeft = Array.Empty<Key>();
        public Key[] StrafeRight = Array.Empty<Key>();
        public Key[] TurnLeft = Array.Empty<Key>();
        public Key[] TurnRight = Array.Empty<Key>();
        public Key[] NavLeft = Array.Empty<Key>();
        public Key[] NavRight = Array.Empty<Key>();
        public Key[] Attack = Array.Empty<Key>();
        public Key[] Special = Array.Empty<Key>();
        public Key[] Dash = Array.Empty<Key>();
        public Key[] Start = Array.Empty<Key>();
        public Key[] Cancel = Array.Empty<Key>();
        public Key[] Ads = Array.Empty<Key>();
        public Key[] Jump = Array.Empty<Key>();
        public Key[] Sprint = Array.Empty<Key>();
        public Key[] Crouch = Array.Empty<Key>();

        /// <summary>
        /// Interact, swap and melee. These had no keys at all: a keyboard player could not board a
        /// vehicle, take a weapon off the floor, or change slot — three mechanics that simply did
        /// not exist for them.
        /// </summary>
        public Key[] Use = Array.Empty<Key>();
        public Key[] Swap = Array.Empty<Key>();
        public Key[] Melee = Array.Empty<Key>();
        public Key[] ClassAbility = Array.Empty<Key>();

        public Key[][] All => new[]
        {
            Forward, Backward, StrafeLeft, StrafeRight, TurnLeft, TurnRight,
            NavLeft, NavRight, Attack, Special, Dash, Start, Cancel,
            Ads, Jump, Sprint, Crouch, Use, Swap, Melee, ClassAbility,
        };
    }

    public static readonly Bindings[] Schemes =
    {
        // Scheme 0 — WASD to move, Q/E to turn.
        new()
        {
            Forward = new[] { Key.W },
            Backward = new[] { Key.S },
            StrafeLeft = new[] { Key.A },
            StrafeRight = new[] { Key.D },
            TurnLeft = new[] { Key.Q },
            TurnRight = new[] { Key.E },
            NavLeft = new[] { Key.A },
            NavRight = new[] { Key.D },
            Attack = new[] { Key.Space },
            Special = new[] { Key.F },
            Dash = new[] { Key.X },
            Start = new[] { Key.Enter },
            Cancel = new[] { Key.Backspace },
            Ads = new[] { Key.R },
            Jump = new[] { Key.V },
            Sprint = new[] { Key.Shift },
            Crouch = new[] { Key.C },
            Use = new[] { Key.G },
            Swap = new[] { Key.Z },
            Melee = new[] { Key.B },
            ClassAbility = new[] { Key.T },
        },

        // Scheme 1 — arrows and numpad, right hand. Arrows turn, numpad 4/6 strafe.
        //
        // Deliberately free of Ctrl/Shift/Alt. Those collided with scheme 0, and they are also
        // exactly what input remappers such as Steam Input emit when a pad is running a desktop
        // layout — which would let a gamepad drive this keyboard player.
        new()
        {
            Forward = new[] { Key.Up },
            Backward = new[] { Key.Down },
            StrafeLeft = new[] { Key.Kp4 },
            StrafeRight = new[] { Key.Kp6 },
            TurnLeft = new[] { Key.Left },
            TurnRight = new[] { Key.Right },
            NavLeft = new[] { Key.Left },
            NavRight = new[] { Key.Right },
            Attack = new[] { Key.Kp0 },
            Special = new[] { Key.Kp1 },
            Dash = new[] { Key.Kp2 },
            Start = new[] { Key.KpEnter },
            Cancel = new[] { Key.KpPeriod },
            Ads = new[] { Key.Kp5 },
            Jump = new[] { Key.Kp3 },
            Sprint = new[] { Key.Kp7 },
            Crouch = new[] { Key.Kp9 },
            Use = new[] { Key.Kp8 },
            Swap = new[] { Key.KpMultiply },
            Melee = new[] { Key.KpSubtract },
            ClassAbility = new[] { Key.KpDivide },
        },
    };

    public readonly int Scheme;

    float mouseIdle = 99f;
    Vector2 lastMousePos;
    bool haveMousePos;

    public KeyboardDevice(int scheme) { Scheme = scheme; }

    public override string Id => "kb" + Scheme;
    public override string Label => Scheme == 0 ? "Keyboard (WASD)" : "Keyboard (Arrows)";
    public override bool Connected => true;
    public override bool IsGamepad => false;
    public override PadKind Kind => PadKind.Keyboard;

    Bindings B => Schemes[Scheme];

    static bool Down(Key[] keys)
    {
        foreach (var k in keys) if (Input.IsPhysicalKeyPressed(k)) return true;
        return false;
    }

    static int Axis(Key[] negative, Key[] positive)
    {
        int v = 0;
        if (Down(negative)) v--;
        if (Down(positive)) v++;
        return v;
    }

    protected override bool TrySample(float dt, out Sample s)
    {
        s = default;
        var b = B;

        int mx = Axis(b.StrafeLeft, b.StrafeRight);
        int my = Axis(b.Forward, b.Backward) * -1;   // forward is -Y, matching stick convention

        s.Attack = Down(b.Attack);
        s.Special = Down(b.Special);
        s.Dash = Down(b.Dash);
        s.Start = Down(b.Start);
        s.Back = Down(b.Cancel);
        s.Ads = Down(b.Ads);
        s.Jump = Down(b.Jump);
        s.Sprint = Down(b.Sprint);
        s.Crouch = Down(b.Crouch);
        s.Use = Down(b.Use);
        s.Swap = Down(b.Swap);
        s.Melee = Down(b.Melee);
        s.ClassAbility = Down(b.ClassAbility);

        // Space confirms menus, which is both what the prompts say and what the attack key is —
        // a keyboard has no reason to move confirm onto the jump key the way a pad does.
        s.Confirm = Down(b.Attack);
        s.Cancel = s.Back;

        // Only scheme 0 has anything to do with the mouse, and only when the player has asked for
        // it. The "has the cursor moved recently" heuristic is not enough on its own: a pad that
        // drives the cursor as well as clicking makes the mouse look genuinely live, so the switch
        // has to be explicit rather than inferred.
        if (Scheme == 0 && UserSettings.KeyboardAndMouse)
        {
            UpdateMouseIdle(dt);

            if (MouseInUse)
            {
                if (Input.IsMouseButtonPressed(MouseButton.Left)) s.Attack = true;
                if (Input.IsMouseButtonPressed(MouseButton.Right)) s.Special = true;
            }
        }

        s.Move = MathU.ClampLen(new Vector2(mx, my), 1f);

        // Turn is digital, so it reads as full deflection on the look axis. There is no pitch key:
        // a keyboard player aims level, which the generous vertical hitboxes make workable.
        s.Look = new Vector2(Axis(b.TurnLeft, b.TurnRight), 0f);

        s.Aim = Vector2.Zero;

        // Menu navigation uses its own keys, because scheme 1's left/right turn the player in game
        // but must still move the cursor sideways in menus.
        s.RawNavX = Axis(b.NavLeft, b.NavRight);
        s.RawNavY = my;
        return true;
    }

    /// <summary>
    /// Tracks cursor motion only. Clicks deliberately do not count as activity — if they did, a
    /// synthetic click would keep the mouse looking "in use" and defeat the filter above.
    /// </summary>
    void UpdateMouseIdle(float dt)
    {
        Vector2 pos = DisplayServer.MouseGetPosition();
        if (!haveMousePos) { lastMousePos = pos; haveMousePos = true; }

        bool moved = (pos - lastMousePos).LengthSquared() > 1f;
        lastMousePos = pos;

        if (moved) mouseIdle = 0f;
        else mouseIdle += dt;
    }

    /// <summary>True while the cursor has moved recently enough to treat the mouse as live.</summary>
    public bool MouseInUse => Scheme == 0 && UserSettings.KeyboardAndMouse && mouseIdle < 2.5f;

    /// <summary>
    /// True while the mouse should drive this player's aim. Goes false a couple of seconds after
    /// the mouse stops moving, so a player who switches to keyboard-only aiming isn't stuck
    /// pointing at wherever the cursor was abandoned.
    /// </summary>
    public bool UseMouseAim => MouseInUse;
}
