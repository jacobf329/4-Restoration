using System.Collections.Generic;
using Godot;

namespace Battlefront;

/// <summary>
/// The match. Forty soldiers, five command posts, and a reinforcement count each.
///
/// This is conquest and nothing else, which is the whole reason the project restarted: the rules
/// are small enough to state in a paragraph and everything in the game serves them. Hold more
/// posts than the other side and their reinforcements bleed away. Run them to zero and you win.
/// Dying costs your side one. That is the game.
///
/// The AI is deliberately simple and will stay that way until it has been watched: pick the post
/// worth going to, walk the waypoint graph to it, shoot whatever is in front of you on the way.
/// A soldier who does that convincingly in a crowd of forty reads as an army, and no amount of
/// individually clever behaviour reads as one if the crowd is not there.
/// </summary>
public sealed class Battle
{
    public const int PerSide = 20;
    public const int StartTickets = 250;

    /// <summary>Seconds of sole occupancy before a post changes hands.</summary>
    public const float CaptureTime = 6f;

    /// <summary>How long a soldier stays down before coming back.</summary>
    public const float RespawnDelay = 4f;

    /// <summary>How long a soldier will stand and trade shots before moving on anyway.</summary>
    public const float HoldFor = 2.5f;

    /// <summary>Reinforcements lost per second, per post of advantage.</summary>
    public const float BleedPerPost = 0.9f;

    public readonly Station Station;
    public readonly Nav Nav;
    public readonly List<Trooper> Troopers = new();
    public readonly float[] Tickets = { StartTickets, StartTickets };

    /// <summary>How far through capturing each post the leading team is, and who that is.</summary>
    readonly float[] capture;
    readonly int[] capturer;

    readonly RandomNumberGenerator rng = new();

    /// <summary>Deaths by cause. "It is draining" is not the same fact as "they are fighting".</summary>
    public int Shot, Fell;

    /// <summary>Furthest anyone has got down each spoke. The wings are at +-86.</summary>
    public float MaxEast, MaxWest;

    /// <summary>Per-soldier brain state, indexed alongside <see cref="Troopers"/>.</summary>
    readonly List<Brain> brains = new();

    sealed class Brain
    {
        /// <summary>This soldier's index within their own side. See ChoosePost.</summary>
        public int Slot;

        public int GoalPost = -1;
        public int NextNode = -1;
        public float Rethink;
        public Trooper? Target;
        public float TargetFor;

        /// <summary>How long this soldier has been standing still to shoot. See HoldFor.</summary>
        public float Held;
    }

    public Battle(Station station)
    {
        Station = station;
        Nav = new Nav(station);
        rng.Seed = 31337;

        capture = new float[station.Posts.Count];
        capturer = new int[station.Posts.Count];
        for (int i = 0; i < capturer.Length; i++) capturer[i] = -1;
    }

    public void Add(Trooper t)
    {
        int slot = 0;
        foreach (var other in Troopers) if (other.Team == t.Team) slot++;

        Troopers.Add(t);
        brains.Add(new Brain { Slot = slot });
    }

    /// <summary>How many living soldiers are currently assigned to each post. For --sim.</summary>
    public int AssignedTo(int post)
    {
        int n = 0;
        for (int i = 0; i < Troopers.Count; i++)
            if (Troopers[i].Alive && brains[i].GoalPost == post) n++;
        return n;
    }

    /// <summary>Where the soldiers sent to a post have actually got to. For --sim.</summary>
    public string ProgressTo(int post)
    {
        Vector3 sum = Vector3.Zero;
        int n = 0;

        for (int i = 0; i < Troopers.Count; i++)
        {
            if (!Troopers[i].Alive || brains[i].GoalPost != post) continue;
            sum += Troopers[i].GlobalPosition;
            n++;
        }

        if (n == 0) return "-";

        var at = sum / n;
        int here = Nav.Nearest(at);
        int goal = Nav.Nearest(Station.Posts[post].Centre);

        return $"({at.X:0},{at.Z:0}) node{here}->{Nav.Next(here, goal)} goal{goal}";
    }

    public int Held(int team)
    {
        int n = 0;
        foreach (var post in Station.Posts) if (post.Owner == team) n++;
        return n;
    }

    // ------------------------------------------------------------------ the tick

