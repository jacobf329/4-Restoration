using System;
using System.Collections.Generic;
using Godot;

namespace HitboxClone;

/// <summary>
/// A fighter in the arena, human or bot.
///
/// Movement is twin-stick: the left stick moves in world space and the right stick aims
/// independently, with the body turning to face aim. Dash grants brief invulnerability, so dashing
/// through a shot is the core defensive skill — the same rule BattleArena's three dashes share.
/// </summary>
public partial class Pawn : CharacterBody3D
{
    public const float Radius = 0.55f;
    public const float Height = 1.8f;
    public const float CrouchHeight = 1.05f;
    public const float EyeHeight = 1.25f;
    public const float CrouchEyeHeight = 0.72f;

    /// <summary>Hits above this fraction of the pawn's *current* height count as headshots.</summary>
    public const float HeadFraction = 0.72f;

    public ClassDef Class = Classes.Trooper;
    public int Slot;

    /// <summary>Splitscreen view this pawn owns, or -1 for a bot. Not the same as the slot.</summary>
    public int ViewIndex = -1;

    public bool IsBot;
    public string Name2 = "";
    public Color Tint = Colors.White;

    public float Health;
    public int Score;
    public int Deaths;

    // ---- battle points ----
    //
    // A second currency, deliberately not the score.
    //
    // Score answers "who is winning". Points answer "what may I spawn as", and the two want to
    // behave differently: score is the thing you are competing over and must mean exactly what the
    // mode says it means, while points are a private budget that accumulates from *anything* useful
    // you did. Capturing a command post earns points in Deathmatch too, where it is worth no score
    // at all, because the question points answer is "have you been contributing" rather than "are
    // you ahead".
    //
    // Kept on death, spent on spawn. That is the Battlefront rule and it is the right one: losing
    // your bank because you died would mean the players having the worst time are the ones least
    // able to do anything about it.

    /// <summary>Unspent battle points. Buys a reinforcement at the spawn screen.</summary>
    public int BattlePoints;

    /// <summary>Points earned across the whole match, spent or not. For the end screen.</summary>
    public int PointsEarned;

    /// <summary>Bank points, and remember the total.</summary>
    public void AwardPoints(int n)
    {
        if (n <= 0) return;
        BattlePoints += n;
        PointsEarned += n;
    }

    /// <summary>
    /// Elimination rounds won. Separate from <see cref="Score"/> because that counts frags, and
    /// sharing one field meant a bot with two kills instantly won a two-round match.
    /// </summary>
    public int RoundsWon;

    public bool Alive => Health > 0f;

    /// <summary>Seconds until respawn. Only meaningful while dead.</summary>
    public float RespawnIn;

    /// <summary>Seconds left on this pawn's hit-confirm marker — set when it lands a shot.</summary>
    public float HitConfirm;

    /// <summary>Seconds left on the damage-direction indicator, and where the shot came from.</summary>
    public float DamageFlash;
    public Vector3 LastAttacker;

    /// <summary>Seconds left on this pawn's HEADSHOT banner.</summary>
    public float HeadshotBanner;

    /// <summary>Seconds left on the "you eliminated X" confirmation, and who it was.</summary>
    public float KillBanner;
    public string KillBannerName = "";

    /// <summary>Who killed this pawn, shown on their own death banner. Empty for a fall.</summary>
    public string KilledBy = "";

    public const float HitConfirmTime = 0.22f;
    public const float DamageFlashTime = 1.1f;

    /// <summary>Facing, in radians on the XZ plane. Zero looks down +X.</summary>
    public float Facing;

    /// <summary>Vertical aim in radians, positive looking up. Clamped short of straight up.</summary>
    public float Pitch;

    public const float MaxPitch = 1.25f;

    /// <summary>Full 3D aim direction — what the weapon actually fires along.</summary>
    public Vector3 AimDir
    {
        get
        {
            float cp = MathF.Cos(Pitch);
            return new Vector3(MathF.Cos(Facing) * cp, MathF.Sin(Pitch), MathF.Sin(Facing) * cp);
        }
    }

    float fireCooldown;
    float dashCooldown;
    float dashTime;
    float invuln;

    /// <summary>Ground direction of the dash in flight. See <see cref="StartDash"/>.</summary>
    Vector3 dashDir;

    float meleeCooldown;

    /// <summary>Seconds left on the melee swing, which is what the view model animates against.</summary>
    public float MeleeSwing { get; private set; }

    /// <summary>Bumped on every swing, so a viewer can spot one without polling a timer.</summary>
    public int MeleeCounter { get; private set; }

    MeshInstance3D? body;
    MeshInstance3D? snout;

    /// <summary>
    /// The team colour to wash a faction model in, or null outside a team mode. Set before
    /// <see cref="Setup"/>, because that is what builds the rig the wash goes onto.
    /// </summary>
    public Color? TeamWash { get => teamWash; set => teamWash = value; }

    Color? teamWash;
    StandardMaterial3D? teamOverlay;

    public bool Invulnerable => invuln > 0f;
    public float DashCooldownFrac => Class.DashCooldown <= 0f ? 0f : MathU.Clamp01(dashCooldown / Class.DashCooldown);
    public float MeleeCooldownFrac => MathU.Clamp01(meleeCooldown / MeleeCooldown);
    public bool MeleeReady => meleeCooldown <= 0f;
    public float HealthFrac => MathU.Clamp01(Health / MaxHealth);

    // ---- special ability ----

    float specialCooldown;

    /// <summary>Seconds left on a timed special. Zero for instant ones and when inactive.</summary>
    public float BuffTime { get; private set; }

    public float SpecialCooldownFrac
        => Faction.SpecialCooldown <= 0f ? 0f : MathU.Clamp01(specialCooldown / Faction.SpecialCooldown);

    public bool SpecialReady => specialCooldown <= 0f && Alive;

    /// <summary>
    /// How many times this pawn has fired its special. The HUD spells out what the special does
    /// until this is non-zero, which is the cheapest possible answer to "I have no idea what
    /// Second Wind is" — it teaches on the first life and then gets out of the way for good.
    /// </summary>
    public int SpecialUses { get; private set; }

    // ---- class ability ----
    //
    // A second, separate ability on its own button and its own clock. The faction special is what
    // you are; this is how your class fights. They share nothing but the shape of the code.

    float classCooldown;

    /// <summary>Seconds left on a timed class ability. Zero for the instant ones.</summary>
    public float ClassBuffTime { get; private set; }

    public float ClassCooldownFrac
        => Class.SpecialCooldown <= 0f ? 0f : MathU.Clamp01(classCooldown / Class.SpecialCooldown);

    public bool ClassAbilityReady => classCooldown <= 0f && Alive;

    /// <summary>How many times this pawn has used its class ability. Drives the HUD's teaching line.</summary>
    public int ClassAbilityUses { get; private set; }

    // These two read the *class* clock and the *class* kind. They read the faction's before, which
    // meant they could never be true at all: no faction has Overdrive or Focus, so every consumer
    // of them — speed, fire interval, spread, projectile speed, the zoomed field of view — was
    // permanently switched off.
    public bool Overdriven => ClassBuffTime > 0f && Class.Special == SpecialKind.Overdrive;
    public bool Focused => ClassBuffTime > 0f && Class.Special == SpecialKind.Focus;

    /// <summary>Muses: hidden from targeting while the decoy walks on without you.</summary>
    public bool Understudying => BuffTime > 0f && Faction.Special == SpecialKind.Understudy;

    /// <summary>
    /// Running the Vessels' rebuilt special: ten seconds of being much harder to keep up with.
    ///
    /// Second Wind used to pay back banked damage as healing, and it was the least visible thing
    /// in the game — worth nothing at full health, and at low health worth a chunk of a health
    /// bar you could not see move. A faction whose whole answer is "humanity was the body" now
    /// gets an ability about what a body *does*.
    /// </summary>
    public bool Surging => BuffTime > 0f && Faction.Special == SpecialKind.SecondWind;

    /// <summary>
    /// Running Revelation, which now half-phases the caster as well as lighting up the enemy.
    ///
    /// The Custodians knew where everyone was and could do nothing with it. Being hard to hit
    /// while you have that information is what turns knowing into an advantage.
    /// </summary>
    public bool Phasing => BuffTime > 0f && Faction.Special == SpecialKind.Revelation;

    /// <summary>Custodians: outlined for the enemy team by someone else's Revelation.</summary>
    public float RevealedFor;

    /// <summary>
    /// Damage taken inside the Second Wind window, which is what the special pays back.
    ///
    /// Kept as a decaying total rather than a list of timestamped hits: the exact shape of the last
    /// few seconds does not matter, only roughly how badly you have just been hurt, and a float
    /// that bleeds away costs nothing per pawn per frame.
    /// </summary>
    public float DamageBanked { get; private set; }

    /// <summary>Seconds for the bank to empty on its own. Long enough to survive a firefight.</summary>
    const float BankWindow = 6f;

    /// <summary>Vessels only pay back a share, or the special would simply undo the fight.</summary>
    /// <summary>What Second Wind multiplies run speed by while it lasts.</summary>
    public const float SurgeSpeed = 2f;

    /// <summary>And jump height. Twice the height is roughly 1.41x the launch speed.</summary>
    public const float SurgeJump = 1.414f;

    /// <summary>
    /// Chance a shot aimed at a phasing Custodian passes straight through.
    ///
    /// Half. Not a damage reduction — a coin flip per shot, which reads completely differently:
    /// damage resistance is a number nobody can see, and a shot that visibly does nothing is a
    /// thing both players notice.
    /// </summary>
    public const float PhaseMissChance = 0.5f;

    // ---- stance ----

    /// <summary>Aiming down sights. Steadier and more accurate, at the cost of pace.</summary>
    public bool Ads { get; private set; }

    public bool Sprinting { get; private set; }
    public bool Crouching { get; private set; }

    /// <summary>Seconds left on a slide. Non-zero means sliding.</summary>
    public float SlideTime { get; private set; }

    public bool Sliding => SlideTime > 0f;

    /// <summary>True during the dash itself, when the pawn is a moving weapon.</summary>
    public bool Dashing => dashTime > 0f;

    /// <summary>
    /// Whether the feet are down. Exposed for the harness, which has to be able to pose "standing"
    /// and "falling" separately now that the dash does two different things depending on which.
    /// </summary>
    public bool StandingOnFloor => IsOnFloor();

    /// <summary>Who this dash has already hit, so one dash cannot damage the same target twice.</summary>
    public readonly HashSet<Pawn> DashHits = new();

    Vector3 slideDir;

    const float SlideDuration = 0.75f;
    const float SlideSpeed = 13.5f;

    /// <summary>
    /// Platformer gravity, not shooter gravity. At 13 with a 9.1 jump the apex is about 3.2m and
    /// the hang is roughly 1.4 seconds — twice the height and nearly twice the airtime of the
    /// original, which is what makes deck-to-deck jumps readable in the air rather than a leap of
    /// faith you commit to on the ground.
    /// </summary>
    public const float Gravity = 13f;
    public const float JumpVelocity = 9.1f;

    /// <summary>Corpses fall at normal weight; a floaty body reads as a bug, not as style.</summary>
    const float CorpseGravity = 22f;

    /// <summary>
    /// Grace period after walking off an edge during which a jump still counts. Standard
    /// platformer forgiveness — without it, jumping from the lip of a deck fails often enough that
    /// players stop trusting the geometry.
    /// </summary>
    const float CoyoteTime = 0.12f;

    /// <summary>A jump pressed just before landing is remembered and fires on touchdown.</summary>
    const float JumpBuffer = 0.12f;

    float timeOffGround;
    float jumpBuffered;

    /// <summary>Eye height for the current stance, smoothed so standing up is not a snap.</summary>
    public float CurrentEyeHeight { get; private set; } = EyeHeight;

    /// <summary>
    /// The pawn's actual physical height right now. Crouching genuinely shrinks the collision
    /// capsule, not just the camera — so a crouched player is a smaller target, can be shot over,
    /// and has their head where their head visibly is.
    /// </summary>
    public float CurrentHeight { get; private set; } = Height;

