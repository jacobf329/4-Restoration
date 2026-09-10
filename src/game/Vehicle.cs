using System;
using System.Collections.Generic;
using Godot;

namespace HitboxClone;

public enum VehicleKind { Car, Tank, Plane }

/// <summary>Everything that differs between the three vehicles. All tuning lives here.</summary>
public sealed class VehicleDef
{
    public VehicleKind Kind;
    public string Name = "";

    public float Health = 300f;
    public float MaxSpeed = 26f;
    public float Accel = 18f;
    public float TurnRate = 1.9f;

    /// <summary>Flying vehicles ignore gravity and answer to a pitch axis instead.</summary>
    public bool Flies;

    /// <summary>Tracked hulls turn on the spot; wheeled and winged ones need speed to steer.</summary>
    public bool NeutralSteer;

    /// <summary>Damage dealt by driving into someone. Zero for the plane, which is airborne.</summary>
    public float RamDamage;

    /// <summary>Null when the vehicle has no gun of its own.</summary>
    public WeaponDef? Gun;

    public Vector3 HalfExtents = new(1.9f, 0.9f, 3.4f);
    public Color Tint = new(0.55f, 0.60f, 0.68f);

    /// <summary>Where the driver sits, relative to the hull.</summary>
    public float EyeHeight = 1.6f;

    // ---- optional models ----
    //
    // Empty means "draw the box". A vehicle with a model still keeps every number above: the model
    // is scaled onto the collision box rather than the box being rebuilt around the model, so what
    // you see and what you drive into things with stay the same shape.

    /// <summary>Base name under <c>assets/vehicles</c> of the body, or empty for the box.</summary>
    public string HullModel = "";

    /// <summary>Base name of the part that rides the turret pivot. Empty for a plain barrel.</summary>
    public string TurretModel = "";

    /// <summary>
    /// Per-part rotation, because the exports do not agree with each other on which way is forward.
    /// The vehicle's local +X is its nose; each model is turned to match that.
    /// </summary>
    public Vector3 HullTilt;
    public Vector3 TurretTilt;

    /// <summary>Turret height as a multiple of the hull's half height. Sized by height, not length.</summary>
    public float TurretHeight = 1.4f;

    /// <summary>Nudges for a model whose origin does not agree with the simulation's.</summary>
    public Vector3 HullOffset;
    public Vector3 TurretOffset;
}

/// <summary>
/// The three drivable vehicles.
///
/// They are deliberately arcade rather than simulated: a car that needs to be driven well is a
/// second game to learn, and this one is about the shooting. Each is a hull that accelerates along
/// its own facing, and the differences between them are speed, toughness and armament.
/// </summary>
public static class Vehicles
{
    /// <summary>
    /// The butter car.
    ///
    /// Same hull, same numbers, new job. It is the only vehicle with no gun, and driving a fast
    /// unarmed box around a shooter was transport rather than a play — the trail it now lays is
    /// the weapon, and it is one you aim by choosing where to *be* rather than where to point.
    /// See <c>Match.StepButter</c> for what crossing one does.
    ///
    /// The tint is the trail's own colour, deliberately: a slick and the thing that laid it should
    /// be obviously the same substance, so the first time somebody goes over backwards they can
    /// see what did it without being told.
    /// </summary>
    public static readonly VehicleDef Car = new()
    {
        Kind = VehicleKind.Car,
        Name = "Butter Car",
        Health = 260f,
        MaxSpeed = 34f,
        Accel = 26f,
        TurnRate = 2.3f,
        RamDamage = 55f,
        Gun = null,

        // Reproportioned to the model, rather than the model being stretched onto the box.
        //
        // The old box was 6.4m long, 1.6m tall and 3.4m wide - a shape authored as a silhouette
        // before there was anything to look at, and a flat wide pancake next to any real vehicle.
        // The buggy is 1 : 0.49 : 0.66 and the box was 1 : 0.25 : 0.53, and a hull whose model is
        // twice as tall as its collision is worse than one that is simply the wrong size.
        //
        // Smaller in every dimension, which is also the safe direction to move it: a shorter,
        // narrower hull fits through more gaps than before, never fewer, so nothing that used to
        // be drivable stops being so.
        HalfExtents = new Vector3(2.4f, 1.1f, 1.5f),
        Tint = new Color(0.99f, 0.90f, 0.46f),
        EyeHeight = 1.5f,

        HullModel = "car_hull",

        // The model's nose points down its own -X, measured from a shaded render and confirmed by
        // the height profile along its length: the lowest, longest slope is at the -X end and the
        // roll cage sits at 0.61 of the way toward +X, which is a bonnet in front of a cockpit.
        // A half turn puts that nose on the +X the simulation drives along.
        HullTilt = new Vector3(0f, 180f, 0f),

        // Seated so the tyres meet the ground. The model is hung from its own centre at half the
        // box height, which lands correctly only when the box is exactly half the model's height -
        // true of the tank by luck rather than design. This is the 7cm the buggy is out by.
        HullOffset = new Vector3(0f, 0.07f, 0f),
    };