    /// <summary>
    /// One frame of the whole battle. <paramref name="player"/> is excluded from the AI, since
    /// something else is driving them.
    /// </summary>
    public void Step(float dt, Trooper? player)
    {
        for (int i = 0; i < Troopers.Count; i++)
        {
            var t = Troopers[i];

            if (!t.Alive)
            {
                t.DeadFor += dt;
                if (t.DeadFor >= RespawnDelay) t.Revive(SpawnFor(t.Team));
                continue;
            }

            if (t.ReloadIn > 0f) t.ReloadIn -= dt;

            if (t != player) Think(dt, t, brains[i]);

            MaxEast = Mathf.Max(MaxEast, t.GlobalPosition.X);
            MaxWest = Mathf.Min(MaxWest, t.GlobalPosition.X);

            // The reactor shaft is not fenced, and the AI is not clever enough to be trusted
            // near it. Anything that goes over the edge dies, which is the same rule the player
            // lives under - see Field.
            if (t.GlobalPosition.Y < Station.KillFloor) Kill(t);
        }

        Capturing(dt);
        Bleed(dt);
    }

    void Kill(Trooper t)
    {
        if (t.Alive) Fell++;

        t.Hurt(Trooper.MaxHealth * 2f);
        Tickets[t.Team] = Mathf.Max(0f, Tickets[t.Team] - 1f);
    }

    // ------------------------------------------------------------------ the brain

    void Think(float dt, Trooper t, Brain brain)
    {
        brain.Rethink -= dt;

        if (brain.Rethink <= 0f)
        {
            brain.Rethink = rng.RandfRange(0.6f, 1.4f);
            brain.GoalPost = ChoosePost(t, brain.Slot);
            brain.Target = FindTarget(t);
        }

        // Movement serves the objective; the enemy in front of you decides your aim and whether
        // you stop, not where you are going.
        //
        // The other way round was the first attempt and it produced a battle that never left the
        // hub. A soldier who halts at the first thing they can see halts forever, because in the
        // middle of this map there is always something to see: the front froze where the two
        // sides first met and two of the five posts were never contested in a two-minute match.
        // Soldiers who shoot while they walk push a front around a map. Soldiers who stop to
        // shoot hold a line across the middle of it and nothing else ever happens.
        var order = Advance(t, brain);

        if (brain.Target is { Alive: true } foe && CanSee(t, foe))
        {
            brain.TargetFor += dt;

            var to = foe.Chest - t.Muzzle;
            t.AimYaw = Mathf.Atan2(to.Z, to.X);

            Shoot(t, foe);

            float gap = to.Length();

            // Stand and fight, but only for a few seconds, and then push on regardless.
            //
            // The hold used to have no time limit, and that one missing clause froze the entire
            // battle on the catwalk. Both sides route over the bridge, the bridge is six metres
            // wide, so every soldier on it is permanently within fourteen metres of an enemy and
            // every soldier on it therefore stopped - forever. Twenty-two were under orders to
            // take control and docking and stood in the middle of the map for two and a half
            // minutes instead. A firefight should be something a soldier comes out of.
            if (gap < 14f && brain.Held < HoldFor)
            {
                brain.Held += dt;
                order.Wish = Vector3.Zero;
                order.Crouch = brain.TargetFor > 1.2f;
            }

            order.Sprint = false;
        }
        else
        {
            brain.TargetFor = 0f;
        }

        // The hold counts time spent STANDING STILL, not time spent on one target, and it only
        // refills once the soldier is actually moving again.
        //
        // Keyed to the target first, which did nothing at all: in a scrum the thing you are
        // shooting at dies or breaks line of sight every second or so, every new target reset
        // the timer, and the soldier stood on the catwalk indefinitely anyway. The output of the
        // run before and the run after were identical to the digit, which is the only reason it
        // was obvious the change had missed.
        if (order.Wish.LengthSquared() > 0.01f) brain.Held = Mathf.Max(0f, brain.Held - dt * 0.7f);

        t.Step(dt, order);
    }

