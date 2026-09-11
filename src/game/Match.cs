using System;
using System.Collections.Generic;
using Godot;

namespace HitboxClone;

/// <summary>One pawn's intent for a tick, already resolved into world XZ space.</summary>
public struct PawnInput
{
    public Vector2 Move;

    /// <summary>
    /// Untransformed stick, straight off the device. <see cref="Move"/> is rotated into world space
    /// against the camera, which is right for a walking pawn and meaningless to a vehicle: a hull
    /// steers and throttles in its own frame, not the camera one.
    /// </summary>
    public Vector2 RawMove;

    /// <summary>Untransformed look stick, for vehicle pitch, which is a rate rather than an angle.</summary>
    public Vector2 RawLook;
    public Vector2 Aim;

    /// <summary>Vertical aim in radians, positive looking up.</summary>
    public float Pitch;

    public bool Fire;
    public bool Dash;
    public bool Special;

    /// <summary>A melee swing. Always available, whatever weapon is in hand.</summary>
    public bool Melee;

    /// <summary>The class ability, on its own button and its own cooldown.</summary>
    public bool ClassAbility;

    public bool Ads;
    public bool Jump;

    /// <summary>Jump held down, which is what sustains jetpack thrust.</summary>
    public bool JumpHeld;
    public bool Sprint;
    public bool Crouch;
    public bool CrouchPressed;

    /// <summary>Enter or leave a vehicle. A press, not a hold.</summary>
    public bool Use;

    /// <summary>Interact held down, which is what takes a weapon off the floor.</summary>
    public bool UseHeld;

    /// <summary>Switch to the other weapon slot.</summary>
    public bool SwapWeapon;
}

/// <summary>
/// The running match: arena, pawns, projectiles and score.
///
/// It is a Node3D driving itself from <c>_PhysicsProcess</c> because both <c>MoveAndSlide</c> and
/// space-state raycasts are only valid inside the physics step. Presentation lives entirely in
/// <see cref="MatchScreen"/>, so this class runs identically with no cameras, meshes or lights —
/// which is what lets the headless harness play full bot matches.
/// </summary>
public partial class Match : Node3D
{
    /// <summary>
    /// A bullet in flight. Simulated by sweeping a ray from the previous position to the next
    /// rather than using a physics body: it cannot tunnel through a wall at any speed, and it
    /// steps deterministically, which the headless test depends on.
    /// </summary>
    sealed class Shot
    {
        public Vector3 Pos;
        public Vector3 Vel;
        public float RangeLeft;
        public Pawn Owner = null!;
        public float Damage;

        /// <summary>Non-zero when the round explodes where it lands.</summary>
        public float BlastDamage;
        public float BlastRadius;

        /// <summary>
        /// What this round's blast is worth against structure, as a multiple of its blast damage.
        ///
        /// Carried on the round rather than decided at the point of detonation, because by the
        /// time a shell goes off the only thing left of where it came from is its owner — and the
        /// owner of a tank shell is a pawn, indistinguishable from one holding a rocket launcher.
        /// </summary>
        public float StructureScale = 1f;

        /// <summary>True when the round plants a gate where it lands rather than doing damage.</summary>
        public bool PlantsPortal;

        /// <summary>True when the round anchors and hauls its owner to where it stuck.</summary>
        public bool Grapples;

        /// <summary>True when this round mends whoever it lands on, if they are on your side.</summary>
        public bool Heals;

        /// <summary>Seconds until it goes off on its own, or zero for a contact round.</summary>
        public float Fuse;

        /// <summary>True when the world turns it around instead of stopping it.</summary>
        public bool Bounces;

        /// <summary>Gravity on this round, as a fraction of a pawn's.</summary>
        public float Weight;

        /// <summary>Radians per second this round turns toward what it is chasing. Zero flies straight.</summary>
        public float SeekTurnRate;

        /// <summary>How far this round looks for a target, and the cone it will accept one in.</summary>
        public float SeekRange;
        public float SeekCone;

        /// <summary>Metres at which anything but the shooter sets this round off in flight.</summary>
        public float TriggerRadius;

        /// <summary>True when this round sticks in whoever it hits and counts toward a supercombine.</summary>
        public bool Needles;

        /// <summary>
        /// True when the weapon that fired this has a scope, which is what a full headshot needs.
        ///
        /// Carried on the round rather than read from the owner's hands when it lands, because by
        /// then they may have swapped: a round in flight was fired by the gun that fired it, and a
        /// railgun shot should not stop being a railgun shot because somebody pulled out a sword.
        /// </summary>
        public bool Scoped;

        /// <summary>Stops a round ping-ponging between two gates on consecutive frames.</summary>
        public float PortalLock;

        public MeshInstance3D? Mesh;
    }

    public Arena Arena = new();

    /// <summary>
    /// Walkable graph over the arena, built once per match. Bots route through it instead of
    /// walking straight at whatever they are chasing.
    /// </summary>
    public NavGraph Nav { get; private set; } = null!;
    public readonly List<Pawn> Pawns = new();
    readonly List<Shot> shots = new();

    public MatchSettings Settings = new();
    public bool Visuals = true;
    public bool Finished { get; private set; }
    public Pawn? Winner { get; private set; }
    public float Elapsed { get; private set; }

    /// <summary>Supplies input for a human pawn by index. Bots never consult it.</summary>
    public Func<int, PawnInput>? InputSource;

    readonly List<BotBrain> brains = new();

    /// <summary>Live projectile count, watched by the headless test for unbounded growth.</summary>
    public int ShotCount => shots.Count;

    /// <summary>Trigger pulls so far. Engagement metrics, so the harness can tell a quiet match
    /// from a broken one without depending on kills — which are a difficulty outcome, not an
    /// invariant.</summary>
    public int ShotsFired { get; private set; }

    public float DamageDealt { get; private set; }

    /// <summary>
    /// Metres walked by all pawns. A bot that is thinking hard and going nowhere looks identical
    /// to a bot that is working, right up until you watch one — so the harness measures it.
    /// </summary>
    public float DistanceWalked { get; private set; }

    readonly Dictionary<Pawn, Vector3> lastSeenAt = new();

    /// <summary>Headshots landed so far, so the harness can prove the head region is reachable.</summary>
    public int Headshots { get; private set; }

    /// <summary>Supercombines set off so far. The needler is worth nothing without them.</summary>
    public int Supercombines { get; private set; }

    /// <summary>Rewards precision without making body shots pointless.</summary>
    /// <summary>
    /// What a hit above the head line is worth.
    ///
    /// Three times what it was. At 2.2 a headshot was a bonus you noticed on the damage numbers
    /// and nowhere else; the shot that deserves the most from the player should be worth the most
    /// to them. At 6.6 the scoped rifles do what a sniper rifle is supposed to do — a railgun
    /// headshot is 693 and a Longshot headshot is 290, against a health pool that tops out well
    /// under either, so a clean shot to the head is a kill and not a negotiation.
    ///
    /// Worth knowing what else this touches, because it is not only the snipers: the minigun's
    /// 6.5 a round becomes 43, so a burst held on someone's head is lethal in about a fifth of a
    /// second. That is the intended shape of the change — every weapon rewards the head — but it
    /// is the reason the number is a constant rather than being folded into the sniper damage.
    /// </summary>
    public const float HeadshotMultiplier = 6.6f;

    /// <summary>
    /// What a headshot is worth without a scope.
    ///
    /// Half. The 6.6 above was set for the shot a scoped rifle exists to take — a still target, a
    /// held breath, one round — and applying it to everything made every other weapon reward the
    /// same thing by accident. A minigun burst held on somebody's head was killing in a fifth of a
    /// second for no decision anybody made.
    ///
    /// Scoped is the test rather than a per-weapon list, because it is the property that actually
    /// distinguishes the shot: a scope is a commitment. You give up your field of view, most of
    /// your pace and any chance of reacting to what is beside you, and the head is what you are
    /// buying with that. Nothing else on the map pays a price for aiming high.
    /// </summary>
    public const float UnscopedHeadshotMultiplier = 3.3f;

    /// <summary>What this shot's headshot is worth, given what fired it.</summary>
    static float HeadshotScale(Shot s)
        => s.Scoped ? HeadshotMultiplier : UnscopedHeadshotMultiplier;

    public const float HeadshotBannerTime = 1.15f;

    public void Build(Node parent, MatchSettings settings, IReadOnlyList<LobbySlot> roster, bool visuals)
    {
        Settings = settings;
        Visuals = visuals;
        Name = "Match";
        parent.AddChild(this);

        Arena = new Arena(ChooseArena(settings));
        Arena.Build(this, visuals);
        Nav = new NavGraph(Arena);
        BuildLevelMachinery();
        BuildPickups();
        BuildVehicles();
        BuildBreakables();

        if (Settings.Mode == GameMode.KingOfTheHill) SetupZone();
        if (Settings.Mode == GameMode.CaptureTheFlag) SetupFlags();
        if (Settings.Mode == GameMode.Dominion) SetupDominion();

        int humansSoFar = 0;
        int botsSoFar = 0;

        for (int i = 0; i < roster.Count; i++)
        {
            var slot = roster[i];
            var pawn = new Pawn { Name = $"Pawn{i}" };
            AddChild(pawn);

            // In a team mode everyone wears their team's colour, not their own. Four different
            // player colours made friend and foe indistinguishable at a glance, which is the one
            // thing a team mode has to get right.
            Color tint = settings.Def.Teams ? Pal.Teams[TeamOf(i)] : Pal.FighterColour(i);

            // -1 for a bot: no viewport, no cull layer of its own. This is what lets a roster of
            // twelve run on a renderer that only has room for four sets of first-person layers.
            int viewIndex = slot.IsBot ? -1 : humansSoFar++;

            // Faction comes from the lobby now rather than from the seat. It decides which special
            // this pawn gets, so a player picking one and being handed another would be the single
            // most misleading thing the lobby could do.
            pawn.Faction = slot.Faction;

            // Set before Setup, which is what builds the rig the wash is applied to.
            pawn.TeamWash = settings.Def.Teams ? tint : null;

            // In Portal mode the gun is the mode. Set before Setup so the first view model built is
            // already the right one.
            pawn.PortalOnly = settings.Mode == GameMode.Portal;

            pawn.Setup(slot.Class, i, slot.IsBot, tint, visuals, viewIndex);

            // Numbered within their own kind rather than by roster position, so a match with two
            // humans and ten bots reads "P1, P2, CPU 1 … CPU 10" instead of starting at CPU 3.
            pawn.Name2 = slot.IsBot ? $"CPU {++botsSoFar}" : $"P{humansSoFar}";
            pawn.GlobalPosition = Arena.SpawnPoints[i % Arena.SpawnPoints.Count];
            pawn.Facing = MathU.Angle(new Vector2(-pawn.GlobalPosition.X, -pawn.GlobalPosition.Z));
            Pawns.Add(pawn);

            brains.Add(slot.IsBot ? new BotBrain(settings.BotSkill) : null!);
        }
    }

    public override void _PhysicsProcess(double delta)
    {
        // A press made while paused or after the final whistle is dropped, not banked. Without
        // this the latch survives the pause and is spent on the frame play resumes, which is a
        // vehicle boarded by a button pressed a minute ago.
        if (Finished || Paused) { Devices.ConsumeGameplayEdges(); return; }

        float dt = (float)delta;
        Elapsed += dt;

        AgeFeedback(dt);
        StepMovingPlatforms(dt);

        // Between Elimination rounds the world keeps running — bodies settle, platforms move —
        // but nobody scores and nobody respawns until the next round begins.
        if (IntermissionLeft > 0f)
        {
            IntermissionLeft -= dt;
            if (IntermissionLeft <= 0f) StartNextRound();
        }

        for (int i = 0; i < Pawns.Count; i++)
        {
            var pawn = Pawns[i];

            if (!pawn.Alive)
            {
                // Dead pawns still tick. They used to be skipped entirely, which meant the
                // topple animation never ran at all — bodies stood upright until they vanished
                // on respawn, and players kept shooting corpses because nothing said they were
                // dead.
                // Killed in the seat. Without this the hull stays permanently occupied by a
                // corpse: nobody else can board it, and the vehicle step keeps asking a dead
                // brain how to drive.
                if (pawn.InVehicle) pawn.Riding!.Eject();

                pawn.Tick(dt, default, this);

                // A corpse stops at the edges of the world rather than leaving it for the whole
                // respawn timer. It used to keep accelerating downward the entire time, which the
                // arena-bounds invariant caught as soon as the outer districts gave it somewhere
                // deep to fall — and which pointed the death camera into the void.
                //
                // The ceiling is the same rule and was missing, which took a rare invariant
                // failure to find: a corpse blasted upward rose at a constant twelve metres a
                // second, out through the top of the world, for a hundred and forty consecutive
                // samples. Nothing caught it. CheckOutOfBounds kills anything outside the play
                // boundary and would have, except that it asks whether the pawn is alive first —
                // correctly, since a corpse cannot be killed again — so the only thing standing
                // between a body and the void was this clamp, which only looked down.
                var body = pawn.GlobalPosition;

                if (body.Y < Arena.KillPlaneY || body.Y > Arena.CeilingY)
                {
                    // Held a metre inside the ceiling rather than exactly on it, mirroring the
                    // metre of slack InPlay already allows below the kill plane. Parked precisely
                    // on the boundary, a clamped body sits at a height the play boundary itself
                    // calls out of play - so the clamp would work and the invariant would still
                    // complain about the result.
                    pawn.GlobalPosition = body with
                    {
                        Y = Mathf.Clamp(body.Y, Arena.KillPlaneY, Arena.CeilingY - 1f),
                    };
                    pawn.Velocity = Vector3.Zero;
                }

                pawn.RespawnIn -= dt;

                // A bot has nobody to show a spawn screen to, so it decides here — once, the frame
                // its timer is about to run out, using the points it has actually banked.
                if (pawn.IsBot && pawn.RespawnIn <= 0f) ChooseBotSpawn(pawn);

                if (pawn.RespawnIn <= 0f && ModeAllowsRespawn && WantsToSpawn(pawn))
                {
                    ApplySpawnChoice(pawn);
                    pawn.Respawn(DominionSpawn(pawn) ?? Arena.BestSpawn(Pawns, pawn));
                    if (Visuals) Sfx.PlayAt(Sound.Respawn, pawn.GlobalPosition);
                }
                continue;
            }

            // A rider's intent comes from the vehicle step instead, which has the hull's state to
            // reason about — its heading, its speed, what is in front of it. Only the dismount is
            // read here, and only for a human: a bot decides to bail as part of driving, where it
            // can see how much of the hull is left.
            if (pawn.InVehicle)
            {
                if (!pawn.IsBot && (InputSource?.Invoke(i) ?? default).Use) ToggleVehicle(pawn);
                pawn.Tick(dt, default, this);
                continue;
            }

            PawnInput input = pawn.IsBot
                ? brains[i].Think(dt, pawn, this)
                : InputSource?.Invoke(i) ?? default;

            // A press boards a vehicle; a hold takes a weapon. The two never fight because a press
            // only counts when a hull is actually in reach.
            if (input.Use && !pawn.Slipping && NearestBoardable(pawn) != null) ToggleVehicle(pawn);
            if (input.SwapWeapon) pawn.SwapWeapon();

            pawn.TrackPickupHold(dt, input.UseHeld);

            pawn.Tick(dt, input, this);

            // Boarding takes effect immediately, so the on-foot interactions below are skipped for
            // the tick the pawn climbed in.
            if (pawn.InVehicle) continue;

            ApplyLaunchPads(pawn);
            ResolveDashImpacts(pawn);
            ApplyHazards(pawn, dt);
            CheckFall(pawn);
            CheckOutOfBounds(pawn);
            CheckCrushed(pawn, dt);
        }

        TrackMovement();

        StepShots(dt);
        StepGrenades(dt);
        StepPickups(dt);
        StepVehicles(dt);
        StepBreakables(dt);
        StepBlooms(dt);
        StepButter(dt);
        StepDecoys(dt);
        StepPortals(dt);
        StepGrappleLines();
        if (Settings.Mode == GameMode.KingOfTheHill) TickZone(dt);
        if (Settings.Mode == GameMode.CaptureTheFlag) TickFlags(dt);
        if (Settings.Mode == GameMode.Dominion) TickDominion(dt);
        if (Settings.IsPuzzle) TickCheckpoints(dt);
        StepBarriers(dt);
        if (Settings.Mode == GameMode.Juggernaut) TickJuggernaut(dt);
        CheckWin();

        // Every press this step was offered has now been acted on or declined, so the latches come
        // down. This is the only place they do: input is raised on the render clock and read on
        // the physics clock, and holding a press until the simulation has actually seen it is what
        // makes one tap mean one action at any frame rate. See <see cref="InputDevice.UseLatched"/>.
        Devices.ConsumeGameplayEdges();
    }

    // ---- King of the Hill ----

    public const float ZoneRadius = 6f;

    /// <summary>Vertical tolerance for standing in the zone.</summary>
    public const float ZoneHeight = 2.5f;

    const float ZoneColumnHeight = 7f;

    /// <summary>Seconds a zone stays put once someone starts holding it.</summary>
    const float ZoneHoldToMove = 14f;

    public Vector3 ZoneCentre { get; private set; }

    /// <summary>The pawn currently scoring, or null if the zone is empty or contested.</summary>
    public Pawn? ZoneHolder { get; private set; }

    /// <summary>True when two or more sides stand in the zone, so nobody scores.</summary>
    public bool ZoneContested { get; private set; }

    int zoneSpot;
    float zoneHeld;
    readonly Dictionary<Pawn, float> scoreAccum = new();
    MeshInstance3D? zoneMesh;

