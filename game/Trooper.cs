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

    CollisionShape3D shape = null!;
    CapsuleShape3D capsule = null!;
    Node3D? body;

    /// <summary>Which of the generated character models this soldier wears.</summary>
    public string Model = "vessels";

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
    }

    /// <summary>
    /// One frame of movement, driven by a pad.
    ///
    /// <paramref name="cameraYaw"/> is which way the view is pointing: the stick is read relative
    /// to the camera, because a soldier walks where you are looking and not where their feet
    /// happen to be aimed.
    /// </summary>
    public void Step(float dt, InputDevice pad, float cameraYaw)
    {
        bool grounded = IsOnFloor();

        SetCrouch(pad.CrouchHeld);

        // Stick to world direction, flattened onto the floor plane.
        var stick = pad.Move;
        if (stick.LengthSquared() > 1f) stick = stick.Normalized();

        var forward = new Vector3(Mathf.Cos(cameraYaw), 0f, Mathf.Sin(cameraYaw));
        var right = new Vector3(-forward.Z, 0f, forward.X);

        // Godot's stick Y is up-negative, so forward is -Y.
        var wish = forward * -stick.Y + right * stick.X;

        float speed = Crouching ? CrouchSpeed
                    : pad.SprintHeld && stick.Y < -0.3f ? SprintSpeed
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
            if (pad.JumpPressed && !Crouching) vy = JumpSpeed;
        }
        else
        {
            vy -= Gravity * dt;
        }

        Velocity = new Vector3(flat.X, vy, flat.Z);
        MoveAndSlide();

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