    CapsuleShape3D capsule = null!;
    CollisionShape3D hull = null!;
    MeshInstance3D? bodyMesh;

    /// <summary>Resizes the capsule, its mesh and the shape offset to match the stance.</summary>
    void ApplyHeight(float h)
    {
        CurrentHeight = h;

        capsule.Height = h;
        hull.Position = Vector3.Up * (h * 0.5f);

        // The whole figure squashes vertically, which keeps the head attached to the top of the
        // body — and the head is where the headshot line is, so it has to move with the stance.
        if (rig != null) rig.Scale = new Vector3(1f, h / Height, 1f);
    }

    /// <summary>
    /// Whether there is headroom to stand back up. Without this a player who crouches under a
    /// deck and releases crouch would grow into the geometry and get shoved out of the world.
    /// </summary>
    bool CanStand()
    {
        if (CurrentHeight >= Height - 0.01f) return true;

        var space = GetWorld3D().DirectSpaceState;
        var from = GlobalPosition + Vector3.Up * (CurrentHeight - Radius);
        var to = GlobalPosition + Vector3.Up * (Height + 0.05f);

        using var query = PhysicsRayQueryParameters3D.Create(from, to);
        query.Exclude = new Godot.Collections.Array<Rid> { GetRid() };
        return space.IntersectRay(query).Count == 0;
    }

    /// <summary>Speed after buffs and stance. Sprinting is fast; aiming, crouching are not.</summary>
    public float EffectiveSpeed
    {
        get
        {
            float s = Class.Speed * MoveScale * (Overdriven ? 1.45f : 1f) * (Crown?.SpeedScale ?? 1f);

            // Twice as fast, flat. This is the whole point of the rebuilt Second Wind and it is
            // meant to be unmistakable from across the arena.
            if (Surging) s *= SurgeSpeed;
            if (Sprinting) s *= 1.5f;
            else if (Ads) s *= 0.55f;

            // Holding the saber up is a stance, not a toggle. Without a cost it would simply be
            // on all the time, and a juggernaut who is permanently immune to fire from the front
            // has no counterplay left at all — this is what makes closing the distance a decision.
            if (Blocking) s *= BlockSlow;

            if (Crouching) s *= 0.5f;

            // Standing in someone else's Bloom. Applied last so it scales whatever the stance
            // arrived at rather than being swamped by a sprint.
            s *= SlowFactor;
            return s;
        }
    }

    // ---- weapon ----

    /// <summary>
    /// Two weapon slots, Halo-style. Slot zero starts as the class weapon and can be replaced;
    /// slot one starts empty.
    ///
    /// Carrying two and choosing between them is a much better decision than carrying one and
    /// losing it: a sword is devastating in a corridor and useless across a causeway, and before
    /// this, taking one meant giving up every long shot until it ran dry.
    /// </summary>
    readonly WeaponDef?[] held = new WeaponDef?[2];
    readonly int[] heldAmmo = new int[2];

    /// <summary>Which slot is in your hands, 0 or 1.</summary>
    public int ActiveSlot { get; private set; }

    public WeaponDef? SlotWeapon(int i) => held[i & 1];
    public int SlotAmmo(int i) => heldAmmo[i & 1];

    /// <summary>True when both slots are full, so a third pickup has to replace one.</summary>
    public bool SlotsFull => held[0] != null && held[1] != null;

    /// <summary>Whether there is a second weapon to switch to at all.</summary>
    public bool CanSwapWeapon => held[0] != null && held[1] != null;

    /// <summary>A weapon picked up off the map, or null while carrying the class weapon.</summary>
    public WeaponDef? PickedUp
        => ReferenceEquals(held[ActiveSlot], Class.Weapon) ? null : held[ActiveSlot];

    /// <summary>Shots left on the active weapon. Zero ammo on a class weapon means unlimited.</summary>
    public int PickupAmmo => heldAmmo[ActiveSlot];

    // ---- jetpack ----

    /// <summary>Seconds of thrust left. Zero means no jetpack.</summary>
    public float JetFuel { get; private set; }

    /// <summary>
    /// Seconds of thrust in a full pack.
    ///
    /// Eighteen, up from four and a half. Four and a half seconds bought about one climb on a map
    /// whose towers are twenty metres tall — enough to reach a roof and not enough to do anything
    /// once you were up there. At eighteen the pack is a way of moving around the arena rather
    /// than a single-use lift.
    /// </summary>
    public const float JetFuelMax = 18f;

    /// <summary>
    /// Everyone's pace, as a fraction of what the class table says.
    ///
    /// One number rather than twelve edits, because the class table's job is the *differences*
    /// between the classes — a Flanker being fast and a Juggernaut being slow — and rewriting
    /// every row to change the overall pace would bury that spread in noise and make the next
    /// adjustment twelve edits again.
    ///
    /// A fifth off. At full pace a duel across open ground was decided by who happened to be
    /// pointing the right way, because both fighters crossed the other's field of view faster
    /// than a stick can follow; the shooting is more interesting when there is time to aim.
    /// Every stance multiplier — sprint, crouch, aim, slide — scales with it, so their relative
    /// weight is unchanged.
    /// </summary>
    public const float MoveScale = 0.8f;

    /// <summary>Top sprinting speed of the fastest class, for balance assertions.</summary>
    public static float Speed15() => Classes.Flanker.Speed * MoveScale * 1.5f;

    /// <summary>Upward acceleration while thrusting. Beats gravity comfortably, not violently.</summary>
    ///
    /// Halved, with <see cref="JetRise"/>. At twice gravity it still climbs without argument, but
    /// it takes most of a second to reach the rise speed instead of being there instantly, so the
    /// pack reads as lift rather than as a launch.
    const float JetThrust = 26f;

    /// <summary>
    /// Terminal upward speed under thrust.
    ///
    /// Halved from 34, which was a launch rather than a climb: a quarter-second tap reached about
    /// 26 m/s, and *releasing* at that speed coasts another 26 metres with nothing capping it —
    /// the ceiling below only applies while the button is held. So a tap could carry you out
    /// through the top of the play boundary, and out of bounds is fatal with no exceptions.
    ///
    /// At 17 the same release coasts about eleven metres, which the ceiling has room to absorb.
    ///
    /// Halved again to 8.5, which is where it started. The pack at 17 was still doing most of a
    /// player's vertical movement for them; at this speed a climb is something you spend fuel on
    /// over several seconds rather than something one tap of the button buys outright. The tank of
    /// fuel is deliberately untouched — this is a change to how hard it pushes, not how long.
    /// </summary>
    public const float JetRise = 8.5f;

    /// <summary>
    /// The altitude a jetpack will not thrust past, in metres.
    ///
    /// At this power the pack will happily carry you out of the world: leaving the arena is fatal
    /// with no exceptions, and the play boundary tops out a little over forty metres. Killing
    /// someone for holding the jump button is not a rule anyone would call fair, so the thrust
    /// simply stops rather than the player simply dying.
    ///
    /// Well above the tallest thing on any layout, so it never gets in the way of a real climb.
    /// </summary>
    const float JetCeiling = 34f;

    public bool HasJetpack => JetFuel > 0f;

    // ---- grapple ----

    /// <summary>Where the hook is stuck, while it is. Null when not grappling.</summary>
    public Vector3? GrappleAnchor { get; private set; }

    /// <summary>Seconds left on the pull before it lets go on its own.</summary>
    float grappleLeft;

    /// <summary>Distance to the anchor last time progress was measured, and how long ago that was.</summary>
    float grappleLastGap;
    float grappleStallCheck;

    public bool Grappling => GrappleAnchor != null;

    /// <summary>
    /// Speed of the winch.
    ///
    /// Raised with the range. At the old twenty-seven metres a second, a rope that now reaches four
    /// hundred metres would spend fifteen seconds reeling you in — which is not a movement tool,
    /// it is a cutscene. At fifty-five you cross an arena in about five seconds, which is fast
    /// enough to be a decision and slow enough to be shot at on the way.
    /// </summary>
    const float GrappleSpeed = 55f;

    /// <summary>How close to the anchor counts as arrived.</summary>
    const float GrappleArrive = 2.2f;

    /// <summary>
    /// Seconds a pull can last before it gives up.
    ///
    /// A grapple that never times out is a grapple that can hold you against a wall forever if the
    /// anchor turns out to be somewhere the controller cannot reach — behind a lip, inside a step,
    /// on the far side of a corner. Long enough to cross the hook's whole range at winch speed —
    /// which is why this moved when the range did: a timeout shorter than the trip would strand you
    /// in mid-air every time you used the reach the gun advertises.
    /// </summary>
    const float GrappleMaxTime = 9f;

    /// <summary>
    /// Stick the hook and start winching.
    ///
    /// Refused at point blank: an anchor closer than the arrive radius would complete on the tick
    /// it started, which is a wasted round and a lurch for no reason.
    /// </summary>
    public void StartGrapple(Vector3 anchor)
    {
        if (anchor.DistanceTo(GlobalPosition) < GrappleArrive) return;

        GrappleAnchor = anchor;
        grappleLeft = GrappleMaxTime;
        grappleLastGap = float.MaxValue;
        grappleStallCheck = 0f;
    }

    public void ReleaseGrapple()
    {
        GrappleAnchor = null;
        grappleLeft = 0f;
    }

    /// <summary>True on the frames thrust is actually being applied, for the exhaust plume.</summary>
    public bool Thrusting { get; private set; }

    public void GiveJetpack() => JetFuel = JetFuelMax;

    /// <summary>Top up, never past the class maximum.</summary>
    public void Heal(float amount)
    {
        if (!Alive) return;
        Health = MathF.Min(MaxHealth, Health + amount);
    }

    /// <summary>
    /// Seconds the interact button has been held this stint. Weapon crates need a deliberate hold
    /// rather than a walk-over, so the pawn has to carry the timer.
    /// </summary>
    public float PickupHold { get; private set; }

    public void ClearPickupHold() => PickupHold = 0f;

    /// <summary>
    /// How much of normal pace this pawn keeps, 1 meaning all of it. Written by the match each
    /// tick from whatever Blooms it is standing in, and read by EffectiveSpeed.
    /// </summary>
    public float SlowFactor { get; set; } = 1f;

    // ---- juggernaut ----
    //
    // Held on the pawn rather than looked up from the match every time it is needed, because it is
    // read by movement, damage and drawing — three paths that should not each have to know what
    // mode is running.

    /// <summary>The figure this pawn is wearing, or null when it is not the juggernaut.</summary>
    public JuggernautDef? Crown { get; private set; }

    public bool IsJuggernaut => Crown != null;

    /// <summary>Kills made while wearing the crown. This is what the mode scores.</summary>
    public int CrownKills;

    /// <summary>
    /// Take the crown, at full strength.
    ///
    /// Health is scaled rather than added to, and set to the new maximum: taking the crown is a
    /// reward for a kill, and handing someone a huge pool they are already most of the way through
    /// would make the reward worth almost nothing.
    /// </summary>
    public void TakeCrown(JuggernautDef def)
    {
        Crown = def;
        Health = MaxHealth;
        DropCrownDrain();
        ShowCrown(true);
    }

    public void LoseCrown()
    {
        Crown = null;
        Health = MathF.Min(Health, MaxHealth);
        ShowCrown(false);
    }

    Node3D? crownMark;

