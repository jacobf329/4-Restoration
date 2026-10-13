using Godot;
using HitboxClone;

namespace Battlefront;

/// <summary>
/// A soldier on the ground, and nothing more than that.
///
/// The movement here is deliberately short of verbs. The game this is cloning gives infantry a
/// walk, a sprint, a crouch, a roll and an ordinary jump, and every one of those keeps you on
/// the floor — which is what makes the floor plan the game. A jetpack turns a corridor map into
/// an open one: the catwalk over the reactor stops being a decision the moment you can fly over
/// the shaft, the gallery in the control room stops being high ground, and the whole hub-and-
/// spoke layout collapses into "go in a straight line". So there is no jetpack, no double jump
/// and no hover, and their absence is a design decision rather than a thing not written yet.
///
/// Gravity is strong and the jump is low on purpose. You jump here to get over a crate, not to
/// cross a room.
/// </summary>
public sealed partial class Trooper : CharacterBody3D
{
    public const float Height = 1.8f;
    public const float CrouchHeight = 1.1f;
    public const float Radius = 0.4f;

    public const float WalkSpeed = 6.2f;
    public const float SprintSpeed = 9.6f;
    public const float CrouchSpeed = 3.0f;

    /// <summary>Hard and fast. A floaty arc is the first thing that stops a shooter feeling grounded.</summary>
    public const float Gravity = 26f;
    public const float JumpSpeed = 7.2f;

    /// <summary>How quickly the soldier reaches the speed being asked for, on the ground and off it.</summary>
    const float GroundGrip = 14f;
    const float AirGrip = 2.2f;

    /// <summary>Which way the body is facing, in radians. The camera owns its own yaw.</summary>
    public float Facing;

    public int Team;
    public bool Crouching;

    public const float MaxHealth = 100f;

    public float Health = MaxHealth;
    public bool Alive = true;

    /// <summary>Seconds until this body is cleared away and the soldier respawns.</summary>
    public float DeadFor;

    /// <summary>Where the soldier is pointing their weapon. Not the same as which way they walk.</summary>
    public float AimYaw;

    /// <summary>Counts down between shots. See Battle.</summary>
    public float ReloadIn;

    /// <summary>Muzzle, roughly: eye height, a little forward.</summary>
    public Vector3 Muzzle => GlobalPosition + Vector3.Up * EyeHeight;

    /// <summary>Centre of mass, which is what gets shot at.</summary>
    public Vector3 Chest => GlobalPosition + Vector3.Up * (Crouching ? 0.75f : 1.25f);

    public void Hurt(float amount)
    {
        if (!Alive) return;

        Health -= amount;
        if (Health > 0f) return;

        Alive = false;
        Health = 0f;
        Visible = false;

        // Out of the way of the living. A corpse that still collides is a corpse that blocks a
        // doorway for the rest of the match, and there is no ragdoll here to make it worth it.
        ProcessMode = ProcessModeEnum.Disabled;
        SetCollisionLayerValue(1, false);
        SetCollisionMaskValue(1, false);
    }

    public void Revive(Vector3 at)
    {
        Alive = true;
        Health = MaxHealth;
        DeadFor = 0f;
        Visible = true;
        ProcessMode = ProcessModeEnum.Inherit;
        SetCollisionLayerValue(1, true);
        SetCollisionMaskValue(1, true);
        GlobalPosition = at;
        Velocity = Vector3.Zero;

        // Or the respawn teleport counts as one frame of travelling the length of the station.
        GroundSpeed = 0f;
        placed = false;
    }

    CollisionShape3D shape = null!;
    CapsuleShape3D capsule = null!;
    Node3D? body;

    /// <summary>The worn model's own animation player, and the two clips worth having.</summary>
    AnimationPlayer? anim;
    string? animWalk, animRun;

