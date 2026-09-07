using System;
using Godot;

namespace HitboxClone;

/// <summary>Tuning for one CPU difficulty. Nothing here touches damage, health or speed.</summary>
public sealed class BotSkillDef
{
    public string Name = "";
    public string Blurb = "";

    /// <summary>Radians of wander added to the aim direction.</summary>
    public float AimError;

    /// <summary>Seconds a target must stay visible before the bot opens fire.</summary>
    public float Reaction;

    /// <summary>How fast the bot can swing its aim, in radians/second. The main difficulty lever.</summary>
    public float AimSlew;

    /// <summary>
    /// How close the aim must get to the bot's intended direction before it fires, in radians.
    /// Roughly constant across difficulties — separation comes from <see cref="AimError"/>, which
    /// is what makes those shots miss.
    /// </summary>
    public float FireTolerance;

    /// <summary>Seconds of sustained fire before pausing.</summary>
    public float BurstLength;

    /// <summary>Seconds of pause between bursts.</summary>
    public float BurstRest;

    /// <summary>
    /// How readily this bot takes a vehicle, 0 to 1. Scales how far it will walk for one and how
    /// often it bothers at all. A Recruit that commandeers a tank is no longer a Recruit, so this
    /// is a difficulty lever as much as a behaviour one.
    /// </summary>
    public float VehicleAppetite;
}

/// <summary>
/// A CPU opponent.
///
/// The bot plays to its class's preferred range rather than always closing: a Tactician wants to be
/// in your face and a Marksman wants to be far away, so the same brain produces visibly different
/// opponents depending on the class it was handed.
///
/// Difficulty is expressed as aim error, reaction delay, how fast the bot can swing its aim, and
/// trigger discipline — never as bonus damage, health or speed. A bot that cheats on stats feels
/// unfair, whereas one that simply aims worse feels beatable.
///
/// Aim slew is the single most important of those. A bot that snaps its aim onto you the instant
/// you round a corner is brutal at any error tolerance, because you never get the moment of grace
/// a human opponent gives you while they drag their crosshair across.
/// </summary>
public sealed class BotBrain
{
    public static readonly BotSkillDef[] Skills =
    {
        new()
        {
            Name = "Recruit",
            Blurb = "Slow to spot you, slower to aim. Good for learning a class.",
            AimError = 0.30f, Reaction = 0.75f, AimSlew = 1.8f,
            FireTolerance = 0.14f, BurstLength = 0.55f, BurstRest = 1.00f,
            VehicleAppetite = 0.15f,
        },
        new()
        {
            Name = "Easy",
            Blurb = "Will lose a straight fight more often than not.",
            AimError = 0.26f, Reaction = 0.66f, AimSlew = 2.1f,
            FireTolerance = 0.14f, BurstLength = 0.60f, BurstRest = 0.90f,
            VehicleAppetite = 0.45f,
        },
        new()
        {
            Name = "Normal",
            Blurb = "Holds its own. Punishes standing still.",
            AimError = 0.135f, Reaction = 0.42f, AimSlew = 3.0f,
            FireTolerance = 0.15f, BurstLength = 0.95f, BurstRest = 0.60f,
            VehicleAppetite = 0.75f,
        },
        new()
        {
            Name = "Veteran",
            Blurb = "Tracks well and rarely wastes a shot.",
            AimError = 0.10f, Reaction = 0.34f, AimSlew = 3.7f,
            FireTolerance = 0.16f, BurstLength = 0.95f, BurstRest = 0.62f,
            VehicleAppetite = 1.0f,
        },
    };

    public static BotSkillDef Skill(int i) => Skills[Mathf.Clamp(i, 0, Skills.Length - 1)];

    readonly BotSkillDef skill;

    Pawn? target;
    float retargetIn;
    float aimNoiseIn;
    Vector2 aimNoise;
    float strafeSign = 1f;
    float strafeFlipIn;
    float sawTargetFor;

    /// <summary>How long since this bot last had eyes on anyone. Drives the hunt for the middle.</summary>
    float timeSinceContact;

    // The bot's own aim, swung toward the target rather than snapped onto it.
    float aimYaw;
    float aimPitch;
    bool aimInitialised;

    float burstTimer;
    bool resting;

    /// <summary>
    /// Upward climb from this bot's own sustained fire, in radians.
    ///
    /// Recoil used to live entirely on the human viewport — it is a field of MatchScreen.View — so
    /// only players paid it. A bot built its aim from scratch every frame and its crosshair did not
    /// move a millimetre however long it held the trigger, while the player fighting back was
    /// riding their own weapon up the whole time. That is most of what "the bots are too deadly"
    /// actually was: not that they aimed better, but that they were the only ones in the fight not
    /// being pushed off target by their own gun.
    ///
    /// Same weapon numbers, same recovery rate, same ceiling as the view applies. A bot on a long
    /// burst now sprays high exactly like a player on a long burst, which lands hardest on the
    /// difficulties that hold the trigger longest — which were the deadly ones.
    /// </summary>
    float recoil;
    int lastShotSeen;