    public static readonly VehicleDef Tank = new()
    {
        Kind = VehicleKind.Tank,
        Name = "Tank",
        Health = 700f,
        MaxSpeed = 15f,
        Accel = 11f,
        TurnRate = 1.2f,
        RamDamage = 80f,
        // A shell, not a bullet. The direct hit is deliberately the smaller half of it: the cannon
        // is meant to be aimed at the ground under someone rather than threaded at them, which is
        // what makes a two-second reload worth carrying.
        Gun = new WeaponDef
        {
            Name = "Cannon",
            Damage = 70f,
            FireInterval = 2.1f,
            SpreadDeg = 0.6f,
            Range = 140f,
            ProjectileSpeed = 95f,
            Recoil = 0.09f,
            AdsFov = 40f,
            BlastDamage = 135f,
            BlastRadius = 8.5f,
            Ammo = 0,
        },
        NeutralSteer = true,
        HalfExtents = new Vector3(4.0f, 1.2f, 2.4f),
        Tint = new Color(0.44f, 0.52f, 0.38f),
        EyeHeight = 2.4f,

        // Two exports, because the gun has to traverse independently of the body. The turret rides
        // the same pivot the box barrel always did.
        HullModel = "tank_hull",
        TurretModel = "tank_turret",

        // Measured, not guessed. The hull is 0.64 x 0.30 x 1.00 in its own units, so it lies along
        // Z and needs turning onto the nose axis; the turret is 1.00 x 0.35 x 0.58 and already lies
        // along X. Rotating both the same way put the gun across the hull.
        //
        // The turret lies along X and points down *negative* X, which bounds cannot tell you —
        // an axis-aligned box is identical either way round. It took someone looking at it to say
        // the gun was backwards.
        HullTilt = new Vector3(0f, -90f, 0f),
        TurretTilt = new Vector3(0f, 180f, 0f),

        // The turret export carries its own mounting post below the body. Seated by its bounds it
        // stands on that post like a periscope; dropped by most of a metre the post goes inside the
        // hull where it belongs and the body sits on the deck.
        TurretHeight = 1.6f,
        TurretOffset = new Vector3(0f, -0.85f, 0f),
    };

    public static readonly VehicleDef Plane = new()
    {
        Kind = VehicleKind.Plane,
        Name = "Plane",
        Health = 180f,
        MaxSpeed = 48f,
        Accel = 22f,
        TurnRate = 1.5f,
        Flies = true,
        RamDamage = 0f,
        Gun = new WeaponDef
        {
            Name = "Wing Guns",
            Damage = 11f,
            FireInterval = 0.07f,
            SpreadDeg = 2.4f,
            Range = 90f,
            ProjectileSpeed = 170f,
            Recoil = 0.004f,
            AdsFov = 50f,
            Ammo = 0,
        },
        HalfExtents = new Vector3(3.0f, 0.6f, 4.2f),
        Tint = new Color(0.78f, 0.86f, 0.94f),
        EyeHeight = 1.2f,
    };

    /// <summary>Every vehicle the game knows about, including ones not currently in rotation.</summary>
    public static readonly VehicleDef[] All = { Car, Tank, Plane };