    /// <summary>
    /// Nodes stuck to the two foot bones, for --gait. Null when there is no model.
    ///
    /// BoneAttachment3D rather than Skeleton3D.GetBoneGlobalPose, which was the first attempt
    /// and quietly did not work: the poses it returns did change frame to frame, but putting
    /// them through the skeleton's transform gave a foot that tracked the body rigidly, so
    /// every rate from half speed to five times it measured exactly the same skate. A probe
    /// that returns the same answer for every input is not measuring its input. An attachment
    /// is the engine's own supported way to ask where a bone ended up, and it is driven by the
    /// same pass that draws the mesh, so it cannot disagree with what is on screen.
    /// </summary>
    BoneAttachment3D? leftFoot, rightFoot;

    /// <summary>
    /// Where the foot that is currently on the ground is, in world space, or null.
    ///
    /// This exists so the gait can be MEASURED. A planted foot does not move: if the clip is
    /// playing at the rate the body is travelling, the foot in contact with the deck stays
    /// exactly where it is from frame to frame, and if it is not, it slides. That slide is the
    /// whole of what "the feet skate" means, it is a number rather than an opinion, and a still
    /// screenshot cannot show it at all.
    /// </summary>
    public Vector3? PlantedFoot
    {
        get
        {
            if (leftFoot == null || rightFoot == null) return null;

            var l = leftFoot.GlobalPosition;
            var r = rightFoot.GlobalPosition;

            return l.Y <= r.Y ? l : r;
        }
    }

    /// <summary>How far apart the feet are, which is zero on a rig that is not animating.</summary>
    public float FootSpread => leftFoot == null || rightFoot == null
        ? 0f
        : leftFoot.GlobalPosition.DistanceTo(rightFoot.GlobalPosition);

    /// <summary>Hang a node off the first of these bones the rig actually has.</summary>
    static BoneAttachment3D? Attach(Skeleton3D skeleton, params string[] bones)
    {
        foreach (string bone in bones)
        {
            if (skeleton.FindBone(bone) < 0) continue;

            var at = new BoneAttachment3D { BoneName = bone };
            skeleton.AddChild(at);
            at.BoneName = bone;
            return at;
        }

        return null;
    }

    static Skeleton3D? FindSkeleton(Node n)
    {
        if (n is Skeleton3D s) return s;

        foreach (var child in n.GetChildren())
            if (FindSkeleton(child) is { } found) return found;

        return null;
    }

    /// <summary>Which of the generated character models this soldier wears.</summary>
    public string Model = "vessels";

    /// <summary>
    /// How fast the walk and run clips are authored to carry a body, in metres per second.
    ///
    /// Measured, not guessed, and the measurement is what --gait exists for. The cycles are
    /// in-place - the root never translates - so there is no speed written in the file to read.
    /// What there is instead is a planted foot, which does not move when the clip and the body
    /// agree and slides when they do not. Holding the animation at rate 1 while walking at a
    /// known speed makes the whole difference show up as that slide, and the clip's own speed
    /// falls straight out of it.
    ///
    /// These were WalkSpeed and SprintSpeed, which is a guess dressed as a constant: the game's
    /// walk is 6.2 m/s and this walk cycle carries 1.76, so the feet were running at under a
    /// third of floor speed and every soldier in the station skated.
    ///
    /// Measured at 0.94m and 1.35m of stride over cycles of 1.067s and 0.667s. See --gait.
    /// </summary>
    public const float WalkClipSpeed = 1.76f;

    public const float RunClipSpeed = 4.06f;

    /// <summary>
    /// The fastest the leg cycle is allowed to be driven.
    ///
    /// There has to be a ceiling because the alternative is worse than a little skate: a clip
    /// played at three times its rate is not a fast walk, it is a blur, and a station full of
    /// soldiers whose legs are a blur looks far more broken than a station full of soldiers
    /// whose feet creep a few centimetres per stride. The game's speeds are set so that the
    /// sprint lands inside this and only the walk is clipped by it.
    /// </summary>
    public const float MaxCycleRate = 2.6f;

    /// <summary>Where the leg cycle has got to, in seconds. For --gait.</summary>
    public double CycleAt => anim?.CurrentAnimationPosition ?? -1.0;

    /// <summary>How long one cycle of the clip currently playing is. For --gait.</summary>
    public double CycleLength => anim?.CurrentAnimationLength ?? 0.0;