    /// <summary>Radians a second the climb settles back down. Matches the human view exactly.</summary>
    const float RecoilRecovery = 0.55f;

    /// <summary>Ceiling on the climb, so a minigun does not point a bot at the sky forever.</summary>
    const float RecoilCeiling = 0.35f;

    public BotBrain(int skillIndex) { skill = Skill(skillIndex); }

    public PawnInput Think(float dt, Pawn self, Match match)
    {
        var input = new PawnInput();

        if (!aimInitialised) { aimYaw = self.Facing; aimPitch = 0f; aimInitialised = true; }

        retargetIn -= dt;
        strafeFlipIn -= dt;
        aimNoiseIn -= dt;

        if (target is not { Alive: true } || retargetIn <= 0f)
        {
            target = PickTarget(self, match);
            retargetIn = 0.7f;
        }

        // With nobody to shoot, still head for the objective rather than standing idle.
        if (target == null)
        {
            input.Aim = MathU.FromAngle(aimYaw);
            input.Pitch = aimPitch;
            if (match.ObjectiveFor(self) is { } lonelyObjective)
                input.Move = SteerToward(self, lonelyObjective);
            return input;
        }

        Vector3 delta3 = target.GlobalPosition - self.GlobalPosition;
        var toTarget = new Vector2(delta3.X, delta3.Z);
        float dist = toTarget.Length();
        if (dist < 0.01f) return input;

        Vector2 dir = toTarget / dist;

        // Wander the aim slightly rather than tracking perfectly, refreshed on a timer so the
        // error drifts like a hand rather than jittering like noise.
        if (aimNoiseIn <= 0f)
        {
            float a = (float)GD.RandRange(-Math.PI, Math.PI);
            float m = (float)GD.RandRange(0.0, skill.AimError);
            aimNoise = MathU.FromAngle(a, m);
            aimNoiseIn = 0.18f;
        }

        float wantYaw = MathU.Angle(dir) + aimNoise.X;
        float wantPitch = MathF.Atan2((target.Eye - self.Eye).Y, MathF.Max(0.5f, dist)) + aimNoise.Y * 0.5f;

        // Swing toward the target at a bounded rate. This is what gives a player time to react.
        aimYaw = MathU.MoveAngleToward(aimYaw, wantYaw, skill.AimSlew * dt);
        aimPitch = Mathf.MoveToward(aimPitch, wantPitch, skill.AimSlew * dt);

        // The bot's own gun pushing it off target, on the same terms as the player's.
        if (self.ShotCounter != lastShotSeen)
        {
            recoil += self.Weapon.Recoil * (self.ShotCounter - lastShotSeen);
            lastShotSeen = self.ShotCounter;
        }

        recoil = Mathf.MoveToward(recoil, 0f, RecoilRecovery * dt);
        recoil = MathU.Clamp(recoil, 0f, RecoilCeiling);

        input.Aim = MathU.FromAngle(aimYaw);
        input.Pitch = MathU.Clamp(aimPitch + recoil, -Pawn.MaxPitch, Pawn.MaxPitch);

        // Hold the class's preferred band: close if too far, back off if too close.
        //
        // A juggernaut has no band to hold. It carries a blade and nothing else, so every metre
        // between it and its target is a metre of nothing happening — it walks all the way in and
        // stays there. This is also what makes the mode read correctly from the other side: the
        // hero is the thing that will not stop coming.
        float preferred = self.HasSaber ? 2f : PreferredRange(self.Class);
        float error = dist - preferred;

        if (strafeFlipIn <= 0f)
        {
            strafeSign = GD.Randf() < 0.5f ? -1f : 1f;
            strafeFlipIn = (float)GD.RandRange(0.8, 2.2);
        }

        var strafe = new Vector2(-dir.Y, dir.X) * strafeSign;

        bool los = match.HasLineOfSight(self, target);
        sawTargetFor = los ? sawTargetFor + dt : 0f;
        timeSinceContact = los ? 0f : timeSinceContact + dt;

        // Where the bot wants to be, in priority order. Fighting comes first, but only when there
        // is actually a fight — otherwise it is travelling, and travelling is what the graph is for.
        // Fighting outranks everything. Putting errands first meant a bot with an enemy in front
        // of it would turn and jog off to a crate, and engagement collapsed on the arenas whose
        // pickups are furthest apart.
        bool engaged = los && dist < 30f;

        Vector3? destination = null;

        // A vehicle outranks a crate and outranks the objective — it is the single biggest swing
        // available on the map. Not while someone is shooting at you from close range, though:
        // walking thirty metres across open ground to reach a tank is how you die on the way.
        vehicleCooldown -= dt;

        // Carrying the flag outranks the lot, including a tank. Every errand on this list is a
        // reason to be somewhere other than your own base, and a flag carrier has exactly one job.
        bool urgent = match.ObjectiveIsUrgent(self);

        var wantedRig = urgent || (engaged && dist < 16f) || vehicleCooldown > 0f
            ? null
            : match.NearestFreeVehicle(self.GlobalPosition, VehicleInterestRange);

        if (urgent && match.ObjectiveFor(self) is { } errand)
        {
            destination = errand;
        }
        else if (wantedRig is { } rig)
        {
            destination = rig.GlobalPosition;

            // Press it every tick within reach rather than on arrival, because "arrived" is a
            // moving target on a hull four metres wide.
            if (rig.CanBoard(self)) { input.Use = true; drivingFor = 0f; reverseTotal = 0f; }
        }
        // Hurt and there is a med kit close by: go and get it. Outranks the fight unless the fight
        // is right on top of you, because disengaging at range to heal is the correct play and
        // disengaging at five metres is just dying more slowly.
        //
        // The thresholds were written when an arena carried three med kits, so "is there one
        // within sixty metres" was a real question and usually answered no. There are twenty-eight
        // now, spread so that nowhere on the floor is more than about thirty-five metres from one —
        // and at the old numbers that meant every bot below half health broke off, every time,
        // from anywhere. Four bots ran supply lines instead of fighting: damage across the suite
        // fell by roughly two thirds and not one special was used in six of the nine scenarios.
        //
        // So both numbers tightened. A bot leaves a fight when it is genuinely in trouble, and only
        // for a kit it can reach without crossing the map to get there.
        else if (self.HealthFrac < 0.42f && !(engaged && dist < 14f)
                 && match.NearestHealth(self.GlobalPosition, 26f, 10f) is { } med)
        {
            destination = med;
        }
        // In a mode whose objective is somewhere other than the fight, the objective outranks
        // standing and trading shots — but not a fight that is already on top of you. Same shape
        // as the health rule above and for the same reason: breaking off at range is a decision,
        // breaking off at five metres is just dying while facing the wrong way.
        else if (match.ObjectiveOutranksFighting && !(engaged && dist < 18f)
                 && match.ObjectiveFor(self) is { } errandNow
                 && errandNow.DistanceTo(self.GlobalPosition) > match.ObjectiveStopRange)
        {
            destination = errandNow;
        }
        else if (engaged)
        {
            destination = null;
        }
        else if (!self.SlotsFull
                 && match.NearestPickup(self, 55f, 12f) is { } loot)
        {
            // A free slot rather than an empty hand. With two slots a bot that already swapped once
            // still has room for a second gun, and gating on "carrying the class weapon" meant it
            // stopped looking the moment it picked anything up.
            destination = loot;
        }
        else if (match.ObjectiveFor(self) is { } objective
                 && objective.DistanceTo(self.GlobalPosition) > match.ObjectiveStopRange)
        {
            destination = objective;
        }
        else if (timeSinceContact > 4f && match.Arena.RallySpots.Count > 0)
        {
            // Nobody seen for a while. Chasing the nearest enemy across a map this size had four
            // bots each following a different quarry and converging on nothing.
            //
            // They rally on a shared point instead, rotating on a clock so everyone picks the same
            // one and a stalemate resolves. The capture zones are used because they are the one
            // set of positions already proven standable and reachable in every arena — the world
            // origin is not: in the Glasshouse it is inside the central tower.
            int spot = (int)(match.Elapsed / 6f) % match.Arena.RallySpots.Count;
            destination = match.Arena.RallySpots[spot];
        }
        else
        {
            destination = target.GlobalPosition;
        }

        Vector2 move;

        if (destination is { } goal)
        {
            // Route to it. Falls back to walking at the goal only when the graph has nothing —
            // mid-air, or somewhere genuinely disconnected.
            move = Navigate(self, match, goal, dt);

            if (move.LengthSquared() < 0.01f)
            {
                Vector3 straight = goal - self.GlobalPosition;
                move = MathU.Norm(new Vector2(straight.X, straight.Z));
            }
        }
        else
        {
            // In a fight and able to see them: hold the class's preferred band and circle.
            ClearPath();

            if (MathF.Abs(error) < 3f) move = strafe;                             // in the pocket
            else if (error > 0f) move = MathU.Norm(dir * 1.1f + strafe * 0.5f);   // too far
            else move = MathU.Norm(-dir * 1.1f + strafe * 0.5f);                  // too close
        }

        // Only freehand steering needs the pit backstop. Applying it to a routed move actively
        // fought the router: crossing a bridge, the look-ahead sees the moat to one side, vetoes
        // the correct step, and knocks the bot off its own path — which is why the two arenas
        // split by a gap were the quietest on the board.
        if (destination is null) move = AvoidPits(self, match, move);

        // Weapon crates need a deliberate hold now, so a bot standing on one has to actually ask
        // for it. Without this bots simply walked over every gun on the map and took none of them.
        input.UseHeld = match.WeaponCrateInReach(self, combatOnly: true);

        move = Unstick(dt, self, move);
        input.Move = MathU.ClampLen(move, 1f);
        input.Jump = wantsJump;
        input.JumpHeld = wantsJump;

        // Compared against where the bot *thinks* the target is, not where it actually is. Gating
        // on the true direction instead made low-skill bots simply hold fire until they happened
        // to be accurate, so aim error never turned into misses and every difficulty below
        // Veteran landed the same damage per second.
        bool lined = MathF.Abs(MathU.AngleDiff(wantYaw, aimYaw)) < skill.FireTolerance;
        bool wants = los && sawTargetFor > skill.Reaction && lined && dist < self.Weapon.Range;

        // Never fire an explosive into your own blast. A bot with a rocket launcher will otherwise
        // put one into the chest of whoever walks up to it and take the splash itself — which is
        // not a difficulty setting, it is a bot killing itself on purpose.
        if (self.Weapon.Explodes && dist < self.Weapon.BlastRadius * 1.2f) wants = false;

        input.Fire = TriggerDiscipline(dt, wants);

        // Dash to break a fight it is losing. Higher skills bail out more readily.
        if (self.HealthFrac < 0.4f && GD.Randf() < 0.004f * (1f + skill.AimSlew * 0.4f))
            input.Dash = true;

        // Swing when the fight has closed to arm's length. A bot that keeps trying to line up a
        // rifle on someone standing on top of it reads as broken, and melee out-damages every
        // class weapon at this range.
        input.Melee = los && lined && dist < Match.MeleeRange * 0.85f;

        // A juggernaut spends its power the moment the fight is close enough to be worth it. These
        // are the biggest things in the game and a bot sitting on one is a bot handing the crown
        // over politely.
        input.Special = self.IsJuggernaut
            ? self.CrownPowerReady && los && dist < CrownPowerRange(self)
            : WantsSpecial(self, dist, los);
        input.ClassAbility = WantsClassAbility(self, dist, los);

        // Aim down sights when settling into a shot at range, which is also what makes bots
        // visibly slow down as they commit — a read the player can use.
        //
        // The same button raises the saber, so for a juggernaut the rule inverts completely: block
        // on the way in, while there is still distance for people to shoot across, and drop it at
        // swinging range where the block is worth nothing and the slow is worth a lot. A bot that
        // walked in with the blade down would die to massed fire before it arrived, and one that
        // never dropped it would stand there at half speed swinging at nobody.
        input.Ads = self.HasSaber
            ? los && dist > Match.MeleeRange
            : los && dist > 9f && sawTargetFor > skill.Reaction * 0.6f;

        // Sprint whenever travelling, not only when closing on someone unseen.
        //
        // The old rule needed no line of sight *and* a big range error, which on the small map was
        // most of the time a bot was walking. On a map five times the size it almost never fired:
        // a bot crossing to a crate or a capture point usually has someone in view somewhere, so
        // it walked the whole way. Measured at about four metres a second across a two hundred and
        // seventy-eight metre arena, which is a bot that never arrives anywhere.
        bool travelling = destination is { } far
                          && far.DistanceTo(self.GlobalPosition) > 14f;

        // Never sprint past a shot you could be taking.
        //
        // Sprinting blocks firing — that is the whole cost of it — and the rule above set it from
        // *travelling alone*. In an objective mode a bot's destination is almost always a post or a
        // flag more than fourteen metres away, so it sprinted permanently, which meant it could
        // never shoot. The harness measured what that does: a full Dominion match, twelve bots,
        // two hundred and ninety metres walked each, and eight shots fired between them. They were
        // running past each other with their guns down.
        //
        // Deathmatch hid it completely, because there the destination goes null the moment a bot
        // engages, so the sprint switches itself off. Every mode with somewhere to *be* had the
        // bug and the one mode without an objective did not.
        // Bounded by the band the bot actually wants to fight in, not by the weapon's reach. The
        // first version used the whole range, which for a Marksman is a hundred and twenty metres —
        // so a sniper with a sightline across the map never travelled again, and the posts went
        // uncaptured for the opposite reason to before. A shot worth stopping for is one you would
        // have closed to anyway.
        // Never for a flag carrier. Their errand outranks everything, and "stop and trade shots
        // with whoever you can see" is the precise behaviour that loses the flag — the same reason
        // they do not stop for a gun or a med kit on the way home.
        bool couldShoot = !urgent && los && dist < MathF.Min(self.Weapon.Range, preferred * 1.6f);

        input.Sprint = !input.Ads && !couldShoot && (travelling || (!los && error > 8f));

        // Crouch to steady a long shot. Higher skills do it more.
        input.Crouch = input.Ads && dist > 22f && GD.Randf() < 0.02f * skill.AimSlew;

        return input;
    }

