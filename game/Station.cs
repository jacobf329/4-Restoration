using System.Collections.Generic;
using Godot;

namespace Battlefront;

/// <summary>One axis-aligned box of station. Walls, floors, consoles, crates — all of it.</summary>
public readonly struct Box
{
    public readonly Vector3 Centre;
    public readonly Vector3 Half;
    public readonly Color Tint;

    /// <summary>What it is made of, which decides the material. See HitboxClone.SurfaceKind.</summary>
    public readonly HitboxClone.SurfaceKind Surface;

    /// <summary>A mesh from assets/props, or empty for a plain box.</summary>
    public readonly string Model;

    public readonly float Yaw;

    public Box(Vector3 centre, Vector3 half, Color tint,
               HitboxClone.SurfaceKind surface = HitboxClone.SurfaceKind.Panel,
               string model = "", float yaw = 0f)
    {
        Centre = centre;
        Half = half;
        Tint = tint;
        Surface = surface;
        Model = model;
        Yaw = yaw;
    }

    public float Top => Centre.Y + Half.Y;
    public float Bottom => Centre.Y - Half.Y;
}

/// <summary>A command post: capture it, spawn at it, deny it to the other side.</summary>
public sealed class Post
{
    public string Name = "";
    public Vector3 Centre;

    /// <summary>0 or 1 once somebody owns it, -1 while it is neutral.</summary>
    public int Owner = -1;

    /// <summary>How far from the middle of the post you have to stand to be capturing it.</summary>
    public const float Radius = 7f;
}

/// <summary>
/// THE BATTLE STATION — the first map, and the one the rest of the game is measured against.
///
/// Laid out the way the 2005 game lays out an interior map, which is a specific thing and not
/// just "corridors": a hub you keep being funnelled back through, four spokes off it, and a room
/// at the end of each spoke worth holding. Every route between two posts passes through the
/// middle, so the middle is where the battle actually is, and the middle is a hole in the floor.
///
/// The reactor shaft is the whole design. A map made of corridors is a map where the fight is
/// always a corridor, and the one thing the Death Star interior is remembered for is the drop —
/// a chasm with a catwalk over it, no railings, and a fight happening on the catwalk. It is the
/// only place on the map where the floor is not a given, which is what makes crossing it a
/// decision instead of a walk.
///
/// Built from axis-aligned boxes because that is what the collision in this engine supports -
/// BoxShape3D and nothing else - and because a blockout you can play is worth more than a mesh
/// you can look at. The props hanging on it come from assets/props, which already exist.
/// </summary>
public sealed class Station
{
    public readonly List<Box> Boxes = new();
    public readonly List<Post> Posts = new();

    /// <summary>Where a side starts the match, before it owns anything. Index by team.</summary>
    public readonly Vector3[] HomeSpawn = new Vector3[2];

    /// <summary>Half the width and depth of the whole station, for bounds checks.</summary>
    public const float HalfSpan = 116f;

    /// <summary>Interior height of a corridor. Low, because a corridor is not a hall.</summary>
    public const float CorridorCeiling = 9f;

    /// <summary>The reactor shaft: anything that falls past this is gone.</summary>
    public const float KillFloor = -40f;

    /// <summary>Half-extent of the square shaft in the middle of the hub.</summary>
    public const float ShaftHalf = 20f;

    /// <summary>Outer edge of the gantry ring that runs around the shaft.</summary>
    public const float GantryOuter = 27f;

    /// <summary>Half-width of the catwalk thrown across the shaft.</summary>
    public const float CatwalkHalf = 3f;

    static readonly Color Hull = new(0.40f, 0.42f, 0.46f);
    static readonly Color Deck = new(0.26f, 0.27f, 0.30f);
    static readonly Color Trim = new(0.54f, 0.56f, 0.60f);
    static readonly Color Console = new(0.22f, 0.24f, 0.28f);

    readonly RandomNumberGenerator rng = new();