    /// <summary>
    /// A lit ring over the juggernaut's head, in their faction's colour.
    ///
    /// The HUD waypoint tells you where they are; this tells you which one they are once you can
    /// see them. In a twelve-fighter brawl those are different problems, and a marker that only
    /// existed on the HUD would leave you shooting the wrong person at close range.
    /// </summary>
    void ShowCrown(bool on)
    {
        if (rig == null) return;

        if (!on)
        {
            crownMark?.QueueFree();
            crownMark = null;
            return;
        }

        if (crownMark != null) return;

        crownMark = new Node3D { Position = Vector3.Up * (Height + 0.55f) };
        rig.AddChild(crownMark);

        var mat = Graphics.Hot(Faction.Tint, 5f);

        // Points on a circle rather than a torus: it matches the boxy vocabulary of everything
        // else in the arena and costs six small meshes.
        for (int i = 0; i < 6; i++)
        {
            float a = i * MathF.Tau / 6f;
            crownMark.AddChild(new MeshInstance3D
            {
                Mesh = new BoxMesh { Size = new Vector3(0.13f, 0.30f, 0.13f) },
                MaterialOverride = mat,
                Layers = VisualLayerFor(ViewIndex),
                Position = new Vector3(MathF.Cos(a) * 0.42f, 0f, MathF.Sin(a) * 0.42f),
            });
        }
    }

    /// <summary>Health ceiling, which the crown multiplies.</summary>
    public float MaxHealth => Class.Health * (Crown?.HealthScale ?? 1f);

    /// <summary>
    /// Scheherazade's clock. Seconds of story left; every kill buys more.
    ///
    /// Zero for every other juggernaut and for everyone not wearing a crown, so nothing else has to
    /// branch on which figure is in play.
    /// </summary>
    public float NightsLeft { get; private set; }

    void DropCrownDrain()
        => NightsLeft = Crown?.Kind == JuggernautKind.Scheherazade ? SchehStartNights : 0f;

    /// <summary>Seconds she gets on taking the crown, and what one kill is worth.</summary>
    public const float SchehStartNights = 14f;
    public const float SchehNightsPerKill = 7f;

    // ---- the crown's power ----

    float crownPowerCooldown;

    /// <summary>Seconds the power is still running. Zero for the instant ones and when idle.</summary>
    public float CrownPowerTime { get; private set; }

    public bool CrownPowerReady => Crown != null && crownPowerCooldown <= 0f && Alive;
    public bool CrownPowerActive => CrownPowerTime > 0f;

    public float CrownPowerFrac
        => Crown is null || Crown.PowerCooldown <= 0f
            ? 0f
            : MathU.Clamp01(crownPowerCooldown / Crown.PowerCooldown);

    /// <summary>Powers fired. Lets the harness prove they are reachable rather than merely defined.</summary>
    public int CrownPowerUses { get; private set; }

    /// <summary>
    /// Incoming damage multiplier. The Ark and the Thousand are both, mechanically, this number.
    /// </summary>
    public float DamageResist { get; set; } = 1f;

    /// <summary>True while Prometheus is off the ground pouring fire.</summary>
    public bool Flying => CrownPowerActive && Crown?.Kind == JuggernautKind.Prometheus;

    /// <summary>
    /// True while something is making this pawn a poor target — the Muses' decoy, or Scheherazade
    /// hiding inside a crowd of herself. Bots skip it, and the aim assist will not stick to it.
    /// </summary>
    public bool HardToFind
        => Understudying
        || (CrownPowerActive && Crown?.Kind == JuggernautKind.Scheherazade);

    /// <summary>Height Prometheus holds above wherever he was standing when he lit up.</summary>
    public const float FlightHeight = 7f;

    float flightFloor;

    public void StartCrownPower()
    {
        if (Crown is not { } c) return;

        flightFloor = GlobalPosition.Y;

        crownPowerCooldown = c.PowerCooldown;
        CrownPowerTime = c.PowerDuration;
        CrownPowerUses++;
    }

    /// <summary>Burn a slice of the clock. Only Scheherazade has one.</summary>
    public void SpendNight(float dt)
    {
        if (Crown?.Kind != JuggernautKind.Scheherazade) return;
        NightsLeft = MathF.Max(0f, NightsLeft - dt);
    }

    public void BuyAnotherNight()
    {
        if (Crown?.Kind != JuggernautKind.Scheherazade) return;
        NightsLeft = MathF.Min(NightsLeft + SchehNightsPerKill, SchehStartNights * 1.6f);
    }

    /// <summary>
    /// Drop spawn protection, for the harness. A freshly respawned pawn cannot be damaged, which
    /// is correct in play and makes it impossible to pose "has just been hurt" in a test.
    /// </summary>
    public void ClearSpawnProtectionForTest() => invuln = 0f;

    /// <summary>Second Wind empties the bank when it is spent, so it cannot be cashed twice.</summary>
    public void SpendBank() => DamageBanked = 0f;

    /// <summary>Accumulate the interact hold, resetting the moment the button is released.</summary>
    public void TrackPickupHold(float dt, bool held) => PickupHold = held ? PickupHold + dt : 0f;

    /// <summary>
    /// The weapon actually being fired right now.
    ///
    /// A crown overrides the loadout outright rather than clearing it. Juggernauts carry the saber
    /// and nothing else, and doing it here means the slots are untouched underneath — lose the
    /// crown and you are holding exactly what you were holding when you took it, with the same
    /// ammo. Clearing the slots on coronation would have quietly robbed whoever got the crown of
    /// the railgun they had walked across the map to find.
    /// </summary>
    public WeaponDef Weapon
        => PortalOnly ? Weapons.PortalGun
         : Crown != null ? Weapons.Saber
         : (held[ActiveSlot] ?? Class.Weapon);

    /// <summary>
    /// True in Portal mode, where the portal gun is the only thing anybody carries.
    ///
    /// Overriding here rather than clearing the slots, for the same reason the saber does: the
    /// loadout underneath is left untouched, so nothing has to be put back afterwards and no
    /// other system needs to know the mode exists.
    /// </summary>
    public bool PortalOnly;

    /// <summary>Whether what is in your hands is a blade rather than a gun.</summary>
    public bool HasSaber => Crown != null;

    // ---- the Tragedian ----
    //
    // The one stat line in the game that rewards the fight having gone badly. Everything else here
    // gets worse as you lose health; this gets better, which makes a Tragedian on a sliver the most
    // dangerous thing on the map and makes finishing one a decision rather than a formality.

    /// <summary>True while Final Act is running: cannot be killed, only worn down to one point.</summary>
    public bool LastStand => Class.Special == SpecialKind.LastStand && ClassBuffTime > 0f;

    /// <summary>
    /// How much harder this pawn hits right now, from how close to death it is.
    ///
    /// Only ever above one for a Tragedian mid-Final-Act, and it scales with the health *missing*
    /// rather than switching on at a threshold — a cliff would mean the interesting part of the
    /// character was a single hit point wide, and a curve means every exchange while it runs makes
    /// the next one worse for whoever is standing there.
    /// </summary>
    public float DesperationScale
        => LastStand ? 1f + (1f - HealthFrac) * LastStandBite : 1f;

    /// <summary>Damage multiplier at death's door. Two and a half times at one hit point.</summary>
    public const float LastStandBite = 1.5f;

    /// <summary>Start the class ability's clock directly, for the harness.</summary>
    public void StartClassAbilityForTest() => ClassBuffTime = Class.SpecialDuration;

    /// <summary>Put the faction buff on without spending its cooldown, for the harness.</summary>
    public void StartSpecialForTest() => BuffTime = Faction.SpecialDuration;

    /// <summary>Set health directly, for the harness. Poses a state instead of playing to it.</summary>
    public void SetHealthForTest(float value) => Health = MathU.Clamp(value, 0.01f, MaxHealth);

    /// <summary>
    /// True while a juggernaut is holding the saber up.
    ///
    /// The aim button, because a blade has no sights and a juggernaut has no gun to aim — the
    /// control was sitting there unused, and it is already the button your finger rests on when
    /// you are being careful.
    /// </summary>
    public bool Blocking { get; private set; }

    /// <summary>
    /// Half the arc in front of a blocking juggernaut that a shot can be turned out of, in radians.
    ///
    /// Just over a right angle either side, so a block covers the direction you are looking and
    /// nothing behind you. That is the whole counterplay: the answer to a hero holding a saber up
    /// is not a bigger gun, it is a second person shooting from somewhere else.
    /// </summary>
    public const float BlockArc = 1.1f;

    /// <summary>What holding the block up costs, as a fraction of normal speed.</summary>
    public const float BlockSlow = 0.45f;

    /// <summary>
    /// Whether a shot arriving along <paramref name="travel"/> gets turned around.
    ///
    /// Measured flat: pitch is left out on purpose, because a shot from a sniper on a tower is
    /// still a shot from that direction, and a block that failed on elevation would be a block
    /// that failed for reasons the player cannot see.
    /// </summary>
    public bool Deflects(Vector3 travel)
    {
        if (!Blocking || !Alive) return false;

        var incoming = new Vector2(-travel.X, -travel.Z);
        if (incoming.LengthSquared() < 0.0001f) return false;

        var facing = MathU.FromAngle(Facing);
        float away = MathF.Acos(Mathf.Clamp(facing.Dot(incoming.Normalized()), -1f, 1f));

        return away <= BlockArc;
    }

    /// <summary>Put the class weapon back in slot zero and empty slot one. Used on respawn.</summary>
    public void ResetLoadout()
    {
        held[0] = Class.Weapon;
        held[1] = null;
        heldAmmo[0] = 0;
        heldAmmo[1] = 0;
        ActiveSlot = 0;
        RefreshViewModel();
    }

    /// <summary>
    /// Take a weapon into a free slot, or replace what is in your hands if both are full.
    ///
    /// Replacing the *active* slot rather than a chosen one is the Halo rule and the right one: you
    /// drop what you are looking at holding, which is the thing you have already decided you like
    /// least by virtue of having swapped away from it.
    /// </summary>
    public void TakeWeapon(WeaponDef w)
    {
        int slot = held[0] == null ? 0
                 : held[1] == null ? 1
                 : ActiveSlot;

        held[slot] = w;
        heldAmmo[slot] = w.Ammo;
        ActiveSlot = slot;
        RefreshViewModel();
    }

    /// <summary>Switch to the other slot, if there is anything in it.</summary>
    public void SwapWeapon()
    {
        if (!CanSwapWeapon) return;

        ActiveSlot ^= 1;
        RefreshViewModel();
    }

    /// <summary>Whether taking this weapon would achieve anything.</summary>
    public bool WouldTake(WeaponDef w)
    {
        // A juggernaut cannot use a gun and should not be walking the map to collect them. Leaving
        // the crate standing also means it is still there for whoever eventually kills them.
        if (HasSaber) return false;

        // Already holding one of these: leave it standing rather than silently refilling, which
        // would make a camped pickup far too strong.
        foreach (var h in held) if (ReferenceEquals(h, w)) return false;
        return true;
    }

    /// <summary>Spends a shot; an emptied pickup falls away and the class weapon comes back.</summary>
    void ConsumeAmmo()
    {
        // A crowned pawn is swinging the saber, whatever is still sitting in the slots. Without
        // this, every swing would spend a round of the railgun the player was holding when the
        // crown landed, and they would find it empty when they lost it.
        if (HasSaber) return;

        int slot = ActiveSlot;
        if (held[slot] == null || heldAmmo[slot] <= 0) return;      // class weapon: unlimited

        heldAmmo[slot]--;
        if (heldAmmo[slot] > 0) return;

        // Running dry empties the slot. If that leaves you with nothing at all, the class weapon
        // comes back — you are never standing there unarmed.
        held[slot] = null;

        if (held[slot ^ 1] != null) ActiveSlot = slot ^ 1;
        else { held[slot] = Class.Weapon; heldAmmo[slot] = 0; }

        RefreshViewModel();
    }

    /// <summary>Seconds between shots after any active buff.</summary>
    public float EffectiveFireInterval => Weapon.FireInterval * (Overdriven ? 0.58f : 1f);

    /// <summary>
    /// Spread after buffs and stance. Aiming is the big one; crouching adds a little on top, so
    /// a crouched, aimed shot is the most accurate the game offers.
    /// </summary>
    public float EffectiveSpreadDeg
    {
        get
        {
            float s = Weapon.SpreadDeg * (Focused ? 0.12f : 1f);
            if (Ads) s *= 0.35f;
            if (Crouching) s *= 0.7f;
            return s;
        }
    }