    Vector3 lastPos;
    bool hasLastPos;
    float stillFor;
    float unstickFor;
    Vector2 unstickDir;

    /// <summary>
    /// Notice when the bot is pushing against something and go round it.
    ///
    /// The router plans over a grid of standable surfaces; it does not know about the corner of a
    /// crate the pawn's capsule is actually caught on. Without this a bot wedged on a piece of
    /// cover leans into it for the rest of the match, which reads exactly like a bot that has
    /// stopped thinking — and with the outer districts full of low scatter there is a great deal
    /// more to get caught on than there used to be.
    /// </summary>
    Vector2 Unstick(float dt, Pawn self, Vector2 move)
    {
        Vector3 now = self.GlobalPosition;

        if (hasLastPos && move.LengthSquared() > 0.04f)
        {
            float moved = new Vector2(now.X - lastPos.X, now.Z - lastPos.Z).Length();

            // Compared against what a walk should cover in this tick, not a flat number, so it
            // does not fire on a bot that is deliberately creeping.
            stillFor = moved < self.Class.Speed * dt * 0.25f ? stillFor + dt : 0f;
        }
        else stillFor = 0f;

        lastPos = now;
        hasLastPos = true;

        if (unstickFor > 0f)
        {
            unstickFor -= dt;
            return unstickDir;
        }

        if (stillFor > 0.55f)
        {
            // Sidestep, biased to one side at random, with a little of the original intent kept so
            // it slides along the obstruction rather than bouncing straight off it.
            var side = new Vector2(-move.Y, move.X) * (GD.Randf() < 0.5f ? -1f : 1f);

            unstickDir = MathU.Norm(side * 1.2f + move * 0.4f);
            unstickFor = 0.7f;
            stillFor = 0f;
            return unstickDir;
        }

        return move;
    }