    /// <summary>The rate the cycle is being played at. For --gait.</summary>
    public float CycleRate => anim == null ? 0f : (float)anim.SpeedScale;

    /// <summary>Which clip is playing, by name. For --gait.</summary>
    public string Cycle => anim?.CurrentAnimation.ToString() ?? "";

    /// <summary>The speed TickAnimation last saw, which is what picks the clip. For --gait.</summary>
    public float LastAnimSpeed;

    /// <summary>
    /// How fast this soldier is actually travelling across the floor, from where they were.
    ///
    /// Not Velocity, and the difference is not small. Velocity after MoveAndSlide is what the
    /// engine resolved the motion to, not what the body did: a soldier commanded along the deck
    /// at eight metres a second covers eight metres a second and reports four and a half. So
    /// the walk/run choice never once picked the run clip - the threshold is 1.15x the walk
    /// speed and the number being compared against it was 56% of the truth - and the cycle rate
    /// was being matched to a speed the soldier was not going.
    ///
    /// Position over time cannot disagree with where the soldier ended up, because it IS where
    /// the soldier ended up.
    /// </summary>
    public float GroundSpeed { get; private set; }

    bool placed;

    /// <summary>--gait: say what clips were found. Off in play.</summary>
    public static bool Reports;

    /// <summary>--gait: hold the cycle at a fixed rate instead of matching it. Zero to match.</summary>
    public float ForceRate;

    /// <summary>--gait: walk at exactly this many metres per second. Zero for the real speeds.</summary>
    public float SpeedOverride;

    public float EyeHeight => (Crouching ? CrouchHeight : Height) - 0.25f;

    public override void _Ready()
    {
        capsule = new CapsuleShape3D { Height = Height, Radius = Radius };
        shape = new CollisionShape3D { Shape = capsule, Position = Vector3.Up * Height * 0.5f };
        AddChild(shape);

        // Steps rather than walls. The station is built out of boxes and a 0.4m lip in a doorway
        // should be walked over, not stopped at.
        FloorMaxAngle = Mathf.DegToRad(50f);
        FloorSnapLength = 0.5f;

        Wear();
    }

    /// <summary>
    /// Put a body on the soldier, scaled so the model is exactly as tall as the capsule.
    ///
    /// Scaled rather than assumed: the generated characters are not the same size in their own
    /// units, so a fixed scale leaves one faction knee-high. CharacterModels reports the source
    /// height for precisely this.
    /// </summary>
    void Wear()
    {
        if (CharacterModels.Instance(Model, out float source) is not { } worn) return;
        if (source <= 0.01f) return;

        worn.Scale = Vector3.One * (Height / source);
        body = new Node3D();
        body.AddChild(worn);
        AddChild(body);

        anim = CharacterModels.FindPlayer(worn);
        PickAnimations();

        if (FindSkeleton(worn) is { } skeleton)
        {
            leftFoot = Attach(skeleton, "LeftToeBase", "LeftFoot");
            rightFoot = Attach(skeleton, "RightToeBase", "RightFoot");
        }
    }

    /// <summary>
    /// Find the walk and the run among whatever clips the export happens to carry.
    ///
    /// By substring rather than by exact name, because the names come out of whatever rig the
    /// model was retargeted onto and are not ours to choose - these two arrive as
    /// "Armature|walking_man|baselayer" and "Armature|running|baselayer". Matching on "run"
    /// and taking everything else as the walk is what the old game does and it has survived
    /// four sets of characters.
    /// </summary>
    void PickAnimations()
    {
        if (anim == null) return;

        foreach (var lib in anim.GetAnimationLibraryList())
            foreach (var name in anim.GetAnimationLibrary(lib).GetAnimationList())
            {
                string full = lib.ToString().Length > 0 ? $"{lib}/{name}" : name.ToString();

                if (full.ToLowerInvariant().Contains("run")) animRun ??= full;
                else animWalk ??= full;
            }

        if (Reports)
            GD.Print($"  clips on {Model}: walk={animWalk ?? "none"}  run={animRun ?? "none"}");

        // One clip serves for both rather than freezing. A soldier running with a walk cycle
        // played fast is wrong; a soldier sliding is broken.
        animWalk ??= animRun;
        animRun ??= animWalk;
    }