    /// <summary>
    /// The vehicles that actually appear on maps.
    ///
    /// The plane is held back deliberately. It cruises at forty-eight metres a second, which crosses
    /// this arena in under six seconds — it needs a map with room to turn around in, and until there
    /// is one it is a novelty that spends most of its life against a wall. The definition stays so
    /// nothing has to be rebuilt when that map exists.
    /// </summary>
    public static readonly VehicleDef[] Spawnable = { Car, Tank };

    public static VehicleDef ByIndex(int i)
        => Spawnable[((i % Spawnable.Length) + Spawnable.Length) % Spawnable.Length];
}

/// <summary>
/// A drivable hull.
///
/// Simulated manually rather than with Godot's VehicleBody3D: the rest of the game moves things by
/// integrating a velocity and calling MoveAndSlide, the headless harness depends on that being
/// deterministic, and a wheel simulation would be a second physics model to keep honest.
/// </summary>
public partial class Vehicle : CharacterBody3D
{
    public VehicleDef Def = Vehicles.Car;

    /// <summary>
    /// Counts down to the next dollop of butter. Only a car uses it.
    ///
    /// Held on the hull rather than in the match's own bookkeeping because it belongs to this
    /// vehicle: a match-side dictionary keyed on the rig would have to be cleaned up when a car is
    /// wrecked and respawned, and forgetting that is a leak nobody would notice.
    /// </summary>
    public float ButterTimer;

    /// <summary>Who is driving, or null when it is parked and enterable.</summary>
    public Pawn? Driver { get; private set; }

    public float Health;
    public bool Alive => Health > 0f;

    /// <summary>Heading in radians on the XZ plane, and pitch for the plane.</summary>
    public float Facing;
    public float Pitch;

    float fireCooldown;
    float exitLock;

    MeshInstance3D? hull;
    Node3D? barrel;

    const float Gravity = 22f;

    /// <summary>Radians per second of pitch at full stick. A rate, not an angle.</summary>
    const float PlanePitchRate = 1.5f;

    /// <summary>Seconds of muzzle flash left, so firing reads on screen as well as in the sim.</summary>
    public float MuzzleFlash;

    public const float MuzzleFlashTime = 0.12f;

    /// <summary>How far through the reload, 0 just fired to 1 ready. Drives the HUD gauge.</summary>
    public float ReloadProgress
        => Def.Gun == null ? 1f : 1f - MathU.Clamp01(fireCooldown / Def.Gun.FireInterval);

    /// <summary>Rounds this hull has put out. Exists so the harness can prove a mounted gun fires.</summary>
    public int ShotsFired;

    /// <summary>Whether the gun has finished reloading. Drives the reticle colour.</summary>
    public bool ReadyToFire => Def.Gun != null && fireCooldown <= 0f;

    public bool Occupied => Driver != null;

    public float HorizontalSpeed => new Vector2(Velocity.X, Velocity.Z).Length();

    /// <summary>
    /// Keep a flying hull inside the map.
    ///
    /// The perimeter walls stop everything that walks or drives, and stop nothing that flies —
    /// they are sixteen metres tall with open sky above. A bot flew a plane straight over one and
    /// out into the void, and a player could have done the same on their first flight. Clamped
    /// rather than bounced: an arena boundary should feel like the end of the map, not like
    /// hitting something.
    /// </summary>
    /// <summary>
    /// How high this hull's arena stands, set when it is parked.
    ///
    /// Carried rather than looked up because a Vehicle has no arena reference and never needed
    /// one while every map was the same height. Defaulted to the ordinary height so a hull built
    /// outside a match - as the harness does - still behaves.
    /// </summary>
    public float ArenaCeiling = Arena.StandardWallHeight;

    void HoldInsideArena()
    {
        const float Margin = 3f;
        float ceiling = ArenaCeiling - 1.5f;

        var p = GlobalPosition;
        var v = Velocity;

        float lx = Arena.HalfWidth - Margin, lz = Arena.HalfDepth - Margin;

        if (p.X < -lx) { p.X = -lx; v.X = MathF.Max(v.X, 0f); }
        if (p.X > lx) { p.X = lx; v.X = MathF.Min(v.X, 0f); }
        if (p.Z < -lz) { p.Z = -lz; v.Z = MathF.Max(v.Z, 0f); }
        if (p.Z > lz) { p.Z = lz; v.Z = MathF.Min(v.Z, 0f); }

        if (p.Y > ceiling) { p.Y = ceiling; v.Y = MathF.Min(v.Y, 0f); }

        GlobalPosition = p;
        Velocity = v;
    }

