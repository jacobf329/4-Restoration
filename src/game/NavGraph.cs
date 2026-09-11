using System;
using System.Collections.Generic;
using Godot;

namespace HitboxClone;

/// <summary>
/// A walkable graph built from an arena's block data, and A* over it.
///
/// Built from the arena description rather than baked from the rendered scene. Everything here is
/// already plain data — boxes, floor slabs, carved pits — so the graph can be constructed with no
/// physics, no meshes and no engine navigation baking. That means it works identically in the
/// headless harness, it is deterministic, and it costs nothing at runtime beyond the search.
///
/// The graph is genuinely three-dimensional. Every arena has decks stacked over floor, so a flat
/// grid would either ignore the upper level or confuse it with the ground beneath. Each column of
/// the grid can therefore hold several nodes — one per standable surface.
/// </summary>
public sealed class NavGraph
{
    /// <summary>Grid spacing. Fine enough to thread the gaps, coarse enough to stay cheap.</summary>
    public const float CellSize = 2.5f;

    /// <summary>Height difference a bot can simply walk up.</summary>
    const float StepUp = 0.7f;

    /// <summary>Height difference a bot can clear with a jump. Kept under the real jump apex.</summary>
    const float JumpUp = 2.6f;

    /// <summary>Drop a bot will willingly take. Beyond this it looks like falling, not pathing.</summary>
    const float MaxDrop = 5.5f;

    /// <summary>Headroom a surface needs before a pawn can stand on it.</summary>
    const float Headroom = Pawn.Height + 0.15f;

    readonly Arena arena;
    readonly AStar3D astar = new();

    /// <summary>
    /// Block indices bucketed by a coarse XZ grid, so a column only tests blocks near it.
    ///
    /// Without this the build is every cell against every block, twice - once to find surfaces and
    /// once for headroom. On the standard arena that is nine thousand cells against three hundred
    /// blocks and nobody notices. On Coldstore it is two hundred and eleven thousand against four
    /// hundred, which is eighty-three million footprint tests per graph before headroom doubles
    /// it, and the harness builds one of these per arena per scenario.
    ///
    /// Twenty metres a bucket rather than the nav cell's 2.5: a canyon slab is a hundred metres
    /// across and would otherwise be inserted into nine hundred buckets. Coarse enough that a big
    /// block costs little to file, fine enough that a column reads a handful of blocks instead of
    /// four hundred.
    /// </summary>
    const float BucketSize = 20f;

    readonly Dictionary<long, List<int>> buckets = new();
    static readonly List<int> NoBlocks = new();

    /// <summary>Node ids by column, so neighbours are found without a spatial query.</summary>
    readonly Dictionary<long, List<int>> columns = new();

    readonly List<Vector3> positions = new();

    public int NodeCount => positions.Count;

    public NavGraph(Arena arena)
    {
        this.arena = arena;
        BuildBuckets();
        BuildNodes();
        BuildEdges();
    }

    static long Key(int cx, int cz) => ((long)cx << 32) ^ (uint)cz;

    static int CellX(float x) => Mathf.FloorToInt(x / CellSize);
    static int CellZ(float z) => Mathf.FloorToInt(z / CellSize);
    static float CentreX(int cx) => (cx + 0.5f) * CellSize;
    static float CentreZ(int cz) => (cz + 0.5f) * CellSize;

    // ---- construction ----

    /// <summary>Files every block under each bucket its footprint touches.</summary>
    void BuildBuckets()
    {
        for (int bi = 0; bi < arena.Blocks.Count; bi++)
        {
            var b = arena.Blocks[bi];

            int x0 = Mathf.FloorToInt((b.Centre.X - b.HalfExtents.X) / BucketSize);
            int x1 = Mathf.FloorToInt((b.Centre.X + b.HalfExtents.X) / BucketSize);
            int z0 = Mathf.FloorToInt((b.Centre.Z - b.HalfExtents.Z) / BucketSize);
            int z1 = Mathf.FloorToInt((b.Centre.Z + b.HalfExtents.Z) / BucketSize);

            for (int bx = x0; bx <= x1; bx++)
            for (int bz = z0; bz <= z1; bz++)
            {
                long key = Key(bx, bz);
                if (!buckets.TryGetValue(key, out var list)) buckets[key] = list = new List<int>();
                list.Add(bi);
            }
        }
    }

    /// <summary>The blocks that could possibly cover this point.</summary>
    List<int> Near(float x, float z)
        => buckets.TryGetValue(Key(Mathf.FloorToInt(x / BucketSize), Mathf.FloorToInt(z / BucketSize)),
                               out var list) ? list : NoBlocks;

