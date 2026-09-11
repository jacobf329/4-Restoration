using System;
using System.Collections.Generic;
using Godot;

namespace HitboxClone;

/// <summary>
/// A box in the arena — one wall, pillar, deck or piece of cover. Kept as plain data so the
/// geometry is described once and then turned into collision, and optionally meshes, separately.
/// The headless test builds the collision half only.
/// </summary>
public readonly struct Block
{
    public readonly Vector3 Centre;
    public readonly Vector3 HalfExtents;
    public readonly Color Tint;

    /// <summary>
    /// Whether explosives can bring this block down. Only the thin walkways of the upper storey
    /// are: blowing out a skybridge under someone is a play, and blowing away the floor plan is
    /// just a map that decays into a flat box over a long match.
    /// </summary>
    public readonly bool Fragile;

    /// <summary>
    /// What this block is made of. Defaults to the plating every arena has always been built from.
    ///
    /// A role, not a look — see <see cref="SurfaceKind"/>. Defaulted so that adding materials did
    /// not mean editing several hundred existing block constructions, and so an arena that has no
    /// opinion about its materials still gets the one the game shipped with.
    /// </summary>
    public readonly SurfaceKind Surface;

    public Block(Vector3 centre, Vector3 halfExtents, Color tint, bool fragile = false,
                 SurfaceKind surface = SurfaceKind.Panel)
    {
        Centre = centre;
        HalfExtents = halfExtents;
        Tint = tint;
        Fragile = fragile;
        Surface = surface;
    }
}

/// <summary>
/// A piece of dressing: seen, never touched.
///
/// Deliberately not a <see cref="Block"/>, and the difference is the whole reason this type
/// exists. A block is collision that happens to be drawn — every one of them is in the navigation
/// graph, the hull-fitting test, the spawn clearance check and the line-of-sight scan, and adding
/// two hundred of them to make a map look inhabited would change how every fight in it plays. A
/// kerb you can trip on is a gameplay change wearing a decoration's clothes.
///
/// So decor is drawn and nothing else. Nothing queries it, nothing walks into it, and the
/// simulation cannot tell it is there. That buys two things worth having: props can be placed
/// with a free hand rather than against a nav budget, and they can be *rotated*, which blocks
/// cannot be. A crate at eleven degrees is the single cheapest way to stop a room reading as a
/// grid, and it is not available to anything that has to answer an axis-aligned overlap test.
/// </summary>
public readonly struct Decor
{
    public readonly Vector3 Centre;
    public readonly Vector3 HalfExtents;

    /// <summary>Euler degrees. The thing a block cannot have.</summary>
    public readonly Vector3 Turn;

    public readonly Color Tint;
    public readonly SurfaceKind Surface;

    public Decor(Vector3 centre, Vector3 halfExtents, Color tint,
                 SurfaceKind surface = SurfaceKind.Panel, Vector3 turn = default)
    {
        Centre = centre;
        HalfExtents = halfExtents;
        Tint = tint;
        Surface = surface;
        Turn = turn;
    }
}

/// <summary>A pad that flings anything standing on it straight up.</summary>
public readonly struct LaunchPad
{
    public readonly Vector3 Centre;
    public readonly float Radius;
    public readonly float Impulse;

    public LaunchPad(Vector3 centre, float radius, float impulse)
    {
        Centre = centre;
        Radius = radius;
        Impulse = impulse;
    }
}

/// <summary>An area of ground that hurts anything standing in it.</summary>
public readonly struct HazardZone
{
    public readonly Rect2 Area;
    public readonly float Top;
    public readonly float DamagePerSecond;

    public HazardZone(Rect2 area, float top, float damagePerSecond)
    {
        Area = area;
        Top = top;
        DamagePerSecond = damagePerSecond;
    }
}

/// <summary>A platform that slides between two points, carrying whoever is riding it.</summary>
public readonly struct MovingPlatformDef
{
    public readonly Vector3 A;
    public readonly Vector3 B;
    public readonly Vector3 HalfExtents;
    public readonly float Period;

    /// <summary>
    /// Fraction of each leg spent waiting at the end before setting off, 0 to about 0.5.
    ///
    /// A shuttle wants none — it should always be somewhere useful. An elevator wants plenty: one
    /// that never stops is something you have to time rather than something you ride, and the
    /// point of an elevator is that it is the easy way up.
    /// </summary>
    public readonly float Dwell;

    /// <summary>
    /// Whether this shoves pawns along instead of blocking them. A moving wall is the one piece of
    /// level machinery that has to reach into the simulation: Godot resolves a character against a
    /// moving body by stopping the character, not by carrying it, so a push wall left to the
    /// physics engine is just a wall that happens to travel.
    /// </summary>
    public readonly bool Pushes;

    public MovingPlatformDef(Vector3 a, Vector3 b, Vector3 halfExtents, float period,
                             float dwell = 0f, bool pushes = false)
    {
        A = a;
        B = b;
        HalfExtents = halfExtents;
        Period = period;
        Dwell = dwell;
        Pushes = pushes;
    }

    /// <summary>
    /// Where along the run the platform sits at this phase, 0 at A and 1 at B.
    ///
    /// Eased at each end rather than reversing instantly, which would fling a passenger off, and
    /// held still for <see cref="Dwell"/> of each leg so an elevator can actually be boarded.
    /// </summary>
    public float Travel(float clock)
    {
        float phase = Mathf.PosMod(clock / Period, 1f);

        bool back = phase >= 0.5f;
        float u = back ? (phase - 0.5f) * 2f : phase * 2f;

        float m = Mathf.Clamp((u - Dwell) / MathF.Max(0.001f, 1f - Dwell), 0f, 1f);
        float eased = 0.5f - 0.5f * MathF.Cos(m * MathF.PI);

        return back ? 1f - eased : eased;
    }
}

/// <summary>
/// Procedurally built arena. There are no model files anywhere in the project — everything is
/// boxes, which is both faithful to the original's minimalist look and keeps the repo tiny.
///
/// Layouts are symmetric so no spawn is advantaged, and the self-test checks every spawn and
/// capture zone is inside the arena, clear of cover, and not hanging over a pit.
/// </summary>
public sealed class Arena
{
    // Five times the area of the previous box — 2.24 times each dimension, since that is what
    // five times the *floor* works out to. Five times each dimension would have been twenty-five
    // times the area, which four players would rattle around in.
    //
    // Grown outward rather than rescaled, for the second time and for the same reason: every jump
    // gap, step rise and ledge spacing in the core is tuned against the pawn's actual apex, and
    // multiplying the coordinates would have turned all of them into gaps nobody can cross. The
    // core is untouched and the new floor became new districts around it.
    public const float StandardHalfWidth = 139f;
    public const float StandardHalfDepth = 103f;

    /// <summary>
    /// The footprint of one arena, which is no longer the same for all of them.
    ///
    /// Coldstore is a siege across open ground and the open ground is the map. At the shared
    /// footprint its plain was ninety metres deep, which a tank crosses in six seconds - that is
    /// a courtyard with snow on it, not an approach. The thing being copied is a long walk under
    /// fire, and a long walk has to actually be long.
    ///
    /// Per-layout rather than global for the same reason wall height is: one map needs a
    /// different number and every other map is tuned against the one it has. See HeightFor.
    /// </summary>
    public static float HalfWidthFor(int layout)
        => layout == ColdstoreLayout ? 250f : StandardHalfWidth;

    public static float HalfDepthFor(int layout)
        => layout == ColdstoreLayout ? 330f : StandardHalfDepth;

    /// <summary>The largest any arena gets, for anything that has to bound all of them.</summary>
    public static float LargestHalfWidth => 250f;
    public static float LargestHalfDepth => 330f;

    public float HalfWidth => HalfWidthFor(Layout);
    public float HalfDepth => HalfDepthFor(Layout);
    /// <summary>
    /// How high the outer wall stands on an ordinary arena, and the height the game was built
    /// around: a jump apex of 3.2m, a jetpack ceiling of 33m, an upper storey at 6.4m.
    /// </summary>
    public const float StandardWallHeight = 16f;

    /// <summary>
    /// How high THIS arena's wall stands.
    ///
    /// Per-layout rather than one constant, which it was until the Laboratory. Everything about
    /// the four original maps is horizontal - they are floors with a storey over them - and a
    /// single height was right for all of them. A tower is not that shape, and a global constant
    /// would have capped it at the height of a map it has nothing in common with.
    ///
    /// Read rather than stored so it is available inside the constructor, where the perimeter is
    /// built before any field initialiser would have run.
    /// </summary>
    public float WallHeight => HeightFor(Layout);

    /// <summary>The wall height a layout wants. See <see cref="WallHeight"/>.</summary>
    public static float HeightFor(int layout)
        => layout == LaboratoryLayout ? StandardWallHeight * 4f : StandardWallHeight;

    /// <summary>The tallest any arena stands, for anything that has to size a shared buffer.</summary>
    public static float TallestWallHeight => StandardWallHeight * 4f;

    /// <summary>
    /// Where the original arena ended. Everything inside this is the tuned core; everything beyond
    /// it is the outer districts, which are free to use a coarser, more open grain.
    /// </summary>
    /// <summary>
    /// The middle of the map, as a fraction of it rather than a fixed box.
    ///
    /// Bots rally here and the rule is "the middle, not the far corners" - which is a statement
    /// about proportion. At the standard footprint these are the same 62 by 46 they always were.
    /// </summary>
    public float CoreHalfWidth => CoreX * HalfWidth / StandardHalfWidth;
    public float CoreHalfDepth => CoreZ * HalfDepth / StandardHalfDepth;

    const float CoreX = 62f;
    const float CoreZ = 46f;

    /// <summary>
    /// Where the outer ring of cover sits. The arena grew outward rather than being rescaled: the
    /// central structures were already tuned, and stretching them would have broken jump distances
    /// that the new gravity was set against.
    /// </summary>
    const float OuterX = 52f;
    const float OuterZ = 38f;

    /// <summary>Fall below this and you are dead, however you got there.</summary>
    public const float KillPlaneY = -5f;

    /// <summary>
    /// The top of the world, as far above it as the kill plane is below.
    ///
    /// Named because two places need to agree about it and used to only by coincidence: the play
    /// boundary below and the corpse clamp in the match step. Comfortably above anything anybody
    /// can legitimately reach - a full jetpack tops out at 33m and the strongest launch pad on any
    /// map apexes at 18.6m - so a pawn up here got there by a fault rather than by playing.
    /// </summary>
    public float CeilingY => WallHeight + 26f;

    /// <summary>
    /// One arena per faith, named for what it is rather than for its floor plan.
    ///
    /// They were Crossfire, Foundry, Atrium and Gauntlet — four descriptions of a shape, which is
    /// what you call a map before you know what it is. The layouts are the same layouts underneath;
    /// what changed is that each of them now belongs to somebody, and the interiors were built to
    /// say so.
    ///
    /// The Reliquary is the Vessels': they hold that humanity *was* its mortality, so their
    /// architecture is a place for keeping bodies. The Furnace is the Custodians': Prometheus stole
    /// the fire and was chained to it, and every hazard left in the game is here and nowhere else.
    /// The Glasshouse is the Garden's: Noah carried the living through the flood and they are still
    /// carrying them. The Thousand Rooms is Ingenuity's: a civilisation
    /// that cannot let potential go unused builds something that never stops adding capacity, and
    /// it is not an accident that it is the layout closest to a camp.
    /// </summary>
    /// <summary>
    /// Every layout, arenas first and puzzle chambers last.
    ///
    /// One list rather than two, because ArenaIndex is an index into this and threading a
    /// second namespace through the lobby, the settings, the harness and the screenshot queue
    /// would touch far more than it is worth. What separates them is IsPuzzle, and the mode
    /// picker is what keeps a puzzle chamber out of a deathmatch rotation.
    /// </summary>
    public static readonly string[] Names =
        { "Reliquary", "Furnace", "Glasshouse", "Thousand Rooms", "Laboratory", "Coldstore",
          "Fairview", "Convocation", "Antechamber", "Orrery" };

    public readonly int Layout;
    public string Name => Names[Layout];

    /// <summary>
    /// The music cue for this place. Named from the layout rather than stored per arena, so a new
    /// map's bed is wired the moment the file lands and is silent until then.
    /// </summary>
    public string MusicCue => Layout switch
    {
        0 => "mus_06_reliquary",
        1 => "mus_07_furnace",
        2 => "mus_08_glasshouse",
        3 => "mus_09_thousand_rooms",
        LaboratoryLayout => "mus_10_laboratory",
        ColdstoreLayout => "mus_11_coldstore",
        _ => "mus_02_standing_orders",
    };

    public readonly List<Block> Blocks = new();

    /// <summary>
    /// Everything drawn but not collided with. See <see cref="Decor"/>.
    ///
    /// Budgeted rather than unlimited. Every entry is one more MeshInstance3D drawn once per
    /// viewport, and this arena renderer has been here before: each block used to carry four
    /// emissive trim bars, and nine hundred extra instances across four splitscreen quarters is
    /// why the whole map shimmered along its edges. Dressing is worth a lot and worth nothing at
    /// half the frame rate, so the scatter passes work to a cap.
    /// </summary>
    public readonly List<Decor> Decorations = new();

    /// <summary>How many pieces of dressing an arena may carry. See <see cref="Decorations"/>.</summary>
    public const int DecorBudget = 260;

    /// <summary>Interior chambers this arena actually built. Reported by the harness.</summary>
    public int RoomsBuilt;

    /// <summary>
    /// Where the last room actually went, which is not always where it was asked to go.
    ///
    /// A caller that puts something inside a room — a weapon crate in a vault — has to read this
    /// rather than reuse its own coordinate, or the crate ends up embedded in a wall eight metres
    /// away. Which is exactly what happened to the Glasshouse seed vaults.
    /// </summary>
    public Vector3 LastRoomAt;
    public readonly List<Vector3> SpawnPoints = new();
    public readonly List<Vector3> ZoneSpots = new();
    public readonly List<LaunchPad> LaunchPads = new();
    public readonly List<MovingPlatformDef> MovingPlatforms = new();

    /// <summary>Ground-level slabs. Pits are carved out of these, leaving real holes.</summary>
    public List<Rect2> FloorSlabs = new();

    /// <summary>
    /// Where bots regroup when nobody has seen anyone for a while. Only the core spots, never the
    /// outer districts: with five times the floor, letting them rally on the far corners spread
    /// four bots across four corners and engagement collapsed to a third of what it had been.
    /// </summary>
    public readonly List<Vector3> RallySpots = new();

    /// <summary>The carved-out holes, kept so bots can be told to stay off them.</summary>
    public readonly List<Rect2> Pits = new();

    /// <summary>Burning ground. Survivable, unlike a pit, so it shapes fights rather than ending them.</summary>
    public readonly List<HazardZone> Hazards = new();

    /// <summary>
    /// Where map weapons sit. Deliberately placed up on decks and out past the pits — a pickup
    /// worth crossing the arena for should cost you a route, not be lying in the open.
    /// </summary>
    public readonly List<Vector3> WeaponSpawns = new();

    /// <summary>
    /// Collision bodies for the blocks explosives can bring down, by index into <see cref="Blocks"/>.
    /// Populated by <see cref="Build"/>, so it is empty until the arena has been realised.
    /// </summary>
    public readonly Dictionary<int, StaticBody3D> FragileBodies = new();

    /// <summary>Where the three vehicles are parked. Out on the ring, clear of the fighting.</summary>
    public readonly List<Vector3> VehicleSpawns = new();

    /// <summary>
    /// Where med kits sit. Their own list, derived from the finished map rather than shared with
    /// the weapon crates.
    ///
    /// Health used to be every fourth weapon spawn, which meant three med kits on a map two hundred
    /// and seventy-eight metres across — you could cross the whole arena bleeding without passing
    /// one. These are laid out on a grid over the entire floor and kept wherever a pawn actually
    /// fits, so health is something you can head towards from anywhere rather than something you
    /// happen upon.
    /// </summary>
    public readonly List<Vector3> HealthSpawns = new();

    /// <summary>
    /// The two capture-the-flag bases, one per team, on opposite sides of the arena.
    ///
    /// Derived rather than declared, for the same reason the med kits are: there are four layouts,
    /// and four sets of hand-placed coordinates is four things to keep in step with geometry that
    /// changes. The pair is placed by nudging outward from the middle along the long axis until
    /// both ends land on ground a pawn can stand on.
    /// </summary>
    public readonly List<Vector3> FlagBases = new();

    // Spread wider apart than they used to be. The four were within about eight percent of each
    // other in value and all the same hue, so wall, cover and deck were effectively one colour and
    // the arena read as a single grey mass. They are still a restrained palette — the look is flat
    // colour and shape — but they are now telling you what kind of thing you are looking at.
    static readonly Color WallTint = new(0.24f, 0.27f, 0.35f);      // structure: darkest, coolest
    static readonly Color CoverTint = new(0.56f, 0.60f, 0.66f);     // things you hide behind
    static readonly Color DeckTint = new(0.40f, 0.51f, 0.66f);      // things you walk on
    static readonly Color AccentTint = new(0.22f, 0.62f, 0.78f);    // the prize: highest ground
    static readonly Color PadTint = new(0.35f, 0.85f, 0.55f);

    Node3D root = null!;

    public Arena(int layout = 0)
    {
        Layout = ((layout % Names.Length) + Names.Length) % Names.Length;

        // A combat arena is a floor with things on it. A puzzle map is the opposite — islands
        // with nothing between them — so it gets no ground plane and adds its own slabs.
        if (!IsPuzzle(Layout))
            FloorSlabs.Add(new Rect2(-HalfWidth, -HalfDepth, HalfWidth * 2f, HalfDepth * 2f));

        AddPerimeter();

        // A puzzle chamber is built and then left alone.
        //
        // None of the passes below belong on one: no districts to cross, no vehicles to park, no
        // weapon crates, no launch pads, and above all no floor filling in the gaps — the gaps are
        // the map. Returning here rather than guarding each pass keeps that a single decision
        // instead of eleven.
        if (Puzzle)
        {
            if (Layout == Names.Length - 2) BuildAntechamber();
            else BuildOrrery();

            MakeStructureBreakable();
            return;
        }

        // A story set is built and then left alone too, and for a stronger reason than a puzzle
        // chamber. Fairview is a town: it has a floor and a perimeter like an arena, and then none
        // of the rest of it. No weapon crates on the green, no tank parked outside the school, no
        // launch pads, and above all nothing destructible — the whole act depends on the place
        // reading as somewhere people live, and one rocket-launcher crate on the corner would say
        // otherwise louder than any amount of dialogue.
        if (Story)
        {
            if (Layout == ConvocationLayout) BuildConvocation();
            else { BuildFairview(); DressFairview(); }
            return;
        }

        switch (Layout)
        {
            case 0: BuildCrossfire(); break;
            case 1: BuildFoundry(); break;
            case 2: BuildAtrium(); break;
            case LaboratoryLayout: BuildLaboratory(); break;
            case ColdstoreLayout: BuildColdstore(); break;
            default: BuildGauntlet(); break;
        }

        // The outer band is the one part of the shared construction that is authored in absolute
        // coordinates - corner decks at 52 by 38, citadels at 104 by 76, scatter between them.
        // On a map two and a half times as wide that is not an outer band, it is a cluster in the
        // middle with empty snow around it. Coldstore builds its own at its own scale.
        if (Layout != ColdstoreLayout)
        {
            AddOuterRing();
            AddVerticality();
            AddOuterDistricts();
        }
        else ColdstoreLoot();

        // Before the interiors rather than after, which is the one ordering that works: a hull
        // needs a long clear run to park on, the rooms are the only thing on the map that can take
        // one away, and the rooms already know how to avoid a vehicle spawn. The other way round,
        // the rooms went up first and two layouts ended up unable to park a tank anywhere.
        ChooseVehicleSpawns();

        // Last of the geometry passes, and deliberately after the outer districts: the interiors
        // sit out on that new floor, which is where all the empty ground was.
        AddInteriors();

        // After every block in the map exists: a pad only knows it is buried once the storey above
        // it has been built.
        ClearLaunchPadCeilings();

        // Before anything reads the block list, and after everything has finished adding to it.
        OpenDriveways();

        // After every block exists and before anything reads them.
        MakeStructureBreakable();

        // Dressing goes on last, once there is a finished map to dress. It reads the block list to
        // find walls and corners, so running it any earlier would decorate half a level - and it
        // adds nothing the passes above would have wanted to know about, because none of them can
        // see it.
        Dress();

        // Last, because it needs the finished map: it picks floor a pawn can stand on.
        ChooseHealthSpawns();
        ChooseFlagBases();

        // On top of the four corner decks, facing the middle. Spawning up high gives you a second
        // to read the arena before dropping into it, and keeps spawns off the routes people run.
        foreach (var spot in ZoneSpots)
            if (MathF.Abs(spot.X) <= CoreHalfWidth && MathF.Abs(spot.Z) <= CoreHalfDepth)
                RallySpots.Add(spot);

        // The four guaranteed spawns sit on the corner decks AddOuterRing builds. An arena that
        // does not run that pass has no decks to stand on, and four spawns hanging five metres
        // over open snow is four players falling on the first frame.
        if (Layout == ColdstoreLayout)
        {
            // Two at each end, on the ground, facing down the approach - which is also what makes
            // the map read as two sides of a siege rather than four corners of a box.
            foreach (int sx in new[] { -1, 1 })
            {
                SpawnPoints.Add(new Vector3(sx * 70f, 1f, StagingZ + 40f));
                SpawnPoints.Add(new Vector3(sx * 70f, 1f, HangarZ + 70f));
            }
        }
        else
        {
            const float DeckTop = 5.3f;
            SpawnPoints.Add(new Vector3(-OuterX, DeckTop, -OuterZ));
            SpawnPoints.Add(new Vector3(OuterX, DeckTop, OuterZ));
            SpawnPoints.Add(new Vector3(OuterX, DeckTop, -OuterZ));
            SpawnPoints.Add(new Vector3(-OuterX, DeckTop, OuterZ));
        }

        AddGroundSpawns();
    }

    /// <summary>How many spawn points an arena aims to carry, corner decks included.</summary>
    public const int SpawnPointTarget = 16;

    /// <summary>
    /// Fill out the spawn points beyond the four corner decks.
    ///
    /// Four was exactly the old roster, so every fighter had a corner to themselves. A roster of
    /// twelve on four spawns means three people materialising on the same deck, which is not a
    /// spawn so much as a three-way knife fight nobody chose.
    ///
    /// Same farthest-point sampling the med kits use, and for the same reason: it maximises the
    /// distance to the nearest already-placed spawn, which is the property that actually matters
    /// when the respawn picker is looking for somewhere away from everybody.
    /// </summary>
    void AddGroundSpawns()
    {
        const float StepX = 19f, StepZ = 17f, Margin = 12f;

        var candidates = new List<Vector3>();

        for (float z = -HalfDepth + Margin; z <= HalfDepth - Margin; z += StepZ)
        for (float x = -HalfWidth + Margin; x <= HalfWidth - Margin; x += StepX)
        {
            var at = new Vector3(x, 1f, z);

            // Room to arrive in, not merely room to stand: a spawn flush against a wall is a spawn
            // you get shot in the back of.
            if (!IsClearOfBlocks(new Vector3(x, 0.6f, z), 2.4f, 2.4f)) continue;
            if (IsOverPit(at)) continue;

            // And it has to lead somewhere.
            //
            // Reported from play: spawning inside a room with no exit. Standing room was the only
            // test a candidate had to pass, and the inside of a sealed vault passes it — the
            // interiors are built before this runs, so the rooms were invisible to the one check
            // that mattered. A spawn you cannot walk out of is not a spawn.
            if (!IsConnected(at)) continue;

            candidates.Add(at);
        }

        while (SpawnPoints.Count < SpawnPointTarget && candidates.Count > 0)
        {
            Vector3 best = candidates[0];
            float bestGap = -1f;

            foreach (var c in candidates)
            {
                float nearest = float.MaxValue;

                foreach (var taken in SpawnPoints)
                    nearest = MathF.Min(nearest, new Vector2(taken.X - c.X, taken.Z - c.Z).Length());

                if (nearest > bestGap) { bestGap = nearest; best = c; }
            }

            // Everything left is on top of something already placed. More spawns than the floor has
            // distinct places to put them is not an improvement.
            if (bestGap < 22f) break;

            SpawnPoints.Add(best);
        }
    }

    /// <summary>
    /// The four outer walls, and the only blocks in the game that cannot be destroyed.
    ///
    /// They are built first and counted here so <see cref="MakeStructureBreakable"/> can find them
    /// by index. Everything else on the map can come down; these cannot, because the outer wall is
    /// not scenery — it is the edge of the world, and a hole in it is a way out of the match.
    /// </summary>
    public const int PerimeterBlocks = 4;