    public Station()
    {
        rng.Seed = 20051101;   // the year the thing this is cloning came out

        Hub();
        Spokes();
        Hangar();
        Detention();
        Control();
        Docking();
        Shell();

        HomeSpawn[0] = new Vector3(0f, 1f, 92f);     // the hangar end
        HomeSpawn[1] = new Vector3(0f, 1f, -92f);    // the detention end
    }

    // ------------------------------------------------------------------ the hub

    /// <summary>
    /// The reactor shaft and the gantry around it.
    ///
    /// The floor here is four slabs with a hole in the middle rather than one slab, because the
    /// hole is the point. The catwalk crosses north to south, so the two posts that matter most —
    /// the hangar and the detention block — are joined by three metres of walkway over a drop,
    /// and going the long way round the gantry is slower but survivable. That choice, every time
    /// you respawn, is the map.
    /// </summary>
    void Hub()
    {
        // The gantry ring: four slabs around the shaft, leaving the middle open.
        Slab(new Vector3(0f, 0f, (ShaftHalf + GantryOuter) * 0.5f),
             new Vector2(GantryOuter, (GantryOuter - ShaftHalf) * 0.5f));
        Slab(new Vector3(0f, 0f, -(ShaftHalf + GantryOuter) * 0.5f),
             new Vector2(GantryOuter, (GantryOuter - ShaftHalf) * 0.5f));
        Slab(new Vector3((ShaftHalf + GantryOuter) * 0.5f, 0f, 0f),
             new Vector2((GantryOuter - ShaftHalf) * 0.5f, ShaftHalf));
        Slab(new Vector3(-(ShaftHalf + GantryOuter) * 0.5f, 0f, 0f),
             new Vector2((GantryOuter - ShaftHalf) * 0.5f, ShaftHalf));

        // The catwalk. No railings, on purpose.
        Slab(new Vector3(0f, 0f, 0f), new Vector2(CatwalkHalf, ShaftHalf));

        // The shaft wall below the lip, so the drop reads as a shaft and not as a void. Four
        // faces dropping away, each one a little darker than the deck above it.
        foreach (var (at, half) in Ring(ShaftHalf, 1.2f))
            Boxes.Add(new Box(at with { Y = -14f }, new Vector3(half.X, 14f, half.Y),
                              Deck.Darkened(0.45f), HitboxClone.SurfaceKind.Panel));

        // Four pylons, pushed out to the outer lip of the gantry.
        //
        // They were at +-24 with a 2.4m half-extent, which put them squarely in the corner of a
        // walkway that runs from 20 to 27 - the ring was blocked at all four corners by its own
        // decoration. Out at the lip they still give the hub a silhouette and still break line
        // of sight, and the way round the shaft is actually walkable.
        foreach (int sx in new[] { -1, 1 })
        foreach (int sz in new[] { -1, 1 })
            Boxes.Add(new Box(new Vector3(sx * 25.8f, 5.5f, sz * 25.8f), new Vector3(1.2f, 5.5f, 1.2f),
                              Trim, HitboxClone.SurfaceKind.Panel));

        // A roof over the whole hub, shaft included.
        //
        // Missing on the first build, and the screenshot showed daylight over the reactor of an
        // enclosed battle station. Every corridor and room roofs itself as it is built; the hub
        // is assembled out of four floor slabs rather than through Room(), so it was the one
        // place nothing put a lid on. Twenty metres up, which is well above the corridor
        // ceilings - arriving in the hub should feel like stepping into a shaft.
        Roof(Vector3.Zero, new Vector2(GantryOuter, GantryOuter), 20f);

        // The contested post, on the gantry rather than on the catwalk: a post you can only reach
        // by crossing the drop would be a post nobody takes.
        Posts.Add(new Post { Name = "REACTOR", Centre = new Vector3(0f, 0f, 23.5f) });
    }

    /// <summary>The four boxes that make a square ring of the given half-extent.</summary>
    static IEnumerable<(Vector3 At, Vector2 Half)> Ring(float inner, float thickness)
    {
        float mid = inner + thickness * 0.5f;

        yield return (new Vector3(0f, 0f, mid), new Vector2(inner + thickness, thickness * 0.5f));
        yield return (new Vector3(0f, 0f, -mid), new Vector2(inner + thickness, thickness * 0.5f));
        yield return (new Vector3(mid, 0f, 0f), new Vector2(thickness * 0.5f, inner));
        yield return (new Vector3(-mid, 0f, 0f), new Vector2(thickness * 0.5f, inner));
    }