    // ---- driving ----
    //
    // A separate decision layer from walking, deliberately. A hull is nothing like a pawn: it
    // cannot strafe, cannot climb the towers, turns on a radius, and aims with a turret that moves
    // independently of where it is going. Routing it through the pawn navigation graph would send
    // a tank at a staircase it cannot climb and leave it grinding against the bottom step.
    //
    // So driving is steering rather than pathfinding: point at the destination, feel ahead for
    // obstacles, and back out when stuck. The arenas are open enough at ground level that this
    // gets a vehicle where it is going, and a vehicle that occasionally takes the long way round
    // reads as a driver rather than as a bug.

    float stuckFor;
    float reverseFor;
    float reverseTotal;
    float drivingFor;

    /// <summary>Seconds before this bot will look for a vehicle again after leaving one.</summary>
    float vehicleCooldown;

    /// <summary>Hull integrity below which a bot bails out rather than going down with it.</summary>
    const float BailHealthFrac = 0.28f;

    /// <summary>Nothing to shoot at for this long and the bot gives the vehicle back.</summary>
    const float BoredOfDrivingAfter = 22f;

    /// <summary>
    /// Whether this bot would walk over to that vehicle. Distance scales with appetite, so a
    /// Veteran crosses the map for a tank and a Recruit only takes one it nearly trips over.
    /// </summary>
    public float VehicleInterestRange => 8f + 34f * skill.VehicleAppetite;