    /// <summary>Swing the barrel to the turret heading, in the hull's local frame.</summary>
    void AimBarrel()
    {
        if (barrel == null) return;
        barrel.Rotation = new Vector3(TurretPitch, -MathU.AngleDiff(TurretYaw, Facing), 0f);
    }

    /// <summary>
    /// Point the node the way the hull is driving.
    ///
    /// Nothing did this before: <see cref="Facing"/> steered the velocity correctly while the mesh
    /// stayed locked to its spawn heading, so a vehicle appeared to slide sideways rather than
    /// drive. The collision box was mis-rotated with it.
    /// </summary>
    void ApplyOrientation()
    {
        // Local +X is the nose, so yaw is negated: Godot's Y rotation runs the opposite way round
        // from the atan2 convention Facing uses everywhere else in the game.
        Rotation = new Vector3(Def.Flies ? Pitch : 0f, -Facing, 0f);
    }

    /// <summary>Where a driver sits and where the vehicle gun fires from.</summary>
    public Vector3 Seat => GlobalPosition + Vector3.Up * Def.EyeHeight;

    public Vector3 Forward
    {
        get
        {
            float cp = MathF.Cos(Pitch);
            return new Vector3(MathF.Cos(Facing) * cp, MathF.Sin(Pitch), MathF.Sin(Facing) * cp);
        }
    }

    /// <summary>Turret heading, independent of the hull. Ground guns only; a plane aims by flying.</summary>
    public float TurretYaw;
    public float TurretPitch;

    const float TurretYawRate = 2.4f;
    const float TurretPitchRate = 1.5f;

    /// <summary>A gun that cannot elevate is useless on a map with a second storey.</summary>
    const float TurretMinPitch = -0.45f;
    const float TurretMaxPitch = 0.85f;

    /// <summary>True when the gun aims independently of the hull, which is everything but the plane.</summary>
    public bool HasTurret => Def.Gun != null && !Def.Flies;

    /// <summary>
    /// Where the gun is pointed.
    ///
    /// The tank used to fire straight down its hull heading at exactly zero elevation, which is
    /// technically "shooting" and practically useless: it could not hit anyone standing on a deck,
    /// on a tower, or on the ground close in front of it, and with a 2.1 second reload and no
    /// crosshair there was nothing to tell you a round had even left the barrel.
    /// </summary>
    public Vector3 AimDir
    {
        get
        {
            if (!HasTurret) return Forward;

            float cp = MathF.Cos(TurretPitch);
            return new Vector3(MathF.Cos(TurretYaw) * cp, MathF.Sin(TurretPitch),
                               MathF.Sin(TurretYaw) * cp);
        }
    }

    /// <summary>Muzzle position, clear of the hull so a round never spawns inside its own vehicle.</summary>
    public Vector3 Muzzle => Seat + AimDir * (Def.HalfExtents.Length() + 0.6f);