    void BuildNodes()
    {
        int minX = CellX(-arena.HalfWidth), maxX = CellX(arena.HalfWidth);
        int minZ = CellZ(-arena.HalfDepth), maxZ = CellZ(arena.HalfDepth);

        var surfaces = new List<float>();

        for (int cx = minX; cx <= maxX; cx++)
        for (int cz = minZ; cz <= maxZ; cz++)
        {
            float x = CentreX(cx), z = CentreZ(cz);

            surfaces.Clear();
            support.Clear();
            CollectSurfaces(x, z, surfaces);
            if (surfaces.Count == 0) continue;

            var ids = new List<int>(surfaces.Count);

            for (int s = 0; s < surfaces.Count; s++)
            {
                float y = surfaces[s];
                int id = positions.Count;
                var at = new Vector3(x, y, z);

                nodeSupport.Add(support[s]);

                // Burning ground is passable but expensive, so a route only crosses it when the
                // detour would genuinely cost more than the burn.
                float weight = InHazard(x, z, y) ? 6f : 1f;

                astar.AddPoint(id, at, weight);
                positions.Add(at);
                ids.Add(id);
            }

            columns[Key(cx, cz)] = ids;
        }
    }

    /// <summary>
    /// Every height in this column a pawn could stand on: the floor, plus the top of any block
    /// whose footprint covers it, keeping only those with headroom above.
    /// </summary>
    void CollectSurfaces(float x, float z, List<float> into)
    {
        // The floor exists wherever a slab does. Pits are carved out of the slabs themselves, so
        // a hole in the world is simply the absence of a surface here — no special case needed.
        foreach (var slab in arena.FloorSlabs)
        {
            if (x < slab.Position.X || x > slab.End.X) continue;
            if (z < slab.Position.Y || z > slab.End.Y) continue;

            // −1 marks the ground, which nothing can destroy. The two lists are kept exactly
            // parallel so a node's supporting block is always support[i].
            into.Add(0f);
            support.Add(-1);
            break;
        }

        foreach (int bi in Near(x, z))
        {
            var b = arena.Blocks[bi];
            if (MathF.Abs(x - b.Centre.X) > b.HalfExtents.X) continue;
            if (MathF.Abs(z - b.Centre.Z) > b.HalfExtents.Z) continue;

            float top = b.Centre.Y + b.HalfExtents.Y;

            // The perimeter walls have tops, but nothing should ever route along them.
            if (top >= arena.WallHeight - 0.5f) continue;

            into.Add(top);
            support.Add(bi);
        }

        for (int i = into.Count - 1; i >= 0; i--)
            if (!HasHeadroom(x, z, into[i])) { into.RemoveAt(i); support.RemoveAt(i); }
    }

    /// <summary>
    /// Block index holding each node up, parallel to <see cref="positions"/>, or −1 for the floor.
    ///
    /// This is what lets a destroyed walkway remove itself from the graph. Without it a bot would
    /// happily route across a bridge that was no longer there and walk into the drop, because the
    /// graph is built once from arena data at match start and never rebuilt.
    /// </summary>
    readonly List<int> nodeSupport = new();

    /// <summary>Scratch, parallel to the surfaces list, filled by <see cref="CollectSurfaces"/>.</summary>
    readonly List<int> support = new();

    /// <summary>
    /// Take every node standing on this block in or out of the graph.
    ///
    /// Disabling rather than removing: AStar3D point ids are the graph's identity, and renumbering
    /// them on every explosion would invalidate every cached path in flight.
    /// </summary>
    public void SetBlockActive(int blockIndex, bool active)
    {
        for (int id = 0; id < nodeSupport.Count; id++)
            if (nodeSupport[id] == blockIndex)
                astar.SetPointDisabled(id, !active);
    }

    /// <summary>How many nodes stand on this block. Zero means nothing ever routes over it.</summary>
    public int NodesOn(int blockIndex)
    {
        int n = 0;
        foreach (int s in nodeSupport) if (s == blockIndex) n++;
        return n;
    }

    /// <summary>Any node standing on this block, or −1. Lets a test address the graph directly
    /// instead of guessing a world position that may legitimately carry no node — a walkway with
    /// something directly above it has no headroom and so no node at that spot.</summary>
    public int FirstNodeOn(int blockIndex)
    {
        for (int id = 0; id < nodeSupport.Count; id++) if (nodeSupport[id] == blockIndex) return id;
        return -1;
    }

    public Vector3 NodePosition(int id) => positions[id];

    /// <summary>Whether this node is currently routable. Nodes on a destroyed walkway are not.</summary>
    public bool NodeEnabled(int id) => id >= 0 && id < positions.Count && !astar.IsPointDisabled(id);

    bool HasHeadroom(float x, float z, float y)
    {
        foreach (int bi in Near(x, z))
        {
            var b = arena.Blocks[bi];
            if (MathF.Abs(x - b.Centre.X) > b.HalfExtents.X) continue;
            if (MathF.Abs(z - b.Centre.Z) > b.HalfExtents.Z) continue;

            float low = b.Centre.Y - b.HalfExtents.Y;
            float high = b.Centre.Y + b.HalfExtents.Y;

            // Overlaps the space a standing pawn would occupy, without being the surface itself.
            if (high > y + 0.05f && low < y + Headroom) return false;
        }
        return true;
    }

