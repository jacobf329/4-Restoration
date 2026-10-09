using System.Collections.Generic;
using Godot;

namespace Battlefront;

/// <summary>
/// Where a soldier can walk, as a handful of waypoints rather than a navigation mesh.
///
/// Hand-authored, and that is the right call for this map rather than a shortcut. The station is
/// a hub with four spokes: there are about a dozen places worth being and the routes between them
/// are obvious from the floor plan, so a graph you can read in one screen beats a generated mesh
/// you have to debug. It also encodes the one thing the map is about, which no generated mesh
/// would know to care about: the catwalk is an EDGE, a short link across the middle, and the
/// gantry ring is the long way round. A bot choosing between them is making the same decision the
/// player makes.
///
/// Straight-line steering alone would not do. The middle of this map is a hole, and anything that
/// walks toward a target by pointing at it walks into the reactor shaft.
/// </summary>
public sealed class Nav
{
    public readonly List<Vector3> Nodes = new();
    readonly List<List<int>> links = new();

    public Nav(Station station)
    {
        float g = Station.GantryOuter - 3.5f;   // standing room on the gantry ring
        const float Spoke = 46f;                // halfway down a corridor

        // 0-4: the posts.
        foreach (var post in station.Posts) Add(post.Centre);

        int gantryN = Add(new Vector3(0f, 0f, g));
        int gantryS = Add(new Vector3(0f, 0f, -g));
        int gantryE = Add(new Vector3(g, 0f, 0f));
        int gantryW = Add(new Vector3(-g, 0f, 0f));

        int spokeN = Add(new Vector3(0f, 0f, Spoke));
        int spokeS = Add(new Vector3(0f, 0f, -Spoke));
        int spokeE = Add(new Vector3(Spoke, 0f, 0f));
        int spokeW = Add(new Vector3(-Spoke, 0f, 0f));

        int catwalk = Add(Vector3.Zero);

        // On the gantry, NOT on the diagonal inside it.
        //
        // These were at 0.72 of the ring radius, which is 16.9m, and the shaft is 20m: all four
        // corner waypoints were hanging over the drop. Every route that went round the ring
        // walked its soldiers into the reactor, so the only working path across the map was the
        // catwalk, and the east and west wings were unreachable - twenty-two soldiers assigned
        // to control and docking, and not one of them ever arrived. Caught by counting who was
        // SENT somewhere against who was standing there; the screenshots showed none of it.
        int cornerNE = Add(new Vector3(g, 0f, g));
        int cornerSE = Add(new Vector3(g, 0f, -g));
        int cornerSW = Add(new Vector3(-g, 0f, -g));
        int cornerNW = Add(new Vector3(-g, 0f, g));

        // Posts are in Station's order: reactor, hangar, detention, control, docking.
        const int Reactor = 0, Hangar = 1, Detention = 2, Control = 3, Docking = 4;

        Link(Hangar, spokeN); Link(spokeN, gantryN);
        Link(Detention, spokeS); Link(spokeS, gantryS);
        Link(Control, spokeE); Link(spokeE, gantryE);
        Link(Docking, spokeW); Link(spokeW, gantryW);

        // The reactor post sits exactly on the gantry's north node, so it has to BE that
        // junction rather than hang off it.
        //
        // It had a single link to gantryN, and that one missing detail pinned the whole battle
        // in one room. Nearest() returns whichever of two identically-placed nodes it checks
        // first, which is the post; the post's only neighbour was the node it is standing on;
        // so every soldier at the hub was told to walk to the spot they were already on and
        // never went anywhere. The furthest anyone got in two minutes was x=11 on a map that is
        // 230m across.
        // The short way: straight over the drop.
        Link(gantryN, catwalk); Link(catwalk, gantryS);

        // The long way: round the ring.
        Link(gantryN, cornerNE); Link(cornerNE, gantryE);
        Link(gantryE, cornerSE); Link(cornerSE, gantryS);
        Link(gantryS, cornerSW); Link(cornerSW, gantryW);
        Link(gantryW, cornerNW); Link(cornerNW, gantryN);

        // The reactor post sits exactly on the gantry's north node, so it has to BE that
        // junction rather than hang off it.
        //
        // It had a single link to gantryN, and that one missing detail pinned the whole battle
        // in one room. Nearest() returns whichever of two identically-placed nodes it checks
        // first, which is the post; the post's only neighbour was the node it is standing on;
        // so every soldier at the hub was told to walk to the spot they were already standing
        // on, and never went anywhere. The furthest anybody reached in two minutes of fighting
        // was x=11, on a map that is 230 metres across.
        Link(Reactor, catwalk);
        Link(Reactor, spokeN);
        Link(Reactor, cornerNE);
        Link(Reactor, cornerNW);
    }

    int Add(Vector3 at)
    {
        Nodes.Add(at with { Y = 0f });
        links.Add(new List<int>());
        return Nodes.Count - 1;
    }

    void Link(int a, int b)
    {
        links[a].Add(b);
        links[b].Add(a);
    }

    public int Nearest(Vector3 to)
    {
        int best = 0;
        float bestGap = float.MaxValue;

        for (int i = 0; i < Nodes.Count; i++)
        {
            float gap = (Nodes[i] - to with { Y = 0f }).LengthSquared();
            if (gap < bestGap) { bestGap = gap; best = i; }
        }

        return best;
    }

    /// <summary>
    /// The next node to walk to on the way from <paramref name="from"/> to <paramref name="goal"/>,
    /// or -1 when there is no route.
    ///
    /// Breadth-first rather than Dijkstra, because with fourteen nodes the difference between the
    /// shortest path and the fewest-hops path is not worth a priority queue — and fewest hops is
    /// what sends a bot over the catwalk instead of round the ring, which is the behaviour this
    /// map wants to see.
    /// </summary>
    public int Next(int from, int goal)
    {
        if (from == goal) return goal;

        var cameFrom = new int[Nodes.Count];
        for (int i = 0; i < cameFrom.Length; i++) cameFrom[i] = -2;

        var queue = new Queue<int>();
        queue.Enqueue(from);
        cameFrom[from] = -1;

        while (queue.Count > 0)
        {
            int at = queue.Dequeue();
            if (at == goal) break;

            foreach (int to in links[at])
            {
                if (cameFrom[to] != -2) continue;
                cameFrom[to] = at;
                queue.Enqueue(to);
            }
        }

        if (cameFrom[goal] == -2) return -1;

        // Walk the chain back to the node right after `from`.
        int step = goal;
        while (cameFrom[step] != from && cameFrom[step] >= 0) step = cameFrom[step];
        return step;
    }
}