    /// <summary>
    /// One tick of driving. Returns pad intent exactly as <see cref="Think"/> does, so the vehicle
    /// cannot tell a bot from a player — which is what keeps one set of driving code honest.
    /// </summary>
    public PawnInput DriveThink(float dt, Pawn self, Vehicle rig, Match match)
    {
        var input = new PawnInput();

        drivingFor += dt;
        retargetIn -= dt;

        if (target is not { Alive: true } || retargetIn <= 0f)
        {
            target = PickTarget(self, match);
            retargetIn = 0.7f;
        }

        // Bail out: the hull is nearly gone, or there has been nothing to do with it for a while.
        // Leaving a wreck-in-waiting is better than dying inside it, and a bot that never gets out
        // hogs the only tank on the map for the whole match.
        bool hulk = rig.Health / rig.Def.Health < BailHealthFrac;
        bool bored = drivingFor > BoredOfDrivingAfter && target == null;
        bool wedged = reverseTotal > 6f;

        if (hulk || bored || wedged)
        {
            // A cooldown rather than an immediate re-evaluation, or the bot walks two paces from
            // the hull it just abandoned, decides a vehicle is the best thing on the map, and
            // climbs straight back in.
            vehicleCooldown = 14f;
            drivingFor = 0f;
            reverseTotal = 0f;
            input.Use = true;
            return input;
        }

        Vector3 goal = target?.GlobalPosition
                       ?? match.ObjectiveFor(self)
                       ?? Vector3.Zero;

        if (rig.Def.Flies) DrivePlane(dt, rig, goal, ref input);
        else DriveGround(dt, rig, goal, ref input, match);

        AimTurret(dt, self, rig, match, ref input);
        return input;
    }