    public void Setup(VehicleDef def, bool visuals)
    {
        Def = def;
        Health = def.Health;

        AddChild(new CollisionShape3D
        {
            Shape = new BoxShape3D { Size = def.HalfExtents * 2f },
            Position = Vector3.Up * def.HalfExtents.Y,
        });

        if (!visuals) return;

        // A modelled hull, where one exists for this vehicle. Everything below it stays exactly as
        // it was — the collision box, the turret pivot, the seat, the muzzle — because the model is
        // scaled onto the simulation rather than the simulation being rebuilt around the model. A
        // vehicle whose shape and hitbox disagree is worse than a box, since now the box lies.
        bool modelled = def.HullModel.Length > 0 && BuildModelledHull(def);

        if (!modelled)
        {
            hull = new MeshInstance3D
            {
                Mesh = new BoxMesh { Size = def.HalfExtents * 2f },
                MaterialOverride = Graphics.Player(def.Tint),
                Position = Vector3.Up * def.HalfExtents.Y,
            };
            AddChild(hull);

            // A bright nose block, so which way a vehicle is pointed is readable from any angle —
            // the same problem the pawn visor solves. A modelled hull has its own front end.
            AddChild(new MeshInstance3D
            {
                Mesh = new BoxMesh { Size = new Vector3(0.7f, 0.5f, 0.7f) },
                MaterialOverride = Graphics.Hot(def.Tint.Lightened(0.5f), 1.8f),
                Position = new Vector3(def.HalfExtents.X + 0.4f, def.HalfExtents.Y, 0f),
            });
        }

        if (def.Gun != null && !def.Flies)
        {
            // Pivoted at the seat so it swings with the turret rather than being welded to the hull.
            barrel = new Node3D { Position = Vector3.Up * def.EyeHeight };
            AddChild(barrel);

            // The turret is a separate export for exactly this reason: it hangs off the pivot the
            // simulation has always turned, so the gun traverses independently of the body without
            // the aiming code learning that a model exists.
            if (modelled && BuildModelledTurret(def)) { }
            else
                barrel.AddChild(new MeshInstance3D
                {
                    Mesh = new BoxMesh { Size = new Vector3(def.HalfExtents.X * 1.6f, 0.34f, 0.34f) },
                    MaterialOverride = Graphics.Player(new Color(0.16f, 0.18f, 0.22f)),
                    Position = new Vector3(def.HalfExtents.X * 0.8f, 0f, 0f),
                });
        }
        else if (def.Gun != null)
        {
            AddChild(new MeshInstance3D
            {
                Mesh = new BoxMesh { Size = new Vector3(def.HalfExtents.X * 1.5f, 0.34f, 0.34f) },
                MaterialOverride = Graphics.Player(new Color(0.16f, 0.18f, 0.22f)),
                Position = new Vector3(def.HalfExtents.X * 0.8f, def.EyeHeight, 0f),
            });
        }
    }

    /// <summary>
    /// The modelled hull, scaled so its longest axis matches the collision box's length.
    ///
    /// Length is the axis to match on because it is the one a player judges a vehicle by — how much
    /// road it takes up, whether it fits through a gap. Matching height instead would leave an eight
    /// metre tank looking like a car with a tall roof.
    /// </summary>
    bool BuildModelledHull(VehicleDef def)
    {
        modelScale = VehicleModels.ScaleFor(def.HullModel, def.HalfExtents.X * 2f);

        if (VehicleModels.Instance(def.HullModel, modelScale) is not { } model) return false;

        model.RotationDegrees = def.HullTilt;
        model.Position += Vector3.Up * def.HalfExtents.Y + def.HullOffset;

        AddChild(model);
        hullModel = model;

        // The flash target the box hull used to be. A destroyed vehicle darkens, and with a
        // textured model that has to be an overlay rather than a recolour.
        foreach (var mi in VehicleModels.AllMeshes(model)) { hull = mi; break; }

        return true;
    }

    /// <summary>
    /// The turret, on the pivot the simulation has always turned.
    ///
    /// Scaled by the *hull's* factor rather than its own, so the two parts keep the proportions the
    /// artist gave them. Sizing each part independently made the turret as long as the whole tank,
    /// because its longest axis measures 1.00 in its own units and so does the hull's — they are
    /// simply not the same unit.
    ///
    /// Seated by its base rather than its middle: the pivot is at the hull's roof line, and a part
    /// centred on that would be half sunk into the deck.
    /// </summary>
    bool BuildModelledTurret(VehicleDef def)
    {
        if (barrel == null) return false;

        // Sized by height, not by length. Scaling it the way the hull is scaled produced an eight
        // metre turret two and a half metres tall sitting on an eight metre hull — the two exports
        // are simply not in the same unit as each other, whatever their bounds suggest.
        float want = def.HalfExtents.Y * def.TurretHeight;
        float scale = VehicleModels.ScaleForHeight(def.TurretModel, want);

        if (VehicleModels.Instance(def.TurretModel, scale) is not { } model) return false;

        model.RotationDegrees = def.TurretTilt;

        // Seated by its base. The pivot is the hull's roof line, and a part centred on that would
        // be half sunk into the deck.
        model.Position += Vector3.Up * (want * 0.5f) + def.TurretOffset;

        barrel.AddChild(model);
        return true;
    }