    bool InHazard(float x, float z, float y)
    {
        foreach (var h in arena.Hazards)
        {
            if (x < h.Area.Position.X || x > h.Area.End.X) continue;
            if (z < h.Area.Position.Y || z > h.Area.End.Y) continue;
            if (y > h.Top) continue;
            return true;
        }
        return false;
    }

    void BuildEdges()
    {
        // Four-connected. Diagonals would let a route clip the corner of a block, because a
        // diagonal step passes through two cells that were never themselves checked.
        Span<int> dx = stackalloc int[] { 1, -1, 0, 0 };
        Span<int> dz = stackalloc int[] { 0, 0, 1, -1 };

        foreach (var pair in columns)
        {
            int cx = (int)(pair.Key >> 32);
            int cz = (int)(uint)pair.Key;

            for (int d = 0; d < 4; d++)
            {
                if (!columns.TryGetValue(Key(cx + dx[d], cz + dz[d]), out var others)) continue;

                foreach (int a in pair.Value)
                foreach (int b in others)
                {
                    float rise = positions[b].Y - positions[a].Y;

                    // Walk up, jump up, or drop. Anything steeper in either direction is not a
                    // link, and the search will route around it.
                    if (rise > JumpUp || rise < -MaxDrop) continue;

                    // Directional: being able to drop off a ledge does not mean you can climb it.
                    astar.ConnectPoints(a, b, bidirectional: false);
                }
            }
        }

        AddLaunchPadLinks();
    }

    /// <summary>
    /// Launch pads are vertical shortcuts, and the grid cannot see that: a pad and the deck it
    /// throws you onto are nowhere near each other in graph terms. Each pad therefore gets direct
    /// links to everything within its reach, which is how the Glasshouse tower becomes routable at all.
    /// </summary>
    void AddLaunchPadLinks()
    {
        foreach (var pad in arena.LaunchPads)
        {
            int from = NearestNode(pad.Centre);
            if (from < 0) continue;

            // How high the pad actually throws you, from the same numbers the pawn uses.
            float apex = pad.Impulse * pad.Impulse / (2f * Pawn.Gravity);

            // Horizontal reach is bounded by how long you are in the air and how fast you run.
            float airtime = 2f * pad.Impulse / Pawn.Gravity;
            float reach = MathF.Min(airtime * 8f, 22f);

            for (int id = 0; id < positions.Count; id++)
            {
                if (id == from) continue;

                var p = positions[id];
                float rise = p.Y - positions[from].Y;
                if (rise <= StepUp || rise > apex - 0.5f) continue;

                var flat = new Vector2(p.X - positions[from].X, p.Z - positions[from].Z);
                if (flat.Length() > reach) continue;

                astar.ConnectPoints(from, id, bidirectional: false);
            }
        }
    }

    // ---- queries ----

    /// <summary>Closest graph node to a world position, or -1 when the graph is empty.</summary>
    public int NearestNode(Vector3 at)
    {
        if (positions.Count == 0) return -1;

        int cx = CellX(at.X), cz = CellZ(at.Z);
        int best = -1;
        float bestScore = float.PositiveInfinity;

        // Searches the immediate neighbourhood first, which is nearly always a hit, and only
        // widens when the pawn is somewhere with no node at all — mid-air, or over a pit.
        for (int radius = 0; radius <= 3 && best < 0; radius++)
        {
            for (int ix = cx - radius; ix <= cx + radius; ix++)
            for (int iz = cz - radius; iz <= cz + radius; iz++)
            {
                if (!columns.TryGetValue(Key(ix, iz), out var ids)) continue;

                foreach (int id in ids)
                {
                    // A node on a walkway that has been blown away is not somewhere to route to
                    // or from. Skipping it here as well as in A* means a bot standing on thin air
                    // snaps to the ground below rather than to the deck that is no longer there.
                    if (astar.IsPointDisabled(id)) continue;

                    var p = positions[id];

                    // Height is weighted far above footprint: standing on a deck must not snap to
                    // the floor directly beneath it.
                    float dy = MathF.Abs(p.Y - at.Y);
                    float score = p.DistanceSquaredTo(at) + dy * dy * 6f;

                    if (score >= bestScore) continue;
                    bestScore = score;
                    best = id;
                }
            }
        }

        return best;
    }

    /// <summary>
    /// Fills <paramref name="into"/> with waypoints between two positions. Returns false when the
    /// two are not connected — across a pit with no bridge, for instance.
    /// </summary>
    public bool TryFindPath(Vector3 from, Vector3 to, List<Vector3> into)
    {
        into.Clear();

        int a = NearestNode(from);
        int b = NearestNode(to);
        if (a < 0 || b < 0 || a == b) return false;

        var points = astar.GetPointPath(a, b);
        if (points.Length == 0) return false;

        foreach (var p in points) into.Add(p);
        return true;
    }

    public bool AreConnected(Vector3 from, Vector3 to)
    {
        int a = NearestNode(from);
        int b = NearestNode(to);
        if (a < 0 || b < 0) return false;
        if (a == b) return true;
        return astar.GetIdPath(a, b).Length > 0;
    }
}