    /// <summary>Look-rate multiplier. Aiming steadies the view; sliding does not.</summary>
    public float LookRateScale => Ads ? 0.55f : 1f;

    /// <summary>You cannot shoot mid-sprint — that is the cost of the speed.</summary>
    public bool CanFire => !Sprinting && Alive;

    public float EffectiveProjectileSpeed => Weapon.ProjectileSpeed * (Focused ? 1.7f : 1f);

    /// <summary>
    /// External shove from a blast, decaying on its own. Kept separate from input-driven velocity
    /// so being blown across the arena does not fight with the player's own movement.
    /// </summary>
    public Vector3 Knockback;

    public void ApplyKnockback(Vector3 impulse)
    {
        if (!Alive || Invulnerable) return;
        Knockback += impulse;
    }

    // ---- slipping ----

    /// <summary>How long a slip takes your feet out from under you.</summary>
    public const float SlipDuration = 1f;

    /// <summary>Seconds left on a slip. Zero means upright.</summary>
    public float SlipTime { get; private set; }

    /// <summary>True while off your feet on a butter trail: no steering, no shooting, no jump.</summary>
    public bool Slipping => SlipTime > 0f;

    /// <summary>
    /// Take this pawn's feet out from under them.
    ///
    /// Like <see cref="Launch"/> and unlike knockback, this ignores invulnerability. Butter is
    /// terrain, not an attack — a dashing player skidding straight through a slick with no effect
    /// would read as the trail being broken rather than as the dash working.
    ///
    /// Backwards, away from where they were facing, so the fall reads as feet-shooting-forwards
    /// rather than as a shove. The upward part is small on purpose: this is a pratfall, not a
    /// launch pad, and being thrown into the air would make it a way of reaching things.
    ///
    /// Already-slipping pawns are ignored rather than re-slipped, so lying in a wide patch is one
    /// second on the floor and not an indefinite hold.
    /// </summary>
    public void Slip()
    {
        if (!Alive || InVehicle || Slipping) return;

        SlipTime = SlipDuration;
        SlideTime = 0f;
        dashTime = 0f;

        var back = new Vector3(-MathF.Cos(Facing), 0f, -MathF.Sin(Facing));
        Knockback += back * 6.5f + Vector3.Up * 3.2f;

        if (body != null) Sfx.PlayAt(Sound.Dash, GlobalPosition, -4f, 0.6f);
    }

    /// <summary>
    /// Flung upward by a launch pad. Unlike knockback this ignores invulnerability — a pad is
    /// level machinery, not an attack, and having it silently fail for a player who just dashed
    /// would read as the pad being broken.
    /// </summary>
    public void Launch(float impulse)
    {
        if (!Alive) return;
        Velocity = new Vector3(Velocity.X, impulse, Velocity.Z);
        SlideTime = 0f;
    }

    /// <summary>Killed by falling out of the world. No attacker, no damage, just gone.</summary>
    public void TakeFatalFall()
    {
        if (!Alive) return;
        Health = 0f;
        Deaths++;
        RespawnIn = RespawnDelay;
        DeadFor = 0f;
    }

    /// <summary>Where shots leave from and where line-of-sight is measured. Follows the stance.</summary>
    public Vector3 Eye => GlobalPosition + Vector3.Up * CurrentEyeHeight;

    /// <param name="viewIndex">
    /// Which splitscreen view belongs to this pawn, or -1 for a bot. Distinct from
    /// <paramref name="slot"/>, which is the roster position: with twelve fighters and two humans,
    /// slots run 0..11 while view indices run 0..1.
    /// </param>
    public void Setup(ClassDef cls, int slot, bool isBot, Color tint, bool visuals, int viewIndex)
    {
        Class = cls;
        Slot = slot;
        IsBot = isBot;
        Tint = tint;
        ViewIndex = viewIndex;
        Health = cls.Health;

        capsule = new CapsuleShape3D { Radius = Radius, Height = Height };
        hull = new CollisionShape3D
        {
            Shape = capsule,
            Position = Vector3.Up * (Height * 0.5f),
        };
        AddChild(hull);

        if (!visuals) return;

        // A human's meshes live on their own render layer so their first-person camera can cull the
        // body it is sitting inside; every bot shares one layer, because no camera has to hide a
        // bot from itself. All views share one World3D, so per-viewport visibility has to be done
        // with layers rather than by toggling Visible.
        uint layer = VisualLayerFor(viewIndex);

        BuildRig(tint, layer);

        if (teamWash is { } mark) AddTeamMarker(mark, layer);

        // Bots are never looked out of, so they need no view model.
        wornModel = Wearing.Model.Length > 0 ? Wearing.Model : Faction.Model;
        viewSlot = slot;
        viewTint = tint;
        held[0] = cls.Weapon;
        if (!isBot) BuildViewModel(cls.Weapon, viewIndex, tint);
    }

    /// <summary>
    /// What this pawn chose to come back as. Read by the respawn, then left alone.
    ///
    /// Held on the pawn rather than on the view because a bot picks one too, and because the choice
    /// has to survive the respawn timer — which is the whole window in which it is made.
    /// </summary>
    public ReinforcementDef NextSpawn = Reinforcements.Trooper;

    /// <summary>The reinforcement currently being worn, or one of the four basics.</summary>
    public ReinforcementDef Wearing = Reinforcements.Trooper;

    /// <summary>Set when the choice on the spawn screen has been locked in. Cleared on spawn.</summary>
    public bool SpawnConfirmed;

    /// <summary>
    /// Which command post to come back at, or -1 to let the match choose.
    ///
    /// Only meaningful in Dominion. Held on the pawn rather than the view because a bot picks one
    /// too, and because the choice has to survive the respawn timer — which is the entire window in
    /// which it is made.
    /// </summary>
    public int SpawnPost = -1;

    /// <summary>
    /// Set when the hero tier was bought and paid for, and not yet worn.
    ///
    /// Separate from <see cref="NextSpawn"/> because a hero is not a class — it is the crown, and
    /// it arrives through <see cref="TakeCrown"/> with its own health scaling, power and saber. The
    /// points come off when the choice is made rather than when the body appears, so a hero cannot
    /// be bought twice in the window between confirming and spawning.
    /// </summary>
    public bool HeroBought;

    /// <summary>
    /// Swap which class this pawn is, without rebuilding it.
    ///
    /// <see cref="Setup"/> cannot be called twice — it builds the collision shape and the rig, and
    /// running it again would add a second of each. Everything a class actually decides at runtime
    /// is health, speed, weapon and abilities, and none of that is baked into the body: the rig
    /// belongs to the *faction*, which does not change. So a class swap is this short, and spawning
    /// as the Orchard costs the same as spawning as a Trooper.
    /// </summary>
    public void BecomeClass(ClassDef cls)
    {
        if (ReferenceEquals(Class, cls)) return;

        Class = cls;
        Health = MathF.Min(Health, MaxHealth);

        // The loadout has to be rebuilt from the new class, or you keep the old class's gun in
        // slot zero and a Marksman respawns holding a shotgun.
        ResetLoadout();
    }

    /// <summary>
    /// Put on the body this pawn should now be wearing, if it is not already wearing it.
    ///
    /// Separate from <see cref="BecomeClass"/> because the two change at different moments and for
    /// different reasons: a class swap is stats and a gun, and this is a whole different character
    /// walking out of the spawn. Called from the respawn, which is the only point at which a pawn's
    /// appearance is allowed to change — swapping the model mid-stride would be a body popping in
    /// front of whoever was shooting at it.
    ///
    /// Rebuilding rather than retexturing, because these are genuinely different meshes with
    /// different skeletons. Everything hanging off the rig — the crown ring, the team wash — is
    /// rebuilt with it, which is why they are put back here rather than left dangling.
    /// </summary>
    public void WearBody(bool visuals)
    {
        if (!visuals || rig == null) return;

        string want = Wearing.Model.Length > 0 ? Wearing.Model : Faction.Model;
        if (want == wornModel) return;

        bool hadCrown = crownMark != null;

        rig.QueueFree();
        rig = null;
        bodyMesh = null;
        crownMark = null;
        teamOverlay = null;

        wornModel = want;
        BuildRig(Tint, VisualLayerFor(ViewIndex));

        if (teamWash is { } mark) AddTeamMarker(mark, VisualLayerFor(ViewIndex));
        if (hadCrown) ShowCrown(true);
    }

    /// <summary>Which model is currently built, so a rebuild only happens when it actually changes.</summary>
    string wornModel = "";