    /// <summary>Walk the graph toward the post this soldier has been sent to.</summary>
    Trooper.Order Advance(Trooper t, Brain brain)
    {
        if (brain.GoalPost < 0) return default;

        int here = Nav.Nearest(t.GlobalPosition);
        int goal = Nav.Nearest(Station.Posts[brain.GoalPost].Centre);

        // Re-aimed every frame rather than cached, because the graph is fourteen nodes and the
        // search is free. Caching a route is how a bot keeps walking at a doorway it has already
        // been pushed away from.
        int next = Nav.Next(here, goal);
        if (next < 0) return default;

        // Walk to the waypoint. Do not cut the corner.
        //
        // There was a look-ahead here - within four metres of a node, aim at the one after it -
        // meant to stop soldiers converging on the exact same spot. It was the single thing
        // keeping this battle in one room. The corridor mouths meet the gantry at z=27 and the
        // corridor walls are at x=+-7.8, so a soldier coming down the north spoke was still
        // between those walls when it started aiming at the gantry's east corner, and walked
        // into the wall instead. Every soldier sent east or west did this. Measured: in sixty
        // seconds of fighting, the furthest anybody reached was x=8 on a map whose east and west
        // posts are at x=+-86, and the wall is at 7.8.
        //
        // The converging problem is real, so it is solved by giving each soldier their own
        // offset within the node instead - which spreads them out without aiming any of them at
        // a place the route does not go.
        var waypoint = Nav.Nodes[next] + Spread(brain.Slot, t.Team);

        var wish = (waypoint - t.GlobalPosition) with { Y = 0f };

        return new Trooper.Order
        {
            Wish = wish,
            Sprint = wish.Length() > 14f,
        };
    }

    /// <summary>
    /// Which post this soldier is for, by sharing the side out across everything worth taking.
    ///
    /// Not "the nearest objective", which was the first two attempts and is why this needed
    /// three goes. Every soldier on a side is in roughly the same place, so every soldier picks
    /// the same post and the team moves as one lump; a crowding penalty did not fix it because
    /// distance still dominated. And on this map the hub junction is seven metres from the
    /// reactor post, so "nearest" is the reactor for almost everybody almost always: seventeen
    /// bodies stood on it permanently while control and docking went uncontested for a whole
    /// two-minute match.
    ///
    /// So the side is dealt out instead. Rank what is worth taking, then soldier number N takes
    /// the Nth objective, round robin. Cruder than squad orders, and it produces the thing
    /// conquest is supposed to look like: pressure on several places at once, and a front that
    /// moves when one of them falls.
    /// </summary>
    int ChoosePost(Trooper t, int slot)
    {
        Span<int> worth = stackalloc int[Station.Posts.Count];
        int n = 0;

        for (int i = 0; i < Station.Posts.Count; i++)
        {
            // Ours and uncontested needs nobody sent to it.
            if (Station.Posts[i].Owner == t.Team && capturer[i] != 1 - t.Team) continue;

            worth[n++] = i;
        }

        if (n == 0) return 0;

        for (int a = 0; a < n; a++)
        for (int b = a + 1; b < n; b++)
            if (Value(t, worth[b]) < Value(t, worth[a]))
                (worth[a], worth[b]) = (worth[b], worth[a]);

        return worth[slot % n];
    }