    void DriveGround(float dt, Vehicle rig, Vector3 goal, ref PawnInput input, Match match)
    {
        Vector3 to = goal - rig.GlobalPosition;
        var flat = new Vector2(to.X, to.Z);
        float dist = flat.Length();

        float want = MathU.Angle(flat);
        float off = MathU.AngleDiff(want, rig.Facing);

        // Feel ahead. Three whiskers rather than one: a single forward ray tells you that you are
        // about to hit something but not which way to go round it.
        float probe = 6f + rig.HorizontalSpeed * 0.8f;
        float clearAhead = Probe(rig, match, 0f, probe);
        float clearLeft = Probe(rig, match, -0.6f, probe * 0.8f);
        float clearRight = Probe(rig, match, 0.6f, probe * 0.8f);

        if (clearAhead < 1f)
            off = clearLeft > clearRight ? -1.2f : 1.2f;

        // Stuck: throttle down and going nowhere. Reverse out and turn, rather than grinding
        // against whatever it is until the match ends.
        if (rig.HorizontalSpeed < 2f && reverseFor <= 0f) stuckFor += dt;
        else stuckFor = 0f;

        if (stuckFor > 1.2f) { reverseFor = 1.1f; stuckFor = 0f; }

        if (reverseFor > 0f)
        {
            reverseFor -= dt;
            reverseTotal += dt;
            input.RawMove = new Vector2(1f, 1f);      // back up, turning as it goes
            return;
        }

        input.RawMove.X = MathU.Clamp(off * 1.6f, -1f, 1f);

        // Ease off the throttle in a hard turn, and stop short of the target rather than shunting
        // it around the map. Ramming is the car's job and it has its own reason to close.
        float turnEase = 1f - MathU.Clamp01(MathF.Abs(off) / 2.2f) * 0.65f;
        float wantSpeed = dist < (rig.Def.RamDamage > 0f && rig.Def.Gun == null ? 3f : 14f) ? 0f : 1f;

        input.RawMove.Y = -wantSpeed * turnEase;
    }

    /// <summary>
    /// Fraction of the probe distance that is clear ahead of the hull, 0 blocked to 1 open.
    /// Cast from the nose at hull height, offset by <paramref name="yawOffset"/> radians.
    /// </summary>
    static float Probe(Vehicle rig, Match match, float yawOffset, float distance)
    {
        float a = rig.Facing + yawOffset;
        var dir = new Vector3(MathF.Cos(a), 0f, MathF.Sin(a));

        Vector3 from = rig.GlobalPosition + Vector3.Up * (rig.Def.HalfExtents.Y + 0.3f);
        return match.ClearAhead(rig, from + dir * (rig.Def.HalfExtents.X + 0.2f), dir, distance);
    }

    void DrivePlane(float dt, Vehicle rig, Vector3 goal, ref PawnInput input)
    {
        Vector3 to = goal - rig.GlobalPosition;
        var flat = new Vector2(to.X, to.Z);

        input.RawMove.X = MathU.Clamp(MathU.AngleDiff(MathU.Angle(flat), rig.Facing) * 1.6f, -1f, 1f);
        input.RawMove.Y = -1f;

        // Hold a cruising height rather than chasing the target's altitude. A plane that dives at
        // whatever it is shooting flies into the floor, every time.
        const float Cruise = 11f;
        float wantPitch = MathU.Clamp((Cruise - rig.GlobalPosition.Y) * 0.05f, -0.35f, 0.5f);

        // Look Y is a pitch *rate* on a plane, so this is a proportional controller onto the
        // attitude the bot wants rather than a direct assignment.
        input.RawLook.Y = -MathU.Clamp((wantPitch - rig.Pitch) * 3f, -1f, 1f);
    }

    /// <summary>
    /// Lay the gun on the target and decide whether to fire.
    ///
    /// The turret is slewed at the difficulty's own aim rate, exactly as on foot — a bot that
    /// snaps a cannon onto you the instant you appear is unfair in a way that has nothing to do
    /// with the vehicle being strong.
    /// </summary>
    void AimTurret(float dt, Pawn self, Vehicle rig, Match match, ref PawnInput input)
    {
        if (rig.Def.Gun is not { } gun || target is not { Alive: true } shootAt) return;

        Vector3 muzzle = rig.Seat;

        // An explosive shell wants the ground under them, not their chest: the splash is the
        // larger half of the cannon, and a near miss at their feet still kills.
        Vector3 at = gun.Explodes
            ? shootAt.GlobalPosition + Vector3.Up * 0.2f
            : shootAt.Eye;

        Vector3 to = at - muzzle;
        float dist = to.Length();
        if (dist < 0.01f) return;

        float wantYaw = MathF.Atan2(to.Z, to.X);
        float wantPitch = MathF.Asin(MathU.Clamp(to.Y / dist, -1f, 1f));

        float yawOff = MathU.AngleDiff(wantYaw, rig.TurretYaw);
        float pitchOff = wantPitch - rig.TurretPitch;

        // Rate control, capped by the difficulty's slew. Planes have no turret and steer their
        // guns by flying, so they are left alone here.
        if (rig.HasTurret)
        {
            float rate = skill.AimSlew * 0.55f;
            input.RawLook.X = MathU.Clamp(yawOff * rate, -1f, 1f);
            input.RawLook.Y = -MathU.Clamp(pitchOff * rate, -1f, 1f);
        }

        bool lined = rig.AimDir.Dot(to / dist) > 0.985f;
        bool inRange = dist < gun.Range;

        // Minimum engagement range for a shell. Even with the hull absorbing its own blast, firing
        // at something standing against the tracks means the round detonates on the hull's own
        // nose and does nothing.
        bool tooClose = gun.Explodes && dist < 9f;

        input.Fire = lined && inRange && !tooClose && match.HasLineOfSight(self, shootAt);
    }