    /// <summary>
    /// One simulation step. <paramref name="move"/> and <paramref name="aim"/> are already in
    /// world XZ space — resolving camera-relative input is the caller's job, because a bot has no
    /// camera to be relative to.
    /// </summary>
    public void Tick(float dt, PawnInput input, Match match)
    {
        Pitch = MathU.Clamp(input.Pitch, -MaxPitch, MaxPitch);

        Vector2 move = input.Move;
        Vector2 aim = input.Aim;

        if (fireCooldown > 0f) fireCooldown -= dt;
        if (dashCooldown > 0f) dashCooldown -= dt;
        if (meleeCooldown > 0f) meleeCooldown -= dt;
        if (MeleeSwing > 0f) MeleeSwing = MathF.Max(0f, MeleeSwing - dt);
        if (invuln > 0f) invuln -= dt;
        if (specialCooldown > 0f) specialCooldown -= dt;
        if (classCooldown > 0f) classCooldown -= dt;
        if (crownPowerCooldown > 0f) crownPowerCooldown -= dt;
        if (CrownPowerTime > 0f) CrownPowerTime = MathF.Max(0f, CrownPowerTime - dt);
        if (BuffTime > 0f) BuffTime -= dt;
        if (ClassBuffTime > 0f) ClassBuffTime -= dt;
        if (RevealedFor > 0f) RevealedFor -= dt;

        // The bank bleeds away, so Second Wind pays for a fight you are in rather than one you
        // walked away from a minute ago.
        if (DamageBanked > 0f)
            DamageBanked = MathF.Max(0f, DamageBanked - Class.Health / BankWindow * dt);

        // Knockback bleeds off quickly enough that a blast is a shove rather than a long slide.
        Knockback = Knockback.MoveToward(Vector3.Zero, 26f * dt);

        TickViewModel(dt);
        TickAnimation();

        if (!Alive)
        {
            TickDeath(dt);
            return;
        }

        // Riding: the hull owns this pawn's position entirely. Running the walk simulation too
        // would have two things fighting over the same transform every tick.
        if (InVehicle)
        {
            Velocity = Vector3.Zero;
            Thrusting = false;
            return;
        }

        // On the floor after a slip. Everything the player was asking for is dropped for the
        // second it lasts — no steering, no shooting, no jump, no ability, no stance — and what is
        // left is gravity and the knockback that put them there.
        //
        // The input is replaced rather than each consumer being taught about slipping. PawnInput
        // is a struct, so this is a local copy and the caller's own is untouched; a dozen separate
        // `&& !Slipping` guards is a dozen places for the next ability to forget one.
        if (SlipTime > 0f)
        {
            SlipTime = MathF.Max(0f, SlipTime - dt);
            input = default;
            move = Vector2.Zero;
            aim = Vector2.Zero;

            // Looking at the sky. Eased rather than snapped, so it reads as going over backwards.
            // Human views are driven from the screen as well, which owns the camera; this is what
            // makes a slipped bot look up too, and what the pawn's own head follows.
            Pitch = Mathf.Lerp(Pitch, MaxPitch, 1f - MathF.Exp(-11f * dt));
        }

        if (aim.LengthSquared() > 0.01f) Facing = MathU.Angle(aim);
        else if (move.LengthSquared() > 0.01f) Facing = MathU.Angle(move);

        UpdateStance(dt, input, move);

        // Wearing a crown replaces your faction special with the juggernaut's power. It is the
        // bigger button for the bigger thing, and it means no thirteenth input: you already know
        // where it is, it just does something enormous now.
        if (input.Special && IsJuggernaut)
        {
            if (CrownPowerReady)
            {
                StartCrownPower();
                match.UseCrownPower(this);
            }
        }
        else if (input.Special && SpecialReady)
        {
            SpecialUses++;

            // Faction, not class. The special moved to the faction and these two lines did not
            // follow it: the ability recharged on the class's timer while the HUD gauge normalised
            // against the faction's, so the bar sat at about two thirds when the special was
            // already usable — and every timed special ran for the wrong length.
            specialCooldown = Faction.SpecialCooldown;
            BuffTime = Faction.SpecialDuration;
            match.UseSpecial(this);
        }

        if (input.ClassAbility && ClassAbilityReady)
        {
            ClassAbilityUses++;
            classCooldown = Class.SpecialCooldown;
            ClassBuffTime = Class.SpecialDuration;
            match.UseClassAbility(this);
        }

        // Melee is deliberately independent of the weapon and of its cooldown. It is the thing you
        // always have, which is exactly what makes it worth a face button: no ammo to check, no
        // reload to wait out, no question of whether the gun in your hands can do it.
        if (input.Melee && meleeCooldown <= 0f)
        {
            meleeCooldown = MeleeCooldown;
            MeleeSwing = MeleeSwingTime;
            MeleeCounter++;
            match.MeleeStrike(this);
        }

        if (input.Dash && dashCooldown <= 0f && dashTime <= 0f)
        {
            StartDash(move);
            if (body != null) Sfx.PlayAt(Sound.Dash, GlobalPosition);
        }

        Vector3 horizontal;
        bool dashingNow = dashTime > 0f;

        if (dashingNow)
        {
            dashTime -= dt;
            horizontal = dashDir * DashSpeed;

            // The moment an airborne dash ends, its climb ends with it.
            //
            // Letting the vertical speed carry over was meant to turn an upward dash into an arc
            // rather than a stop in mid-air. What it actually produced was a catapult: the dash
            // finishes at twenty-two metres a second and then coasts, so a dash worth six metres of
            // ground put you eighteen metres up. Reported as it still launching far higher than it
            // should, and it did — the launch was almost entirely the coast, not the dash.
            if (dashTime <= 0f && dashRise > 0f) { dashRise = 0f; Velocity = Velocity with { Y = 0f }; }
        }
        else if (Sliding)
        {
            // A slide keeps its launch direction and bleeds speed, so it commits you: you cannot
            // steer out of it, which is what makes it a decision rather than a free speed boost.
            float t = SlideTime / SlideDuration;

            // Scaled with everything else. A slide is locomotion rather than an ability — it is
            // how you cross ground — so leaving it at full speed while walking dropped a fifth
            // would have made it the best way to travel by a wider margin than it was designed to
            // win by. The dash is deliberately *not* scaled: its reach is a stated distance the
            // ability is balanced around, not a pace.
            horizontal = slideDir * (SlideSpeed * MoveScale * Mathf.Lerp(0.35f, 1f, t));
        }
        else
        {
            horizontal = new Vector3(move.X, 0f, move.Y) * EffectiveSpeed;
        }

        horizontal += new Vector3(Knockback.X, 0f, Knockback.Z);

        // Gravity keeps pawns on the platform and lets them fall off cover naturally. A blast can
        // also lift you, which is why knockback contributes vertically too.
        bool grounded = IsOnFloor();
        timeOffGround = grounded ? 0f : timeOffGround + dt;

        if (input.Jump) jumpBuffered = JumpBuffer;
        else if (jumpBuffered > 0f) jumpBuffered -= dt;

        float vy = grounded && Knockback.Y <= 0.1f ? -1f : Velocity.Y - Gravity * dt;
        if (Knockback.Y > 0.1f) vy = MathF.Max(vy, Knockback.Y);

        bool canJump = timeOffGround <= CoyoteTime && !Sliding;

        if (jumpBuffered > 0f && canJump)
        {
            // Twice the height while surging. Height goes with the square of launch speed, so the
            // multiplier is the root of two rather than two — jumping twice as fast would be four
            // times as high, which is a different ability.
            vy = JumpVelocity * (Surging ? SurgeJump : 1f);
            jumpBuffered = 0f;
            timeOffGround = CoyoteTime + 1f;   // consume the grace so it cannot double-fire
            if (body != null) Sfx.PlayAt(Sound.Dash, GlobalPosition, -8f, 1.5f);
        }

        // Jetpack: holding jump in the air burns fuel and pushes up. It used to be a climb rather
        // than a launch; it is a launch now, and the fuel to keep launching.
        Thrusting = false;

        if (HasJetpack && input.JumpHeld && !grounded)
        {
            JetFuel = MathF.Max(0f, JetFuel - dt);
            Thrusting = true;

            // Thrust eases off approaching the ceiling instead of stopping dead at it. Cutting
            // power at an exact altitude reads as hitting something invisible; fading it out over
            // the last few metres reads as running out of air, which is at least a thing that
            // happens to aircraft.
            float headroom = MathU.Clamp01((JetCeiling - GlobalPosition.Y) / 6f);

            vy = MathF.Min(vy + JetThrust * headroom * dt, JetRise * headroom);

            if (body != null && GD.Randf() < 0.5f)
                Sfx.PlayAt(Sound.Dash, GlobalPosition, -14f, 2.1f);
        }

        // The winch. It owns both axes while it runs, which is what makes it a grapple rather than
        // a suggestion — fighting gravity and the movement stick at the same time would leave you
        // dangling under the anchor rather than arriving at it.
        //
        // Let go on a jump, on arrival, on running out of time, or if the pull stops making
        // progress. That last one is the important one: an anchor the controller cannot actually
        // reach — behind a lip, inside a step — would otherwise hold you against the wall for the
        // full timeout with the stick doing nothing.
        if (GrappleAnchor is { } anchor)
        {
            grappleLeft -= dt;

            Vector3 toAnchor = anchor - (GlobalPosition + Vector3.Up * (CurrentHeight * 0.5f));
            float gap = toAnchor.Length();

            bool stalled = grappleStallCheck > 0.15f && gap > grappleLastGap - 0.05f;
            grappleStallCheck = stalled ? 0f : grappleStallCheck + dt;
            if (!stalled) grappleLastGap = gap;

            if (gap < GrappleArrive || grappleLeft <= 0f || input.Jump || stalled)
            {
                ReleaseGrapple();

                // A little of the pull is kept, so arriving carries you on rather than stopping
                // you dead against the thing you just flew at.
                if (gap >= GrappleArrive) horizontal *= 0.4f;
            }
            else
            {
                Vector3 pull = toAnchor / gap * GrappleSpeed;
                horizontal = new Vector3(pull.X, 0f, pull.Z);
                vy = pull.Y;

                if (body != null && GD.Randf() < 0.25f)
                    Sfx.PlayAt(Sound.Dash, GlobalPosition, -16f, 2.4f);
            }
        }

        // Prometheus off the ground. Gravity is suspended outright rather than fought — he is not
        // jumping, he is hovering, and a titan who sags while pouring fire is not a titan.
        // Measured from where he stood when he lit up rather than from the ground beneath him.
        // There is no cheap "height above whatever is under me" in this simulation, and a titan
        // who sinks every time he crosses a trench is worse than one who holds his line.
        if (Flying)
        {
            float want = flightFloor + FlightHeight;

            vy = GlobalPosition.Y < want - 0.4f ? 10f
               : GlobalPosition.Y > want + 0.4f ? -4f
               : 0f;
        }

        // A dash owns the vertical axis for as long as it lasts, but only carries the modest rise
        // worked out in StartDash — never the dash's full 37 m/s. Gravity is suspended rather than
        // fought, so a rise of 9.5 really is 9.5 rather than most of it being eaten in transit.
        //
        // The vertical speed carries over when the dash ends. That is what makes an upward dash an
        // arc rather than a stop in mid-air, and at these numbers the arc is a second jump.
        if (dashingNow && dashRise != 0f) vy = dashRise;

        Velocity = new Vector3(horizontal.X, vy, horizontal.Z);
        MoveAndSlide();

        if (input.Fire && fireCooldown <= 0f && CanFire)
        {
            match.FireWeapon(this);
            fireCooldown = EffectiveFireInterval;
        }

        UpdateVisuals();
    }

    const float DashDuration = 0.16f;

    /// <summary>Metres per second while dashing. Distance and duration are the tuned numbers.</summary>
    float DashSpeed => Class.DashDistance / DashDuration;

    /// <summary>
    /// Vertical speed of the dash in flight. Zero for an ordinary flat one.
    ///
    /// There used to be a ceiling on this, which was treating the symptom. The climb was never the
    /// problem — an unbounded *coast* after the dash ended was, and capping the speed only changed
    /// how far the catapult threw you.
    /// </summary>
    float dashRise;

    /// <summary>
    /// Where a dash goes, decided by whether your feet are on the floor.
    ///
    /// On the ground it is the sidestep it always was: flat, along the movement stick, or along your
    /// facing if you are not holding one. Nothing about looking up or down touches it, which is what
    /// makes it reliable in a firefight — you dash where you are moving, every time.
    ///
    /// In the air it follows the crosshair, in full three dimensions. That is a different verb: on
    /// the ground you are dodging, in the air you are steering, and the two want different rules.
    /// The two previous versions each picked one and applied it everywhere — flat always, which
    /// could not leave the ground, then aim-directed always, which turned a glance upward into a
    /// launch.
    /// </summary>
    void StartDash(Vector2 move)
    {
        if (IsOnFloor())
        {
            dashRise = 0f;
            dashDir = move.LengthSquared() > 0.01f
                ? new Vector3(move.X, 0f, move.Y).Normalized()
                : new Vector3(MathF.Cos(Facing), 0f, MathF.Sin(Facing));
        }
        else
        {
            // One dash, pointed wherever you are looking. The *whole* aim vector, split into a
            // horizontal part and a vertical one, so the total distance travelled is exactly the
            // same six metres it would be along the ground — a dash straight up goes up as far as a
            // dash forward goes forward, and no further.
            //
            // The previous version took the horizontal component and then bolted a separate climb
            // on top of it, which meant an upward dash was strictly longer than a flat one. Between
            // that and the coast after it ended, aiming up turned a sidestep into a launch.
            Vector3 aim = AimDir.Normalized();

            dashDir = new Vector3(aim.X, 0f, aim.Z);
            dashRise = aim.Y * DashSpeed;
        }

        dashTime = DashDuration;
        DashHits.Clear();
        dashCooldown = Class.DashCooldown;
        invuln = DashDuration + 0.05f;
    }

    // ---- melee ----

    /// <summary>Seconds between swings. Long enough that melee is a choice, not a second trigger.</summary>
    public const float MeleeCooldown = 0.8f;

    /// <summary>How long the swing animation runs.</summary>
    public const float MeleeSwingTime = 0.24f;