    /// <summary>
    /// Drive the body from how fast it is actually moving.
    ///
    /// Speed-matched rather than switched at a threshold: the clip plays at a rate proportional
    /// to ground speed, so the feet keep up with the floor instead of skating, and slowing down
    /// slows the cycle instead of snapping to a different one.
    ///
    /// The idle case is the one that matters most here and it is why this exists at all. With
    /// nothing driving the player, every one of the forty soldiers stood in the rig's bind pose
    /// - arms straight out, legs together - and SLID across the station at six metres a second.
    /// The models have carried a walk and a run the whole time; the new game simply never asked
    /// for them. Holding the walk clip still is not an idle animation, but a soldier paused
    /// mid-stride reads as a soldier standing, and a T-pose reads as a bug.
    /// </summary>
    void TickAnimation()
    {
        if (anim == null) return;

        if (!Alive) { anim.Pause(); return; }

        float speed = GroundSpeed;
        LastAnimSpeed = speed;

        if (speed < 0.4f)
        {
            if (animWalk != null && anim.CurrentAnimation != animWalk) anim.Play(animWalk);

            // Parked at a passing pose rather than at frame zero. The start of a walk cycle is
            // the widest part of the stride, so a crowd of idle soldiers all stood at frame
            // zero reads as a crowd caught mid-lunge; a quarter of the way in the legs are
            // under the body, which is close enough to standing.
            if (!anim.CurrentAnimation.IsEmpty && anim.CurrentAnimationPosition < 0.001)
                anim.Seek(anim.CurrentAnimationLength * 0.25, update: true);

            anim.Pause();
            return;
        }

        // Whichever clip was authored CLOSEST to how fast this soldier is actually going,
        // rather than a threshold hung off the game's own walk speed.
        //
        // This is the whole thing, and it is a consequence of the two clips having been
        // measured. They are an ordinary human walk at 1.76 m/s and an ordinary human jog at
        // 4.06; the game's walk is 6.2 and its sprint 9.6, because a shooter's "walking" is
        // nobody's idea of walking. Picking the walk clip for the game's walk - which is what a
        // threshold at 1.15x the walk speed does - asks a 1.76 m/s cycle to carry a body going
        // three and a half times that, and no amount of playing it faster makes that look like
        // anything but a wind-up toy.
        //
        // Choosing by nearness instead, every speed the game actually uses lands on a clip it
        // can carry: the walk runs the jog cycle at 1.5x, the sprint at 2.4x, and a crouch -
        // which is the only movement here slow enough to be a walk - runs the walk cycle at
        // 1.7x. All three are exact matches, so nothing skates, and none of them is fast enough
        // to blur.
        bool running = !Crouching
                    && Mathf.Abs(speed - RunClipSpeed) < Mathf.Abs(speed - WalkClipSpeed);

        string? want = running ? animRun : animWalk;
        if (want == null) return;

        if (anim.CurrentAnimation != want || !anim.IsPlaying()) anim.Play(want);

        // Matched against what the CLIP travels at, not against what the game's walk speed
        // happens to be. Those are different numbers and the difference is the skate.
        float reference = running ? RunClipSpeed : WalkClipSpeed;

        anim.SpeedScale = ForceRate > 0f
            ? ForceRate
            : Mathf.Clamp(speed / reference, 0.35f, MaxCycleRate);
    }

    /// <summary>
    /// What a soldier is being told to do this frame, by a pad or by a brain.
    ///
    /// One struct for both so there is exactly one movement implementation. A bot that moves
    /// through its own code path is a bot that feels different to fight, and every difference
    /// between the two is a bug waiting to be found by the player rather than by the harness.
    /// </summary>
    public struct Order
    {
        /// <summary>Where to go, in world space, flattened. Zero to stand still.</summary>
        public Vector3 Wish;

        public bool Sprint;
        public bool Crouch;
        public bool Jump;
    }