    Node3D? hullModel;

    /// <summary>Scale applied to every modelled part, taken from the hull so the parts agree.</summary>
    float modelScale = 1f;

    /// <summary>Whether a pawn is close enough, and this hull free enough, to be boarded.</summary>
    public bool CanBoard(Pawn p)
        => Alive && Driver == null && exitLock <= 0f
           && p.GlobalPosition.DistanceTo(GlobalPosition) < Def.HalfExtents.Length() + 2.5f;

    public void Board(Pawn p)
    {
        Driver = p;
        p.EnterVehicle(this);
        Facing = p.Facing;
        TurretYaw = p.Facing;
        TurretPitch = 0f;
        AimBarrel();
    }

    /// <summary>
    /// Puts the driver back on their feet somewhere they can actually stand.
    ///
    /// The old version dropped them at a fixed offset and hoped. It hoped twice over: it measured
    /// the offset along <c>HalfExtents.X</c>, which is the hull's *length*, while pushing them out
    /// along the hull's sideways axis — so the driver of a tank was flung 5.6m out to the side of a
    /// vehicle only 2.4m wide, and in a map this dense that lands inside a wall more often than
    /// not. Collision comes back on at the same instant, so the solver had to resolve a pawn
    /// standing in solid geometry, and it resolved it by squeezing them out somewhere arbitrary —
    /// frequently underneath the hull they had just been driving.
    ///
    /// Every version after that measured spots on the ground and picked the first free one, and
    /// every version after that still put somebody under the tracks eventually — because a spot
    /// beside a hull is only clear until the hull moves, and the hull is usually moving. The
    /// answer is not a better search. It is to stop looking at the ground: the driver goes on the
    /// roof, which is the one place that cannot be driven over by the vehicle they just left,
    /// because it travels with it. See <see cref="FreeSpotFor"/>.
    /// </summary>
    /// <param name="thrown">
    /// True when the hull was destroyed under them, which throws them clear rather than letting
    /// them step down into their own wreck.
    /// </param>
    public void Eject(bool thrown = false)
    {
        if (Driver is not { } p) return;

        Driver = null;
        exitLock = 0.6f;   // stops the same button press boarding again immediately

        p.ExitVehicle(FreeSpotFor(p));

        if (thrown)
        {
            var away = (p.GlobalPosition - GlobalPosition) with { Y = 0f };
            away = away.LengthSquared() < 0.01f
                ? new Vector3(-MathF.Sin(Facing), 0f, MathF.Cos(Facing))
                : away.Normalized();

            p.ApplyKnockback(away * 9f + Vector3.Up * 7f);
        }
    }

    /// <summary>
    /// Where <paramref name="p"/> is put down when they leave this hull: on the roof, always.
    ///
    /// One answer and no search. The flanks are gone with the nose and the tail before them — a
    /// door beside a hull is only clear until the hull moves, and every version of this that
    /// measured a spot on the ground eventually put somebody under the tracks, which is the same
    /// report four times over. The roof is the one place that cannot be driven over by the thing
    /// you just got out of, because it moves with it.
    ///
    /// Dropped from slightly above the roof rather than placed exactly on it. Landing a fraction
    /// high costs a short fall that the controller resolves on its own; landing a fraction low
    /// means starting the frame *inside* the hull, and the solver's answer to that is to squeeze
    /// the pawn out somewhere arbitrary — underneath, as often as not. Erring upward turns the
    /// worst case from the bug into a hop.
    ///
    /// <see cref="DropIn"/> is deliberately small. This is a step up out of a hatch, not a launch:
    /// enough to guarantee clearance over the roof and never enough to be a way of gaining height,
    /// and short enough that you are standing again before it reads as being thrown.
    /// </summary>
    Vector3 FreeSpotFor(Pawn p)
    {
        float roof = Def.HalfExtents.Y * 2f + 0.15f;

        // Tried lifted first, then flush. The lift is what makes the drop happen, but a hull can
        // be parked under something — the spawn nearest the Reliquary's galleries is two metres
        // from a room — and a spot inside a ceiling is exactly the state this whole thing exists
        // to avoid. When there is no headroom, the roof itself still is not under the tracks.
        var lifted = GlobalPosition + Vector3.Up * (roof + DropIn);
        if (InsideWalls(lifted) && Fits(p, lifted)) return lifted;

        return GlobalPosition + Vector3.Up * roof;
    }