    /// <summary>
    /// Resolves the stance for this tick. The interesting case is the slide: crouching *while*
    /// sprinting converts the sprint into a committed slide, rather than simply crouching.
    /// </summary>
    void UpdateStance(float dt, PawnInput input, Vector2 move)
    {
        if (SlideTime > 0f)
        {
            SlideTime -= dt;

            // A slide holds you low and locked in until it ends or you jump out of it.
            Crouching = true;
            Sprinting = false;
            Ads = false;

            if (input.Jump && IsOnFloor()) SlideTime = 0f;
        }
        else
        {
            bool movingForward = move.LengthSquared() > 0.25f;

            // Sprint needs real movement and rules out aiming — you cannot line up a shot at a
            // dead run.
            Sprinting = input.Sprint && movingForward && !input.Ads && IsOnFloor()
                        && Class.CanSprint;

            if (input.CrouchPressed && Sprinting && IsOnFloor())
            {
                slideDir = new Vector3(move.X, 0f, move.Y).Normalized();
                SlideTime = SlideDuration;
                Sprinting = false;
                Crouching = true;
                if (body != null) Sfx.PlayAt(Sound.Dash, GlobalPosition, -3f, 0.8f);
            }
            else
            {
                // Refuse to stand back up with something overhead.
                Crouching = input.Crouch || !CanStand();

                // The same button does two different things depending on what is in your hands.
                // Nothing about a blade wants a sight picture, so for a juggernaut the aim button
                // raises the saber instead — and Ads stays false, which keeps the field of view,
                // the crosshair and the view model from all trying to zoom a sword.
                Blocking = HasSaber && input.Ads && !Sprinting;
                Ads = input.Ads && !Sprinting && !HasSaber;
            }
        }

        // Eye height and the collision capsule ease together between stances, so dropping into a
        // crouch reads as a movement rather than as the camera teleporting — and so the hitbox
        // always matches what is on screen.
        bool low = Crouching || Sliding;

        float wantEye = low ? CrouchEyeHeight : EyeHeight;
        CurrentEyeHeight = MathU.Damp(CurrentEyeHeight, wantEye, 14f, dt);

        float wantHeight = low ? CrouchHeight : Height;
        ApplyHeight(MathU.Damp(CurrentHeight, wantHeight, 14f, dt));
    }

    // ---- death ----

    /// <summary>Seconds since this pawn died, driving the topple.</summary>
    public float DeadFor { get; private set; }

    /// <summary>How far through the falling-over animation, 0 to 1.</summary>
    public float FallProgress => MathU.Clamp01(DeadFor / FallDuration);

    public const float FallDuration = 0.55f;

    void TickDeath(float dt)
    {
        DeadFor += dt;

        // The body keeps its physics so it settles on whatever it fell onto, but stops steering.
        Velocity = new Vector3(Velocity.X * 0.86f, Velocity.Y - CorpseGravity * dt, Velocity.Z * 0.86f);
        MoveAndSlide();

        if (rig == null) return;

        // The whole figure topples as one, pivoting about the feet and settling flat on the
        // ground. Eased rather than linear so it starts fast and lands heavily.
        float t = Mathf.SmoothStep(0f, 1f, FallProgress);

        rig.Rotation = new Vector3(0f, 0f, Mathf.Lerp(0f, Mathf.Pi * 0.5f, t));
        rig.Position = new Vector3(Mathf.Lerp(0f, 0.35f, t), 0f, 0f);

        // Fades toward the ground as it falls, so a corpse is visibly not a threat well before it
        // disappears — the whole point being that you stop shooting it.
        if (bodyMesh?.MaterialOverride is StandardMaterial3D mat)
            mat.EmissionEnergyMultiplier = Mathf.Lerp(0.45f, 0f, t);
    }

    // ---- third-person figure ----

    /// <summary>
    /// Everything visible of this pawn to other players, under one node so the whole figure
    /// topples as a unit and scales as one when crouching.
    /// </summary>
    Node3D? rig;

    /// <summary>
    /// A blocky figure rather than a single box: head, torso, two arms, two legs and a held gun.
    ///
    /// The head is not decoration — headshots land above 72% of the pawn's height, so there has to
    /// be something up there to aim at. A featureless box gave no clue where that line was, or
    /// which way an opponent was facing.
    /// </summary>
    /// <summary>The faction's animation player, when a model is being worn. Null for the box rig.</summary>
    AnimationPlayer? anim;

    string? animWalk, animRun;

    /// <summary>Which faction's character this pawn wears. Presentation only; never affects the sim.</summary>
    public FactionDef Faction = Factions.Vessels;

    /// <summary>
    /// Put a faction character on this pawn, or fall back to the box rig.
    ///
    /// The model goes into the same <c>rig</c> node the boxes used, which is what keeps everything
    /// hanging off it working unchanged: the first-person cull layer, the turn to face, the death
    /// topple and the crouch squash all address the rig, not its contents.
    /// </summary>
    bool BuildFactionRig(uint layer)
    {
        // What this pawn is wearing, which is the reinforcement's own body if it has one and the
        // faction's otherwise. A Sinew is not a Vessel in a different colour.
        string want = Wearing.Model.Length > 0 ? Wearing.Model : Faction.Model;

        if (CharacterModels.Instance(want, out float sourceHeight) is not { } model) return false;

        rig = new Node3D { Name = "Rig" };
        AddChild(rig);
        rig.AddChild(model);

        // Scaled from the measured height of each export rather than a shared constant. The four
        // are not the same size in their own units and every pawn has to match one capsule.
        model.Scale = Vector3.One * (Height / sourceHeight);

        // The models face -Z out of Meshy and the pawn's zero heading looks down +X, so the model
        // is turned to agree with Facing rather than the maths being bent to agree with the model.
        model.RotationDegrees = new Vector3(0f, -90f, 0f);

        // Every mesh in the model has to carry this pawn's render layer, or a player would see
        // their own body filling their own first-person view.
        foreach (var mesh in AllMeshes(model)) mesh.Layers = layer;

        // In a team mode the whole character is washed in its side's colour.
        //
        // The box rig never needed this — it *was* the tint. A faction model carries its own
        // texture, and the moment those went in, the two sides of a team match became four people
        // in four unrelated costumes with nothing on them saying who was with whom. Reported as
        // "I can't tell who's on whose team right now", and it is not a small problem: shooting
        // your own side is the one mistake a team mode must not make easy.
        //
        // Mixed rather than added, and applied to every mesh rather than just the torso. Adding
        // brightens without changing hue, which does nothing for a pale model, and washing only
        // the chest leaves the head and legs reading as the wrong side from behind.
        if (teamWash is { } wash)
        {
            teamOverlay = new StandardMaterial3D
            {
                AlbedoColor = new Color(wash.R, wash.G, wash.B, 0.62f),
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                BlendMode = BaseMaterial3D.BlendModeEnum.Mix,

                // Emission carries most of the work. A purely diffuse wash goes to grey in the
                // shadowed half of the arena — exactly where you most need to know who someone is
                // — and on the Reliquary, which is a blue map, a diffuse blue character is invisible.
                EmissionEnabled = true,
                Emission = wash,
                EmissionEnergyMultiplier = 0.85f,
            };

            foreach (var mesh in AllMeshes(model)) mesh.MaterialOverlay = teamOverlay;
        }

        // The torso stands in for the flash target the box rig used.
        body = CharacterModels.FindMesh(model);
        bodyMesh = body;

        anim = CharacterModels.FindPlayer(model);
        PickAnimations();

        return true;
    }

    /// <summary>
    /// A lit team marker over the head, in team modes only.
    ///
    /// The wash alone is not enough and the first capture proved it: the Reliquary is a blue arena, so
    /// a blue-washed character standing against it is camouflaged by exactly the thing meant to
    /// identify them. Changing the palette is not the answer — blue against orange is the pairing
    /// a red-green colourblind player can actually separate, and that matters more.
    ///
    /// So the marker is emissive rather than tinted. Nothing in the arena is self-lit at this
    /// intensity, which means it reads against any wall of any colour at any distance, and it is
    /// the one cue that still works when a character is in shadow or silhouetted against the sky.
    ///
    /// It sits on the pawn's own render layer, so a player never sees their own — the first-person
    /// camera already culls the body it is inside.
    /// </summary>
    void AddTeamMarker(Color team, uint layer)
    {
        if (rig == null) return;

        // A flat chevron rather than a ball: two bars angled together read as pointing down at
        // someone, where a floating sphere reads as a pickup.
        foreach (int side in new[] { -1, 1 })
        {
            var bar = new MeshInstance3D
            {
                Mesh = new BoxMesh { Size = new Vector3(0.34f, 0.10f, 0.10f) },
                MaterialOverride = Graphics.Hot(team, 4.5f),
                Layers = layer,
                Position = new Vector3(side * 0.13f, Height + 0.34f, 0f),
            };
            bar.RotationDegrees = new Vector3(0f, 0f, side * 34f);
            rig.AddChild(bar);
        }
    }

    static IEnumerable<MeshInstance3D> AllMeshes(Node n)
    {
        if (n is MeshInstance3D m) yield return m;

        foreach (var child in n.GetChildren())
            foreach (var found in AllMeshes(child)) yield return found;
    }

    /// <summary>
    /// Work out which of the merged clips is the walk and which is the run.
    ///
    /// Matched by name rather than by index: the merge prefixes a clash with <c>run_</c>, and the
    /// Meshy exports do not agree with each other on what the base clip is called.
    /// </summary>
    void PickAnimations()
    {
        if (anim == null) return;

        foreach (var lib in anim.GetAnimationLibraryList())
            foreach (var name in anim.GetAnimationLibrary(lib).GetAnimationList())
            {
                string full = lib.ToString().Length > 0 ? $"{lib}/{name}" : name.ToString();
                string lower = full.ToLowerInvariant();

                if (lower.Contains("run")) animRun ??= full;
                else animWalk ??= full;
            }

        // A model with only one clip uses it for both, which reads better than freezing.
        animWalk ??= animRun;
        animRun ??= animWalk;
    }

    /// <summary>
    /// Drive the character's animation from how fast it is actually moving.
    ///
    /// Speed-matched rather than switched at a threshold: the clip is played at a rate proportional
    /// to ground speed, so the feet keep up with the floor instead of sliding, and slowing down
    /// slows the cycle rather than snapping to a different one.
    /// </summary>
    void TickAnimation()
    {
        if (anim == null) return;

        float speed = new Vector2(Velocity.X, Velocity.Z).Length();

        if (!Alive)
        {
            anim.Pause();
            return;
        }

        if (speed < 0.4f)
        {
            // Idle: hold the walk cycle still rather than looping it on the spot.
            if (animWalk != null && anim.CurrentAnimation != animWalk) anim.Play(animWalk);
            anim.Pause();
            return;
        }

        bool running = speed > Class.Speed * 1.15f;
        string? want = running ? animRun : animWalk;
        if (want == null) return;

        if (anim.CurrentAnimation != want) anim.Play(want);
        else if (!anim.IsPlaying()) anim.Play(want);

        // The reference clips are authored at roughly these speeds; anything else and the feet
        // skate. Clamped so a launch pad or a knockback does not spin the legs into a blur.
        float reference = running ? Class.Speed * 1.5f : Class.Speed;
        anim.SpeedScale = MathU.Clamp(speed / MathF.Max(reference, 0.5f), 0.35f, 2.2f);
    }

    void BuildRig(Color tint, uint layer)
    {
        if (BuildFactionRig(layer)) return;

        rig = new Node3D { Name = "Rig" };
        AddChild(rig);

        var skin = Graphics.Player(tint);
        var dark = Graphics.Player(tint * new Color(0.55f, 0.55f, 0.55f));

        MeshInstance3D Part(Vector3 size, Vector3 at, StandardMaterial3D mat)
        {
            var m = new MeshInstance3D
            {
                Mesh = new BoxMesh { Size = size },
                MaterialOverride = mat,
                Position = at,
                Layers = layer,
            };
            rig!.AddChild(m);
            return m;
        }

        // Legs.
        Part(new Vector3(0.22f, 0.62f, 0.26f), new Vector3(0f, 0.31f, -0.17f), dark);
        Part(new Vector3(0.22f, 0.62f, 0.26f), new Vector3(0f, 0.31f, 0.17f), dark);

        // Torso — kept as `body` because the invulnerability flash tints it.
        body = Part(new Vector3(0.52f, 0.62f, 0.66f), new Vector3(0f, 0.94f, 0f), skin);
        bodyMesh = body;

        // Arms, held forward so the figure reads as aiming rather than standing to attention.
        Part(new Vector3(0.44f, 0.18f, 0.18f), new Vector3(0.20f, 1.06f, -0.40f), dark);
        Part(new Vector3(0.44f, 0.18f, 0.18f), new Vector3(0.20f, 1.06f, 0.40f), dark);

        // The gun they are holding, so you can see what is pointed at you.
        Part(new Vector3(0.62f, 0.12f, 0.12f), new Vector3(0.52f, 1.06f, 0.22f),
             Graphics.Player(new Color(0.14f, 0.15f, 0.19f)));

        // Head. Sits astride the 72% line, which is where headshots begin.
        Part(new Vector3(0.40f, 0.40f, 0.40f), new Vector3(0f, 1.52f, 0f), skin);

        // A bright visor on the front of the head: the clearest single cue for facing, and it
        // marks the head itself as a target from across the arena.
        snout = Part(new Vector3(0.10f, 0.14f, 0.34f), new Vector3(0.20f, 1.54f, 0f),
                     Graphics.Hot(tint.Lightened(0.55f), 2.2f));
    }