    /// <summary>
    /// When to spend the special. Each class wants a different moment, and a bot that fires them
    /// on cooldown regardless of range would just waste them.
    /// </summary>
    /// <summary>How close a fight has to be before a juggernaut spends its power on it.</summary>
    static float CrownPowerRange(Pawn self) => self.Crown!.Kind switch
    {
        // Wrath is a radius around him, so it is only worth firing with someone inside it.
        JuggernautKind.Achilles => Match.WrathRadius * 0.8f,

        // The beam reaches, and rising off the ground is worth doing before they are on top of you.
        JuggernautKind.Prometheus => Match.FireRange * 0.7f,

        // Both defensive: spent when someone is actually shooting.
        _ => 26f,
    };

    bool WantsSpecial(Pawn self, float dist, bool los)
        => self.SpecialReady && WantsAbility(self.Faction.Special, self, dist, los);

    /// <summary>
    /// Whether to spend the class ability now.
    ///
    /// The judgement table below has always had branches for all four class kinds. It was keyed on
    /// the *faction's* special, so those four could never be reached — the bot brain has carried
    /// dead rules for Shockwave, Frag, Overdrive and Focus for as long as the abilities themselves
    /// have been unreachable.
    /// </summary>
    bool WantsClassAbility(Pawn self, float dist, bool los)
        => self.ClassAbilityReady && WantsAbility(self.Class.Special, self, dist, los);

    bool WantsAbility(SpecialKind kind, Pawn self, float dist, bool los)
    {
        // Higher skills read the moment better; lower ones fire them off roughly.
        float judgement = 0.35f + skill.AimSlew * 0.12f;
        if (GD.Randf() > judgement * 0.08f) return false;

        return kind switch
        {
            // Only worth it with someone inside the blast.
            SpecialKind.Shockwave => dist < self.Class.BlastRadius * 0.8f,

            // Lobbed, so it does not need line of sight — that is the point of it.
            SpecialKind.Frag => dist is > 6f and < 32f,

            // Committing abilities: spend them when a fight is actually happening.
            SpecialKind.Overdrive => los && dist < 22f,
            SpecialKind.Focus => los && dist > 16f,

            // Worth exactly as much as you have just been hurt, so it is spent when hurt and not
            // before. Held above a floor so a bot does not cash a scratch.
            SpecialKind.SecondWind => self.DamageBanked > self.Class.Health * 0.3f,

            // Information is worth most when you have none: spent on losing contact rather than
            // during a fight you can already see.
            SpecialKind.Revelation => !los && timeSinceContact > 2.5f,

            // Ground worth standing on, planted while the fight is close enough to stand in it.
            SpecialKind.Bloom => dist < 18f && self.HealthFrac < 0.8f,

            // A bluff is worth nothing unless someone is watching it.
            SpecialKind.Understudy => los && dist is > 8f and < 40f,

            _ => false,
        };
    }

    /// <summary>
    /// Breaks fire into bursts. Without this a bot holds the trigger down from the moment it has
    /// a target, which reads as inhuman and makes the fast-firing classes far deadlier than the
    /// difficulty intends.
    /// </summary>
    bool TriggerDiscipline(float dt, bool wantsToFire)
    {
        if (!wantsToFire)
        {
            burstTimer = 0f;
            resting = false;
            return false;
        }

        burstTimer += dt;

        if (resting)
        {
            if (burstTimer < skill.BurstRest) return false;
            burstTimer = 0f;
            resting = false;
            return true;
        }

        if (burstTimer < skill.BurstLength) return true;

        burstTimer = 0f;
        resting = true;
        return false;
    }

    // ---- navigation ----

    readonly List<Vector3> path = new();
    int pathIndex;
    float repathIn;
    Vector3 pathGoal;
    bool wantsJump;

    void ClearPath()
    {
        path.Clear();
        pathIndex = 0;
        wantsJump = false;
    }

    /// <summary>
    /// Follows a route through the arena's walkable graph toward <paramref name="goal"/>.
    ///
    /// Replans on a timer rather than every frame — A* over a few thousand nodes is cheap but not
    /// free, and four bots doing it sixty times a second would be. It also replans when the goal
    /// itself has moved far enough that the old route is answering a stale question.
    /// </summary>
    Vector2 Navigate(Pawn self, Match match, Vector3 goal, float dt)
    {
        repathIn -= dt;

        bool stale = path.Count == 0
                     || pathIndex >= path.Count
                     || repathIn <= 0f
                     || goal.DistanceTo(pathGoal) > 7f;

        if (stale)
        {
            pathGoal = goal;
            repathIn = (float)GD.RandRange(0.7, 1.1);   // staggered, so four bots never replan together
            pathIndex = 0;

            if (!match.Nav.TryFindPath(self.GlobalPosition, goal, path)) path.Clear();
        }

        wantsJump = false;
        if (path.Count == 0) return Vector2.Zero;

        // Retire waypoints already reached. Generous horizontally and vertically, because a bot
        // shoved by a blast should resume the route rather than reverse into a waypoint it passed.
        while (pathIndex < path.Count)
        {
            Vector3 d = path[pathIndex] - self.GlobalPosition;
            if (new Vector2(d.X, d.Z).Length() < 1.8f && MathF.Abs(d.Y) < 2.5f) pathIndex++;
            else break;
        }

        if (pathIndex >= path.Count) { ClearPath(); return Vector2.Zero; }

        Vector3 delta = path[pathIndex] - self.GlobalPosition;

        // The graph links surfaces up to a jump apart, so a step that rises is a step that has to
        // be jumped. Without this bots route onto decks and then walk into the side of them.
        if (delta.Y > 0.8f && new Vector2(delta.X, delta.Z).Length() < 4f) wantsJump = true;

        return MathU.Norm(new Vector2(delta.X, delta.Z));
    }