    /// <summary>
    /// A fixed per-soldier offset, so twenty of them walking to one waypoint arrive as a crowd
    /// rather than as a tower. Stable per soldier, so nobody wanders.
    /// </summary>
    static Vector3 Spread(int slot, int team)
    {
        float angle = slot * 2.39996f + team * 1.7f;    // golden angle: no two land together
        // Small. The catwalk is six metres wide and an offset bigger than three put soldiers'
        // waypoints over the drop - falls went from none to thirty-one in a two-minute match.
        float radius = 0.6f + slot % 3 * 0.5f;

        return new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius);
    }

    /// <summary>Lower is more worth walking to.</summary>
    float Value(Trooper t, int post)
    {
        float gap = t.GlobalPosition.DistanceTo(Station.Posts[post].Centre);
        int owner = Station.Posts[post].Owner;

        if (owner == t.Team) return gap * 0.5f;   // ours and under attack: defend it
        if (owner < 0) return gap * 0.7f;         // neutral: costs nobody a life
        return gap;                               // theirs: has to be fought for
    }


    Trooper? FindTarget(Trooper t)
    {
        Trooper? best = null;
        float bestGap = 60f * 60f;

        foreach (var other in Troopers)
        {
            if (!other.Alive || other.Team == t.Team) continue;

            float gap = (other.GlobalPosition - t.GlobalPosition).LengthSquared();
            if (gap > bestGap) continue;
            if (!CanSee(t, other)) continue;

            bestGap = gap;
            best = other;
        }

        return best;
    }

    bool CanSee(Trooper from, Trooper to)
    {
        var space = from.GetWorld3D().DirectSpaceState;

        var hit = space.IntersectRay(new PhysicsRayQueryParameters3D
        {
            From = from.Muzzle,
            To = to.Chest,
            Exclude = new Godot.Collections.Array<Rid> { from.GetRid(), to.GetRid() },
        });

        return hit.Count == 0;
    }

    // ------------------------------------------------------------------ shooting

    public const float RateOfFire = 0.42f;
    public const float Damage = 17f;

    void Shoot(Trooper t, Trooper foe)
    {
        if (t.ReloadIn > 0f) return;

        t.ReloadIn = RateOfFire * rng.RandfRange(0.85f, 1.25f);

        // Hit chance falls off with range rather than simulating a bullet. A tracer and a
        // probability read identically at forty metres, and this is a blockout.
        float gap = t.GlobalPosition.DistanceTo(foe.GlobalPosition);
        float chance = Mathf.Clamp(1.15f - gap / 55f, 0.18f, 0.8f);

        if (rng.Randf() < chance) Wound(foe, Damage, t.Team);
    }

    void Wound(Trooper foe, float damage, int by)
    {
        bool wasAlive = foe.Alive;
        foe.Hurt(damage);

        if (wasAlive && !foe.Alive)
        {
            Shot++;
            Tickets[foe.Team] = Mathf.Max(0f, Tickets[foe.Team] - 1f);
        }
    }

    // ------------------------------------------------------------------ posts

    void Capturing(float dt)
    {
        for (int i = 0; i < Station.Posts.Count; i++)
        {
            var post = Station.Posts[i];

            int blue = 0, orange = 0;

            foreach (var t in Troopers)
            {
                if (!t.Alive) continue;
                if (t.GlobalPosition.DistanceTo(post.Centre) > Post.Radius) continue;

                if (t.Team == 0) blue++; else orange++;
            }

            // Contested by both sides: nothing moves. A post that flips while people are still
            // fighting over it is a post nobody defends.
            int holder = blue > 0 && orange == 0 ? 0
                       : orange > 0 && blue == 0 ? 1
                       : -1;

            if (holder < 0 || holder == post.Owner)
            {
                capture[i] = Mathf.Max(0f, capture[i] - dt);
                if (capture[i] <= 0f) capturer[i] = -1;
                continue;
            }

            if (capturer[i] != holder) { capturer[i] = holder; capture[i] = 0f; }

            // More bodies take it faster, but with a ceiling: a whole team standing on one post
            // should not flip it instantly.
            capture[i] += dt * Mathf.Min(1f + (holder == 0 ? blue : orange) * 0.25f, 2.5f);

            if (capture[i] >= CaptureTime)
            {
                post.Owner = holder;
                capture[i] = 0f;
                capturer[i] = -1;
            }
        }
    }

    void Bleed(float dt)
    {
        int blue = Held(0), orange = Held(1);
        if (blue == orange) return;

        int losing = blue > orange ? 1 : 0;
        Tickets[losing] = Mathf.Max(0f, Tickets[losing] - Mathf.Abs(blue - orange) * BleedPerPost * dt);
    }

    // ------------------------------------------------------------------ spawning

    /// <summary>
    /// Somewhere this side owns, preferring whichever post is furthest from the enemy.
    ///
    /// The point of conquest is that capturing a post changes where your side can arrive, so a
    /// side that has lost everything falls back to its home corner and has to fight back in.
    /// </summary>
    public Vector3 SpawnFor(int team)
    {
        Vector3 best = Station.HomeSpawn[team];
        float bestGap = -1f;

        foreach (var post in Station.Posts)
        {
            if (post.Owner != team) continue;

            float nearest = float.MaxValue;

            foreach (var t in Troopers)
            {
                if (!t.Alive || t.Team == team) continue;
                nearest = Mathf.Min(nearest, t.GlobalPosition.DistanceTo(post.Centre));
            }

            if (nearest > bestGap) { bestGap = nearest; best = post.Centre; }
        }

        // Scattered a little, so twenty respawns do not stack on one point.
        return best + new Vector3(rng.RandfRange(-4f, 4f), 1f, rng.RandfRange(-4f, 4f));
    }
}