    // ---- first-person view model ----

    /// <summary>
    /// The weapon seen down the barrel, on a layer only this pawn's own camera renders. Without
    /// it, firing in first person shows nothing at all except a tracer heading into the distance —
    /// there is no gun on screen, no flash, and with the sound off, no feedback whatsoever.
    ///
    /// Its transform is driven from the camera each frame by <c>MatchScreen</c>, since the camera
    /// carries pitch and the pawn body only carries yaw.
    /// </summary>
    public Node3D? ViewModel { get; private set; }

    MeshInstance3D? muzzle;

    /// <summary>Seconds left on the muzzle flash.</summary>
    float muzzleTimer;

    /// <summary>Increments once per trigger pull, so the camera can kick per shot.</summary>
    public int ShotCounter { get; private set; }

    public const float MuzzleFlashTime = 0.055f;

    /// <summary>The weapon the current view model was built for, so a swap can be detected.</summary>
    WeaponDef? viewWeapon;

    int viewSlot;
    Color viewTint;

    /// <summary>
    /// Rebuild the held model if the weapon has changed since it was last built.
    ///
    /// The view model used to be built once from the class and never touched again, so picking up
    /// a minigun or a sword changed everything about how you fought and nothing about what was in
    /// your hands — the strongest signal the game has for "you are holding something different"
    /// was simply absent.
    /// </summary>
    public void RefreshViewModel()
    {
        if (ViewModel == null && viewWeapon == null) return;      // headless: never built one
        if (ReferenceEquals(viewWeapon, Weapon)) return;

        ViewModel?.QueueFree();
        BuildViewModel(Weapon, viewSlot, viewTint);
    }

    /// <summary>
    /// Put a generated weapon model in the hands, or report that there is not one.
    ///
    /// Scaled and turned rather than trusted. Text-to-3D has no idea how long a rifle is and no
    /// consistent opinion about which way it faces, so the file's own length and longest axis are
    /// measured and mapped onto the silhouette the box version already used — which means a model
    /// dropping in keeps the hand position, the reach and the on-screen bulk that the rest of the
    /// feel was tuned against. A gun that arrives twice the size of the one it replaced is not an
    /// upgrade.
    /// </summary>
    /// <returns>False when there is no model, and the caller should build boxes instead.</returns>
    bool BuildWeaponModel(WeaponDef w, uint layer, Vector3 hold, float nearZ)
    {
        if (WeaponModels.Instance(w, out float sourceLength, out Vector3 along, out float facing)
                is not { } model)
            return false;

        ViewModel!.AddChild(model);

        // The silhouette's own length, so swapping a box for a model does not change how far the
        // gun reaches into the view.
        float want = w.Silhouette switch
        {
            WeaponSilhouette.Shotgun => 0.52f,
            WeaponSilhouette.Sniper => 0.94f,
            WeaponSilhouette.Smg => 0.38f,
            WeaponSilhouette.Minigun => 0.62f,
            WeaponSilhouette.Blade => 0.86f,
            WeaponSilhouette.Launcher => 0.72f,
            WeaponSilhouette.Portal => 0.46f,
            WeaponSilhouette.Grapple => 0.40f,
            _ => 0.58f,
        };

        model.Scale = Vector3.One * (want / sourceLength);

        // Turn the measured long axis to point down -Z, which is forward for the view model. Done
        // from the measurement rather than from a per-weapon tilt constant: twenty-three hand-tuned
        // rotations is twenty-three chances to get one wrong, and the axis is knowable.
        // The extra half-turn when the model was authored facing the other way. See
        // WeaponModels.MuzzleDirection — a generated gun has no reliable idea which way is front.
        float flip = facing > 0f ? 0f : 180f;

        if (along == Vector3.Right) model.RotationDegrees = new Vector3(0f, 90f + flip, 0f);
        else if (along == Vector3.Up) model.RotationDegrees = new Vector3(90f + flip, 0f, 0f);
        else model.RotationDegrees = new Vector3(0f, flip, 0f);

        // Sat at the same grip the boxes use, pushed forward so the near end clears the camera.
        model.Position = hold + new Vector3(0f, 0f, nearZ + want * 0.5f);

        foreach (var mesh in WeaponModels.AllMeshes(model)) mesh.Layers = layer;
        return true;
    }