    /// <summary>How far above the roof a driver is let go of, in metres.</summary>
    const float DropIn = 1.1f;

    /// <summary>
    /// Whether a point is inside the arena at all. A spot on the far side of the perimeter wall is
    /// empty space and would pass the fit test happily, and ejecting into it is a death sentence
    /// under the out-of-bounds rule.
    /// </summary>
    static bool InsideWalls(Vector3 p)
        => MathF.Abs(p.X) <= Arena.HalfWidth && MathF.Abs(p.Z) <= Arena.HalfDepth
           && p.Y > Arena.KillPlaneY;

    /// <summary>Whether a standing pawn placed at <paramref name="at"/> would be inside anything.</summary>
    bool Fits(Pawn p, Vector3 at)
    {
        using var probe = new CapsuleShape3D
        {
            // Slightly under the real capsule. At exactly full size a pawn placed on the floor
            // registers against the floor itself and no spot on the map would ever be free.
            Radius = Pawn.Radius * 0.9f,
            Height = MathF.Max(Pawn.Height * 0.9f, Pawn.Radius * 2.1f),
        };

        using var query = new PhysicsShapeQueryParameters3D
        {
            Shape = probe,
            Transform = new Transform3D(Basis.Identity, at + Vector3.Up * (Pawn.Height * 0.5f)),

            // The hull you are climbing out of is excluded as well as the pawn.
            //
            // It was not, and that is the whole bug behind "I get out and I am under the tank",
            // reported three times. Every door candidate sits just outside the hull, well within
            // the probe's reach of it — so the tank's own collider vetoed all four of them, the
            // search fell through to the unchecked roof fallback every single time, and a pawn
            // dropped onto a moving hull ends up under it a moment later. The vehicle cannot be an
            // obstacle to leaving itself.
            Exclude = new Godot.Collections.Array<Rid> { p.GetRid(), GetRid() },
        };

        return GetWorld3D().DirectSpaceState.IntersectShape(query, 1).Count == 0;
    }