    // ------------------------------------------------------------------ the spokes

    /// <summary>
    /// Four corridors out of the hub, each with alcoves down its length.
    ///
    /// Fourteen metres wide and nine high, which is wide enough for a squad to move through and
    /// tight enough that a rocket is a reasonable idea. The alcoves matter more than the width:
    /// a bare corridor is a shooting gallery where whoever fires first wins, and a corridor with
    /// somewhere to step out of the line is a fight.
    /// </summary>
    void Spokes()
    {
        foreach (int sign in new[] { -1, 1 })
        {
            Corridor(alongZ: true, sign);
            Corridor(alongZ: false, sign);
        }
    }

    void Corridor(bool alongZ, int sign)
    {
        const float HalfWide = 7f, From = GantryOuter, To = 66f;

        float mid = (From + To) * 0.5f, run = (To - From) * 0.5f;

        Vector3 Along(float a, float across, float y = 0f) =>
            alongZ ? new Vector3(across, y, sign * a) : new Vector3(sign * a, y, across);

        Vector2 Size(float a, float across) =>
            alongZ ? new Vector2(across, a) : new Vector2(a, across);

        Slab(Along(mid, 0f), Size(run, HalfWide));
        Roof(Along(mid, 0f), Size(run, HalfWide), CorridorCeiling);

        // The two side walls, each broken by a pair of alcoves.
        foreach (int side in new[] { -1, 1 })
        {
            float across = side * (HalfWide + 0.8f);

            Boxes.Add(new Box(Along(mid, across, CorridorCeiling * 0.5f),
                              new Vector3(Size(run, 0.8f).X, CorridorCeiling * 0.5f,
                                          Size(run, 0.8f).Y),
                              Hull, HitboxClone.SurfaceKind.Panel));

            // Cover inside the corridor: crates against the wall, from the prop library.
            for (int i = 0; i < 2; i++)
            {
                float a = From + (To - From) * (0.3f + i * 0.42f);
                Prop("supply_crate", Along(a, across - side * 2.6f), 2.2f, rng.RandfRange(0f, 360f));
            }
        }

        // A blast-door frame where the corridor meets the hub, so the hub reads as a room you
        // enter rather than as more corridor.
        foreach (int side in new[] { -1, 1 })
            Boxes.Add(new Box(Along(From + 1.2f, side * (HalfWide - 0.9f), CorridorCeiling * 0.5f),
                              new Vector3(Size(1.2f, 1.8f).X, CorridorCeiling * 0.5f,
                                          Size(1.2f, 1.8f).Y),
                              Trim, HitboxClone.SurfaceKind.Panel));
    }

    // ------------------------------------------------------------------ the rooms

    /// <summary>The hangar: the biggest room, the highest ceiling, and one side open to space.</summary>
    void Hangar()
    {
        const float Z = 88f, HalfX = 46f, HalfZ = 22f, Ceiling = 22f;

        Room(new Vector3(0f, 0f, Z), new Vector2(HalfX, HalfZ), Ceiling, openSouth: true);

        // Deck clutter, which is what makes a hangar a hangar rather than a hall: drums, sleds,
        // a wrecked fighter on the far side, floodlights on the walls.
        Prop("wreck_fighter", new Vector3(-26f, 0f, Z + 8f), 14f, 24f);
        Prop("cargo_sled", new Vector3(22f, 0f, Z + 10f), 8f, 96f);

        for (int i = 0; i < 7; i++)
            Prop("fuel_drum", new Vector3(rng.RandfRange(-38f, 38f), 0f,
                                          rng.RandfRange(Z - 16f, Z + 16f)), 1.6f,
                 rng.RandfRange(0f, 360f));

        for (int i = 0; i < 5; i++)
            Prop("supply_crate", new Vector3(rng.RandfRange(-40f, 40f), 0f,
                                             rng.RandfRange(Z - 15f, Z + 15f)), 2.4f,
                 rng.RandfRange(0f, 360f));

        foreach (int sx in new[] { -1, 1 })
            Prop("floodlight", new Vector3(sx * 40f, 0f, Z + 16f), 6f, sx > 0 ? 200f : 160f);

        Posts.Add(new Post { Name = "HANGAR", Centre = new Vector3(0f, 0f, Z), Owner = 0 });
    }