    void AddPerimeter()
    {
        const float t = 1.5f;
        float hy = WallHeight / 2f;

        Blocks.Add(new Block(new Vector3(0, hy, -HalfDepth - t), new Vector3(HalfWidth + t, hy, t), WallTint));
        Blocks.Add(new Block(new Vector3(0, hy, HalfDepth + t), new Vector3(HalfWidth + t, hy, t), WallTint));
        Blocks.Add(new Block(new Vector3(-HalfWidth - t, hy, 0), new Vector3(t, hy, HalfDepth + t), WallTint));
        Blocks.Add(new Block(new Vector3(HalfWidth + t, hy, 0), new Vector3(t, hy, HalfDepth + t), WallTint));
    }

    /// <summary>
    /// The outer band every layout gains from the arena growing. Shared rather than hand-placed
    /// per layout: the middle is what gives each map its character, and the periphery only needs
    /// to be somewhere worth running through on the way there.
    /// </summary>
    void AddOuterRing()
    {
        // Raised corner platforms — reachable by jump from the crates beside them, and high enough
        // to shoot across the arena from.
        foreach (int sx in new[] { -1, 1 })
            foreach (int sz in new[] { -1, 1 })
            {
                Deck(new Vector3(sx * OuterX, 2.6f, sz * OuterZ), new Vector3(7f, 2.6f, 6f));

                // Two steps up onto the platform. Without them a 5.2m deck is a one-way trip:
                // you spawn on it, drop off, and can never get back — and neither can a bot,
                // which is what the navigation graph made obvious.
                Blocks.Add(new Block(new Vector3(sx * (OuterX - 12f), 1.0f, sz * OuterZ),
                                     new Vector3(2.4f, 1.0f, 3f), CoverTint));
                Blocks.Add(new Block(new Vector3(sx * (OuterX - 7.5f), 1.9f, sz * OuterZ),
                                     new Vector3(2.4f, 1.9f, 3f), CoverTint));

                Blocks.Add(new Block(new Vector3(sx * (OuterX - 10f), 1.2f, sz * (OuterZ - 8f)),
                                     new Vector3(2.2f, 1.2f, 2.2f), CoverTint));
            }

        // Mid-edge cover along all four walls, breaking the long runs the bigger arena created.
        foreach (int sx in new[] { -1, 1 })
        {
            Blocks.Add(new Block(new Vector3(sx * OuterX, 1.6f, 0f), new Vector3(3f, 1.6f, 7f), CoverTint));
            Blocks.Add(new Block(new Vector3(sx * 26f, 1.3f, OuterZ), new Vector3(6f, 1.3f, 2.4f), CoverTint));
            Blocks.Add(new Block(new Vector3(sx * 26f, 1.3f, -OuterZ), new Vector3(6f, 1.3f, 2.4f), CoverTint));
        }

        Pad(new Vector3(-OuterX + 2f, 0f, 0f), 15f);
        Pad(new Vector3(OuterX - 2f, 0f, 0f), 15f);

        // Burning strips inside the corner approaches, and only on the Furnace.
        //
        // These used to be on all four maps, four to a map, along with four more out in the corner
        // citadels and two by the causeways — ten patches of burning ground on every arena in the
        // game, none of which meant anything. Hazard scattered everywhere is not danger, it is
        // terrain you learn to walk around, and it made every layout read the same.
        //
        // Now fire belongs to the Custodians, whose whole story is being chained to it, and it is
        // a landmark on their map rather than a texture on all of them.
        if (Layout == 1)
            foreach (int sx in new[] { -1, 1 })
            foreach (int sz in new[] { -1, 1 })
                Hazard(new Rect2(sx * 34f - 5f, sz * 26f - 5f, 10f, 10f));
    }

    /// <summary>
    /// The upper storey, which is different for every arena.
    ///
    /// It used to be one shared block of towers laid over all four layouts, on the reasoning that
    /// the floor plan is what gives a map its character. That was wrong, and a player spotted it
    /// straight away: the vertical layer is the largest and most visible structure on the map, so
    /// making it identical made all four read as the same place however much the ground underneath
    /// them differed. Each layout now gets a second storey that echoes its own floor plan.
    ///
    /// What is shared is the *rule*, not the shape: steps rise no more than about 2.4m and gaps stay
    /// inside 6m, so a route across the roofs always exists without touching the floor. The
    /// navigation graph verifies it — a gap too wide to cross fails connectivity rather than
    /// shipping as a ledge nobody can reach.
    /// </summary>
    void AddVerticality()
    {
        // Parked out on the ring, clear of whatever the layout builds inland.
        VehicleSpawns.Add(new Vector3(-14f, 1f, 42f));
        VehicleSpawns.Add(new Vector3(14f, 1f, 42f));
        VehicleSpawns.Add(new Vector3(0f, 1f, -42f));

        buildingUpperStorey = true;

        switch (Layout)
        {
            case 0: VerticalSpire(); break;
            case 1: VerticalGantries(); break;
            case 2: VerticalBalcony(); break;

            // Nothing here either, for the same reason. A map that is already sixty-four metres of
            // vertical does not want a second storey adding to it, and the ascent pass runs
            // catwalks straight through the shaft.
            case LaboratoryLayout: break;

            default: VerticalAscent(); break;
        }

        buildingUpperStorey = false;
    }

    // ---- the outer districts ----
    //
    // Everything from the old arena edge out to the new wall. The design brief for all of it is
    // that the extra floor has to be worth walking across: five times the area of empty ground
    // would just be five times the walking. So it is dense, it carries the best of the loot and
    // all of the vehicles, and it is threaded with the machinery that makes crossing it a decision
    // — elevators, stairs, moving walls and the pits they shove you into.
    //
    // Every district is reachable on foot by stairs. The elevators are the fast way, never the
    // only way: navigation nodes are built from static blocks, so a district reachable only by
    // elevator would be a district no bot could ever visit.

    void AddOuterDistricts()
    {
        // The causeways are two raised roads crossing at the middle, and the north-south one is a
        // solid deck from z=57 to the back wall across the middle sixteen metres. On every other
        // arena that is a road over open ground. On Coldstore it runs straight through both
        // hangars and both generator yards, filling them to five metres - the first build put the
        // base's back door inside a road. The east-west arm lands on the ridge shelves.
        //
        // Coldstore's long axis IS the route, so a road down it is redundant as well as in the way.
        if (Layout != ColdstoreLayout) AddCauseways();

        AddCornerCitadels();

        // Not on Coldstore. The gauntlet carves two trenches out of the floor and sweeps a crusher
        // along them, and its far-end pair is cut at z = +/-84 across the middle third of the map -
        // which is exactly where that arena's hangars and generators stand. The first build came out
        // with both bases hanging over a hole.
        //
        // Moving the bases was the other option and it is the wrong one: the gauntlet belongs to the
        // vocabulary of the other arenas - industrial machinery in an industrial place - and a
        // crusher on an ice field would be the odd thing here even if it fitted.
        if (Layout != ColdstoreLayout) AddPushWallGauntlets();

        AddOuterScatter();
        AddOuterLoot();
    }

    /// <summary>
    /// Cover and small structures filling the ground between the landmarks.
    ///
    /// Without this the outer band was a landmark every eighty metres with nothing in between, and
    /// crossing it meant a long walk in the open with no decisions in it. The pieces here are
    /// deliberately low and plentiful rather than tall and few: what the band needs is somewhere to
    /// break line of sight every few strides, not more silhouettes on the skyline.
    /// </summary>
    void AddOuterScatter()
    {
        foreach (int sx in new[] { -1, 1 })
        foreach (int sz in new[] { -1, 1 })
        {
            // Staggered cover blocks along the diagonal approaches, which is the route between the
            // core and a citadel that avoids both trenches.
            for (int i = 0; i < 5; i++)
            {
                float t = 0.18f + i * 0.16f;
                float x = sx * Mathf.Lerp(58f, 96f, t);
                float z = sz * Mathf.Lerp(52f, 74f, t);

                Blocks.Add(new Block(new Vector3(x, 1.5f, z), new Vector3(4.5f, 1.5f, 3f), CoverTint));
                Blocks.Add(new Block(new Vector3(x + sx * 7f, 1.0f, z - sz * 8f),
                                     new Vector3(3f, 1.0f, 4.5f), CoverTint));
            }

            // A stepped redoubt between the causeway and the trench, with stairs onto it. Somewhere
            // to fight over that is not a fortress and not open ground.
            float rx = sx * 88f, rz = sz * 40f;

            Deck(new Vector3(rx, 1.3f, rz), new Vector3(11f, 1.3f, 8f), CoverTint);
            Deck(new Vector3(rx + sx * 3f, 2.6f, rz), new Vector3(6f, 2.6f, 5f));

            Ramp(new Vector3(rx - sx * 14f, 0f, rz), sx > 0 ? Vector3.Right : Vector3.Left,
                 2.6f, 2, 4f);

            // Walls along the outer edge, so the band has interior corners rather than being a
            // single open room with a fence round it.
            Blocks.Add(new Block(new Vector3(sx * 124f, 2.4f, sz * 40f),
                                 new Vector3(2f, 2.4f, 20f), WallTint));
            Blocks.Add(new Block(new Vector3(sx * 52f, 2.4f, sz * 90f),
                                 new Vector3(20f, 2.4f, 2f), WallTint));
        }

        // Burning ground either side of each causeway ramp, where everyone funnels on the way out
        // of the core. The Furnace only — see AddOuterRing.
        if (Layout != 1) return;

        foreach (int sx in new[] { -1, 1 })
            Hazard(new Rect2(sx * 70f - 8f, -30f, 16f, 16f));

        foreach (int sz in new[] { -1, 1 })
            Hazard(new Rect2(-38f, sz * 56f - 8f, 16f, 16f));
    }

    /// <summary>
    /// The four approaches out of the core, each a long stepped climb onto a raised causeway. They
    /// are the walking route to everything beyond, so they are wide, obvious, and covered.
    /// </summary>
    void AddCauseways()
    {
        // The stairs and the road they meet have to agree on a height, and the last step has to
        // overlap the road rather than stop just short of it: a gap smaller than a nav cell is
        // still a gap the graph will not link across.
        const float RoadTop = 5.2f;

        foreach (int sx in new[] { -1, 1 })
        {
            Ramp(new Vector3(sx * 64f, 0f, 0f), sx > 0 ? Vector3.Right : Vector3.Left,
                 RoadTop, 5, 7f);

            Deck(new Vector3(sx * 103f, RoadTop * 0.5f, 0f), new Vector3(27f, RoadTop * 0.5f, 8f));

            // Guard rails, so a raised road reads as a road rather than as a ledge.
            foreach (int sz in new[] { -1, 1 })
                Blocks.Add(new Block(new Vector3(sx * 103f, RoadTop + 1.2f, sz * 7f),
                                     new Vector3(27f, 1.2f, 1f), WallTint));
        }

        foreach (int sz in new[] { -1, 1 })
        {
            Ramp(new Vector3(0f, 0f, sz * 48f), sz > 0 ? Vector3.Back : Vector3.Forward,
                 RoadTop, 5, 7f);

            Deck(new Vector3(0f, RoadTop * 0.5f, sz * 80f), new Vector3(8f, RoadTop * 0.5f, 23f));

            foreach (int sx in new[] { -1, 1 })
                Blocks.Add(new Block(new Vector3(sx * 7f, RoadTop + 1.2f, sz * 80f),
                                     new Vector3(1f, 1.2f, 23f), WallTint));
        }
    }

    /// <summary>
    /// A stepped fortress in each far corner: stairs on two faces, an elevator up the middle, and
    /// a roof worth holding. The stairs are the route that always works; the elevator is the one
    /// that gets you there before whoever took the stairs.
    /// </summary>
    void AddCornerCitadels()
    {
        foreach (int sx in new[] { -1, 1 })
        foreach (int sz in new[] { -1, 1 })
        {
            float cx = sx * 104f, cz = sz * 76f;

            // Tiers rise 2.4m each, which is inside the jump apex, so the whole fortress can be
            // climbed without the stairs if you are willing to work for it. The first attempt used
            // four-metre tiers and a sixteen-metre top: unjumpable, and the top surface sat at the
            // wall height the navigation graph deliberately refuses to route along, so the roof was
            // invisible to every bot in the game.
            Deck(new Vector3(cx, 1.2f, cz), new Vector3(22f, 1.2f, 18f), CoverTint);
            Deck(new Vector3(cx + sx * 4f, 2.4f, cz + sz * 3f), new Vector3(15f, 2.4f, 12f));
            Deck(new Vector3(cx + sx * 8f, 3.6f, cz + sz * 6f), new Vector3(8f, 3.6f, 6f), AccentTint);

            // Stairs onto the base from the two inboard faces, so the fortress can be taken on
            // foot from the direction people arrive.
            Ramp(new Vector3(cx - sx * 27f, 0f, cz), sx > 0 ? Vector3.Right : Vector3.Left,
                 2.4f, 2, 4f);
            Ramp(new Vector3(cx, 0f, cz - sz * 23f), sz > 0 ? Vector3.Back : Vector3.Forward,
                 2.4f, 2, 4f);

            // The elevator, running from the ground to a perch above the top tier and waiting at
            // both ends so it can actually be boarded.
            MovingPlatforms.Add(new MovingPlatformDef(
                new Vector3(cx - sx * 14f, 0.6f, cz - sz * 12f),
                new Vector3(cx - sx * 14f, 12.4f, cz - sz * 12f),
                new Vector3(3.4f, 0.35f, 3.4f), period: 9f, dwell: 0.34f));

            // A perch the elevator reaches and the stairs do not, so riding it buys something.
            Deck(new Vector3(cx - sx * 14f, 12.8f, cz - sz * 12f), new Vector3(5f, 0.4f, 5f), AccentTint);

            Pad(new Vector3(cx + sx * 19f, 0f, cz - sz * 15f), 17f);
        }
    }

    /// <summary>
    /// The nasty bit. Each mid-edge of the outer band is a trench with a wall that sweeps along it,
    /// and there is nowhere to stand that the wall does not reach except the far side.
    ///
    /// This is the one piece of machinery that reaches into the simulation rather than being solid
    /// geometry, because Godot resolves a character against a moving body by stopping the character
    /// dead — so a push wall left to the physics engine is just a wall that travels.
    /// </summary>
    void AddPushWallGauntlets()
    {
        foreach (int sx in new[] { -1, 1 })
        {
            float x = sx * 100f;

            // The trench, carved either side of the causeway.
            foreach (int sz in new[] { -1, 1 })
            {
                Carve(new Rect2(x - 24f, sz * 20f - 11f, 48f, 22f));

                // A ledge of safe ground at each end of the sweep, so the gauntlet is survivable
                // if you are quick rather than being a coin toss.
                Deck(new Vector3(x - 27f, 0.6f, sz * 20f), new Vector3(3f, 0.6f, 12f), CoverTint);
                Deck(new Vector3(x + 27f, 0.6f, sz * 20f), new Vector3(3f, 0.6f, 12f), CoverTint);

                MovingPlatforms.Add(new MovingPlatformDef(
                    new Vector3(x - 22f, 1.6f, sz * 20f),
                    new Vector3(x + 22f, 1.6f, sz * 20f),
                    new Vector3(1.2f, 1.6f, 11f), period: 7.5f, dwell: 0.12f, pushes: true));
            }
        }

        // And one across each far end, sweeping toward the wall.
        foreach (int sz in new[] { -1, 1 })
        {
            float z = sz * 84f;
            Carve(new Rect2(-26f, z - 13f, 52f, 26f));

            Deck(new Vector3(0f, 0.6f, z - sz * 16f), new Vector3(14f, 0.6f, 3f), CoverTint);

            MovingPlatforms.Add(new MovingPlatformDef(
                new Vector3(0f, 1.6f, z - sz * 11f),
                new Vector3(0f, 1.6f, z + sz * 11f),
                new Vector3(13f, 1.6f, 1.2f), period: 8.5f, dwell: 0.1f, pushes: true));
        }
    }

    /// <summary>
    /// What makes the walk worth it. The strongest guns and every vehicle live out here, so the
    /// outer band is contested rather than scenery you can ignore.
    /// </summary>
    void AddOuterLoot()
    {
        foreach (int sx in new[] { -1, 1 })
        foreach (int sz in new[] { -1, 1 })
        {
            // Both on tiers the stairs reach. Nothing is gated behind the elevator: navigation
            // nodes are built from static geometry, so a weapon that could only be fetched by
            // riding one would be a weapon no bot could ever contest.
            WeaponSpawns.Add(new Vector3(sx * 112f, 7.7f, sz * 82f));
            WeaponSpawns.Add(new Vector3(sx * 98f, 5.3f, sz * 72f));

            if (Layout == 1) Hazard(new Rect2(sx * 72f - 9f, sz * 60f - 9f, 18f, 18f));
        }

        // On the citadel base, outboard of the second tier so the zone is standing room rather
        // than a point buried inside a block.
        ZoneSpots.Add(new Vector3(-87f, 2.5f, -61f));
        ZoneSpots.Add(new Vector3(87f, 2.5f, 61f));
        ZoneSpots.Add(new Vector3(90f, 5.3f, 0f));
        ZoneSpots.Add(new Vector3(-90f, 5.3f, 0f));

        // Vehicle spawns are not placed here. They are chosen from ground a hull can actually
        // reach — see ChooseVehicleSpawns, which runs once the whole map exists.
    }

    // ---- service roads ----

    /// <summary>
    /// Make sure a hull can actually get to the middle of the map, and open a road if it cannot.
    ///
    /// This is a repair pass in the same spirit as ClearLaunchPadCeilings: the layouts are built by
    /// several independent passes that have never known what the others were doing, and the way
    /// they interact is not something anybody can hold in their head. The Thousand Rooms is what
    /// that produces. Its two warrens seal the map's north and south, the Gauntlet's lane walls
    /// close the middle, and one piece of mid-edge cover - shared by all four arenas, harmless on
    /// the other three - shuts the last gap in its waist. The result was a hull able to circle the
    /// outside forever and never once reach the centre, on the only map where two of the four
    /// objectives stand at x = +/-34, z = 0.
    ///
    /// Nobody wrote that. It is the product of four reasonable passes meeting.
    ///
    /// So rather than hand-editing coordinates until it happens to work - which the next layout
    /// change would silently undo - the arena checks the thing that matters and fixes it only when
    /// it is broken. Three of the four maps come out of here untouched.
    /// </summary>
    void OpenDriveways()
    {
        if (!IsArena(Layout) || VehicleSpawns.Count == 0) return;

        float lane = DriveLane();
        if (SpawnsConnected(lane)) return;

        // Roads in order of what they cost, and only as many as it takes.
        //
        // The waist first, as a PAIR either side of the middle rather than one road through it.
        // The middle of this map is a hole - the Gauntlet carves a pit at its centre that only a
        // moving platform crosses, and that is the whole point of the layout - so a road down the
        // axis arrives at the void and stops. These two run in the eleven metres between the lip
        // of the pit and the edge of the warrens, which is the only ground there is.
        foreach (int side in new[] { -1, 1 })
            CarveRoad(new Rect2(-HalfWidth, side * WaistRoadZ - RoadHalf,
                                HalfWidth * 2f, RoadHalf * 2f));

        if (SpawnsConnected(lane)) return;

        foreach (int side in new[] { -1, 1 })
            CarveRoad(new Rect2(side * RingRoadX - RoadHalf, -HalfDepth,
                                RoadHalf * 2f, HalfDepth * 2f));

        if (SpawnsConnected(lane)) return;

        // Still walled, so something is across the middle itself. This is the expensive one and it
        // is only ever paid for by a layout that leaves no other way in.
        CarveRoad(new Rect2(-RoadHalf, -HalfDepth, RoadHalf * 2f, HalfDepth * 2f));
    }

    /// <summary>Where the north-south service roads run: past the interiors, inside the districts.</summary>
    const float RingRoadX = 60f;

    /// <summary>How far off centre the waist roads run, to pass the central pit rather than into it.</summary>
    const float WaistRoadZ = 12.5f;

    /// <summary>Half-width of a service road: the widest hull that must pass, and room beside it.</summary>
    const float RoadHalf = 5f;

    /// <summary>The clearance a hull needs, taken from the vehicles that actually spawn.</summary>
    static float DriveLane()
    {
        float lane = 0f;
        foreach (var v in Vehicles.Spawnable) lane = MathF.Max(lane, v.HalfExtents.Z + 0.4f);
        return lane;
    }

    /// <summary>
    /// Whether every vehicle spawn can reach every other one.
    ///
    /// Two earlier versions of this asked the wrong question. The first asked whether the middle
    /// of the map could reach ANY spawn, and was satisfied by a road joining the centre to one
    /// corner while three quarters of the ring stayed as cut off as before. The second asked for
    /// every spawn but still seeded from the middle of the map - which on this layout is a pit,
    /// so it was measuring a flood that started on one arbitrary lip of a hole.
    ///
    /// What a driver actually needs is that a hull appearing anywhere can get to a hull appearing
    /// anywhere else. That is a property of the spawns and does not care where the centre is or
    /// whether there is any ground there at all.
    /// </summary>
    bool SpawnsConnected(float lane)
    {
        if (VehicleSpawns.Count < 2) return true;

        var region = DrivableRegion(VehicleSpawns[0], lane);

        foreach (var sp in VehicleSpawns)
            if (!region.Contains((Mathf.RoundToInt(sp.X / DriveCell),
                                  Mathf.RoundToInt(sp.Z / DriveCell)))) return false;

        return true;
    }

    /// <summary>
    /// Take a road out of the block list, leaving whatever of each block survives beside it.
    ///
    /// Only blocks at hull height are touched. A kerb a hull rides over and a walkway it drives
    /// under are both irrelevant to a road and cutting them would take away cover and cover alone.
    /// Blocks are trimmed rather than deleted wherever there is something left to keep, so a wall
    /// crossing the road becomes two walls with a gateway between them.
    /// </summary>
    void CarveRoad(Rect2 road)
    {
        var kept = new List<Block>();

        foreach (var b in Blocks)
        {
            float top = b.Centre.Y + b.HalfExtents.Y;
            float bottom = b.Centre.Y - b.HalfExtents.Y;

            var foot = new Rect2(b.Centre.X - b.HalfExtents.X, b.Centre.Z - b.HalfExtents.Z,
                                 b.HalfExtents.X * 2f, b.HalfExtents.Z * 2f);

            if (top <= 0.35f || bottom >= 2.6f || !foot.Intersects(road)) { kept.Add(b); continue; }

            foreach (var band in Subtract(foot, road))
            {
                // A sliver narrower than it is worth drawing is not cover, it is a splinter left
                // standing in the road.
                if (band.Size.X < 0.6f || band.Size.Y < 0.6f) continue;

                kept.Add(new Block(
                    new Vector3(band.GetCenter().X, b.Centre.Y, band.GetCenter().Y),
                    new Vector3(band.Size.X * 0.5f, b.HalfExtents.Y, band.Size.Y * 0.5f),
                    b.Tint, b.Fragile, b.Surface));
            }
        }

        Blocks.Clear();
        Blocks.AddRange(kept);
    }

    // ---- where vehicles can go ----

    /// <summary>Grid spacing for the drivable sweep. Fine enough to find a gap a hull fits, cheap.</summary>
    const float DriveCell = 4f;

    /// <summary>
    /// Whether a hull of this half-width fits here, standing on the ground.
    ///
    /// A vehicle cannot climb. It is a CharacterBody3D with no step handling whatsoever, so
    /// anything from kerb height up to hull height is a wall to it — which is why this exists
    /// separately from the pawn navigation graph, where most of those same blocks are stairs.
    /// </summary>
    public bool HullFits(Vector3 at, float radius)
    {
        if (IsOverPit(at)) return false;

        foreach (var b in Blocks)
        {
            float top = b.Centre.Y + b.HalfExtents.Y;
            float bottom = b.Centre.Y - b.HalfExtents.Y;

            // Below a kerb a hull rides over it; above hull height it drives underneath.
            if (top <= 0.35f || bottom >= 2.6f) continue;

            if (MathF.Abs(at.X - b.Centre.X) < b.HalfExtents.X + radius
                && MathF.Abs(at.Z - b.Centre.Z) < b.HalfExtents.Z + radius) return false;
        }
        return true;
    }

    /// <summary>The closest point to <paramref name="near"/> where a hull actually fits.</summary>
    public Vector3 NearestDrivable(Vector3 near, float radius)
    {
        Vector3 best = near;
        float bestDist = float.MaxValue;

        // Scanned on exactly the lattice DrivableRegion floods — integer multiples of the cell
        // size — rather than on its own offset grid. They used to disagree, so on Foundry this
        // returned a point that fitted a hull, the flood rounded it to the neighbouring cell,
        // that cell was inside the central spine, and the entire map came back undrivable.
        int nx = Mathf.FloorToInt((HalfWidth - 4f) / DriveCell);
        int nz = Mathf.FloorToInt((HalfDepth - 4f) / DriveCell);

        for (int cx = -nx; cx <= nx; cx++)
        for (int cz = -nz; cz <= nz; cz++)
        {
            var at = new Vector3(cx * DriveCell, 0f, cz * DriveCell);
            if (!HullFits(at, radius)) continue;

            float d = at.DistanceSquaredTo(near with { Y = 0f });
            if (d < bestDist) { bestDist = d; best = at; }
        }
        return best;
    }