    /// <summary>Where bots should head when the mode has an objective. Null otherwise.</summary>
    void SetupZone()
    {
        zoneSpot = 0;
        ZoneCentre = Arena.ZoneSpots[0];

        if (!Visuals) return;

        // A column rather than a floor disc. At eye height a flat marker on the ground is almost
        // invisible past the nearest crate, and the whole mode depends on being able to find the
        // zone from across the arena.
        zoneMesh = new MeshInstance3D
        {
            Mesh = new CylinderMesh
            {
                TopRadius = ZoneRadius,
                BottomRadius = ZoneRadius,
                Height = ZoneColumnHeight,
            },
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = new Color(0.29f, 0.78f, 0.98f, 0.16f),
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                EmissionEnabled = true,
                Emission = new Color(0.29f, 0.78f, 0.98f),
                EmissionEnergyMultiplier = 2.0f,
            },
        };
        AddChild(zoneMesh);
        MoveZoneMesh();
    }

    void TickZone(float dt)
    {
        Pawn? sole = null;
        bool contested = false;

        foreach (var p in Pawns)
        {
            if (!p.Alive) continue;

            var d = p.GlobalPosition - ZoneCentre;
            if (new Vector2(d.X, d.Z).Length() > ZoneRadius) continue;

            // Height matters as well as footprint: a zone on top of the centre platform must not
            // be capturable by standing on the floor beside it.
            if (MathF.Abs(d.Y) > ZoneHeight) continue;

            if (sole == null) sole = p;
            else if (!Settings.Def.Teams || !SameTeam(sole, p)) contested = true;
        }

        ZoneContested = contested;
        ZoneHolder = contested ? null : sole;

        if (ZoneHolder == null) return;

        // Score accrues per second rather than per tick, so the limit means the same thing at any
        // physics rate.
        scoreAccum.TryGetValue(ZoneHolder, out float acc);
        acc += dt;
        while (acc >= 1f) { acc -= 1f; ZoneHolder.Score++; ZoneHolder.AwardPoints(BattlePoints.ZoneSecond); }
        scoreAccum[ZoneHolder] = acc;

        // The zone relocates once it has been held long enough, so one player cannot camp a single
        // favourable spot for the whole match.
        zoneHeld += dt;
        if (zoneHeld < ZoneHoldToMove) return;

        zoneHeld = 0f;
        zoneSpot = (zoneSpot + 1) % Arena.ZoneSpots.Count;
        ZoneCentre = Arena.ZoneSpots[zoneSpot];
        MoveZoneMesh();
        if (Visuals) Sfx.Play(Sound.ZoneCapture, -4f);
    }


    // ---- the spawn screen ----
    //
    // Dying used to be a two and a half second wait with nothing to do in it. Now it is the only
    // moment in the match where you choose what kind of fight you want to have next, which is what
    // turns a death from a punishment into a decision — and it is the whole reason battle points
    // are worth earning.
    //
    // The timer still runs. It is a floor rather than a countdown to something automatic: you may
    // not come back before it expires, and after that you come back when you have chosen.

    /// <summary>Seconds a human is given to choose before the game picks for them.</summary>
    public const float SpawnChoiceGrace = 12f;

    /// <summary>
    /// Whether this pawn is ready to come back.
    ///
    /// A bot always is — its choice is made the instant its timer runs out. A human has confirmed,
    /// or has sat on the screen long enough that the match stops waiting: leaving a player dead
    /// indefinitely because they walked away is worse for everyone else than spawning them as
    /// whatever the cursor was on.
    /// </summary>
    bool WantsToSpawn(Pawn pawn)
        => pawn.IsBot || pawn.SpawnConfirmed || pawn.DeadFor >= SpawnChoiceGrace;

    /// <summary>
    /// Charge for the choice and put it on.
    ///
    /// Re-checks affordability rather than trusting the spawn screen. Points can be spent between
    /// choosing and spawning in exactly one way — they cannot, today — but the screen is UI and the
    /// bank is simulation, and a purchase validated only by the thing displaying it is a purchase
    /// waiting to be free.
    /// </summary>
    void ApplySpawnChoice(Pawn pawn)
    {
        // A hero costs what a hero costs.
        //
        // This is the only place points come off, and it did not used to be. The spawn screen set a
        // flag for a hero and left the price for somebody else to take, the bot path took it
        // itself, and this method charged whatever was in NextSpawn — which for a hero is the
        // Trooper, at zero. So a human could buy a hero for nothing, every single death, for the
        // whole match. Two code paths that both half-charge is how that happens, so now there is
        // one.
        bool hero = pawn.HeroBought;
        var want = pawn.NextSpawn;
        int cost = hero ? Reinforcements.HeroCost : want.Cost;

        // Re-checked here rather than trusted from the screen. The bank is simulation and the
        // spawn screen is UI, and a purchase validated only by the thing displaying it is a
        // purchase waiting to be free.
        if (cost > pawn.BattlePoints)
        {
            hero = false;
            want = Reinforcements.Trooper;
            cost = 0;
        }

        pawn.BattlePoints -= cost;
        pawn.Wearing = want;
        pawn.BecomeClass(want.Class);
        pawn.WearBody(Visuals);
        pawn.SpawnConfirmed = false;
        pawn.HeroBought = false;

        // A hero is not a class swap. It is the crown, with everything that already hangs off it —
        // the health scaling, the power, the saber, the marker over the head — so it goes on
        // through the same path the Juggernaut mode uses rather than through a second one.
        if (hero) pawn.TakeCrown(pawn.Faction.Juggernaut);

        // Losing a bought hero on death is the point of buying one. In Juggernaut the crown is the
        // mode and passes by its own rules, so this must not touch it.
        else if (Settings.Mode != GameMode.Juggernaut) pawn.LoseCrown();
    }

    /// <summary>
    /// Whether a hero may be bought at all right now.
    ///
    /// Never in Juggernaut, where the crown is the mode and buying one would make the whole scoring
    /// rule meaningless. And only one per side at a time, so a match cannot turn into four titans
    /// standing in a circle hitting each other with swords — which is a different game, and not
    /// this one.
    /// </summary>
    public bool HeroAvailableTo(Pawn pawn)
    {
        if (Settings.Mode == GameMode.Juggernaut) return false;

        foreach (var p in Pawns)
        {
            if (p == pawn || !p.IsJuggernaut) continue;
            if (!Settings.Def.Teams || SameTeam(p, pawn)) return false;
        }

        return true;
    }

    /// <summary>
    /// What a bot comes back as.
    ///
    /// Deliberately not "the most expensive thing it can afford". A roster where every bot spawns
    /// as an Orchard the moment it clears eleven hundred points is a roster with no Troopers in it,
    /// and the basic classes are most of what the arena should look like. So it buys when it can
    /// and then only sometimes, which produces a match where a reinforcement arriving is an event.
    /// </summary>
    void ChooseBotSpawn(Pawn pawn)
    {
        if (pawn.SpawnConfirmed) return;

        pawn.SpawnConfirmed = true;
        pawn.HeroBought = false;
        pawn.SpawnPost = ChooseBotPost(pawn);

        var options = Reinforcements.For(pawn.Faction);

        // The hero first, and rarely. A bot sitting on the points forever would mean players never
        // meet one.
        if (pawn.BattlePoints >= Reinforcements.HeroCost && HeroAvailableTo(pawn)
            && GD.Randf() < 0.5f)
        {
            pawn.HeroBought = true;
            pawn.NextSpawn = Reinforcements.Trooper;
            return;
        }

        ReinforcementDef best = Reinforcements.Trooper;

        foreach (var r in options)
        {
            if (r.Cost == 0 || r.Cost > pawn.BattlePoints) continue;
            if (r.Cost <= best.Cost) continue;
            best = r;
        }

        // Two in three when it can afford something. The rest of the time it comes back as one of
        // the basics, chosen at random rather than always the same one.
        if (best.Cost > 0 && GD.Randf() < 0.66f) { pawn.NextSpawn = best; return; }

        pawn.NextSpawn = Reinforcements.Basic[(int)(GD.Randi() % (uint)Reinforcements.Basic.Length)];
    }

    /// <summary>
    /// Which layout to play, keeping puzzle chambers and combat arenas apart.
    ///
    /// A random pick used to range over every entry in Arena.Names, which now includes two chambers
    /// with no floor between the islands. Dropping a deathmatch onto one would be an instant loss
    /// for everybody, and dropping a puzzle onto the Reliquary would be a puzzle with no puzzle in
    /// it — so the roll is taken over the half that matches the mode.
    /// </summary>
    /// <summary>The map picker, for the harness. See <see cref="ChooseArena"/>.</summary>
    public static int ChooseArenaForTest(MatchSettings settings) => ChooseArena(settings);

    static int ChooseArena(MatchSettings settings)
    {
        // Story mode says where it is going and is not negotiated with. Checked first so the
        // versus rules below - which exist to keep a match off a story set - cannot refuse it.
        if (settings.IsStoryMission && Arena.IsStory(settings.StoryLayout))
            return settings.StoryLayout;

        int combat = Arena.CombatLayouts;

        if (settings.ArenaIndex >= 0)
        {
            // An explicit choice is honoured unless it is the wrong kind entirely, which can
            // happen if the mode was changed after the map was picked — or if the index came from
            // somewhere that has no business choosing a map at all.
            //
            // Asked as "is it the right kind", not "is it not the other kind". Those were the same
            // question while there were two kinds of layout: `IsPuzzle(index) == wantPuzzle` let
            // any non-puzzle through, and the moment a third kind existed that included Fairview,
            // so a versus match explicitly pointed at the town got the town — a deathmatch in the
            // house John Smith grew up in, with no weapons on the floor and two spawn points.
            bool ok = settings.IsPuzzle
                ? Arena.IsPuzzle(settings.ArenaIndex)
                : Arena.IsArena(settings.ArenaIndex);

            if (ok) return settings.ArenaIndex;
        }

        // Counted from where the puzzles actually start, not up from the end of the arenas. Those
        // were the same index until a story set was put between them, and the difference is a
        // Portal match rolling the town.
        return settings.IsPuzzle
            ? Arena.FirstPuzzleLayout + (int)(GD.Randi() % (uint)Arena.PuzzleLayouts)
            : (int)(GD.Randi() % (uint)combat);
    }

    // ---- Portal ----

    /// <summary>Checkpoints already reached, by index into the arena's list.</summary>
    readonly HashSet<int> checkpointsDone = new();

    /// <summary>How close you have to be to claim a checkpoint.</summary>
    public const float CheckpointReach = 3.2f;

    /// <summary>How many checkpoints this map has, and how many are done.</summary>
    public int CheckpointCount => Arena.Checkpoints.Count;
    public int CheckpointsReached => checkpointsDone.Count;

    /// <summary>Whether a given checkpoint has been claimed. For the HUD.</summary>
    public bool CheckpointDone(int i) => checkpointsDone.Contains(i);

    /// <summary>
    /// Claim any checkpoint somebody is standing on.
    ///
    /// Shared rather than per-player, and permanent once claimed. The chambers are co-operative and
    /// the Orrery cannot be finished alone, so scoring them individually would be scoring a thing
    /// nobody can do — two people splitting the work is the intended solution, not a loophole.
    /// </summary>
    void TickCheckpoints(float dt)
    {
        for (int i = 0; i < Arena.Checkpoints.Count; i++)
        {
            if (checkpointsDone.Contains(i)) continue;

            foreach (var p in Pawns)
            {
                if (!p.Alive) continue;
                if (p.GlobalPosition.DistanceTo(Arena.Checkpoints[i]) > CheckpointReach) continue;

                checkpointsDone.Add(i);
                p.AwardPoints(BattlePoints.PostCapture);

                if (Visuals)
                {
                    Impact.DeathRing(this, Arena.Checkpoints[i], Pal.Ready);
                    Sfx.PlayAt(Sound.ZoneCapture, Arena.Checkpoints[i]);
                }

                break;
            }
        }
    }

    // ---- Dominion ----
    //
    // Battlefront's conquest, which is a different game from every other mode here and worth being
    // precise about why. Deathmatch asks who shoots better. Capture the Flag asks who can make one
    // long run. Dominion asks where a whole map's worth of people should be standing, right now,
    // and it answers with a number that is visibly draining while you decide.
    //
    // The reinforcement pool is what makes that true. Deaths cost you, and so does simply holding
    // fewer posts than the other side — so a team can be winning every firefight and still lose,
    // and a team pinned in a corner can see exactly how long they have left. Nothing else in this
    // game gives you a reason to leave a fight you are winning.

    /// <summary>One command post: a place worth standing, which costs reinforcements to ignore.</summary>
    public sealed class Post
    {
        public Vector3 Centre;
        public string Name = "";

        /// <summary>Team holding it, or -1 while it is nobody's.</summary>
        public int Owner = -1;

        /// <summary>Team currently taking it, or -1.</summary>
        public int Contender = -1;

        /// <summary>How far through the capture, 0 to 1.</summary>
        public float Progress;

        /// <summary>True while both sides stand in it, which stops the clock.</summary>
        public bool Contested;
    }

    public readonly List<Post> Posts = new();

    /// <summary>
    /// Tickets each side has left. Float because bleed is continuous.
    ///
    /// Called tickets rather than reinforcements, which is what conquest usually calls them, because
    /// <see cref="Reinforcements"/> is now the roster of characters you spend battle points on and
    /// two meanings of one word in one file is how a reader ends up misunderstanding both. Tickets
    /// is the other standard term for the same thing and it is unambiguous here.
    /// </summary>
    public readonly float[] Tickets = new float[2];

    /// <summary>How far a post reaches, and how much height it covers.</summary>
    public const float PostRadius = 9f;
    public const float PostHeight = 3.5f;

    /// <summary>Seconds one fighter needs to take a post. A crowd is faster, up to a point.</summary>
    public const float PostCaptureTime = 7f;

    /// <summary>Most fighters who can usefully stack on a capture. Past this it is wasted bodies.</summary>
    public const int PostCaptureCrowd = 3;

    /// <summary>
    /// Reinforcements a second the losing side sheds, per post of disadvantage.
    ///
    /// This is the whole economy. At a hundred reinforcements, being two posts down costs a
    /// reinforcement a second — a hundred-second clock if nobody ever dies, which is long enough
    /// to mount a comeback and short enough that ignoring the map loses the match.
    /// </summary>
    public const float BleedPerPost = 0.5f;

    /// <summary>Posts that have changed hands this match. Reported by the harness.</summary>
    public int PostCaptures;

    readonly List<MeshInstance3D> postMarks = new();

    static readonly string[] PostNames =
        { "ALPHA", "BRAVO", "CHARLIE", "DELTA", "ECHO", "FOXTROT", "GOLF", "HOTEL", "INDIA" };

    /// <summary>
    /// How many command posts a Dominion match runs on.
    ///
    /// Five: two that start owned and three worth fighting over. Enough that the map has a shape
    /// and few enough that both sides keep arriving at the same place.
    /// </summary>
    public const int PostTarget = 5;

    /// <summary>
    /// Lay the posts out as a chain across the middle of the map, west to east.
    ///
    /// This started as ordinary farthest-point sampling — the same thing the med kits and the ground
    /// spawns use — and that was precisely the wrong tool. Farthest-point maximises separation, so
    /// it puts the five posts in the four corners and the middle, which is the layout least likely
    /// to make two teams meet: each side spreads across a hundred and forty metres chasing
    /// different errands. The harness kept catching it as a Dominion round with no damage in it.
    ///
    /// Conquest wants the opposite shape. A chain of posts along one axis is a *front*: both sides
    /// push along it, the middle one is contested constantly, and you always know which way the
    /// enemy is. That is what every Battlefront map does and it is why they work.
    ///
    /// So: prefer the spots nearest the arena's centre line, then take the five most spread out
    /// along the long axis alone. Central in Z, strung out in X.
    /// </summary>
    static List<Vector3> FrontLine(IReadOnlyList<Vector3> from, int want)
    {
        var kept = new List<Vector3>();
        if (from.Count == 0) return kept;

        // Most central first. Ties by X so the ordering is stable rather than depending on the
        // order the layout happened to add its zone spots in.
        var central = new List<Vector3>(from);
        central.Sort((l, r) =>
        {
            int byZ = MathF.Abs(l.Z).CompareTo(MathF.Abs(r.Z));
            return byZ != 0 ? byZ : l.X.CompareTo(r.X);
        });

        // Keep a generous shortlist rather than exactly what is wanted: the spread step below needs
        // room to choose, and the flanking spots are still worth having if the central band is thin.
        int shortlist = Math.Min(central.Count, Math.Max(want + 3, want * 2));
        central.RemoveRange(shortlist, central.Count - shortlist);

        // Now spread along X only, so the chain reaches both ends of the map.
        kept.Add(central[0]);

        while (kept.Count < want && kept.Count < central.Count)
        {
            Vector3 best = central[0];
            float bestGap = -1f;

            foreach (var c in central)
            {
                float nearest = float.MaxValue;
                foreach (var taken in kept) nearest = MathF.Min(nearest, MathF.Abs(taken.X - c.X));
                if (nearest > bestGap) { bestGap = nearest; best = c; }
            }

            if (bestGap <= 0.01f) break;
            kept.Add(best);
        }

        kept.Sort((l, r) => l.X.CompareTo(r.X));
        return kept;
    }

    void SetupDominion()
    {
        Posts.Clear();
        postMarks.Clear();

        // Five, not all nine.
        //
        // Every zone spot in the arena was being made a post, which on a two-hundred-and-seventy-
        // metre map is nine places for twelve fighters to be. The harness caught what that does:
        // a full Dominion match with bots walking three hundred and twenty metres each and firing
        // *four shots* between them, because both sides scattered to nine different errands and
        // never met. A conquest map is not a set of objectives, it is a front line, and a front
        // line needs few enough posts that two sides arrive at the same one.
        //
        // Farthest-point sampling picks the five that are most spread out, so the ones kept are a
        // line across the map rather than a cluster, and then they are named across it — "they have
        // taken Bravo" carries a rough direction with it.
        var spots = FrontLine(Arena.ZoneSpots, PostTarget);

        for (int i = 0; i < spots.Count; i++)
            Posts.Add(new Post { Centre = spots[i], Name = PostNames[i % PostNames.Length] });

        // The outermost post at each end starts owned, so neither side opens the match with
        // nowhere to spawn. Everything between them is up for grabs, which is where the match is.
        if (Posts.Count >= 2)
        {
            Posts[0].Owner = 0;
            Posts[^1].Owner = 1;
        }

        Tickets[0] = Tickets[1] = Settings.ScoreLimit;

        if (!Visuals) return;

        foreach (var post in Posts)
        {
            var mesh = new MeshInstance3D
            {
                Mesh = new CylinderMesh { TopRadius = PostRadius, BottomRadius = PostRadius, Height = 9f },
                MaterialOverride = new StandardMaterial3D
                {
                    AlbedoColor = new Color(1f, 1f, 1f, 0.13f),
                    Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                    ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                    CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                    EmissionEnabled = true,
                    EmissionEnergyMultiplier = 1.7f,
                },
                Position = post.Centre + Vector3.Up * 4f,
            };

            AddChild(mesh);
            postMarks.Add(mesh);
        }

        PaintPosts();
    }

    /// <summary>A post's colour: its owner's, or neutral white while it is nobody's.</summary>
    public static Color PostTint(int owner)
        => owner < 0 ? new Color(0.82f, 0.84f, 0.88f) : Pal.Teams[owner & 1];

    void PaintPosts()
    {
        for (int i = 0; i < postMarks.Count && i < Posts.Count; i++)
        {
            if (postMarks[i].MaterialOverride is not StandardMaterial3D mat) continue;

            Color tint = PostTint(Posts[i].Owner);
            mat.AlbedoColor = tint with { A = 0.13f };
            mat.Emission = tint;
        }
    }

    void TickDominion(float dt)
    {
        int[] held = { 0, 0 };

        foreach (var post in Posts)
        {
            int[] standing = { 0, 0 };

            foreach (var p in Pawns)
            {
                if (!p.Alive) continue;

                var d = p.GlobalPosition - post.Centre;
                if (new Vector2(d.X, d.Z).Length() > PostRadius) continue;

                // Height matters as much as footprint. A post on a gantry must not be capturable
                // from underneath it, or half the posts on a map with an upper storey are taken by
                // people who never went up.
                if (MathF.Abs(d.Y) > PostHeight) continue;

                standing[TeamOf(p.Slot)]++;
            }

            post.Contested = standing[0] > 0 && standing[1] > 0;

            if (post.Owner >= 0) held[post.Owner]++;

            // Frozen rather than reset. A capture that unwound the instant one defender arrived
            // would make contesting strictly better than capturing, and both sides would stand in
            // the circle refusing to leave.
            if (post.Contested) continue;

            int taking = standing[0] > 0 ? 0 : standing[1] > 0 ? 1 : -1;

            if (taking < 0 || taking == post.Owner)
            {
                // Empty, or only the owner standing in it: whatever the other side built up decays.
                post.Progress = MathF.Max(0f, post.Progress - dt / PostCaptureTime);
                if (post.Progress <= 0f) post.Contender = -1;
                continue;
            }

            if (post.Contender != taking) { post.Contender = taking; post.Progress = 0f; }

            // A crowd takes it faster, but only up to three — otherwise the winning move in every
            // Dominion match is for the whole team to travel as one lump.
            int crowd = Math.Min(standing[taking], PostCaptureCrowd);
            post.Progress += dt * crowd / PostCaptureTime;

            if (post.Progress < 1f) continue;

            if (post.Owner >= 0) held[post.Owner]--;

            post.Owner = taking;
            post.Progress = 0f;
            post.Contender = -1;
            held[taking]++;
            PostCaptures++;

            // Everyone who was standing in it when it turned, not just whoever got there first.
            // Capturing is the one thing in this game that is strictly better done together, and
            // paying only one of them out would teach exactly the wrong lesson.
            foreach (var p in Pawns)
            {
                if (!p.Alive || TeamOf(p.Slot) != taking) continue;

                var d = p.GlobalPosition - post.Centre;
                if (new Vector2(d.X, d.Z).Length() > PostRadius) continue;
                if (MathF.Abs(d.Y) > PostHeight) continue;

                p.AwardPoints(BattlePoints.PostCapture);
            }

            if (Visuals)
            {
                PaintPosts();
                Sfx.PlayAt(Sound.ZoneCapture, post.Centre);
                Impact.DeathRing(this, post.Centre, PostTint(taking));
            }
        }

        // The bleed. Whoever holds fewer posts pays for it continuously, which is what turns a map
        // full of circles into a clock.
        int lead = held[0] - held[1];
        if (lead == 0) return;

        int losing = lead > 0 ? 1 : 0;
        Tickets[losing] =
            MathF.Max(0f, Tickets[losing] - MathF.Abs(lead) * BleedPerPost * dt);
    }

    /// <summary>
    /// How close an enemy has to be before a bot standing on a post stops leaving it.
    ///
    /// Comfortably beyond the post itself, so a defender commits before the attacker is inside the
    /// circle rather than after — the whole value of holding ground is being there first.
    /// </summary>
    public const float PostDefendRange = 55f;

    /// <summary>Whether anyone on the other side is within <paramref name="range"/> of a point.</summary>
    bool EnemyNear(Pawn who, Vector3 at, float range)
    {
        int team = TeamOf(who.Slot);

        foreach (var p in Pawns)
        {
            if (!p.Alive || TeamOf(p.Slot) == team) continue;
            if (p.GlobalPosition.DistanceTo(at) <= range) return true;
        }

        return false;
    }

    /// <summary>How many posts a team holds right now.</summary>
    public int PostsHeld(int team)
    {
        int n = 0;
        foreach (var post in Posts) if (post.Owner == team) n++;
        return n;
    }

    /// <summary>
    /// A death costs your side a reinforcement, in the one mode that counts them.
    ///
    /// Every death, including your own splash and every fall — this is a supply of bodies, not a
    /// scoreline, and the supply does not care how you spent it.
    /// </summary>
    void SpendReinforcement(Pawn who)
    {
        if (Settings.Mode != GameMode.Dominion) return;

        int team = TeamOf(who.Slot);
        Tickets[team] = MathF.Max(0f, Tickets[team] - 1f);
    }

    /// <summary>
    /// Where a fighter comes back in Dominion: a post their side holds, furthest from the enemy.
    ///
    /// Spawning on your own front line is what makes conquest feel like a war rather than a series
    /// of long walks — take a post and it becomes the place your side arrives. Furthest from the
    /// enemy rather than nearest to the fight, because arriving inside someone's crosshair is not
    /// a spawn. With no posts left it returns null and the ordinary corner spawns take over, so
    /// losing the map is a bleed rather than a lockout.
    /// </summary>
    Vector3? DominionSpawn(Pawn who)
    {
        int team = TeamOf(who.Slot);

        // Where they asked to come back, if it is still theirs.
        //
        // This is the half of conquest that was missing. Capturing a post changes where your side
        // *can* arrive, and until the player got to choose, that meant nothing to them — the game
        // picked the safest post and the capture they had just fought for was invisible. Choosing
        // is the entire reason the posts are worth taking.
        //
        // Re-checked rather than trusted, because a post can change hands between the choice and
        // the spawn — which is not an edge case in a mode about posts changing hands. Losing it
        // falls through to the automatic pick rather than refusing to spawn them.
        if (who.SpawnPost >= 0 && who.SpawnPost < Posts.Count)
        {
            var want = Posts[who.SpawnPost];
            if (want.Owner == team) return want.Centre + Vector3.Up * 0.6f;
        }

        Vector3? best = null;
        float bestGap = -1f;

        foreach (var post in Posts)
        {
            if (post.Owner != team) continue;

            float nearest = float.MaxValue;

            foreach (var p in Pawns)
            {
                if (!p.Alive || TeamOf(p.Slot) == team) continue;
                nearest = MathF.Min(nearest, p.GlobalPosition.DistanceTo(post.Centre));
            }

            if (nearest <= bestGap) continue;

            bestGap = nearest;
            best = post.Centre + Vector3.Up * 0.6f;
        }

        return best;
    }

    /// <summary>
    /// Which post a bot comes back at.
    ///
    /// The one nearest the fighting rather than the one furthest from it, which is the opposite of
    /// what the automatic picker does. That difference is deliberate and it is what the player will
    /// feel: the automatic pick is a safety net for somebody who did not choose, and a side that
    /// always takes the safety net never turns up where the match is. A bot that spawns onto the
    /// contested end is a bot defending its front line.
    ///
    /// Returns -1 in every other mode and whenever the side holds nothing, which hands it back to
    /// the automatic picker.
    /// </summary>
    int ChooseBotPost(Pawn pawn)
    {
        if (Settings.Mode != GameMode.Dominion) return -1;

        int team = TeamOf(pawn.Slot);
        int best = -1;
        float bestGap = float.MaxValue;

        for (int i = 0; i < Posts.Count; i++)
        {
            if (Posts[i].Owner != team) continue;

            float nearest = float.MaxValue;

            foreach (var p in Pawns)
            {
                if (!p.Alive || TeamOf(p.Slot) == team) continue;
                nearest = MathF.Min(nearest, p.GlobalPosition.DistanceTo(Posts[i].Centre));
            }

            // Nobody in sight anywhere: any post will do, so take the first one held rather than
            // leaving it to a comparison against infinity.
            if (nearest >= float.MaxValue) { if (best < 0) best = i; continue; }

            if (nearest >= bestGap) continue;

            bestGap = nearest;
            best = i;
        }

        return best;
    }

    /// <summary>The post a bot should be walking towards, or null once the map is settled.</summary>
    Vector3? DominionObjective(Pawn p)
    {
        int team = TeamOf(p.Slot);

        // Hold the post you are standing in while anyone is near it.
        //
        // Without this nobody ever defends: a bot captures a post, the post stops being an errand
        // the moment it turns, and the bot immediately sets off for the next one. Both sides did
        // that simultaneously and simply exchanged territory all match without meeting — measured
        // at nine shots and *zero damage* across a full twelve-bot round, against three hundred and
        // forty in a deathmatch on the same map. A conquest mode where nobody garrisons anything is
        // two teams doing laps.
        //
        // Returning null hands the bot back to ordinary combat behaviour, which is what holding
        // ground actually looks like: stand near the thing, and fight whoever comes.
        foreach (var post in Posts)
        {
            if (post.Owner != team) continue;

            var d = p.GlobalPosition - post.Centre;
            if (new Vector2(d.X, d.Z).Length() > PostRadius) continue;
            if (MathF.Abs(d.Y) > PostHeight) continue;

            if (EnemyNear(p, post.Centre, PostDefendRange)) return null;
            break;
        }

        Post? best = null;
        float bestCost = float.MaxValue;

        foreach (var post in Posts)
        {
            if (post.Owner == team && post.Contender < 0 && !post.Contested) continue;

            // Distance, discounted for posts that are half taken or actively being lost. Without
            // the discount a bot walks past a post its own side is three seconds from losing
            // because something neutral happens to be marginally closer.
            float cost = post.Centre.DistanceTo(p.GlobalPosition);
            if (post.Owner < 0) cost *= 0.8f;
            if (post.Owner == team) cost *= 0.55f;      // being taken from us: go and stand on it
            if (post.Progress > 0.3f) cost *= 0.7f;

            if (cost >= bestCost) continue;

            bestCost = cost;
            best = post;
        }

        return best?.Centre;
    }

    // ---- juggernaut ----

    /// <summary>Who is wearing the crown, or null before first blood.</summary>
    public Pawn? Juggernaut { get; private set; }

    /// <summary>Seconds left on the banner announcing a new juggernaut.</summary>
    public float CrownBanner { get; private set; }

    public const float CrownBannerTime = 3.2f;

    /// <summary>Times the crown has changed hands. Proves the mode's central loop actually runs.</summary>
    public int CrownChanges { get; private set; }

    /// <summary>How much extra damage Achilles takes in the heel.</summary>
    public const float HeelMultiplier = 4f;

    /// <summary>Fraction of the body height that counts as the heel. The lowest fifth of him.</summary>
    public const float HeelFraction = 0.2f;

    /// <summary>Health a second Noah claws back, as a fraction of his pool.</summary>
    const float NoahRegenPerSecond = 0.055f;

    /// <summary>How far Noah's wake drags at people, and how much.</summary>
    const float NoahWakeRadius = 9f;
    const float NoahWakeSlow = 0.55f;

    /// <summary>
    /// Hand the crown to someone.
    ///
    /// Each faction fields its own figure, which is what makes the crown changing hands a change of
    /// game rather than a change of health bar — a tank with a weak spot becomes an information
    /// dump becomes an attrition wall becomes a glass cannon on a clock.
    /// </summary>
    void CrownPawn(Pawn next)
    {
        if (Juggernaut == next) return;

        Juggernaut?.LoseCrown();

        Juggernaut = next;
        next.TakeCrown(next.Faction.Juggernaut);

        CrownChanges++;
        CrownBanner = CrownBannerTime;

        if (Visuals)
        {
            Impact.DeathRing(this, next.GlobalPosition, next.Faction.Tint);
            Sfx.Play(Sound.ZoneCapture, -1f);
        }
    }

    /// <summary>
    /// The crown has to go somewhere when its wearer dies with nobody to blame — a fall, a crush,
    /// the boundary, or Scheherazade simply running out of nights.
    ///
    /// It goes to whoever is nearest, which is both readable and a reason to be close to a
    /// juggernaut you cannot quite finish.
    /// </summary>
    Pawn? NearestTo(Pawn who)
    {
        Pawn? best = null;
        float bestDist = float.MaxValue;

        foreach (var p in Pawns)
        {
            if (p == who || !p.Alive) continue;

            float d = p.GlobalPosition.DistanceTo(who.GlobalPosition);
            if (d >= bestDist) continue;

            bestDist = d;
            best = p;
        }

        return best;
    }

    // Test hooks. The crown changes hands through kills, and a headless harness cannot reliably
    // arrange a specific pawn killing a specific other one at a specific moment.
    public void AwardKillForTest(Pawn killer, Pawn victim)
    {
        victim.Health = 0f;
        AwardKill(killer, victim);
    }

    public void TickJuggernautForTest(float dt) => TickJuggernaut(dt);
    public void TickDominionForTest(float dt) => TickDominion(dt);
    public void TickCheckpointsForTest(float dt) => TickCheckpoints(dt);
    public void CheckWinForTest() => CheckWin();

    /// <summary>Hand a post over outright, for the harness. Skips the capture entirely.</summary>
    public void ForcePostOwnerForTest(Post post, int owner)
    {
        post.Owner = owner;
        post.Progress = 0f;
        post.Contender = -1;
        if (Visuals) PaintPosts();
    }

    /// <summary>Set a side's reinforcement pool directly, so the harness can pose the endgame.</summary>
    public void SetTicketsForTest(int team, float value) => Tickets[team & 1] = value;
    public void ForceCrownForTest(Pawn p) => CrownPawn(p);

    /// <summary>Wrath's reach and bite. Deliberately larger than any weapon in the game.</summary>
    public const float WrathRadius = 20f;
    public const float WrathDamage = 135f;

    /// <summary>
    /// Damage a second the Fire pours down its beam.
    ///
    /// Above every class weapon in the game, deliberately — a titan's stolen fire that loses to a
    /// shotgun is not stolen fire. He pays for it by hovering seven metres up in the open, as the
    /// single most visible thing on the map, for four seconds.
    ///
    /// The number moves when the guns do. It was 140 against a Tactician who topped out near 113;
    /// buffing the shotgun to a one-shot took her to 174 and quietly left the beam second-best,
    /// which the suite caught. There is a check that compares this against every class weapon, so
    /// the next gun change will catch it again.
    /// </summary>
    public const float FireDamagePerSecond = 215f;
    public const float FireRange = 70f;

    /// <summary>
    /// What a deflected round carries when it goes back the other way.
    ///
    /// More than it arrived with. A returned shot that did normal damage would be a nuisance
    /// rather than a consequence, and the point of a block is that shooting a juggernaut in the
    /// face while they are holding a blade up should be the last thing you do.
    /// </summary>
    public const float DeflectDamageScale = 1.6f;

    /// <summary>
    /// Minimum reach given back to a deflected round.
    ///
    /// A shotgun pellet has almost no range left by the time it reaches the blade, and a deflection
    /// that expired two metres later would be invisible — the block would look like it simply ate
    /// the shot. This is what makes the return trip actually happen.
    /// </summary>
    public const float DeflectRange = 45f;

    /// <summary>Rounds turned around by a raised saber this match. Reported by the harness.</summary>
    public int Deflections;

    /// <summary>What the Ark and the Thousand leave of an incoming shot.</summary>
    public const float ArkResist = 0.12f;
    public const float ThousandResist = 0.35f;

    /// <summary>How many of herself she becomes.</summary>
    public const float ThousandDecoys = 8;

    /// <summary>
    /// Fire a juggernaut's power.
    ///
    /// Each is meant to be plainly stronger than anything a fighter can do. That is the whole point
    /// of the crown: before this the juggernaut was a health bar with a passive attached, and a
    /// health bar does not change how you play — you notice the number and then carry on doing
    /// exactly what you were doing.
    /// </summary>
    public void UseCrownPower(Pawn user)
    {
        if (user.Crown is not { } crown) return;

        CrownPowersUsed++;

        switch (crown.Kind)
        {
            case JuggernautKind.Achilles:
            {
                // The anger of Achilles, as a radius. Instant, enormous, and it throws survivors
                // rather than merely hurting them — being near him has to be the mistake.
                Blast(user, user.GlobalPosition, WrathDamage, WrathRadius, hurtSelf: false);

                foreach (var p in Pawns)
                {
                    if (p == user || !p.Alive) continue;

                    Vector3 away = p.GlobalPosition - user.GlobalPosition;
                    float d = away.Length();
                    if (d > WrathRadius) continue;

                    away = d < 0.01f ? Vector3.Up : away / d;
                    float force = 1f - d / WrathRadius;

                    p.ApplyKnockback(away * (34f * force) + Vector3.Up * (16f * force));
                }

                if (Visuals)
                {
                    Impact.Death(this, user.GlobalPosition, user.Faction.Tint);
                    Impact.DeathRing(this, user.GlobalPosition, user.Faction.Tint);
                    Sfx.PlayAt(Sound.Death, user.GlobalPosition, 2f, 0.55f);
                }
                break;
            }

            case JuggernautKind.Noah:
                user.DamageResist = ArkResist;
                if (Visuals) Sfx.PlayAt(Sound.Respawn, user.GlobalPosition, 0f, 0.6f);
                break;

            case JuggernautKind.Scheherazade:
            {
                user.DamageResist = ThousandResist;

                // A crowd of her, thrown outward in every direction. Killing one is worth nothing,
                // and at a glance there is no telling which is the woman and which is the story.
                for (int i = 0; i < ThousandDecoys; i++)
                    LeaveDecoyAlong(user, i * MathF.Tau / ThousandDecoys);

                if (Visuals) Sfx.PlayAt(Sound.Respawn, user.GlobalPosition, 0f, 1.3f);
                break;
            }

            case JuggernautKind.Prometheus:
                // The rise and the beam are both handled per tick; this only starts it.
                if (Visuals) Sfx.PlayAt(Sound.Dash, user.GlobalPosition, 2f, 0.5f);
                break;
        }
    }

    /// <summary>Crown powers fired. Proves they are reachable rather than merely defined.</summary>
    public int CrownPowersUsed { get; private set; }

    /// <summary>
    /// The Fire, per tick: a beam down the aim doing damage over time to the first thing it meets.
    ///
    /// A sweep rather than a projectile, because a beam that has to travel is not a beam — and
    /// because a titan hovering seven metres up pouring fire onto one spot should hit what it is
    /// pointed at, immediately, for as long as it is pointed there.
    /// </summary>
    void StepFireBeam(Pawn user, float dt)
    {
        Vector3 from = user.Eye;
        Vector3 to = from + user.AimDir * FireRange;

        using var query = PhysicsRayQueryParameters3D.Create(from, to);
        query.Exclude = new Godot.Collections.Array<Rid> { user.GetRid() };

        var hit = GetWorld3D().DirectSpaceState.IntersectRay(query);
        Vector3 end = hit.Count > 0 ? hit["position"].AsVector3() : to;

        if (hit.Count > 0 && hit["collider"].As<GodotObject>() is Pawn target && target.Alive)
        {
            float before = target.Health;
            bool killed = target.TakeDamage(FireDamagePerSecond * dt);
            DamageDealt += before - target.Health;

            if (target.Health < before)
            {
                target.DamageFlash = Pawn.DamageFlashTime;
                target.LastAttacker = user.GlobalPosition;
                user.HitConfirm = Pawn.HitConfirmTime;
            }

            if (killed) AwardKill(user, target);
        }

        if (!Visuals) return;

        beamMesh ??= BuildBeam();
        beamMesh.Visible = true;

        float len = from.DistanceTo(end);
        beamMesh.GlobalPosition = from + (end - from) * 0.5f;
        beamMesh.LookAt(end, MathF.Abs((end - from).Normalized().Y) > 0.98f ? Vector3.Right : Vector3.Up);
        beamMesh.Scale = new Vector3(1f, 1f, MathF.Max(len, 0.1f));
    }

    MeshInstance3D? beamMesh;

    MeshInstance3D BuildBeam()
    {
        var m = new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(0.85f, 0.85f, 1f) },
            MaterialOverride = Graphics.Hot(new Color(1f, 0.72f, 0.28f), 7f),
            Visible = false,
        };
        AddChild(m);
        return m;
    }

    void TickJuggernaut(float dt)
    {
        if (CrownBanner > 0f) CrownBanner -= dt;

        if (Juggernaut is not { } king)
        {
            // Nobody has taken it yet: it goes to first blood, handled in AwardKill.
            return;
        }

        if (!king.Alive)
        {
            // Died to the world rather than to a person. Somebody still has to wear it.
            if (NearestTo(king) is { } heir) CrownPawn(heir);
            return;
        }

        // The power, while it runs. Resistance is re-applied every tick and cleared the moment it
        // lapses, so nothing can leave a juggernaut permanently armoured by a dropped frame.
        if (king.CrownPowerActive)
        {
            switch (king.Crown!.Kind)
            {
                case JuggernautKind.Noah: king.DamageResist = ArkResist; break;
                case JuggernautKind.Scheherazade: king.DamageResist = ThousandResist; break;
                case JuggernautKind.Prometheus: StepFireBeam(king, dt); break;
            }
        }
        else
        {
            king.DamageResist = 1f;
            if (beamMesh != null) beamMesh.Visible = false;
        }

        switch (king.Crown!.Kind)
        {
            case JuggernautKind.Noah:
            {
                king.Heal(king.MaxHealth * NoahRegenPerSecond * dt);

                // The wake. Everyone else near him is dragged at, which is what turns "burst him
                // down" from advice into the only option.
                foreach (var p in Pawns)
                {
                    if (p == king || !p.Alive) continue;
                    if (p.GlobalPosition.DistanceTo(king.GlobalPosition) > NoahWakeRadius) continue;

                    p.SlowFactor = MathF.Min(p.SlowFactor, NoahWakeSlow);
                }
                break;
            }

            case JuggernautKind.Prometheus:
            {
                // The fire was stolen for humanity, not for him. While he reigns *nobody* hides:
                // every fighter is outlined to every other, himself included.
                foreach (var p in Pawns)
                    if (p.Alive) p.RevealedFor = MathF.Max(p.RevealedFor, 0.4f);
                break;
            }

            case JuggernautKind.Scheherazade:
            {
                // The clock. She is not killed by it so much as ended by it — running out passes
                // the crown to whoever was closest, which is exactly what a story stopping does.
                king.SpendNight(dt);

                if (king.NightsLeft <= 0f)
                {
                    if (Visuals) Impact.Death(this, king.GlobalPosition + Vector3.Up, king.Faction.Tint);
                    if (NearestTo(king) is { } heir) CrownPawn(heir);
                }
                break;
            }
        }
    }

    /// <summary>
    /// Damage multiplier for a shot landing on a juggernaut at a given height up its body.
    ///
    /// Achilles only. The heel inverts the usual instinct — every other target in this game rewards
    /// aiming high — and it is the one thing that makes a four-and-a-half-times health pool a fight
    /// rather than an arithmetic problem.
    /// </summary>
    public float JuggernautHitScale(Pawn target, float heightUpBody)
    {
        if (target.Crown?.Kind != JuggernautKind.Achilles) return 1f;
        return heightUpBody <= target.CurrentHeight * HeelFraction ? HeelMultiplier : 1f;
    }

    // ---- capture the flag ----

    /// <summary>One team's flag: where it lives, where it is, and who has it.</summary>
    public sealed class Flag
    {
        /// <summary>The team that defends this flag. The other team scores by taking it.</summary>
        public int Team;

        public Vector3 Home;
        public Vector3 At;

        /// <summary>Who is carrying it, or null when it is on the ground.</summary>
        public Pawn? Carrier;

        /// <summary>Seconds it has been lying loose. It goes home on its own after a while.</summary>
        public float Loose;

        public bool AtHome => Carrier == null && At.DistanceTo(Home) < 0.5f;
        public bool Dropped => Carrier == null && !AtHome;

        public Node3D? Node;
    }

    readonly List<Flag> flags = new();

    /// <summary>The two flags, in team order. Empty outside capture the flag.</summary>
    public IReadOnlyList<Flag> Flags => flags;

    /// <summary>How close you have to be to pick a flag up, take it back, or capture with it.</summary>
    public const float FlagReach = 2.6f;

    /// <summary>Seconds a dropped flag lies loose before returning itself.</summary>
    public const float FlagReturnTime = 22f;

    /// <summary>Captures made. Exists so the harness can prove the mode is playable end to end.</summary>
    public int Captures { get; private set; }

    /// <summary>
    /// Times an enemy flag has been picked up. A better measure of "is the mode working" than
    /// captures are: a capture is a 122-metre round trip through the other team, and whether one
    /// lands inside a test window says more about bot pace than about the rules.
    /// </summary>
    public int FlagTouches { get; private set; }

    /// <summary>The flag this pawn is carrying, or null.</summary>
    public Flag? FlagCarriedBy(Pawn p)
    {
        foreach (var f in flags) if (f.Carrier == p) return f;
        return null;
    }

    public Flag? FlagOf(int team)
    {
        foreach (var f in flags) if (f.Team == team) return f;
        return null;
    }

    void SetupFlags()
    {
        flags.Clear();
        if (Arena.FlagBases.Count < 2) return;

        for (int team = 0; team < 2; team++)
        {
            var flag = new Flag
            {
                Team = team,
                Home = Arena.FlagBases[team],
                At = Arena.FlagBases[team],
            };

            if (Visuals)
            {
                var node = new Node3D();
                AddChild(node);

                Color tint = Pal.Teams[team];

                // A pole and a banner. Tall and lit, because the whole mode is about knowing where
                // the flag is from the other side of a two hundred and seventy metre arena — and a
                // marker at ankle height is invisible past the first crate.
                node.AddChild(new MeshInstance3D
                {
                    Mesh = new BoxMesh { Size = new Vector3(0.14f, 3.4f, 0.14f) },
                    MaterialOverride = Graphics.Hot(tint.Lightened(0.4f), 1.4f),
                    Position = new Vector3(0f, 1.7f, 0f),
                });

                node.AddChild(new MeshInstance3D
                {
                    Mesh = new BoxMesh { Size = new Vector3(0.1f, 1.0f, 1.5f) },
                    MaterialOverride = Graphics.Hot(tint, 3.2f),
                    Position = new Vector3(0f, 2.7f, 0.75f),
                });

                // A ring on the ground marking the base, so a carrier can see where to run to.
                var basePad = new MeshInstance3D
                {
                    Mesh = new CylinderMesh { TopRadius = FlagReach, BottomRadius = FlagReach, Height = 0.12f },
                    MaterialOverride = Graphics.Hot(tint, 1.1f),
                };
                AddChild(basePad);
                basePad.GlobalPosition = flag.Home + Vector3.Up * 0.06f;

                flag.Node = node;
                node.GlobalPosition = flag.At;
            }

            flags.Add(flag);
        }
    }

    void TickFlags(float dt)
    {
        foreach (var flag in flags)
        {
            // A carrier who dies, or drives off in a tank, drops it where they stood.
            if (flag.Carrier is { } holder && (!holder.Alive || holder.InVehicle))
            {
                flag.At = holder.GlobalPosition;
                flag.Carrier = null;
                flag.Loose = 0f;
                if (Visuals) Sfx.PlayAt(Sound.MenuBack, flag.At, -2f, 0.8f);
            }

            if (flag.Carrier is { } carrier)
            {
                // Carried above the head, so from behind you can see who has it and from in front
                // you know what you are shooting at.
                flag.At = carrier.GlobalPosition;
                flag.Loose = 0f;
            }
            else if (flag.Dropped)
            {
                flag.Loose += dt;
                if (flag.Loose >= FlagReturnTime) SendFlagHome(flag);
            }

            if (flag.Node != null)
                flag.Node.GlobalPosition = flag.At + (flag.Carrier != null ? Vector3.Up * 1.1f : Vector3.Zero);
        }

        foreach (var pawn in Pawns)
        {
            if (!pawn.Alive || pawn.InVehicle) continue;

            int team = TeamOf(pawn.Slot);

            foreach (var flag in flags)
            {
                if (flag.Carrier != null) continue;
                if (pawn.GlobalPosition.DistanceTo(flag.At) > FlagReach) continue;

                if (flag.Team == team)
                {
                    // Your own flag. Touching it where it lies sends it straight home — which is
                    // the defensive play, and the reason a stalemate can be broken by someone who
                    // never touches the enemy base.
                    if (!flag.Dropped) continue;

                    SendFlagHome(flag);
                    if (Visuals) Sfx.PlayAt(Sound.ZoneCapture, flag.Home, -3f, 1.2f);
                }
                else
                {
                    flag.Carrier = pawn;
                    FlagTouches++;
                    if (Visuals) Sfx.PlayAt(Sound.Respawn, pawn.GlobalPosition, -2f, 1.1f);
                }

                break;
            }

            // Capturing: your own base, carrying theirs, and your own flag standing at home.
            //
            // That last condition is what stops capture the flag being a race in which neither
            // side ever defends. Without it both teams simply run past each other.
            if (FlagCarriedBy(pawn) is not { } prize) continue;
            if (FlagOf(team) is not { } own) continue;

            if (pawn.GlobalPosition.DistanceTo(own.Home) > FlagReach) continue;
            if (!own.AtHome) continue;

            prize.Carrier = null;
            SendFlagHome(prize);

            pawn.Score++;
            pawn.AwardPoints(BattlePoints.FlagCapture);
            Captures++;

            if (Visuals)
            {
                Sfx.Play(Sound.ZoneCapture, -1f);
                Impact.Death(this, own.Home + Vector3.Up * 1.2f, Pal.Teams[team]);
            }
        }
    }

    /// <summary>Steps the flag rules once. The harness poses a situation, then asks for a tick.</summary>
    public void TickFlagsForTest(float dt) => TickFlags(dt);

    void SendFlagHome(Flag flag)
    {
        flag.Carrier = null;
        flag.At = flag.Home;
        flag.Loose = 0f;
        if (flag.Node != null) flag.Node.GlobalPosition = flag.At;
    }

    /// <summary>
    /// Where this pawn should be heading, given the mode.
    ///
    /// Per pawn rather than per match, because capture the flag asks two different questions of
    /// the same team at the same time: whoever has the enemy flag runs home with it, and everyone
    /// else goes to wherever that flag currently is.
    /// </summary>
    public Vector3? ObjectiveFor(Pawn p)
    {
        if (Settings.Mode == GameMode.KingOfTheHill) return ZoneCentre;
        if (Settings.Mode == GameMode.Dominion) return DominionObjective(p);

        // Everyone converges on the crown. The juggernaut has nowhere to be but wherever the fight
        // is, so it gets nothing here.
        if (Settings.Mode == GameMode.Juggernaut)
            return Juggernaut is { Alive: true } king && king != p ? king.GlobalPosition : null;

        if (Settings.Mode != GameMode.CaptureTheFlag || flags.Count < 2) return null;

        int team = TeamOf(p.Slot);

        // Carrying: run it home.
        if (FlagCarriedBy(p) != null) return FlagOf(team)?.Home;

        // Your own flag loose on the floor is the more urgent of the two errands, and the closer
        // one — a dropped flag is by definition somewhere your side was just fighting.
        if (FlagOf(team) is { Dropped: true } mine
            && mine.At.DistanceTo(p.GlobalPosition) < 55f) return mine.At;

        return FlagOf(1 - team)?.At;
    }

    /// <summary>
    /// True when the objective outranks looting and healing.
    ///
    /// Only for a flag carrier. A bot that stops to pick up a rifle on the way home with the flag
    /// is a bot that loses the flag, and no amount of gun is worth that.
    /// </summary>
    public bool ObjectiveIsUrgent(Pawn p)
        => Settings.Mode == GameMode.CaptureTheFlag && FlagCarriedBy(p) != null;

    /// <summary>
    /// How close a bot has to get before it counts as having arrived.
    ///
    /// Standing anywhere in a King of the Hill zone scores, so stopping short of the middle is
    /// fine and stops bots jittering on the spot. A flag has to actually be touched.
    /// </summary>
    public float ObjectiveStopRange
        => Settings.Mode switch
        {
            GameMode.CaptureTheFlag => FlagReach * 0.5f,

            // Anywhere inside the circle captures, so a bot that stops well short of the middle is
            // still doing the job — and stopping short is what keeps a squad from all trying to
            // stand on the same square metre.
            GameMode.Dominion => PostRadius * 0.6f,

            _ => ZoneRadius * 0.7f,
        };

    /// <summary>
    /// Whether the objective should pull a bot out of a loose fight.
    ///
    /// False for King of the Hill, where the objective *is* the fight — you score by standing in
    /// the zone, so a bot shooting someone near it is already doing the right thing. True for
    /// capture the flag, where the objective is somewhere else entirely.
    ///
    /// Without this the flag simply never got picked up. The bot's errand list put the objective
    /// below "am I engaged?", and four Veteran bots on one arena are engaged nearly all the time:
    /// a full match ran with shots fired, damage landed, kills scored, and not one flag touched.
    /// The mode looked like it worked from every statistic except the only one that mattered.
    ///
    /// Dominion is the flag case, not the hill case, and the harness said so rather than anyone
    /// having to guess: exactly the same thing happened again, a full scenario with the bots
    /// walking two hundred metres each and not one post changing hands. The posts are spread over
    /// the whole map on purpose, so the fight is almost never standing on one — and a bot that
    /// only heads for a post while nobody is shooting at it never arrives at any of them.
    /// </summary>
    public bool ObjectiveOutranksFighting => ObjectiveOutranksFightingFor(Settings.Mode);

    /// <summary>The same rule, asked of a mode rather than of a match. For the harness.</summary>
    public static bool ObjectiveOutranksFightingFor(GameMode mode)
        => mode is GameMode.CaptureTheFlag or GameMode.Juggernaut or GameMode.Dominion;

    void MoveZoneMesh()
    {
        if (zoneMesh == null) return;
        zoneMesh.GlobalPosition = ZoneCentre with { Y = ZoneCentre.Y + ZoneColumnHeight * 0.5f - 1f };
    }

    /// <summary>Paused by the pause screen. Physics keeps ticking; the simulation does not.</summary>
    public bool Paused { get; set; }

    bool ModeAllowsRespawn => Settings.Mode != GameMode.Elimination;

    // ---- weapons ----

    public void FireWeapon(Pawn shooter)
    {
        // Read the weapon before firing: OnFired spends a round, and an emptied pickup reverts to
        // the class weapon, which would otherwise change these values mid-volley.
        var gun = shooter.Weapon;
        float spread = Mathf.DegToRad(shooter.EffectiveSpreadDeg);
        float muzzleVelocity = shooter.EffectiveProjectileSpeed;

        ShotsFired++;
        shooter.OnFired();

        for (int i = 0; i < gun.Pellets; i++)
        {
            // A single pellet fires dead straight; a spread of pellets is spaced evenly across the
            // cone with a little jitter, so a shotgun pattern is consistent rather than clumping.
            float yawOff = gun.Pellets == 1
                ? (float)GD.RandRange(-spread, spread) * 0.5f
                : Mathf.Lerp(-spread, spread, (i + 0.5f) / gun.Pellets) + (float)GD.RandRange(-0.02, 0.02);

            // Spread has to open vertically as well now that the game is first-person and shots
            // carry pitch — a purely horizontal fan would be a flat sheet of pellets.
            float pitchOff = (float)GD.RandRange(-spread, spread) * 0.5f;

            float yaw = shooter.Facing + yawOff;
            float pitch = MathU.Clamp(shooter.Pitch + pitchOff, -Pawn.MaxPitch, Pawn.MaxPitch);
            float cp = MathF.Cos(pitch);
            var dir = new Vector3(MathF.Cos(yaw) * cp, MathF.Sin(pitch), MathF.Sin(yaw) * cp);

            var shot = new Shot
            {
                Pos = shooter.Eye + dir * (Pawn.Radius + 0.2f),
                Vel = dir * muzzleVelocity,
                RangeLeft = gun.Range,
                Owner = shooter,
                Damage = gun.Damage,
                BlastDamage = gun.BlastDamage,
                BlastRadius = gun.BlastRadius,
                PlantsPortal = gun.PlantsPortal,
                Grapples = gun.Grapples,
                Heals = gun.HealsFriendlies,
                Fuse = gun.FuseTime,
                Bounces = gun.Bounces,
                Weight = gun.Weight,
                SeekTurnRate = gun.SeekTurnRate,
                SeekRange = gun.SeekRange,
                SeekCone = Mathf.DegToRad(gun.SeekConeDeg),
                TriggerRadius = gun.TriggerRadius,
                Needles = gun.Needles,
                Scoped = gun.HasScope,
            };

            if (Visuals)
            {
                // Bright enough to cross the glow threshold, so tracers streak instead of being
                // small pale rectangles lost against the floor.
                //
                // A portal round is the one tracer that does not wear the shooter's colour. It is
                // not a shot at anybody, and colouring it like one means the first thing anyone
                // learns about the portal gun is that someone is shooting at them.
                var mesh = new MeshInstance3D
                {
                    Mesh = new BoxMesh { Size = new Vector3(0.10f, 0.10f, 0.55f) },
                    MaterialOverride = gun.PlantsPortal
                        ? Graphics.Hot(new Color(0.35f, 0.88f, 1f), 3.4f)
                        : Graphics.Hot(shooter.Tint.Lightened(0.5f), 3.4f),
                };
                AddChild(mesh);
                PointAlong(mesh, shot.Pos, shot.Vel);
                shot.Mesh = mesh;
            }

            shots.Add(shot);
        }

        // One report per trigger pull, not one per pellet — eight overlapping copies of the
        // shotgun sample would just clip.
        if (Visuals)
            Sfx.PlayAt(Sfx.ShotFor(gun), shooter.Eye, pitch: (float)GD.RandRange(0.94, 1.06));
    }

    // ---- level hazards and machinery ----

    readonly List<AnimatableBody3D> platforms = new();
    float platformClock;

    void BuildLevelMachinery()
    {
        foreach (var def in Arena.MovingPlatforms)
        {
            var body = new AnimatableBody3D
            {
                // Sync to physics is what makes a rider inherit the platform's motion: Godot's
                // CharacterBody3D reads velocity from the body it is standing on, and without
                // this the platform teleports out from under you every tick.
                SyncToPhysics = true,
                Position = def.A,
            };
            AddChild(body);

            body.AddChild(new CollisionShape3D
            {
                Shape = new BoxShape3D { Size = def.HalfExtents * 2f },
            });

            if (Visuals)
            {
                body.AddChild(new MeshInstance3D
                {
                    Mesh = new BoxMesh { Size = def.HalfExtents * 2f },
                    MaterialOverride = Graphics.Hot(new Color(0.35f, 0.72f, 0.95f), 0.8f),
                });
            }

            platforms.Add(body);
        }
    }

    void StepMovingPlatforms(float dt)
    {
        if (platforms.Count == 0) return;

        platformClock += dt;

        for (int i = 0; i < platforms.Count; i++)
        {
            var def = Arena.MovingPlatforms[i];

            // Easing and any dwell at the ends live on the definition, so an elevator that waits
            // to be boarded and a shuttle that never stops are the same machinery.
            Vector3 was = platforms[i].GlobalPosition;
            Vector3 now = def.A.Lerp(def.B, def.Travel(platformClock));
            platforms[i].GlobalPosition = now;

            if (def.Pushes) ShoveAlong(def, was, now, dt);
        }
    }

    /// <summary>
    /// Sweep a moving wall through the pawns in its way and shove them along it.
    ///
    /// Godot resolves a CharacterBody3D against an AnimatableBody3D by stopping the character, not
    /// by carrying it, so a push wall left to the physics engine is a wall that happens to travel.
    /// Anyone it catches is moved with it and given a shove, which over a trench is fatal — which
    /// is the entire point of the thing.
    /// </summary>
    void ShoveAlong(MovingPlatformDef def, Vector3 was, Vector3 now, float dt)
    {
        Vector3 step = now - was;
        if (step.LengthSquared() < 1e-6f) return;

        Vector3 dir = step.Normalized();
        float speed = step.Length() / MathF.Max(dt, 1e-4f);

        foreach (var p in Pawns)
        {
            if (!p.Alive || p.InVehicle) continue;

            // Measured against the wall's leading face rather than its centre, so it catches you
            // as it arrives instead of once you are already inside it.
            Vector3 d = p.GlobalPosition - now;

            if (MathF.Abs(d.X) > def.HalfExtents.X + Pawn.Radius + 0.2f) continue;
            if (MathF.Abs(d.Z) > def.HalfExtents.Z + Pawn.Radius + 0.2f) continue;
            if (d.Y < -def.HalfExtents.Y - 0.4f || d.Y > def.HalfExtents.Y + Pawn.Height) continue;

            // Carried with the wall, plus a push. Without the carry a pawn wedged against the far
            // side is simply overrun, because the wall covers more ground per tick than the shove
            // has time to move them.
            //
            // Moved rather than teleported. Assigning the position directly shoved pawns *into*
            // whatever was behind them, and Godot resolves a body that starts a frame inside
            // geometry by ejecting it — hard, and in whatever direction the solver likes. That is
            // what was firing players out of the map.
            p.MoveAndCollide(step);
            p.ApplyKnockback(dir * MathF.Max(speed * 1.4f, 7f));

            if (Visuals) p.DamageFlash = Pawn.DamageFlashTime;
        }
    }

    // ---- vehicles ----

    public readonly List<Vehicle> VehicleList = new();

    /// <summary>Vehicles boarded so far, so the harness can prove they are reachable and drivable.</summary>
    public int VehicleBoardings { get; private set; }

    /// <summary>Rounds put out by mounted guns, so the harness can see bots actually using them.</summary>
    public int VehicleShotsFired
    {
        get
        {
            int n = 0;
            foreach (var v in VehicleList) n += v.ShotsFired;
            return n;
        }
    }

    void BuildVehicles()
    {
        // One of each parked around the ring, so every match offers all three without any one of
        // them dominating a spawn.
        for (int i = 0; i < Arena.VehicleSpawns.Count; i++)
        {
            var def = Vehicles.ByIndex(i);
            var v = new Vehicle { Name = $"Vehicle{i}" };
            AddChild(v);
            v.Setup(def, Visuals);
            v.ArenaCeiling = Arena.WallHeight;
            v.HomePosition = Arena.VehicleSpawns[i];
            v.GlobalPosition = Arena.VehicleSpawns[i] + Vector3.Up * 0.5f;
            v.Facing = MathU.Angle(new Vector2(-v.GlobalPosition.X, -v.GlobalPosition.Z));
            VehicleList.Add(v);
        }
    }

    void StepVehicles(float dt)
    {
        foreach (var v in VehicleList)
        {
            PawnInput input = default;

            if (v.Driver is { } driver)
            {
                int index = Pawns.IndexOf(driver);

                if (driver.IsBot)
                {
                    input = brains[index].DriveThink(dt, driver, v, this);

                    // The bot's dismount is handled here rather than in the pawn loop, because
                    // this is where it was decided.
                    if (input.Use) { ToggleVehicle(driver); continue; }
                }
                else input = InputSource?.Invoke(index) ?? default;
            }

            v.Tick(dt, input, this);
            v.Driver?.RideAlong();

            // Driven into a pit. Vehicles had no kill plane at all, so a hull that went over an
            // edge fell forever — and its driver fell with it, out of reach of the pawn fall
            // check, which is skipped for anyone being carried.
            if (v.Alive && v.GlobalPosition.Y < Arena.KillPlaneY)
            {
                v.TakeDamage(v.Def.Health);
                if (Visuals) Sfx.PlayAt(Sound.Death, v.GlobalPosition);
            }

            // Wrecks come back. Now that hulls can actually be destroyed, a long match would
            // otherwise burn through all three in the first few minutes and never see another.
            if (!v.Alive)
            {
                // A wreck settles at the kill plane instead of falling out of the world until it
                // respawns. Same fault the corpses had, found the same way: a tank driven into one
                // of the outer trenches was six hundred metres down by the end of the run.
                if (v.GlobalPosition.Y < Arena.KillPlaneY)
                {
                    v.GlobalPosition = v.GlobalPosition with { Y = Arena.KillPlaneY };
                    v.Velocity = Vector3.Zero;
                }

                v.WreckAge += dt;
                if (v.WreckAge >= VehicleRespawnTime)
                {
                    v.Restore(v.HomePosition + Vector3.Up * 0.5f,
                              MathU.Angle(new Vector2(-v.HomePosition.X, -v.HomePosition.Z)));
                    if (Visuals) Sfx.PlayAt(Sound.Respawn, v.GlobalPosition);
                }
                continue;
            }

            ResolveVehicleRam(v);
        }
    }

    /// <summary>Driving into someone hurts, hard. It is the car's only weapon.</summary>
    void ResolveVehicleRam(Vehicle v)
    {
        if (!v.Alive || v.Driver == null || v.Def.RamDamage <= 0f) return;

        float speed = new Vector2(v.Velocity.X, v.Velocity.Z).Length();
        if (speed < 8f) return;

        foreach (var p in Pawns)
        {
            if (!p.Alive || p == v.Driver || p.InVehicle) continue;
            if (p.GlobalPosition.DistanceTo(v.GlobalPosition) > v.Def.HalfExtents.Length() + 1.2f) continue;

            // Scaled by how fast you were actually going, so a crawl is a shove and a charge kills.
            float dealt = v.Def.RamDamage * MathU.Clamp01(speed / v.Def.MaxSpeed);

            Vector3 away = (p.GlobalPosition - v.GlobalPosition) with { Y = 0f };
            away = away.LengthSquared() < 0.01f ? v.Forward : away.Normalized();
            p.ApplyKnockback(away * 20f + Vector3.Up * 7f);

            float before = p.Health;
            bool killed = p.TakeDamage(dealt);
            DamageDealt += before - p.Health;

            if (Visuals) Impact.Hit(this, p.GlobalPosition + Vector3.Up, away, v.Def.Tint);
            if (killed && v.Driver != null) AwardKill(v.Driver, p);
        }
    }

    /// <summary>
    /// Boards or leaves a vehicle. Routed through the match rather than the pawn because it needs
    /// to see every hull, not just the one being asked about.
    /// </summary>
    /// <summary>
    /// The hull this pawn would board right now, or null. Drives the on-screen prompt: a vehicle
    /// you can enter but are never told about may as well not be drivable.
    /// </summary>
    public Vehicle? NearestBoardable(Pawn p)
    {
        Vehicle? best = null;
        float bestDist = float.MaxValue;

        foreach (var v in VehicleList)
        {
            if (!v.CanBoard(p)) continue;
            float d = p.GlobalPosition.DistanceTo(v.GlobalPosition);
            if (d < bestDist) { bestDist = d; best = v; }
        }
        return best;
    }

    /// <summary>
    /// The closest vehicle worth walking to, or null. Wider than <see cref="NearestBoardable"/>,
    /// which only answers "can I get in right now".
    /// </summary>
    public Vehicle? NearestFreeVehicle(Vector3 at, float maxDistance)
    {
        Vehicle? best = null;
        float bestDist = maxDistance;

        foreach (var v in VehicleList)
        {
            if (!v.Alive || v.Occupied) continue;

            float d = at.DistanceTo(v.GlobalPosition);
            if (d < bestDist) { bestDist = d; best = v; }
        }
        return best;
    }

    /// <summary>
    /// Fraction of <paramref name="distance"/> that is clear along a ray, 1 being wholly open.
    ///
    /// Exists so the driving brain can feel its way around the arena without a navigation graph:
    /// a hull cannot climb the towers a pawn can, so routing it through the pawn graph would send
    /// a tank at a staircase and leave it grinding on the bottom step.
    /// </summary>
    public float ClearAhead(Vehicle ignoring, Vector3 from, Vector3 dir, float distance)
    {
        using var query = PhysicsRayQueryParameters3D.Create(from, from + dir * distance);
        query.Exclude = new Godot.Collections.Array<Rid> { ignoring.GetRid() };

        var hit = GetWorld3D().DirectSpaceState.IntersectRay(query);
        if (hit.Count == 0) return 1f;

        return MathU.Clamp01(from.DistanceTo(hit["position"].AsVector3()) / MathF.Max(0.01f, distance));
    }

    /// <summary>
    /// A weapon crate this pawn is standing at and would actually take. Bots need to know, because
    /// taking one now requires holding the interact button rather than walking over it.
    /// </summary>
    /// <param name="combatOnly">
    /// Skip weapons that cannot hurt anyone. Bots pass true: the portal gun is a movement tool and
    /// a bot that picks one up is a bot standing in the open pressing the trigger on something that
    /// does no damage. Players are shown the prompt for everything, because for a player a gate is
    /// worth crossing the map for.
    /// </param>
    public bool WeaponCrateInReach(Pawn p, bool combatOnly = false)
    {
        foreach (var c in pickups)
        {
            if (!c.Available || c.Kind != PickupKind.Weapon) continue;
            if (combatOnly && c.Weapon!.IsUtility) continue;
            if (p.GlobalPosition.DistanceTo(c.At) > PickupReach) continue;
            if (p.WouldTake(c.Weapon!)) return true;
        }
        return false;
    }

    public void ToggleVehicle(Pawn p)
    {
        if (p.Riding is { } current) { current.Eject(); return; }

        foreach (var v in VehicleList)
        {
            if (!v.CanBoard(p)) continue;
            v.Board(p);
            VehicleBoardings++;
            if (Visuals) Sfx.PlayAt(Sound.Respawn, v.GlobalPosition, pitch: 0.8f);
            return;
        }
    }

    /// <summary>Fires a vehicle-mounted gun, reusing the same projectile path as everything else.</summary>
    public void FireVehicleWeapon(Vehicle v)
    {
        if (v.Def.Gun is not { } gun || v.Driver is not { } shooter) return;

        float spread = Mathf.DegToRad(gun.SpreadDeg);
        ShotsFired++;

        for (int i = 0; i < gun.Pellets; i++)
        {
            float yawOff = (float)GD.RandRange(-spread, spread) * 0.5f;
            float pitchOff = (float)GD.RandRange(-spread, spread) * 0.5f;

            // Down the turret, not down the hull. A plane has no turret and returns its nose.
            var aim = v.AimDir;
            float yaw = MathF.Atan2(aim.Z, aim.X) + yawOff;
            float pitch = MathF.Asin(MathU.Clamp(aim.Y, -1f, 1f)) + pitchOff;
            float cp = MathF.Cos(pitch);
            var dir = new Vector3(MathF.Cos(yaw) * cp, MathF.Sin(pitch), MathF.Sin(yaw) * cp);

            var shot = new Shot
            {
                Pos = v.Seat + dir * (v.Def.HalfExtents.Length() + 0.6f),
                Vel = dir * gun.ProjectileSpeed,
                RangeLeft = gun.Range,
                Owner = shooter,
                Damage = gun.Damage,
                BlastDamage = gun.BlastDamage,
                BlastRadius = gun.BlastRadius,
                StructureScale = VehicleStructureMultiplier,
            };

            if (Visuals)
            {
                var mesh = new MeshInstance3D
                {
                    Mesh = new BoxMesh { Size = new Vector3(0.22f, 0.22f, 1.1f) },
                    MaterialOverride = Graphics.Hot(v.Def.Tint.Lightened(0.5f), 3.4f),
                };
                AddChild(mesh);
                PointAlong(mesh, shot.Pos, shot.Vel);
                shot.Mesh = mesh;
            }

            shots.Add(shot);
        }

        if (Visuals) Sfx.PlayAt(Sfx.ShotFor(gun), v.Seat, pitch: 0.75f);
    }

    // ---- weapon pickups ----

    public enum PickupKind { Weapon, Jetpack, Health }

    sealed class Pickup
    {
        public Vector3 At;
        public PickupKind Kind;

        /// <summary>The gun in the crate. Only meaningful for a weapon crate.</summary>
        public WeaponDef? Weapon;

        public float RespawnIn;
        public Node3D? Node;
        public bool Available => RespawnIn <= 0f;
        public bool IsJetpack => Kind == PickupKind.Jetpack;

        /// <summary>
        /// Whether taking this needs a deliberate hold rather than walking over it.
        ///
        /// Weapons do, because taking one may cost you the weapon in your hands — that has to be a
        /// decision, not something that happens while you are running past. Health and jetpacks do
        /// not: neither takes anything away, and fumbling a heal mid-fight because you had to stand
        /// still and hold a button would be miserable.
        /// </summary>
        public bool NeedsHold => Kind == PickupKind.Weapon;

        public string Label => Kind switch
        {
            PickupKind.Jetpack => "Jetpack",
            PickupKind.Health => "Health",
            _ => Weapon?.Name ?? "Weapon",
        };

        public Color Tint => Kind switch
        {
            PickupKind.Jetpack => JetpackTint,
            PickupKind.Health => HealthTint,
            _ => Weapons.TintFor(Weapon!),
        };
    }

    static readonly Color HealthTint = new(0.35f, 0.95f, 0.45f);

    /// <summary>Health restored by a med crate. Enough to matter, not enough to undo a fight.</summary>
    public const float HealthPickupAmount = 45f;

    /// <summary>Seconds of holding interact before a weapon crate is taken.</summary>
    public const float PickupHoldTime = 0.4f;

    /// <summary>Jetpack crates are their own colour so they read as gear, not a gun.</summary>
    static readonly Color JetpackTint = new(0.45f, 0.95f, 0.98f);

    readonly List<Pickup> pickups = new();

    /// <summary>Available pickups, for the HUD to label in world space.</summary>
    public IEnumerable<(Vector3 At, string Label, Color Tint)> AvailablePickups()
    {
        foreach (var p in pickups)
            if (p.Available)
                yield return (p.At + Vector3.Up * 1.9f, p.Label, p.Tint);
    }

    const float PickupRespawn = 16f;
    const float PickupReach = 2.2f;

    /// <summary>Weapons taken so far, so the harness can prove pickups are reachable.</summary>
    public int PickupsTaken { get; private set; }

    /// <summary>Split out by kind, because a walk-over med kit and a held weapon swap are very
    /// different behaviours and one counter cannot tell you whether either is working.</summary>
    /// <summary>Med kits inside the core, where the fighting is. Zero means nobody will ever
    /// reach one, which is exactly what happened when kinds were cycled on the declaration order
    /// rather than on distance.</summary>
    public int HealthCratesInCore
    {
        get
        {
            int n = 0;
            foreach (var p in pickups)
                if (p.Kind == PickupKind.Health && new Vector2(p.At.X, p.At.Z).Length() < 62f) n++;
            return n;
        }
    }

    /// <summary>Crate composition, so the harness can see what the map is actually offering.</summary>
    public string PickupMix()
    {
        int w = 0, j = 0, h = 0, coreHealth = 0;

        foreach (var p in pickups)
        {
            if (p.Kind == PickupKind.Weapon) w++;
            else if (p.Kind == PickupKind.Jetpack) j++;
            else
            {
                h++;
                if (new Vector2(p.At.X, p.At.Z).Length() < 62f) coreHealth++;
            }
        }

        return $"{w} weapon, {j} jetpack, {h} med ({coreHealth} in the core)";
    }

    public int WeaponsTaken { get; private set; }
    public int HealthTaken { get; private set; }

    void BuildPickups()
    {
        // Kinds are assigned in order of distance from the middle, not in the order the arena
        // happened to declare its spawns.
        //
        // Cycling on the raw index put every med kit and all but one jetpack out in the districts,
        // because each layout declares its three core spawns first and the shared district pass
        // appends eight more afterwards. Bots rally in the core, so across every scenario in the
        // suite not one med kit was ever taken — the crates existed and nobody was ever near them.
        // No crates in Portal mode. The whole point is that everybody has one gun and it is the
        // same gun, and a railgun on the floor would end that in about four seconds.
        if (Settings.Mode == GameMode.Portal) return;

        var order = new List<int>();
        for (int k = 0; k < Arena.WeaponSpawns.Count; k++) order.Add(k);

        order.Sort((a, b) => Arena.WeaponSpawns[a].LengthSquared()
                                 .CompareTo(Arena.WeaponSpawns[b].LengthSquared()));

        for (int rank = 0; rank < order.Count; rank++)
        {
            int i = order[rank];

            // Cycled rather than random, so each arena always offers a spread of types instead of
            // occasionally three miniguns. Every fourth crate is a jetpack; med kits are no longer
            // taken out of this list at all — they have their own spawns spread over the whole
            // floor, which is what the crate list could never give them.
            var kind = rank % 4 == 3 ? PickupKind.Jetpack : PickupKind.Weapon;

            AddPickup(Arena.WeaponSpawns[i], kind,
                      kind == PickupKind.Weapon ? Weapons.ByIndex(i) : null);
        }

        foreach (var at in Arena.HealthSpawns)
            AddPickup(at, PickupKind.Health, null);
    }

    void AddPickup(Vector3 at, PickupKind kind, WeaponDef? weapon)
    {
        var p = new Pickup { At = at, Kind = kind, Weapon = weapon };

        if (Visuals)
        {
            var holder = new Node3D();
            AddChild(holder);
            holder.GlobalPosition = p.At + Vector3.Up * 0.9f;

            // A weapon on the floor is the weapon, not a box with its colour on. Gear keeps its
            // crate: there is no model of a jetpack or a med kit, and a crate is what they are.
            if (kind != PickupKind.Weapon || !BuildPickupModel(holder, weapon!))
            {
                // A jetpack crate is taller and thinner than a weapon crate, so the two are
                // distinguishable by silhouette and not only by colour.
                var size = kind switch
                {
                    PickupKind.Jetpack => new Vector3(0.7f, 1.2f, 0.55f),
                    PickupKind.Health => new Vector3(1.0f, 0.45f, 0.7f),   // flat and wide: a case
                    _ => new Vector3(0.9f, 0.9f, 0.9f),
                };

                holder.AddChild(new MeshInstance3D
                {
                    Mesh = new BoxMesh { Size = size },
                    MaterialOverride = Graphics.Hot(p.Tint, 1.5f),
                });
            }

            p.Node = holder;
        }

        pickups.Add(p);
    }

    /// <summary>
    /// Put the actual gun on the floor, or report that there is not a model of it.
    ///
    /// Scaled by the measurement rather than trusted, exactly as the view model does it: a
    /// generated mesh has no idea how long a rifle is, and a pickup that arrives twice the size of
    /// the one beside it reads as a bug rather than as a bigger gun. The silhouette's own length
    /// is the target so a railgun on the floor is longer than a sidearm, which is information.
    /// </summary>
    bool BuildPickupModel(Node3D holder, WeaponDef weapon)
    {
        if (WeaponModels.Instance(weapon, out float sourceLength, out Vector3 along,
                                  out float facing) is not { } model)
            return false;

        // Bigger than in the hands. A held gun is half a metre from the camera and a dropped one is
        // across a courtyard, and the thing that has to survive that distance is the silhouette.
        const float Longest = 1.5f;

        float want = weapon.Silhouette switch
        {
            WeaponSilhouette.Blade => Longest,
            WeaponSilhouette.Sniper => Longest,
            WeaponSilhouette.Launcher => Longest * 0.85f,
            WeaponSilhouette.Minigun => Longest * 0.8f,
            WeaponSilhouette.Smg => Longest * 0.55f,
            WeaponSilhouette.Portal => Longest * 0.6f,
            WeaponSilhouette.Grapple => Longest * 0.55f,
            _ => Longest * 0.7f,
        };

        model.Scale = Vector3.One * (want / sourceLength);

        // Laid across the spin rather than pointed down it. A gun rotating about its own long axis
        // is a rolling stick; across, the shape swings through the view and reads as what it is.
        // Tilted a little nose-up so it looks placed rather than dropped.
        //
        // Turned end for end with the same facing the held model uses, rather than ignoring it as
        // this did. A pickup lying muzzle-backwards is less obviously wrong than a held one and it
        // is still wrong, and having the two disagree about which end is the front would be worse
        // than either.
        float turn = facing > 0f ? 0f : 180f;

        if (along == Vector3.Right) model.RotationDegrees = new Vector3(0f, turn, 14f);
        else if (along == Vector3.Up) model.RotationDegrees = new Vector3(0f, turn, 90f - 14f);
        else model.RotationDegrees = new Vector3(0f, 90f + turn, 14f);

        holder.AddChild(model);

        // And the colour underneath it, because colour is how you tell one pickup from another
        // across an arena and the model is the weapon's own texture rather than its tint. The
        // crate carried both jobs; the gun only does the first, so the second gets its own ring.
        holder.AddChild(new MeshInstance3D
        {
            Mesh = new TorusMesh { InnerRadius = 0.52f, OuterRadius = 0.62f, RingSegments = 6 },
            MaterialOverride = Graphics.Hot(Weapons.TintFor(weapon), 2.2f),
            Position = new Vector3(0f, -0.55f, 0f),
        });

        return true;
    }

    void StepPickups(float dt)
    {
        foreach (var p in pickups)
        {
            if (p.RespawnIn > 0f)
            {
                p.RespawnIn -= dt;
                if (p.RespawnIn <= 0f && p.Node != null) p.Node.Visible = true;
                continue;
            }

            // Spin and bob, so a crate reads as a pickup rather than as more level geometry.
            if (p.Node != null)
            {
                p.Node.Rotation = new Vector3(0f, p.Node.Rotation.Y + 1.9f * dt, 0f);
                p.Node.GlobalPosition = p.At + Vector3.Up * (0.9f + 0.18f * MathF.Sin(Elapsed * 2.6f));
            }

            foreach (var pawn in Pawns)
            {
                if (!pawn.Alive) continue;
                if (pawn.GlobalPosition.DistanceTo(p.At) > PickupReach) continue;

                if (p.Kind == PickupKind.Jetpack)
                {
                    // A full tank already: leave it for someone who needs it.
                    if (pawn.JetFuel >= Pawn.JetFuelMax - 0.01f) continue;
                    pawn.GiveJetpack();
                }
                else if (p.Kind == PickupKind.Health)
                {
                    if (pawn.Health >= pawn.Class.Health - 0.01f) continue;
                    pawn.Heal(HealthPickupAmount);
                }
                else
                {
                    if (!pawn.WouldTake(p.Weapon!)) continue;

                    // Held, not walked over. Taking a weapon may cost you the one in your hands, so
                    // it has to be a decision rather than something that happens while running past.
                    if (pawn.PickupHold < PickupHoldTime) continue;

                    pawn.ClearPickupHold();
                    pawn.TakeWeapon(p.Weapon!);
                }

                PickupsTaken++;
                if (p.Kind == PickupKind.Weapon) WeaponsTaken++;
                if (p.Kind == PickupKind.Health) HealthTaken++;

                p.RespawnIn = PickupRespawn;
                if (p.Node != null) p.Node.Visible = false;

                if (Visuals)
                {
                    Sfx.PlayAt(Sound.Respawn, p.At, pitch: 1.35f);
                    Impact.Hit(this, p.At, Vector3.Up, p.Tint);
                }
                break;
            }
        }
    }

    /// <summary>
    /// Nearest available pickup a pawn could plausibly reach. The height limit is generous now
    /// that bots route through the navigation graph rather than walking in a straight line — a
    /// crate on a catwalk is a climb, not an impossibility, and the graph works out the way up.
    /// </summary>
    /// <summary>The nearest med kit worth walking to, or null.</summary>
    public Vector3? NearestHealth(Vector3 from, float maxDistance, float maxRise)
    {
        Vector3? best = null;
        float bestDist = maxDistance;

        foreach (var p in pickups)
        {
            if (!p.Available || p.Kind != PickupKind.Health) continue;
            if (MathF.Abs(p.At.Y - from.Y) > maxRise) continue;

            float d = from.DistanceTo(p.At);
            if (d >= bestDist) continue;

            bestDist = d;
            best = p.At;
        }
        return best;
    }

    /// <summary>
    /// The nearest crate <paramref name="who"/> would gain something from walking to.
    ///
    /// Filtered by what the pawn actually needs, which it previously was not: this returned the
    /// nearest pickup of *any* kind, and the caller is the bot's "I have a free weapon slot, go
    /// and fill it" branch. With three med kits on a map that was a harmless inefficiency. With
    /// twenty-eight it was fatal — a bot would walk to the nearest med kit, take it, still have a
    /// free weapon slot, and set off for the next one. Four bots toured the arena's medical
    /// supplies for a full match: damage across the suite fell by roughly nine tenths and whole
    /// scenarios finished without a shot landing.
    ///
    /// Health is deliberately not included even when the pawn is hurt. That is a different
    /// decision with different thresholds, and it has <see cref="NearestHealth"/> to make it with.
    /// </summary>
    public Vector3? NearestPickup(Pawn who, float maxDistance, float maxRise)
    {
        Vector3? best = null;
        float bestDist = maxDistance;
        Vector3 from = who.GlobalPosition;

        foreach (var p in pickups)
        {
            if (!p.Available) continue;

            switch (p.Kind)
            {
                case PickupKind.Weapon:
                    // A gun that can hurt someone, and one this pawn would actually take.
                    if (p.Weapon!.IsUtility || !who.WouldTake(p.Weapon)) continue;
                    break;

                case PickupKind.Jetpack:
                    if (who.HasJetpack) continue;
                    break;

                default:
                    continue;
            }

            if (MathF.Abs(p.At.Y - from.Y) > maxRise) continue;

            float d = from.DistanceTo(p.At);
            if (d >= bestDist) continue;

            bestDist = d;
            best = p.At;
        }

        return best;
    }

    void ApplyLaunchPads(Pawn pawn)
    {
        if (!pawn.Alive) return;

        foreach (var pad in Arena.LaunchPads)
        {
            var d = pawn.GlobalPosition - pad.Centre;

            // Must be roughly on the pad, not merely above it somewhere in the air.
            if (new Vector2(d.X, d.Z).Length() > pad.Radius) continue;
            if (d.Y < -0.5f || d.Y > 1.6f) continue;

            pawn.Launch(pad.Impulse);
            if (Visuals)
            {
                Sfx.PlayAt(Sound.Dash, pawn.GlobalPosition, pitch: 1.4f);
                Impact.Hit(this, pad.Centre, Vector3.Up, new Color(0.35f, 0.85f, 0.55f));
            }
            return;
        }
    }

    /// <summary>Damage a dash deals to whoever it slams into.</summary>
    public const float DashDamage = 26f;

    /// <summary>
    /// A dash is a weapon. Slamming someone deals damage and throws them hard — hard enough to
    /// put them into a pit or a hazard, which is the point: it turns the arena's own dangers into
    /// something you can aim an opponent at.
    /// </summary>
    void ResolveDashImpacts(Pawn dasher)
    {
        if (!dasher.Dashing || !dasher.Alive) return;

        foreach (var other in Pawns)
        {
            if (other == dasher || !other.Alive) continue;
            if (Settings.Def.Teams && SameTeam(dasher, other)) continue;
            if (dasher.DashHits.Contains(other)) continue;

            float reach = Pawn.Radius * 2f + 0.5f;
            if (dasher.GlobalPosition.DistanceTo(other.GlobalPosition) > reach) continue;

            dasher.DashHits.Add(other);

            Vector3 away = other.GlobalPosition - dasher.GlobalPosition;
            away.Y = 0f;
            away = away.LengthSquared() < 0.01f
                ? new Vector3(MathF.Cos(dasher.Facing), 0f, MathF.Sin(dasher.Facing))
                : away.Normalized();

            // A shove far stronger than a blast: this is meant to relocate someone, not nudge them.
            other.ApplyKnockback(away * 26f + Vector3.Up * 6f);

            float before = other.Health;
            bool killed = other.TakeDamage(DashDamage);
            DamageDealt += before - other.Health;

            if (other.Health < before)
            {
                dasher.HitConfirm = Pawn.HitConfirmTime;
                other.DamageFlash = Pawn.DamageFlashTime;
                other.LastAttacker = dasher.GlobalPosition;
            }

            if (Visuals)
            {
                Impact.Hit(this, other.GlobalPosition + Vector3.Up, away, dasher.Tint);
                Sfx.PlayAt(Sound.Hit, other.GlobalPosition, pitch: 0.7f);
            }

            if (killed) AwardKill(dasher, other);
        }
    }

    /// <summary>Damage of a bare melee swing. Two of them kill nobody; three do, on most classes.</summary>
    public const float MeleeDamage = 42f;

    /// <summary>How far the swing reaches from the pawn's centre.</summary>
    public const float MeleeRange = 2.8f;

    /// <summary>
    /// Half-angle of the swing arc, in radians. Wide enough that melee lands without pixel-perfect
    /// aim, narrow enough that it is still a thing you point at someone.
    /// </summary>
    const float MeleeHalfAngle = 0.95f;

    /// <summary>Melee count, so the harness can prove swings actually connect.</summary>
    public int MeleeHits { get; private set; }

    /// <summary>
    /// A swing. Not a projectile: it resolves instantly against everything in a cone in front of
    /// the swinger, because a melee that can miss to travel time is a melee nobody trusts.
    ///
    /// It hits vehicles too. Chipping a tank to death by hand takes seventeen swings, which is not
    /// a strategy — but a melee that visibly does nothing to a hull standing in front of you reads
    /// as broken, and the alternative was explaining why.
    /// </summary>
    public void MeleeStrike(Pawn swinger)
    {
        if (!swinger.Alive || swinger.InVehicle) return;

        Vector3 origin = swinger.GlobalPosition + Vector3.Up * swinger.CurrentEyeHeight;
        Vector3 dir = swinger.AimDir;
        float cosLimit = MathF.Cos(MeleeHalfAngle);

        bool connected = false;

        foreach (var other in Pawns)
        {
            if (other == swinger || !other.Alive || other.InVehicle) continue;
            if (Settings.Def.Teams && SameTeam(swinger, other)) continue;

            // Aim at the body's middle rather than its feet, or looking level at someone standing
            // right in front of you points below the cone.
            Vector3 to = other.GlobalPosition + Vector3.Up * (other.CurrentHeight * 0.5f) - origin;
            float dist = to.Length();
            if (dist > MeleeRange + Pawn.Radius) continue;
            if (dist > 0.01f && to.Normalized().Dot(dir) < cosLimit) continue;

            Vector3 away = to.LengthSquared() < 0.01f ? dir : to.Normalized();
            away.Y = 0f;
            away = away.LengthSquared() < 0.01f
                ? new Vector3(MathF.Cos(swinger.Facing), 0f, MathF.Sin(swinger.Facing))
                : away.Normalized();

            other.ApplyKnockback(away * 11f + Vector3.Up * 3.5f);

            float before = other.Health;
            bool killed = other.TakeDamage(MeleeDamage);
            DamageDealt += before - other.Health;
            connected = true;

            if (other.Health < before)
            {
                swinger.HitConfirm = Pawn.HitConfirmTime;
                other.DamageFlash = Pawn.DamageFlashTime;
                other.LastAttacker = swinger.GlobalPosition;
            }

            if (Visuals)
            {
                Impact.Hit(this, other.GlobalPosition + Vector3.Up, away, swinger.Tint);
                Sfx.PlayAt(Sound.Hit, other.GlobalPosition, pitch: 0.65f);
            }

            if (killed) AwardKill(swinger, other);
        }

        foreach (var rig in VehicleList)
        {
            if (!rig.Alive || rig == swinger.Riding) continue;

            // Nearest point on the hull, not its centre — a tank is eight metres long and its
            // middle is well out of arm's reach from the side of it.
            Vector3 near = NearestPointOn(rig.GlobalPosition + Vector3.Up * rig.Def.HalfExtents.Y,
                                          rig.Def.HalfExtents, origin);
            Vector3 to = near - origin;
            float dist = to.Length();
            if (dist > MeleeRange) continue;
            if (dist > 0.01f && to.Normalized().Dot(dir) < cosLimit) continue;

            float before = rig.Health;
            bool wrecked = rig.TakeDamage(MeleeDamage);
            DamageDealt += before - rig.Health;
            connected = true;

            if (wrecked) WreckVehicle(rig, swinger);
        }

        if (connected) MeleeHits++;
        else if (Visuals) Sfx.PlayAt(Sound.Dash, origin + dir * 1.2f, -6f, 1.4f);
    }

    /// <summary>
    /// Standing in a hazard burns you. Unlike a pit it is survivable, so it shapes where fights
    /// happen rather than simply deleting anyone who touches it — and being shoved into one is a
    /// real threat without being an instant loss.
    /// </summary>
    void ApplyHazards(Pawn pawn, float dt)
    {
        if (!pawn.Alive || Arena.Hazards.Count == 0) return;

        foreach (var h in Arena.Hazards)
        {
            var p = pawn.GlobalPosition;
            if (p.X < h.Area.Position.X || p.X > h.Area.End.X) continue;
            if (p.Z < h.Area.Position.Y || p.Z > h.Area.End.Y) continue;
            if (p.Y > h.Top) continue;

            float before = pawn.Health;
            bool killed = pawn.TakeDamage(h.DamagePerSecond * dt);
            DamageDealt += before - pawn.Health;

            if (pawn.Health < before) pawn.DamageFlash = Pawn.DamageFlashTime;

            if (killed)
            {
                // Burned to death by the level. Whoever shoved you in does not get the credit,
                // but you do lose the frag, exactly as with a fall.
                AwardKill(pawn, pawn);
                if (Visuals) Sfx.PlayAt(Sound.Death, pawn.GlobalPosition);
            }
            return;
        }
    }

    /// <summary>Falling off the world kills, and costs a frag the same way a suicide does.</summary>
    /// <summary>
    /// Outside the arena is fatal, with no exceptions and no grace.
    ///
    /// There is no legitimate way to be out here. Every route that put a player outside the walls
    /// was a bug — a shove into geometry, a stacked knockback, a launch pad clipping a corner — and
    /// the honest response to being somewhere impossible is to die rather than to float around
    /// behind the level shooting into it.
    /// </summary>
    void CheckOutOfBounds(Pawn pawn)
    {
        if (!pawn.Alive || Arena.InPlay(pawn.GlobalPosition)) return;

        pawn.TakeFatalFall();
        AwardKill(pawn, pawn);
        if (Visuals) Sfx.PlayAt(Sound.Death, pawn.GlobalPosition);
    }

    /// <summary>
    /// Seconds a pawn has to be inside solid geometry before it counts as being crushed.
    ///
    /// Not instant: a capsule brushes a corner for a frame all the time, and killing on that would
    /// be far worse than the thing it fixes.
    /// </summary>
    const float CrushGrace = 0.3f;

    readonly Dictionary<Pawn, float> crushTime = new();

    /// <summary>
    /// Being squeezed into the level kills you.
    ///
    /// A moving wall against a static one leaves nowhere to be, and the old outcome was to be
    /// squirted out of the gap at whatever speed the solver felt like. Dying is both the honest
    /// result and the one that cannot throw you through a wall.
    /// </summary>
    /// <summary>
    /// Whether a pawn is currently standing inside solid geometry.
    ///
    /// Deliberately a smaller capsule than the real one: at full size a pawn standing on the floor
    /// registers against the floor every frame. Public because it is the only honest way to assert
    /// that something which *places* a pawn — an eject, a respawn, a shove — put them somewhere
    /// they actually fit.
    /// </summary>
    public bool OverlapsSolid(Pawn pawn) => OverlapsSolid(pawn, null);

    /// <summary>
    /// The same, ignoring one body.
    ///
    /// Used by the eject checks to ignore the hull the driver just climbed out of. Not because
    /// being inside a tank is acceptable — the geometric check beside it proves they are clear of
    /// the hull, from the node transforms — but because this query asks the *physics server*, and
    /// a harness that teleports a vehicle and asks one line later is asking about where the
    /// vehicle used to be. The server does not see a scripted move until it steps.
    /// </summary>
    public bool OverlapsSolid(Pawn pawn, Vehicle? ignore)
    {
        using var probe = new CapsuleShape3D
        {
            Radius = Pawn.Radius * 0.62f,
            Height = MathF.Max(pawn.CurrentHeight * 0.7f, Pawn.Radius * 1.3f),
        };

        using var query = new PhysicsShapeQueryParameters3D
        {
            Shape = probe,
            Transform = new Transform3D(Basis.Identity,
                                        pawn.GlobalPosition + Vector3.Up * (pawn.CurrentHeight * 0.5f)),
            Exclude = ignore is { } skip
                ? new Godot.Collections.Array<Rid> { pawn.GetRid(), skip.GetRid() }
                : new Godot.Collections.Array<Rid> { pawn.GetRid() },
        };

        return GetWorld3D().DirectSpaceState.IntersectShape(query, 1).Count > 0;
    }

    void CheckCrushed(Pawn pawn, float dt)
    {
        if (!pawn.Alive) { crushTime.Remove(pawn); return; }

        bool inside = OverlapsSolid(pawn);

        crushTime.TryGetValue(pawn, out float t);
        t = inside ? t + dt : 0f;
        crushTime[pawn] = t;

        if (t < CrushGrace) return;

        crushTime[pawn] = 0f;
        pawn.KilledBy = "";
        pawn.TakeFatalFall();
        AwardKill(pawn, pawn);

        if (Visuals)
        {
            Impact.Death(this, pawn.GlobalPosition + Vector3.Up * 0.9f, pawn.Tint);
            Sfx.PlayAt(Sound.Death, pawn.GlobalPosition);
        }
    }

    void CheckFall(Pawn pawn)
    {
        if (!pawn.Alive || pawn.GlobalPosition.Y > Arena.KillPlaneY) return;

        pawn.TakeFatalFall();
        AwardKill(pawn, pawn);
        if (Visuals) Sfx.PlayAt(Sound.Death, pawn.GlobalPosition);
    }

    // ---- specials ----

    /// <summary>A grenade in flight. Same manual sweep as a bullet, but with gravity and a fuse.</summary>
    sealed class Grenade
    {
        public Vector3 Pos;
        public Vector3 Vel;
        public float Fuse;
        public Pawn Owner = null!;
        public MeshInstance3D? Mesh;
    }

    readonly List<Grenade> grenades = new();

    /// <summary>Live grenade count, watched by the headless test alongside the projectile list.</summary>
    public int GrenadeCount => grenades.Count;

    /// <summary>Specials fired so far, per kind. Engagement metric for the harness.</summary>
    public readonly Dictionary<SpecialKind, int> SpecialsUsed = new();

    // ---- Bloom ----

    /// <summary>
    /// A patch of living ground: heals whoever belongs to the planter's side, slows everyone else.
    ///
    /// The only ability in the game that makes ground worth standing *on* rather than worth
    /// avoiding, which is the Garden's whole argument expressed as a mechanic.
    /// </summary>
    sealed class BloomPatch
    {
        public Vector3 At;
        public Pawn Owner = null!;
        public float Left;
        public Node3D? Node;
    }

    readonly List<BloomPatch> blooms = new();

    /// <summary>
    /// How far a Bloom reaches.
    ///
    /// Four times what it was. At five and a half metres it was a patch you had to be standing
    /// exactly on, which meant it healed whoever planted it and nobody else — a team ability that
    /// only ever affected one person. At twenty-two it was a piece of *ground*: a doorway, a
    /// command post, the middle of a room. That is the difference between a buff and a place.
    ///
    /// Then 60% off, to 8.8. Twenty-two metres was not a doorway, it was a district — the circle
    /// was wider than most rooms on any layout, so there was no standing *outside* one without
    /// leaving the fight, and no skill in placing something that covered everywhere you might have
    /// wanted it. At 8.8 it is a room or a doorway again, and where you put it is a decision.
    /// Reach only: the heal rate and the slow are unchanged.
    /// </summary>
    public const float BloomRadius = 8.8f;
    public const float BloomDuration = 9f;

    /// <summary>Health per second inside the patch, and the fraction of pace it costs an enemy.</summary>
    /// <summary>
    /// Healing a second inside one. Enough to out-heal sustained fire, on purpose.
    ///
    /// Twenty-two a second lost to any two people shooting at you, so standing in it was never a
    /// decision anybody had to respect. Ninety was the overcorrection: it out-healed most of the
    /// armoury, so the counter to a planted Bloom was to leave rather than to fight. Thirty is a
    /// third of that — it wins a duel you were already winning and loses one you were not, which
    /// is the amount of help an ability should be.
    /// </summary>
    public const float BloomHealPerSecond = 30f;

    /// <summary>
    /// What an enemy caught in one is reduced to.
    ///
    /// A third of the slow it was, which is not a third of this number: at 0.22 the patch took
    /// away 78% of your pace, and a third of that is 26%, so what is left is 0.74. Stated as the
    /// leftover fraction because that is what the movement code multiplies by, but it is the
    /// *taken* part that was tuned.
    ///
    /// Being cut to a fifth of walking pace inside a twenty-two metre circle was not a slow, it
    /// was a hold — crossing one meant being shot at for several seconds with no say in it.
    /// </summary>
    public const float BloomSlow = 0.74f;

    /// <summary>How slowed this pawn is right now, 1 meaning unaffected. Read by the pawn's move.</summary>
    public float BloomSlowFor(Pawn p)
    {
        foreach (var b in blooms)
        {
            if (SameSide(b.Owner, p)) continue;
            if (p.GlobalPosition.DistanceTo(b.At) < BloomRadius) return BloomSlow;
        }
        return 1f;
    }

    /// <summary>Teams in a team mode, otherwise only the planter counts as their own side.</summary>
    bool SameSide(Pawn a, Pawn b)
        => a == b || (Settings.Def.Teams && SameTeam(a, b));

    /// <summary>Advance the Bloom patches once, for the harness.</summary>
    public void StepBloomsForTest(float dt) => StepBlooms(dt);

    void StepBlooms(float dt)
    {
        // Recomputed from scratch every tick rather than accumulated, so a pawn that walks out of
        // a patch is back to full pace on the next frame with no bookkeeping to get wrong.
        foreach (var p in Pawns) p.SlowFactor = BloomSlowFor(p);

        for (int i = blooms.Count - 1; i >= 0; i--)
        {
            var b = blooms[i];
            b.Left -= dt;

            if (b.Left <= 0f)
            {
                b.Node?.QueueFree();
                blooms.RemoveAt(i);
                continue;
            }

            foreach (var p in Pawns)
            {
                if (!p.Alive || p.InVehicle) continue;
                if (!SameSide(b.Owner, p)) continue;
                if (p.GlobalPosition.DistanceTo(b.At) > BloomRadius) continue;

                p.Heal(BloomHealPerSecond * dt);
            }
        }
    }

    void PlantBloom(Pawn user)
    {
        // Thrown a short way along the aim and dropped to the floor, so it lands in front of you
        // rather than under your feet — it is cover you create, not a puddle you stand in.
        Vector3 at = user.GlobalPosition + new Vector3(user.AimDir.X, 0f, user.AimDir.Z).Normalized() * 4f;
        at.Y = user.GlobalPosition.Y;

        var patch = new BloomPatch { At = at, Owner = user, Left = BloomDuration };

        if (Visuals)
        {
            var node = new Node3D();
            AddChild(node);
            node.GlobalPosition = at + Vector3.Up * 0.06f;

            node.AddChild(new MeshInstance3D
            {
                Mesh = new CylinderMesh
                {
                    TopRadius = BloomRadius,
                    BottomRadius = BloomRadius,
                    Height = 0.12f,
                },
                MaterialOverride = Graphics.Hot(user.Faction.Tint, 0.9f),
            });

            patch.Node = node;
            Impact.Hit(this, at, Vector3.Up, user.Faction.Tint);
        }

        blooms.Add(patch);
    }

    // ---- butter trail ----
    //
    // The car greases the floor behind it. Anyone on foot who crosses the slick goes over
    // backwards and spends a second looking at the sky.
    //
    // It is the one thing the car has. The tank has a cannon and the plane has wing guns; the car
    // had a top speed and a bumper, which made it transport rather than a weapon. A trail turns
    // driving *through* a fight into a play — you are not trying to hit anybody, you are trying to
    // be somewhere they are about to run.

    sealed class ButterPatch
    {
        public Vector3 At;
        public float Left;
        public Node3D? Node;
    }

    readonly List<ButterPatch> butter = new();

    /// <summary>How wide one dollop of the trail is.</summary>
    public const float ButterRadius = 2.4f;

    /// <summary>Seconds a patch stays slick before it wears off.</summary>
    public const float ButterLife = 11f;

    /// <summary>
    /// Seconds between dollops while a car is moving.
    ///
    /// Distance would be the obvious measure and is the wrong one: at 34 m/s a car covers four
    /// metres in this interval and lays a trail with gaps you can run between, which is exactly
    /// the reward a fast driver should get for driving fast. Metering by time means the trail
    /// thins as you speed up rather than costing more to lay.
    /// </summary>
    public const float ButterDropInterval = 0.11f;

    /// <summary>Below this the car is parked or crawling, and a stationary car should not puddle.</summary>
    public const float ButterMinSpeed = 7f;

    /// <summary>
    /// Ceiling on live patches, shared across every car on the map.
    ///
    /// Without it a four-car match laying nine dollops a second each has the whole floor greased
    /// inside a minute, and a hazard that is everywhere is not a hazard — it is the ground rules.
    /// The oldest goes when the cap is reached, so the trail behind you is always the newest.
    /// </summary>
    public const int ButterMaxPatches = 150;

    /// <summary>Live butter patches. Read by the harness.</summary>
    public int ButterCount => butter.Count;

    /// <summary>Advance the butter trail once, for the harness.</summary>
    public void StepButterForTest(float dt) => StepButter(dt);

    /// <summary>Lay one patch where the harness asks, without needing a car to drive over it.</summary>
    public void DropButterForTest(Vector3 at) => DropButter(at);

    void StepButter(float dt)
    {
        foreach (var v in VehicleList)
        {
            if (!v.Alive || v.Def.Kind != VehicleKind.Car) continue;

            // Driven or not. A wreck rolling to a stop should not keep buttering, and neither
            // should an empty car shoved along by a blast.
            if (v.Driver == null || v.HorizontalSpeed < ButterMinSpeed)
            {
                v.ButterTimer = 0f;
                continue;
            }

            v.ButterTimer -= dt;
            if (v.ButterTimer > 0f) continue;

            v.ButterTimer = ButterDropInterval;
            DropButter(v.GlobalPosition);
        }

        for (int i = butter.Count - 1; i >= 0; i--)
        {
            var b = butter[i];
            b.Left -= dt;

            if (b.Left <= 0f)
            {
                b.Node?.QueueFree();
                butter.RemoveAt(i);
                continue;
            }

            foreach (var p in Pawns)
            {
                if (!p.Alive || p.InVehicle || p.Slipping) continue;

                // Feet, not centre of mass. Measured flat and then gated on height, so the
                // skybridge over a slick is not slippery and neither is a jetpack four metres up.
                var d = p.GlobalPosition - b.At;
                if (MathF.Abs(d.Y) > 1.6f) continue;
                if (new Vector2(d.X, d.Z).Length() > ButterRadius) continue;

                // Everyone, the driver included. A hazard you are immune to is a weapon, and the
                // car is not supposed to have one — what makes the trail fair is that getting out
                // of your own car in the middle of it is exactly as bad an idea as it looks.
                p.Slip();
            }
        }
    }

    void DropButter(Vector3 at)
    {
        if (butter.Count >= ButterMaxPatches)
        {
            butter[0].Node?.QueueFree();
            butter.RemoveAt(0);
        }

        var patch = new ButterPatch { At = at, Left = ButterLife };

        if (Visuals)
        {
            var node = new Node3D();
            AddChild(node);

            // Just off the floor, like the Bloom disc. Any lower and it z-fights the slab it is
            // lying on, which reads as the trail flickering rather than as a decal.
            node.GlobalPosition = at + Vector3.Up * 0.05f;

            node.AddChild(new MeshInstance3D
            {
                Mesh = new CylinderMesh
                {
                    TopRadius = ButterRadius,
                    BottomRadius = ButterRadius,
                    Height = 0.08f,
                },
                // The trail is painted in the colour of the thing that laid it, read from the
                // car itself rather than restated here, so a reskin cannot leave a butter car
                // dropping puddles of the colour it used to be.
                MaterialOverride = Graphics.Hot(Vehicles.Car.Tint, 0.55f),
            });

            patch.Node = node;
        }

        butter.Add(patch);
    }

    // ---- Understudy ----

    /// <summary>
    /// A decoy that walks on in the direction its owner was facing.
    ///
    /// Not a pawn: it has no health, takes no damage and cannot shoot. It exists to be shot *at*,
    /// which is the whole point — every round spent on it is a round not spent on the Ingenuity
    /// fighter who is
    /// no longer standing there.
    /// </summary>
    sealed class Decoy
    {
        public Node3D Node = null!;
        public Vector3 Vel;
        public float Left;

        /// <summary>Who sent it. Credited with whatever it kills, and spared its blast.</summary>
        public Pawn? Owner;
    }

    readonly List<Decoy> decoys = new();

    public const float DecoyDuration = 5f;
    public const float DecoySpeed = 5.5f;

    /// <summary>
    /// What a decoy is worth when it goes off. A tank shell, as asked for.
    ///
    /// Above the heaviest thing on the floor, because a decoy is far harder to land than a grenade:
    /// it walks in a straight line at a fixed speed for five seconds, in plain sight, and anyone who
    /// reads it simply steps away. The payoff has to justify being ignorable.
    /// </summary>
    ///
    /// Four times over, both halves. At 150 in a nine-metre circle the payoff still did not
    /// justify how ignorable the thing is — reading a decoy and stepping away cost one sidestep,
    /// so the bomb half of the bluff was never a real threat and Ingenuity were back to owning a
    /// lie nobody had to respect. At 600 across thirty-six metres, walking away is a commitment:
    /// the radius is most of a room, so "step aside" becomes "leave", and leaving is exactly the
    /// concession the ability is asking a player to make.
    public const float DecoyBlastDamage = 600f;
    public const float DecoyBlastRadius = 36f;

    /// <summary>Rounds that passed through a phasing Custodian. Reported by the harness.</summary>
    public int PhasedShots;

    /// <summary>Live decoys, so the harness can prove one was actually left behind.</summary>
    public int DecoyCount => decoys.Count;

    // ---- grapple line ----
    //
    // One stretched box per pawn, shown only while that pawn is being winched in. A grapple with
    // no visible line is a teleport with a wind-up: the line is what tells everyone else what just
    // happened, and it is the only cue the person being pulled has for where they are going.

    readonly Dictionary<Pawn, MeshInstance3D> grappleLines = new();

    void StepGrappleLines()
    {
        if (!Visuals) return;

        foreach (var pawn in Pawns)
        {
            grappleLines.TryGetValue(pawn, out var line);

            if (pawn.GrappleAnchor is not { } anchor || !pawn.Alive)
            {
                if (line != null) line.Visible = false;
                continue;
            }

            if (line == null)
            {
                line = new MeshInstance3D
                {
                    Mesh = new BoxMesh { Size = new Vector3(0.07f, 0.07f, 1f) },
                    MaterialOverride = Graphics.Hot(new Color(0.92f, 0.95f, 0.5f), 2.4f),
                };
                AddChild(line);
                grappleLines[pawn] = line;
            }

            // From the hand rather than the feet, so it leaves the gun instead of the floor.
            Vector3 from = pawn.GlobalPosition + Vector3.Up * (pawn.CurrentEyeHeight - 0.25f);
            Vector3 span = anchor - from;
            float len = span.Length();

            line.Visible = len > 0.4f;
            if (!line.Visible) continue;

            line.GlobalPosition = from + span * 0.5f;
            line.LookAt(anchor, MathF.Abs(span.Normalized().Y) > 0.98f ? Vector3.Right : Vector3.Up);
            line.Scale = new Vector3(1f, 1f, len);
        }
    }

    void StepDecoys(float dt)
    {
        for (int i = decoys.Count - 1; i >= 0; i--)
        {
            var d = decoys[i];
            d.Left -= dt;

            if (d.Left <= 0f)
            {
                // It goes off.
                //
                // A decoy that simply expired was a five-second lie with no teeth: people learned
                // to ignore them, at which point they stopped being a lie at all. One that
                // detonates like a tank shell means every double has to be treated as either a
                // trick or a bomb, and you cannot tell which. That it is a *copy of a person*,
                // made to be spent, is Ingenuity's whole argument stated as a mechanic.
                Vector3 at = d.Node.GlobalPosition + Vector3.Up * 0.9f;

                if (d.Owner is { } owner)
                    Blast(owner, at, DecoyBlastDamage, DecoyBlastRadius, hurtSelf: false);

                if (Visuals)
                {
                    Impact.Death(this, at, Factions.Ingenuity.Tint);
                    Impact.DeathRing(this, d.Node.GlobalPosition, Factions.Ingenuity.Tint);
                    Sfx.PlayAt(Sound.Death, at);
                }

                d.Node.QueueFree();
                decoys.RemoveAt(i);
                continue;
            }

            d.Node.GlobalPosition += d.Vel * dt;
        }
    }

    // ---- portals ----

    /// <summary>One gate. Two of them, linked, are what the portal gun is for.</summary>
    sealed class Portal
    {
        /// <summary>Where a pawn is put down when they come out of this one.</summary>
        public Vector3 At;

        /// <summary>The surface normal it was planted on — the direction you emerge.</summary>
        public Vector3 Out;

        public float Age;
        public Node3D? Node;

        /// <summary>Who planted it. Credited for anything that dies coming out of it.</summary>
        public Pawn? Owner;
    }

    readonly List<Portal> portals = new();
    readonly Dictionary<Pawn, float> portalLock = new();

    /// <summary>Seconds a gate stands before it closes on its own.</summary>
    public const float PortalLifetime = 30f;

    /// <summary>How close you have to be to be taken through.</summary>
    public const float PortalReach = 1.7f;

    /// <summary>
    /// Seconds before the same pawn can use a gate again.
    ///
    /// Applied on arrival, not on entry. Without it you land inside the exit gate, which is by
    /// definition within reach of a portal, and get sent straight back — an infinite loop that
    /// resolves at whatever frame rate the machine happens to be running at.
    /// </summary>
    const float PortalCooldown = 1.1f;

    /// <summary>Gates currently standing. Never more than two.</summary>
    public int PortalCount => portals.Count;

    /// <summary>Trips through a gate. Exists so the harness can prove they actually carry anyone.</summary>
    public int PortalTrips { get; private set; }

    /// <summary>
    /// Plant a gate where a portal round landed.
    ///
    /// Held off the surface along its own normal, because the point of a gate is that you arrive
    /// standing next to the wall rather than inside it. If nothing fits at any of the offsets tried
    /// the shot simply fails — a gate you cannot come out of is worse than no gate.
    /// </summary>
    void PlantPortal(Vector3 where, Vector3 normal, Pawn owner)
    {
        if (normal.LengthSquared() < 0.001f) normal = Vector3.Up;
        normal = normal.Normalized();

        Vector3? spot = null;

        foreach (float out_ in new[] { 1.3f, 1.9f, 2.6f })
        {
            var at = where + normal * out_;
            if (!Arena.InPlay(at)) continue;
            if (!PawnFitsAt(at)) continue;
            spot = at;
            break;
        }

        if (spot is not { } at2)
        {
            if (Visuals) Sfx.PlayAt(Sound.MenuBack, where, -4f, 0.7f);
            return;
        }

        // Two at a time. A third closes the oldest, which is also how you move a gate: shoot a new
        // one and the one you have finished with goes.
        while (portals.Count >= 2)
        {
            ClosePortal(portals[0]);
            portals.RemoveAt(0);
        }

        var portal = new Portal { At = at2, Out = normal, Owner = owner };

        if (Visuals)
        {
            var node = new Node3D();
            AddChild(node);
            node.GlobalPosition = where + normal * 0.25f;

            // Oriented with the ring's axis along the surface normal, so a gate on a wall stands
            // up and a gate on the floor lies flat.
            Vector3 up = normal;
            Vector3 reference = MathF.Abs(up.Y) > 0.9f ? Vector3.Forward : Vector3.Up;
            Vector3 rx = reference.Cross(up).Normalized();
            node.Basis = new Basis(rx, up, up.Cross(rx));

            // The two gates are different colours, or there is no telling which end you are at.
            Color tint = portals.Count == 0
                ? new Color(0.98f, 0.60f, 0.18f)
                : new Color(0.28f, 0.72f, 0.98f);

            node.AddChild(new MeshInstance3D
            {
                Mesh = new TorusMesh { InnerRadius = 1.05f, OuterRadius = 1.45f },
                MaterialOverride = Graphics.Hot(tint, 3.4f),
            });

            // A dim disc in the mouth. Without something filling the ring a gate on a wall is a
            // circle you can see straight through, which reads as decoration rather than a way in.
            node.AddChild(new MeshInstance3D
            {
                Mesh = new CylinderMesh { TopRadius = 1.05f, BottomRadius = 1.05f, Height = 0.06f },
                MaterialOverride = Graphics.Hot(tint.Darkened(0.35f), 0.9f),
            });

            portal.Node = node;
            Sfx.PlayAt(Sound.Respawn, at2, -2f, 1.35f);
        }

        portals.Add(portal);
        _ = owner;
    }

    void ClosePortal(Portal p)
    {
        if (p.Node == null) return;
        if (Visuals) Impact.Hit(this, p.Node.GlobalPosition, p.Out, new Color(0.4f, 0.8f, 1f));
        p.Node.QueueFree();
    }

    /// <summary>Whether a standing pawn would fit at a point, with nothing else in the way.</summary>
    bool PawnFitsAt(Vector3 at)
    {
        using var probe = new CapsuleShape3D
        {
            Radius = Pawn.Radius * 0.9f,
            Height = MathF.Max(Pawn.Height * 0.9f, Pawn.Radius * 2.1f),
        };

        using var query = new PhysicsShapeQueryParameters3D
        {
            Shape = probe,
            Transform = new Transform3D(Basis.Identity, at + Vector3.Up * (Pawn.Height * 0.5f)),
        };

        return GetWorld3D().DirectSpaceState.IntersectShape(query, 1).Count == 0;
    }

    void StepPortals(float dt)
    {
        for (int i = portals.Count - 1; i >= 0; i--)
        {
            portals[i].Age += dt;
            if (portals[i].Age < PortalLifetime) continue;

            ClosePortal(portals[i]);
            portals.RemoveAt(i);
        }

        // A gate on its own does nothing. This is deliberate: the first shot is a commitment you
        // can be punished for, and the pair is the payoff.
        if (portals.Count < 2) return;

        var a = portals[0];
        var b = portals[1];

        foreach (var pawn in Pawns)
        {
            portalLock.TryGetValue(pawn, out float locked);
            if (locked > 0f) { portalLock[pawn] = locked - dt; continue; }

            if (!pawn.Alive || pawn.InVehicle) continue;

            // Measured against the pawn's middle rather than their feet, so a gate on a wall
            // catches someone walking into it at chest height.
            Vector3 mid = pawn.GlobalPosition + Vector3.Up * (pawn.CurrentHeight * 0.5f);

            Portal? entered = null;
            if (mid.DistanceTo(a.At) < PortalReach) entered = a;
            else if (mid.DistanceTo(b.At) < PortalReach) entered = b;

            if (entered == null) continue;

            var exit = entered == a ? b : a;
            if (!PawnFitsAt(exit.At)) continue;

            TakeThroughPortal(pawn, exit);
        }
    }

    /// <summary>
    /// Put a pawn down at the far gate.
    ///
    /// Their look direction is left exactly as it was. Spinning the camera to face out of the exit
    /// is the more "correct" answer and it is also the one that makes people motion sick in
    /// splitscreen — you did not turn, so the view should not turn. The push along the exit normal
    /// is what carries them clear of the gate they just arrived in.
    /// </summary>
    void TakeThroughPortal(Pawn pawn, Portal exit)
    {
        float speed = MathF.Max(new Vector2(pawn.Velocity.X, pawn.Velocity.Z).Length(), 5f);

        pawn.GlobalPosition = exit.At;
        pawn.Velocity = exit.Out * speed;
        pawn.ApplyKnockback(exit.Out * 3f);

        portalLock[pawn] = PortalCooldown;
        PortalTrips++;

        // Remember who sent them, for the two seconds in which whatever happens next is that
        // person's doing. See PortalCreditFor.
        if (exit.Owner is { } layer) portalCredit[pawn] = (layer, Elapsed);

        if (Visuals)
        {
            Impact.Hit(this, exit.At + Vector3.Up * 0.9f, exit.Out, new Color(0.35f, 0.85f, 1f));
            Sfx.PlayAt(Sound.Dash, exit.At, -2f, 1.5f);
        }
    }

    // Test hooks. Portals are planted by a projectile landing on a surface, which a headless
    // harness cannot arrange reliably — the round has to actually hit something, and where it hits
    // depends on the geometry in front of whichever spawn the probe happened to start on.
    public void PlantPortalForTest(Vector3 where, Vector3 normal) => PlantPortal(where, normal, Pawns[0]);
    public void StepPortalsForTest(float dt) => StepPortals(dt);

    /// <summary>Advance rounds in flight, for the harness. Deflection lives inside this sweep.</summary>
    public void StepShotsForTest(float dt) => StepShots(dt);

    /// <summary>Rounds still in the air. A fused one is only really tested by watching it wait.</summary>
    public int ShotsInFlight => shots.Count;

    /// <summary>Put a round in the air at a known place and heading, for the harness.</summary>
    public void InjectShotForTest(Pawn owner, Vector3 at, Vector3 vel, float range = 200f)
        => shots.Add(new Shot { Pos = at, Vel = vel, RangeLeft = range, Owner = owner, Damage = 1f });

    /// <summary>
    /// The same, but carrying a weapon's seeking behaviour, so the harness can watch one turn.
    ///
    /// Deliberately does not set BlastDamage. What is under test is the steering, and a round that
    /// detonates the moment it arrives takes itself out of the air before the check can read where
    /// it was going.
    /// </summary>
    public void InjectSeekerForTest(Pawn owner, Vector3 at, Vector3 vel, WeaponDef gun)
        => shots.Add(new Shot
        {
            Pos = at,
            Vel = vel,
            RangeLeft = gun.Range,
            Owner = owner,
            Damage = gun.Damage,
            SeekTurnRate = gun.SeekTurnRate,
            SeekRange = gun.SeekRange,
            SeekCone = Mathf.DegToRad(gun.SeekConeDeg),
        });

    /// <summary>Where the nth round in flight is. Throws if there is no nth round.</summary>
    public Vector3 ShotPositionForTest(int i) => shots[i].Pos;

    /// <summary>Which way the nth round is travelling. Throws if there is no nth round.</summary>
    public Vector3 ShotVelocityForTest(int i) => shots[i].Vel;

    /// <summary>Clear the air, so a probe starts from a known number of rounds.</summary>
    public void ClearShotsForTest()
    {
        foreach (var s in shots) s.Mesh?.QueueFree();
        shots.Clear();
    }

    /// <summary>Book a death with nobody to blame, the way a fall or a pit does.</summary>
    public void AwardEnvironmentalDeathForTest(Pawn victim) => AwardKill(victim, victim);

    /// <summary>Run the match clock on without simulating anything, to age a credit window.</summary>
    public void AdvanceClockForTest(float dt) => Elapsed += dt;
    public void ClearPortalsForTest() => ClearPortals();

    /// <summary>Where the nth gate puts you down. Throws if there is no nth gate.</summary>
    public Vector3 PortalSpot(int i) => portals[i].At;

    /// <summary>Closes every gate. Used between elimination rounds and when a match restarts.</summary>
    void ClearPortals()
    {
        foreach (var p in portals) ClosePortal(p);
        portals.Clear();
        portalLock.Clear();
    }

    void LeaveDecoy(Pawn user) => LeaveDecoyAlong(user, 0f);

    /// <summary>
    /// One decoy, thrown off at an angle from the user's heading.
    ///
    /// Ingenuity's special leaves a single one walking straight on; the Thousand leaves eight in a
    /// ring. Same body, same lifetime, same lie — only the direction differs.
    /// </summary>
    void LeaveDecoyAlong(Pawn user, float offset)
    {
        if (!Visuals) return;      // headless: nothing to look at, and nothing shoots at it

        var node = new Node3D();
        AddChild(node);
        node.GlobalPosition = user.GlobalPosition;
        node.Rotation = new Vector3(0f, -user.Facing, 0f);

        // The user's own body, not a coloured box.
        //
        // It was two glowing boxes, on the reasoning that a decoy should be "obviously false close
        // up". That reasoning was wrong and the player said so: a lie that announces itself is not
        // a lie, and it made Ingenuity's whole special into a distraction nobody was distracted by.
        // A double that looks exactly like you is the entire ability — and now that it detonates,
        // being unable to tell at a glance is the threat as well as the bluff.
        //
        // Falls back to the boxes when there is no model to copy, which is the same fallback the
        // pawns themselves use.
        string body = user.Wearing.Model.Length > 0 ? user.Wearing.Model : user.Faction.Model;

        if (CharacterModels.Instance(body, out float sourceHeight) is { } copy)
        {
            node.AddChild(copy);
            copy.Scale = Vector3.One * (Pawn.Height / sourceHeight);

            // Same correction the pawn rig applies: the exports face -Z and a zero heading looks
            // down +X, so the model is turned to agree with the heading rather than the other way.
            copy.RotationDegrees = new Vector3(0f, -90f, 0f);
        }
        else
        {
            var mat = Graphics.Hot(user.Faction.Tint, 1.1f);

            node.AddChild(new MeshInstance3D
            {
                Mesh = new BoxMesh { Size = new Vector3(0.52f, 1.1f, 0.62f) },
                MaterialOverride = mat,
                Position = new Vector3(0f, 0.85f, 0f),
            });

            node.AddChild(new MeshInstance3D
            {
                Mesh = new BoxMesh { Size = new Vector3(0.38f, 0.38f, 0.38f) },
                MaterialOverride = mat,
                Position = new Vector3(0f, 1.6f, 0f),
            });
        }

        float heading = user.Facing + offset;
        var forward = new Vector3(MathF.Cos(heading), 0f, MathF.Sin(heading));

        node.Rotation = new Vector3(0f, -heading, 0f);
        decoys.Add(new Decoy
        {
            Node = node, Vel = forward * DecoySpeed, Left = DecoyDuration, Owner = user,
        });
    }

    // ---- Revelation ----

    /// <summary>
    /// Light up every enemy for the caster's side.
    ///
    /// The mark lives on the revealed pawn rather than on the caster, so it survives the Custodian
    /// dying a second later — the archive outlives the archivist.
    /// </summary>
    void Reveal(Pawn user)
    {
        foreach (var p in Pawns)
        {
            if (!p.Alive || SameSide(user, p)) continue;
            p.RevealedFor = user.Faction.SpecialDuration;
        }
    }

    public void UseSpecial(Pawn user)
    {
        var kind = user.Faction.Special;

        SpecialsUsed.TryGetValue(kind, out int n);
        SpecialsUsed[kind] = n + 1;

        switch (kind)
        {
            case SpecialKind.SecondWind:
            {
                // Ten seconds of being much harder to keep up with.
                //
                // This used to pay back banked damage as healing, and it was the least visible
                // ability in the game: worth nothing at full health, and at low health worth a
                // chunk of a bar you could not watch move. The buff itself is applied by the timed
                // path below — all this has to do is announce it.
                if (Visuals)
                {
                    Impact.DeathRing(this, user.GlobalPosition, user.Faction.Tint);
                    Impact.Hit(this, user.GlobalPosition + Vector3.Up * 0.9f,
                               Vector3.Up, user.Faction.Tint);
                }
                break;
            }

            case SpecialKind.Revelation:
                Reveal(user);
                break;

            case SpecialKind.Bloom:
                PlantBloom(user);
                break;

            case SpecialKind.Understudy:
                LeaveDecoy(user);
                break;
        }

        if (Visuals) Sfx.PlayAt(Sound.Dash, user.GlobalPosition, pitch: 0.7f);
    }

    /// <summary>
    /// The class ability, on its own button and its own cooldown.
    ///
    /// These four were written when the special belonged to the class, and were orphaned when it
    /// moved to the faction: fully implemented, named, blurbed, tuned, wired into the bot brain and
    /// into five separate effect hooks, and unreachable. Nothing could fire them for weeks.
    ///
    /// They belong to the class rather than the faction because all four are about *how you shoot*,
    /// which is the line this game already draws — your faction is what you are, your class is how
    /// you fight. Before this, class was nothing but a stat sheet.
    /// </summary>
    public void UseClassAbility(Pawn user)
    {
        var kind = user.Class.Special;

        SpecialsUsed.TryGetValue(kind, out int n);
        SpecialsUsed[kind] = n + 1;
        ClassAbilitiesUsed++;

        switch (kind)
        {
            case SpecialKind.Shockwave:
                Blast(user, user.GlobalPosition, user.Class.BlastDamage, user.Class.BlastRadius,
                      hurtSelf: false);
                if (Visuals) Impact.Death(this, user.GlobalPosition, user.Tint);
                break;

            case SpecialKind.Frag:
                ThrowGrenade(user);
                break;

            case SpecialKind.Barrier:
                PlantBarrier(user);
                break;

            // Overdrive, Focus and Final Act are pure timers; the pawn has already started the
            // clock, and everything they do is read off it elsewhere.
            default:
                break;
        }

        if (Visuals) Sfx.PlayAt(Sound.Dash, user.GlobalPosition, pitch: 1.25f);
    }

    /// <summary>Class abilities fired. Exists so the harness can prove they are reachable at all.</summary>
    public int ClassAbilitiesUsed { get; private set; }

    void ThrowGrenade(Pawn user)
    {
        // Lobbed along the aim with an upward bias, so it arcs over cover rather than travelling
        // flat like a bullet — the whole point of the ability.
        Vector3 dir = (user.AimDir + Vector3.Up * 0.45f).Normalized();

        var g = new Grenade
        {
            Pos = user.Eye + dir * (Pawn.Radius + 0.3f),
            Vel = dir * 24f,
            Fuse = 1.5f,
            Owner = user,
        };

        if (Visuals)
        {
            var mesh = new MeshInstance3D
            {
                Mesh = new BoxMesh { Size = new Vector3(0.28f, 0.28f, 0.28f) },
                MaterialOverride = Graphics.Hot(user.Tint.Lightened(0.3f), 1.8f),
            };
            AddChild(mesh);
            mesh.GlobalPosition = g.Pos;
            g.Mesh = mesh;
        }

        grenades.Add(g);
    }

    void StepGrenades(float dt)
    {
        var space = GetWorld3D().DirectSpaceState;

        for (int i = grenades.Count - 1; i >= 0; i--)
        {
            var g = grenades[i];

            g.Vel += new Vector3(0f, -24f * dt, 0f);
            Vector3 to = g.Pos + g.Vel * dt;
            g.Fuse -= dt;

            using var query = PhysicsRayQueryParameters3D.Create(g.Pos, to);
            query.Exclude = new Godot.Collections.Array<Rid> { g.Owner.GetRid() };
            var hit = space.IntersectRay(query);

            bool detonate = g.Fuse <= 0f;

            if (hit.Count > 0)
            {
                // Detonates on any contact rather than bouncing: a grenade that skitters off into
                // a corner is frustrating in an arena this size.
                g.Pos = hit["position"].AsVector3();
                detonate = true;
            }
            else
            {
                g.Pos = to;
                if (g.Mesh != null) g.Mesh.GlobalPosition = to;
            }

            if (!detonate) continue;

            Blast(g.Owner, g.Pos, g.Owner.Class.BlastDamage, g.Owner.Class.BlastRadius, hurtSelf: true);
            if (Visuals) Impact.Death(this, g.Pos, g.Owner.Tint);

            g.Mesh?.QueueFree();
            grenades.RemoveAt(i);
        }
    }

    /// <summary>
    /// Radial damage and shove. Damage falls off to nothing at the edge, so positioning matters
    /// rather than the blast being a flat circle of death.
    /// </summary>
    /// <summary>
    /// Put a tracer where it is and point it the way it is going.
    ///
    /// Tracers used to be moved but never turned, so every round in the game — from every gun, in
    /// every direction — drew a streak lying along world X. A shot fired sideways looked like a
    /// bar of soap sliding through the air.
    /// </summary>
    static void PointAlong(Node3D mesh, Vector3 at, Vector3 vel)
    {
        mesh.GlobalPosition = at;

        // LookAt aims local -Z, which is why the tracer boxes are built long on Z. It throws if the
        // direction is parallel to up, so a round fired straight up gets left as it is.
        Vector3 dir = vel.Normalized();
        if (MathF.Abs(dir.Y) > 0.999f) return;

        mesh.LookAt(at + dir, Vector3.Up);
    }

    // ---- destructible walkways ----

    /// <summary>
    /// One walkway that explosives can bring down.
    ///
    /// Only the thin decks of the upper storey qualify, and only explosives hurt them. Letting
    /// small-arms fire erode the map would mean every long match ends in a flat box, and nobody
    /// would ever have chosen to do it — it would just happen. A shell is a decision.
    /// </summary>
    sealed class Breakable
    {
        public int BlockIndex;
        public StaticBody3D Body = null!;
        public CollisionShape3D Shape = null!;
        public MeshInstance3D? Mesh;
        public Vector3 Centre;
        public Vector3 HalfExtents;
        public Color Tint;

        public float MaxHealth = PlatformHealth;
        public float Health = PlatformHealth;
        public float RebuildIn = PlatformRespawnTime;
        public float DownFor = -1f;          // negative while standing
        public bool Down => DownFor >= 0f;
    }

    readonly List<Breakable> breakables = new();

    /// <summary>
    /// What a thin walkway is worth: one well-placed tank shell, or a pair of grenades.
    ///
    /// The floor of the scale rather than the whole of it, now that every wall and platform on the
    /// map can be brought down. A catwalk and a citadel tier being equally fragile would be worse
    /// than nothing being fragile at all — you would stop being able to tell, by looking, whether
    /// the thing in front of you was cover or a suggestion.
    /// </summary>
    public const float PlatformHealth = 120f;

    /// <summary>Ceiling on how tough a single piece of structure can be.</summary>
    public const float StructureHealthCap = 900f;

    /// <summary>
    /// What it takes to bring a piece of structure down, from its size.
    ///
    /// Volume, straight. It gives exactly the reading you want from across a map: a thirty-metre
    /// skybridge goes to one shell, a machine-hall wall takes three or four, and the base tier of
    /// a corner citadel is an project rather than an opportunity. Nobody has to be told the rule —
    /// it is the same rule everything in the physical world follows.
    /// </summary>
    public static float StructureHealth(Vector3 halfExtents)
    {
        float volume = 8f * halfExtents.X * halfExtents.Y * halfExtents.Z;
        return MathU.Clamp(volume, PlatformHealth, StructureHealthCap);
    }

    /// <summary>Seconds before a downed walkway rebuilds itself.</summary>
    public const float PlatformRespawnTime = 30f;

    /// <summary>
    /// How long a downed piece stays down, scaled by how much of it there was.
    ///
    /// This is what keeps a long match from eroding into a flat box now that everything can be
    /// destroyed. The old answer was to make most of the map immortal; the better one is that
    /// nothing stays down, and the arena is a shape that keeps healing rather than one that wears
    /// away. Big structure takes longer, which means blowing out a citadel wall is a play that
    /// lasts long enough to be worth making.
    /// </summary>
    public static float StructureRebuildTime(float maxHealth)
        => PlatformRespawnTime * MathU.Clamp(maxHealth / PlatformHealth, 1f, 2.5f);

    /// <summary>How many pieces of this arena can be blown up. Read by the harness.</summary>
    public int BreakableCount => breakables.Count;

    /// <summary>How many are currently down.</summary>
    public int BreakablesDown
    {
        get
        {
            int n = 0;
            foreach (var b in breakables) if (b.Down) n++;
            return n;
        }
    }

    void BuildBreakables()
    {
        foreach (var (index, body) in Arena.FragileBodies)
        {
            var block = Arena.Blocks[index];
            float health = StructureHealth(block.HalfExtents);

            breakables.Add(new Breakable
            {
                BlockIndex = index,
                Body = body,
                Shape = body.GetChild<CollisionShape3D>(0),
                Mesh = body.GetChildOrNull<MeshInstance3D>(1),
                Centre = block.Centre,
                HalfExtents = block.HalfExtents,
                Tint = block.Tint,
                MaxHealth = health,
                Health = health,
                RebuildIn = StructureRebuildTime(health),
            });
        }
    }

    /// <summary>
    /// Explosive damage to any walkway inside the blast. Called from <see cref="Blast"/> rather
    /// than from the projectile hit test, which is what makes "explosives only" fall out for free.
    /// </summary>
    void DamageBreakables(Vector3 centre, float damage, float radius)
    {
        foreach (var b in breakables)
        {
            if (b.Down) continue;

            // Distance to the box, not to its centre. A thirty-metre skybridge measured centre to
            // centre would be untouchable at either end.
            float d = NearestPointOn(b.Centre, b.HalfExtents, centre).DistanceTo(centre);
            if (d > radius) continue;

            b.Health -= damage * (1f - MathU.Clamp01(d / radius));
            if (b.Health > 0f)
            {
                // Darkens as it takes damage, so a walkway about to go is readable before it does.
                if (b.Mesh?.MaterialOverride is StandardMaterial3D mat)
                    mat.AlbedoColor = b.Tint.Darkened(0.45f * (1f - b.Health / b.MaxHealth));
                continue;
            }

            DropBreakable(b);
        }
    }

    void DropBreakable(Breakable b)
    {
        b.DownFor = 0f;

        // Deferred because this runs inside the physics step, and reshaping the world mid-step is
        // how you get a body that is half there for a frame. ProcessMode does not do it: that
        // gates the process callbacks a StaticBody3D does not use, and the collider would have
        // stayed solid — an invisible walkway you could still stand on.
        b.Shape.SetDeferred(CollisionShape3D.PropertyName.Disabled, true);
        b.Body.Visible = false;

        // Anyone standing on it is now standing on nothing. Gravity does the rest — no push is
        // needed and a push would look like a launch rather than a floor giving way.
        Nav.SetBlockActive(b.BlockIndex, false);

        if (Visuals) Impact.Death(this, b.Centre, b.Tint);
    }

    void RaiseBreakable(Breakable b)
    {
        b.DownFor = -1f;
        b.Health = b.MaxHealth;
        b.Shape.SetDeferred(CollisionShape3D.PropertyName.Disabled, false);
        b.Body.Visible = true;

        Nav.SetBlockActive(b.BlockIndex, true);

        if (b.Mesh?.MaterialOverride is StandardMaterial3D mat) mat.AlbedoColor = b.Tint;
    }

    void StepBreakables(float dt)
    {
        foreach (var b in breakables)
        {
            if (!b.Down) continue;

            b.DownFor += dt;
            if (b.DownFor >= b.RebuildIn) RaiseBreakable(b);
        }
    }

    /// <summary>Sweep a wall through the pawns once, for the harness.</summary>
    public void ShoveAlongForTest(MovingPlatformDef def, Vector3 was, Vector3 now, float dt)
        => ShoveAlong(def, was, now, dt);

    /// <summary>Apply the play boundary once, for the harness.</summary>
    public void BoundsSweepForTest()
    {
        foreach (var p in Pawns) CheckOutOfBounds(p);
    }

    /// <summary>
    /// Apply the vehicle kill plane once, for the harness. The same two rules the vehicle step
    /// runs every tick: below the plane is destroyed, and a wreck settles rather than falling on.
    /// </summary>
    public void KillPlaneSweepForTest()
    {
        foreach (var v in VehicleList)
        {
            if (v.Alive && v.GlobalPosition.Y < Arena.KillPlaneY) v.TakeDamage(v.Def.Health);

            if (!v.Alive && v.GlobalPosition.Y < Arena.KillPlaneY)
            {
                v.GlobalPosition = v.GlobalPosition with { Y = Arena.KillPlaneY };
                v.Velocity = Vector3.Zero;
            }
        }
    }

    /// <summary>
    /// The brain behind a bot pawn, so the harness can interrogate a decision directly rather than
    /// waiting for one to happen by chance inside a twenty-second window.
    /// </summary>
    public BotBrain? BrainForTest(int pawnIndex)
        => pawnIndex >= 0 && pawnIndex < brains.Count ? brains[pawnIndex] : null;

    // Deliberately narrow hooks for the harness rather than making the whole Breakable list
    // public: the test needs to bring one down on command, and nothing else.

    /// <summary>
    /// A destructible walkway that bots actually route over, or −1.
    ///
    /// Not simply the first one: some walkways carry no navigation nodes at all — too high to be
    /// reachable, or roofed by the deck above — and testing against one of those proves nothing,
    /// which is exactly the false pass the first version of this gave.
    /// </summary>
    public int FirstRoutableBreakableForTest()
    {
        foreach (var b in breakables)
            if (Nav.NodesOn(b.BlockIndex) > 0) return b.BlockIndex;
        return -1;
    }

    /// <summary>How many destructible walkways carry navigation nodes. Reported by the harness.</summary>
    public int RoutableBreakableCount()
    {
        int n = 0;
        foreach (var b in breakables) if (Nav.NodesOn(b.BlockIndex) > 0) n++;
        return n;
    }

    public void DropBreakableForTest(int blockIndex)
    {
        foreach (var b in breakables) if (b.BlockIndex == blockIndex) { DropBreakable(b); return; }
    }

    public void RaiseBreakableForTest(int blockIndex)
    {
        foreach (var b in breakables) if (b.BlockIndex == blockIndex) { RaiseBreakable(b); return; }
    }

    /// <summary>
    /// Find the heaviest standing piece of this arena, for the harness.
    ///
    /// The point of testing against the heaviest rather than the nearest is that it is the case
    /// most likely to be quietly impossible: a hundred and twenty hit points goes down to anything,
    /// and nine hundred needs a real number of shells for the whole feature to be worth having.
    /// </summary>
    public (int Index, Vector3 Centre, float Health) HeaviestBreakableForTest()
    {
        int index = -1;
        Vector3 at = Vector3.Zero;
        float best = -1f;

        foreach (var b in breakables)
        {
            if (b.Down || b.MaxHealth <= best) continue;
            best = b.MaxHealth;
            index = b.BlockIndex;
            at = b.Centre;
        }

        return (index, at, best);
    }

    /// <summary>Health left in one standing piece, or -1 if it is down or unknown.</summary>
    public float BreakableHealthForTest(int blockIndex)
    {
        foreach (var b in breakables)
            if (b.BlockIndex == blockIndex) return b.Down ? -1f : b.Health;
        return -1f;
    }

    /// <summary>Set one off exactly where the caller says, through the real blast path.</summary>
    public void BlastForTest(Pawn owner, Vector3 centre, float damage, float radius)
        => Blast(owner, centre, damage, radius, hurtSelf: false);

    /// <summary>Advance the rebuild clocks once, for the harness.</summary>
    public void StepBreakablesForTest(float dt) => StepBreakables(dt);

    /// <summary>Age the planted walls once, for the harness.</summary>
    public void StepBarriersForTest(float dt) => StepBarriers(dt);

    /// <summary>Closest point on an axis-aligned box to a point in space.</summary>
    static Vector3 NearestPointOn(Vector3 centre, Vector3 halfExtents, Vector3 to) => new(
        MathU.Clamp(to.X, centre.X - halfExtents.X, centre.X + halfExtents.X),
        MathU.Clamp(to.Y, centre.Y - halfExtents.Y, centre.Y + halfExtents.Y),
        MathU.Clamp(to.Z, centre.Z - halfExtents.Z, centre.Z + halfExtents.Z));

    /// <summary>A hull destroyed by damage: it goes up, and being next to it when it does hurts.</summary>
    void WreckVehicle(Vehicle rig, Pawn killer)
    {
        // Worth more than the kill it usually contains. A hull is a problem the whole team has, and
        // somebody had to stop shooting at people to deal with it.
        killer.AwardPoints(BattlePoints.VehicleKill);

        // The wreck itself is a hazard. A tank going up beside you should be an event, not a
        // silent change of colour.
        Blast(killer, rig.GlobalPosition + Vector3.Up * rig.Def.HalfExtents.Y,
              VehicleWreckDamage, VehicleWreckRadius, hurtSelf: true);

        if (Visuals)
        {
            Impact.VehicleWreck(this, rig.GlobalPosition + Vector3.Up * rig.Def.HalfExtents.Y, rig.Def.Tint);
            Sfx.PlayAt(Sound.Death, rig.GlobalPosition);
        }
    }

    /// <summary>
    /// What a vehicle's own gun is worth against walls and walkways, over what it does to people.
    ///
    /// A tank shell was already the single best thing in the game for opening a building up, and
    /// it still took three or four to get through a machine-hall wall — long enough that nobody
    /// did it on purpose, because standing still and reloading twice in the open is how a tank
    /// dies. At three times, one shell takes a wall and a citadel tier is four rather than
    /// thirteen: demolition becomes a thing you drive a tank somewhere to do.
    ///
    /// Applied to structure only. What the shell does to people is untouched — this is about
    /// making the cannon the answer to the *map*, not making it a better anti-personnel weapon.
    ///
    /// Bounded by the invariant the arena depends on: at this multiple the cannon's 135 comes to
    /// 405, still well under <see cref="StructureHealthCap"/>, so nothing in the game drops heavy
    /// structure in a single hit. Raising it past that would flatten the biggest pieces on every
    /// map and there would be nothing left to fight over.
    /// </summary>
    public const float VehicleStructureMultiplier = 3f;

    /// <summary>
    /// Needles that have to be in one person at once before they go off together.
    ///
    /// Seven, as in Halo, and the number is the weapon. Low enough that a committed magazine gets
    /// there against someone who stands still, high enough that it never happens by accident to
    /// whoever you last glanced at.
    /// </summary>
    public const int SupercombineNeedles = 7;

    /// <summary>How long a needle stays in before it works loose. Refreshed by each new one.</summary>
    public const float SupercombineWindow = 3.2f;

    /// <summary>What the seven go off for, and how far it reaches.</summary>
    public const float SupercombineDamage = 190f;
    public const float SupercombineRadius = 4.5f;

    public const float VehicleWreckDamage = 90f;
    public const float VehicleWreckRadius = 9f;

    /// <summary>Seconds before a wreck is cleared and a fresh hull is parked back on its spawn.</summary>
    public const float VehicleRespawnTime = 22f;

    void Blast(Pawn owner, Vector3 centre, float damage, float radius, bool hurtSelf,
               float structureScale = 1f)
    {
        DamageBreakables(centre, damage * structureScale, radius);

        // Vehicles catch the blast too. Without this a shell landing against a hull did nothing to
        // it unless the ray happened to strike the hull directly, so splash weapons were the one
        // thing that could not threaten a tank.
        foreach (var rig in VehicleList)
        {
            if (!rig.Alive) continue;

            // Never the hull the round came out of. The crew are shielded from their own blast a
            // few lines below, but the vehicle was not: firing the cannon at anything within the
            // blast radius — a wall, the floor, a doorway — quietly wrecked your own tank.
            if (owner.Riding == rig) continue;

            float d = rig.GlobalPosition.DistanceTo(centre);
            if (d > radius) continue;

            float before = rig.Health;
            bool wrecked = rig.TakeDamage(damage * (1f - MathU.Clamp01(d / radius)));
            DamageDealt += before - rig.Health;

            if (wrecked) WreckVehicle(rig, owner);
        }

        foreach (var p in Pawns)
        {
            if (!p.Alive) continue;
            if (p == owner && !hurtSelf) continue;

            // The hull takes it instead. A crew shielded by four metres of armour should not be
            // hurt by a blast the vehicle itself already absorbed above — and without this a tank
            // firing its own shell at anything nearby wounded its own driver, which made the
            // cannon unusable at exactly the range it should be strongest.
            if (p.InVehicle) continue;

            float dist = p.GlobalPosition.DistanceTo(centre);
            if (dist > radius) continue;

            float falloff = 1f - MathU.Clamp01(dist / radius);

            Vector3 away = p.GlobalPosition - centre;
            away.Y = 0f;
            if (away.LengthSquared() < 0.01f) away = Vector3.Forward;
            away = away.Normalized();

            p.ApplyKnockback(away * (14f * falloff) + Vector3.Up * (7f * falloff));

            float dealt = damage * falloff;
            float before = p.Health;
            bool killed = p.TakeDamage(dealt);
            DamageDealt += before - p.Health;

            if (p.Health < before)
            {
                p.DamageFlash = Pawn.DamageFlashTime;
                p.LastAttacker = centre;
                if (p != owner) owner.HitConfirm = Pawn.HitConfirmTime;
            }

            if (killed) AwardKill(owner, p);
        }
    }

    void TrackMovement()
    {
        foreach (var p in Pawns)
        {
            if (lastSeenAt.TryGetValue(p, out var was) && p.Alive && was.DistanceTo(p.GlobalPosition) < 12f)
                DistanceWalked += was.DistanceTo(p.GlobalPosition);

            lastSeenAt[p] = p.GlobalPosition;
        }
    }


    /// <summary>
    /// How hard the rope hauls something living. Metres a second, straight at the shooter.
    ///
    /// Well above running speed, so being hooked is not something you walk out of — the whole point
    /// is that it takes the decision away from them for a moment. Below dash speed, so a dash is
    /// still the answer if you have one.
    /// </summary>
    public const float GrappleHaulSpeed = 26f;

    /// <summary>What a hull gets instead. Heavier than a body, and it stays on its wheels.</summary>
    public const float GrappleHaulVehicle = 12f;

    /// <summary>Closer than this and the rope has nothing useful to do.</summary>
    public const float GrappleHaulMin = 4f;

    /// <summary>Rope hits that dragged something rather than moving the shooter. For the harness.</summary>
    public int GrappleHauls;

    /// <summary>
    /// Decide which end of the rope moves.
    ///
    /// Hitting the world pulls you to it — that is the grapple as it has always been, and it is a
    /// movement tool. Hitting a *person* or a *hull* pulls them to you, which makes the same weapon
    /// a completely different thing: it is the only way in the game to take someone out of the
    /// position they chose and put them somewhere you chose. A sniper on a tower, a juggernaut
    /// holding a doorway, a tank sat on a command post — all of them have an answer now, and the
    /// answer is a gun somebody has to walk across the map to find.
    ///
    /// The rope does no damage on its own. What it does is arrange a fight on your terms, and if
    /// you cannot win that fight you have hauled an angry Sinew into your lap.
    /// </summary>
    void ResolveGrapple(Shot s, Vector3 where, GodotObject? struck)
    {
        var shooter = s.Owner;

        // A body. Never a teammate — hauling your own side around is at best a nuisance and at
        // worst a way to throw them into a pit, and neither is a thing to build a weapon on.
        if (struck is Pawn target && target.Alive && target != shooter
            && !(Settings.Def.Teams && SameTeam(shooter, target)))
        {
            Vector3 pull = shooter.GlobalPosition - target.GlobalPosition;
            if (pull.Length() < GrappleHaulMin) return;

            // Aimed slightly above the shooter's feet, so a haul across a gap arrives on the ledge
            // rather than into the wall below it.
            pull = (pull + Vector3.Up * 1.2f).Normalized();

            target.ApplyKnockback(pull * GrappleHaulSpeed);
            target.DamageFlash = Pawn.DamageFlashTime;
            target.LastAttacker = shooter.GlobalPosition;
            shooter.HitConfirm = Pawn.HitConfirmTime;
            GrappleHauls++;

            if (Visuals)
            {
                Impact.Hit(this, where, -s.Vel.Normalized(), new Color(0.9f, 0.95f, 0.45f));
                Sfx.PlayAt(Sound.Dash, target.GlobalPosition, -2f, 0.7f);
            }

            return;
        }

        // A hull. Slower, and flat: dragging a tank into the air would be funny once and wrong
        // every time after that.
        if (struck is Vehicle rig && rig.Alive)
        {
            Vector3 pull = shooter.GlobalPosition - rig.GlobalPosition;
            pull.Y = 0f;

            if (pull.Length() < GrappleHaulMin) return;

            rig.Velocity += pull.Normalized() * GrappleHaulVehicle;
            shooter.HitConfirm = Pawn.HitConfirmTime;
            GrappleHauls++;

            if (Visuals) Impact.Hit(this, where, -s.Vel.Normalized(), new Color(0.9f, 0.95f, 0.45f));
            return;
        }

        // Anything else is scenery, and the rope works the way it always did — but only if the
        // anchor is somewhere inside the world.
        //
        // At eighty-five metres this could not really go wrong. At four hundred and twenty-five it
        // can anchor on the far face of the perimeter wall, and the winch pulls in a straight line
        // at fifty-five metres a second — so the rope became a way to drag yourself out of the map
        // and die. The harness caught it as a bot leaving the arena at thirty-seven metres up.
        if (!Arena.InPlay(where)) return;

        shooter.StartGrapple(where);
        if (Visuals) Impact.Hit(this, where, -s.Vel.Normalized(), new Color(0.9f, 0.95f, 0.45f));
    }

    // ---- the Apologist's barrier ----
    //
    // The one piece of cover in the game that somebody decided to put there.
    //
    // Every other wall is where the level designer left it. This one appears in a doorway you chose
    // at a moment you chose, it stops their fire and passes yours, and it lasts long enough to take
    // a post behind. That asymmetry is the whole character: the Apologist does not win a fight, it
    // decides where the fight is allowed to happen.

    /// <summary>A planted wall of inscribed plate. Stops enemy rounds, passes friendly ones.</summary>
    sealed class Barrier
    {
        public Vector3 Centre;

        /// <summary>Unit vector along the wall's width. Its face is perpendicular to this.</summary>
        public Vector3 Along;

        public Pawn Owner = null!;
        public float Age;
        public Node3D? Node;
    }

    readonly List<Barrier> barriers = new();

    /// <summary>Half the width and height of a planted wall.</summary>
    public const float BarrierHalfWidth = 4.2f;
    public const float BarrierHalfHeight = 2.2f;

    /// <summary>Barriers standing at once, per owner. A third replaces the oldest.</summary>
    public const int BarriersPerOwner = 1;

    /// <summary>Rounds a barrier has stopped this match. Reported by the harness.</summary>
    public int BarrierBlocks;

    /// <summary>How many walls are standing. For the harness.</summary>
    public int BarrierCount => barriers.Count;

    void PlantBarrier(Pawn user)
    {
        // Across the direction you are looking, a little in front of you. Standing *behind* your
        // own wall is the point, so it goes down in front rather than centred on you.
        Vector3 facing = new Vector3(MathF.Cos(user.Facing), 0f, MathF.Sin(user.Facing));
        Vector3 along = new Vector3(-facing.Z, 0f, facing.X);

        var at = user.GlobalPosition + facing * 2.6f;

        // One at a time each. A player who can stack them turns a doorway into a bunker, and the
        // ability is meant to be a decision about *where*, not an accumulation.
        int mine = 0;
        for (int i = barriers.Count - 1; i >= 0; i--)
        {
            if (barriers[i].Owner != user) continue;
            if (++mine < BarriersPerOwner) continue;

            barriers[i].Node?.QueueFree();
            barriers.RemoveAt(i);
        }

        var wall = new Barrier { Centre = at, Along = along, Owner = user };

        if (Visuals)
        {
            var node = new MeshInstance3D
            {
                Mesh = new BoxMesh
                {
                    Size = new Vector3(BarrierHalfWidth * 2f, BarrierHalfHeight * 2f, 0.3f),
                },
                MaterialOverride = new StandardMaterial3D
                {
                    AlbedoColor = user.Faction.Tint with { A = 0.42f },
                    Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                    ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                    CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                    EmissionEnabled = true,
                    Emission = user.Faction.Tint,
                    EmissionEnergyMultiplier = 1.6f,
                },
            };

            AddChild(node);
            node.GlobalPosition = at + Vector3.Up * BarrierHalfHeight;
            node.LookAt(node.GlobalPosition + facing, Vector3.Up);
            wall.Node = node;
        }

        barriers.Add(wall);
        if (Visuals) Sfx.PlayAt(Sound.ZoneCapture, at, -4f, 1.3f);
    }

    void StepBarriers(float dt)
    {
        for (int i = barriers.Count - 1; i >= 0; i--)
        {
            barriers[i].Age += dt;

            // Tied to the ability's own duration rather than a second number, so the gauge on the
            // HUD is telling the truth about how long the wall has left.
            if (barriers[i].Age < SpecialClasses.Apologist.SpecialDuration) continue;

            barriers[i].Node?.QueueFree();
            barriers.RemoveAt(i);
        }
    }

    /// <summary>
    /// Whether a round travelling <paramref name="from"/> to <paramref name="to"/> is stopped.
    ///
    /// Friendly fire passes straight through, which is the entire asymmetry and the reason this is
    /// worth a slot. A wall that blocked everything would be a wall, and the map already has three
    /// hundred of those.
    /// </summary>
    bool BarrierStops(Shot s, Vector3 from, Vector3 to, out Vector3 where)
    {
        where = to;

        foreach (var wall in barriers)
        {
            if (wall.Owner == s.Owner) continue;
            if (Settings.Def.Teams && SameTeam(wall.Owner, s.Owner)) continue;

            // The face normal, and how far either end of this frame's step sits along it.
            Vector3 normal = new Vector3(-wall.Along.Z, 0f, wall.Along.X);

            float a = (from - wall.Centre).Dot(normal);
            float b = (to - wall.Centre).Dot(normal);

            // Both ends the same side: it did not cross the plane this frame.
            if (a * b > 0f) continue;
            if (MathF.Abs(a - b) < 1e-5f) continue;

            Vector3 hit = from + (to - from) * (a / (a - b));
            Vector3 d = hit - wall.Centre;

            if (MathF.Abs(d.Dot(wall.Along)) > BarrierHalfWidth) continue;
            if (d.Y < 0f || d.Y > BarrierHalfHeight * 2f) continue;

            where = hit;
            BarrierBlocks++;

            if (Visuals) Impact.Hit(this, hit, -s.Vel.Normalized(), wall.Owner.Faction.Tint);
            return true;
        }

        return false;
    }
    /// <summary>How much speed a bouncing round keeps off a wall.</summary>
    public const float BounceDamping = 0.6f;

    /// <summary>
    /// How much a healing round gives back, against what the same round would take out.
    ///
    /// Above one, because mending is slower work than breaking and the Anatomist has to stand next
    /// to whoever it is mending to do it — sixteen metres of reach in a game where everything else
    /// shoots forty. At two, a full second of beam is a hundred and twenty back, which is most of a
    /// Trooper and about as long as anybody survives standing still in the open.
    /// </summary>
    public const float HealScale = 2f;

    /// <summary>Health handed back to teammates this match. Reported by the harness.</summary>
    public float HealingDone;

    /// <summary>
    /// A fused round reaching zero, wherever it happens to be.
    ///
    /// Separate from the contact path because the two are genuinely different events: a rocket
    /// explodes on something and a grenade explodes at a *place*, quite possibly in mid-air with
    /// nothing near it. Hurting the thrower is deliberate and is most of what keeps the biggest
    /// blast in the game honest — two seconds is long enough to walk into your own shot.
    /// </summary>
    void Detonate(Shot s)
    {
        s.Mesh?.QueueFree();

        if (s.BlastDamage <= 0f) return;

        Blast(s.Owner, s.Pos, s.BlastDamage, s.BlastRadius, hurtSelf: true, s.StructureScale);
        if (Visuals) Impact.Death(this, s.Pos, s.Owner.Tint);
    }

    /// <summary>
    /// Carry a round through a gate, if it flew into one.
    ///
    /// Rounds used to sail straight through a portal as though it were not there, which made the
    /// gun strictly a movement tool and quietly wrong: a gate you can walk through but cannot shoot
    /// through is not a hole in space, it is a lift. Everything goes now — bullets, pellets,
    /// grenades, rockets — because "everything" is the only rule anybody can hold in their head
    /// while playing, and because a grenade lobbed through a gate into a room is the best thing
    /// either weapon does.
    ///
    /// Returns true when the round was moved, so the caller skips this frame's sweep: the round is
    /// somewhere else entirely now and a ray from where it *was* would cut through half the map.
    /// </summary>
    bool StepShotThroughPortals(Shot s)
    {
        if (portals.Count < 2 || s.PortalLock > 0f) return false;

        var a = portals[0];
        var b = portals[1];

        Portal? entered = null;
        if (s.Pos.DistanceTo(a.At) < PortalReach) entered = a;
        else if (s.Pos.DistanceTo(b.At) < PortalReach) entered = b;

        if (entered == null) return false;

        var exit = entered == a ? b : a;
        float speed = s.Vel.Length();

        // Out along the gate's own normal rather than along the round's heading. A round that kept
        // its direction would come out of a floor gate travelling sideways through the floor.
        s.Pos = exit.At + exit.Out * 0.6f;
        s.Vel = exit.Out * speed;

        // Long enough to clear the far gate's own reach at any speed the game fires at.
        s.PortalLock = 0.25f;

        // The round belongs to whoever laid the gate now, for the same reason a deflected round
        // does: a kill made by a portal somebody else built is not the shooter's kill.
        if (entered.Owner is { } layer) s.Owner = layer;

        if (Visuals && s.Mesh != null) PointAlong(s.Mesh, s.Pos, s.Vel);
        return true;
    }

    /// <summary>
    /// Turn a seeking round toward the best thing in front of it.
    ///
    /// Re-targeted every tick rather than locked on at launch. A lock is worse in both directions:
    /// it wastes the round when its target dies or ducks into cover, and it makes the weapon feel
    /// like it belongs to the shooter rather than to the arena. Re-targeting means a seeker that
    /// loses its mark will take whatever else wanders into the cone, which is both more dangerous
    /// and more honest about what the thing is.
    /// </summary>
    void SteerSeeker(Shot s, float dt)
    {
        if (SeekerTarget(s) is not { } target) return;

        var cur = s.Vel.Normalized();
        var want = (target - s.Pos);
        if (want.LengthSquared() < 0.0001f) return;
        want = want.Normalized();

        float angle = cur.AngleTo(want);
        if (angle < 0.0001f) return;

        // Rate-limited, which is the entire balance of the weapon: it can correct for a target
        // that moves and it cannot correct for one that moves enough. Lerped and renormalised
        // rather than slerped, because a target directly behind makes the rotation axis undefined
        // and a seeker that stops dead on a divide-by-zero is worse than one that turns wide.
        float t = MathU.Clamp01(s.SeekTurnRate * dt / angle);
        var blended = cur.Lerp(want, t);
        if (blended.LengthSquared() < 0.000001f) return;

        s.Vel = blended.Normalized() * s.Vel.Length();

        if (s.Mesh != null) PointAlong(s.Mesh, s.Pos, s.Vel);
    }

    /// <summary>
    /// What a seeking round should chase, or null when nothing qualifies.
    ///
    /// Vehicles count, and are the reason the weapon reads as heat-seeking rather than as a
    /// magic bullet: a tank is the largest, hottest, slowest thing on any map and the one target
    /// a rocket that steers should obviously be good against.
    ///
    /// The cone is measured from where the round is *going*, not from where it was fired, so a
    /// seeker that has committed to a turn keeps chasing rather than losing its mark to its own
    /// manoeuvre.
    /// </summary>
    Vector3? SeekerTarget(Shot s)
    {
        var dir = s.Vel.Normalized();
        float cosCone = MathF.Cos(s.SeekCone);

        Vector3? best = null;
        float bestDist = s.SeekRange;

        void Consider(Vector3 at)
        {
            var to = at - s.Pos;
            float d = to.Length();
            if (d > bestDist || d < 0.01f) return;
            if (dir.Dot(to / d) < cosCone) return;

            best = at;
            bestDist = d;
        }

        foreach (var p in Pawns)
        {
            if (p == s.Owner || !p.Alive || p.InVehicle) continue;
            if (Settings.Def.Teams && SameTeam(p, s.Owner)) continue;

            Consider(p.GlobalPosition + Vector3.Up * (p.CurrentHeight * 0.55f));
        }

        foreach (var rig in VehicleList)
        {
            if (!rig.Alive || rig == s.Owner.Riding) continue;
            if (rig.Driver is { } crew && Settings.Def.Teams && SameTeam(crew, s.Owner)) continue;

            Consider(rig.GlobalPosition + Vector3.Up * rig.Def.HalfExtents.Y);
        }

        return best;
    }

    /// <summary>
    /// Whether anything but the shooter is close enough to set this round off where it is.
    ///
    /// The shooter is exempt and has to be: a round spawns at the muzzle, well inside its own
    /// trigger radius, so counting them would detonate every seeker in the shooter's face on the
    /// frame it was fired. They are not exempt from the *blast* — standing next to your own
    /// rocket when somebody else sets it off is the ordinary way to be hurt by one.
    ///
    /// Teammates are not exempt either. A seeker crossing a room is a thing nobody can walk
    /// through, and one of your own running into yours is your shot to have wasted.
    /// </summary>
    bool TouchingSomething(Shot s)
    {
        float r2 = s.TriggerRadius * s.TriggerRadius;

        foreach (var p in Pawns)
        {
            if (p == s.Owner || !p.Alive || p.InVehicle) continue;

            // Measured to the middle of the pawn rather than their feet, or a round at head height
            // would sail over somebody it is visibly touching.
            var mid = p.GlobalPosition + Vector3.Up * (p.CurrentHeight * 0.5f);
            if (mid.DistanceSquaredTo(s.Pos) <= r2) return true;
        }

        foreach (var rig in VehicleList)
        {
            if (!rig.Alive || rig == s.Owner.Riding) continue;

            var half = rig.Def.HalfExtents;
            var mid = rig.GlobalPosition + Vector3.Up * half.Y;

            // The hull's *smallest* half-extent is added, not its diagonal. This check is a
            // backstop rather than the way a seeker normally kills a tank: a round approaching
            // from outside crosses the hull's face and the sweep resolves it as a direct hit,
            // which is worth the impact damage on top of the blast. What the sweep cannot do is
            // notice a hull that has driven *onto* a round already in the air — a ray that begins
            // inside a collider reports nothing — and that is the case this catches.
            //
            // Sized off the diagonal instead, a tank's 4.8m would put the trigger three metres
            // clear of the nose and every seeker would airburst short of the one target the
            // weapon is meant to be best against.
            float reach = s.TriggerRadius + MathF.Min(half.X, MathF.Min(half.Y, half.Z));
            if (mid.DistanceSquaredTo(s.Pos) <= reach * reach) return true;
        }

        return false;
    }

    void StepShots(float dt)
    {
        var space = GetWorld3D().DirectSpaceState;

        for (int i = shots.Count - 1; i >= 0; i--)
        {
            var s = shots[i];

            // A heavy round arcs. Applied before the sweep so the ray follows the path the round
            // actually takes this frame rather than the flat one it would have taken.
            if (s.Weight > 0f) s.Vel += Vector3.Down * (Pawn.Gravity * s.Weight * dt);

            // And a seeker turns, for the same reason and in the same place: the sweep below has
            // to follow the path the round actually takes, not the one it was pointed down when it
            // left the tube.
            if (s.SeekTurnRate > 0f) SteerSeeker(s, dt);

            // Anything that walks into it. Checked before the sweep because it is not a sweep
            // question — see WeaponDef.TriggerRadius.
            if (s.TriggerRadius > 0f && TouchingSomething(s))
            {
                Detonate(s);
                shots.RemoveAt(i);
                continue;
            }

            if (s.PortalLock > 0f) s.PortalLock -= dt;

            // A fused round is on a clock that the world cannot stop. Bouncing off three walls and
            // rolling into a corner does not save you from it — that is the entire difference
            // between a grenade and a rocket.
            if (s.Fuse > 0f)
            {
                s.Fuse -= dt;
                if (s.Fuse <= 0f) { Detonate(s); shots.RemoveAt(i); continue; }
            }

            if (StepShotThroughPortals(s)) continue;

            Vector3 from = s.Pos;
            Vector3 to = from + s.Vel * dt;

            // Checked before the world sweep, and deliberately: a barrier is in front of whatever
            // it was planted in front of, so a round that would have hit the wall behind it has to
            // stop here instead.
            if (BarrierStops(s, from, to, out Vector3 stopped))
            {
                // A fused round still goes off. Lobbing a grenade at somebody's wall and having it
                // detonate against the face of it is a perfectly good answer to one, and the blast
                // does not care what stopped the shell.
                if (s.Fuse > 0f && s.BlastDamage > 0f) { s.Pos = stopped; Detonate(s); }
                else s.Mesh?.QueueFree();

                shots.RemoveAt(i);
                continue;
            }

            using var query = PhysicsRayQueryParameters3D.Create(from, to);

            // The firing vehicle is excluded as well as the shooter. Without it a hull that drove
            // forward into its own shell would blow itself up, which is not a skill expression.
            query.Exclude = s.Owner.Riding is { } own
                ? new Godot.Collections.Array<Rid> { s.Owner.GetRid(), own.GetRid() }
                : new Godot.Collections.Array<Rid> { s.Owner.GetRid() };

            var hit = space.IntersectRay(query);

            bool expired = false;

            if (hit.Count > 0)
            {
                Vector3 where = hit["position"].AsVector3();

                // A grenade off a wall.
                //
                // Handled before anything else in this branch, and it deliberately does not care
                // what it struck. A grenade that bounces off scenery and detonates on a body would
                // be a rocket with extra steps, and the throw people actually want is the one that
                // goes off a doorframe into a room — so it bounces off everything, and the fuse is
                // the only thing that ever sets it off.
                if (s.Bounces && s.Fuse > 0f)
                {
                    Vector3 normal = hit["normal"].AsVector3();

                    // Damped, or a grenade thrown down a corridor never settles. Sixty per cent is
                    // enough for two or three useful bounces and not enough for a fourth.
                    s.Vel = s.Vel.Bounce(normal) * BounceDamping;
                    s.Pos = where + normal * 0.25f;

                    if (Visuals)
                    {
                        if (s.Mesh != null) PointAlong(s.Mesh, s.Pos, s.Vel);
                        Sfx.PlayAt(Sound.Hit, where, -8f, 1.7f);
                    }

                    continue;
                }

                // Vehicles used to be invisible to this test entirely: a round that struck a hull
                // simply stopped, and Vehicle.TakeDamage was dead code. Every vehicle in the game
                // was indestructible.
                if (hit["collider"].As<GodotObject>() is Vehicle rig && rig.Alive)
                {
                    float before = rig.Health;
                    bool wrecked = rig.TakeDamage(s.Damage);
                    DamageDealt += before - rig.Health;

                    s.Owner.HitConfirm = Pawn.HitConfirmTime;
                    if (Visuals)
                    {
                        Sfx.PlayAt(Sound.Hit, where);
                        Impact.Hit(this, where, -s.Vel.Normalized(), rig.Def.Tint);
                    }
                    if (wrecked) WreckVehicle(rig, s.Owner);
                }
                else if (hit["collider"].As<GodotObject>() is Pawn blocker && blocker.Alive
                         && blocker.Deflects(s.Vel))
                {
                    // Turned out of the air by a raised saber.
                    //
                    // Sent back at whoever fired it rather than bounced off the surface normal.
                    // A physical bounce is what a simulation would do and it is not what anybody
                    // wants: it sprays rounds into the scenery and the shooter never learns that
                    // they did it to themselves. Returning fire to sender makes the block read as
                    // a punish, which is the only thing that stops people standing in the open
                    // shooting at a juggernaut who is visibly holding a blade up.
                    Vector3 back = s.Owner.GlobalPosition
                                 + Vector3.Up * s.Owner.CurrentEyeHeight * 0.7f
                                 - where;

                    if (back.LengthSquared() < 0.01f) back = -s.Vel;

                    s.Pos = where + back.Normalized() * 0.6f;
                    s.Vel = back.Normalized() * s.Vel.Length();
                    s.Damage *= DeflectDamageScale;

                    // The round changes hands. Otherwise the shooter's own exclusion would carry it
                    // straight through them, and a kill on the rebound would be scored to the
                    // person who was hit by it.
                    s.Owner = blocker;
                    s.RangeLeft = MathF.Max(s.RangeLeft, DeflectRange);

                    blocker.HitConfirm = Pawn.HitConfirmTime;
                    Deflections++;

                    if (Visuals)
                    {
                        Sfx.PlayAt(Sound.Hit, where, 2f, 1.6f);
                        Impact.Hit(this, where, -s.Vel.Normalized(), new Color(0.98f, 0.86f, 0.30f));
                    }

                    // Not expired: it carries on from here as the juggernaut's round, so the sweep
                    // has to keep stepping it.
                    continue;
                }
                // A surgical beam landing on your own side. Checked before the damage path rather
                // than folded into it, because the two do not share a single line of behaviour: no
                // headshot, no hit marker for the victim, no damage flash, no kill.
                else if (s.Heals && hit["collider"].As<GodotObject>() is Pawn friend && friend.Alive
                         && friend != s.Owner && Settings.Def.Teams && SameTeam(s.Owner, friend))
                {
                    float was = friend.Health;
                    friend.Heal(s.Damage * HealScale);

                    if (friend.Health > was)
                    {
                        s.Owner.HitConfirm = Pawn.HitConfirmTime;
                        HealingDone += friend.Health - was;

                        if (Visuals)
                            Impact.Hit(this, hit["position"].AsVector3(), -s.Vel.Normalized(),
                                       new Color(0.45f, 0.98f, 0.62f));
                    }

                    expired = true;
                }
                else if (hit["collider"].As<GodotObject>() is Pawn target && target.Alive)
                {
                    // Straight through a phasing Custodian, half the time.
                    //
                    // A coin flip per shot rather than a damage reduction, because the two read
                    // completely differently: resistance is a number nobody can see, and a round
                    // that visibly does nothing is a thing both players notice immediately.
                    if (target.Phasing && GD.Randf() < Pawn.PhaseMissChance)
                    {
                        if (Visuals)
                            Impact.Hit(this, hit["position"].AsVector3(), -s.Vel.Normalized(),
                                       target.Faction.Tint);

                        PhasedShots++;

                        // The round is spent, and it has to be *removed* rather than skipped.
                        // `continue` here jumps past the removal at the bottom of the loop, so a
                        // phased shot stayed in the world and phased again on the very next frame,
                        // for ever — the shot list grew without bound and the run died. Setting the
                        // flag and falling through is the only correct exit from this branch.
                        expired = true;
                    }
                    else
                    {
                    // Headshots come from the impact height rather than a separate collider: the
                    // pawn is one capsule, and a second body just for the head would double the
                    // physics cost of every pawn for something a height test resolves exactly.
                    // Measured against the target's *current* height, so a crouched player's head
                    // really is lower down and shots that would have been headshots against a
                    // standing target sail over.
                    Vector3 impact = hit["position"].AsVector3();
                    float local = impact.Y - target.GlobalPosition.Y;
                    bool headshot = local >= target.CurrentHeight * Pawn.HeadFraction;

                    float damage = headshot ? s.Damage * HeadshotScale(s) : s.Damage;

                    // Achilles' heel, and the shooter's own crown if they are wearing one. The heel
                    // deliberately inverts the instinct every other target in this game trains: aim
                    // high at people, aim at the floor at him.
                    damage *= JuggernautHitScale(target, local);
                    damage *= s.Owner.Crown?.DamageScale ?? 1f;

                    // The Tragedian, hitting harder the nearer it is to going down.
                    damage *= s.Owner.DesperationScale;

                    float before = target.Health;
                    bool killed = target.TakeDamage(damage);
                    float dealt = before - target.Health;
                    DamageDealt += dealt;

                    // The needle sticks. Counted only when it actually did something, so needles
                    // that glanced off a dashing player's invulnerability do not quietly build a
                    // supercombine out of shots that missed.
                    if (s.Needles && dealt > 0f && !killed
                        && target.AddNeedle(SupercombineNeedles, SupercombineWindow))
                    {
                        Supercombines++;

                        // Centred on them rather than on the impact, because it is the needles
                        // going off and the needles are in them.
                        var at = target.GlobalPosition + Vector3.Up * (target.CurrentHeight * 0.5f);
                        Blast(s.Owner, at, SupercombineDamage, SupercombineRadius, hurtSelf: false);

                        if (Visuals)
                        {
                            Impact.Death(this, at, Weapons.TintFor(Weapons.Needler));
                            Sfx.PlayAt(Sound.Death, at, -2f, 1.4f);
                        }

                        killed = !target.Alive;
                    }

                    // Only a shot that actually did something confirms. Hitting someone who is
                    // dashing through their invulnerability frames should read as a miss, because
                    // that is exactly what it was.
                    if (dealt > 0f)
                    {
                        s.Owner.HitConfirm = Pawn.HitConfirmTime;
                        target.DamageFlash = Pawn.DamageFlashTime;
                        target.LastAttacker = s.Owner.GlobalPosition;

                        if (headshot)
                        {
                            s.Owner.HeadshotBanner = HeadshotBannerTime;
                            Headshots++;
                        }
                    }

                    if (Visuals)
                    {
                        Sfx.PlayAt(killed ? Sound.Death : Sound.Hit, target.GlobalPosition);

                        // Bursts spawn at the impact point, blown back along the incoming shot.
                        // They used to spawn at the ray's start, which on a fast projectile was
                        // most of a metre short of where the round actually landed.
                        Vector3 outward = -s.Vel.Normalized();

                        // The death burst is thrown by AwardKill, so every death looks the same
                        // however it happened — bullet, blast or a fall into a pit.
                        if (headshot && !killed) Impact.Headshot(this, impact, outward, target.Tint);
                        else if (dealt > 0f) Impact.Hit(this, impact, outward, target.Tint);
                    }

                    if (killed) AwardKill(s.Owner, target);
                    }
                }

                // An explosive round does its real work here, on whatever it struck — a body, a
                // hull, or the floor under someone's feet.
                if (s.BlastDamage > 0f)
                {
                    Blast(s.Owner, where, s.BlastDamage, s.BlastRadius, hurtSelf: true,
                          s.StructureScale);
                    if (Visuals) Impact.Death(this, where, s.Owner.Tint);
                }

                // A portal round does its work here too, and does no damage on the way.
                if (s.PlantsPortal)
                    PlantPortal(where, hit["normal"].AsVector3(), s.Owner);

                // A grapple round. What it hit decides which way the rope pulls.
                if (s.Grapples && s.Owner.Alive && !s.Owner.InVehicle)
                    ResolveGrapple(s, where, hit["collider"].As<GodotObject>());

                expired = true;
            }
            else
            {
                s.RangeLeft -= s.Vel.Length() * dt;
                s.Pos = to;
                if (s.RangeLeft <= 0f) expired = true;
                else if (s.Mesh != null) PointAlong(s.Mesh, to, s.Vel);
            }

            if (!expired) continue;

            s.Mesh?.QueueFree();
            shots.RemoveAt(i);
        }
    }

    /// <summary>One line of the kill feed.</summary>
    public sealed class KillEvent
    {
        public string Killer = "";
        public string Victim = "";
        public string Weapon = "";
        public Color KillerTint;
        public Color VictimTint;
        public bool SelfInflicted;
        public float Age;
    }

    /// <summary>Recent kills, newest last. Entries expire on their own.</summary>
    public readonly List<KillEvent> KillFeed = new();

    const float KillFeedLifetime = 5.5f;

    void AgeFeedback(float dt)
    {
        foreach (var p in Pawns)
        {
            if (p.HitConfirm > 0f) p.HitConfirm -= dt;
            if (p.DamageFlash > 0f) p.DamageFlash -= dt;
            if (p.HeadshotBanner > 0f) p.HeadshotBanner -= dt;
            if (p.KillBanner > 0f) p.KillBanner -= dt;
        }

        for (int i = KillFeed.Count - 1; i >= 0; i--)
        {
            KillFeed[i].Age += dt;
            if (KillFeed[i].Age > KillFeedLifetime) KillFeed.RemoveAt(i);
        }
    }

    public const float KillBannerTime = 1.8f;

    /// <summary>
    /// How long after coming out of a gate a death still counts as the gate-layer's doing.
    ///
    /// Two seconds, which is about how long it takes to fall into something. Long enough that
    /// putting a gate over a pit and shooting somebody into it is a kill, and short enough that
    /// walking through a portal does not make you the property of whoever built it for the rest of
    /// the round.
    /// </summary>
    public const float PortalCreditWindow = 2f;

    /// <summary>Who last put each pawn through a gate, and when.</summary>
    readonly Dictionary<Pawn, (Pawn Layer, float At)> portalCredit = new();

    /// <summary>Portal kills scored this match. Reported by the harness.</summary>
    public int PortalKills;

    /// <summary>
    /// Whoever laid the gate you just came out of, if it was recent enough to be their doing.
    ///
    /// This exists because of a play the game did not reward and should have: a gate planted over a
    /// pit, and enemies shot into it. Every part of that is deliberate and skilful and the game
    /// scored it as the victim tripping over their own feet, because the death arrives with no
    /// attacker and every environmental death in here is booked as a suicide.
    ///
    /// Deliberately narrow. It only ever redirects a death that had *no* killer — walk out of a
    /// gate and get shot and the shooter keeps their kill, which is the right answer and also the
    /// one that stops this becoming a way to farm frags off your own gate.
    /// </summary>
    Pawn? PortalCreditFor(Pawn victim)
    {
        if (!portalCredit.TryGetValue(victim, out var trip)) return null;
        if (Elapsed - trip.At > PortalCreditWindow) return null;
        if (trip.Layer == victim) return null;
        if (Settings.Def.Teams && SameTeam(trip.Layer, victim)) return null;

        return trip.Layer;
    }

    void AwardKill(Pawn killer, Pawn victim)
    {
        // A death with nobody to blame, shortly after being put through somebody's gate, is that
        // somebody's kill. Redirected here rather than at each of the four environmental death
        // paths — a fall, a pit, lava, being crushed — because they all funnel through this one
        // call as a self-kill, and four copies of this rule is four places for it to drift.
        if (killer == victim && PortalCreditFor(victim) is { } layer)
        {
            killer = layer;
            portalCredit.Remove(victim);
            PortalKills++;
        }

        // Both sides get told, loudly. Previously a death was a line in the corner and a body
        // falling over, which in a four-way fight was easy to miss entirely — including your own.
        victim.KilledBy = killer == victim ? "" : killer.Name2;

        if (killer != victim)
        {
            killer.KillBanner = KillBannerTime;
            killer.KillBannerName = victim.Name2;
        }

        if (Visuals)
        {
            // A far bigger burst than a hit, plus a ring of debris thrown outward along the
            // ground, so a kill is visible from across the arena and not just to the two involved.
            Impact.Death(this, victim.GlobalPosition, victim.Tint);
            Impact.DeathRing(this, victim.GlobalPosition, victim.Tint);
        }

        KillFeed.Add(new KillEvent
        {
            Killer = killer.Name2,
            Victim = victim.Name2,
            Weapon = killer.Class.WeaponName,
            KillerTint = killer.Tint,
            VictimTint = victim.Tint,
            SelfInflicted = killer == victim,
        });

        // Only the most recent few are ever drawn; letting the list grow unbounded across a long
        // match would be a slow leak.
        while (KillFeed.Count > 8) KillFeed.RemoveAt(0);

        // Before any of the scoring branches, and unconditional. In Dominion a death is a body off
        // the pool whoever caused it — a frag, a own-goal grenade, a fall — and every branch below
        // returns early somewhere, so hanging this off one of them would quietly stop counting.
        SpendReinforcement(victim);
        AwardKillPoints(killer, victim);

        if (Settings.Mode == GameMode.Juggernaut) { ScoreJuggernautKill(killer, victim); return; }

        // Killing yourself costs a frag rather than earning one, so splash and blind fire near
        // cover carry a real price.
        if (killer == victim) { killer.Score--; return; }

        if (Settings.Def.Teams && SameTeam(killer, victim)) { killer.Score--; return; }

        killer.Score++;
    }

    /// <summary>
    /// Battle points for a kill, which is a different question from score.
    ///
    /// Awarded in every mode and to every kind of kill, including ones that score nothing: killing
    /// an ordinary fighter in Juggernaut is worth no points on the board and is still worth banking,
    /// because the bank is a record of having done something useful rather than of being ahead.
    ///
    /// Nothing for suicides or for shooting your own side — those are the two cases where paying
    /// out would make farming your own team the fastest route to a hero.
    /// </summary>
    void AwardKillPoints(Pawn killer, Pawn victim)
    {
        if (killer == victim) return;
        if (Settings.Def.Teams && SameTeam(killer, victim)) return;

        killer.AwardPoints(victim.IsJuggernaut ? BattlePoints.HeroKill : BattlePoints.Kill);
    }

    /// <summary>
    /// Juggernaut scoring, which is a different game from frags.
    ///
    /// Only kills made *while wearing the crown* count. Everything else on the map is positioning:
    /// shooting an ordinary fighter is worth nothing on the scoreboard, which is what stops the
    /// mode collapsing into a deathmatch with a hat on. The one reason to shoot anyone else is that
    /// they are between you and the juggernaut.
    /// </summary>
    void ScoreJuggernautKill(Pawn killer, Pawn victim)
    {
        // Killing the juggernaut is the promotion. You wear your own faction's figure, not theirs —
        // which is why who takes the crown changes what the crown does.
        if (victim == Juggernaut && killer != victim)
        {
            CrownPawn(killer);
            return;
        }

        // First blood takes it. Somebody has to start, and starting it with a kill means the match
        // opens as an ordinary scramble that resolves the moment anyone lands one.
        if (Juggernaut == null && killer != victim)
        {
            CrownPawn(killer);
            return;
        }

        if (killer == victim)
        {
            // The juggernaut falling off the map hands the crown on rather than merely dying.
            if (victim == Juggernaut && NearestTo(victim) is { } heir) CrownPawn(heir);
            return;
        }

        // A kill *by* the juggernaut is the only thing worth a point.
        if (killer != Juggernaut) return;

        killer.Score++;
        killer.CrownKills++;
        killer.BuyAnotherNight();
    }

    public static int TeamOf(int slot) => slot % 2;

    public static bool SameTeam(Pawn a, Pawn b) => TeamOf(a.Slot) == TeamOf(b.Slot);

    /// <summary>Combined score of one team, which is what a team mode actually plays to.</summary>
    public int TeamScore(int team)
    {
        int total = 0;
        foreach (var p in Pawns) if (TeamOf(p.Slot) == team) total += p.Score;
        return total;
    }

    /// <summary>Seconds left, or null when the match is untimed.</summary>
    public float? TimeRemaining
        => Settings.TimeLimitSeconds > 0 ? MathF.Max(0f, Settings.TimeLimitSeconds - Elapsed) : null;

    // ---- rounds (Elimination) ----

    /// <summary>Current round, one-based. Only meaningful in Elimination.</summary>
    public int Round { get; private set; } = 1;

    /// <summary>Seconds left between rounds, or zero while a round is being played.</summary>
    public float IntermissionLeft { get; private set; }

    /// <summary>What just happened, shown during the intermission.</summary>
    public string RoundResult { get; private set; } = "";

    const float IntermissionTime = 3.5f;

    public bool BetweenRounds => IntermissionLeft > 0f;

    void StartNextRound()
    {
        Round++;
        RoundResult = "";

        // Gates do not survive the round that made them. A pair left standing across a reset would
        // put a shortcut on the map that nobody alive had paid for.
        ClearPortals();

        // Everyone comes back, wherever they died — a round is a clean reset, not a respawn.
        foreach (var p in Pawns)
            p.Respawn(Arena.SpawnPoints[p.Slot % Arena.SpawnPoints.Count]);
    }

    void CheckWin()
    {
        // A scene ends when its script does. Every condition below is a way of winning, and there
        // is nothing to win in a town - left in, a story mission would quietly declare somebody
        // the victor of his own childhood the moment the clock ran out.
        if (Settings.IsStoryMission) return;

        // Time runs out for every mode, and whoever is ahead takes it. A draw leaves no winner
        // rather than picking one arbitrarily.
        if (TimeRemaining is <= 0f)
        {
            Finished = true;

            var order = Standings();
            Winner = order.Count > 1 && order[0].Score == order[1].Score ? null : order[0];
            return;
        }

        if (Settings.Mode == GameMode.Elimination) { CheckElimination(); return; }

        // The chambers are finished, not won. Everybody who is standing there when the last
        // checkpoint lights up has completed it, so the "winner" is whoever contributed most —
        // which in a two-player chamber is a way of saying who did the shooting.
        if (Settings.IsPuzzle)
        {
            if (CheckpointCount == 0 || CheckpointsReached < CheckpointCount) return;

            Finished = true;
            Winner = Standings().Count > 0 ? Standings()[0] : null;
            return;
        }

        // Dominion is the one mode whose number goes down, so it cannot use the shared "first to
        // the limit" path below: the limit is the size of the pool each side started with, not a
        // target either of them is chasing.
        if (Settings.Mode == GameMode.Dominion)
        {
            for (int team = 0; team < 2; team++)
            {
                if (Tickets[team] > 0f) continue;

                Finished = true;
                Winner = BestOnTeam(1 - team);
                return;
            }
            return;
        }

        // A team mode plays to a combined score, so one player cannot win it alone.
        if (Settings.Def.Teams)
        {
            for (int team = 0; team < 2; team++)
            {
                if (TeamScore(team) < Settings.ScoreLimit) continue;

                Finished = true;
                Winner = BestOnTeam(team);
                return;
            }
            return;
        }

        foreach (var p in Pawns)
        {
            if (p.Score < Settings.ScoreLimit) continue;
            Finished = true;
            Winner = p;
            return;
        }
    }

    Pawn? BestOnTeam(int team)
    {
        Pawn? best = null;
        foreach (var p in Standings())
            if (TeamOf(p.Slot) == team) { best = p; break; }
        return best;
    }

    /// <summary>
    /// Elimination is played in rounds, which it previously was not: the mode scored in "rounds"
    /// but the whole match ended the first time someone was left standing.
    /// </summary>
    void CheckElimination()
    {
        if (BetweenRounds) return;

        int alive = 0;
        Pawn? last = null;
        foreach (var p in Pawns) if (p.Alive) { alive++; last = p; }

        if (alive > 1) return;

        if (last != null)
        {
            last.RoundsWon++;
            RoundResult = $"{last.Name2} takes round {Round}";
        }
        else
        {
            // Everyone died in the same instant — a mutual grenade. Nobody scores.
            RoundResult = $"Round {Round} drawn";
        }

        if (last != null && last.RoundsWon >= Settings.ScoreLimit)
        {
            Finished = true;
            Winner = last;
            return;
        }

        IntermissionLeft = IntermissionTime;
    }

    /// <summary>What a pawn's headline number is in this mode — rounds won, or frags.</summary>
    public int ScoreOf(Pawn p) => Settings.Mode switch
    {
        GameMode.Elimination => p.RoundsWon,

        // Only kills made while wearing the crown. An ordinary frag moves nothing.
        GameMode.Juggernaut => p.CrownKills,

        _ => p.Score,
    };

    /// <summary>Pawns ordered for the scoreboard, by whatever the mode actually scores.</summary>
    public List<Pawn> Standings()
    {
        var list = new List<Pawn>(Pawns);
        list.Sort((a, b) =>
        {
            int sa = ScoreOf(a), sb = ScoreOf(b);
            if (sa != sb) return sb.CompareTo(sa);
            if (a.Score != b.Score) return b.Score.CompareTo(a.Score);
            return a.Deaths.CompareTo(b.Deaths);
        });
        return list;
    }

    /// <summary>Clear line of fire between two pawns, used by bots to decide whether to shoot.</summary>
    public bool HasLineOfSight(Pawn from, Pawn to)
    {
        var space = GetWorld3D().DirectSpaceState;
        using var query = PhysicsRayQueryParameters3D.Create(from.Eye, to.Eye);

        // Either end may be sitting inside a hull. Without excluding them the ray leaves the
        // looker's own vehicle and stops dead against it, so a bot in a tank could never see
        // anything and never fired a single round — and a target in a vehicle registered as
        // permanently behind cover.
        var skip = new Godot.Collections.Array<Rid> { from.GetRid(), to.GetRid() };
        if (from.Riding is { } fromRig) skip.Add(fromRig.GetRid());
        if (to.Riding is { } toRig) skip.Add(toRig.GetRid());
        query.Exclude = skip;

        var hit = space.IntersectRay(query);
        return hit.Count == 0;
    }
}