    /// <summary>Turn a pad into an order, read relative to where the camera is looking.</summary>
    public static Order FromPad(InputDevice pad, float cameraYaw)
    {
        var stick = pad.Move;
        if (stick.LengthSquared() > 1f) stick = stick.Normalized();

        var forward = new Vector3(Mathf.Cos(cameraYaw), 0f, Mathf.Sin(cameraYaw));
        var right = new Vector3(-forward.Z, 0f, forward.X);

        return new Order
        {
            // Godot's stick Y is up-negative, so forward is -Y.
            Wish = forward * -stick.Y + right * stick.X,
            Sprint = pad.SprintHeld && stick.Y < -0.3f,
            Crouch = pad.CrouchHeld,
            Jump = pad.JumpPressed,
        };
    }

    /// <summary>One frame of movement.</summary>
    public void Step(float dt, Order order)
    {
        bool grounded = IsOnFloor();

        SetCrouch(order.Crouch);

        var wish = order.Wish;
        if (wish.LengthSquared() > 1f) wish = wish.Normalized();
        wish.Y = 0f;

        float speed = SpeedOverride > 0f ? SpeedOverride
                    : Crouching ? CrouchSpeed
                    : order.Sprint ? SprintSpeed
                    : WalkSpeed;

        var want = wish * speed;
        var flat = new Vector3(Velocity.X, 0f, Velocity.Z);

        // Far less authority in the air than on the ground. Full air control is the other half of
        // what makes a shooter feel weightless, and it is why a jump here commits you.
        flat = flat.Lerp(want, 1f - Mathf.Exp(-(grounded ? GroundGrip : AirGrip) * dt));

        float vy = Velocity.Y;

        if (grounded)
        {
            // Pinned down rather than zeroed, so the body stays glued going down a step.
            if (vy < 0f) vy = -2f;
            if (order.Jump && !Crouching) vy = JumpSpeed;
        }
        else
        {
            vy -= Gravity * dt;
        }

        Velocity = new Vector3(flat.X, vy, flat.Z);

        var from = GlobalPosition;
        MoveAndSlide();

        // Smoothed a little, because a single frame against a step or a doorway frame reads as
        // a full stop and would stutter the legs.
        float went = dt > 0.0001f
            ? new Vector2(GlobalPosition.X - from.X, GlobalPosition.Z - from.Z).Length() / dt
            : 0f;

        GroundSpeed = placed ? Mathf.Lerp(GroundSpeed, went, 1f - Mathf.Exp(-18f * dt)) : went;
        placed = true;

        // Face the way you are moving, and snap to the camera while standing still so the soldier
        // does not stand sideways on.
        if (wish.LengthSquared() > 0.04f)
        {
            float want2 = Mathf.Atan2(wish.Z, wish.X);
            Facing = Mathf.LerpAngle(Facing, want2, 1f - Mathf.Exp(-12f * dt));
        }

        // The model faces +X in its own space, and the body sinks with the crouch so the head
        // goes down rather than the feet going through the floor.
        if (body != null)
        {
            body.Rotation = new Vector3(0f, -Facing, 0f);
            body.Scale = Vector3.One with { Y = Crouching ? CrouchHeight / Height : 1f };
        }

        TickAnimation();
    }

    void SetCrouch(bool want)
    {
        if (want == Crouching) return;

        // Standing up into a ceiling is how a soldier ends up inside the roof. Only the crouch
        // is unconditional; getting back up has to be allowed by the room.
        if (!want && !RoomToStand()) return;

        Crouching = want;
        capsule.Height = want ? CrouchHeight : Height;
        shape.Position = Vector3.Up * capsule.Height * 0.5f;
    }

    bool RoomToStand()
    {
        var space = GetWorld3D().DirectSpaceState;

        var probe = new PhysicsShapeQueryParameters3D
        {
            Shape = new CapsuleShape3D { Height = Height, Radius = Radius - 0.02f },
            Transform = new Transform3D(Basis.Identity, GlobalPosition + Vector3.Up * Height * 0.5f),
            Exclude = new Godot.Collections.Array<Rid> { GetRid() },
        };

        return space.IntersectShape(probe, 1).Count == 0;
    }
}