    /// <summary>
    /// The largest connected patch of ground a hull can drive around.
    ///
    /// Every cell is visited once: a cell already claimed by an earlier flood is skipped, so the
    /// whole sweep costs about one flood over the lattice however many separate regions a layout
    /// turns out to have.
    /// </summary>
    HashSet<(int, int)> LargestDrivableRegion(float radius)
    {
        var seen = new HashSet<(int, int)>();
        var best = new HashSet<(int, int)>();

        int nx = Mathf.FloorToInt((HalfWidth - 4f) / DriveCell);
        int nz = Mathf.FloorToInt((HalfDepth - 4f) / DriveCell);

        for (int cx = -nx; cx <= nx; cx++)
        for (int cz = -nz; cz <= nz; cz++)
        {
            if (!seen.Add((cx, cz))) continue;

            var at = new Vector3(cx * DriveCell, 0f, cz * DriveCell);
            if (!HullFits(at, radius)) continue;

            var region = DrivableRegion(at, radius);
            foreach (var c in region) seen.Add(c);

            if (region.Count > best.Count) best = region;
        }

        return best;
    }

    /// <summary>Every grid cell a hull can drive to from <paramref name="from"/>.</summary>


    /// <summary>Cell size for the on-foot reachability flood. Fine enough to find a doorway.</summary>
    const float WalkCell = 1.6f;

    /// <summary>
    /// How much open floor a spawn has to be able to walk to before it counts as connected.
    ///
    /// A sealed vault is perhaps forty square metres of perfectly good floor with no way out of it,
    /// so "can I stand up" and "is there room to move" both answer yes inside one. The only question
    /// that separates a room from a cell is how far you can *get*, and this is that question with a
    /// number on it.
    /// </summary>
    const int ReachableCellsNeeded = 400;