    /// <summary>The detention block: the tightest room on the map, and the other side's home.</summary>
    void Detention()
    {
        const float Z = -88f, HalfX = 26f, HalfZ = 22f;

        Room(new Vector3(0f, 0f, Z), new Vector2(HalfX, HalfZ), 10f, openNorth: true);

        // Two rows of cells down the long walls. Each is three walls and a gap, which is cover
        // from one direction and a trap from the other.
        foreach (int sx in new[] { -1, 1 })
        for (int i = -1; i <= 1; i++)
        {
            float cx = sx * 19f, cz = Z + i * 13f;

            Boxes.Add(new Box(new Vector3(cx + sx * 3.4f, 2.5f, cz), new Vector3(0.6f, 2.5f, 5f),
                              Hull, HitboxClone.SurfaceKind.Panel));

            foreach (int sz in new[] { -1, 1 })
                Boxes.Add(new Box(new Vector3(cx, 2.5f, cz + sz * 4.4f),
                                  new Vector3(3.4f, 2.5f, 0.6f), Hull,
                                  HitboxClone.SurfaceKind.Panel));
        }

        Posts.Add(new Post { Name = "DETENTION", Centre = new Vector3(0f, 0f, Z), Owner = 1 });
    }

    /// <summary>The control room: a gallery above the floor, looking back down the spoke.</summary>
    void Control()
    {
        const float X = 88f, HalfX = 22f, HalfZ = 28f;

        Room(new Vector3(X, 0f, 0f), new Vector2(HalfX, HalfZ), 12f, openWest: true);

        // A raised gallery along the back wall, reached by steps at both ends. Height is the
        // whole point of this room: it is the one post where holding the floor is not enough.
        Boxes.Add(new Box(new Vector3(X + 12f, 1.6f, 0f), new Vector3(8f, 1.6f, 20f),
                          Deck, HitboxClone.SurfaceKind.Panel));

        foreach (int sz in new[] { -1, 1 })
        for (int s = 0; s < 4; s++)
            Boxes.Add(new Box(new Vector3(X + 2.5f + s * 1.6f, 0.2f + s * 0.4f, sz * 17f),
                              new Vector3(0.8f, 0.2f + s * 0.4f, 3f), Deck,
                              HitboxClone.SurfaceKind.Panel));

        // Consoles along the gallery, and a few on the floor.
        for (int i = -2; i <= 2; i++)
            Boxes.Add(new Box(new Vector3(X + 17f, 3.9f, i * 6.5f), new Vector3(2f, 1.1f, 2.4f),
                              Console, HitboxClone.SurfaceKind.Panel));

        for (int i = -1; i <= 1; i++)
            Boxes.Add(new Box(new Vector3(X - 8f, 0.9f, i * 9f), new Vector3(2.4f, 0.9f, 3f),
                              Console, HitboxClone.SurfaceKind.Panel));

        Posts.Add(new Post { Name = "CONTROL", Centre = new Vector3(X - 2f, 0f, 0f) });
    }

    /// <summary>The docking bay: open, pillared, and the easiest post on the map to flank.</summary>
    void Docking()
    {
        const float X = -88f, HalfX = 24f, HalfZ = 30f;

        Room(new Vector3(X, 0f, 0f), new Vector2(HalfX, HalfZ), 14f, openEast: true);

        foreach (int sz in new[] { -1, 1 })
        foreach (int i in new[] { -1, 1 })
            Boxes.Add(new Box(new Vector3(X + i * 12f, 7f, sz * 15f), new Vector3(2f, 7f, 2f),
                              Trim, HitboxClone.SurfaceKind.Panel));

        Prop("wreck_hull", new Vector3(X - 12f, 0f, 0f), 16f, 70f);

        for (int i = 0; i < 6; i++)
            Prop("barricade", new Vector3(rng.RandfRange(X - 16f, X + 16f), 0f,
                                          rng.RandfRange(-22f, 22f)), 3.4f,
                 rng.RandfRange(0f, 360f));

        Posts.Add(new Post { Name = "DOCKING", Centre = new Vector3(X + 2f, 0f, 0f) });
    }