    /// <summary>
    /// Refuses to walk off an edge.
    ///
    /// The graph has no nodes over a pit, so a routed bot already avoids them. This remains as a
    /// backstop for the freehand steering used in a close fight, where a bot circling an opponent
    /// at the lip of the Glasshouse moat could still back into it.
    /// </summary>
    static Vector2 AvoidPits(Pawn self, Match match, Vector2 move)
    {
        if (match.Arena.Pits.Count == 0 || move.LengthSquared() < 0.01f) return move;

        const float LookAhead = 3.2f;

        // Only ground-level movement can fall in; a bot up on a deck is already above the moat.
        if (self.GlobalPosition.Y > 2.5f) return move;

        if (!Blocked(self, match, move, LookAhead)) return move;

        var left = new Vector2(-move.Y, move.X);
        if (!Blocked(self, match, left, LookAhead)) return left;
        if (!Blocked(self, match, -left, LookAhead)) return -left;

        // Boxed in on three sides: back away rather than stand still. Freezing at a pit edge is
        // worse than retreating — a motionless bot is free target practice, and it read as the
        // AI being broken rather than cautious.
        return -MathU.Norm(move);
    }

    static bool Blocked(Pawn self, Match match, Vector2 dir, float distance)
    {
        Vector2 n = MathU.Norm(dir);
        var ahead = self.GlobalPosition + new Vector3(n.X, 0f, n.Y) * distance;
        return match.Arena.IsOverPit(ahead);
    }

    static Vector2 SteerToward(Pawn self, Vector3 point)
    {
        Vector3 d = point - self.GlobalPosition;
        return MathU.ClampLen(MathU.Norm(new Vector2(d.X, d.Z)), 1f);
    }

    /// <summary>
    /// The distance a bot tries to hold from whoever it is fighting.
    ///
    /// Derived from the weapon rather than looked up by name.
    ///
    /// It used to be a table of four class names with a default of eighteen metres, which was fine
    /// while there were exactly four classes. There are twelve now, and all eight of the new ones
    /// fell through to the default — so a bot Lector with a hundred-and-forty-metre marking rifle
    /// walked to eighteen metres to use it, and a Sinew that should be closing held the same line
    /// as a Marksman. A name table cannot know about a class nobody remembered to add to it; the
    /// weapon in the bot's hands always can.
    ///
    /// Roughly a third of the weapon's reach, floored so nothing tries to fight from on top of its
    /// target and capped so nothing tries to hold a lane it cannot see down. It reproduces the old
    /// four almost exactly — Tactician 30m/3 ≈ 10, Marksman 120/3 = 40 — while also giving the
    /// Anatomist five metres and the Lector forty-seven, which is what those want.
    /// </summary>
    static float PreferredRange(ClassDef c)
        => Mathf.Clamp(c.Weapon.Range / 3f, 5f, 48f);

    static Pawn? PickTarget(Pawn self, Match match)
    {
        Pawn? best = null;
        float bestDist = float.PositiveInfinity;

        foreach (var p in match.Pawns)
        {
            if (p == self || !p.Alive) continue;
            if (match.Settings.Def.Teams && Match.SameTeam(self, p)) continue;

            float d = self.GlobalPosition.DistanceTo(p.GlobalPosition);

            // A Muse who has just left a decoy is not a target until it expires — the bluff has to
            // work on a bot or it only ever works on a person, which would make the whole ability
            // an anti-human weapon rather than an ability.
            if (p.HardToFind && d > 6f) continue;

            // And someone lit up by Revelation is picked over anyone else, which is what makes the
            // Custodian's pulse worth something to a team rather than only to its caster.
            if (p.RevealedFor > 0f) d *= 0.5f;

            // The juggernaut is the only target worth anything, so bots hunt it from a long way
            // off. Without this they would fight whoever was nearest and the mode would resolve
            // itself entirely by accident.
            if (p.IsJuggernaut && !self.IsJuggernaut) d *= 0.2f;

            if (d >= bestDist) continue;
            bestDist = d;
            best = p;
        }

        return best;
    }
}