    /// <summary>One simulation step. Input is empty when nobody is aboard.</summary>
    public void Tick(float dt, PawnInput input, Match match)
    {
        if (exitLock > 0f) exitLock -= dt;
        if (fireCooldown > 0f) fireCooldown -= dt;
        if (MuzzleFlash > 0f) MuzzleFlash -= dt;

        if (!Alive)
        {
            Velocity = new Vector3(Velocity.X * 0.9f, Velocity.Y - Gravity * dt, Velocity.Z * 0.9f);
            MoveAndSlide();
            return;
        }

        if (Driver == null)
        {
            // Parked: settle and stay put.
            Velocity = new Vector3(Velocity.X * 0.86f, Def.Flies ? 0f : Velocity.Y - Gravity * dt, Velocity.Z * 0.86f);
            MoveAndSlide();
            ApplyOrientation();
            return;
        }

        // Raw stick, deliberately. PawnInput.Move has already been rotated into world space against
        // the driver's camera, which is right for a walking pawn and meaningless here: a hull
        // steers and throttles in its own frame. Reading Move fed world X/Z into the steer and
        // throttle axes, which is why driving did nothing coherent.
        // Stick right turns right. Facing runs anticlockwise from +X in the atan2 convention the
        // whole game uses, and "right" of a pawn facing +X is world +Z — so turning right *raises*
        // Facing. Negating it here inverted the steering.
        float steer = input.RawMove.X;
        float drive = -input.RawMove.Y;

        // Full turn authority at a standstill lets a car pirouette on the spot, which reads as a
        // turret rather than a vehicle. Tracked hulls really do neutral-steer, so the tank keeps it.
        if (!Def.NeutralSteer)
            steer *= 0.25f + 0.75f * MathU.Clamp01(HorizontalSpeed / Def.MaxSpeed);

        Facing += steer * Def.TurnRate * dt;

        // The look stick lays the gun, not the hull — you drive with one thumb and aim with the
        // other, exactly as on foot.
        if (HasTurret)
        {
            TurretYaw += input.RawLook.X * TurretYawRate * dt;
            TurretPitch = MathU.Clamp(TurretPitch - input.RawLook.Y * TurretPitchRate * dt,
                                      TurretMinPitch, TurretMaxPitch);
            AimBarrel();
        }

        if (Def.Flies)
        {
            // Nose up and down with the look axis. This is a rate rather than the driver's absolute
            // view angle: a plane is flown by holding the stick back, not by looking upward.
            Pitch = MathU.Clamp(Pitch - input.RawLook.Y * PlanePitchRate * dt, -0.9f, 0.9f);

            // It holds altitude rather than stalling, because a stall model is a flight sim and
            // this is not. Throttle has a floor for the same reason.
            float throttle = MathF.Max(0.35f, drive);
            Velocity = Velocity.MoveToward(Forward * (Def.MaxSpeed * throttle), Def.Accel * dt * 2f);
        }
        else
        {
            // Horizontal and vertical are integrated separately. One 3D MoveToward spends a single
            // shared delta budget across both, so gravity eats most of the acceleration every tick
            // and the hull crawls — for the tank it consumed nearly the whole budget.
            var flat = new Vector3(MathF.Cos(Facing), 0f, MathF.Sin(Facing));
            var horiz = new Vector3(Velocity.X, 0f, Velocity.Z)
                .MoveToward(flat * (Def.MaxSpeed * drive), Def.Accel * dt * 3f);

            // A small downward bias while grounded keeps the hull pinned to ramps instead of
            // skipping off every crest.
            float vy = IsOnFloor() ? -2f : Velocity.Y - Gravity * dt;
            Velocity = new Vector3(horiz.X, vy, horiz.Z);
        }

        MoveAndSlide();
        if (Def.Flies) HoldInsideArena();
        ApplyOrientation();

        if (Def.Gun != null && input.Fire && fireCooldown <= 0f)
        {
            match.FireVehicleWeapon(this);
            ShotsFired++;
            fireCooldown = Def.Gun.FireInterval;

            // A shell that heavy should shove the hull. It is feedback as much as physics: with a
            // two-second reload there is otherwise almost nothing telling you the gun went off.
            if (Def.Gun.Explodes)
            {
                var kick = -AimDir * 6.5f;
                Velocity = new Vector3(Velocity.X + kick.X, Velocity.Y, Velocity.Z + kick.Z);
                MuzzleFlash = MuzzleFlashTime;
            }
        }
    }

    /// <summary>Where this hull is parked at the start of the match, and where a wreck comes back.</summary>
    public Vector3 HomePosition;

    /// <summary>Seconds since it was destroyed. Drives the respawn.</summary>
    public float WreckAge;

    public bool TakeDamage(float amount)
    {
        if (!Alive) return false;

        Health -= amount;
        if (Health > 0f) return false;

        Health = 0f;
        WreckAge = 0f;

        // The driver is thrown clear rather than dying with it — a vehicle should be a risk you
        // can walk away from, not a coin flip that deletes you.
        Eject(thrown: true);

        if (hull?.MaterialOverride is StandardMaterial3D mat)
            mat.AlbedoColor = new Color(0.16f, 0.14f, 0.13f);

        return true;
    }

    /// <summary>Put a wrecked hull back on its spawn, whole again.</summary>
    public void Restore(Vector3 at, float facing)
    {
        Health = Def.Health;
        WreckAge = 0f;
        Velocity = Vector3.Zero;
        Facing = facing;
        Pitch = 0f;
        TurretYaw = facing;
        TurretPitch = 0f;
        fireCooldown = 0f;
        exitLock = 0f;

        GlobalPosition = at;
        ApplyOrientation();
        AimBarrel();

        if (hull?.MaterialOverride is StandardMaterial3D mat) mat.AlbedoColor = Def.Tint;
    }
}