    void BuildViewModel(WeaponDef w, int viewIndex, Color tint)
    {
        uint layer = ViewModelLayerFor(viewIndex);

        viewWeapon = w;
        viewSlot = viewIndex;
        viewTint = tint;

        ViewModel = new Node3D { Name = "ViewModel" };
        AddChild(ViewModel);

        // Held low and right. The near edge sits well clear of the camera: at this field of view
        // anything within half a metre is enormous on screen, which is what made the first
        // attempt's grip swallow the barrel.
        const float NearZ = -0.72f;

        // Both offsets pulled 15% toward the middle of the screen. Held this far out the gun was
        // mostly off the edge of a splitscreen quarter — you could see that you were carrying
        // something and not what. Scaled rather than retyped so the low-and-right relationship
        // between the two axes survives the next adjustment.
        const float HoldInset = 0.85f;
        var hold = new Vector3(0.32f * HoldInset, -0.27f * HoldInset, 0f);

        // A real model if there is one, boxes if there is not.
        //
        // The fallback is the point rather than a nicety: twenty-three weapons will get models one
        // at a time, and every one of them has to be able to arrive without a code change and
        // without the other twenty-two looking broken while it does.
        if (BuildWeaponModel(w, layer, hold, NearZ)) return;

        var dark = Arena.Flat(new Color(0.15f, 0.17f, 0.21f));
        var darker = Arena.Flat(new Color(0.10f, 0.11f, 0.14f));

        void Part(Vector3 size, Vector3 at, Material mat)
            => ViewModel.AddChild(new MeshInstance3D
            {
                Mesh = new BoxMesh { Size = size },
                MaterialOverride = mat,
                Layers = layer,
                Position = hold + at,
            });

        // Overall length and bulk per silhouette. At this field of view length and bulk are most
        // of what registers; the distinguishing lumps below do the rest.
        (float length, float thick) = w.Silhouette switch
        {
            WeaponSilhouette.Shotgun => (0.52f, 0.150f),
            WeaponSilhouette.Sniper => (0.94f, 0.072f),
            WeaponSilhouette.Smg => (0.38f, 0.100f),
            WeaponSilhouette.Minigun => (0.62f, 0.190f),
            WeaponSilhouette.Blade => (0.86f, 0.040f),
            WeaponSilhouette.Launcher => (0.72f, 0.170f),   // a tube, and a fat one
            WeaponSilhouette.Portal => (0.46f, 0.130f),     // stubby, front-heavy
            WeaponSilhouette.Grapple => (0.40f, 0.115f),    // short, with a hook on the front
            _ => (0.58f, 0.105f),
        };

        if (w.Silhouette == WeaponSilhouette.Blade)
        {
            // A blade rather than a barrel: wide, flat and canted across the view, with a guard and
            // a hilt. Nothing about it should read as something you point at people.
            var edge = Graphics.Hot(new Color(0.72f, 0.98f, 0.95f), 1.6f);

            // Assembled as a chain from the hand forward — grip, then guard, then blade starting at
            // the guard's face. The first attempt placed each piece independently and canted the
            // blade steeply, which left it hanging in the air well clear of the hilt: three parts
            // in shot, none of them apparently attached to each other.
            const float GuardZ = NearZ - 0.09f;

            Part(new Vector3(0.050f, 0.160f, 0.050f), new Vector3(0f, -0.10f, NearZ - 0.02f), darker);
            Part(new Vector3(0.155f, 0.036f, 0.052f), new Vector3(0f, -0.010f, GuardZ), darker);
            Part(new Vector3(0.026f, 0.034f, 0.034f), new Vector3(0f, 0.030f, GuardZ), Arena.Flat(tint));

            var blade = new MeshInstance3D
            {
                Mesh = new BoxMesh { Size = new Vector3(0.026f, 0.125f, length) },
                MaterialOverride = Arena.Flat(new Color(0.78f, 0.86f, 0.92f)),
                Layers = layer,

                // Pivoted at the guard rather than at its own middle, so the cant swings the tip
                // and leaves the hilt where the hand is.
                Position = hold + new Vector3(0f, 0.01f, GuardZ),
            };
            blade.RotationDegrees = new Vector3(-5f, 0f, 9f);
            ViewModel.AddChild(blade);

            var span = new Node3D { Position = new Vector3(0f, 0f, -length * 0.5f) };
            blade.AddChild(span);

            span.AddChild(new MeshInstance3D
            {
                Mesh = new BoxMesh { Size = new Vector3(0.026f, 0.125f, length) },
                MaterialOverride = Arena.Flat(new Color(0.80f, 0.88f, 0.94f)),
                Layers = layer,
            });

            // The lit edge, parented alongside so it cants with the blade.
            span.AddChild(new MeshInstance3D
            {
                Mesh = new BoxMesh { Size = new Vector3(0.032f, 0.024f, length * 0.95f) },
                MaterialOverride = edge,
                Layers = layer,
                Position = new Vector3(0f, 0.054f, 0f),
            });

            // A blade flashes along its length rather than at a muzzle.
            muzzle = new MeshInstance3D
            {
                Mesh = new BoxMesh { Size = new Vector3(0.055f, 0.17f, length * 1.02f) },
                MaterialOverride = Graphics.Hot(new Color(0.85f, 1f, 0.98f), 5f),
                Layers = layer,
                Visible = false,
            };
            span.AddChild(muzzle);
            return;
        }

        Part(new Vector3(thick, thick, length), new Vector3(0f, 0f, NearZ - length * 0.5f), dark);

        // A stripe in the player's colour along the top, so in splitscreen you can tell at a
        // glance which view is yours without reading the HUD.
        Part(new Vector3(thick * 0.5f, thick * 0.35f, length * 0.55f),
             new Vector3(0f, thick * 0.62f, NearZ - length * 0.45f), Arena.Flat(tint));

        Part(new Vector3(thick * 0.8f, 0.16f, thick * 0.9f),
             new Vector3(0f, -0.10f, NearZ - 0.06f), darker);

        switch (w.Silhouette)
        {
            case WeaponSilhouette.Sniper:
                // A scope sitting proud of the receiver, which is the one feature that reads
                // instantly as "this shoots a long way".
                Part(new Vector3(thick * 0.7f, thick * 0.7f, length * 0.42f),
                     new Vector3(0f, thick * 1.5f, NearZ - length * 0.42f), darker);
                Part(new Vector3(thick * 0.9f, thick * 0.9f, thick * 0.5f),
                     new Vector3(0f, thick * 1.5f, NearZ - length * 0.20f), darker);
                break;

            case WeaponSilhouette.Shotgun:
                // A second tube slung under the first: the pump.
                Part(new Vector3(thick * 0.72f, thick * 0.60f, length * 0.82f),
                     new Vector3(0f, -thick * 0.68f, NearZ - length * 0.44f), darker);
                break;

            case WeaponSilhouette.Smg:
                // A magazine hanging below the grip. Short and busy.
                Part(new Vector3(thick * 0.55f, 0.13f, thick * 0.75f),
                     new Vector3(0f, -0.16f, NearZ - 0.14f), darker);
                break;

            case WeaponSilhouette.Minigun:
                // A ring of barrels in front of a heavy receiver. Six thin boxes on a circle read
                // as a rotating cluster far better than one fat tube does.
                for (int i = 0; i < 6; i++)
                {
                    float a = i * MathF.Tau / 6f;
                    Part(new Vector3(0.030f, 0.030f, length * 0.68f),
                         new Vector3(MathF.Cos(a) * thick * 0.42f,
                                     MathF.Sin(a) * thick * 0.42f,
                                     NearZ - length * 0.78f),
                         darker);
                }

                Part(new Vector3(thick * 1.25f, thick * 1.25f, length * 0.34f),
                     new Vector3(0f, 0f, NearZ - length * 0.24f), darker);
                break;

            case WeaponSilhouette.Launcher:
                // A flared muzzle and a warhead sitting visibly in it. The flare is what separates
                // a launcher from "a rifle but wider" at this field of view — a tube that opens
                // out at the far end reads as something that fires a rocket and nothing else does.
                Part(new Vector3(thick * 1.45f, thick * 1.45f, length * 0.16f),
                     new Vector3(0f, 0f, NearZ - length * 0.92f), darker);

                Part(new Vector3(thick * 0.55f, thick * 0.55f, length * 0.30f),
                     new Vector3(0f, 0f, NearZ - length * 0.86f),
                     Graphics.Hot(new Color(0.98f, 0.45f, 0.25f), 1.6f));

                // A sight up top, offset to the side of the tube the way a real one has to be.
                Part(new Vector3(thick * 0.30f, thick * 0.55f, length * 0.20f),
                     new Vector3(thick * 0.55f, thick * 0.70f, NearZ - length * 0.34f), darker);
                break;

            case WeaponSilhouette.Portal:
                // A ring on the front, lit. Nothing else in the game has a hole in the end that
                // glows, which is the whole job: you should be able to tell you are holding the
                // thing that does not shoot people without checking the HUD.
                var ringMat = Graphics.Hot(new Color(0.30f, 0.90f, 0.98f), 3.2f);

                for (int i = 0; i < 8; i++)
                {
                    float a = i * MathF.Tau / 8f;
                    Part(new Vector3(0.026f, 0.026f, thick * 0.5f),
                         new Vector3(MathF.Cos(a) * thick * 0.85f,
                                     MathF.Sin(a) * thick * 0.85f,
                                     NearZ - length * 0.98f),
                         ringMat);
                }

                // A pair of prongs running back from the ring to the body, so the ring reads as
                // mounted on the gun rather than floating in front of it.
                foreach (int side in new[] { -1, 1 })
                    Part(new Vector3(0.024f, 0.024f, length * 0.44f),
                         new Vector3(side * thick * 0.72f, thick * 0.30f, NearZ - length * 0.72f),
                         darker);
                break;

            case WeaponSilhouette.Grapple:
                // A claw sitting in the mouth: three prongs splayed open. It is the one shape that
                // says "this fires something that grabs" without a single word of HUD.
                var claw = Graphics.Hot(new Color(0.90f, 0.94f, 0.45f), 1.9f);

                for (int i = 0; i < 3; i++)
                {
                    float a = i * MathF.Tau / 3f + 0.4f;
                    var prong = new MeshInstance3D
                    {
                        Mesh = new BoxMesh { Size = new Vector3(0.026f, 0.026f, length * 0.34f) },
                        MaterialOverride = claw,
                        Layers = layer,
                        Position = hold + new Vector3(MathF.Cos(a) * thick * 0.55f,
                                                      MathF.Sin(a) * thick * 0.55f,
                                                      NearZ - length * 1.02f),
                    };

                    // Splayed outward from the barrel line, so they read as open jaws rather than
                    // as a second barrel cluster.
                    prong.RotationDegrees = new Vector3(MathF.Sin(a) * 18f, MathF.Cos(a) * 18f, 0f);
                    ViewModel.AddChild(prong);
                }

                // A drum of line under the receiver.
                Part(new Vector3(thick * 0.9f, thick * 0.9f, thick * 0.55f),
                     new Vector3(0f, -thick * 0.75f, NearZ - length * 0.45f), darker);
                break;
        }

        muzzle = new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(thick * 2.1f, thick * 2.1f, thick * 1.6f) },
            MaterialOverride = Graphics.Hot(new Color(1f, 0.88f, 0.52f), 6f),
            Layers = layer,
            Position = hold + new Vector3(0f, 0f, NearZ - length - thick * 0.6f),
            Visible = false,
        };
        ViewModel.AddChild(muzzle);
    }

    void TickViewModel(float dt)
    {
        if (muzzle == null) return;

        if (muzzleTimer > 0f)
        {
            muzzleTimer -= dt;
            if (muzzleTimer <= 0f) muzzle.Visible = false;
        }
    }

    /// <summary>Called by the match when this pawn's weapon goes off.</summary>
    public void OnFired()
    {
        ShotCounter++;
        ConsumeAmmo();
        if (muzzle == null) return;
        muzzleTimer = MuzzleFlashTime;
        muzzle.Visible = true;
    }

    /// <summary>Additive wash laid over a textured model while invulnerable. Built once, shared.</summary>
    static StandardMaterial3D? flashOverlay;

    void UpdateVisuals()
    {
        // Outside the body check, deliberately. It used to sit below it, so a pawn with no mesh
        // never turned to face anything — invisible while everything was boxes, and a real bug the
        // moment a rig could fail to build.
        Rotation = new Vector3(0f, -Facing, 0f);

        if (body == null) return;

        // Flash while invulnerable so a dodged shot reads as dodged rather than as a miss.
        if (body.MaterialOverride is StandardMaterial3D mat)
        {
            mat.AlbedoColor = Invulnerable ? Tint.Lightened(0.6f) : Tint;
            return;
        }

        // A faction model carries its own textured material, and recolouring that would throw away
        // the artwork. An additive overlay goes over the top instead and comes off when it ends.
        flashOverlay ??= new StandardMaterial3D
        {
            AlbedoColor = new Color(1f, 1f, 1f, 0.30f),
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            BlendMode = BaseMaterial3D.BlendModeEnum.Add,
        };

        // The flash borrows the torso's overlay slot for as long as it lasts, then hands it back to
        // the team wash. Without the hand-back, one dash would permanently strip a player's team
        // colour off their chest.
        body.MaterialOverlay = Invulnerable ? flashOverlay : teamOverlay;
    }

    /// <summary>Returns true if this shot killed the pawn.</summary>
    public bool TakeDamage(float amount)
    {
        if (!Alive || Invulnerable) return false;

        Health -= amount * DamageResist;

        // The last act. While it runs you cannot be killed, only worn down to a sliver.
        //
        // A duellist whose whole design is being frightening at low health has to be able to *get*
        // to low health, and at a hundred and fifty hit points the usual answer is that you die on
        // the way there. This buys the seconds in which the rest of the character exists.
        //
        // It stops short of the version that kills you when the timer runs out. Dying to a clock
        // with no counterplay reads as the ability betraying you rather than as a cost you took on,
        // and being left on one hit point is already a real bill — you survive the scene, and then
        // anything at all finishes you.
        if (LastStand && Health < 1f) Health = 1f;

        // Banked for Second Wind. Every pawn tracks it; only a Vessel can spend it.
        DamageBanked += amount;

        if (Health > 0f) return false;

        Health = 0f;
        Deaths++;
        RespawnIn = RespawnDelay;
        DeadFor = 0f;

        // The body stays visible and topples. Vanishing on death gave no read on where someone
        // went down, which matters when you are trying to work out who shot you from where.
        return true;
    }

    public const float RespawnDelay = 2.4f;

    // ---- vehicles ----

    /// <summary>The vehicle this pawn is driving, or null when on foot.</summary>
    public Vehicle? Riding { get; private set; }

    public bool InVehicle => Riding != null;

    /// <summary>
    /// Climbs aboard. The pawn stops simulating itself — no gravity, no collision, no shooting —
    /// and rides the hull, because two things trying to move the same player fight each other.
    /// </summary>
    public void EnterVehicle(Vehicle v)
    {
        Riding = v;
        ReleaseGrapple();
        Velocity = Vector3.Zero;
        SetCollisionLayerValue(1, false);
        SetCollisionMaskValue(1, false);
        if (rig != null) rig.Visible = false;
    }

    public void ExitVehicle(Vector3 at)
    {
        Riding = null;
        GlobalPosition = at;
        Velocity = Vector3.Zero;
        SetCollisionLayerValue(1, true);
        SetCollisionMaskValue(1, true);
        if (rig != null) rig.Visible = true;
    }

    /// <summary>Keeps the passenger glued to the seat while riding.</summary>
    public void RideAlong()
    {
        if (Riding is not { } v) return;
        GlobalPosition = v.GlobalPosition;
        Facing = v.Facing;
    }

    public void Respawn(Vector3 at)
    {
        Health = MaxHealth;
        GlobalPosition = at;
        Velocity = Vector3.Zero;
        dashTime = 0f;
        dashRise = 0f;
        ReleaseGrapple();
        classCooldown = 0f;
        ClassBuffTime = 0f;
        crownPowerCooldown = 0f;
        CrownPowerTime = 0f;
        DamageResist = 1f;

        // Cleared, along with the others. It was the one cooldown that survived death, so you
        // could respawn with no dash for a second and a half through no fault of your own — and
        // it made a test that dashes twice in a row silently measure a pawn simply falling.
        dashCooldown = 0f;
        SlipTime = 0f;
        fireCooldown = 0f;
        meleeCooldown = 0f;
        MeleeSwing = 0f;
        invuln = SpawnProtection;
        DeadFor = 0f;
        SlideTime = 0f;
        Knockback = Vector3.Zero;
        timeOffGround = 0f;
        jumpBuffered = 0f;
        ResetLoadout();
        Sprinting = Crouching = Ads = false;
        CurrentEyeHeight = EyeHeight;
        ApplyHeight(Height);

        // Undo the topple, or the respawned pawn would still be lying on its side.
        if (rig != null)
        {
            rig.Visible = true;
            rig.Rotation = Vector3.Zero;
            rig.Position = Vector3.Zero;
        }

        if (bodyMesh?.MaterialOverride is StandardMaterial3D mat)
            mat.EmissionEnergyMultiplier = 0.45f;
    }

    public const float SpawnProtection = 1.2f;

    // ---- render layers ----
    //
    // Godot exposes 20. The allocation used to be one body layer and one view-model layer per
    // roster slot, which quietly capped the whole game at four fighters — a fifth pawn would have
    // taken a bit the view models were using.
    //
    // Only a *human* needs a body layer of its own, and only so their own first-person camera can
    // cull the body it is sitting inside. No camera ever needs to hide a bot from itself, so every
    // bot shares one layer however many of them there are. Four humans plus the shared bot layer
    // is nine of the twenty, and the roster is no longer bounded by the renderer.

    /// <summary>Render layer for a pawn's world body. Layer 1 is shared world geometry.</summary>
    public static uint VisualLayerFor(int viewIndex)
        => viewIndex < 0 ? BotBodyLayer : 1u << (viewIndex + 1);

    /// <summary>The one layer every bot's body lives on. Nothing ever culls it.</summary>
    public const uint BotBodyLayer = 1u << 9;

    /// <summary>
    /// Render layer for a human's first-person weapon, which only their own camera may see. Human
    /// bodies occupy bits 1-4, their view models bits 5-8, and every bot body shares bit 9.
    /// </summary>
    public static uint ViewModelLayerFor(int viewIndex)
        => viewIndex < 0 ? 0u : 1u << (viewIndex + 5);

    const uint AllLayers = (1u << 20) - 1;
    const uint AllViewModelLayers = 0b1111u << 5;

    /// <summary>
    /// Cull mask for a first-person camera: everything except the body it is inside, and none of
    /// the view models except its own. Godot exposes 20 render layers, so the mask is bounded to
    /// those bits.
    /// </summary>
    public static uint FirstPersonCullMask(int viewIndex)
        => (AllLayers & ~VisualLayerFor(viewIndex) & ~AllViewModelLayers) | ViewModelLayerFor(viewIndex);
}