    /// <summary>
    /// Flood the walkable floor outward from a point, up to <paramref name="cap"/> cells.
    ///
    /// Written because a player spawned inside a sealed room and could not get out, which is the
    /// worst class of bug this game can have: not a bad fight or an unfair death, but a match you
    /// are simply not in. The interiors pass builds rooms, and the ground-spawn pass runs after it
    /// and only ever asked whether a candidate had room to stand — which is true inside a cell.
    ///
    /// Capped rather than exhaustive. Any spawn that reaches four hundred cells is connected to the
    /// map at large, and continuing to flood the remaining twenty thousand proves nothing further.
    /// </summary>
    public int ReachableFrom(Vector3 start, int cap = ReachableCellsNeeded)
    {
        var seen = new HashSet<(int, int)>();
        var queue = new Queue<(int, int)>();

        var at = (Mathf.RoundToInt(start.X / WalkCell), Mathf.RoundToInt(start.Z / WalkCell));
        seen.Add(at);
        queue.Enqueue(at);

        while (queue.Count > 0 && seen.Count < cap)
        {
            var (cx, cz) = queue.Dequeue();

            foreach (var (dx, dz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
            {
                var next = (cx + dx, cz + dz);
                if (seen.Contains(next)) continue;

                float nx = next.Item1 * WalkCell, nz = next.Item2 * WalkCell;
                if (MathF.Abs(nx) > HalfWidth - 2f || MathF.Abs(nz) > HalfDepth - 2f) continue;

                var p = new Vector3(nx, 0f, nz);

                // A pit is not a route. Walking into one is a death, not a way out of a room, and
                // counting it as connectivity would call a ledge over a trench an escape.
                if (IsOverPit(p)) continue;

                // Tested at the *spawn's* own height, not at floor level.
                //
                // Flooding at ground level called every corner-deck spawn sealed, because the deck
                // it stands on is solid at y=0.6 — the flood could not leave the first cell. What
                // makes a room a room is walls beside you, so the question has to be asked at the
                // height you are actually standing at. Five metres up on a deck, the neighbouring
                // cells are open air and the flood spreads freely, which is correct: you can step
                // off a deck in any direction.
                if (!IsClearOfBlocks(p with { Y = start.Y + 0.6f }, Pawn.Radius, Pawn.Height)) continue;

                seen.Add(next);
                queue.Enqueue(next);
            }
        }

        return seen.Count;
    }

    /// <summary>Whether somewhere is connected to the rest of the map rather than walled in.</summary>
    public bool IsConnected(Vector3 at) => ReachableFrom(at) >= ReachableCellsNeeded;

    public HashSet<(int, int)> DrivableRegion(Vector3 from, float radius)
    {
        var seen = new HashSet<(int, int)>();
        var queue = new Queue<(int, int)>();

        var start = (Mathf.RoundToInt(from.X / DriveCell), Mathf.RoundToInt(from.Z / DriveCell));
        seen.Add(start);
        queue.Enqueue(start);

        while (queue.Count > 0)
        {
            var (cx, cz) = queue.Dequeue();

            foreach (var (dx, dz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
            {
                var next = (cx + dx, cz + dz);
                if (seen.Contains(next)) continue;

                float nx = next.Item1 * DriveCell, nz = next.Item2 * DriveCell;
                if (MathF.Abs(nx) > HalfWidth - 2f || MathF.Abs(nz) > HalfDepth - 2f) continue;

                seen.Add(next);
                if (HullFits(new Vector3(nx, 0f, nz), radius)) queue.Enqueue(next);
            }
        }

        // The frontier cells were added to stop them being revisited but are not themselves
        // drivable, so they are filtered out before the set is handed back.
        seen.RemoveWhere(c => !HullFits(new Vector3(c.Item1 * DriveCell, 0f, c.Item2 * DriveCell), radius));
        return seen;
    }

    /// <summary>
    /// Park the vehicles on ground they can actually get out of.
    ///
    /// Hand-placing these went wrong three times running, and the last time it went wrong on every
    /// map at once: the spawns passed a clearance check and were still walled in by a trench on one
    /// side, a citadel on another and a redoubt on a third, so a tank could turn on the spot and go
    /// nowhere. Clearance was never the question — connectivity was.
    ///
    /// So they are derived instead. Flood the ground a hull can reach from the middle of the map,
    /// then take the furthest reachable cell in each quadrant. That keeps the intent that made them
    /// outer-district loot — they are as far from the core as the geometry allows — while making it
    /// impossible for one to be somewhere a vehicle cannot leave.
    /// </summary>
    void ChooseVehicleSpawns()
    {
        float radius = 0f, turn = 0f;

        foreach (var v in Vehicles.Spawnable)
        {
            if (v.Flies) continue;
            radius = MathF.Max(radius, v.HalfExtents.Z + 0.4f);
            turn = MathF.Max(turn, new Vector2(v.HalfExtents.X, v.HalfExtents.Z).Length() + 0.3f);
        }

        // The biggest patch of drivable ground on the map, wherever it happens to be.
        //
        // This used to flood from the open ground nearest the middle, which was already a fix for
        // flooding from the middle itself — three of the four original layouts have a structure
        // standing on the origin. The Laboratory broke it again and harder: it is a building, so
        // the ground nearest the origin is INSIDE it, and the flood filled a room and stopped.
        // The map came out with too few vehicle spawns to park one of each, and OpenDriveways
        // could not report the problem because it has nothing to check when there are fewer than
        // two spawns to connect.
        //
        // Asking which patch is biggest has no such assumption in it. Nearest-to-something is a
        // heuristic about where maps put their open ground; largest is the thing actually wanted.
        var region = LargestDrivableRegion(radius);

        VehicleSpawns.Clear();

        // Candidates: reachable ground with turning room as well as a lane, or the hull arrives
        // somewhere it can drive into and never drive out of.
        var candidates = new List<Vector3>();

        foreach (var (cx, cz) in region)
        {
            var at = new Vector3(cx * DriveCell, 1f, cz * DriveCell);
            if (HullFits(at, turn)) candidates.Add(at);
        }

        // Ranked by proximity to a band well out from the core, not by raw distance. Raw distance
        // put every vehicle in a corner jammed against the perimeter wall — technically the
        // furthest reachable ground and a poor place to keep a car.
        const float PreferredRadius = 105f;

        candidates.Sort((a, b) =>
            MathF.Abs(new Vector2(a.X, a.Z).Length() - PreferredRadius)
                .CompareTo(MathF.Abs(new Vector2(b.X, b.Z).Length() - PreferredRadius)));

        // Greedy, with a separation rule rather than one per quadrant. Quadrants looked tidier and
        // failed on Foundry, whose geometry leaves one quadrant with no turning room at all — and
        // an arena short of a vehicle is worse than one whose vehicles are unevenly spread.
        const float MinApart = 55f;

        foreach (var at in candidates)
        {
            bool crowded = false;
            foreach (var taken in VehicleSpawns)
                if (taken.DistanceTo(at) < MinApart) { crowded = true; break; }

            if (crowded) continue;

            VehicleSpawns.Add(at);
            if (VehicleSpawns.Count == 4) break;
        }
    }

    /// <summary>
    /// How far out along the arena's long axis each flag base sits, as a fraction of the half
    /// width. Far enough apart that a run is a journey; close enough that it is not a commute.
    /// </summary>
    const float FlagBaseReach = 0.46f;

    /// <summary>
    /// Place the two flag bases, one either side of the middle.
    ///
    /// Nudged rather than snapped to a grid: the ideal spot is a fixed distance out along X, and
    /// if that lands inside a structure the search walks outward in rings until it finds floor.
    /// Every layout puts something on or near the origin — Crossfire's spire, the Atrium's tower —
    /// so the ideal point being blocked is the normal case rather than the exception.
    /// </summary>
    void ChooseFlagBases()
    {
        FlagBases.Clear();

        foreach (int side in new[] { -1, 1 })
        {
            float wantX = side * HalfWidth * FlagBaseReach;
            Vector3? found = null;

            for (float radius = 0f; radius <= 46f && found == null; radius += 4f)
            for (int step = 0; step < 12 && found == null; step++)
            {
                float angle = step * MathF.Tau / 12f;
                float x = wantX + MathF.Cos(angle) * radius;
                float z = MathF.Sin(angle) * radius;

                if (MathF.Abs(x) > HalfWidth - 8f || MathF.Abs(z) > HalfDepth - 8f) continue;

                var at = new Vector3(x, 1f, z);

                // Room for the flag and for a fight over it, not merely for a standing pawn.
                if (!IsClearOfBlocks(new Vector3(x, 0.6f, z), 3.2f, 2.6f)) continue;
                if (IsOverPit(at)) continue;

                found = at;
            }

            // A layout with nowhere clear at the ideal radius falls back to the middle of its half,
            // which every arena has open floor in.
            FlagBases.Add(found ?? new Vector3(side * HalfWidth * 0.3f, 1f, 0f));
        }
    }

    /// <summary>
    /// How many med kits an arena aims to carry. Reached where the geometry allows it.
    ///
    /// Twenty-eight sounds a lot until you divide it into 278x206 metres: it works out at roughly
    /// one med kit per sixty-metre square, and the measured worst case is still a thirty-odd metre
    /// walk. Sixteen left corners of the map fifty metres from the nearest one.
    /// </summary>
    public const int HealthCrateTarget = 34;

    /// <summary>
    /// Med kits for THIS arena, scaled by floor area.
    ///
    /// A fixed thirty-four was right while every map was the same size. On a floor five times
    /// larger the same thirty-four spread to eighty-two metres from the nearest kit, which the
    /// harness caught immediately: the number that matters is the longest walk to health, and
    /// that is a property of area, not of a constant.
    /// </summary>
    public int HealthCrates => Mathf.RoundToInt(
        HealthCrateTarget * (HalfWidth * HalfDepth) / (StandardHalfWidth * StandardHalfDepth));

    /// <summary>
    /// Lay med kits over the whole floor.
    ///
    /// They used to be every fourth weapon crate, which on a map this size meant three of them, all
    /// on routes chosen for where a *weapon* should be. Being hurt and having nowhere to go is not
    /// tension, it is just a long walk, and it pushed every wounded fight into the same three spots.
    ///
    /// A grid rather than hand-placed spots, because there are four layouts and this has to work on
    /// all of them without four sets of coordinates to keep in step with the geometry. Anything the
    /// grid lands inside a block, over a pit, or on top of a weapon crate is dropped; the rest is
    /// thinned down to the target so the spacing stays even instead of clumping wherever the map
    /// happens to be open.
    /// </summary>
    void ChooseHealthSpawns()
    {
        HealthSpawns.Clear();

        const float StepX = 15f, StepZ = 14f;
        const float Margin = 7f;

        var candidates = new List<Vector3>();

        for (float z = -HalfDepth + Margin; z <= HalfDepth - Margin; z += StepZ)
        for (float x = -HalfWidth + Margin; x <= HalfWidth - Margin; x += StepX)
        {
            var at = new Vector3(x, 1f, z);

            // Room for a standing pawn, measured from just above the floor. A crate half inside a
            // wall is a crate nobody can reach.
            if (!IsClearOfBlocks(new Vector3(x, 0.6f, z), 1.4f, 2.2f)) continue;
            if (IsOverPit(at)) continue;

            // Never on top of a weapon crate: two pickups in one place is one pickup you cannot see.
            bool onACrate = false;
            foreach (var w in WeaponSpawns)
                if (new Vector2(w.X - x, w.Z - z).Length() < 7f) { onACrate = true; break; }

            if (!onACrate) candidates.Add(at);
        }

        if (candidates.Count == 0) return;

        // Farthest-point sampling, not a stride through the grid order.
        //
        // A stride was the first attempt and it was quietly terrible: the grid is generated row by
        // row, so taking every nth entry and stopping at the target covers the first seven rows of
        // ten and leaves the whole far side of the map without a med kit. Measured worst case on
        // Crossfire was 110 metres from the nearest one, on an arena where a full crossing is 278.
        //
        // This picks each kit at the point furthest from every kit already placed, which optimises
        // exactly the thing that matters — the longest walk to health anywhere on the floor — and
        // does it without caring what shape the layout is.
        HealthSpawns.Add(candidates[0]);

        while (HealthSpawns.Count < HealthCrates && HealthSpawns.Count < candidates.Count)
        {
            Vector3 best = candidates[0];
            float bestGap = -1f;

            foreach (var c in candidates)
            {
                float nearest = float.MaxValue;

                foreach (var taken in HealthSpawns)
                    nearest = MathF.Min(nearest, new Vector2(taken.X - c.X, taken.Z - c.Z).Length());

                if (nearest > bestGap) { bestGap = nearest; best = c; }
            }

            // Everything left is already on top of something placed. More kits than the floor has
            // distinct places to put them is not an improvement.
            if (bestGap < 12f) break;

            HealthSpawns.Add(best);
        }
    }

    /// <summary>
    /// A stepped pyramid of concentric slabs climbing to <paramref name="top"/>. Concentric rather
    /// than a single column so it can be climbed from any side — a tower with one staircase is a
    /// chokepoint, and up here that reads as a dead end.
    /// </summary>
    void Tower(float x, float z, float top, float baseHalf, int tiers = 3)
    {
        for (int i = 0; i < tiers; i++)
        {
            float h = top * (i + 1) / tiers;
            float half = baseHalf * (1f - i * 0.72f / tiers);
            Color tint = i == tiers - 1 ? AccentTint : (i == 0 ? CoverTint : DeckTint);
            Deck(new Vector3(x, h * 0.5f, z), new Vector3(half, h * 0.5f, half), tint);
        }
    }

    /// <summary>
    /// Crossfire — a high cross mirroring the cross on the floor. Two skybridges span the whole
    /// arena and meet directly over the central tier, so the best position on the map is also the
    /// most exposed one: everything below can see you and you can see all of it.
    /// </summary>
    void VerticalSpire()
    {
        foreach (int sx in new[] { -1, 1 })
        foreach (int sz in new[] { -1, 1 })
            Tower(sx * 30f, sz * 24f, 7.2f, 5.5f);

        // The cross itself, one jump above the tower tops.
        Deck(new Vector3(0f, 9.6f, 0f), new Vector3(52f, 0.4f, 3f));
        Deck(new Vector3(0f, 9.6f, 0f), new Vector3(3f, 0.4f, 38f));

        // A crow's nest over the junction, reached from the bridges themselves.
        Deck(new Vector3(0f, 12.0f, 0f), new Vector3(4.5f, 0.4f, 4.5f), AccentTint);

        foreach (int sx in new[] { -1, 1 })
            Deck(new Vector3(sx * 46f, 9.6f, 0f), new Vector3(6f, 0.4f, 7f), CoverTint);

        Pad(new Vector3(-30f, 0f, 0f), 18f);
        Pad(new Vector3(30f, 0f, 0f), 18f);
    }

    /// <summary>
    /// Foundry — gantries hugging the long walls in a full circuit, deliberately asymmetric: the
    /// north wall carries a second level the south wall does not. It rewards learning the map
    /// rather than reading it, and it keeps the fighting off the centre spine.
    /// </summary>
    void VerticalGantries()
    {
        foreach (int sz in new[] { -1, 1 })
            Deck(new Vector3(0f, 6.4f, sz * 34f), new Vector3(46f, 0.4f, 2.8f));

        // The ends, closing the circuit.
        foreach (int sx in new[] { -1, 1 })
            Deck(new Vector3(sx * 44f, 6.4f, 0f), new Vector3(2.8f, 0.4f, 34f));

        // Access towers at the four corners of the circuit.
        foreach (int sx in new[] { -1, 1 })
        foreach (int sz in new[] { -1, 1 })
            Tower(sx * 44f, sz * 34f, 6.4f, 4.6f);

        // The upper deck, north wall only.
        Deck(new Vector3(0f, 10.6f, -34f), new Vector3(28f, 0.4f, 2.6f), AccentTint);
        foreach (int sx in new[] { -1, 1 })
            Deck(new Vector3(sx * 32f, 8.5f, -34f), new Vector3(3.2f, 0.4f, 2.6f), CoverTint);

        // Spurs reaching in toward the spine catwalks, so the circuit is not sealed off from the
        // middle of the map.
        foreach (int sx in new[] { -1, 1 })
        foreach (int sz in new[] { -1, 1 })
            Deck(new Vector3(sx * 24f, 6.4f, sz * 24f), new Vector3(2.6f, 0.4f, 8f), CoverTint);

        Pad(new Vector3(-44f, 0f, 0f), 17f);
        Pad(new Vector3(44f, 0f, 0f), 17f);
    }

    /// <summary>
    /// Atrium — a balcony running the full perimeter with the middle left completely open, so the
    /// upper level is a ring you circle and drop from rather than a place you cross. It doubles the
    /// moat's logic one storey up: the centre is the prize and there is nothing to hide behind.
    /// </summary>
    void VerticalBalcony()
    {
        // Held inside the corner spawn decks rather than run to the wall. At x = 50 the side rails
        // passed straight through all four spawns, which the spawn-clearance test caught: you would
        // have started the match with a walkway through your head.
        foreach (int sz in new[] { -1, 1 })
            Deck(new Vector3(0f, 7.4f, sz * 40f), new Vector3(44f, 0.4f, 3.5f));

        foreach (int sx in new[] { -1, 1 })
            Deck(new Vector3(sx * 46f, 7.4f, 0f), new Vector3(3.5f, 0.4f, 30f));

        // Access at the midpoint of each side, for the same reason — a tower in a corner and a
        // spawn deck in a corner want the same ground.
        foreach (int sx in new[] { -1, 1 })
            Tower(sx * 41f, 0f, 7.4f, 5.5f);

        foreach (int sz in new[] { -1, 1 })
            Tower(0f, sz * 33f, 7.4f, 5.5f);

        // Diving platforms cantilevered inward off the balcony corners — the committed way down
        // into the middle, one jump below the ring so stepping out is deliberate.
        foreach (int sx in new[] { -1, 1 })
        foreach (int sz in new[] { -1, 1 })
            Deck(new Vector3(sx * 38f, 5.6f, sz * 32f), new Vector3(4.5f, 0.4f, 4.5f), AccentTint);

        Pad(new Vector3(-46f, 0f, 22f), 20f);
        Pad(new Vector3(46f, 0f, -22f), 20f);
    }

    /// <summary>
    /// Gauntlet — a climb from both ends to a single perch above the middle pit. The lanes below
    /// stay the map's identity; this turns the whole arena into a race for one spot, with the
    /// longest fall on any of the four waiting underneath it.
    /// </summary>
    void VerticalAscent()
    {
        // Two staircases climbing inward, offset in Z so the two sides are not mirror images and
        // you can be flanked on the way up.
        foreach (int sx in new[] { -1, 1 })
        {
            float zOff = sx > 0 ? 20f : -20f;

            Deck(new Vector3(sx * 46f, 1.3f, zOff), new Vector3(6f, 1.3f, 6f), CoverTint);
            Deck(new Vector3(sx * 36f, 2.4f, zOff * 0.8f), new Vector3(5.5f, 2.4f, 5.5f));
            Deck(new Vector3(sx * 27f, 3.6f, zOff * 0.55f), new Vector3(5f, 3.6f, 5f));
            Deck(new Vector3(sx * 19f, 4.8f, zOff * 0.3f), new Vector3(4.5f, 4.8f, 4.5f), AccentTint);
            Deck(new Vector3(sx * 12f, 6.0f, 0f), new Vector3(4f, 0.4f, 4f), AccentTint);
        }

        // The perch, spanning the pit. Nothing else on the map is this high, and falling off it
        // lands you in the gap.
        Deck(new Vector3(0f, 8.2f, 0f), new Vector3(7f, 0.4f, 6f), AccentTint);
        Deck(new Vector3(0f, 10.4f, 0f), new Vector3(3.5f, 0.4f, 3.5f), AccentTint);

        // Perimeter perches looking down the lanes, so the climb is not the only high ground.
        foreach (int sz in new[] { -1, 1 })
        {
            Deck(new Vector3(-46f, 6.6f, sz * 36f), new Vector3(5f, 0.4f, 4f));
            Deck(new Vector3(46f, 6.6f, sz * 36f), new Vector3(5f, 0.4f, 4f));
            Tower(-38f, sz * 36f, 4.4f, 4f, 2);
            Tower(38f, sz * 36f, 4.4f, 4f, 2);
        }

        Pad(new Vector3(-30f, 0f, 34f), 19f);
        Pad(new Vector3(30f, 0f, -34f), 19f);
    }

    // ---- construction helpers ----

    /// <summary>
    /// True while the upper storey is being laid down, so the thin walkways built there come out
    /// destructible without every call site having to say so.
    /// </summary>
    bool buildingUpperStorey;

    /// <summary>A walkway rather than structure. Thin decks are the things worth blowing out.</summary>
    const float FragileThickness = 0.5f;

    void Deck(Vector3 centre, Vector3 halfExtents, Color? tint = null,
              SurfaceKind surface = SurfaceKind.Panel)
        => Blocks.Add(new Block(centre, halfExtents, tint ?? DeckTint,
                                buildingUpperStorey && halfExtents.Y <= FragileThickness,
                                surface));

    /// <summary>
    /// A staircase of boxes climbing to <paramref name="top"/>. Steps rather than a slope because
    /// everything else in the arena is an axis-aligned box, and a ramp mesh would need its own
    /// collision shape and would look out of place.
    /// </summary>
    void Ramp(Vector3 baseAt, Vector3 dir, float top, int steps, float width)
    {
        for (int i = 1; i <= steps; i++)
        {
            float h = top * i / steps;
            Vector3 at = baseAt + dir * (i * 2.4f);
            Blocks.Add(new Block(at with { Y = h * 0.5f },
                                 new Vector3(MathF.Abs(dir.X) > 0.5f ? 1.2f : width, h * 0.5f,
                                             MathF.Abs(dir.Z) > 0.5f ? 1.2f : width),
                                 CoverTint));
        }
    }

    /// <summary>
    /// A walled room with a roof and doorways cut into it.
    ///
    /// The arenas were built almost entirely out of horizontal surfaces — decks, platforms, ledges,
    /// gantries — and the result was open ground with things standing on it. Every sightline ran
    /// the width of the map, every fight was a shooting gallery at range, and the only cover was
    /// something to stand behind rather than somewhere to be.
    ///
    /// A room is the opposite kind of space. It cuts sightlines rather than raising you above them,
    /// it makes a corner worth holding, and it gives a shotgun somewhere it beats a rifle. It also
    /// makes an arena feel like a place rather than a diagram — the reason a hallway is more
    /// memorable than a platform is that it has a shape you can be inside of.
    ///
    /// Doorways are gaps in the walls rather than holes in a mesh: each wall is built as up to two
    /// segments with a hole between them, which stays inside the "everything is an axis-aligned
    /// box" rule the whole game depends on for its navigation and its clearance tests.
    /// </summary>
    /// <param name="centre">Middle of the floor of the room.</param>
    /// <param name="half">Half the interior footprint, and the wall height in Y.</param>
    /// <param name="doors">Which sides get a doorway: -X, +X, -Z, +Z in that order.</param>
    /// <param name="roofed">Whether to lid it. An open room is a courtyard; a lid is a corridor.</param>
    /// <returns>False when the site was already occupied and the room was skipped.</returns>
    bool Room(Vector3 centre, Vector3 half, bool[] doors, bool roofed = true, Color? tint = null,
              SurfaceKind surface = SurfaceKind.Panel)
    {
        const float Thick = 0.9f;
        const float DoorHalf = 2.4f;

        // Shift onto clear ground if the intended site is taken, but only a little.
        //
        // Skipping outright was the first attempt and it was far too brittle: the Furnace's halls
        // sit ten metres from a corner deck and the Glasshouse's bays fourteen, so both layouts
        // silently built nothing at all and the arenas came out exactly as bare as before. A room
        // nudged eight metres is still the room that was designed; a room that does not exist is
        // not a composition decision, it is a hole.
        if (FreeRoomSite(centre, half) is not { } site) return false;

        centre = site;
        LastRoomAt = site;

        Color wall = tint ?? WallTint;
        float y = centre.Y + half.Y;

        // Each wall runs the full span unless it has a doorway, in which case it becomes the two
        // pieces either side of the gap. A span too short to leave anything either side of a door
        // is simply left open — better an oversized opening than a doorframe with no wall in it.
        void Side(Vector3 at, float span, bool alongX, bool door)
        {
            Vector3 Half(float s) => alongX
                ? new Vector3(s, half.Y, Thick)
                : new Vector3(Thick, half.Y, s);

            if (!door) { Deck(at with { Y = y }, Half(span), wall, surface); return; }

            float piece = (span - DoorHalf) * 0.5f;
            if (piece < 1.2f) return;

            float off = DoorHalf + piece;
            Vector3 step = alongX ? new Vector3(off, 0f, 0f) : new Vector3(0f, 0f, off);

            Deck((at - step) with { Y = y }, Half(piece), wall, surface);
            Deck((at + step) with { Y = y }, Half(piece), wall, surface);
        }

        Side(centre - new Vector3(half.X, 0f, 0f), half.Z, alongX: false, doors[0]);
        Side(centre + new Vector3(half.X, 0f, 0f), half.Z, alongX: false, doors[1]);
        Side(centre - new Vector3(0f, 0f, half.Z), half.X, alongX: true, doors[2]);
        Side(centre + new Vector3(0f, 0f, half.Z), half.X, alongX: true, doors[3]);

        RoomsBuilt++;

        if (!roofed) return true;

        // Thin, and built outside the upper-storey pass, so a roof is solid rather than something
        // a rocket takes out from underneath the people standing on it.
        Deck(centre with { Y = centre.Y + half.Y * 2f + 0.3f },
             new Vector3(half.X + Thick, 0.3f, half.Z + Thick), DeckTint, surface);

        return true;
    }

    /// <summary>
    /// Whether a room can be built here without burying something that is already there.
    ///
    /// Every interior in the game is placed by hand, over districts also placed by hand, and the
    /// first pass at them landed walls on four corner spawns, two weapon crates, a capture zone,
    /// two launch pads and the ground the vehicles park on. Twenty failures, every one of them a
    /// coordinate somebody would otherwise have had to find by playing the map.
    ///
    /// So the rooms ask instead. A site that is not free is skipped rather than shifted: an
    /// interior is a composition, and a chamber slid eleven metres to the left is not the room that
    /// was designed — better to lose it and see the gap.
    /// </summary>
    /// <summary>
    /// The nearest place to <paramref name="centre"/> a room of this size fits, or null.
    ///
    /// Spirals outward in the same shape the launch pads use, and for the same reason: hand-placed
    /// geometry laid over other hand-placed geometry needs one rule that resolves the collisions,
    /// not twenty coordinates that the next layout change invalidates.
    /// </summary>
    Vector3? FreeRoomSite(Vector3 centre, Vector3 half)
    {
        if (RoomSiteIsFree(centre, half)) return centre;

        for (float r = 6f; r <= 30f; r += 6f)
        for (int a = 0; a < 12; a++)
        {
            float th = a * MathF.Tau / 12f;
            var at = centre with
            {
                X = centre.X + MathF.Cos(th) * r,
                Z = centre.Z + MathF.Sin(th) * r,
            };

            if (MathF.Abs(at.X) > HalfWidth - half.X - 4f) continue;
            if (MathF.Abs(at.Z) > HalfDepth - half.Z - 4f) continue;
            if (RoomSiteIsFree(at, half)) return at;
        }

        return null;
    }

    bool RoomSiteIsFree(Vector3 centre, Vector3 half)
    {
        // Generous margin. A wall that merely touches a spawn is still a wall someone materialises
        // inside of, and a doorway one metre from a weapon crate is a crate you cannot walk around.
        float mx = half.X + 4f;
        float mz = half.Z + 4f;

        // Height matters, and leaving it out cost both of the low-walled layouts their interiors.
        // A capture zone nine metres up on a catwalk is not buried by a two-metre wall underneath
        // it, and a spawn on a corner deck five metres up is not buried by a courtyard beside it —
        // but a flat footprint test says both are, which is why the Furnace's crucible and every
        // one of the Glasshouse's bays refused to build.
        float ceiling = centre.Y + half.Y * 2f + 1.5f;

        bool Inside(Vector3 p)
            => p.Y < ceiling
               && MathF.Abs(p.X - centre.X) < mx
               && MathF.Abs(p.Z - centre.Z) < mz;

        foreach (var z in ZoneSpots) if (Inside(z)) return false;
        foreach (var w in WeaponSpawns) if (Inside(w)) return false;
        // Vehicles need far more clearance than a fighter: the harness asks whether a hull can
        // turn on the spot where it parks, and a room built at the ordinary margin is close enough
        // to answer no. Eight metres of extra keep-clear is roughly one tank length.
        foreach (var v in VehicleSpawns)
            if (v.Y < ceiling
                && MathF.Abs(v.X - centre.X) < mx + 9f
                && MathF.Abs(v.Z - centre.Z) < mz + 9f) return false;
        foreach (var s in SpawnPoints) if (Inside(s)) return false;

        // Pads are checked both ways round: a room must not sit on a pad, and a pad's whole arc
        // must not end at a roof. ClearLaunchPadCeilings runs after this and would otherwise spend
        // its search budget relocating pads away from rooms that had no business being over them.
        foreach (var pad in LaunchPads) if (Inside(pad.Centre)) return false;

        // The corner decks, which are where everybody spawns and which are not in SpawnPoints yet:
        // they are added at the very end of construction, after the geometry exists.
        foreach (int sx in new[] { -1, 1 })
        foreach (int sz in new[] { -1, 1 })
            if (Inside(new Vector3(sx * OuterX, 0f, sz * OuterZ))) return false;

        // Not over a hole, and not straddling the lip of one — a room half over a trench is a room
        // with a floor missing.
        foreach (int cx in new[] { -1, 0, 1 })
        foreach (int cz in new[] { -1, 0, 1 })
            if (IsOverPit(new Vector3(centre.X + cx * mx, 0f, centre.Z + cz * mz))) return false;

        // And the ground it stands on has to be empty. This is the check that was missing when the
        // Glasshouse put a seed vault inside a district wall: the crate at its centre was correctly
        // placed in the middle of the room, and the room was inside a building.
        // The interior only, not the wall line: a room whose wall happens to abut a district wall
        // is fine and quite often good, and testing out to the full footprint was strict enough
        // that two layouts built nothing at all again.
        for (int cx = -1; cx <= 1; cx++)
        for (int cz = -1; cz <= 1; cz++)
        {
            var at = new Vector3(centre.X + cx * half.X * 0.6f,
                                 centre.Y + 0.6f,
                                 centre.Z + cz * half.Z * 0.6f);

            if (!IsClearOfBlocks(at, Pawn.Radius, Pawn.Height)) return false;
        }

        return true;
    }

    /// <summary>
    /// Anything flush with the floor is paint, not structure. Pad plates and burning ground.
    /// </summary>
    const float FloorDecalTop = 0.5f;

    /// <summary>
    /// Mark every wall and platform in the arena destructible.
    ///
    /// This used to be five to eleven blocks per map — the thin walkways of the upper storey, and
    /// nothing else — on the reasoning that a map which erodes ends every long match in a flat box.
    /// That reasoning was sound and the result was still wrong, because what actually happened was
    /// that the one skybridge on the map was the only thing anybody ever shot at, and every other
    /// wall in the arena was permanent in a way that read as painted-on rather than built.
    ///
    /// The flat-box worry is answered by the rebuild timer rather than by immortal geometry. Nothing
    /// stays down: a wall comes back on its own, and the bigger it was the longer it takes, so a
    /// long match is a map that keeps changing shape rather than one that wears away.
    ///
    /// Two exceptions, and only two. The perimeter, because a hole in the edge of the world is a way
    /// out of it. And anything lying flat on the floor — launch pad plates, burning ground — which
    /// is paint rather than something you could knock down.
    /// </summary>
    void MakeStructureBreakable()
    {
        for (int i = PerimeterBlocks; i < Blocks.Count; i++)
        {
            var b = Blocks[i];

            if (b.Fragile) continue;
            if (b.Centre.Y + b.HalfExtents.Y <= FloorDecalTop) continue;

            // b.Surface carried through. It was not, and that was a live bug waiting for the
            // first combat arena to name a material: this pass rebuilds nearly every block in the
            // map, so anything a builder had chosen was silently reset to the default plating on
            // the way past. Nothing had chosen one yet, which is the only reason it never showed.
            Blocks[i] = new Block(b.Centre, b.HalfExtents, b.Tint, fragile: true, surface: b.Surface);
        }
    }

    /// <summary>Burning ground, marked by a glowing slab flush with the floor.</summary>
    void Hazard(Rect2 area, float damagePerSecond = 34f)
    {
        Hazards.Add(new HazardZone(area, 2.2f, damagePerSecond));

        var centre = area.GetCenter();
        Blocks.Add(new Block(new Vector3(centre.X, 0.05f, centre.Y),
                             new Vector3(area.Size.X * 0.5f, 0.05f, area.Size.Y * 0.5f),
                             new Color(0.95f, 0.28f, 0.16f)));
    }

    /// <summary>Block index of each pad's plate, so a pad and its plate can be moved together.</summary>
    readonly List<int> padPlates = new();

    void Pad(Vector3 at, float impulse = 17f)
    {
        LaunchPads.Add(new LaunchPad(at, 2.4f, impulse));
        padPlates.Add(Blocks.Count);
        Blocks.Add(new Block(at with { Y = 0.16f }, new Vector3(2.2f, 0.16f, 2.2f), PadTint));
    }

    /// <summary>Top of a pad's plate — where a pawn standing on one actually is.</summary>
    const float PadTop = 0.32f;

    /// <summary>
    /// How high an impulse carries from a standing start. Straight out of <c>v² / 2g</c>, using the
    /// same gravity the pawn actually falls under.
    /// </summary>
    public static float PadApex(float impulse) => impulse * impulse / (2f * Pawn.Gravity);

    /// <summary>
    /// Clear vertical distance above a point before something solid stops you.
    ///
    /// A cheap AABB scan rather than a raycast: this runs while the arena is still being built,
    /// before there is a physics world to query, and the whole map is axis-aligned boxes anyway.
    /// </summary>
    public float PadHeadroom(Vector3 at)
    {
        float lowest = float.MaxValue;

        foreach (var b in Blocks)
        {
            float bottom = b.Centre.Y - b.HalfExtents.Y;

            // Only things overhead can cap a jump. A pad's own plate sits down on the floor.
            if (bottom <= PadTop + 0.2f) continue;

            // A pawn is a body, not a point, so a deck edge a hand's width to the side still takes
            // the top off a jump. Same radius the standing-clearance test uses.
            float dx = MathF.Max(0f, MathF.Abs(at.X - b.Centre.X) - b.HalfExtents.X);
            float dz = MathF.Max(0f, MathF.Abs(at.Z - b.Centre.Z) - b.HalfExtents.Z);
            if (dx * dx + dz * dz >= 1.4f * 1.4f) continue;

            lowest = MathF.Min(lowest, bottom);
        }

        return lowest == float.MaxValue ? float.MaxValue : lowest - PadTop;
    }

    /// <summary>
    /// Move any pad that got built underneath something.
    ///
    /// Pads were placed by hand while laying out each map's floor, and then the upper storey was
    /// added on top of them by a completely separate pass — <see cref="AddVerticality"/> has never
    /// known or cared where the pads went. The result was pads that fling you a metre and a half
    /// into the underside of a skybridge, which is worse than no pad at all: it looks like a route
    /// and it is a ceiling.
    ///
    /// Rather than hand-tune twenty coordinates that the next layout change would bury again, this
    /// walks each pad outward until it finds open sky. The search spirals because a pad belongs to
    /// a place in the map's flow — one shoved thirty metres away is a different pad — so the
    /// nearest clear spot is always the right answer.
    /// </summary>
    void ClearLaunchPadCeilings()
    {
        for (int i = 0; i < LaunchPads.Count; i++)
        {
            var pad = LaunchPads[i];
            float want = PadApex(pad.Impulse);

            float bestRoom = PadHeadroom(pad.Centre);
            if (bestRoom >= want) continue;

            Vector3 best = pad.Centre;

            for (float r = 3.5f; r <= 18f && bestRoom < want; r += 2.5f)
            for (int a = 0; a < 12; a++)
            {
                float th = a * MathF.Tau / 12f;
                var at = new Vector3(pad.Centre.X + MathF.Cos(th) * r, 0f,
                                     pad.Centre.Z + MathF.Sin(th) * r);

                if (MathF.Abs(at.X) > HalfWidth - 4f || MathF.Abs(at.Z) > HalfDepth - 4f) continue;
                if (IsOverPit(at)) continue;
                if (!IsClearOfBlocks(at with { Y = 0.6f }, 2.6f, 2.2f)) continue;

                bool crowded = false;
                for (int j = 0; j < LaunchPads.Count && !crowded; j++)
                    if (j != i && new Vector2(LaunchPads[j].Centre.X - at.X,
                                              LaunchPads[j].Centre.Z - at.Z).Length() < 9f)
                        crowded = true;

                if (crowded) continue;

                float room = PadHeadroom(at);
                if (room > bestRoom) { bestRoom = room; best = at; }
            }

            if (best == pad.Centre) continue;

            LaunchPads[i] = new LaunchPad(best, pad.Radius, pad.Impulse);

            var plate = Blocks[padPlates[i]];
            Blocks[padPlates[i]] = new Block(best with { Y = 0.16f }, plate.HalfExtents, plate.Tint);
        }
    }

    /// <summary>Cuts a hole in the floor. Anything that walks in falls to the kill plane.</summary>
    void Carve(Rect2 hole)
    {
        Pits.Add(hole);

        var result = new List<Rect2>();
        foreach (var slab in FloorSlabs) result.AddRange(Subtract(slab, hole));
        FloorSlabs = result;
    }

    /// <summary>Axis-aligned rectangle subtraction, yielding up to four surviving bands.</summary>
    static IEnumerable<Rect2> Subtract(Rect2 a, Rect2 b)
    {
        if (!a.Intersects(b)) { yield return a; yield break; }

        float ax0 = a.Position.X, ax1 = a.End.X, az0 = a.Position.Y, az1 = a.End.Y;
        float bx0 = MathF.Max(ax0, b.Position.X), bx1 = MathF.Min(ax1, b.End.X);
        float bz0 = MathF.Max(az0, b.Position.Y), bz1 = MathF.Min(az1, b.End.Y);

        if (bz0 > az0) yield return new Rect2(ax0, az0, ax1 - ax0, bz0 - az0);
        if (bz1 < az1) yield return new Rect2(ax0, bz1, ax1 - ax0, az1 - bz1);
        if (bx0 > ax0) yield return new Rect2(ax0, bz0, bx0 - ax0, bz1 - bz0);
        if (bx1 < ax1) yield return new Rect2(bx1, bz0, ax1 - bx1, bz1 - bz0);
    }

    // ---- layouts ----

    /// <summary>
    /// Crossfire — a tall central deck to hold, reached by ramps or flung onto by launch pads.
    /// Whoever owns the middle sees everything, which is exactly why it is worth taking.
    /// </summary>
    void BuildCrossfire()
    {
        Deck(new Vector3(0, 3f, 0), new Vector3(11f, 3f, 8f), AccentTint);
        Deck(new Vector3(0, 6.4f, 0), new Vector3(4.5f, 0.4f, 3.5f), DeckTint);

        Ramp(new Vector3(-16f, 0, 0), Vector3.Right, 6f, 4, 3.5f);
        Ramp(new Vector3(16f, 0, 0), Vector3.Left, 6f, 4, 3.5f);

        Pad(new Vector3(0f, 0f, -15f));
        Pad(new Vector3(0f, 0f, 15f));

        foreach (int sx in new[] { -1, 1 })
            foreach (int sz in new[] { -1, 1 })
            {
                Blocks.Add(new Block(new Vector3(sx * 27f, 3f, sz * 20f), new Vector3(2.2f, 3f, 2.2f), CoverTint));
                Blocks.Add(new Block(new Vector3(sx * 16f, 1f, sz * 26f), new Vector3(5f, 1f, 1.4f), CoverTint));
            }

        // Side catwalks, reachable from the corner pillars, giving a flanking route above the floor.
        foreach (int sx in new[] { -1, 1 })
            Deck(new Vector3(sx * 34f, 4.5f, 0f), new Vector3(3f, 0.4f, 14f));

        // On top of the upper tier, not level with it — a zone at the deck's own height sits
        // inside the tier block above it.
        // One up top as the prize worth climbing for, two on the floor so the arena still hands
        // out weapons to anyone fighting at ground level.
        WeaponSpawns.Add(new Vector3(0f, 6.9f, 0f));
        WeaponSpawns.Add(new Vector3(-30f, 1f, 12f));
        WeaponSpawns.Add(new Vector3(30f, 1f, -12f));

        ZoneSpots.Add(new Vector3(0f, 6.8f, 0f));
        ZoneSpots.Add(new Vector3(-30f, 1f, 0f));
        ZoneSpots.Add(new Vector3(30f, 1f, 0f));
        ZoneSpots.Add(new Vector3(0f, 1f, -26f));
        ZoneSpots.Add(new Vector3(0f, 1f, 26f));
    }

    /// <summary>
    /// Foundry — a spine down the middle with catwalks over the chokepoints, and a moving platform
    /// crossing the gap. Timing the platform is the fast way across; the long way round is safer.
    /// </summary>
    void BuildFoundry()
    {
        Blocks.Add(new Block(new Vector3(0, 4f, 0f), new Vector3(2f, 4f, 9f), WallTint));
        Blocks.Add(new Block(new Vector3(0, 4f, -28f), new Vector3(2f, 4f, 8f), WallTint));
        Blocks.Add(new Block(new Vector3(0, 4f, 28f), new Vector3(2f, 4f, 8f), WallTint));

        // Catwalks along the spine, above the two gaps.
        Deck(new Vector3(0f, 8.2f, -14f), new Vector3(4.5f, 0.4f, 6f));
        Deck(new Vector3(0f, 8.2f, 14f), new Vector3(4.5f, 0.4f, 6f));

        foreach (int sx in new[] { -1, 1 })
        {
            Ramp(new Vector3(sx * 15f, 0, -14f), sx > 0 ? Vector3.Left : Vector3.Right, 8f, 4, 3f);
            Ramp(new Vector3(sx * 15f, 0, 14f), sx > 0 ? Vector3.Left : Vector3.Right, 8f, 4, 3f);
            Blocks.Add(new Block(new Vector3(sx * 12f, 1.4f, 0f), new Vector3(3.2f, 1.4f, 3.2f), CoverTint));
            Blocks.Add(new Block(new Vector3(sx * 28f, 2.4f, 0f), new Vector3(4f, 2.4f, 3f), AccentTint));
            Blocks.Add(new Block(new Vector3(sx * 40f, 1.6f, -17f), new Vector3(3f, 1.6f, 3f), CoverTint));
            Blocks.Add(new Block(new Vector3(sx * 40f, 1.6f, 17f), new Vector3(3f, 1.6f, 3f), CoverTint));
        }

        // Crosses the eastern gap at catwalk height.
        MovingPlatforms.Add(new MovingPlatformDef(
            new Vector3(-9f, 8.2f, 14f), new Vector3(9f, 8.2f, 14f),
            new Vector3(3.2f, 0.35f, 3.2f), 6f));

        Pad(new Vector3(-20f, 0f, -26f));
        Pad(new Vector3(20f, 0f, 26f));

        WeaponSpawns.Add(new Vector3(0f, 9.1f, -14f));
        WeaponSpawns.Add(new Vector3(0f, 9.1f, 14f));

        // Not on the centre line: the spine runs straight through it.
        WeaponSpawns.Add(new Vector3(-20f, 1f, 0f));

        ZoneSpots.Add(new Vector3(0f, 9f, -14f));
        ZoneSpots.Add(new Vector3(0f, 9f, 14f));
        ZoneSpots.Add(new Vector3(-36f, 1f, 0f));
        ZoneSpots.Add(new Vector3(36f, 1f, 0f));
    }

    /// <summary>
    /// Atrium — a tiered central tower ringed by a pit. Cross on the two bridges, or launch over
    /// it and commit to the air. Falling in is fatal, so the middle is genuinely dangerous ground.
    /// </summary>
    void BuildAtrium()
    {
        // The moat, with two bridges left intact across it.
        Carve(new Rect2(-20f, -16f, 40f, 12f));
        Carve(new Rect2(-20f, 4f, 40f, 12f));

        Deck(new Vector3(0, 2.5f, 0), new Vector3(13f, 2.5f, 4f), AccentTint);
        Deck(new Vector3(0, 5.5f, 0), new Vector3(6f, 0.5f, 3f), DeckTint);

        // Offset half a step so no pillar sits on a cardinal axis. On the axes they blocked the
        // straight approaches to the middle, which are also where the capture zones live.
        for (int k = 0; k < 8; k++)
        {
            float a = k * MathF.Tau / 8f + MathF.Tau / 16f;
            var at = new Vector3(MathF.Cos(a) * 30f, 3.5f, MathF.Sin(a) * 24f);
            Blocks.Add(new Block(at, new Vector3(2f, 3.5f, 2f), CoverTint));
        }

        Pad(new Vector3(-26f, 0f, 0f), 19f);
        Pad(new Vector3(26f, 0f, 0f), 19f);

        Blocks.Add(new Block(new Vector3(0, 1f, -30f), new Vector3(8f, 1f, 1.6f), CoverTint));
        Blocks.Add(new Block(new Vector3(0, 1f, 30f), new Vector3(8f, 1f, 1.6f), CoverTint));
        Blocks.Add(new Block(new Vector3(-42f, 1.4f, 0f), new Vector3(2.4f, 1.4f, 5f), CoverTint));
        Blocks.Add(new Block(new Vector3(42f, 1.4f, 0f), new Vector3(2.4f, 1.4f, 5f), CoverTint));

        WeaponSpawns.Add(new Vector3(0f, 6.1f, 0f));      // on the tower, over the moat
        WeaponSpawns.Add(new Vector3(-46f, 1f, 10f));
        WeaponSpawns.Add(new Vector3(46f, 1f, -10f));

        ZoneSpots.Add(new Vector3(0f, 6f, 0f));
        ZoneSpots.Add(new Vector3(-34f, 1f, 0f));
        ZoneSpots.Add(new Vector3(34f, 1f, 0f));
        ZoneSpots.Add(new Vector3(0f, 1f, -26f));
        ZoneSpots.Add(new Vector3(0f, 1f, 26f));
    }

    /// <summary>
    /// The Laboratory — the Vessels' own, where they made John Smith, and which they brought down
    /// afterwards.
    ///
    /// The one arena that is a building rather than a floor. Every other map in the game is a
    /// plan with a storey over it; this is eight levels stacked to sixty-four metres, which is why
    /// the wall height had to stop being a constant.
    ///
    /// WHAT MAKES IT PLAYABLE RATHER THAN A TOWER OF BOXES. Three things, and they are all the
    /// same thing seen from different angles:
    ///
    /// The blast went up the middle, so there is a shaft through every floor. You can see the
    /// whole height of the building from the ground, shoot up it, fall down it, and know where
    /// everyone is. A tall map whose levels cannot see each other is eight small maps stacked up
    /// with a loading screen between them.
    ///
    /// Every floor is missing a quarter, and it is a different quarter each level, rotating. So
    /// the shaft opens onto a different side as you climb, no floor is a repeat of the one below,
    /// and the hole you fall through is never the hole you fell through last time.
    ///
    /// And nothing is gated behind one route. Two stair cores at opposite corners, a lift up the
    /// shaft, and launch pads under the holes - four ways up, deliberately more than a map this
    /// size needs, because being cut off at the bottom of a tower is not a setback, it is the
    /// rest of the round spent climbing.
    /// </summary>
    void BuildLaboratory()
    {
        const float TowerX = 44f;        // half extents of the building footprint
        const float TowerZ = 32f;
        const float Rise = 7f;           // floor to floor: two ramps' worth, never a jump
        const int Levels = 8;            // ground, then seven above it
        const float ShaftHalf = 11f;     // the hole the blast made, all the way up
        const float Slab = 0.4f;

        var bone = new Color(0.80f, 0.79f, 0.76f);
        var scorched = new Color(0.34f, 0.32f, 0.31f);
        var steel = new Color(0.52f, 0.54f, 0.58f);

        // The stair wells: the column of open air above each staircase, cut out of every plate.
        //
        // A flight climbing 7m has its lower steps 1.75m under the floor it arrives at, and a
        // pawn does not fit in 1.75m - so a staircase under a solid slab is not a staircase, it is
        // a crawlspace, and the navigation graph correctly refuses to route through it. The well
        // spans the run of the flight and stops short of its top step, which is level with the
        // landing and wants floor beside it rather than sky.
        // Written out as coordinates rather than derived, because a well in the wrong place is a
        // hole somebody falls through for no reason. Each spans the run of one flight and stops
        // short of its top step, which is level with the landing and wants floor beside it.
        var wells = new List<Rect2>
        {
            new(TowerX - 14f, 15f, 14f, 10f),
            new(-TowerX, -25f, 14f, 10f),
        };

        // Level 0 is the arena floor itself, so the loop starts at 1 and builds what stands above.
        for (int level = 1; level < Levels; level++)
        {
            float y = level * Rise;

            // The quarter that is missing on this level. Rotating rather than random: a player
            // should be able to learn this building, and a ruin that is differently ruined every
            // time you look at it is noise rather than architecture.
            int gone = level % 4;

            // North and south bands run the full width; east and west fill between them. Four
            // bands leaving the shaft open in the middle.
            if (gone != 0)
                DeckWithHoles(new Vector3(0f, y, -(ShaftHalf + TowerZ) * 0.5f),
                              new Vector3(TowerX, Slab, (TowerZ - ShaftHalf) * 0.5f),
                              wells, bone, SurfaceKind.Concrete);

            if (gone != 1)
                DeckWithHoles(new Vector3(0f, y, (ShaftHalf + TowerZ) * 0.5f),
                              new Vector3(TowerX, Slab, (TowerZ - ShaftHalf) * 0.5f),
                              wells, bone, SurfaceKind.Concrete);

            if (gone != 2)
                DeckWithHoles(new Vector3(-(ShaftHalf + TowerX) * 0.5f, y, 0f),
                              new Vector3((TowerX - ShaftHalf) * 0.5f, Slab, ShaftHalf),
                              wells, bone, SurfaceKind.Concrete);

            if (gone != 3)
                DeckWithHoles(new Vector3((ShaftHalf + TowerX) * 0.5f, y, 0f),
                              new Vector3((TowerX - ShaftHalf) * 0.5f, Slab, ShaftHalf),
                              wells, bone, SurfaceKind.Concrete);

            // The outer skin, in four pieces with the corners left open. Waist high rather than
            // full height: a parapet you fight over and can be shot across, not a wall that turns
            // each floor into its own sealed room.
            foreach (int sz in new[] { -1, 1 })
                Deck(new Vector3(0f, y + 1.4f, sz * TowerZ), new Vector3(TowerX - 9f, 1.4f, 0.6f),
                     level % 2 == 0 ? scorched : bone, SurfaceKind.Concrete);

            foreach (int sx in new[] { -1, 1 })
                Deck(new Vector3(sx * TowerX, y + 1.4f, 0f), new Vector3(0.6f, 1.4f, TowerZ - 9f),
                     level % 2 == 0 ? scorched : bone, SurfaceKind.Concrete);

            // The stairwells stand whatever else fell.
            //
            // Not decoration: the missing quarter rotates, and on the levels where it takes the
            // band a stair core lands in, the climb simply stopped - the north band is gone on
            // level 4 and the south on 1 and 5, so each core was severed twice on the way up and
            // nothing above level 3 could be walked to at all. A landing at every core on every
            // level is what makes the building one place instead of four.
            //
            // It is also what a ruin looks like. The stair core is the strongest part of a
            // building and routinely the only part left standing.
            foreach (int corner in new[] { -1, 1 })
                DeckWithHoles(new Vector3(corner * (TowerX - 7f), y, corner * (TowerZ - 13f)),
                              new Vector3(6f, Slab, 13f), wells, bone, SurfaceKind.Concrete);

            // Labs, in the band that survived on this level.
            //
            // Kept to 4m tall under a floor 7m up. A room the usual 5.2m leaves 1.8m of crawlspace
            // between its roof and the storey above, and a pawn does not fit in 1.8m - the same
            // trap that made the staircases unclimbable, which is worth stating twice because
            // vertical maps invite it everywhere and horizontal ones never do.
            // Whichever of the two long bands is still standing. North unless the blast took it,
            // in which case south - and both are never gone at once, because only one quarter goes
            // per level.
            float bandZ = gone != 0 ? -(ShaftHalf + TowerZ) * 0.5f : (ShaftHalf + TowerZ) * 0.5f;

            foreach (int sx in new[] { -1, 1 })
                Room(new Vector3(sx * 22f, y, bandZ), new Vector3(7f, 2f, 5f),
                     doors: new[] { true, level % 2 == 0, true, level % 2 != 0 },
                     roofed: true, tint: bone, surface: SurfaceKind.Plaster);

            // Standing partitions, so a floor is a laboratory rather than a slab. Cover at the
            // height that matters, and they alternate so no two floors read the same.
            foreach (int sx in new[] { -1, 1 })
            {
                float px = sx * (ShaftHalf + 9f);
                float pz = (level % 2 == 0 ? 1f : -1f) * (ShaftHalf + 7f);

                Deck(new Vector3(px, y + 1.5f, pz), new Vector3(6f, 1.5f, 0.5f), steel);
                Deck(new Vector3(px + sx * 5.5f, y + 1.5f, pz - MathF.Sign(pz) * 5f),
                     new Vector3(0.5f, 1.5f, 5f), steel);
            }
        }

        // ---- getting up ----

        // Two stair cores, at opposite corners, each climbing every level. Opposite so that
        // holding one is not holding the building, and inboard of the parapet so the climb is
        // fought over rather than walked.
        foreach (int corner in new[] { -1, 1 })
        {
            float sx = corner * (TowerX - 7f);
            float sz = corner * (TowerZ - 7f);

            for (int level = 0; level < Levels - 1; level++)
                Flight(new Vector3(sx, level * Rise, sz), corner > 0 ? Vector3.Forward : Vector3.Back,
                       Rise, 4, 3.2f, steel);
        }

        // The lift, running the full height of the shaft. The fast way up and the exposed one:
        // there is nothing to hide behind on a platform in the middle of a hole.
        MovingPlatforms.Add(new MovingPlatformDef(
            new Vector3(0f, 0.6f, 0f), new Vector3(0f, (Levels - 1) * Rise + 0.6f, 0f),
            new Vector3(4.5f, 0.35f, 4.5f), period: 16f, dwell: 1.2f));

        // ---- what is left of the place ----

        // The clean room, on the ground, at the bottom of the shaft: where he was made. Roofless,
        // because the ceiling is eight floors of hole, and the only enclosed space down here.
        if (Room(new Vector3(0f, 0f, 0f), new Vector3(9f, 2.6f, 9f),
                 doors: new[] { true, false, true, false }, roofed: false, tint: bone))
            WeaponSpawns.Add(LastRoomAt with { Y = 1f });

        // Launch pads in the clean room, firing straight up the shaft.
        //
        // Here rather than out on the floor plates, and the reason is mechanical before it is
        // thematic: a pad has to be able to reach its apex or ClearLaunchPadCeilings walks it
        // somewhere it can, and the only column of open sky in this building is the hole the blast
        // made. Two pads placed under floor plates were duly relocated and reported as having 6m
        // of clearance for a 15m throw.
        //
        // That it also flings you out of the room he was made in is the sort of thing a level
        // gets to keep once the geometry has decided it.
        // Diagonally opposite corners of the room, which is the only part of the shaft with open
        // sky the whole way up: a shared pass runs a catwalk across the middle at 9.6m, and pads
        // under it were duly relocated by ClearLaunchPadCeilings and reported as having 6m of
        // clearance for a 15m throw. Clear of the lift as well, which occupies the centre.
        foreach (int s in new[] { -1, 1 })
            Pad(new Vector3(s * 6.5f, 0f, s * 6.5f), 20f);

        // Guns up the building, so height is worth taking rather than merely available.
        WeaponSpawns.Add(new Vector3(-(ShaftHalf + TowerX) * 0.5f, 3f * Rise + 1f, 0f));
        WeaponSpawns.Add(new Vector3((ShaftHalf + TowerX) * 0.5f, 5f * Rise + 1f, 0f));

        // Objectives at three heights, so a mode that fights over ground fights over the building.
        ZoneSpots.Add(new Vector3(0f, 1f, -(ShaftHalf + TowerZ) * 0.5f));
        ZoneSpots.Add(new Vector3(0f, 2f * Rise + 1f, (ShaftHalf + TowerZ) * 0.5f));
        ZoneSpots.Add(new Vector3(0f, 4f * Rise + 1f, -(ShaftHalf + TowerZ) * 0.5f));
        ZoneSpots.Add(new Vector3(0f, 6f * Rise + 1f, (ShaftHalf + TowerZ) * 0.5f));
    }

    /// <summary>
    /// A floor plate with holes cut out of it.
    ///
    /// Built on the same rectangle subtraction the pits use. It exists because a staircase needs
    /// an open well above it and nothing else in this file could express one: the first version of
    /// the Laboratory ran its stairs up under solid floor plates, which left the lower steps with
    /// 1.75m of headroom, and the navigation graph discards any surface a pawn cannot stand on.
    /// The building was climbable to the third storey and no further - not because the steps were
    /// missing, but because they were in a crawlspace.
    /// </summary>
    void DeckWithHoles(Vector3 centre, Vector3 half, IEnumerable<Rect2> holes, Color tint,
                       SurfaceKind surface = SurfaceKind.Panel)
    {
        var plates = new List<Rect2>
        {
            new(centre.X - half.X, centre.Z - half.Z, half.X * 2f, half.Z * 2f),
        };

        foreach (var hole in holes)
        {
            var next = new List<Rect2>();
            foreach (var plate in plates) next.AddRange(Subtract(plate, hole));
            plates = next;
        }

        foreach (var plate in plates)
        {
            // Slivers are not floor, they are a trip hazard the size of a kerb.
            if (plate.Size.X < 1.2f || plate.Size.Y < 1.2f) continue;

            Deck(new Vector3(plate.GetCenter().X, centre.Y, plate.GetCenter().Y),
                 new Vector3(plate.Size.X * 0.5f, half.Y, plate.Size.Y * 0.5f), tint, surface);
        }
    }

    /// <summary>
    /// A flight of steps from one floor to the next.
    ///
    /// <see cref="Ramp"/> cannot do this: every step it builds stands on the ground, which is
    /// right for a ramp onto a deck and useless seven storeys up. These steps stand on the floor
    /// they start from.
    /// </summary>
    void Flight(Vector3 from, Vector3 dir, float rise, int steps, float width, Color tint)
    {
        for (int i = 1; i <= steps; i++)
        {
            float top = from.Y + rise * i / steps;

            // Treads sit ON the navigation lattice, centred on its sample points.
            //
            // That grid is 2.5m and four-connected, and a tread has to do two things at once: be
            // long enough to contain a sample point at all, and not overhang the tread below it.
            // A riser is solid from the floor to its own tread, so overlapping them puts each step
            // inside the headroom of the one before - which is how a staircase becomes a
            // crawlspace the graph refuses to route through. Exactly 2.5m long, centred on the
            // sample points, satisfies both: every tread holds one node and no tread covers
            // another.
            var at = from + dir * (i * NavGraph.CellSize - NavGraph.CellSize * 0.5f);

            // Treads are thin slabs, not solid risers.
            //
            // A riser filled from the floor to its own tread is what a staircase looks like, and
            // it is why this building was unclimbable. The flights stack one above another, so
            // every flight's risers stand in the headroom of the flight below: tread three sits
            // exactly 1.75m under the next flight's riser, which is a few centimetres under a
            // standing pawn, and the navigation graph discards any surface a pawn cannot stand on.
            // Tread three failed on every flight at every level, which is why nothing done to the
            // treads themselves ever changed where the climb stopped.
            //
            // Thin treads leave seven metres of air above each one. It also happens to be what a
            // staircase in a building that exploded would look like.
            const float Riser = 0.25f;
            const float Tread = NavGraph.CellSize * 0.5f;

            Blocks.Add(new Block(at with { Y = top - Riser },
                                 new Vector3(MathF.Abs(dir.X) > 0.5f ? Tread : width, Riser,
                                             MathF.Abs(dir.Z) > 0.5f ? Tread : width),
                                 tint));
        }
    }

    // ---- Coldstore ----

    /// <summary>
    /// COLDSTORE - a siege across open snow, which is a shape no other arena in the game has.
    ///
    /// Every other map here is a building or a yard: cover every few metres, sightlines measured in
    /// tens of metres, and a fight that is always within a grenade of somebody. This one is built
    /// the opposite way round, because the thing it is copying - the assault on an ice base - is
    /// about crossing ground you cannot hide on.
    ///
    /// Four bands, mirrored north and south:
    ///
    ///     the plain        open snow, the width of the map. Vehicle country, and the only
    ///                      place on any map where a hull has room to actually drive at somebody
    ///     the trench line  a dug line facing the plain, with firing bays and cut-throughs. The
    ///                      thing you are crossing the plain to reach
    ///     the hangar       an open-fronted shed with a mezzanine, the fallback once the line goes
    ///     the generator    behind the hangar, the thing worth holding
    ///
    /// and ice ridges down both flanks with a pass through each, so the open ground is a choice
    /// rather than the only route. Somebody who does not want to cross the plain goes around, and
    /// pays for it in time.
    ///
    /// The trench is the piece that makes it work, and it is built as berms rather than as a dug
    /// channel. This engine's arenas are a flat floor with boxes on it; there is no digging. Two
    /// parallel walls with a walkway between them read from inside exactly as a trench does - a
    /// corridor you move along in cover, with a parapet at chest height to shoot over - and the
    /// front berm being lower than the back one is what lets you fire out but not straight through.
    /// </summary>
    void BuildColdstore()
    {
        // The approach runs south to north. The attacker's ground is at -Z, the base at +Z, and
        // the two hundred metres between them is the map.
        ImperialStaging();
        TheApproach();
        OuterTrench();
        InnerTrench();
        EchoBase();
        ShieldGenerator();
        CanyonWalls();
        Crevasses();
        Lifts();
    }

    // Landmarks along the approach axis, south to north. Written as named distances rather than
    // as numbers at the call sites, because the whole design is the spacing between them: shorten
    // any one of these and the map stops being a siege and becomes a courtyard again.
    const float StagingZ = -270f;      // where the attack forms up
    const float ApproachStart = -210f; // the last cover before open ground
    const float OuterTrenchZ = -60f;   // the first line, facing the open
    const float InnerTrenchZ = 20f;    // the fallback line
    const float HangarZ = 130f;        // the base itself
    const float GeneratorZ = 250f;     // the thing being defended
    const float CanyonX = 205f;        // the walls down both flanks

    Color SnowTint => new(0.95f, 0.96f, 0.98f);
    Color IceTint => new(0.70f, 0.82f, 0.92f);
    Color SteelTint => new(0.42f, 0.45f, 0.50f);
    Color GirderTint => new(0.24f, 0.26f, 0.30f);

    /// <summary>
    /// The attacker's end: a shelf of hard standing with wind breaks, and nothing to hide behind
    /// once you leave it. Deliberately thin. It is a start line, not a fortress - the whole point
    /// of this map is that the side with the ground has to cross it.
    /// </summary>
    void ImperialStaging()
    {
        foreach (int sx in new[] { -1, 1 })
        {
            // Wind breaks at an angle, so the staging area reads as sheltered rather than walled.
            for (int i = 0; i < 3; i++)
                Blocks.Add(new Block(new Vector3(sx * (60f + i * 34f), 2.6f, StagingZ - i * 16f),
                                     new Vector3(16f, 2.6f, 1.4f), SnowTint, false, SurfaceKind.Snow));

            // Two loading ramps: raised platforms a hull can sit on and a fighter can shoot from.
            Deck(new Vector3(sx * 96f, 2.2f, StagingZ + 30f), new Vector3(20f, 2.2f, 13f),
                 SteelTint, SurfaceKind.Panel);
            // Seven steps of 0.63m, landing the last one on the deck's front edge. Ramp spaces
            // its steps 2.4m apart whatever else you ask, so reaching a 4.4m deck without
            // exceeding NavGraph.StepUp takes seven of them and 16.8m of run.
            // Starts 16.8m clear of the deck's front edge (z = StagingZ + 17) and climbs toward
            // it, so the last step lands ON that edge. Started nearer, the whole staircase is
            // inside the deck it is meant to reach - which is what the first attempt did.
            Ramp(new Vector3(sx * 96f, 0f, StagingZ + 59.8f), Vector3.Forward, 4.4f, 7, 7f);

            // On the deck's top surface. At y=3 it was inside a block four and a half metres
            // thick, which the graph reports as unreachable because it is.
            WeaponSpawns.Add(new Vector3(sx * 96f, 4.8f, StagingZ + 30f));
        }

        ZoneSpots.Add(new Vector3(0f, 1f, StagingZ + 20f));
    }

    /// <summary>
    /// A hundred and fifty metres of open snow, which is the entire idea.
    ///
    /// This is the part that was missing. The first version of this arena had ninety metres
    /// between the trench and the far wall and called it a plain; a tank crosses ninety metres in
    /// six seconds. What makes the original work is that the walk is long enough to be a decision
    /// and long enough to be shot at for the whole of it, so the cover out here is sparse, low,
    /// and far apart - enough to plan a run between, never enough to be safe.
    /// </summary>
    void TheApproach()
    {
        // Ice outcrops, thinning as you get closer to the line. A run across is a sequence of
        // shorter and shorter sprints between worse and worse cover.
        for (int row = 0; row < 6; row++)
        {
            float z = ApproachStart + row * 28f;
            int count = 5 - row / 2;                       // fewer the further north you get
            float height = 4.5f - row * 0.45f;             // and lower

            for (int i = 0; i < count; i++)
            {
                // Offset each row so no two line up into a corridor.
                float x = (i - (count - 1) * 0.5f) * 74f + (row % 2 == 0 ? 26f : -26f);
                if (MathF.Abs(x) > 190f) continue;

                Blocks.Add(new Block(new Vector3(x, height * 0.5f, z),
                                     new Vector3(9f, height * 0.5f, 6f), IceTint,
                                     false, SurfaceKind.Ice));
            }
        }

        // Two downed hulls, the landmarks people will name the ground after.
        foreach (int sx in new[] { -1, 1 })
        {
            float x = sx * 120f, z = -130f + sx * 40f;

            Blocks.Add(new Block(new Vector3(x, 2.6f, z), new Vector3(22f, 2.6f, 5f),
                                 GirderTint, false, SurfaceKind.Panel));
            Blocks.Add(new Block(new Vector3(x + sx * 13f, 5.6f, z), new Vector3(7f, 3f, 4.4f),
                                 SteelTint, false, SurfaceKind.Panel));

            WeaponSpawns.Add(new Vector3(x, 1f, z + 9f));
            ZoneSpots.Add(new Vector3(x, 1f, z - 9f));
        }

        ZoneSpots.Add(new Vector3(0f, 1f, -120f));
    }

    /// <summary>
    /// The first line: a dug position facing the open ground, with emplacements at intervals.
    ///
    /// Berms rather than an excavation, because this engine's arenas are a flat floor with boxes
    /// on it and there is no digging. Two parallel walls with a walkway between read from inside
    /// exactly as a trench does, and a front parapet lower than the back wall is what lets you
    /// fire out of it without being seen through it.
    /// </summary>
    void OuterTrench() => TrenchLine(OuterTrenchZ, emplacements: true);

    /// <summary>The fallback line, closer in and without the gun positions. Losing the first
    /// line is meant to cost ground, not the match.</summary>
    void InnerTrench() => TrenchLine(InnerTrenchZ, emplacements: false);

    void TrenchLine(float z, bool emplacements)
    {
        const float Front = 2.0f, Back = 3.4f, Walk = 5f;

        // Wide enough to drive a hull through, spaced so breaking the line is a decision about
        // WHERE rather than whether.
        var gaps = new List<(float, float)>
        {
            (-176f, -160f), (-118f, -102f), (-52f, -36f), (36f, 52f), (102f, 118f), (160f, 176f),
        };

        WallWithGaps(z - Walk * 0.5f, Front, 1.2f, gaps, SnowTint, SurfaceKind.Snow);
        WallWithGaps(z + Walk * 0.5f, Back, 1.4f, gaps, SnowTint, SurfaceKind.Snow);

        if (!emplacements) return;

        // Gun positions pushed out into the open, so the line is not one flat face and standing
        // in one means being shot at from three sides.
        foreach (float bx in new[] { -195f, -140f, -84f, 0f, 84f, 140f, 195f })
        {
            Blocks.Add(new Block(new Vector3(bx, 1.3f, z - Walk * 0.5f - 9f),
                                 new Vector3(9f, 1.3f, 1.2f), SnowTint, false, SurfaceKind.Snow));

            foreach (int side in new[] { -1, 1 })
                Blocks.Add(new Block(new Vector3(bx + side * 9f, 1.3f, z - Walk * 0.5f - 5f),
                                     new Vector3(1.2f, 1.3f, 4.5f), SnowTint, false, SurfaceKind.Snow));

            Deck(new Vector3(bx, 2.4f, z - Walk * 0.5f - 7f), new Vector3(4f, 0.4f, 3f),
                 SteelTint, SurfaceKind.Panel);
        }

        foreach (float bx in new[] { -140f, 0f, 140f })
            WeaponSpawns.Add(new Vector3(bx, 1f, z + Walk));

        ZoneSpots.Add(new Vector3(-70f, 1f, z + Walk));
        ZoneSpots.Add(new Vector3(70f, 1f, z + Walk));
    }

    /// <summary>
    /// Echo Base: a hangar you can fly a plane into, with a mezzanine and side bays.
    ///
    /// Far larger than the shed this used to be, because at this scale a twenty-six metre shed is
    /// a hut. The mouth faces the approach on purpose - it is the thing the attack is walking
    /// toward, and it should be visible from the trench line.
    /// </summary>
    void EchoBase()
    {
        const float HalfX = 78f, HalfZ = 40f, Roof = 22f, Mezz = 7f;

        float mouth = HangarZ - HalfZ, rear = HangarZ + HalfZ;

        foreach (int sx in new[] { -1, 1 })
            Blocks.Add(new Block(new Vector3(sx * HalfX, Roof * 0.5f, HangarZ),
                                 new Vector3(2f, Roof * 0.5f, HalfZ), SteelTint,
                                 false, SurfaceKind.Panel));

        // Back wall with a door through to the generator yard.
        foreach (int sx in new[] { -1, 1 })
            Blocks.Add(new Block(new Vector3(sx * 47f, Roof * 0.5f, rear),
                                 new Vector3(31f, Roof * 0.5f, 2f), SteelTint,
                                 false, SurfaceKind.Panel));

        Deck(new Vector3(0f, Roof, HangarZ), new Vector3(HalfX, 1f, HalfZ),
             GirderTint, SurfaceKind.Panel);

        // The lintel over the mouth, so it reads as a doorway rather than a missing wall.
        Blocks.Add(new Block(new Vector3(0f, Roof - 3f, mouth), new Vector3(HalfX, 3f, 2f),
                             GirderTint, false, SurfaceKind.Panel));

        // Mezzanine down both long sides, reached from inside. Firing down into your own hangar
        // is the whole reason to hold it after the line goes.
        foreach (int sx in new[] { -1, 1 })
        {
            Deck(new Vector3(sx * 60f, Mezz, HangarZ), new Vector3(16f, 0.6f, HalfZ - 3f),
                 SteelTint, SurfaceKind.Panel);

            // In two pieces with a gap in the middle, because the ramp arrives at exactly this
            // edge: an unbroken parapet stands in the headroom above the top step, and the graph
            // correctly refuses to route into a railing.
            foreach (int sz in new[] { -1, 1 })
                Blocks.Add(new Block(new Vector3(sx * 44f, Mezz + 1.4f, HangarZ + sz * 23f),
                                     new Vector3(0.8f, 1.4f, 14f), GirderTint,
                                     false, SurfaceKind.Panel));

            // Climbs outward along X to the mezzanine's inboard edge. Running it along Z put the
            // whole staircase underneath the deck it was meant to reach, which is the third time
            // that mistake has been made on this map and the reason it is spelled out here.
            Ramp(new Vector3(sx * 17.6f, 0f, HangarZ), sx > 0 ? Vector3.Right : Vector3.Left,
                 Mezz + 0.6f, 11, 7f);

            WeaponSpawns.Add(new Vector3(sx * 60f, Mezz + 1f, HangarZ + 14f));

            // Side bays off the hangar floor: rooms with a doorway, the close quarters this map
            // otherwise has none of.
            foreach (int sz in new[] { -1, 1 })
                Room(new Vector3(sx * 40f, 0f, HangarZ + sz * 24f), new Vector3(14f, 4f, 10f),
                     doors: new[] { true, true, true, true }, tint: SteelTint,
                     surface: SurfaceKind.Panel);
        }

        ZoneSpots.Add(new Vector3(0f, 1f, HangarZ));
        WeaponSpawns.Add(new Vector3(0f, 1f, HangarZ - 20f));
    }

    /// <summary>
    /// The shield generator behind the base: a drum in a walled yard, and the deepest thing on the
    /// map. Open to the sky, so holding it is a commitment rather than a corner to hide in.
    /// </summary>
    void ShieldGenerator()
    {
        Blocks.Add(new Block(new Vector3(0f, 7f, GeneratorZ), new Vector3(20f, 7f, 20f),
                             SteelTint, false, SurfaceKind.Panel));
        Blocks.Add(new Block(new Vector3(0f, 15.5f, GeneratorZ), new Vector3(13f, 1.5f, 13f),
                             IceTint, false, SurfaceKind.Ice));

        foreach (int sx in new[] { -1, 1 })
        {
            Blocks.Add(new Block(new Vector3(sx * 46f, 1.6f, GeneratorZ),
                                 new Vector3(1.4f, 1.6f, 44f), SnowTint, false, SurfaceKind.Snow));

            WeaponSpawns.Add(new Vector3(sx * 32f, 1f, GeneratorZ + 30f));
        }

        Blocks.Add(new Block(new Vector3(0f, 1.6f, GeneratorZ + 44f), new Vector3(46f, 1.6f, 1.4f),
                             SnowTint, false, SurfaceKind.Snow));

        // Beside the drum, not on it. The drum is forty metres across and fourteen tall, so a
        // zone at its centre is a zone inside a block - the same fault this map had at the old
        // scale, made bigger.
        ZoneSpots.Add(new Vector3(32f, 1f, GeneratorZ));
        ZoneSpots.Add(new Vector3(-32f, 1f, GeneratorZ - 34f));
    }

    /// <summary>
    /// Ice walls down both flanks with passes through them, so the open ground is a choice.
    /// Without these the approach is the only route and the map is one shooting gallery.
    /// </summary>
    void CanyonWalls()
    {
        foreach (int sx in new[] { -1, 1 })
        {
            for (int i = 0; i < 11; i++)
            {
                float z = -300f + i * 58f;
                if (i == 3 || i == 7) continue;            // the two passes

                float h = 12f + (i % 3) * 6f;
                Blocks.Add(new Block(new Vector3(sx * CanyonX, h * 0.5f, z),
                                     new Vector3(26f, h * 0.5f, 24f), IceTint,
                                     false, SurfaceKind.Ice));
            }

            // A shelf inside each pass, looking back along the flank.
            foreach (float pz in new[] { -126f, 106f })
            {
                Deck(new Vector3(sx * (CanyonX - 30f), 4.2f, pz), new Vector3(9f, 0.6f, 15f),
                     IceTint, SurfaceKind.Ice);

                Ramp(new Vector3(sx * (CanyonX - 58.2f), 0f, pz),
                     sx > 0 ? Vector3.Right : Vector3.Left, 4.8f, 8, 7f);

                WeaponSpawns.Add(new Vector3(sx * (CanyonX - 30f), 5.2f, pz));
            }
        }
    }

    /// <summary>Crevasses out on the flanks of the approach, each with a slab grinding along it.
    /// This is the arena's push wall, kept clear of the centre so the long run stays drivable.</summary>
    void Crevasses()
    {
        foreach (int sx in new[] { -1, 1 })
        foreach (float z in new[] { -170f, -40f })
        {
            float x = sx * 155f;

            Carve(new Rect2(x - 11f, z - 14f, 22f, 28f));

            foreach (int end in new[] { -1, 1 })
                Deck(new Vector3(x, 0.6f, z + end * 17f), new Vector3(12f, 0.6f, 3f),
                     IceTint, SurfaceKind.Ice);

            MovingPlatforms.Add(new MovingPlatformDef(
                new Vector3(x, 1.6f, z - 12f),
                new Vector3(x, 1.6f, z + 12f),
                new Vector3(10f, 1.6f, 1.4f), period: 8f, dwell: 0.15f, pushes: true));
        }
    }

    /// <summary>
    /// Four lifts, because the arena needs them and the corner citadels that used to supply them
    /// are not built here.
    ///
    /// Two carry the hangar floor to its roof, which is the highest ground on the map and looks
    /// down the whole approach. Two more climb the canyon walls. Each dwells at both ends: a lift
    /// that does not wait to be boarded is a timing puzzle, not a lift.
    /// </summary>
    void Lifts()
    {
        foreach (int sx in new[] { -1, 1 })
        {
            MovingPlatforms.Add(new MovingPlatformDef(
                new Vector3(sx * 70f, 1.2f, HangarZ + 34f),
                new Vector3(sx * 70f, 23f, HangarZ + 34f),
                new Vector3(5f, 0.6f, 5f), period: 11f, dwell: 0.8f));

            MovingPlatforms.Add(new MovingPlatformDef(
                new Vector3(sx * (CanyonX - 30f), 1.2f, -10f),
                new Vector3(sx * (CanyonX - 30f), 16f, -10f),
                new Vector3(5f, 0.6f, 5f), period: 10f, dwell: 0.8f));
        }
    }

    /// <summary>A wall running along X at a fixed Z, with spans left out of it.</summary>
    void WallWithGaps(float z, float height, float thick,
                      List<(float Lo, float Hi)> gaps, Color tint, SurfaceKind surface)
    {
        float at = -HalfWidth + 10f;

        foreach (var (lo, hi) in gaps)
        {
            if (lo > at) Span(at, lo);
            at = MathF.Max(at, hi);
        }
        Span(at, HalfWidth - 10f);

        void Span(float lo, float hi)
        {
            if (hi - lo < 0.5f) return;
            Blocks.Add(new Block(new Vector3((lo + hi) * 0.5f, height * 0.5f, z),
                                 new Vector3((hi - lo) * 0.5f, height * 0.5f, thick),
                                 tint, false, surface));
        }
    }

    /// <summary>
    /// Coldstore's own loot pass, replacing the outer-district one it opts out of.
    ///
    /// Spread along the approach axis rather than clustered in four corners, because that is the
    /// shape of this map: a crate every eighty metres down the length of it is what makes the long
    /// walk worth taking rather than a stretch of nothing.
    /// </summary>
    void ColdstoreLoot()
    {
        foreach (float z in new[] { -240f, -170f, -100f, -30f, 60f, 170f, 280f })
        foreach (int sx in new[] { -1, 1 })
            WeaponSpawns.Add(new Vector3(sx * 168f, 1f, z));
    }


    /// <summary>
    /// Gauntlet — three lanes, with the middle one broken by a pit that only a moving platform or
    /// a well-timed launch crosses. The outer lanes are safe and slow; the middle is fast and can
    /// kill you.
    /// </summary>
    void BuildGauntlet()
    {
        foreach (int sz in new[] { -1, 1 })
            foreach (int sx in new[] { -1, 1 })
                Blocks.Add(new Block(new Vector3(sx * 32f, 4f, sz * 11f), new Vector3(13f, 4f, 1.5f), WallTint));

        // The gap in the middle lane.
        Carve(new Rect2(-11f, -7f, 22f, 14f));

        MovingPlatforms.Add(new MovingPlatformDef(
            new Vector3(-14f, 1.2f, 0f), new Vector3(14f, 1.2f, 0f),
            new Vector3(3.6f, 0.35f, 4f), 7f));

        Pad(new Vector3(-17f, 0f, 0f), 16f);
        Pad(new Vector3(17f, 0f, 0f), 16f);

        foreach (int sz in new[] { -1, 1 })
        {
            Blocks.Add(new Block(new Vector3(-24f, 1.6f, sz * 24f), new Vector3(3f, 1.6f, 3f), AccentTint));
            Blocks.Add(new Block(new Vector3(24f, 1.6f, sz * 24f), new Vector3(3f, 1.6f, 3f), AccentTint));
            Deck(new Vector3(0f, 5f, sz * 26f), new Vector3(9f, 0.4f, 4f));
            Ramp(new Vector3(0f, 0f, sz * 13f), sz > 0 ? Vector3.Back : Vector3.Forward, 4.6f, 3, 4f);
        }

        WeaponSpawns.Add(new Vector3(0f, 5.5f, -26f));
        WeaponSpawns.Add(new Vector3(0f, 5.5f, 26f));
        WeaponSpawns.Add(new Vector3(-38f, 1f, 0f));

        ZoneSpots.Add(new Vector3(0f, 5.5f, -26f));
        ZoneSpots.Add(new Vector3(0f, 5.5f, 26f));
        ZoneSpots.Add(new Vector3(-34f, 1f, 0f));
        ZoneSpots.Add(new Vector3(34f, 1f, 0f));
    }

    void AddInteriors()
    {
        switch (Layout)
        {
            case 0: RoomsReliquary(); break;
            case 1: RoomsFurnace(); break;
            case 2: RoomsGlasshouse(); break;

            // The Laboratory's interior is the building. BuildLaboratory puts eight floors of it
            // up, and a second interior pass on top of that is not decoration, it is another map
            // stamped through this one - which is exactly what the default below was quietly
            // doing: the tower came out carrying all twenty-six of the Thousand Rooms' chambers,
            // scattered through its floors and filling the shaft.
            case LaboratoryLayout: break;

            case ColdstoreLayout: RoomsColdstore(); break;

            default: RoomsThousand(); break;
        }
    }

    // ---- interiors ----
    //
    // Four rooms-and-corridors passes, one per layout, laid over the outer districts.
    //
    // These arenas were built out of horizontal surfaces almost exclusively — decks, tiers,
    // gantries, catwalks, ledges — with pits and lava for punctuation. That produces a particular
    // and quite narrow kind of fight: every sightline runs the full width of the map, every
    // engagement opens at forty metres, and cover is something to crouch behind rather than
    // somewhere to be. Playing it, the arenas read as smaller than they are, because open ground
    // you can see all of is ground you have already been to.
    //
    // Interiors fix that from the other direction. A wall shortens a sightline the way a hundred
    // metres of extra floor never can, a doorway is a decision, and a roofed room is the only
    // place on these maps where a shotgun beats a rifle on merit. So each layout gets a district
    // of them, and each is themed to one of the four faiths — because the arenas are their places,
    // and a map that is only geometry is a map nobody remembers the name of.

    /// <summary>
    /// Crossfire → THE RELIQUARY, of the Vessels.
    ///
    /// They hold that humanity *was* its mortality, so their architecture is a place for keeping
    /// bodies: long vaulted galleries of stacked cradles, cell after identical cell, opening onto
    /// each other down the length of the hall. Achilles' own reliquary, and the map with the
    /// tightest interior on the board — most fights in here happen inside four metres.
    /// </summary>
    void RoomsReliquary()
    {
        // Two long galleries either side of the centre, each a run of cells that open into one
        // another. The doorways are all on the long axis, so the gallery reads as a corridor you
        // can be flanked down rather than a row of boxes.
        foreach (int sz in new[] { -1, 1 })
        {
            for (int i = -2; i <= 2; i++)
            {
                // Spaced with real gaps between the cells rather than butted together.
                //
                // The first version put them on a fifteen-metre pitch, which with thirteen-metre
                // cells is a continuous seventy-metre wall across the middle of the district — and
                // the harness caught it as two vehicles that could no longer drive out of their
                // own corner. A gallery is a run of rooms you can walk between, not a barricade.
                var at = new Vector3(i * 24f, 0f, sz * 44f);
                bool end = i == -2 || i == 2;

                Room(at, new Vector3(6.5f, 2.6f, 7f),
                     doors: new[] { true, true, end, !end },
                     roofed: true);
            }
        }

        // Cradles: waist-high slabs inside the cells, which is cover at exactly the height that
        // matters in a room this size.
        foreach (int sz in new[] { -1, 1 })
        for (int i = -2; i <= 2; i++)
            Blocks.Add(new Block(new Vector3(i * 24f, 0.75f, sz * 44f + sz * 3f),
                                 new Vector3(4.5f, 0.75f, 1.2f), CoverTint));

        // A closed vault at each end of the western gallery. No through route, one door, and the
        // best gun on the floor inside it — somewhere worth going that you cannot be chased out of
        // without someone coming through the door you are looking at.
        foreach (int sz in new[] { -1, 1 })
            if (Room(new Vector3(-58f, 0f, sz * 62f), new Vector3(7f, 3f, 7f),
                     doors: new[] { false, true, false, false }))
                WeaponSpawns.Add(LastRoomAt with { Y = 1f });
    }

    /// <summary>
    /// Foundry → THE FURNACE, of the Custodians.
    ///
    /// Prometheus stole the fire and was chained to it, and this is the place he was chained: heavy
    /// machine halls wrapped around a single crucible. Every hazard left in the game lives here and
    /// nowhere else — the other three arenas had burning ground scattered across them for no reason
    /// anyone could name, which made lava a nuisance rather than a landmark.
    /// </summary>
    void RoomsFurnace()
    {
        // The machine halls: big, roofed, four ways in, arranged as a ring around the middle. Wide
        // enough to fight across and closed enough that arriving through a door is a commitment.
        foreach (int sx in new[] { -1, 1 })
        foreach (int sz in new[] { -1, 1 })
        {
            if (!Room(new Vector3(sx * 62f, 0f, sz * 46f), new Vector3(9f, 3.2f, 8f),
                      doors: new[] { true, true, true, true })) continue;

            // Machinery inside, so a hall is not an empty box with four doors.
            Blocks.Add(new Block(new Vector3(sx * 62f - 5f, 1.4f, sz * 46f),
                                 new Vector3(2.4f, 1.4f, 5f), CoverTint));
            Blocks.Add(new Block(new Vector3(sx * 62f + 5f, 1.1f, sz * 46f + sz * 4f),
                                 new Vector3(3.2f, 1.1f, 1.6f), CoverTint));
        }

        // A second rank of halls inboard of the first, so the Furnace reads as a works rather than
        // four sheds in the corners of a field.
        foreach (int sx in new[] { -1, 1 })
        {
            if (!Room(new Vector3(sx * 40f, 0f, 66f), new Vector3(10f, 3.2f, 8f),
                      doors: new[] { true, true, false, true })) continue;

            Blocks.Add(new Block(LastRoomAt with { Y = 1.3f },
                                 new Vector3(3f, 1.3f, 3f), CoverTint));

            // Somewhere ordinary worth walking to, as well as somewhere expensive.
            //
            // Put in the halls that already exist rather than in new ones. Two halls were built
            // for this first, flanking the crucible, and adding them severed a route the map had
            // been relying on: the road pass fired on the Furnace for the first time and took
            // sixteen blocks of cover out of the Custodians' map to reconnect it. A weapon crate
            // is not worth paying for in walls when there is already a room standing empty.
            //
            // Beside the machinery rather than on it. LastRoomAt is exactly where the block above
            // stands, so a crate at the room's centre is a crate inside a solid object - which the
            // suite caught at once as two unreachable weapon spawns. The hall is 20m across and
            // the machinery 6m, so five and a half metres outboard is clear of one and well
            // inside the other.
            WeaponSpawns.Add(LastRoomAt + new Vector3(sx * 5.5f, 1f, 0f));
        }

        // The crucible: the one piece of burning ground on this map meant to be looked at, in a
        // roofless ring you can be pushed into. Off the centre line, because the centre line is
        // where the Foundry's spine runs and there has never been room there.
        //
        // The fire is now a RING with an island in it, and the best gun on the map is on the
        // island. That is the Furnace's answer to a question every other arena had already
        // answered and this one had not.
        //
        // The Reliquary keeps its prize in a closed vault, the Glasshouse in a seed store, the
        // Thousand Rooms in a corner of the warren - each of them somewhere worth going that you
        // cannot be chased out of without someone coming through the door you are watching. The
        // Furnace had no such place at all: seven halls, thirteen hazards and not one weapon
        // indoors, so its interiors were somewhere to fight THROUGH and never somewhere to go.
        //
        // A door would have been the easy fix and the wrong one. This is the Custodians' map,
        // whose whole story is Prometheus chained to the fire he stole, so the price of the best
        // thing on it should be paid in fire rather than in checking a doorway. You cross four
        // metres of burning ground to reach it and four metres to leave, which at forty-four a
        // second is most of a health bar for the round trip - survivable, expensive, and entirely
        // your decision.
        if (Room(new Vector3(0f, 0f, -66f), new Vector3(14f, 2.2f, 14f),
                 doors: new[] { true, true, true, true }, roofed: false, tint: AccentTint))
        {
            var at = LastRoomAt;

            const float Reach = 8f;    // outer half-extent of the burning ground
            const float Isle = 4f;     // half-extent of the standing island at its centre

            // Four bands rather than one square, leaving the middle cold. Written as bands
            // because a hazard is a rectangle and a ring is not.
            Hazard(new Rect2(at.X - Reach, at.Z - Reach, Reach * 2f, Reach - Isle), 44f);
            Hazard(new Rect2(at.X - Reach, at.Z + Isle, Reach * 2f, Reach - Isle), 44f);
            Hazard(new Rect2(at.X - Reach, at.Z - Isle, Reach - Isle, Isle * 2f), 44f);
            Hazard(new Rect2(at.X + Isle, at.Z - Isle, Reach - Isle, Isle * 2f), 44f);

            WeaponSpawns.Add(at with { Y = 1f });
        }

    }

    /// <summary>
    /// Atrium → THE GLASSHOUSE, of the Garden.
    ///
    /// Noah carried the living through the flood, and the Garden keeps carrying them: a vivarium of
    /// growing halls under long ribbed roofs, threaded with water channels. Structurally the airiest
    /// of the four — plenty of walls, but low ones, so it reads as bays in a greenhouse rather than
    /// rooms in a building and you can still see the map over the top of them.
    /// </summary>
    void RoomsGlasshouse()
    {
        // Growing bays down each side. Low and open-topped: cover from the ground, transparent from
        // the balcony, which gives the upper storey a real reason to exist beyond height.
        foreach (int sx in new[] { -1, 1 })
        for (int i = -1; i <= 1; i++)
        {
            if (!Room(new Vector3(sx * 66f, 0f, i * 30f), new Vector3(8f, 1.7f, 8f),
                      doors: new[] { true, true, true, true }, roofed: false)) continue;

            Blocks.Add(new Block(LastRoomAt with { Y = 0.5f },
                                 new Vector3(4.5f, 0.5f, 3f), new Color(0.30f, 0.52f, 0.36f)));
        }

        // Two seed vaults, roofed and single-doored, at the ends of the long axis. The one enclosed
        // space on the map, which is what makes them worth taking.
        foreach (int sz in new[] { -1, 1 })
            if (Room(new Vector3(0f, 0f, sz * 62f), new Vector3(12f, 3f, 9f),
                     doors: new[] { true, true, false, false }))
                WeaponSpawns.Add(LastRoomAt with { Y = 1f });
    }

    /// <summary>
    /// Coldstore -> the bunker line.
    ///
    /// Everywhere except the plain. The interiors on the other maps exist to shorten sightlines
    /// that are otherwise the width of the arena; here the long sightline is the point of the
    /// place, so the rooms go where the fighting ends up instead - dug in behind the trench, and
    /// flanking the hangars as outbuildings.
    ///
    /// Low and roofed, which is the opposite of the Glasshouse's open bays: what a base in the
    /// snow wants is somewhere the sky is not, and this is the only shelter on the map besides
    /// the hangar itself.
    /// </summary>
    void RoomsColdstore()
    {
        // Outbuildings either side of each hangar. Doors facing in toward the base, so they are
        // taken from behind rather than from the plain.
        foreach (int sx in new[] { -1, 1 })
        foreach (int sz in new[] { -1, 1 })
            if (Room(new Vector3(sx * 58f, 0f, sz * 74f), new Vector3(10f, 3f, 9f),
                     doors: new[] { true, true, true, false }))
                WeaponSpawns.Add(LastRoomAt with { Y = 1f });

        // Dugouts, offered more sites than are needed.
        //
        // Room does not refuse because something is built there - it refuses because a spawn, a
        // weapon, a capture zone or a parked hull is within its margin, and a hull's margin is
        // nineteen metres. The ground immediately behind the trench is a long clear run, which is
        // exactly what ChooseVehicleSpawns is looking for, so the obvious place for a dugout is
        // the one place a dugout cannot go.
        //
        // Hence eight candidates for four rooms. Which ones take depends on where the hulls parked,
        // and that is fine: what the map needs is dugouts behind the line, not dugouts at
        // particular coordinates. Guessing one site and hoping is what left this arena with half
        // its interiors twice.
        var dugouts = new List<Vector3>();

        foreach (int sx in new[] { -1, 1 })
        foreach (int sz in new[] { -1, 1 })
        {
            dugouts.Add(new Vector3(sx * 30f, 0f, sz * 60f));
            dugouts.Add(new Vector3(sx * 92f, 0f, sz * 32f));
        }

        foreach (var at in dugouts)
            Room(at, new Vector3(6f, 2.4f, 3.5f), doors: new[] { true, true, true, true });
    }

    /// <summary>
    /// Gauntlet → THE THOUSAND ROOMS, of Ingenuity.
    ///
    /// Scheherazade lived one more night for every story, and her faction's answer to being told
    /// humanity was a specification sheet is a building that will not stop adding rooms. The
    /// densest interior in the game: a warren of small chambers with doors that do not line up,
    /// where nothing can be seen from more than a room away and every corner is somebody's.
    /// </summary>
    void RoomsThousand()
    {
        // A grid of small chambers whose doorways alternate, so there is no straight run through
        // the block in either direction — you are always turning, and you are never sure which of
        // the two walls in front of you is the one that opens.
        //
        // Every other chamber is open to the sky, and that is not decoration.
        //
        // The first version roofed all twenty-six, and the harness measured what that did: a full
        // Dominion round on this map produced eight shots and *zero damage*, where the identical
        // mode with the identical bots on the Furnace produced fifty-nine and fifty-one. Nothing
        // was broken — the map had simply cut every sightline in the district, so twelve fighters
        // walked three hundred metres each and never saw one another. A maze with a lid is not a
        // close-quarters map, it is a building nobody meets inside.
        //
        // Open cells put the sightlines back without giving up the warren: you still cannot see
        // *through* a chamber, but you can see across the block, and the upper storey can see down
        // into it. Which is the version of this that is worth walking into.
        for (int gx = -2; gx <= 2; gx++)
        for (int gz = -1; gz <= 1; gz++)
        {
            bool odd = ((gx + gz) & 1) == 0;

            // The corner of the warren carries the gun, wherever that corner ended up. Fixing the
            // crate to the coordinate instead put two of them inside walls on a layout where the
            // warren had been nudged over.
            if (Room(new Vector3(gx * 22f, 0f, 46f + gz * 20f), new Vector3(9f, 2.8f, 8f),
                     doors: new[] { odd, odd, !odd, !odd }, roofed: odd)
                && gx == -2 && gz == 0)
                WeaponSpawns.Add(LastRoomAt with { Y = 1f });
        }

        // The mirror of it on the far side, offset half a cell so the two warrens do not read as
        // one repeated stamp.
        for (int gx = -2; gx <= 1; gx++)
        for (int gz = -1; gz <= 1; gz++)
        {
            bool odd = ((gx + gz) & 1) == 1;

            if (Room(new Vector3(gx * 22f + 11f, 0f, -46f + gz * 20f), new Vector3(9f, 2.8f, 8f),
                     doors: new[] { odd, odd, !odd, !odd }, roofed: odd)
                && gx == 1 && gz == 0)
                WeaponSpawns.Add(LastRoomAt with { Y = 1f });
        }
    }


    // ---- the puzzle chambers ----
    //
    // A completely separate build path from the four arenas, and it has to be.
    //
    // Every pass those maps run — the outer districts, the vehicles, the interiors, the weapon
    // crates, the launch pads — exists to make a *fight* work. A puzzle map wants none of it. What
    // it wants is the opposite: no route between two places except the one you make yourself, which
    // is precisely the thing the connectivity checks elsewhere in this file were written to
    // guarantee never happens.
    //
    // So a puzzle arena is chambers of solid floor with nothing between them, and the only thing
    // that crosses the nothing is a portal. That is the whole design.

    /// <summary>How many of the arenas at the end of <see cref="Names"/> are puzzle chambers.</summary>
    public const int PuzzleLayouts = 2;

    /// <summary>
    /// How many layouts before the puzzle chambers belong to story mode instead of to a fight.
    ///
    /// Ordered combat, story, puzzle so that <see cref="IsPuzzle"/> stays "the last few" and did
    /// not have to change when Fairview arrived.
    /// </summary>
    public const int StoryLayouts = 2;

    /// <summary>
    /// The Vessels' laboratory: the one arena that is a building rather than a floor.
    ///
    /// Named rather than left as an index because its height, its palette and its build pass all
    /// have to agree about which layout it is, and a magic 4 in three files is how they stop
    /// agreeing.
    /// </summary>
    public const int LaboratoryLayout = 4;

    /// <summary>
    /// Coldstore: the siege across open snow.
    ///
    /// Named for the same reason the Laboratory is - its palette, its build pass and the two
    /// passes it opts out of all have to agree about which layout it is.
    /// </summary>
    public const int ColdstoreLayout = 5;

    /// <summary>Fairview, the town of Act I.</summary>
    public static int FairviewLayout => CombatLayouts;

    /// <summary>The chamber the four convene in for Act II.</summary>
    public static int ConvocationLayout => CombatLayouts + 1;

    /// <summary>How many layouts are combat arenas — the only ones a versus match may pick.</summary>
    ///
    /// A property rather than a const because it is derived from the length of the name list, and
    /// deriving it is the point: adding a map should not require anybody to remember to change a
    /// number somewhere else.
    public static int CombatLayouts => Names.Length - PuzzleLayouts - StoryLayouts;

    /// <summary>
    /// The index the puzzle chambers start at.
    ///
    /// Named because it is *not* <see cref="CombatLayouts"/>, and it was until Fairview arrived.
    /// Those were the same number for as long as there were only two kinds of layout, and one
    /// caller was picking a random puzzle chamber by counting up from the end of the arenas — which
    /// silently became "or the town" the moment something sat between them.
    /// </summary>
    public static int FirstPuzzleLayout => Names.Length - PuzzleLayouts;

    /// <summary>Whether a layout index is a puzzle map rather than an arena.</summary>
    public static bool IsPuzzle(int layout) => layout >= FirstPuzzleLayout;

    /// <summary>Whether a layout belongs to story mode. Never picked by a versus match.</summary>
    public static bool IsStory(int layout) => layout >= CombatLayouts && !IsPuzzle(layout);

    /// <summary>
    /// Whether a layout is a combat arena — not a puzzle chamber, not a story set.
    ///
    /// The predicate the invariants actually want. Six checks across the harness were written as
    /// "skip puzzle chambers" and meant "only real arenas", and the difference did not exist until
    /// there was a third kind of map. A town has no weapon crates, no vehicle spawns and no
    /// destructible skybridges, and every one of those checks would have failed on it while being
    /// completely right about arenas.
    /// </summary>
    public static bool IsArena(int layout) => !IsPuzzle(layout) && !IsStory(layout);

    /// <summary>This arena's checkpoints, in the order they must be reached.</summary>
    public readonly List<Vector3> Checkpoints = new();

    /// <summary>True when this layout is a puzzle chamber set rather than a combat arena.</summary>
    public bool Puzzle => IsPuzzle(Layout);

    /// <summary>True when this layout is a story set rather than somewhere a match happens.</summary>
    public bool Story => IsStory(Layout);

    /// <summary>
    /// A slab of floor with a lip, floating in the void.
    ///
    /// The lip matters: without a raised edge a player walking backwards while lining up a portal
    /// steps off without ever seeing the drop, and a puzzle that kills you for looking up is not a
    /// puzzle. Low enough to shoot a portal over, high enough to feel underfoot.
    /// </summary>
    void Ledge(Vector3 centre, float halfX, float halfZ, bool lip = true)
    {
        Deck(centre, new Vector3(halfX, 0.6f, halfZ), DeckTint);
        FloorSlabs.Add(new Rect2(centre.X - halfX, centre.Z - halfZ, halfX * 2f, halfZ * 2f));

        if (!lip) return;

        foreach (int sx in new[] { -1, 1 })
            Blocks.Add(new Block(centre + new Vector3(sx * halfX, 1.0f, 0f),
                                 new Vector3(0.3f, 0.4f, halfZ), WallTint));

        foreach (int sz in new[] { -1, 1 })
            Blocks.Add(new Block(centre + new Vector3(0f, 1.0f, sz * halfZ),
                                 new Vector3(halfX, 0.4f, 0.3f), WallTint));
    }

    /// <summary>
    /// A wall you can put a portal on. The only currency in a puzzle map.
    ///
    /// Tall and flat and deliberately unmissable: every crossing in these maps is "there is a
    /// surface over there, and the far side of the gap is behind you", so the surfaces have to read
    /// as targets rather than as scenery.
    /// </summary>
    void PortalWall(Vector3 centre, float halfX, float halfY, float halfZ)
        => Blocks.Add(new Block(centre, new Vector3(halfX, halfY, halfZ), AccentTint));

    /// <summary>A checkpoint, and the pad under it so it reads as somewhere to stand.</summary>
    void Checkpoint(Vector3 at)
    {
        Checkpoints.Add(at);
        Blocks.Add(new Block(at with { Y = 0.7f }, new Vector3(2.2f, 0.12f, 2.2f), PadTint));
    }

    /// <summary>
    /// THE ANTECHAMBER — the teaching map.
    ///
    /// Four islands in a line, each gap wider than any jump, each with a portal wall facing back
    /// across it. Nothing here is clever: it exists so that two people who have never used the gun
    /// work out, once, that a gate on the far wall and a gate at their feet is a bridge. Every
    /// later chamber assumes that lesson.
    /// </summary>
    /// <summary>
    /// FAIRVIEW — the town the Vessels built to raise John Smith in.
    ///
    /// The only map in the game that is not a place to fight. Everything else here is a ruin or an
    /// arena; this is somewhere people live, and Act I does not work unless the player believes
    /// that for an hour before finding out otherwise.
    ///
    /// Built to be *readable as a set* on a second look rather than on the first. The tells are
    /// deliberate and none of them is pointed at:
    ///
    /// - **Every house is the same house.** Four footprints, repeated down both sides of one
    ///   street, alternating so it scans as variety in motion and as a pattern when you stop.
    /// - **The street runs straight and stops.** No junctions, no side roads, nowhere the layout
    ///   admits anything exists beyond it — because nothing does.
    /// - **The school has one classroom.** A town of this size needs eight. This one needed one,
    ///   because there was one child.
    /// - **The green is exactly central**, and everything faces it, the way nothing real ever is.
    ///
    /// No crates, no hazards, no launch pads, nothing breakable. Those are all handled by the
    /// early return in the constructor rather than here, so this method is only the town.
    /// </summary>
    void BuildFairview()
    {
        var render = new Color(0.86f, 0.84f, 0.78f);      // rendered walls, sun-bleached
        var roof = new Color(0.42f, 0.36f, 0.34f);
        var civic = new Color(0.78f, 0.80f, 0.84f);       // the school and the hall, colder
        var hedge = new Color(0.36f, 0.50f, 0.34f);

        // The street. One axis, and it is the whole town plan.
        const float StreetHalf = 5.5f;
        const float Row = 16f;                            // house centres, off the street
        const float First = -78f;
        const float Pitch = 26f;                          // door to door along the street
        const int Houses = 6;                             // per side
        const float StreetEnd = 92f;                      // where the specification stopped

        // Spawns are on the doorstep of the house that is meant to be his, not on a grid. There is
        // no versus match here to balance, and a story that begins by dropping you in a field
        // begins worse than one that begins with you leaving your own front door.
        SpawnPoints.Add(new Vector3(First + Pitch * 2f, 1.4f, -Row + 6f));
        SpawnPoints.Add(new Vector3(First + Pitch * 2f + 3f, 1.4f, -Row + 6f));

        for (int i = 0; i < Houses; i++)
        {
            float x = First + i * Pitch;

            foreach (int side in new[] { -1, 1 })
            {
                // Four footprints in rotation. The variation is real and the vocabulary is tiny,
                // which is exactly the impression wanted: a place somebody specified.
                int kind = (i + (side > 0 ? 2 : 0)) % 4;
                var half = kind switch
                {
                    0 => new Vector3(6.0f, 3.2f, 5.0f),
                    1 => new Vector3(5.0f, 3.6f, 5.0f),
                    2 => new Vector3(6.5f, 3.0f, 4.5f),
                    _ => new Vector3(5.5f, 3.4f, 5.5f),
                };

                var at = new Vector3(x, 0f, side * Row);

                // The door faces the street, always. Which side that is depends on which side of
                // the street the house is on, and nothing else about the house changes.
                var doors = side < 0
                    ? new[] { false, false, false, true }
                    : new[] { false, false, true, false };

                if (!Room(at, half, doors, roofed: true, render, SurfaceKind.Plaster)) continue;

                // A roof, sat on top of the box, purely so the skyline is not flat. It is the one
                // piece of this map that exists for no reason but to look like a place.
                Deck(LastRoomAt + Vector3.Up * (half.Y * 2f + 0.4f),
                     new Vector3(half.X + 0.6f, 0.4f, half.Z + 0.6f), roof, SurfaceKind.RoofTile);

                // A hedge along the front, leaving the doorway clear.
                float front = side * (Row - half.Z - 2.2f);
                Deck(new Vector3(x - half.X * 0.55f, 0.5f, front),
                     new Vector3(2.2f, 0.5f, 0.4f), hedge, SurfaceKind.Foliage);
                Deck(new Vector3(x + half.X * 0.55f, 0.5f, front),
                     new Vector3(2.2f, 0.5f, 0.4f), hedge, SurfaceKind.Foliage);
            }
        }

        // The road. Flat enough to be a surface rather than a kerb, and the one thing in the town
        // that is not a building — without it the houses read as boxes on a field.
        Deck(new Vector3(0f, 0.04f, 0f), new Vector3(StreetEnd, 0.04f, StreetHalf),
             new Color(0.30f, 0.30f, 0.32f), SurfaceKind.Tarmac);

        // The green, dead centre, with everything looking at it.
        Deck(new Vector3(0f, 0.06f, 0f), new Vector3(13f, 0.06f, StreetHalf + 1f),
             hedge, SurfaceKind.Foliage);

        // The school, on the north side of the green. One classroom.
        Room(new Vector3(0f, 0f, -Row - 6f), new Vector3(11f, 4.0f, 7f),
             new[] { false, false, false, true }, roofed: true, civic, SurfaceKind.Concrete);
        Deck(LastRoomAt + Vector3.Up * 8.4f, new Vector3(11.6f, 0.5f, 7.6f), roof, SurfaceKind.RoofTile);

        // The hall opposite it, which is the only other public building and has never been used
        // for anything. It is there because a town has one.
        Room(new Vector3(0f, 0f, Row + 6f), new Vector3(9f, 3.6f, 6f),
             new[] { false, false, true, false }, roofed: true, civic, SurfaceKind.Concrete);
        Deck(LastRoomAt + Vector3.Up * 7.6f, new Vector3(9.6f, 0.5f, 6.6f), roof, SurfaceKind.RoofTile);

        // And the end of the street. A low wall across it, and nothing drawn past it.
        //
        // Not a fence, not a gate, not a road going on into fog — a wall, at the point where the
        // specification stopped. A player who walks the length of Fairview arrives at the edge of
        // what was built for him, which is the act's turn arriving early for anyone curious enough
        // to go looking. It should be possible to find in the first ten minutes.
        foreach (int end in new[] { -1, 1 })
            Deck(new Vector3(end * StreetEnd, 2.0f, 0f),
                 new Vector3(1.0f, 2.0f, Row + 12f), civic.Darkened(0.35f), SurfaceKind.Concrete);
    }

    // ---- the Convocation ----
    //
    // Act II's set, and the opposite of Fairview in every way that matters. Fairview is a place
    // somebody lives, built to be liked; this is a room four civilisations built to be *right* in.
    // Nothing in it is comfortable, there is no way out of it, and the only thing on the floor is
    // the man they are arguing about.
    //
    // The four bays are the argument made out of geometry. Each faction has an identical footprint
    // at an identical distance — nobody is nearer, nobody is higher, nobody has the floor — and
    // the player decides the whole story by walking into one of them. That is the entire design,
    // and everything below is in service of making the walk read as a decision.

    /// <summary>How far the four delegations stand from the middle of the floor.</summary>
    public const float ConvocationRadius = 34f;

    /// <summary>How much floor each delegation has, measured out from its bay's mouth.</summary>
    public const float ConvocationBay = 9f;

    /// <summary>
    /// The four bays, in a fixed order, so the level and the script cannot disagree about which
    /// corner belongs to whom.
    ///
    /// East, west, north, south, and the assignment is not arbitrary: the Garden is put in the
    /// player's path along the street of the room, the Vessels are behind him where everything he
    /// came from is, and the Custodians face him because they are the ones who ask him a question
    /// rather than making him an offer.
    /// </summary>
    public static Vector3 ConvocationSeat(int i) => i switch
    {
        0 => new Vector3(ConvocationRadius, 0f, 0f),      // the Garden
        1 => new Vector3(-ConvocationRadius, 0f, 0f),     // Ingenuity
        2 => new Vector3(0f, 0f, -ConvocationRadius),     // the Custodians
        _ => new Vector3(0f, 0f, ConvocationRadius),      // the Vessels
    };

    /// <summary>The faction standing in each bay, in the same order as <see cref="ConvocationSeat"/>.</summary>
    public static FactionDef ConvocationHost(int i) => i switch
    {
        0 => Factions.Garden,
        1 => Factions.Ingenuity,
        2 => Factions.Custodians,
        _ => Factions.Vessels,
    };

    // ---- dressing ----
    //
    // A vocabulary of props, each built from a handful of rotated boxes. Not because boxes are
    // the ambition, but because a box at an angle with the right material on it stops being a box
    // at about four metres, and eight of them arranged as a barrel stops being one immediately.
    // The silhouette does almost all of the work; the material does the rest.
    //
    // Every one of these is decoration only. Nothing here is walked on, shot through differently,
    // or pathed around - see Decor for why that is a deliberate line rather than a shortcut.

    /// <summary>Deterministic per-arena randomness, so a map dresses the same way every launch.</summary>
    ///
    /// Seeded from the layout index rather than from the clock. A map that rearranges its own
    /// litter between rounds is a map players cannot learn, and learning where things are is most
    /// of what makes a place feel like a place.
    Random dressRng = new(0);

    float Vary(float a, float b) => a + (float)dressRng.NextDouble() * (b - a);
    float Spin() => (float)dressRng.NextDouble() * 360f;

    bool DecorRoom => Decorations.Count < DecorBudget;

    void Prop(Vector3 at, Vector3 half, Color tint, SurfaceKind surface, Vector3 turn = default)
    {
        if (!DecorRoom) return;

        // Kept inside the map at the one place every prop passes through.
        //
        // The scatter passes work outward from walls and along the perimeter, and both of those
        // put things near the edge on purpose - then a random spread of a metre or two carries
        // some of them past it. Clamping here rather than in each caller means a new prop shape
        // cannot reintroduce this, and clamping rather than rejecting means the litter still
        // reaches the edges of the map, which is where it was wanted.
        at.X = Mathf.Clamp(at.X, -HalfWidth + 1f, HalfWidth - 1f);
        at.Z = Mathf.Clamp(at.Z, -HalfDepth + 1f, HalfDepth - 1f);

        Decorations.Add(new Decor(at, half, tint, surface, turn));
    }

    /// <summary>
    /// A drum: two boxes crossed at 45 degrees, which is an octagon from every angle that matters,
    /// plus a rim band so it reads as a container rather than a post.
    /// </summary>
    void Barrel(Vector3 foot, Color tint, float scale = 1f)
    {
        float r = 0.42f * scale, h = 0.58f * scale;
        var mid = foot + Vector3.Up * h;
        float yaw = Spin();

        Prop(mid, new Vector3(r, h, r), tint, SurfaceKind.Panel, new Vector3(0f, yaw, 0f));
        Prop(mid, new Vector3(r, h * 0.98f, r), tint, SurfaceKind.Panel, new Vector3(0f, yaw + 45f, 0f));

        // Two bands, darker, standing slightly proud. This is the whole difference between a
        // barrel and a bollard.
        foreach (float t in new[] { 0.42f, 0.72f })
            Prop(foot + Vector3.Up * (h * 2f * t), new Vector3(r * 1.06f, 0.05f * scale, r * 1.06f),
                 tint.Darkened(0.4f), SurfaceKind.Panel, new Vector3(0f, yaw + 22f, 0f));
    }

    /// <summary>A crate, sat at a slight angle with battens along its edges.</summary>
    void Crate(Vector3 foot, Color tint, float scale = 1f)
    {
        float s = 0.55f * scale;
        var mid = foot + Vector3.Up * s;
        var turn = new Vector3(0f, Spin(), 0f);

        Prop(mid, new Vector3(s, s, s), tint, SurfaceKind.Timber, turn);

        // Battens: two thin bands across the faces, which is what says "crate" rather than "cube".
        Prop(mid + Vector3.Up * s * 0.55f, new Vector3(s * 1.03f, s * 0.10f, s * 1.03f),
             tint.Darkened(0.28f), SurfaceKind.Timber, turn);
        Prop(mid - Vector3.Up * s * 0.55f, new Vector3(s * 1.03f, s * 0.10f, s * 1.03f),
             tint.Darkened(0.28f), SurfaceKind.Timber, turn);
    }

    /// <summary>Broken masonry: a few slabs at unrelated angles, lying where they fell.</summary>
    void Rubble(Vector3 at, Color tint, int pieces = 4, float spread = 2.2f)
    {
        for (int i = 0; i < pieces; i++)
        {
            var to = at + new Vector3(Vary(-spread, spread), 0f, Vary(-spread, spread));
            float sx = Vary(0.22f, 0.7f), sy = Vary(0.07f, 0.22f), sz = Vary(0.22f, 0.7f);

            Prop(to + Vector3.Up * sy, new Vector3(sx, sy, sz), tint.Darkened(Vary(0f, 0.3f)),
                 SurfaceKind.Concrete,
                 new Vector3(Vary(-14f, 14f), Spin(), Vary(-14f, 14f)));
        }
    }

    /// <summary>A run of pipe along a wall, with a flange every few metres.</summary>
    void PipeRun(Vector3 from, Vector3 to, float radius, Color tint)
    {
        var mid = (from + to) * 0.5f;
        var span = to - from;
        float len = span.Length();
        if (len < 0.5f) return;

        bool alongX = MathF.Abs(span.X) > MathF.Abs(span.Z);
        var half = alongX ? new Vector3(len * 0.5f, radius, radius)
                          : new Vector3(radius, radius, len * 0.5f);

        Prop(mid, half, tint, SurfaceKind.Panel);
        Prop(mid, half * new Vector3(1f, 0.99f, 0.99f), tint, SurfaceKind.Panel,
             alongX ? new Vector3(45f, 0f, 0f) : new Vector3(0f, 0f, 45f));

        int flanges = Mathf.Clamp((int)(len / 4f), 1, 4);
        for (int i = 1; i <= flanges; i++)
        {
            float t = i / (float)(flanges + 1);
            var at = from.Lerp(to, t);
            var band = alongX ? new Vector3(radius * 0.3f, radius * 1.35f, radius * 1.35f)
                              : new Vector3(radius * 1.35f, radius * 1.35f, radius * 0.3f);
            Prop(at, band, tint.Darkened(0.35f), SurfaceKind.Panel);
        }
    }

    /// <summary>A planter with something growing out of it.</summary>
    void Planter(Vector3 foot, Color box, Color green)
    {
        float w = Vary(0.8f, 1.15f);
        Prop(foot + Vector3.Up * 0.34f, new Vector3(w, 0.34f, w * 0.8f), box, SurfaceKind.Concrete,
             new Vector3(0f, Vary(-8f, 8f), 0f));

        for (int i = 0; i < 3; i++)
            Prop(foot + new Vector3(Vary(-w * 0.5f, w * 0.5f), Vary(0.7f, 1.15f),
                                    Vary(-w * 0.4f, w * 0.4f)),
                 new Vector3(Vary(0.22f, 0.42f), Vary(0.24f, 0.5f), Vary(0.22f, 0.42f)),
                 green.Darkened(Vary(0f, 0.25f)), SurfaceKind.Foliage,
                 new Vector3(Vary(-20f, 20f), Spin(), Vary(-20f, 20f)));
    }

    /// <summary>A lamp on a post, leaning very slightly, because nothing outdoors is plumb.</summary>
    void StreetLight(Vector3 foot, Color post)
    {
        float h = Vary(3.4f, 4.0f);
        float lean = Vary(-1.4f, 1.4f);

        Prop(foot + Vector3.Up * h * 0.5f, new Vector3(0.09f, h * 0.5f, 0.09f), post,
             SurfaceKind.Panel, new Vector3(lean, Spin(), lean));

        Prop(foot + Vector3.Up * (h + 0.06f), new Vector3(0.55f, 0.09f, 0.2f),
             post.Darkened(0.2f), SurfaceKind.Panel, new Vector3(0f, Spin(), 0f));
    }

    /// <summary>Tufts of growth, for the seams where a floor meets a wall.</summary>
    void Weeds(Vector3 at, Color green, int tufts, float spread)
    {
        for (int i = 0; i < tufts; i++)
            Prop(at + new Vector3(Vary(-spread, spread), Vary(0.10f, 0.26f), Vary(-spread, spread)),
                 new Vector3(Vary(0.12f, 0.34f), Vary(0.10f, 0.26f), Vary(0.12f, 0.34f)),
                 green.Darkened(Vary(0f, 0.35f)), SurfaceKind.Foliage,
                 new Vector3(Vary(-18f, 18f), Spin(), Vary(-18f, 18f)));
    }

    /// <summary>
    /// Dress a combat arena.
    ///
    /// Placed against the blocks already standing rather than on a grid: props go where things
    /// collect in real places — at the feet of walls, in corners, against the perimeter. Scanning
    /// the block list to find those spots means a layout change moves the litter with it, which is
    /// the only way this stays true after the next time somebody edits a map.
    /// </summary>
    void Dress()
    {
        dressRng = new Random(9001 + Layout * 7919);

        var drum = new Color(0.52f, 0.44f, 0.26f);
        var timber = new Color(0.55f, 0.38f, 0.22f);
        var stone = new Color(0.48f, 0.48f, 0.50f);
        var green = new Color(0.30f, 0.46f, 0.24f);
        var steel = new Color(0.40f, 0.43f, 0.48f);

        // Sizeable standing blocks are walls, and things get left at the foot of walls.
        var walls = new List<Block>();
        foreach (var b in Blocks)
        {
            if (b.Fragile) continue;
            if (b.HalfExtents.Y < 1.2f) continue;                 // not a wall, a step
            if (b.Centre.Y - b.HalfExtents.Y > 0.8f) continue;    // not on the ground
            walls.Add(b);
        }

        // Shuffled so a budget that runs out does not always run out in the same corner of the
        // map. Fisher-Yates on a seeded generator, so it is still the same every launch.
        for (int i = walls.Count - 1; i > 0; i--)
        {
            int j = dressRng.Next(i + 1);
            (walls[i], walls[j]) = (walls[j], walls[i]);
        }

        foreach (var w in walls)
        {
            if (!DecorRoom) break;

            // Pick a face and stand along it, a little way out so nothing z-fights the wall.
            bool longX = w.HalfExtents.X > w.HalfExtents.Z;
            float side = dressRng.Next(2) == 0 ? 1f : -1f;

            var outward = longX ? new Vector3(0f, 0f, side) : new Vector3(side, 0f, 0f);
            float offset = (longX ? w.HalfExtents.Z : w.HalfExtents.X) + 0.55f;
            float along = longX ? w.HalfExtents.X : w.HalfExtents.Z;

            var basePos = w.Centre with { Y = w.Centre.Y - w.HalfExtents.Y } + outward * offset;
            var slide = longX ? Vector3.Right : Vector3.Back;

            switch (dressRng.Next(6))
            {
                case 0:
                    Barrel(basePos + slide * Vary(-along * 0.6f, along * 0.6f), drum, Vary(0.85f, 1.15f));
                    Barrel(basePos + slide * Vary(-along * 0.6f, along * 0.6f), drum, Vary(0.85f, 1.1f));
                    break;
                case 1:
                    Crate(basePos + slide * Vary(-along * 0.6f, along * 0.6f), timber, Vary(0.8f, 1.2f));
                    break;
                case 2:
                    Rubble(basePos + slide * Vary(-along * 0.5f, along * 0.5f), stone, 5, 1.8f);
                    break;
                case 3:
                    PipeRun(basePos + slide * -along * 0.8f + Vector3.Up * Vary(1.4f, 2.6f),
                            basePos + slide * along * 0.8f + Vector3.Up * Vary(1.4f, 2.6f),
                            Vary(0.10f, 0.17f), steel);
                    break;
                case 4:
                    Weeds(basePos + slide * Vary(-along * 0.7f, along * 0.7f), green, 4, 1.3f);
                    break;
                default:
                    Planter(basePos + slide * Vary(-along * 0.5f, along * 0.5f), stone, green);
                    break;
            }
        }

        // The perimeter, which is otherwise the emptiest and most visible surface on the map.
        for (int i = 0; i < 14 && DecorRoom; i++)
        {
            float t = (i + 0.5f) / 14f;
            float x = -HalfWidth + t * HalfWidth * 2f;

            foreach (float z in new[] { -HalfDepth + 2.2f, HalfDepth - 2.2f })
            {
                if (!DecorRoom) break;
                if (dressRng.Next(3) == 0) Weeds(new Vector3(x, 0f, z), green, 3, 1.6f);
                else if (dressRng.Next(2) == 0) Rubble(new Vector3(x, 0f, z), stone, 3, 1.4f);
                else StreetLight(new Vector3(x, 0f, z), steel);
            }
        }
    }

    /// <summary>
    /// Dress Fairview, which wants a different hand entirely.
    ///
    /// The combat pass scatters industrial litter to make an arena look used. A town has to look
    /// *lived in*, which is the opposite job: nothing broken, nothing burnt, everything tidy and
    /// slightly dull. That is the whole point of Fairview — it was built for one person by people
    /// who wanted him to like it — so this places bins that have been put out, hedges that have
    /// been cut, and lamps that all work.
    /// </summary>
    void DressFairview()
    {
        dressRng = new Random(4242);

        var green = new Color(0.34f, 0.48f, 0.28f);
        var steel = new Color(0.42f, 0.45f, 0.50f);
        var stone = new Color(0.62f, 0.62f, 0.60f);
        var bin = new Color(0.28f, 0.36f, 0.30f);

        const float Row = 16f, Pitch = 26f, First = -78f;
        const int Houses = 6;

        for (int i = 0; i < Houses; i++)
        {
            float x = First + i * Pitch;

            foreach (int side in new[] { -1, 1 })
            {
                float front = side * (Row - 8.5f);

                // A wheelie bin at the kerb, and a second one when the household has two.
                Barrel(new Vector3(x + Vary(-2.5f, 2.5f), 0f, front), bin, 0.9f);
                if (dressRng.Next(2) == 0)
                    Barrel(new Vector3(x + Vary(-3.5f, 3.5f), 0f, front), bin, 0.85f);

                // A planter by the door.
                Planter(new Vector3(x + Vary(-4f, 4f), 0f, side * (Row - 10.5f)), stone, green);
            }
        }

        // Lamp posts down one side of the street, evenly, the way a council would.
        for (float x = First - 6f; x <= 88f && DecorRoom; x += Pitch * 0.5f)
            StreetLight(new Vector3(x, 0f, -7.5f), steel);

        // And the green itself gets shrubs rather than litter.
        for (int i = 0; i < 8 && DecorRoom; i++)
            Weeds(new Vector3(Vary(-12f, 12f), 0f, Vary(-4f, 4f)), green, 3, 1.2f);
    }

    void BuildConvocation()
    {
        var stone = new Color(0.30f, 0.30f, 0.33f);
        var floor = new Color(0.24f, 0.24f, 0.27f);

        // He starts a little short of the middle, so the first thing the act asks him to do is
        // walk into the centre of a room that is already looking at him.
        SpawnPoints.Add(new Vector3(0f, 1.4f, 14f));
        SpawnPoints.Add(new Vector3(2.5f, 1.4f, 14f));

        // The floor he is called onto. Raised barely enough to feel like a stage underfoot, which
        // is what it is.
        Deck(new Vector3(0f, 0.12f, 0f), new Vector3(11f, 0.12f, 11f), floor, SurfaceKind.Concrete);

        const float Wall = 48f;
        const float Height = 9f;

        // A sealed square. No gate, no gap, no corridor out — the act ends when he answers, and a
        // room with a visible exit invites a player to spend Act II looking for it.
        foreach (int side in new[] { -1, 1 })
        {
            Deck(new Vector3(side * Wall, Height, 0f),
                 new Vector3(1.2f, Height, Wall), stone, SurfaceKind.Concrete);
            Deck(new Vector3(0f, Height, side * Wall),
                 new Vector3(Wall, Height, 1.2f), stone, SurfaceKind.Concrete);
        }

        for (int i = 0; i < 4; i++)
        {
            var seat = ConvocationSeat(i);
            var host = ConvocationHost(i);

            // Which way this bay faces. One of the two components is zero, so this is the axis it
            // stands on and the sign is the direction it stands in.
            bool alongX = MathF.Abs(seat.X) > MathF.Abs(seat.Z);
            float sign = alongX ? MathF.Sign(seat.X) : MathF.Sign(seat.Z);

            // Depth runs away from the centre, width across it. Swapping the two for the east and
            // west bays is the whole of what makes four identical bays face four ways.
            Vector3 Extent(float across, float deep, float y)
                => alongX ? new Vector3(deep, y, across) : new Vector3(across, y, deep);

            Vector3 Out(float d) => alongX
                ? new Vector3(seat.X + sign * d, 0f, 0f)
                : new Vector3(0f, 0f, seat.Z + sign * d);

            // The back of the bay, in the faction's own colour, darkened. It is the only large
            // block of colour in the room and it is what the player is walking towards.
            Deck(Out(ConvocationBay) + Vector3.Up * 5f, Extent(ConvocationBay + 2f, 1.0f, 5f),
                 host.Tint.Darkened(0.55f), SurfaceKind.Concrete);

            // Two side walls, low enough to see over from the floor and high enough that standing
            // between them is standing somewhere rather than near something.
            foreach (int s in new[] { -1, 1 })
            {
                var across = alongX
                    ? new Vector3(0f, 0f, s * (ConvocationBay + 1.5f))
                    : new Vector3(s * (ConvocationBay + 1.5f), 0f, 0f);

                Deck(Out(ConvocationBay * 0.5f) + across + Vector3.Up * 2.6f,
                     Extent(1.0f, ConvocationBay * 0.5f, 2.6f), stone, SurfaceKind.Concrete);
            }

            // The floor of the bay, tinted. This is the patch the mission measures against, so it
            // is exactly as wide as the zone the player has to stand in — what he can see and what
            // the game is testing are the same rectangle, which is the only honest way to ask
            // somebody to commit by standing somewhere.
            Deck(Out(0f) + Vector3.Up * 0.1f, Extent(ConvocationBay, ConvocationBay, 0.1f),
                 host.Tint.Darkened(0.25f), SurfaceKind.Concrete);

            // A standard at the back, so a bay is legible from the middle of the room at a glance
            // and the player never has to walk somewhere to find out whose it is.
            Deck(Out(ConvocationBay - 0.5f) + Vector3.Up * 7f,
                 Extent(0.5f, 0.5f, 7f), host.Tint, SurfaceKind.Panel);
        }
    }

    void BuildAntechamber()
    {
        SpawnPoints.Add(new Vector3(-96f, 1.4f, -6f));
        SpawnPoints.Add(new Vector3(-96f, 1.4f, 6f));

        for (int i = 0; i < 4; i++)
        {
            float x = -96f + i * 44f;

            Ledge(new Vector3(x, 0f, 0f), 14f, 16f);

            // The wall stands on the far side of each island, facing back the way you came, so the
            // shot you need is always the one across the gap you are looking at.
            PortalWall(new Vector3(x + 15f, 6f, 0f), 0.6f, 6f, 12f);

            if (i > 0) Checkpoint(new Vector3(x, 0f, 0f));
        }

        // One high wall at the far end, so the last crossing is upward as well as across — the
        // first thing in the game that asks you to think about where a portal *puts* you rather
        // than only about reaching it.
        PortalWall(new Vector3(48f, 16f, 0f), 0.6f, 10f, 14f);
        Ledge(new Vector3(76f, 18f, 0f), 12f, 12f);
        Checkpoint(new Vector3(76f, 18f, 0f));
    }

    /// <summary>
    /// THE ORRERY — the one that needs two people.
    ///
    /// A ring of islands around a tower, and the tower's own walls face outward only. One player
    /// standing on the ring can open a gate onto a face the other cannot see from where they are,
    /// which is the point: the checkpoints at the top are reachable, but not by anybody working
    /// alone.
    /// </summary>
    void BuildOrrery()
    {
        SpawnPoints.Add(new Vector3(-70f, 1.4f, -70f));
        SpawnPoints.Add(new Vector3(70f, 1.4f, 70f));

        // The outer ring: six islands, each with a wall facing the middle.
        for (int i = 0; i < 6; i++)
        {
            float a = i * MathF.Tau / 6f;
            var at = new Vector3(MathF.Cos(a) * 76f, 0f, MathF.Sin(a) * 76f);

            Ledge(at, 13f, 13f);

            var inward = new Vector3(-MathF.Cos(a), 0f, -MathF.Sin(a));
            PortalWall(at + inward * 12f + Vector3.Up * 7f, 6f, 7f, 6f);

            if (i % 2 == 0) Checkpoint(at);
        }

        // The tower, in stages, each stage's landing only reachable from a wall on the ring.
        for (int tier = 0; tier < 3; tier++)
        {
            float y = 12f + tier * 16f;
            Ledge(new Vector3(0f, y, 0f), 16f - tier * 3f, 16f - tier * 3f);

            // Outward-facing faces, so a gate placed from the ring lands you on the tier.
            foreach (int sx in new[] { -1, 1 })
                PortalWall(new Vector3(sx * (17f - tier * 3f), y + 6f, 0f), 0.6f, 6f, 10f - tier * 2f);
        }

        Checkpoint(new Vector3(0f, 44f, 0f));
    }

    // ---- build ----

    /// <summary>
    /// Realise the arena. <paramref name="fog"/> is off for the map preview, whose camera sits far
    /// enough back that aerial perspective tuned for a player inside the arena hazes the whole map.
    /// </summary>
    public void Build(Node3D parent, bool visuals, bool fog = true)
    {
        root = new Node3D { Name = "Arena" };
        parent.AddChild(root);

        foreach (var slab in FloorSlabs)
        {
            var body = new StaticBody3D
            {
                Position = new Vector3(slab.GetCenter().X, -1f, slab.GetCenter().Y),
            };
            root.AddChild(body);
            body.AddChild(new CollisionShape3D
            {
                Shape = new BoxShape3D { Size = new Vector3(slab.Size.X, 2f, slab.Size.Y) },
            });

            if (!visuals) continue;

            var ground = new Vector3(slab.GetCenter().X, -1f, slab.GetCenter().Y);
            var (groundKind, groundTint) = GroundDressing();

            body.AddChild(new MeshInstance3D
            {
                Mesh = new BoxMesh { Size = new Vector3(slab.Size.X, 2f, slab.Size.Y) },
                MaterialOverride = Graphics.SurfaceAt(groundTint, ground, 0f, false, groundKind),
            });
        }

        for (int i = 0; i < Blocks.Count; i++)
        {
            var b = Blocks[i];
            var body = new StaticBody3D { Position = b.Centre };
            root.AddChild(body);
            body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = b.HalfExtents * 2f } });

            // Handed out by index so the match can take a walkway away and put it back. Only the
            // fragile ones are kept — nothing else is ever going to move.
            if (b.Fragile) FragileBodies[i] = body;

            if (!visuals) continue;

            float top = b.Centre.Y + b.HalfExtents.Y;
            bool outer = MathF.Abs(b.Centre.X) > CoreX || MathF.Abs(b.Centre.Z) > CoreZ;

            var (kind, tint) = DressingFor(b, outer);

            body.AddChild(new MeshInstance3D
            {
                Mesh = new BoxMesh { Size = b.HalfExtents * 2f },
                MaterialOverride = Graphics.SurfaceAt(tint, b.Centre, top, outer, kind),
            });

            // No edge trim any more. Every block used to get four glowing bars stuck along its top
            // edges, and each bar had *two* faces exactly coplanar with the block it was decorating
            // — its top on the block's top, its outer face on the block's side. Two hundred blocks
            // times four bars times two surfaces is why the arena shimmered along every edge.
            //
            // The plating in the material does what the trim was there for, and does it without
            // nine hundred extra mesh instances in four splitscreen viewports.
        }

        if (!visuals) return;

        // Dressing. No bodies, no shapes, no entry in any of the queries below — see Decor.
        foreach (var d in Decorations)
        {
            root.AddChild(new MeshInstance3D
            {
                Position = d.Centre,
                RotationDegrees = d.Turn,
                Mesh = new BoxMesh { Size = d.HalfExtents * 2f },
                MaterialOverride = Graphics.SurfaceAt(d.Tint, d.Centre,
                                                      d.Centre.Y + d.HalfExtents.Y, false, d.Surface),

                // Props are small and everywhere. Letting them cast into the shadow map costs
                // more than the shadows are worth, and a barrel's own shadow is the least
                // interesting thing on a map that already shadows every wall.
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            });
        }

        root.AddChild(Graphics.BuildSun());
        root.AddChild(Graphics.BuildFill());
        root.AddChild(Graphics.BuildEnvironment(fog));
    }

    /// <summary>
    /// Emissive bars along the four top edges of a block. Flat shading gives two adjacent surfaces
    /// of similar value no boundary at all; a lit edge draws that boundary and is what makes the
    /// geometry read as built rather than as a silhouette.
    ///
    /// A frame, not a plate. Covering the whole top face was fine on waist-high cover but turned
    /// the big decks into glowing slabs, which was especially obvious from underneath.
    /// </summary>

    /// <summary>What the floor of a combat arena is made of.</summary>
    ///
    /// Named because it is asked for in two places and because it is a decision rather than a
    /// detail: the ground is the largest single surface in the game and was, until there were any
    /// materials at all, one flat blue-grey colour across every arena.
    /// <summary>
    /// What a block is made of, for blocks that never said.
    ///
    /// The combat arenas were built before the material library existed and ask for nothing, so
    /// every one of their three hundred blocks defaults to the original plating — which meant the
    /// seven materials could be finished, loaded and correct, and still be invisible anywhere
    /// except Fairview. Deriving a material from what a block *is* fixes that in one place instead
    /// of in nine hundred call sites, and a builder that does have an opinion still wins: only
    /// Panel, the default, is reinterpreted here.
    ///
    /// Fragile blocks are exempt and stay plating. That is legibility, not taste — the walkways of
    /// the upper storey are the only things on the map that can be blown out from under somebody,
    /// and a player has to be able to tell them apart at a glance. Making them look like the
    /// concrete they are standing on would hide a rule of the game inside a texture change.
    /// </summary>
    /// <summary>
    /// What one arena is built out of, and what colour it is.
    ///
    /// Four maps that each belong to somebody, painted from four different palettes. That
    /// belonging was written into this file long before there were any materials, and then the
    /// first material pass ignored it completely and derived everything from block shape - so the
    /// Vessels' ossuary, the Custodians' furnace, the Garden's glasshouse and Ingenuity's camp
    /// were all the same concrete and the same brick, and the maps had no identity at all.
    ///
    /// The four roles are what a place is made of at four scales: the big masses it is planned
    /// around, the walls of its rooms, the waist-high things you crouch behind, and the ground.
    /// </summary>
    public readonly struct Palette
    {
        public readonly SurfaceKind Mass, Wall, Cover, Ground;
        public readonly Color MassTint, WallTint, CoverTint, GroundTint;

        public Palette(SurfaceKind mass, Color massTint, SurfaceKind wall, Color wallTint,
                       SurfaceKind cover, Color coverTint, SurfaceKind ground, Color groundTint)
        {
            Mass = mass; MassTint = massTint;
            Wall = wall; WallTint = wallTint;
            Cover = cover; CoverTint = coverTint;
            Ground = ground; GroundTint = groundTint;
        }
    }

    /// <summary>
    /// The palette an arena is painted from. See <see cref="Palette"/> and the note on
    /// <see cref="Names"/> for whose each place is.
    ///
    /// The tints are multiplied over materials that were graded to one shared look, which is what
    /// lets four maps be recognisably different without coming apart into four art styles. The
    /// faction colours themselves are used sparingly and never at full strength - the Furnace is a
    /// place with gold in it, not a gold place.
    /// </summary>
    public static Palette PaletteFor(int layout)
    {
        if (layout == FairviewLayout || layout == ConvocationLayout || IsPuzzle(layout))
            return Neutral;

        return layout switch
        {
            // THE RELIQUARY - the Vessels'. Somewhere bodies are kept: pale stone, lead, and dark
            // oiled timber. The coldest and lightest of the four, and the only one that looks
            // clean.
            0 => new Palette(
                SurfaceKind.Concrete, new Color(0.78f, 0.77f, 0.74f),
                SurfaceKind.Plaster,  new Color(0.86f, 0.84f, 0.79f),
                SurfaceKind.Timber,   new Color(0.34f, 0.30f, 0.28f),
                SurfaceKind.Concrete, new Color(0.62f, 0.61f, 0.60f)),

            // THE FURNACE - the Custodians'. Prometheus chained to the fire: firebrick, soot and
            // scorched steel, with their gold showing through where the heat has not taken it.
            1 => new Palette(
                SurfaceKind.Concrete, new Color(0.40f, 0.35f, 0.32f),
                SurfaceKind.Brick,    new Color(0.86f, 0.52f, 0.34f),
                SurfaceKind.Timber,   new Color(0.26f, 0.22f, 0.20f),
                SurfaceKind.Tarmac,   new Color(0.52f, 0.44f, 0.38f)),

            // THE GLASSHOUSE - the Garden's. White-painted iron and glazing bars over planting
            // beds. Light, damp, and the only arena where the ground is growing.
            2 => new Palette(
                SurfaceKind.Plaster,  new Color(0.88f, 0.90f, 0.86f),
                SurfaceKind.Plaster,  new Color(0.80f, 0.85f, 0.80f),
                SurfaceKind.Timber,   new Color(0.52f, 0.46f, 0.34f),
                SurfaceKind.Foliage,  new Color(0.72f, 0.86f, 0.66f)),

            // THE THOUSAND ROOMS - Ingenuity's. A civilisation that cannot let capacity go unused,
            // building partitions forever: board, ply and breeze block in salvaged paint. The
            // drabbest of the four on purpose - it is the one closest to a camp.
            3 => new Palette(
                SurfaceKind.Concrete, new Color(0.56f, 0.54f, 0.50f),
                SurfaceKind.Timber,   new Color(0.66f, 0.56f, 0.42f),
                SurfaceKind.Timber,   new Color(0.86f, 0.50f, 0.34f),
                SurfaceKind.Tarmac,   new Color(0.50f, 0.49f, 0.47f)),

            // COLDSTORE - nobody's, which is why it is fought over. Snow, glare ice and cold
            // grey plant. The only arena whose ground is the brightest thing in it, and the only
            // one built from materials no other map uses at all.
            ColdstoreLayout => new Palette(
                SurfaceKind.Panel, new Color(0.44f, 0.47f, 0.52f),
                SurfaceKind.Snow,  new Color(0.95f, 0.96f, 0.98f),
                SurfaceKind.Ice,   new Color(0.70f, 0.82f, 0.92f),
                SurfaceKind.Snow,  new Color(0.90f, 0.93f, 0.97f)),

            // THE LABORATORY - the Vessels' again, and the same bone white as the Reliquary
            // because it is the same people, but everything here is soot-stained and cold. Where
            // the ossuary is kept clean, this was left exactly as it was found.
            _ => new Palette(
                SurfaceKind.Concrete, new Color(0.60f, 0.59f, 0.57f),
                SurfaceKind.Plaster,  new Color(0.70f, 0.69f, 0.66f),
                SurfaceKind.Timber,   new Color(0.30f, 0.28f, 0.27f),
                SurfaceKind.Tarmac,   new Color(0.46f, 0.45f, 0.44f)),
        };
    }

    /// <summary>The palette with no opinion, for the town, the hearing room and the void.</summary>
    static readonly Palette Neutral = new(
        SurfaceKind.Concrete, Colors.White, SurfaceKind.Brick, Colors.White,
        SurfaceKind.Timber, Colors.White, SurfaceKind.Tarmac, Colors.White);

    /// <summary>What this arena's ground is made of and what colour it is.</summary>
    public (SurfaceKind Kind, Color Tint) GroundDressing()
    {
        var p = PaletteFor(Layout);
        return (p.Ground, p.GroundTint);
    }

    /// <summary>
    /// What a block is made of and what colour it is, for blocks that never said.
    ///
    /// Only Panel, the default, is reinterpreted: a builder with an opinion still wins, which is
    /// what keeps Fairview's plaster and roof tile out of this.
    /// </summary>
    public (SurfaceKind Kind, Color Tint) DressingFor(Block b, bool outer)
    {
        if (b.Surface != SurfaceKind.Panel) return (b.Surface, b.Tint);

        var p = PaletteFor(Layout);

        // Out past the core is the shell of the place, and it takes the mass material at a lower
        // key so the outskirts do not compete with the middle for attention.
        if (outer) return (p.Mass, p.MassTint.Darkened(0.22f) * b.Tint);

        // Flat, wide and low is a floor, a step or a deck.
        if (b.HalfExtents.Y <= 0.6f) return (p.Mass, p.MassTint * b.Tint);

        float footprint = b.HalfExtents.X * b.HalfExtents.Z;

        // The big masses the map is planned around.
        if (footprint > 26f) return (p.Mass, p.MassTint * b.Tint);

        // Small and no taller than a person is something to crouch behind rather than part of the
        // building, and it says so by being made of something else.
        if (footprint <= 5f && b.HalfExtents.Y <= 1.6f) return (p.Cover, p.CoverTint * b.Tint);

        return (p.Wall, p.WallTint * b.Tint);
    }

    /// <summary>The material derivation, for the harness. See <see cref="DressingFor"/>.</summary>
    public SurfaceKind MaterialForTest(Block b, bool outer) => DressingFor(b, outer).Kind;

    public static StandardMaterial3D Flat(Color c) => Graphics.Surface(c);

    // ---- queries ----

    /// <summary>Whether a point in XZ is over a pit, and so has nothing to stand on.</summary>
    public bool IsOverPit(Vector3 p)
    {
        foreach (var pit in Pits)
            if (p.X > pit.Position.X && p.X < pit.End.X && p.Z > pit.Position.Y && p.Z < pit.End.Y)
                return true;
        return false;
    }

    /// <summary>
    /// Spawn furthest from any living opponent, so respawning never drops you into someone's
    /// crosshair. With no opponents alive it falls back to the slot's own corner.
    /// </summary>
    public Vector3 BestSpawn(IReadOnlyList<Pawn> pawns, Pawn forPawn)
    {
        Vector3 best = SpawnPoints[forPawn.Slot % SpawnPoints.Count];
        float bestScore = float.NegativeInfinity;

        foreach (var sp in SpawnPoints)
        {
            float nearest = float.PositiveInfinity;
            foreach (var p in pawns)
            {
                if (p == forPawn || !p.Alive) continue;
                nearest = MathF.Min(nearest, sp.DistanceTo(p.Position));
            }

            if (float.IsPositiveInfinity(nearest)) continue;
            if (nearest > bestScore) { bestScore = nearest; best = sp; }
        }

        return best;
    }

    /// <summary>
    /// Whether a capsule of <paramref name="radius"/> at <paramref name="p"/> clears every block.
    /// Used to assert that no spawn drops a player inside cover.
    /// </summary>
    public bool IsClearOfBlocks(Vector3 p, float radius, float height)
    {
        foreach (var b in Blocks)
        {
            // Vertical bands must overlap before a horizontal overlap matters — standing on top
            // of a deck is fine, standing inside it is not.
            float lowA = p.Y, highA = p.Y + height;
            float lowB = b.Centre.Y - b.HalfExtents.Y, highB = b.Centre.Y + b.HalfExtents.Y;
            if (highA <= lowB || highB <= lowA) continue;

            float dx = MathF.Max(0f, MathF.Abs(p.X - b.Centre.X) - b.HalfExtents.X);
            float dz = MathF.Max(0f, MathF.Abs(p.Z - b.Centre.Z) - b.HalfExtents.Z);
            if (dx * dx + dz * dz < radius * radius) return false;
        }
        return true;
    }

    /// <summary>
    /// Whether a point is somewhere a player is allowed to be.
    ///
    /// Tighter than <see cref="Contains"/>, which carries slack because it is an invariant check
    /// looking for gross escapes. This one is the play boundary: outside it is fatal.
    /// </summary>
    public bool InPlay(Vector3 p)
        => MathF.Abs(p.X) <= HalfWidth + 1.5f
        && MathF.Abs(p.Z) <= HalfDepth + 1.5f
        && p.Y > KillPlaneY - 1f
        && p.Y < CeilingY;

    public bool Contains(Vector3 p)
        => MathF.Abs(p.X) <= HalfWidth + 4f
        && MathF.Abs(p.Z) <= HalfDepth + 4f
        && p.Y > KillPlaneY - 30f && p.Y < 60f;
}