    // ------------------------------------------------------------------ pieces

    /// <summary>A floor slab, 1m thick, with its top surface at <paramref name="at"/>.Y.</summary>
    void Slab(Vector3 at, Vector2 half) =>
        Boxes.Add(new Box(at with { Y = at.Y - 0.5f }, new Vector3(half.X, 0.5f, half.Y),
                          Deck, HitboxClone.SurfaceKind.Panel));

    void Roof(Vector3 at, Vector2 half, float height) =>
        Boxes.Add(new Box(at with { Y = height + 0.4f }, new Vector3(half.X, 0.4f, half.Y),
                          Hull, HitboxClone.SurfaceKind.Panel));

    /// <summary>A roofed room with a floor, four walls and a doorway on whichever side is open.</summary>
    void Room(Vector3 at, Vector2 half, float ceiling,
              bool openNorth = false, bool openSouth = false,
              bool openEast = false, bool openWest = false)
    {
        const float DoorHalf = 7f, Thick = 1f;

        Slab(at, half);
        Roof(at, half, ceiling);

        void Wall(Vector3 centre, Vector2 size) =>
            Boxes.Add(new Box(centre with { Y = ceiling * 0.5f },
                              new Vector3(size.X, ceiling * 0.5f, size.Y), Hull,
                              HitboxClone.SurfaceKind.Panel));

        void Side(float offset, bool alongX, bool open)
        {
            float span = alongX ? half.X : half.Y;

            Vector3 Centre(float slide) => alongX
                ? new Vector3(at.X + slide, 0f, at.Z + offset)
                : new Vector3(at.X + offset, 0f, at.Z + slide);

            Vector2 Size(float s) => alongX
                ? new Vector2(s, Thick)
                : new Vector2(Thick, s);

            if (!open) { Wall(Centre(0f), Size(span)); return; }

            float piece = (span - DoorHalf) * 0.5f;
            if (piece < 0.5f) return;

            Wall(Centre(-(DoorHalf + piece)), Size(piece));
            Wall(Centre(DoorHalf + piece), Size(piece));
        }

        Side(half.Y, alongX: true, openNorth);
        Side(-half.Y, alongX: true, openSouth);
        Side(half.X, alongX: false, openEast);
        Side(-half.X, alongX: false, openWest);
    }

    /// <summary>
    /// A prop from assets/props, standing on the floor, in a box its own shape.
    ///
    /// The box comes from the mesh's measured proportions rather than from a guess - the same
    /// rule the props have always been placed by, and the reason a crate's collider is the size
    /// of the crate. <paramref name="longest"/> is its largest dimension in metres.
    /// </summary>
    void Prop(string key, Vector3 foot, float longest, float yaw)
    {
        var half = HitboxClone.PropShapes.HalfExtents(key, longest);

        Boxes.Add(new Box(foot + Vector3.Up * half.Y, half, Colors.White,
                          HitboxClone.SurfaceKind.Panel, key, yaw));
    }

    /// <summary>The outer hull, which is the edge of the world and cannot be left.</summary>
    void Shell()
    {
        const float H = 26f;

        foreach (int sign in new[] { -1, 1 })
        {
            Boxes.Add(new Box(new Vector3(0f, H * 0.5f, sign * HalfSpan),
                              new Vector3(HalfSpan, H * 0.5f, 1f), Hull.Darkened(0.3f)));
            Boxes.Add(new Box(new Vector3(sign * HalfSpan, H * 0.5f, 0f),
                              new Vector3(1f, H * 0.5f, HalfSpan), Hull.Darkened(0.3f)));
        }
    }
}
