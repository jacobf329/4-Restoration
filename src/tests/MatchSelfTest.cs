using System;
using System.Collections.Generic;
using Godot;

namespace HitboxClone;

/// <summary>
/// Plays full bot-vs-bot matches with no cameras, meshes or lights, checking simulation invariants
/// every frame.
///
/// It runs across real frames rather than in a loop because the simulation depends on Godot's
/// physics step — <c>MoveAndSlide</c> and space-state queries are only valid inside it. So
/// <see cref="Begin"/> starts a match and <see cref="Step"/> is pumped from
/// <c>Main._PhysicsProcess</c> until every scenario has run.
///
/// That "inside it" is load-bearing, and it was wrong for a long time: <see cref="Step"/> used to
/// be pumped from <c>_Process</c>, where Godot refuses a space query and returns an empty result.
/// Any check asking whether something was inside geometry therefore passed unconditionally.
/// </summary>
public static class MatchSelfTest
{
    /// <summary>
    /// Simulated seconds per scenario before it is judged a pass. Raised with the arenas: across
    /// a 124x92 map a two-a-side team match could genuinely run twelve seconds without the sides
    /// meeting, which failed the engagement checks for a reason that was not a defect.
    /// </summary>
    const float SecondsPerMatch = 28f;

    /// <summary>A projectile list longer than this means shots are not being retired.</summary>
    const int MaxReasonableShots = 400;

    static Main app = null!;
    static readonly List<MatchSettings> scenarios = new();
    static int scenarioIndex;
    static Match? current;
    static float elapsed;
    static int failures;
    static int checks;
    static bool done;

    /// <summary>Specials fired across every scenario. Asserted once, at the end.</summary>
    static int specialsAcrossTheRun;

    public static void Begin(Main main)
    {
        app = main;
        failures = 0;
        checks = 0;
        scenarioIndex = 0;
        done = false;
        specialsAcrossTheRun = 0;

        // One scenario per mode, plus a hard-skill deathmatch, since bot aggression is what
        // stresses the collision and projectile paths hardest.
        // One scenario per mode, plus a hard-skill deathmatch, and spread across both arenas so
        // neither layout can rot unnoticed.
        scenarios.Clear();
        // Every difficulty on identical mode and arena first, so the printed damage rates are
        // directly comparable and a difficulty inversion would be obvious. Comparing across modes
        // would not show it: King of the Hill herds bots into one zone, so it reads as harder than
        // a deathmatch regardless of skill.
        for (int skill = 0; skill < BotBrain.Skills.Length; skill++)
            scenarios.Add(new MatchSettings { Mode = GameMode.Deathmatch, ScoreLimit = 8, BotSkill = skill, ArenaIndex = 0 });

        // Then the remaining modes and the second arena, for coverage.
        scenarios.Add(new MatchSettings { Mode = GameMode.TeamDeathmatch, ScoreLimit = 10, BotSkill = 1, ArenaIndex = 1 });
        // Elimination with a low round limit, so the round loop actually runs several times inside
        // the window rather than the mode ending on its first survivor.
        scenarios.Add(new MatchSettings { Mode = GameMode.Elimination, ScoreLimit = 2, BotSkill = 3, ArenaIndex = 0 });
        scenarios.Add(new MatchSettings { Mode = GameMode.KingOfTheHill, ScoreLimit = 12, BotSkill = 2, ArenaIndex = 1 });

        // Capture the flag, played by bots for real rather than only posed by hand. The unit test
        // proves the rules; this proves four bots in a live arena do not deadlock, wander, or find
        // some way to hold a flag that the rules never anticipated.
        scenarios.Add(new MatchSettings { Mode = GameMode.CaptureTheFlag, ScoreLimit = 9, BotSkill = 3, ArenaIndex = 3 });

        // A time limit short enough to actually elapse inside the test window, so the clock's win
        // condition is exercised rather than assumed.
        scenarios.Add(new MatchSettings
        {
            Mode = GameMode.Deathmatch, ScoreLimit = 99, BotSkill = 2, ArenaIndex = 2,
            TimeLimitSeconds = 7,
        });

        // Juggernaut, played for real. The unit test proves the rules; this proves twelve bots left
        // alone actually converge on the crown and pass it around rather than milling about.
        juggernautScenario = scenarios.Count;
        scenarios.Add(new MatchSettings { Mode = GameMode.Juggernaut, ScoreLimit = 40, BotSkill = 3, ArenaIndex = 2 });

        // Dominion, played for real, with a small pool so the bleed and the win condition both
        // actually land inside the window. The unit test proves the capture rules; this proves a
        // full roster of bots spreads out over the posts instead of all converging on one.
        dominionScenario = scenarios.Count;
        scenarios.Add(new MatchSettings { Mode = GameMode.Dominion, ScoreLimit = 50, BotSkill = 2, ArenaIndex = 3 });

        // A full roster, on the biggest layout. Everything that scales with fighter count is only
        // ever exercised by building that many of them.
        crowdScenario = scenarios.Count;
        scenarios.Add(new MatchSettings { Mode = GameMode.Deathmatch, ScoreLimit = 30, BotSkill = 2, ArenaIndex = 1 });

        // A scenario that actually drives. Every other vehicle check tests the definitions, and
        // none of them ever moved a hull — which is how three separate faults shipped together:
        // the raw stick never reached the vehicle, gravity ate the acceleration budget out of a
        // shared 3D MoveToward, and the node was never rotated to its heading.
        driveScenario = scenarios.Count;
        scenarios.Add(new MatchSettings { Mode = GameMode.Deathmatch, ScoreLimit = 99, BotSkill = 0, ArenaIndex = 0 });

        TestLog.Line("=== HitboxClone match self-test ===");
        StartScenario();
    }

    // ---- driving ----

    const float DriveForwardTime = 1.5f;
    const float DriveSteerTime = 2f;
    const float DriveFireTime = 5f;

    static int driveScenario = -1;
    static int crowdScenario = -1;
    static int juggernautScenario = -1;
    static int dominionScenario = -1;

    /// <summary>Damage landed across every scored scenario. See the note where it is added to.</summary>
    static float damageAcrossTheRun;
    static readonly List<(Vehicle Hull, Vector3 Start, float StartFacing)> driveProbes = new();
    static float drivePhase;
    static Vector2 driveStick;
    static bool driveDistanceChecked;
    static bool driveSteerChecked;
    static bool driveFire;
    static Vector2 driveLook;

    /// <summary>
    /// Put a driver in every hull and point it down the map. These pawns are flagged human purely
    /// so the harness input stub is consulted for them — bots are never asked for vehicle input.
    /// </summary>
    static void SetUpDriveProbes()
    {
        var m = current!;
        driveProbes.Clear();
        drivePhase = 0f;
        driveDistanceChecked = false;
        driveSteerChecked = false;
        driveFire = false;
        driveLook = Vector2.Zero;
        driveStick = new Vector2(0f, -1f);          // full forward, no steer

        m.InputSource = _ => new PawnInput { RawMove = driveStick, RawLook = driveLook, Fire = driveFire };

        for (int i = 0; i < 3 && i < m.VehicleList.Count && i < m.Pawns.Count; i++)
        {
            var hull = m.VehicleList[i];
            hull.Board(m.Pawns[i]);

            // Aim at the middle of the map, so a short run has open floor ahead of it rather than
            // the perimeter wall two metres away.
            hull.Facing = MathF.Atan2(-hull.GlobalPosition.Z, -hull.GlobalPosition.X);
            driveProbes.Add((hull, hull.GlobalPosition, hull.Facing));
        }

        Check(driveProbes.Count == 3, "drive test: boarded one of every vehicle");
    }

    /// <summary>
    /// Put every probe hull back on its spawn, whole and crewed.
    ///
    /// Each phase starts from this rather than from wherever the last one left off. The outer
    /// districts have real trenches in them now, and a hull driven blind for eight seconds finds
    /// one — which is correct behaviour and made the later phases measure a wreck at the bottom of
    /// a pit instead of the thing they were supposed to be testing.
    /// </summary>
    static void ResetDriveProbes()
    {
        var m = current!;

        for (int i = 0; i < driveProbes.Count; i++)
        {
            var (hull, start, facing) = driveProbes[i];

            hull.Restore(start, facing);
            if (hull.Driver == null && i < m.Pawns.Count) hull.Board(m.Pawns[i]);

            // Board resets the heading to the driver's, so it is set back afterwards.
            hull.Facing = facing;
            hull.TurretYaw = facing;
        }
    }

    /// <summary>Drives forward, then turns. Returns true once both phases are judged.</summary>
    static bool StepDriveProbes(float dt)
    {
        drivePhase += dt;

        if (!driveDistanceChecked && drivePhase >= DriveForwardTime)
        {
            driveDistanceChecked = true;

            foreach (var (hull, start, _) in driveProbes)
            {
                float travelled = hull.GlobalPosition.DistanceTo(start);
                TestLog.Line($"    {hull.Def.Name}: {travelled:0.0}m under full throttle");

                // A deliberately loose bar. The point is to separate "moves" from "does not move"
                // without becoming brittle to whatever scenery happens to be downrange.
                Check(travelled > 8f, $"{hull.Def.Name} drives when the stick is held forward");
            }

            ResetDriveProbes();
            driveStick = new Vector2(1f, -1f);      // forward and hard over
            return false;
        }

        if (!driveSteerChecked && drivePhase >= DriveForwardTime + DriveSteerTime)
        {
            driveSteerChecked = true;
            CheckSteering();

            ResetDriveProbes();
            driveStick = Vector2.Zero;

            // Traverse and elevate while firing, so the turret is proven to move off the hull axis
            // rather than merely existing.
            driveLook = new Vector2(0.5f, -0.4f);
            driveFire = true;
            return false;
        }

        // The push-wall probe runs after every other driving check, in its own tail window.
        if (pushSubject != null)
            return drivePhase >= DriveForwardTime + DriveSteerTime + DriveFireTime + DrivePushTime
                   && FinishPushWallProbe();

        if (drivePhase < DriveForwardTime + DriveSteerTime + DriveFireTime) return false;

        foreach (var (hull, _, _) in driveProbes)
        {
            if (hull.Def.Gun is not { } gun)
            {
                Check(hull.ShotsFired == 0, $"{hull.Def.Name} has no gun and fires nothing");
                continue;
            }

            // A mounted gun that never goes off is the whole point of this phase. It is checked
            // against the gun's own fire interval rather than a flat number, so a 2.1s cannon and
            // a 0.07s wing gun are both held to what they claim.
            TestLog.Line($"    {hull.Def.Name}: {hull.Health:0}/{hull.Def.Health:0} hull at "
                     + $"{hull.GlobalPosition}, driver={hull.Driver != null}");

            int expected = (int)(DriveFireTime / gun.FireInterval);
            TestLog.Line($"    {hull.Def.Name}: {hull.ShotsFired} rounds in {DriveFireTime}s "
                     + $"(expected about {expected})");

            Check(hull.ShotsFired > 0, $"{hull.Def.Name} fires its {gun.Name} when attack is held");

            // Allowing for physics-tick quantisation: a 0.07s interval cannot be met exactly at
            // 60Hz, so the wing guns land on 0.083s and come up about 15 percent short of nominal.
            Check(hull.ShotsFired >= expected * 0.7f, $"{hull.Def.Name} fires at close to its stated rate");

            if (hull.HasTurret)
            {
                // The gun has to lay independently of the hull and has to elevate. Firing flat down
                // the hull heading is technically shooting and hits nothing on a map with a second
                // storey — which is exactly how the tank felt to play.
                float offAxis = MathF.Abs(MathU.AngleDiff(hull.TurretYaw, hull.Facing));
                TestLog.Line($"    {hull.Def.Name}: turret {offAxis:0.00} rad off the hull, "
                         + $"elevation {hull.TurretPitch:0.00} rad");

                Check(offAxis > 0.2f, $"{hull.Def.Name} turret traverses independently of the hull");
                Check(hull.TurretPitch > 0.1f, $"{hull.Def.Name} gun elevates");
                Check(hull.AimDir.Y > 0.05f, $"{hull.Def.Name} can put a round above the horizontal");
            }
        }

        CheckVehicleFallsAreFatal();
        ResetDriveProbes();
        CheckVehiclesAreDestructible();

        // Not done yet: the push-wall probe it just armed needs its own window to run in.
        return false;
    }

    /// <summary>
    /// Driving into a trench destroys the vehicle and settles the wreck.
    ///
    /// Written after a probe hull was found six hundred metres below the map: vehicles had no kill
    /// plane at all, and once they got one the wreck still fell forever, exactly as corpses used
    /// to. Both are now checked rather than assumed.
    /// </summary>
    static void CheckVehicleFallsAreFatal()
    {
        var m = current!;
        if (driveProbes.Count == 0) return;

        var hull = driveProbes[0].Hull;

        hull.Restore(hull.HomePosition + Vector3.Up * 0.5f, 0f);
        hull.GlobalPosition = new Vector3(0f, Arena.KillPlaneY - 2f, 0f);

        Check(hull.Alive, "a hull below the kill plane has not been judged yet");

        // One tick of the vehicle step is what applies it.
        m.KillPlaneSweepForTest();

        Check(!hull.Alive, "a vehicle driven below the kill plane is destroyed");
        Check(hull.GlobalPosition.Y >= Arena.KillPlaneY - 0.01f,
              "and the wreck settles there rather than falling out of the world");
    }

    /// <summary>
    /// Shoot every hull to pieces and check it dies, then check it comes back.
    ///
    /// Vehicles were invisible to the projectile hit test entirely: a round that struck a hull
    /// simply stopped, and <c>Vehicle.TakeDamage</c> was dead code. Every vehicle in the game was
    /// indestructible, and nothing in the suite noticed because nothing ever shot one.
    /// </summary>
    static void CheckVehiclesAreDestructible()
    {
        var m = current!;

        foreach (var (hull, _, _) in driveProbes)
        {
            float full = hull.Health;
            Check(full > 0f, $"{hull.Def.Name} starts intact");

            // A fraction first, to prove damage accumulates rather than only the killing blow
            // registering.
            hull.TakeDamage(full * 0.25f);
            Check(hull.Health < full && hull.Alive,
                  $"{hull.Def.Name} takes damage without dying");

            bool wrecked = hull.TakeDamage(full);
            Check(wrecked && !hull.Alive, $"{hull.Def.Name} can be destroyed");
            Check(hull.Driver == null, $"{hull.Def.Name} throws its driver clear when wrecked");

            hull.Restore(hull.HomePosition + Vector3.Up * 0.5f, 0f);
            Check(hull.Alive && hull.Health == hull.Def.Health,
                  $"{hull.Def.Name} comes back whole");
        }

        // A shell has to be worth its reload. Splash is the larger half of the cannon on purpose:
        // it is aimed at the ground under someone rather than threaded at them.
        var cannon = Vehicles.Tank.Gun!;
        Check(cannon.Explodes, "the tank cannon fires explosive shells");
        Check(cannon.BlastDamage > cannon.Damage, "the cannon's splash outweighs its direct hit");
        Check(cannon.BlastRadius > 5f, "the cannon's blast has real reach");
        Check(Vehicles.Plane.Gun!.Explodes == false, "the plane's wing guns are not explosive");

        Check(Match.VehicleRespawnTime > 5f && Match.VehicleRespawnTime < 90f,
              "a wreck comes back on a sane timer");

        Once(CheckWalkwaysCanBeBroken);
        Once(CheckBotsDrive);
        Once(CheckLeavingTheMapIsFatal);
        Once(CheckTwoWeaponSlots);
        Once(CheckFactionSpecials);
        Once(CheckBlownUpDriverGetsOut);
        Once(CheckMelee);
        Once(CheckDashFollowsTheCrosshair);
        Once(CheckScopedRiflesCanBePickedUp);
        Once(CheckClassAbilities);
        Once(CheckBotsSeekGunsNotMedKits);
        Once(CheckCaptureTheFlag);
        Once(CheckJuggernaut);
        Once(CheckDominion);
        Once(CheckPortalMode);
        Once(CheckBattlePoints);
        Once(CheckGrenadeLauncher);
        Once(CheckFlamethrower);
        Once(CheckPortalKills);
        Once(CheckGrappleHauls);
        Once(CheckPushWallShoves);
        Once(CheckTankShellFollowsTheCrosshair);
        Once(CheckBotLethality);
        Once(CheckReinforcementAbilities);
        Once(CheckRocketLauncher);
        Once(CheckPortalGun);
        Once(CheckGrapple);
        Once(CheckJetpack);
        Once(CheckLeavingAVehicleUnderWay);
        Once(CheckButterTrail);
        Once(CheckSeeker);
        BeginPushWallProbe();
    }

    /// <summary>
    /// Run one of the one-off checks, turning a throw into a failed check rather than a wrecked run.
    ///
    /// These all live inside <see cref="StepDriveProbes"/>, which arms the push-wall probe on its
    /// way out — and that probe is what stops the whole block re-running next frame. An exception
    /// anywhere in the list therefore skipped the arming, so the block ran again, and again, with
    /// vehicles already spent and drivers already ejected: one null reference in a new check
    /// produced eleven unrelated failures across the vehicles, the bots and the eject placement,
    /// none of which were real. Diagnosing that cost more than writing this.
    /// </summary>

    /// <summary>
    /// The butter trail: crossing one takes your feet out from under you for a second.
    ///
    /// Driven through the harness hook rather than by actually driving a car, because what is
    /// under test is the slip and not the driving. A car doing more than
    /// <see cref="Match.ButterMinSpeed"/> in a headless world is a second thing to get working
    /// before this one can be checked at all, and if it broke, this test would fail for a reason
    /// that has nothing to do with butter.
    /// </summary>
    static void CheckButterTrail()
    {
        var m = current!;
        var p = m.Pawns[0];

        if (p.InVehicle) m.ToggleVehicle(p);
        p.Respawn(m.Arena.SpawnPoints[0]);
        p.ClearSpawnProtectionForTest();

        var still = new PawnInput { Aim = MathU.FromAngle(p.Facing) };

        Check(!p.Slipping, "a pawn on clean ground is on its feet");

        int before = m.ButterCount;
        m.DropButterForTest(p.GlobalPosition);
        Check(m.ButterCount == before + 1, "a patch of butter goes down where it is laid");

        m.StepButterForTest(1f / 60f);
        Check(p.Slipping, "and standing in one takes your feet out from under you");

        // Held down for about the second it claims. Ticked without stepping the trail again, so
        // this measures the slip's own timer rather than the patch re-slipping them underneath it.
        int ticks = 0;
        while (p.Slipping && ticks < 240)
        {
            p.Tick(1f / 60f, still, m);
            ticks++;
        }

        float held = ticks / 60f;
        TestLog.Line($"    a slip holds you down for {held:0.00}s");

        Check(!p.Slipping, "and you get back up again");
        Check(MathF.Abs(held - Pawn.SlipDuration) < 0.2f,
              $"about a second later ({held:0.00}s against {Pawn.SlipDuration:0.00}s)");

        // The trail is terrain, not an attack: it must not be lethal on its own, or driving in
        // circles round a spawn would be a way of killing people who never saw a weapon.
        Check(p.Alive, "and slipping over does not kill you");

        // Nothing left greased behind the test. A patch surviving into the scenarios below would
        // have bots falling over for reasons those tests know nothing about.
        //
        // Checked by standing in it again rather than by counting patches. The count is shared
        // with every car on the map, and one being driven by another part of the suite would drop
        // a fresh patch during the very step that ages this one out — a count of zero is not
        // something this test can honestly demand. Whether the spot is still slippery is.
        m.StepButterForTest(Match.ButterLife + 1f);

        p.Respawn(m.Arena.SpawnPoints[0]);
        p.ClearSpawnProtectionForTest();
        m.StepButterForTest(1f / 60f);

        Check(!p.Slipping, "and the trail wears off");
    }



    /// <summary>
    /// The seeker: a slow rocket that turns toward what it is fired at.
    ///
    /// Two halves, checked separately. The table says what the weapon is and can be read without a
    /// world; the steering only means anything through the real projectile loop, because turning
    /// is something that happens per tick against live targets.
    /// </summary>
    static void CheckSeeker()
    {
        var gun = Weapons.Seeker;
        var rocket = Weapons.RocketLauncher;

        Check(gun.Seeks, "the seeker seeks");
        Check(gun.TriggerRadius > 0f, "and goes off when something touches it");

        // Everything it gives up for the steering. A seeker that were also the best rocket would
        // simply retire the rocket launcher.
        Check(gun.ProjectileSpeed < rocket.ProjectileSpeed * 0.75f,
              $"it is markedly slower than a rocket ({gun.ProjectileSpeed:0} against {rocket.ProjectileSpeed:0})");
        Check(gun.BlastDamage < rocket.BlastDamage, "and its blast is smaller");
        Check(gun.FireInterval > rocket.FireInterval, "and it reloads more slowly");
        Check(gun.Ammo < rocket.Ammo, "and it carries fewer");

        // Turn rate against speed is the whole balance: it has to lose to distance, or breaking
        // line of sight would not be the answer to it and nothing would be.
        Check(gun.SeekTurnRate > 0.5f && gun.SeekTurnRate < 3f,
              $"it turns hard enough to matter and not hard enough to be unavoidable ({gun.SeekTurnRate:0.0} rad/s)");
        Check(gun.SeekConeDeg < 90f,
              $"and it still has to be pointed at something ({gun.SeekConeDeg:0} degrees)");

        // ---- it actually turns ----
        var m = current!;
        var a = m.Pawns[0];
        var b = m.Pawns[1];

        if (a.InVehicle) m.ToggleVehicle(a);
        if (b.InVehicle) m.ToggleVehicle(b);

        var lane = ClearLane(m.Arena, 30f);
        Check(lane.HasValue, "the harness has somewhere to fly one");
        if (lane is not { } from) return;

        m.ClearShotsForTest();

        // The target sits down the lane; the round is launched along it but aimed off to one side.
        // Fired straight at them there would be nothing to prove — a round that never turns would
        // pass the same check.
        var target = from + new Vector3(25f, 0f, 0f);

        a.Respawn(from);
        b.Respawn(target);
        a.ClearSpawnProtectionForTest();
        b.ClearSpawnProtectionForTest();

        float off = Mathf.DegToRad(15f);
        var aim = new Vector3(MathF.Cos(off), 0f, MathF.Sin(off));

        m.InjectSeekerForTest(a, from + Vector3.Up * 1.2f, aim * gun.ProjectileSpeed, gun);
        Check(m.ShotsInFlight == 1, "a seeker is in the air");
        if (m.ShotsInFlight != 1) return;

        float Astray()
        {
            var to = (b.GlobalPosition + Vector3.Up * 1.2f) - m.ShotPositionForTest(0);
            return m.ShotVelocityForTest(0).Normalized().AngleTo(to.Normalized());
        }

        float before = Astray();

        for (int i = 0; i < 18 && m.ShotsInFlight == 1; i++) m.StepShotsForTest(1f / 60f);

        Check(m.ShotsInFlight == 1, "and it is still in the air three tenths of a second later");
        if (m.ShotsInFlight != 1) return;

        float after = Astray();

        TestLog.Line($"    seeker: {Mathf.RadToDeg(before):0.0} degrees off target, "
                 + $"{Mathf.RadToDeg(after):0.0} three tenths of a second later");

        Check(after < before, "and it has turned toward its target rather than flying straight");

        m.ClearShotsForTest();
    }


    static void Once(Action check)
    {
        try
        {
            check();
        }
        catch (Exception e)
        {
            Check(false, $"{check.Method.Name} threw: {e.GetType().Name} — {e.Message}");
        }
    }

    /// <summary>
    /// Being outside the map kills you, with no exceptions.
    ///
    /// A player reported being fired out of the arena and left alive out there. Two things were
    /// wrong: the shove that threw them, and the fact that nothing objected once they had gone.
    /// This covers the second — the first is fixed by moving pawns instead of teleporting them,
    /// which cannot be asserted here because it is the absence of an event.
    /// </summary>
    static void CheckLeavingTheMapIsFatal()
    {
        var m = current!;
        var p = m.Pawns[0];

        if (p.InVehicle) m.ToggleVehicle(p);
        if (!p.Alive) p.Respawn(m.Arena.SpawnPoints[0]);

        foreach (var outside in new[]
        {
            new Vector3(Arena.HalfWidth + 12f, 2f, 0f),
            new Vector3(0f, 2f, -Arena.HalfDepth - 12f),
            new Vector3(0f, 260f, 0f),
        })
        {
            p.Respawn(m.Arena.SpawnPoints[0]);
            Check(p.Alive, "the probe starts alive and in play");

            p.GlobalPosition = outside;
            Check(!m.Arena.InPlay(outside), $"{outside} counts as outside the map");

            m.BoundsSweepForTest();
            Check(!p.Alive, $"a pawn at {outside} is killed for leaving the map");
        }

        p.Respawn(m.Arena.SpawnPoints[0]);
    }

    /// <summary>
    /// Two slots, a swap, and a third pickup replacing what is in your hands.
    ///
    /// Asked of a live pawn rather than of a freshly constructed one, because a pawn is a physics
    /// body and its loadout is set up as part of entering the scene.
    /// </summary>
    static void CheckTwoWeaponSlots()
    {
        var m = current!;
        var p = m.Pawns[0];

        if (p.InVehicle) m.ToggleVehicle(p);
        p.Respawn(m.Arena.SpawnPoints[0]);

        Check(ReferenceEquals(p.Weapon, p.Class.Weapon), "you start holding your class weapon");
        Check(p.SlotWeapon(1) == null, "the second slot starts empty");
        Check(!p.CanSwapWeapon, "with one weapon there is nothing to swap to");
        Check(!p.SlotsFull, "one weapon is not two");

        // First pickup fills the empty slot and is drawn.
        p.TakeWeapon(Weapons.Railgun);
        Check(ReferenceEquals(p.Weapon, Weapons.Railgun), "a first pickup goes into your hands");
        Check(ReferenceEquals(p.SlotWeapon(0), p.Class.Weapon), "and leaves the class weapon behind");
        Check(p.SlotsFull && p.CanSwapWeapon, "now you are carrying two");

        // Swapping goes back and forth without losing either.
        p.SwapWeapon();
        Check(ReferenceEquals(p.Weapon, p.Class.Weapon), "swap returns to the class weapon");
        p.SwapWeapon();
        Check(ReferenceEquals(p.Weapon, Weapons.Railgun), "and back again");

        Check(!p.WouldTake(Weapons.Railgun), "a gun you are already carrying is left standing");
        Check(p.WouldTake(Weapons.Minigun), "a gun you are not carrying is worth taking");

        // Third pickup with both slots full replaces the active one, which is the Halo rule: you
        // drop what you are holding, not some other slot chosen for you.
        p.TakeWeapon(Weapons.Minigun);
        Check(ReferenceEquals(p.Weapon, Weapons.Minigun), "a third pickup goes into your hands");
        Check(ReferenceEquals(p.SlotWeapon(0), p.Class.Weapon), "the other slot is untouched");
        Check(!p.WouldTake(Weapons.Minigun), "and it is now the one you are carrying");

        // Dying gives you your class weapon back and nothing else.
        p.Respawn(m.Arena.SpawnPoints[0]);
        Check(ReferenceEquals(p.Weapon, p.Class.Weapon), "respawning resets the loadout");
        Check(p.SlotWeapon(1) == null, "and empties the second slot");

        // Weapons need a deliberate hold; the timer only accumulates while the button is down.
        p.TrackPickupHold(0.2f, true);
        p.TrackPickupHold(0.2f, true);
        Check(p.PickupHold >= Match.PickupHoldTime, "holding interact reaches the pickup threshold");

        p.TrackPickupHold(0.2f, false);
        Check(p.PickupHold == 0f, "letting go resets it");

        Check(Match.PickupHoldTime is > 0.15f and < 1.2f,
              "the hold is long enough to be deliberate and short enough not to be a chore");
    }

    /// <summary>
    /// Each faction's special does the thing its philosophy claims.
    ///
    /// Fired directly at a posed situation rather than waited for. A bot happening to spend the
    /// right ability at the right moment inside a test window is a coin flip, and the harness has
    /// been bitten by that shape of test twice already.
    /// </summary>
    static void CheckFactionSpecials()
    {
        var m = current!;

        // Four factions, four different abilities. A shared one would be a faction that is a skin.
        var kinds = new HashSet<SpecialKind>();

        foreach (var f in Factions.All)
        {
            TestLog.Line($"    {f.Name}: {f.SpecialName} ({f.Special}), {f.SpecialCooldown:0}s cooldown");

            Check(kinds.Add(f.Special), $"{f.Name} has a special nobody else has");
            Check(f.SpecialName.Length > 0, $"{f.Name} names its special");
            Check(f.SpecialCooldown > 3f, $"{f.Name}'s special has a real cooldown");
        }

        var a = m.Pawns[0];
        var b = m.Pawns[1];

        if (a.InVehicle) m.ToggleVehicle(a);
        if (b.InVehicle) m.ToggleVehicle(b);

        // ---- Second Wind: ten seconds of being much harder to keep up with ----
        //
        // This used to pay banked damage back as healing, and it was scrapped on the report that
        // nobody could tell what it did — which was fair. At full health it was worth exactly
        // nothing, and at low health it was worth a bar you could not watch move. The replacement
        // is about the body doing something, which is at least on-message for the faction that
        // believes humanity *was* the body.
        a.Faction = Factions.Vessels;
        a.Respawn(m.Arena.SpawnPoints[0]);
        a.ClearSpawnProtectionForTest();

        Check(!a.Surging, "a Vessel is ordinary to begin with");

        float baseSpeed = a.EffectiveSpeed;

        // Posed rather than fired through the input path: firing for real spends the special's
        // sixteen-second cooldown, and the checks further down this same method assert that the
        // faction special is ready. A test should not leave the pawn in a state the next test
        // disagrees with.
        a.StartSpecialForTest();
        a.Tick(1f / 60f, new PawnInput { Aim = MathU.FromAngle(0f) }, m);

        Check(a.Surging, "the special makes them surge");
        Check(a.BuffTime > 5f, $"for a long time ({a.BuffTime:0.0}s)");

        Check(a.EffectiveSpeed > baseSpeed * 1.8f,
              $"and they move far faster ({a.EffectiveSpeed:0.0} against {baseSpeed:0.0} m/s)");

        Check(Pawn.SurgeJump > 1.3f, "and jump much higher");

        // It has to end. A permanent double-speed fighter is a different game.
        var idle = new PawnInput { Aim = MathU.FromAngle(0f) };
        for (int i = 0; i < (int)((Factions.Vessels.SpecialDuration + 1f) * 60f); i++)
            a.Tick(1f / 60f, idle, m);

        Check(!a.Surging, "and it runs out");
        Check(MathF.Abs(a.EffectiveSpeed - baseSpeed) < 0.01f, "leaving them ordinary again");

        a.Respawn(m.Arena.SpawnPoints[0]);
        a.ClearSpawnProtectionForTest();

        // ---- Bloom: heals your side, slows the other ----
        b.Faction = Factions.Garden;
        b.Respawn(m.Arena.SpawnPoints[1]);
        b.ClearSpawnProtectionForTest();
        b.TakeDamage(50f);

        float before = b.Health;
        m.UseSpecial(b);

        // Stood in its own patch: the throw lands ahead along the aim, so the planter is walked in.
        b.GlobalPosition += new Vector3(b.AimDir.X, 0f, b.AimDir.Z).Normalized() * 4f;

        m.StepBloomsForTest(0.5f);
        Check(b.Health > before, "a Bloom heals whoever planted it");

        a.GlobalPosition = b.GlobalPosition;
        m.StepBloomsForTest(0f);
        Check(a.SlowFactor < 1f, "and slows anyone who is not on that side");

        b.GlobalPosition = m.Arena.SpawnPoints[1];
        m.StepBloomsForTest(0f);

        // ---- Understudy: leaves something behind and takes you off the board ----
        a.Faction = Factions.Ingenuity;
        a.Respawn(m.Arena.SpawnPoints[0]);

        int decoysBefore = m.DecoyCount;
        m.UseSpecial(a);

        Check(m.DecoyCount > decoysBefore || !m.Visuals,
              "Understudy leaves a decoy behind");

        // ---- Revelation: marks the other side, and only the other side ----
        b.Faction = Factions.Custodians;
        b.Respawn(m.Arena.SpawnPoints[1]);
        a.Respawn(m.Arena.SpawnPoints[0]);

        m.UseSpecial(b);

        Check(a.RevealedFor > 0f, "Revelation lights up an enemy");
        Check(b.RevealedFor <= 0f, "and never the caster");
    }

    /// <summary>
    /// Being in a tank when it dies puts you on your feet beside it, not inside it.
    ///
    /// Reported from play: "I get stuck under the tank if the tank blows up while I'm driving it."
    /// The eject spot was a fixed offset measured along the wrong axis — <c>HalfExtents.X</c> is
    /// the hull's length, and it was being used as the sideways clearance — so the driver of a
    /// tank was placed 5.6m out to the side of a hull 2.4m wide. On a map this dense that lands
    /// inside geometry, and re-enabling collision on a pawn standing in a wall lets the solver
    /// squirt them anywhere, frequently under the hull they had just been driving.
    ///
    /// Every hull on every layout gets checked, from a few different headings, because the failure
    /// depended entirely on what happened to be beside the vehicle.
    /// </summary>
    static void CheckBlownUpDriverGetsOut()
    {
        var m = current!;
        var p = m.Pawns[0];

        // Every hull on its own spawn, from four headings — and then the case that actually
        // matters. A vehicle spawn is open drivable ground by construction, so a driver ejected
        // there lands in the clear no matter how badly the placement is calculated: the first
        // version of this test did only that, and the *old* broken code passed it. Nobody dies in
        // a tank parked on its spawn. They die jammed against something.
        var sites = new List<(Vector3 At, float Facing, string Where)>();

        foreach (var rig0 in m.VehicleList)
            foreach (float facing in new[] { 0f, 1.6f, 3.1f, 4.7f })
                sites.Add((rig0.HomePosition + Vector3.Up * 0.5f, facing, "on its spawn"));

        // Before trusting any of it: prove the overlap probe can see a wall at all.
        //
        // A test whose central question is "is the pawn inside something?" is worth nothing if the
        // answer is always no, and there are several ways for it to be always no — collision not
        // built in a headless world, the query running outside the physics step, the probe capsule
        // sized wrong. This is the control.
        Block? solid = null;
        foreach (var b in m.Arena.Blocks)
        {
            if (b.Centre.Y - b.HalfExtents.Y > 0.3f) continue;
            if (b.Centre.Y + b.HalfExtents.Y < 2.2f) continue;
            if (b.HalfExtents.X < 3.5f || b.HalfExtents.Z < 3.5f) continue;
            solid = b;
            break;
        }

        Check(solid != null, "the arena has a wall thick enough to bury a pawn in");

        if (solid is { } wall)
        {
            if (p.InVehicle) m.ToggleVehicle(p);
            p.Respawn(m.Arena.SpawnPoints[0]);
            p.GlobalPosition = wall.Centre with { Y = wall.Centre.Y - wall.HalfExtents.Y + 0.2f };

            Check(m.OverlapsSolid(p), "the overlap probe detects a pawn standing inside a wall");

            p.Respawn(m.Arena.SpawnPoints[0]);
            Check(!m.OverlapsSolid(p), "and does not fire on a pawn standing on its spawn");
        }

        int flush = 0;
        var subject = m.VehicleList.Count > 0 ? m.VehicleList[0] : null;

        if (subject != null)
        {
            foreach (var b in m.Arena.Blocks)
            {
                if (flush >= 6) break;

                // A real ground-level wall, deep enough that a sideways eject measured against the
                // hull's *length* rather than its width lands inside it, and tall enough that a
                // pawn placed there is genuinely buried.
                if (b.Centre.Y - b.HalfExtents.Y > 0.3f) continue;
                if (b.Centre.Y + b.HalfExtents.Y < 2.2f) continue;
                if (b.HalfExtents.Z < 3.5f || b.HalfExtents.X < 3.5f) continue;

                // Parked flush against the block's +Z face, turned so the hull's sideways axis
                // points straight into it.
                var at = new Vector3(b.Centre.X,
                                     0.5f,
                                     b.Centre.Z + b.HalfExtents.Z + subject.Def.HalfExtents.Z + 0.1f);

                // The hull itself has to be standing somewhere legitimate, or the setup is not a
                // situation the game can produce.
                if (!m.Arena.HullFits(at, 0.5f)) continue;
                if (!m.Arena.InPlay(at)) continue;

                sites.Add((at, MathF.PI, $"flush against a wall at ({at.X:0},{at.Z:0})"));
                flush++;
            }
        }

        TestLog.Line($"    eject: {sites.Count} sites, {flush} of them jammed against a wall");
        Check(flush > 0, "the eject test found somewhere tight to blow a tank up in");

        foreach (var (at, facing, where) in sites)
        {
            foreach (var rig in m.VehicleList)
            {
                if (p.InVehicle) m.ToggleVehicle(p);
                if (!p.Alive) p.Respawn(m.Arena.SpawnPoints[0]);

                // Every other hull sent home first.
                //
                // The loop moves one vehicle to each site in turn and used to leave it there, so
                // by the second rig two hulls were standing on the same spot — and a driver
                // stepping out of one landed inside the other. That reads as an eject bug and is
                // not one; it only surfaced once the placement search started finding the doors
                // instead of falling through to the roof every single time.
                foreach (var other in m.VehicleList)
                    if (other != rig) other.Restore(other.HomePosition + Vector3.Up * 0.5f, 0f);

                rig.Restore(at, facing);
                rig.Board(p);

                // Board adopts the driver's heading, so the hull has to be pointed again
                // afterwards. Without this the site headings were silently discarded and every
                // "jammed against a wall" case was really a tank facing whatever direction the
                // pawn happened to be looking — which is how the first version of this test
                // managed to pass against the broken code twice.
                rig.Facing = facing;
                rig.TurretYaw = facing;

                rig.TakeDamage(rig.Def.Health * 2f);

                Check(!p.InVehicle, $"a wrecked {rig.Def.Name} does not keep its driver");

                // The one that matters: on their feet somewhere they fit, not welded into the
                // hull or the scenery. The blast may well have killed them, which is fair — being
                // inside a tank when it goes up should hurt. Being stuck is the bug.
                Check(!m.OverlapsSolid(p, rig),
                      $"the driver thrown from a {rig.Def.Name} {where} is not inside anything");

                Check(m.Arena.InPlay(p.GlobalPosition),
                      $"the driver thrown from a {rig.Def.Name} {where} lands inside the map");

                // And clear of the hull specifically, which is the shape the report named.
                //
                // Tested in the hull's own frame rather than against an axis-aligned box or a
                // bounding sphere: the hull is eight metres long and four wide, so a sphere big
                // enough to contain it also contains the perfectly good spot beside it, and an
                // axis-aligned box is simply the wrong shape once the tank is pointing anywhere
                // but down an axis.
                var d = p.GlobalPosition + Vector3.Up * (Pawn.Height * 0.5f)
                        - (rig.GlobalPosition + Vector3.Up * rig.Def.HalfExtents.Y);

                float cf = MathF.Cos(rig.Facing), sf = MathF.Sin(rig.Facing);
                float alongNose = d.X * cf + d.Z * sf;
                float alongSide = -d.X * sf + d.Z * cf;

                bool insideHull = MathF.Abs(alongNose) < rig.Def.HalfExtents.X + Pawn.Radius * 0.5f
                                  && MathF.Abs(d.Y) < rig.Def.HalfExtents.Y + Pawn.Height * 0.4f
                                  && MathF.Abs(alongSide) < rig.Def.HalfExtents.Z + Pawn.Radius * 0.5f;

                Check(!insideHull,
                      $"the driver thrown from a {rig.Def.Name} {where} is clear of the hull");
            }
        }

        CheckWalkingOutOfAVehicle(m, p, sites);

        foreach (var rig in m.VehicleList) rig.Restore(rig.HomePosition + Vector3.Up * 0.5f, 0f);
        if (!p.Alive) p.Respawn(m.Arena.SpawnPoints[0]);
    }

    /// <summary>
    /// Getting out on purpose, which is the case the player actually reported.
    ///
    /// Everything above this tests being *thrown* from a hull that just exploded. Walking out of a
    /// working one goes through the same placement and was never covered, and that is the half
    /// that kept being broken.
    ///
    /// "I get out and I am under the tank", four times, against a suite that was green each time —
    /// because the suite only ever blew the tank up first, and a wreck is not going anywhere.
    ///
    /// The reports outlasted three different placement searches. Each one put the driver on the
    /// ground somewhere the hull was not *at that instant*, and a hull that is being driven is
    /// somewhere else an instant later. The ground beside a vehicle cannot be made safe; the roof
    /// needs no search, because it moves with the thing it is on top of.
    ///
    /// Tested moving as well as stationary, and that is the half that matters: a stationary hull
    /// hides every version of this bug, because nothing can be run over by something parked.
    /// </summary>
    static void CheckWalkingOutOfAVehicle(
        Match m, Pawn p, List<(Vector3 At, float Facing, string Where)> sites)
    {
        foreach (var (at, facing, where) in sites)
        {
            foreach (var rig in m.VehicleList)
            {
                if (p.InVehicle) m.ToggleVehicle(p);
                if (!p.Alive) p.Respawn(m.Arena.SpawnPoints[0]);

                // Every other hull sent home first.
                //
                // The loop moves one vehicle to each site in turn and used to leave it there, so
                // by the second rig two hulls were standing on the same spot — and a driver
                // stepping out of one landed inside the other. That reads as an eject bug and is
                // not one; it only surfaced once the placement search started finding the doors
                // instead of falling through to the roof every single time.
                foreach (var other in m.VehicleList)
                    if (other != rig) other.Restore(other.HomePosition + Vector3.Up * 0.5f, 0f);

                rig.Restore(at, facing);
                rig.Board(p);

                // Board adopts the driver's heading, so the hull has to be pointed again after.
                rig.Facing = facing;
                rig.TurretYaw = facing;

                // Under way, which is the state the complaint was made in. A hull that is not
                // moving cannot run over the person who just stepped out of it.
                rig.Velocity = new Vector3(MathF.Cos(facing), 0f, MathF.Sin(facing)) * 9f;

                m.ToggleVehicle(p);

                Check(!p.InVehicle, $"a driver can walk out of a moving {rig.Def.Name} {where}");
                if (p.InVehicle) continue;

                Check(!m.OverlapsSolid(p, rig),
                      $"walking out of a {rig.Def.Name} {where} does not leave you inside anything");

                Check(m.Arena.InPlay(p.GlobalPosition),
                      $"walking out of a {rig.Def.Name} {where} leaves you inside the map");

                // Clear of the hull, in the hull's own frame — the same test the wreck case uses,
                // because it is the same failure and it is the one the player described.
                var d = p.GlobalPosition + Vector3.Up * (Pawn.Height * 0.5f)
                        - (rig.GlobalPosition + Vector3.Up * rig.Def.HalfExtents.Y);

                float cf = MathF.Cos(rig.Facing), sf = MathF.Sin(rig.Facing);
                float alongNose = d.X * cf + d.Z * sf;
                float alongSide = -d.X * sf + d.Z * cf;

                bool insideHull = MathF.Abs(alongNose) < rig.Def.HalfExtents.X + Pawn.Radius * 0.5f
                                  && MathF.Abs(d.Y) < rig.Def.HalfExtents.Y + Pawn.Height * 0.4f
                                  && MathF.Abs(alongSide) < rig.Def.HalfExtents.Z + Pawn.Radius * 0.5f;

                Check(!insideHull,
                      $"walking out of a {rig.Def.Name} {where} leaves you clear of the hull");

                // And on top of it, which is the rule now and used to be the opposite one.
                //
                // This check read `d.Y < HalfExtents.Y + Pawn.Height` and said "beside it rather
                // than on its roof", because at the time the roof was the fallback and the
                // fallback being taken routinely was the bug. Three more reports later the ground
                // beside a hull turned out to be the thing that could not be made safe — it is
                // only clear until the hull moves, and the hull is usually moving — so the roof
                // stopped being the fallback and became the answer. It is the one place the
                // vehicle cannot drive over you, because it travels with you.
                //
                // Both the lifted spot and the flush one clear this: the lift only adds to it.
                Check(d.Y > rig.Def.HalfExtents.Y,
                      $"and on top of a {rig.Def.Name} {where}, not beside it");
            }
        }
    }

    /// <summary>
    /// Melee lands in front of you, and only in front of you.
    /// </summary>
    static void CheckMelee()
    {
        var m = current!;
        var a = m.Pawns[0];
        var b = m.Pawns[1];

        if (a.InVehicle) m.ToggleVehicle(a);
        if (b.InVehicle) m.ToggleVehicle(b);

        Check(Match.MeleeRange > 1.5f && Match.MeleeRange < 5f, "melee reaches about an arm's length");
        Check(Match.MeleeDamage > 0f && Match.MeleeDamage < 100f,
              "a bare swing hurts without being a one-hit kill");

        a.Respawn(m.Arena.SpawnPoints[0]);
        b.Respawn(m.Arena.SpawnPoints[0]);
        a.ClearSpawnProtectionForTest();
        b.ClearSpawnProtectionForTest();

        a.Facing = 0f;
        a.Pitch = 0f;

        // Straight in front, well inside reach.
        b.GlobalPosition = a.GlobalPosition + new Vector3(Match.MeleeRange * 0.5f, 0f, 0f);

        float before = b.Health;
        m.MeleeStrike(a);
        Check(b.Health < before, "a swing hits someone standing in front of you");

        // Behind, at the same distance.
        b.Health = before;
        b.GlobalPosition = a.GlobalPosition + new Vector3(-Match.MeleeRange * 0.5f, 0f, 0f);
        m.MeleeStrike(a);
        Check(b.Health == before, "and misses someone standing behind you");

        // In front but far out of reach.
        b.GlobalPosition = a.GlobalPosition + new Vector3(Match.MeleeRange * 3f, 0f, 0f);
        m.MeleeStrike(a);
        Check(b.Health == before, "and misses someone out of reach");

        // A driver is inside a hull; a fist should not reach through it.
        b.GlobalPosition = a.GlobalPosition + new Vector3(Match.MeleeRange * 0.5f, 0f, 0f);
        var rig = m.VehicleList.Count > 0 ? m.VehicleList[0] : null;
        if (rig != null)
        {
            rig.Restore(b.GlobalPosition, 0f);
            rig.Board(b);
            m.MeleeStrike(a);
            Check(b.Health == before, "and cannot reach a driver through their hull");
            rig.Eject();
            rig.Restore(rig.HomePosition + Vector3.Up * 0.5f, 0f);
        }

        // The cooldown is what stops melee being a second trigger.
        Check(Pawn.MeleeCooldown > 0.3f, "melee has a real cooldown");
        Check(Match.MeleeDamage / Pawn.MeleeCooldown < Classes.Trooper.Dps,
              "and swinging is worse than shooting, so melee never replaces your gun");
    }

    /// <summary>
    /// A dash is a flat sidestep on the ground and a steer in the air.
    ///
    /// Three versions of this have now shipped, and the first two each picked one rule and applied
    /// it everywhere. Flat-always could not leave the ground. Aim-directed-always turned a glance
    /// above the horizon into a launch — "I can literally fly out of the arena if I point it up".
    /// The pitch-threshold compromise that followed was still one rule: it just moved the angle at
    /// which a sidestep silently became a launch, which is the worst of both, because now the rule
    /// changes underneath you mid-look.
    ///
    /// The split is by state, not by angle, because they are two different verbs. On the ground you
    /// are dodging a shot and you want it to go where you are moving, every time, regardless of
    /// where you happen to be looking. In the air you are steering, and the crosshair is the only
    /// control you have.
    ///
    /// So both halves are asserted, and the ground half is asserted at pitches that would send an
    /// airborne dash a long way vertically — that is the whole point of it.
    /// </summary>
    static void CheckDashFollowsTheCrosshair()
    {
        var m = current!;
        var p = m.Pawns[0];

        if (p.InVehicle) m.ToggleVehicle(p);

        // ---- on the ground, pitch does nothing ----
        foreach (float pitch in new[] { 0.0f, 0.45f, -0.45f, 0.8f, Pawn.MaxPitch })
        {
            var level = new PawnInput { Aim = MathU.FromAngle(0f), Pitch = pitch };

            if (!Land(p, m, level)) { Check(false, $"the harness can put a pawn on the floor at {pitch:0.00} rad"); continue; }

            float startY = p.GlobalPosition.Y;
            level.Dash = true;

            p.Tick(1f / 60f, level, m);
            Check(p.Dashing, $"the grounded dash fires at {pitch:0.00} rad");

            level.Dash = false;
            for (int i = 0; i < 8; i++) p.Tick(1f / 60f, level, m);

            float drift = p.GlobalPosition.Y - startY;

            // Standing on a floor, so there is nowhere to fall to either: a grounded dash should
            // finish at very nearly the height it started at, whatever the crosshair was doing.
            Check(MathF.Abs(drift) < 0.6f,
                  $"a grounded dash at {pitch:0.00} rad stays on the floor (moved {drift:0.00}m)");
        }

        // ---- in the air, the crosshair is the steering ----
        foreach (float pitch in new[] { 1.2f, -1.2f, 0.45f, -0.45f })
        {
            var input = new PawnInput { Aim = MathU.FromAngle(0f), Pitch = pitch };

            // Middle of the arena with room in every direction.
            if (!Fall(p, m, input)) { Check(false, $"the harness can put a pawn in the air at {pitch:0.0} rad"); continue; }

            input.Dash = true;
            float startY = p.GlobalPosition.Y;

            p.Tick(1f / 60f, input, m);

            // The dash has to have actually fired. Without this the check below cannot tell a dash
            // that went nowhere from a dash that never happened — which is exactly what an earlier
            // pass hit while the dash cooldown still survived a respawn.
            Check(p.Dashing, $"the airborne dash fires when aiming at {pitch:0.0} rad");

            input.Dash = false;
            for (int i = 0; i < 8; i++) p.Tick(1f / 60f, input, m);

            float moved = p.GlobalPosition.Y - startY;

            // Gravity is worth about a quarter of a metre over these nine ticks, so a flat dash
            // would land inside a tenth of a metre of where it started. Half a metre is well clear
            // of that in either direction.
            if (pitch > 0f)
                Check(moved > 0.5f, $"an airborne dash at {pitch:0.0} rad carries you upward (moved {moved:0.00}m)");
            else
                Check(moved < -0.5f, $"an airborne dash at {pitch:0.0} rad carries you downward (moved {moved:0.00}m)");
        }

        // ---- and the airborne climb is bounded ----
        //
        // Not because climbing is wrong — it is the requested behaviour — but because the play
        // boundary is a little over forty metres up and leaving it is fatal with no appeal. Flown
        // out to its apex rather than measured over nine ticks: the complaint was never about the
        // first fraction of a second, it was about where you end up.
        var straightUp = new PawnInput { Aim = MathU.FromAngle(0f), Pitch = Pawn.MaxPitch };
        Check(Fall(p, m, straightUp), "the harness can put a pawn in the air looking straight up");

        straightUp.Dash = true;
        float from = p.GlobalPosition.Y;

        p.Tick(1f / 60f, straightUp, m);
        straightUp.Dash = false;

        float apex = p.GlobalPosition.Y;
        for (int i = 0; i < 180 && p.GlobalPosition.Y >= apex - 0.01f; i++)
        {
            p.Tick(1f / 60f, straightUp, m);
            apex = MathF.Max(apex, p.GlobalPosition.Y);
        }

        float climb = apex - from;
        float run = p.Class.DashDistance;

        TestLog.Line($"    straight-up airborne dash climbs {climb:0.0}m "
                 + $"(a flat dash covers {run:0.0}m)");

        // The rule, stated the way the player stated it: a dash goes the same distance whichever
        // way it is pointed. Straight up should be worth the dash's own length and nothing more.
        //
        // It was eighteen metres. Almost none of that was the dash — the climb was capped at
        // twenty-two metres a second and then allowed to *coast*, so the dash contributed about
        // four metres and gravity gave back fourteen more on the way up. Capping the speed had
        // looked like a fix twice, and both times the catapult was the coast.
        Check(climb > run * 0.6f, $"a straight-up airborne dash is worth having ({climb:0.0}m)");
        Check(climb < run * 1.5f,
              $"and goes no further up than a dash goes along ({climb:0.0}m against {run:0.0}m)");

        p.Respawn(m.Arena.SpawnPoints[0]);
    }

    /// <summary>
    /// Put a pawn on solid floor and confirm it got there.
    ///
    /// Respawning drops a pawn onto a corner deck in mid-air, so "grounded" is a state the harness
    /// has to settle into rather than one it can set. Returns false rather than asserting, so the
    /// caller can report which case it failed to pose.
    /// </summary>
    static bool Land(Pawn p, Match m, PawnInput idle)
    {
        p.Respawn(m.Arena.SpawnPoints[0] + Vector3.Up * 0.6f);
        p.ClearSpawnProtectionForTest();

        for (int i = 0; i < 240; i++)
        {
            p.Tick(1f / 60f, idle, m);
            if (p.StandingOnFloor) return true;
        }

        return false;
    }

    /// <summary>
    /// Put a pawn in mid-air and confirm the simulation agrees that it is there.
    ///
    /// Teleporting one up is not enough, and this cost a false failure to work out: Godot's
    /// <c>IsOnFloor</c> reports the result of the last <c>MoveAndSlide</c>, so a pawn moved into
    /// the sky still reads as standing until it has been ticked once. A dash on that frame took
    /// the grounded branch and stayed flat, which looked exactly like the airborne dash being
    /// broken. Ticking until the pawn is genuinely falling poses the state instead of assuming it.
    /// </summary>
    static bool Fall(Pawn p, Match m, PawnInput idle)
    {
        p.Respawn(m.Arena.SpawnPoints[0] + Vector3.Up * 14f);
        p.ClearSpawnProtectionForTest();

        for (int i = 0; i < 8; i++)
        {
            p.Tick(1f / 60f, idle, m);
            if (!p.StandingOnFloor) return true;
        }

        return false;
    }

    /// <summary>
    /// A scope belongs to the weapon in your hands, not to the class you picked in the lobby.
    ///
    /// Reported as "I need to be able to pick up a rifle with a scope like the marksman has". The
    /// railgun already declared <c>HasScope</c> and the game ignored it — every scope decision read
    /// <c>pawn.Class.HasScope</c>, so the sight belonged permanently to the Marksman.
    /// </summary>
    static void CheckScopedRiflesCanBePickedUp()
    {
        int scoped = 0;
        foreach (var w in Weapons.Pickups) if (w.HasScope) scoped++;

        Check(scoped >= 2, "more than one weapon on the floor carries a scope");
        Check(Weapons.Longshot.HasScope, "the Longshot is scoped");
        Check(Weapons.Longshot.Ammo > 0, "and carries enough ammo to hold a lane with");
        Check(Weapons.Longshot.AdsFov < Classes.Trooper.AdsFov,
              "and zooms in further than an iron sight");

        var m = current!;
        var p = m.Pawns[0];

        if (p.InVehicle) m.ToggleVehicle(p);
        p.Respawn(m.Arena.SpawnPoints[0]);

        Check(!p.Weapon.HasScope || p.Class.HasScope,
              "a pawn on its class weapon has whatever sight that class has");

        p.TakeWeapon(Weapons.Longshot);
        Check(p.Weapon.HasScope, "and picking up a scoped rifle gives you the scope with it");
        Check(ReferenceEquals(p.Weapon, Weapons.Longshot), "the picked-up weapon is the active one");

        p.ResetLoadout();
    }

    /// <summary>
    /// Capture the flag, driven a step at a time rather than waited for.
    ///
    /// Every rule of the mode is a rule about state — who has what, whether your own flag is home,
    /// what happens when a carrier dies — and waiting for four bots to happen to produce each of
    /// those states inside a test window would be a coin flip dressed up as coverage. The world is
    /// arranged so there is a right answer, then the answer is checked.
    ///
    /// This runs inside whatever mode the current scenario is, so the flags are set up by hand.
    /// </summary>
    static void CheckCaptureTheFlag()
    {
        var ctf = Modes.Get(GameMode.CaptureTheFlag);

        Check(ctf.Teams, "capture the flag is a team mode");
        Check(ctf.LimitStep == 1, "captures step one at a time");
        Check(ctf.DefaultLimit <= 5, "and the default match is a handful of them, not thirty");

        // Played on its own match, because the flag rules only exist when the mode does — and
        // because taking over the running scenario would leave the invariant checks looking at a
        // world the scenario did not build.
        var roster = new List<LobbySlot>();
        for (int i = 0; i < 4; i++)
            roster.Add(new LobbySlot { IsBot = true, ClassIndex = i, FactionIndex = i });

        var m = new Match();
        m.Build(app, new MatchSettings
        {
            Mode = GameMode.CaptureTheFlag, ScoreLimit = 3, BotSkill = 1, ArenaIndex = 0,
        }, roster, visuals: false);

        Check(m.Flags.Count == 2, "a capture the flag match has two flags");
        if (m.Flags.Count != 2) { m.QueueFree(); return; }

        var red = m.FlagOf(0)!;
        var blue = m.FlagOf(1)!;

        Check(red.AtHome && blue.AtHome, "both flags start at their bases");

        // Slots alternate teams, so pawn 0 is on team 0 and pawn 1 is on team 1.
        var attacker = m.Pawns[0];
        var defender = m.Pawns[1];

        Check(Match.TeamOf(attacker.Slot) == 0 && Match.TeamOf(defender.Slot) == 1,
              "the probe pawns are on opposite teams");

        foreach (var p in m.Pawns) p.Respawn(m.Arena.SpawnPoints[p.Slot % m.Arena.SpawnPoints.Count]);

        // Everyone else out of the way, or a bot wandering into a base mid-test captures for free.
        for (int i = 2; i < m.Pawns.Count; i++)
            m.Pawns[i].GlobalPosition = m.Arena.SpawnPoints[i] + Vector3.Up * 40f;

        // ---- you cannot pick up your own flag where it stands ----
        attacker.GlobalPosition = red.Home;
        defender.GlobalPosition = m.Arena.SpawnPoints[1] + Vector3.Up * 40f;
        m.TickFlagsForTest(1f / 60f);

        Check(red.Carrier == null, "you cannot pick up your own flag off its base");

        // ---- but you can take theirs ----
        attacker.GlobalPosition = blue.Home;
        m.TickFlagsForTest(1f / 60f);

        Check(blue.Carrier == attacker, "walking onto the enemy flag takes it");
        Check(m.FlagCarriedBy(attacker) == blue, "and the carrier knows what it is holding");

        // ---- carrying it home scores ----
        int before = m.Captures;
        int scoreBefore = attacker.Score;

        attacker.GlobalPosition = red.Home;
        m.TickFlagsForTest(1f / 60f);

        Check(m.Captures == before + 1, "carrying their flag to your base is a capture");
        Check(attacker.Score == scoreBefore + 1, "and it scores for the team");
        Check(blue.AtHome, "the captured flag goes back to its own base");
        Check(m.FlagCarriedBy(attacker) == null, "and the carrier is empty-handed again");

        // ---- your own flag has to be home to score ----
        //
        // This is the rule that stops the mode being two teams running past each other.
        defender.GlobalPosition = red.Home;
        m.TickFlagsForTest(1f / 60f);
        Check(red.Carrier == defender, "the other side can take yours too");

        attacker.GlobalPosition = blue.Home;
        m.TickFlagsForTest(1f / 60f);
        Check(blue.Carrier == attacker, "and you can be holding theirs at the same time");

        before = m.Captures;
        attacker.GlobalPosition = red.Home;
        m.TickFlagsForTest(1f / 60f);

        Check(m.Captures == before, "you cannot score while your own flag is taken");
        Check(blue.Carrier == attacker, "and you keep hold of theirs while you wait");

        // ---- a dead carrier drops it, and a team-mate touching it sends it home ----
        //
        // Killed well away from either base. Dying on top of the flag's own base drops it exactly
        // where it lives, which counts as home and is correct behaviour — but it makes "did the
        // drop work?" unanswerable, which is how the first version of this test failed.
        var away = m.Arena.SpawnPoints[2];
        Check(away.DistanceTo(red.Home) > 20f, "the drop probe is well clear of the base");

        defender.GlobalPosition = away;
        m.TickFlagsForTest(1f / 60f);

        defender.Health = 0f;
        m.TickFlagsForTest(1f / 60f);

        Check(red.Carrier == null, "killing a carrier makes them drop the flag");
        Check(red.Dropped, "and it lies where they fell rather than teleporting home");

        var dropped = red.At;
        attacker.GlobalPosition = dropped;
        m.TickFlagsForTest(1f / 60f);

        Check(red.AtHome, "touching your own dropped flag returns it");

        // ---- and now the capture goes through ----
        before = m.Captures;
        attacker.GlobalPosition = red.Home;
        m.TickFlagsForTest(1f / 60f);
        Check(m.Captures == before + 1, "with your flag home again, the capture stands");

        // ---- a loose flag returns on its own ----
        defender.Respawn(m.Arena.SpawnPoints[1]);
        defender.GlobalPosition = red.Home;
        m.TickFlagsForTest(1f / 60f);

        // Team 1 taking team 0's flag, then dropping it in the middle of nowhere.
        Check(red.Carrier == defender, "the flag is taken again");

        defender.GlobalPosition = away;
        m.TickFlagsForTest(1f / 60f);

        defender.Health = 0f;
        m.TickFlagsForTest(1f / 60f);
        Check(red.Dropped, "and dropped");

        for (float t = 0f; t < Match.FlagReturnTime + 1f; t += 0.5f) m.TickFlagsForTest(0.5f);
        Check(red.AtHome, "a flag left lying comes home on its own");

        // ---- bots are pointed at the right thing ----
        attacker.Respawn(m.Arena.SpawnPoints[0]);
        Check(m.ObjectiveFor(attacker) is { } goal && goal.DistanceTo(blue.At) < 1f,
              "a bot with no flag heads for the enemy flag");

        blue.Carrier = attacker;
        Check(m.ObjectiveIsUrgent(attacker), "a carrier's errand outranks looting and healing");
        Check(m.ObjectiveFor(attacker) is { } run && run.DistanceTo(red.Home) < 1f,
              "and a carrier heads for their own base");

        // Parked above the world first — QueueFree is deferred, and these pawns are standing on
        // the live scenario's spawn coordinates until the frame ends.
        foreach (var p in m.Pawns) p.GlobalPosition = new Vector3(0f, 800f, 0f);

        m.QueueFree();
    }

    /// <summary>
    /// Every class ability fires, and every one of them does something you can measure.
    ///
    /// The whole point of this check is the second half. All four of these were fully defined and
    /// fully unreachable for weeks, and the UI suite was happily asserting that each class named
    /// its ability, explained it, and gave it a cooldown the entire time. Testing a definition
    /// tells you a designer filled in a form. It tells you nothing about whether a button does it.
    ///
    /// So each one is fired through the real pawn input path and then checked by its effect:
    /// someone takes damage, a grenade exists, the pawn moves faster, shots get tighter.
    /// </summary>
    static void CheckClassAbilities()
    {
        var m = current!;
        var a = m.Pawns[0];
        var b = m.Pawns[1];

        if (a.InVehicle) m.ToggleVehicle(a);
        if (b.InVehicle) m.ToggleVehicle(b);

        var press = new PawnInput { Aim = MathU.FromAngle(0f), ClassAbility = true };

        foreach (var cls in Classes.All)
        {
            a.Respawn(m.Arena.SpawnPoints[0]);
            a.ClearSpawnProtectionForTest();
            a.Class = cls;

            Check(a.ClassAbilityReady, $"{cls.Name}: the ability starts ready");

            int before = m.ClassAbilitiesUsed;
            int usesBefore = a.ClassAbilityUses;

            // Posed per ability, so each one has something to actually do.
            switch (cls.Special)
            {
                case SpecialKind.Shockwave:
                {
                    b.Respawn(m.Arena.SpawnPoints[0]);
                    b.ClearSpawnProtectionForTest();
                    b.GlobalPosition = a.GlobalPosition + new Vector3(cls.BlastRadius * 0.4f, 0f, 0f);

                    float health = b.Health;
                    a.Tick(1f / 60f, press, m);

                    Check(b.Health < health, $"{cls.Name}: Shockwave hurts someone inside the blast");
                    break;
                }

                case SpecialKind.Frag:
                {
                    int grenades = m.GrenadeCount;
                    a.Tick(1f / 60f, press, m);
                    Check(m.GrenadeCount > grenades, $"{cls.Name}: Frag puts a grenade in the air");
                    break;
                }

                case SpecialKind.Overdrive:
                {
                    float walk = a.EffectiveSpeed;
                    float interval = a.EffectiveFireInterval;

                    a.Tick(1f / 60f, press, m);

                    Check(a.Overdriven, $"{cls.Name}: Overdrive actually engages");
                    Check(a.EffectiveSpeed > walk, $"{cls.Name}: Overdrive is faster");
                    Check(a.EffectiveFireInterval < interval, $"{cls.Name}: Overdrive shoots quicker");
                    break;
                }

                case SpecialKind.Focus:
                {
                    float spread = a.EffectiveSpreadDeg;
                    float speed = a.EffectiveProjectileSpeed;

                    a.Tick(1f / 60f, press, m);

                    Check(a.Focused, $"{cls.Name}: Focus actually engages");
                    Check(a.EffectiveSpreadDeg <= spread, $"{cls.Name}: Focus tightens the spread");
                    Check(a.EffectiveProjectileSpeed > speed, $"{cls.Name}: Focus speeds the round up");
                    break;
                }
            }

            Check(m.ClassAbilitiesUsed > before, $"{cls.Name}: the ability reaches the match");
            Check(a.ClassAbilityUses > usesBefore, $"{cls.Name}: and the pawn counts having used it");
            Check(!a.ClassAbilityReady, $"{cls.Name}: and it goes on cooldown afterwards");

            // The faction special is on its own clock and must be untouched by any of this.
            Check(a.SpecialReady, $"{cls.Name}: the faction special is a separate cooldown");
        }

        // A timed ability has to end. Overdrive at 1.45x speed forever is not an ability.
        a.Respawn(m.Arena.SpawnPoints[0]);
        a.Class = Classes.Flanker;
        a.ClearSpawnProtectionForTest();
        a.Tick(1f / 60f, press, m);
        Check(a.Overdriven, "Overdrive is running");

        var idle = new PawnInput { Aim = MathU.FromAngle(0f) };

        // The buff ends on the *duration*; the cooldown runs longer, which is the whole reason a
        // timed ability is not permanent. Waiting only the duration and expecting to be able to
        // fire again was the test misreading its own design rule.
        for (int i = 0; i < (int)((Classes.Flanker.SpecialDuration + 0.5f) * 60f); i++)
            a.Tick(1f / 60f, idle, m);

        Check(!a.Overdriven, "and it runs out on its duration");
        Check(!a.ClassAbilityReady, "while the cooldown is still running");

        for (int i = 0; i < (int)(Classes.Flanker.SpecialCooldown * 60f); i++)
            a.Tick(1f / 60f, idle, m);

        Check(a.ClassAbilityReady, "and comes back once the cooldown is up");

        a.Class = Classes.ByIndex(a.Slot);
        a.Respawn(m.Arena.SpawnPoints[0]);
        b.Respawn(m.Arena.SpawnPoints[1]);
    }

    /// <summary>
    /// Battle points, and the spawn screen they pay for.
    ///
    /// Four separate things, and every one of them fails silently if it is wrong. Points that are
    /// never awarded look like a slow economy. A class choice that is not applied looks like the
    /// player mis-pressed. A purchase that never charges looks like generosity until somebody has
    /// four heroes. And a hero bought outside Juggernaut that does not actually put the crown on
    /// looks like a stronger Trooper — which is exactly the complaint that led to the saber.
    ///
    /// So each is posed directly rather than played for: the live scenario alongside this proves
    /// bots use the system, and this proves the system does what it says.
    /// </summary>
    static void CheckBattlePoints()
    {
        // ---- the roster is coherent ----
        Check(Reinforcements.Basic.Length == Classes.All.Length,
              "every basic class can be spawned as");

        foreach (var r in Reinforcements.Basic)
            Check(r.Cost == 0 && r.IsBasic, $"{r.Name} is free");

        var faction = new HashSet<string>();
        foreach (var r in Reinforcements.All)
        {
            if (r.IsBasic) continue;

            Check(r.Faction != null, $"{r.Name} belongs to a faction");
            Check(r.Cost > 0, $"{r.Name} costs something");
            Check(r.Epithet.Length > 0 && r.Blurb.Length > 0, $"{r.Name} is named and explained");
            Check(faction.Add(r.Name), $"{r.Name} appears once");
        }

        // Two per faction, and the same two tiers each, or one side of the roster is stronger for
        // reasons nobody chose.
        foreach (var f in Factions.All)
        {
            var mine = Reinforcements.For(f);
            int line = 0, elite = 0;

            foreach (var r in mine)
            {
                if (r.Tier == ReinforcementTier.Line) line++;
                if (r.Tier == ReinforcementTier.Elite) elite++;
                Check(r.IsBasic || r.Faction == f, $"{f.Name} is only offered its own characters");
            }

            TestLog.Line($"    {f.Name}: {mine.Count} spawn options");
            Check(line == 1 && elite == 1, $"{f.Name} fields one of each tier ({line}/{elite})");
            Check(mine.Count == Reinforcements.Basic.Length + 2,
                  $"{f.Name} offers the basics plus its own two");
        }

        // A paid character has to actually be worth paying for. Compared against the best basic on
        // each axis rather than against the Trooper, so "it is better than the worst class" does
        // not pass for an upgrade.
        float bestHealth = 0f, bestDps = 0f, bestSpeed = 0f;
        foreach (var c in Classes.All)
        {
            bestHealth = MathF.Max(bestHealth, c.Health);
            bestDps = MathF.Max(bestDps, c.Dps);
            bestSpeed = MathF.Max(bestSpeed, c.Speed);
        }

        foreach (var r in Reinforcements.All)
        {
            if (r.IsBasic) continue;

            var c = r.Class;
            bool standsOut = c.Health > bestHealth || c.Dps > bestDps || c.Speed > bestSpeed
                             || c.HasScope;

            TestLog.Line($"    {r.Name}: {c.Health:0}hp, {c.Dps:0} dps, {c.Speed:0.0} m/s, {r.Cost}pts");
            Check(standsOut, $"{r.Name} beats every basic class at something");
        }

        // ---- a match to pose it in ----
        var roster = new List<LobbySlot>();
        for (int i = 0; i < 4; i++)
            roster.Add(new LobbySlot { IsBot = true, ClassIndex = i, FactionIndex = i });

        var m = new Match();
        m.Build(app, new MatchSettings
        {
            Mode = GameMode.TeamDeathmatch, ScoreLimit = 50, BotSkill = 1, ArenaIndex = 0,
        }, roster, visuals: false);

        var killer = m.Pawns[0];
        var victim = m.Pawns[1];      // slot 1: the other team

        foreach (var p in m.Pawns) { p.Respawn(m.Arena.SpawnPoints[p.Slot]); p.ClearSpawnProtectionForTest(); }
        foreach (var p in m.Pawns) { p.BattlePoints = 0; p.PointsEarned = 0; }

        // ---- a kill pays ----
        m.AwardKillForTest(killer, victim);

        Check(killer.BattlePoints == BattlePoints.Kill,
              $"a kill banks points ({killer.BattlePoints})");
        Check(killer.PointsEarned == BattlePoints.Kill, "and the running total remembers it");
        Check(victim.BattlePoints == 0, "dying does not");

        // ---- but not off your own side, and not off yourself ----
        var mate = m.Pawns[2];        // slot 2: same team as slot 0
        Check(Match.SameTeam(killer, mate), "the harness has a teammate to hand");

        int before = killer.BattlePoints;
        mate.Respawn(m.Arena.SpawnPoints[2]);
        mate.ClearSpawnProtectionForTest();
        m.AwardKillForTest(killer, mate);

        Check(killer.BattlePoints == before, "shooting your own side banks nothing");

        killer.Respawn(m.Arena.SpawnPoints[0]);
        killer.ClearSpawnProtectionForTest();
        m.AwardKillForTest(killer, killer);

        Check(killer.BattlePoints == before, "and neither does shooting yourself");

        // ---- killing a hero pays a great deal more ----
        victim.Respawn(m.Arena.SpawnPoints[1]);
        victim.ClearSpawnProtectionForTest();
        victim.TakeCrown(victim.Faction.Juggernaut);

        before = killer.BattlePoints;
        m.AwardKillForTest(killer, victim);

        Check(killer.BattlePoints - before == BattlePoints.HeroKill,
              $"killing a hero is worth much more ({killer.BattlePoints - before})");
        Check(BattlePoints.HeroKill > BattlePoints.Kill * 2, "which is the point of it");

        victim.LoseCrown();

        // ---- an objective outpays a kill ----
        Check(BattlePoints.PostCapture > BattlePoints.Kill, "taking a post beats taking a life");
        Check(BattlePoints.FlagCapture > BattlePoints.Kill, "and so does capping a flag");

        CheckSpawnChoiceIsObeyed(m);

        foreach (var p in m.Pawns) p.GlobalPosition = new Vector3(0f, 800f, 0f);
        m.QueueFree();
    }

    /// <summary>
    /// What you picked is what you come back as, and it is paid for.
    ///
    /// Driven through the real respawn loop rather than by calling the apply step directly, because
    /// the interesting failures all live in the wiring: a choice recorded and never read, a purchase
    /// charged twice because the timer ticked past zero on two frames, a hero flag that survives its
    /// own spawn and re-crowns you next time you die.
    /// </summary>
    static void CheckSpawnChoiceIsObeyed(Match m)
    {
        var p = m.Pawns[0];
        var idle = new PawnInput { Aim = MathU.FromAngle(0f) };

        // ---- a basic class, chosen and worn ----
        foreach (var want in Reinforcements.Basic)
        {
            p.Respawn(m.Arena.SpawnPoints[0]);
            Down(p);

            p.BattlePoints = 0;
            p.NextSpawn = want;
            p.SpawnConfirmed = true;

            for (int i = 0; i < 300 && !p.Alive; i++) m._PhysicsProcess(1.0 / 60.0);

            Check(p.Alive, $"the fighter comes back as {want.Name}");
            Check(p.Class == want.Class, $"and is a {want.Name} ({p.Class.Name})");
            Check(p.Wearing == want, "and the HUD knows it");
            Check(p.Weapon == want.Class.Weapon,
                  $"holding the {want.Class.WeaponName} rather than the last one");
            Check(MathF.Abs(p.Health - want.Class.Health) < 0.01f,
                  $"at a {want.Name}'s health ({p.Health:0} of {want.Class.Health:0})");
            Check(!p.SpawnConfirmed, "and the choice is cleared for next time");
        }

        // ---- a paid character you cannot afford ----
        var elite = Reinforcements.For(p.Faction)[^1];
        Check(elite.Cost > 0, "the harness has something to buy");

        p.Respawn(m.Arena.SpawnPoints[0]);
        Down(p);
        p.BattlePoints = elite.Cost - 1;
        p.NextSpawn = elite;
        p.SpawnConfirmed = true;

        for (int i = 0; i < 300 && !p.Alive; i++) m._PhysicsProcess(1.0 / 60.0);

        Check(p.Class != elite.Class,
              $"a fighter one point short does not get a {elite.Name}");
        Check(p.BattlePoints == elite.Cost - 1, "and is not charged for it");

        // ---- and one you can ----
        Down(p);
        p.BattlePoints = elite.Cost + 250;
        p.NextSpawn = elite;
        p.SpawnConfirmed = true;

        for (int i = 0; i < 300 && !p.Alive; i++) m._PhysicsProcess(1.0 / 60.0);

        Check(p.Class == elite.Class, $"paying for a {elite.Name} gets you one");
        Check(p.BattlePoints == 250, $"and costs exactly the price ({p.BattlePoints} left)");
        Check(p.Health > Classes.Trooper.Health,
              $"which is worth having ({p.Health:0} health)");

        // ---- a hero costs what a hero costs ----
        //
        // The assertion this test was missing, and it cost the worst bug of the batch. It checked
        // that buying a hero produced a hero and never that it produced a *bill*, so nothing
        // noticed the price was being taken in the bot path and nowhere else — a human could buy
        // one for free, every death, all match. An assertion that a thing happens is not an
        // assertion that it costs anything.
        Down(p);
        p.BattlePoints = Reinforcements.HeroCost + 400;
        p.NextSpawn = Reinforcements.Trooper;
        p.HeroBought = true;
        p.SpawnConfirmed = true;

        for (int i = 0; i < 300 && !p.Alive; i++) m._PhysicsProcess(1.0 / 60.0);

        Check(p.BattlePoints == 400,
              $"a hero costs the hero price ({Reinforcements.HeroCost + 400 - p.BattlePoints} taken)");

        // And cannot be had on credit.
        p.LoseCrown();
        Down(p);
        p.BattlePoints = Reinforcements.HeroCost - 1;
        p.NextSpawn = Reinforcements.Trooper;
        p.HeroBought = true;
        p.SpawnConfirmed = true;

        for (int i = 0; i < 300 && !p.Alive; i++) m._PhysicsProcess(1.0 / 60.0);

        Check(!p.IsJuggernaut, "and one point short buys nothing");
        Check(p.BattlePoints == Reinforcements.HeroCost - 1, "and is not charged for the refusal");

        // ---- a hero is the crown, not a class ----
        Down(p);
        p.BattlePoints = Reinforcements.HeroCost;
        p.NextSpawn = Reinforcements.Trooper;
        p.HeroBought = true;
        p.SpawnConfirmed = true;

        for (int i = 0; i < 300 && !p.Alive; i++) m._PhysicsProcess(1.0 / 60.0);

        Check(p.IsJuggernaut, "buying a hero puts the crown on");
        Check(p.Crown == p.Faction.Juggernaut, "your own faction's, not somebody else's");
        Check(p.HasSaber, "with the blade that comes with it");
        Check(p.MaxHealth > Classes.Tactician.Health * 2f,
              $"and a titan's health ({p.MaxHealth:0})");
        Check(!p.HeroBought, "and the purchase does not linger");

        // Only one at a time per side, or a match becomes four titans in a circle.
        Check(!m.HeroAvailableTo(m.Pawns[2]),
              "no second hero while a teammate is wearing one");

        // ---- and it is lost on death ----
        Down(p);
        p.BattlePoints = 0;
        p.NextSpawn = Reinforcements.Trooper;
        p.SpawnConfirmed = true;

        for (int i = 0; i < 300 && !p.Alive; i++) m._PhysicsProcess(1.0 / 60.0);

        Check(!p.IsJuggernaut, "dying as a hero puts you back in the ranks");
        Check(p.Class == Classes.Trooper, "as whatever you chose next");
        Check(m.HeroAvailableTo(m.Pawns[2]), "and frees the slot for someone else");
    }

    /// <summary>Kill a pawn outright through the real damage path, so the death bookkeeping runs.</summary>
    static void Down(Pawn p)
    {
        p.ClearSpawnProtectionForTest();
        p.TakeDamage(p.MaxHealth * 3f);
    }

    /// <summary>
    /// A tank shell goes where the crosshair is pointing.
    ///
    /// This exists to settle a bug that was reported three times — by me, to the user, repeatedly —
    /// and was not real. The reasoning was that the on-foot look rates (3.4 yaw, 2.2 pitch) are
    /// faster than the turret's (2.4 and 1.5), so the camera must outrun the gun and the crosshair
    /// must drift off the barrel. Every one of those numbers is correct and the conclusion was
    /// wrong: while driving, the camera does not use the on-foot rates at all. It looks down
    /// <c>ride.AimDir</c>, the shell is fired down <c>v.AimDir</c>, and those are the same vector,
    /// so the two cannot diverge by construction.
    ///
    /// Reading two constants and inferring a bug between them is not the same as tracing the path,
    /// and the difference between those is exactly what a test is for. This is the trace, written
    /// down: traverse the turret somewhere awkward, fire, and confirm the round leaves along the
    /// gun. If anybody ever wires the vehicle camera to the view's own yaw, this fails.
    /// </summary>
    static void CheckTankShellFollowsTheCrosshair()
    {
        var m = current!;

        Vehicle? tank = null;
        foreach (var (hull, _, _) in driveProbes)
            if (hull.HasTurret && hull.Alive) { tank = hull; break; }

        Check(tank != null, "the harness has a turreted hull to aim");
        if (tank is not { } rig) return;

        // A driver, because the cannon refuses to fire without one — and by the time this runs the
        // destructibility check has wrecked every hull once and thrown its driver clear.
        if (rig.Driver == null)
        {
            var crew = m.Pawns[0];
            if (crew.Riding is { } already) already.Eject();
            rig.Board(crew);
        }

        Check(rig.Driver != null, "and somebody in the seat to fire it");
        if (rig.Driver == null) return;

        // Board() overwrites the hull facing with the driver's, so the turret is laid *after*
        // seating rather than before — the other way round and this measures nothing.
        //
        // Somewhere awkward on both axes, so a bug that only shows up off-centre still shows up.
        rig.TurretYaw = rig.Facing + 0.9f;
        rig.TurretPitch = 0.35f;

        Vector3 aim = rig.AimDir;

        Check(MathF.Abs(aim.Length() - 1f) < 0.01f, "the gun direction is a unit vector");

        // The turret must actually be off the hull, or this proves nothing about traverse.
        Vector3 hullFwd = rig.Forward;
        float offHull = MathF.Acos(Mathf.Clamp(aim.Dot(hullFwd), -1f, 1f));

        TestLog.Line($"    tank: gun {offHull:0.00} rad off the hull, elevation {rig.TurretPitch:0.00}");
        Check(offHull > 0.3f, $"the turret really is traversed away from the hull ({offHull:0.00} rad)");

        // Fire down it and see where the round goes. Several, because the cannon has a little
        // spread and one round would be testing the dice as much as the geometry.
        m.ClearShotsForTest();

        float worst = 0f;
        const int Rounds = 8;

        for (int i = 0; i < Rounds; i++)
        {
            m.ClearShotsForTest();
            m.FireVehicleWeapon(rig);

            Check(m.ShotsInFlight > 0, "the cannon fires");
            if (m.ShotsInFlight == 0) return;

            Vector3 went = m.ShotVelocityForTest(0).Normalized();
            worst = MathF.Max(worst, MathF.Acos(Mathf.Clamp(went.Dot(aim), -1f, 1f)));
        }

        m.ClearShotsForTest();

        // The cannon's own cone is 0.6 degrees, so anything inside a couple of degrees is the
        // spread and nothing else. A camera-versus-turret divergence would be tens of degrees.
        float spread = Mathf.DegToRad(Vehicles.Tank.Gun!.SpreadDeg);

        TestLog.Line($"    tank: worst round {Mathf.RadToDeg(worst):0.00}° off the gun "
                 + $"(cone is {Vehicles.Tank.Gun.SpreadDeg:0.0}°)");

        Check(worst < spread + 0.02f,
              $"every round leaves along the gun ({Mathf.RadToDeg(worst):0.00}° off)");

        // And the camera is aimed from the same vector the shell is. Asserted on the property both
        // of them read rather than on a camera the harness has no viewport for: if this is what the
        // renderer looks down — and it is, in the driving branch of DrawCameras — then the crosshair
        // is the gun, whatever the on-foot look rates happen to be.
        Check(rig.AimDir == aim, "and the camera looks down that same vector");
    }

    /// <summary>
    /// How hard each difficulty actually hits, measured rather than asserted.
    ///
    /// "The bots are too deadly" is a real report and it was impossible to act on, because nothing
    /// in this project turned bot difficulty into a number. The skill table has aim error, reaction,
    /// slew and burst timings in it, and no way to tell what any combination of them does to a
    /// person standing in front of one.
    ///
    /// So: one bot, one stationary target, a fixed range, a fixed window. Damage landed is the
    /// number. It is not a claim about how the game feels — it is the thing that has to move when
    /// somebody says the bots are too strong, and the thing that must not creep back up later.
    ///
    /// Ordering is asserted, absolute values are only reported. A Veteran must out-damage a Recruit
    /// or the difficulty slider is not a slider; exactly how much is a tuning decision, and pinning
    /// it in a test would mean every future tuning pass starts by editing the test to agree.
    /// </summary>
    static void CheckBotLethality()
    {
        lethalityIndex = 0;
        lethalityTrial = 0;
        lethalityTotal = 0f;
        lethalityAimTotal = 0f;
        lethalityResults.Clear();
        BeginLethalityProbe();
    }

    static readonly List<(string Name, float Damage, float Aim)> lethalityResults = new();
    static int lethalityIndex;
    static int lethalityTrial;
    static float lethalityTotal;
    static float lethalityAimTotal;
    static Match? lethalityMatch;

    /// <summary>
    /// Trials averaged per difficulty.
    ///
    /// One run of one bot is far too noisy to tune against: the aim wander is random and a five
    /// second window is a tiny sample, so a difficulty that had just been *weakened* measured twice
    /// as deadly as before. Averaging is what turns this from an anecdote into an instrument.
    /// </summary>
    const int LethalityTrials = 6;

    /// <summary>Seconds of sustained fire each difficulty gets to prove itself.</summary>
    const float LethalityWindow = 8f;

    /// <summary>How far the target stands. Inside every class's reach, outside knife range.</summary>
    const float LethalityRange = 20f;

    /// <summary>
    /// Pose one difficulty's bot against a target, and hand off a frame so physics can see them.
    ///
    /// Its own match per difficulty, frozen, because the brain has to be the one the match built —
    /// difficulty is baked into a BotBrain at construction and there is no way to re-skill one.
    /// </summary>
    static void BeginLethalityProbe()
    {
        if (lethalityIndex >= BotBrain.Skills.Length) { FinishLethality(); return; }

        var roster = new List<LobbySlot>
        {
            new() { IsBot = true, ClassIndex = 0, FactionIndex = 0 },
            new() { IsBot = true, ClassIndex = 0, FactionIndex = 1 },
        };

        var m = new Match();
        m.Build(app, new MatchSettings
        {
            Mode = GameMode.Deathmatch, ScoreLimit = 99,
            BotSkill = lethalityIndex, ArenaIndex = 0,
        }, roster, visuals: false);

        m.Paused = true;

        // High above everything, in open air. The shared physics world has every other scenario's
        // arena and fighters in it, and a lane that is clear of this arena's geometry can still
        // have somebody else standing in it.
        var at = new Vector3(0f, 300f, 0f);

        var shooter = m.Pawns[0];
        var mark = m.Pawns[1];

        shooter.Respawn(at);
        shooter.ClearSpawnProtectionForTest();
        shooter.Tick(1f / 60f, new PawnInput { Aim = MathU.FromAngle(0f) }, m);

        mark.Respawn(at + new Vector3(LethalityRange, 0f, 0f));
        mark.ClearSpawnProtectionForTest();
        mark.Tick(1f / 60f, new PawnInput { Aim = MathU.FromAngle(MathF.PI) }, m);

        lethalityMatch = m;
        NextFrame(FinishLethalityProbe);
    }

    /// <summary>
    /// Let the brain fight for five seconds and count what landed.
    ///
    /// The target is healed back up every tick rather than allowed to die. Time-to-kill would stop
    /// the clock at the first kill and measure nothing after it, which makes the fast difficulties
    /// indistinguishable from each other; total damage over a fixed window separates them all the
    /// way up. It also keeps the probe from turning into a respawn test.
    /// </summary>
    static void FinishLethalityProbe()
    {
        var m = lethalityMatch!;
        var shooter = m.Pawns[0];
        var mark = m.Pawns[1];
        var brain = m.BrainForTest(0);

        Check(brain != null, "the harness has a brain to drive");

        if (brain == null) { m.QueueFree(); lethalityMatch = null; return; }

        Vector3 shooterAt = shooter.GlobalPosition;
        Vector3 markAt = mark.GlobalPosition;

        float landed = 0f;
        float aimError = 0f;
        int aimSamples = 0;
        const float dt = 1f / 60f;

        // The true bearing from shooter to target, which never changes because neither of them
        // moves. Everything the bot's aim does is measured against this one number.
        Vector3 toMark = markAt - shooterAt;
        float trueYaw = MathF.Atan2(toMark.Z, toMark.X);

        for (int i = 0; i < (int)(LethalityWindow * 60f); i++)
        {
            // Both held in place. What is under test is the shooting, not the walking, and a bot
            // that wanders off mid-probe would measure its pathfinding instead.
            shooter.GlobalPosition = shooterAt;
            mark.GlobalPosition = markAt;

            var input = brain.Think(dt, shooter, m);
            shooter.Tick(dt, input, m);

            float before = mark.Health;
            m.StepShotsForTest(dt);

            landed += MathF.Max(0f, before - mark.Health);

            // How far off the bot is pointing, this frame.
            //
            // This is the measurement that actually works. Damage is a threshold: a shot either
            // lands or it does not, so a run of luck swings the total wildly and six averaged
            // trials still put Recruit above Veteran often enough to fail the ordering on correct
            // code. Angular error is continuous, sampled every frame rather than every shot, and it
            // is the thing the difficulty table literally sets — so it separates the four cleanly.
            aimError += MathF.Abs(MathU.AngleDiff(shooter.Facing, trueYaw));
            aimSamples++;

            mark.Health = mark.MaxHealth;
            mark.ClearSpawnProtectionForTest();
        }

        lethalityTotal += landed;
        lethalityAimTotal += aimSamples > 0 ? aimError / aimSamples : 0f;

        foreach (var p in m.Pawns) p.GlobalPosition = new Vector3(0f, 800f, 0f);
        m.QueueFree();
        lethalityMatch = null;

        lethalityTrial++;

        if (lethalityTrial < LethalityTrials) { BeginLethalityProbe(); return; }

        string name = BotBrain.Skills[lethalityIndex].Name;
        float mean = lethalityTotal / LethalityTrials;
        float aim = lethalityAimTotal / LethalityTrials;
        lethalityResults.Add((name, mean, aim));

        TestLog.Line($"    {name}: {mean:0} damage in {LethalityWindow:0}s at {LethalityRange:0}m "
                 + $"({mean / LethalityWindow:0} dps), aim off by {Mathf.RadToDeg(aim):0.0}°");

        lethalityIndex++;
        lethalityTrial = 0;
        lethalityTotal = 0f;
        lethalityAimTotal = 0f;
        BeginLethalityProbe();
    }

    /// <summary>Once every difficulty has been measured, check they are actually different.</summary>
    static void FinishLethality()
    {
        Check(lethalityResults.Count == BotBrain.Skills.Length,
              $"every difficulty was measured ({lethalityResults.Count})");

        if (lethalityResults.Count < 2) return;

        // Every difficulty has to land *something*, or it is not an opponent.
        foreach (var (name, damage, _) in lethalityResults)
            Check(damage > 0f, $"a {name} bot can hit a stationary target at all ({damage:0})");

        // The ordering is asserted on aim, not on damage.
        //
        // This check used to compare damage end to end and it was flaky: it fired once at
        // "Recruit 102 vs Veteran 133" on a build where nothing was wrong. Damage is a threshold —
        // each shot lands or does not — so even six averaged trials swing far enough to invert the
        // two ends of the ladder. Angular error is continuous and is the quantity the difficulty
        // table actually sets, so it separates them by a wide, stable margin.
        float worstAim = lethalityResults[0].Aim;
        float bestAim = lethalityResults[^1].Aim;

        Check(bestAim < worstAim * 0.75f,
              $"the difficulty slider is a slider "
              + $"({lethalityResults[0].Name} off by {Mathf.RadToDeg(worstAim):0.0}°, "
              + $"{lethalityResults[^1].Name} off by {Mathf.RadToDeg(bestAim):0.0}°)");

        // The one absolute bound worth keeping. A stationary target in the open is the easiest shot
        // in the game and the bot has five seconds of it; if the hardest difficulty cannot be
        // survived even there, no amount of playing well helps anywhere else.
        float toughest = 0f;
        foreach (var c in Classes.All) toughest = MathF.Max(toughest, c.Health);

        float strongest = lethalityResults[^1].Damage;

        TestLog.Line($"    the toughest class has {toughest:0} health; "
                 + $"a {lethalityResults[^1].Name} lands {strongest:0} in {LethalityWindow:0}s");

        // What the player actually faces, which is never one bot. A default match fields seven, and
        // a single-bot figure quietly understates the thing being complained about by that factor.
        var defaults = new MatchSettings();
        TestLog.Line($"    a default match fields {defaults.BotCount} bots: "
                 + $"{strongest / LethalityWindow * defaults.BotCount:0} dps if they all see you");

        // Still measured in damage, because "the bots are too deadly" was a complaint about
        // damage. A loose ceiling rather than a tight ordering: this one only has to catch a
        // regression that makes the hardest difficulty lethal again, and a wide bound survives the
        // variance that broke the ordering check.
        Check(strongest < toughest * 6f,
              $"even the hardest bot is not instant death ({strongest:0} against {toughest:0} health)");
    }

    /// <summary>
    /// The grenade launcher: a fuse, a bounce, and the biggest blast in the game.
    ///
    /// Every one of those three fails silently on its own. A fuse that never fires leaves a round
    /// bouncing around the map forever, which from inside a match looks like the gun not working. A
    /// bounce that does not happen turns it into a slow rocket. And a blast that is not actually
    /// bigger than a rocket's makes the whole weapon pointless, which no amount of playing would
    /// tell you quickly.
    /// </summary>
    static void CheckGrenadeLauncher()
    {
        var g = Weapons.GrenadeLauncher;
        var rocket = Weapons.RocketLauncher;

        Check(g.Explodes, "the grenade launcher explodes");
        Check(g.FuseTime > 0f, $"on a fuse rather than on contact ({g.FuseTime:0.0}s)");
        Check(g.Bounces, "and bounces off the world on the way");
        Check(g.Weight > 0f, "and arcs rather than flying flat");

        // The number the request was actually about.
        float ratio = g.BlastDamage / rocket.BlastDamage;
        TestLog.Line($"    grenade blast {g.BlastDamage:0} vs rocket {rocket.BlastDamage:0} ({ratio:0.00}x)");

        Check(ratio > 1.2f && ratio < 1.45f,
              $"a grenade is about a third heavier than a rocket ({ratio:0.00}x)");

        foreach (var cl in Classes.All)
            Check(g.BlastDamage > cl.Health, $"a direct hit kills a {cl.Name} outright");

        // Harder to land, or the rocket launcher would never be worth picking up again.
        Check(g.ProjectileSpeed < rocket.ProjectileSpeed,
              "and it is slower in the air than a rocket");
        Check(g.FireInterval > rocket.FireInterval, "and fires less often");
        Check(g.Ammo < rocket.Ammo, "and carries fewer");

        // ---- a real grenade, through the real sweep ----
        var m = current!;
        var p = m.Pawns[0];

        if (p.InVehicle) m.ToggleVehicle(p);

        var lane = ClearLane(m.Arena, 10f);
        Check(lane.HasValue, "the harness has somewhere to lob one");
        if (lane is not { } at) return;

        var idle = new PawnInput { Aim = MathU.FromAngle(0f) };

        p.Respawn(at);
        p.ClearSpawnProtectionForTest();
        p.ResetLoadout();
        p.TakeWeapon(Weapons.GrenadeLauncher);
        p.Tick(1f / 60f, idle, m);

        Check(p.Weapon == Weapons.GrenadeLauncher, "and something to lob it with");

        int before = m.ShotsFired;
        m.FireWeapon(p);
        Check(m.ShotsFired > before, "the grenade goes out");

        // It must still be in the air well after a rocket would have hit something. Stepped without
        // ticking the pawn, so the only thing moving is the round.
        for (int i = 0; i < 30; i++) m.StepShotsForTest(1f / 60f);
        Check(m.ShotsInFlight > 0, "and is still in the air half a second later");

        // And it must go off on its own, with nothing to hit.
        for (int i = 0; i < (int)(g.FuseTime * 60f) + 40; i++) m.StepShotsForTest(1f / 60f);
        Check(m.ShotsInFlight == 0, $"and detonates on its own after {g.FuseTime:0.0}s");

        p.Respawn(m.Arena.SpawnPoints[0]);
        p.ResetLoadout();
    }

    /// <summary>
    /// The flamethrower: a cloud rather than a shot.
    ///
    /// Tested as a shape rather than a number. What makes it a flamethrower is that it is the best
    /// weapon in the game at four metres and the worst at twenty, and both halves have to be true
    /// or it is either a shotgun or a bad rifle.
    /// </summary>
    static void CheckFlamethrower()
    {
        var f = Weapons.Flamethrower;

        Check(!f.IsUtility, "the flamethrower is a weapon");
        Check(f.Pellets > 1, "it puts out a cloud rather than a shot");
        Check(f.SpreadDeg > 20f, $"a wide one ({f.SpreadDeg:0}°)");

        // Above everything in the game at point blank.
        float best = 0f;
        string bestName = "";
        foreach (var c in Classes.All)
            if (c.Dps > best) { best = c.Dps; bestName = c.Name; }

        TestLog.Line($"    flamethrower {f.Dps:0} dps at {f.Range:0}m "
                 + $"(best class is the {bestName} at {best:0})");

        Check(f.Dps > best, $"and hits harder than any class weapon up close ({f.Dps:0} vs {best:0})");

        // And useless past its own nose. Compared against every other pickup, so "shorter than the
        // railgun" does not pass for short.
        foreach (var w in Weapons.Pickups)
        {
            if (w == f || w.IsUtility || w == Weapons.Sword) continue;
            Check(f.Range < w.Range, $"and reaches nowhere next to the {w.Name}");
        }

        Check(f.Ammo > 60, "with enough fuel to hold a doorway");
    }

    /// <summary>
    /// A gate over a pit is a weapon, and the person who built it gets the kill.
    ///
    /// Reported from play: putting a portal over a pit and shooting people into it. Every part of
    /// that is deliberate and the game scored none of it — an environmental death arrives with no
    /// attacker, so all four of them are booked as suicides, and the player doing the cleverest
    /// thing on the map was getting minus one frag for it.
    ///
    /// The narrowness is the part worth testing. It must credit a death with nobody to blame, and
    /// it must not touch a death that already has a killer, or laying a gate becomes a way to steal
    /// other people's frags.
    /// </summary>
    static void CheckPortalKills()
    {
        var m = current!;
        var layer = m.Pawns[0];
        var victim = m.Pawns[1];

        if (layer.InVehicle) m.ToggleVehicle(layer);
        if (victim.InVehicle) m.ToggleVehicle(victim);

        Check(Match.PortalCreditWindow >= 1.5f && Match.PortalCreditWindow <= 4f,
              $"the credit window is about two seconds ({Match.PortalCreditWindow:0.0}s)");

        // Two gates, laid by pawn zero.
        m.ClearPortalsForTest();
        m.PlantPortalForTest(m.Arena.SpawnPoints[2] + Vector3.Up * 1f, Vector3.Up);
        m.PlantPortalForTest(m.Arena.SpawnPoints[3] + Vector3.Up * 1f, Vector3.Up);

        Check(m.PortalCount == 2, "two gates are up");

        // ---- through the gate, then off the world ----
        victim.Respawn(m.PortalSpot(0));
        victim.ClearSpawnProtectionForTest();

        int killsBefore = layer.Score;
        int portalKillsBefore = m.PortalKills;
        int tripsBefore = m.PortalTrips;

        m.StepPortalsForTest(1f / 60f);
        Check(m.PortalTrips > tripsBefore, "the victim goes through");

        victim.TakeFatalFall();
        m.AwardEnvironmentalDeathForTest(victim);

        Check(m.PortalKills > portalKillsBefore, "falling straight afterwards is somebody's doing");
        Check(layer.Score > killsBefore,
              $"and it is the gate-layer's kill ({layer.Score - killsBefore})");
        Check(victim.KilledBy == layer.Name2, $"who the feed names ({victim.KilledBy})");

        // ---- but not for ever ----
        victim.Respawn(m.PortalSpot(0));
        victim.ClearSpawnProtectionForTest();
        m.StepPortalsForTest(1f / 60f);

        // Run the clock well past the window without anything else happening.
        for (int i = 0; i < (int)((Match.PortalCreditWindow + 1f) * 60f); i++)
            m.AdvanceClockForTest(1f / 60f);

        killsBefore = layer.Score;
        portalKillsBefore = m.PortalKills;

        victim.TakeFatalFall();
        m.AwardEnvironmentalDeathForTest(victim);

        Check(m.PortalKills == portalKillsBefore,
              "a fall long afterwards is nobody's but your own");
        Check(layer.Score <= killsBefore, "and the gate-layer gets nothing for it");

        // ---- and never off a real kill ----
        victim.Respawn(m.PortalSpot(0));
        victim.ClearSpawnProtectionForTest();
        m.StepPortalsForTest(1f / 60f);

        var shooter = m.Pawns[2];
        int layerBefore = layer.Score;
        int shooterBefore = shooter.Score;

        m.AwardKillForTest(shooter, victim);

        Check(shooter.Score > shooterBefore, "someone who shoots you through a gate keeps the kill");
        Check(layer.Score == layerBefore, "and the gate-layer does not take it off them");

        // ---- and a gate carries rounds, not just people ----
        //
        // A gate you can walk through but cannot shoot through is not a hole in space, it is a
        // lift. Injected at a known point with a known heading rather than fired, because what is
        // under test is the gate, and firing drags in the whole ray sweep and whatever else in the
        // shared physics world happens to be standing in the way.
        Vector3 mouth = m.PortalSpot(0);
        Vector3 far = m.PortalSpot(1);

        m.ClearShotsForTest();
        m.InjectShotForTest(layer, mouth, Vector3.Right * 30f);

        Check(m.ShotsInFlight == 1, "a round is in the air");

        m.StepShotsForTest(1f / 60f);

        Check(m.ShotsInFlight == 1, "and the gate does not eat it");
        Check(m.ShotPositionForTest(0).DistanceTo(far) < 4f,
              $"it comes out of the far gate ({m.ShotPositionForTest(0).DistanceTo(far):0.0}m from it)");
        Check(m.ShotPositionForTest(0).DistanceTo(mouth) > 10f, "rather than staying where it was");

        m.ClearShotsForTest();
        m.ClearPortalsForTest();
        foreach (var p in m.Pawns) p.Respawn(m.Arena.SpawnPoints[p.Slot]);
    }

    /// <summary>
    /// The rope pulls the other way when it hits something alive.
    ///
    /// Two behaviours from one weapon, decided by what it struck, and the failure mode is that a
    /// hook on a person quietly falls through to the old path — which would haul the *shooter* into
    /// the person they just shot, doing the exact opposite of what was asked for and looking, in a
    /// match, like an aggressive lunge that went wrong.
    ///
    /// Its own match, frozen, and straddling a physics step. All three are needed: the bodies have
    /// to be posed and then left alone for a step before a ray can see them, and the live scenario's
    /// pawns are being moved by half a dozen other checks in between.
    /// </summary>
    static void CheckGrappleHauls()
    {
        Check(Match.GrappleHaulSpeed > Classes.Flanker.Speed,
              "a hooked fighter cannot simply walk out of it");
        Check(Match.GrappleHaulVehicle > 0f, "and a hull can be dragged too");

        var roster = new List<LobbySlot>();
        for (int i = 0; i < 2; i++)
            roster.Add(new LobbySlot { IsBot = true, ClassIndex = i, FactionIndex = i });

        var m = new Match();
        m.Build(app, new MatchSettings
        {
            Mode = GameMode.Deathmatch, ScoreLimit = 50, BotSkill = 1, ArenaIndex = 0,
        }, roster, visuals: false);

        m.Paused = true;

        // High above everything, in open air.
        //
        // Not a clear lane in the arena, which is what this tried first and why it failed: every
        // match in the suite shares one physics world, so a lane that is clear of *this* arena's
        // geometry can still have another scenario's twelve fighters standing in it. The rope hit
        // one of them, hauled it perfectly correctly, and the assertions here reported that the
        // target had not moved — which was true, and about the wrong pawn.
        var at = new Vector3(0f, 260f, 0f);

        var shooter = m.Pawns[0];
        var target = m.Pawns[1];

        shooter.Respawn(at);
        shooter.ClearSpawnProtectionForTest();
        shooter.ResetLoadout();
        shooter.TakeWeapon(Weapons.Grapple);
        shooter.Tick(1f / 60f, new PawnInput { Aim = MathU.FromAngle(0f) }, m);

        target.Respawn(at + new Vector3(14f, 0f, 0f));
        target.ClearSpawnProtectionForTest();
        target.Tick(1f / 60f, new PawnInput { Aim = MathU.FromAngle(MathF.PI) }, m);

        Check(shooter.Weapon == Weapons.Grapple, "holding the rope");

        grappleMatch = m;
        NextFrame(FinishGrappleHaul);
    }

    static Match? grappleMatch;

    /// <summary>
    /// Fire the rope a frame later, once the physics server can see the two posed bodies.
    ///
    /// The same straddle the saber block needs, and for the same reason: a body moved in script is
    /// invisible to a ray query until physics has stepped, so hooking one on the frame it was posed
    /// hits the scenery behind it and looks exactly like the feature not working.
    /// </summary>
    static void FinishGrappleHaul()
    {
        var m = grappleMatch!;
        var shooter = m.Pawns[0];
        var target = m.Pawns[1];

        float gapBefore = shooter.GlobalPosition.DistanceTo(target.GlobalPosition);
        Vector3 shooterAt = shooter.GlobalPosition;
        int haulsBefore = m.GrappleHauls;

        m.FireWeapon(shooter);
        for (int i = 0; i < 30; i++) m.StepShotsForTest(1f / 60f);

        Check(m.GrappleHauls > haulsBefore,
              $"a rope in a person hauls them ({m.GrappleHauls - haulsBefore})");

        // Asserted on the target rather than on the counter. The counter goes up for a rope in
        // anything alive, including somebody else's pawn in the shared world.
        Check(target.Knockback.LengthSquared() > 1f,
              $"and it is this one that is on its way ({target.Knockback.Length():0.0} m/s)");

        // Towards the shooter, not away — and the shooter has not gone to them, which is what the
        // old behaviour would have done.
        Vector3 toShooter = (shooterAt - target.GlobalPosition).Normalized();
        Check(target.Knockback.Normalized().Dot(toShooter) > 0.5f, "in the right direction");
        Check(shooter.GlobalPosition.DistanceTo(shooterAt) < 1f,
              "and the shooter stays where they were");
        Check(shooter.GrappleAnchor == null, "the rope did not haul the shooter instead");

        var idle = new PawnInput { Aim = MathU.FromAngle(MathF.PI) };
        for (int i = 0; i < 40; i++) target.Tick(1f / 60f, idle, m);

        float gapAfter = shooter.GlobalPosition.DistanceTo(target.GlobalPosition);
        TestLog.Line($"    grapple hauled a fighter from {gapBefore:0.0}m to {gapAfter:0.0}m");

        Check(gapAfter < gapBefore - 2f, $"and they end up closer ({gapAfter:0.0}m)");

        foreach (var p in m.Pawns) p.GlobalPosition = new Vector3(0f, 800f, 0f);
        m.QueueFree();
        grappleMatch = null;
    }


    /// <summary>
    /// The two abilities whose cards used to be lying.
    ///
    /// The Apologist's said it walked behind a wall of scripture and it had a shove. The
    /// Tragedian's said it hit harder the closer it was to dying and it had a speed buff. Both were
    /// flagged as owed at the time, which is a fine thing to do once and a bad thing to leave.
    ///
    /// Posed rather than played, and each one checked for the property that makes it worth a slot
    /// rather than for the property that makes it exist. A barrier that blocks everything is a
    /// wall; a barrier that blocks *their* fire and passes yours is a decision. So the friendly
    /// round is the assertion that matters, and it is the one a naive implementation fails.
    /// </summary>
    static void CheckReinforcementAbilities()
    {
        Check(SpecialClasses.Apologist.Special == SpecialKind.Barrier,
              "the Apologist plants a barrier rather than shoving people");
        Check(SpecialClasses.Tragedian.Special == SpecialKind.LastStand,
              "the Tragedian has a last stand rather than a speed buff");
        Check(SpecialClasses.Apologist.SpecialDuration > 5f,
              "a wall stands long enough to be worth planting");

        // Cards that claim things the code does not do.
        //
        // Three of these shipped: the Apologist walked behind a wall it did not have, the Tragedian
        // hit harder at low health except it did not, and the Sinew could not sprint except that it
        // could. Every one was a sentence somebody wrote and nothing ever checked, and a card that
        // lies about what you just spent five hundred points on is worse than a card that says
        // nothing. So the specific claims are asserted against the specific flags.
        foreach (var r in Reinforcements.All)
        {
            string says = r.Blurb.ToLowerInvariant();

            if (says.Contains("cannot sprint"))
                Check(!r.Class.CanSprint, $"{r.Name} really cannot sprint");

            if (says.Contains("mends your own side"))
                Check(r.Class.HealsFriendlies, $"{r.Name} really mends its own side");

            if (says.Contains("cannot be killed"))
                Check(r.Class.Special == SpecialKind.LastStand && r.Class.SpecialDuration > 0f,
                      $"{r.Name} really cannot be killed while its ability runs");

            if (says.Contains("wall their fire cannot cross"))
                Check(r.Class.Special == SpecialKind.Barrier, $"{r.Name} really plants a wall");

            // The other direction too, so an ability cannot be quietly removed from a character
            // whose card still promises it.
            if (r.Class.CanSprint == false)
                Check(says.Contains("cannot sprint"), $"{r.Name} says it cannot sprint");

            if (r.Class.HealsFriendlies)
                Check(says.Contains("mends your own side"), $"{r.Name} says it heals");
        }

        var roster = new List<LobbySlot>();
        for (int i = 0; i < 2; i++)
            roster.Add(new LobbySlot { IsBot = true, ClassIndex = i, FactionIndex = i });

        var m = new Match();
        m.Build(app, new MatchSettings
        {
            Mode = GameMode.TeamDeathmatch, ScoreLimit = 50, BotSkill = 1, ArenaIndex = 0,
        }, roster, visuals: false);

        m.Paused = true;

        var owner = m.Pawns[0];
        var enemy = m.Pawns[1];

        Check(!Match.SameTeam(owner, enemy), "the probes are on opposite sides");

        // ---- the Tragedian ----
        owner.Respawn(m.Arena.SpawnPoints[0]);
        owner.ClearSpawnProtectionForTest();
        owner.BecomeClass(SpecialClasses.Tragedian);
        owner.Respawn(m.Arena.SpawnPoints[0]);
        owner.ClearSpawnProtectionForTest();

        Check(!owner.LastStand, "the last stand starts off");
        Check(MathF.Abs(owner.DesperationScale - 1f) < 0.001f, "and changes nothing while it is");

        owner.StartClassAbilityForTest();
        m.UseClassAbility(owner);

        Check(owner.LastStand, "the ability turns it on");

        // Hitting harder as it goes down, measured across the curve rather than at one point — a
        // threshold would make the interesting part of the character one hit point wide.
        owner.SetHealthForTest(owner.MaxHealth * 0.75f);
        float atThreeQuarters = owner.DesperationScale;

        owner.SetHealthForTest(owner.MaxHealth * 0.25f);
        float atAQuarter = owner.DesperationScale;

        TestLog.Line($"    the Tragedian hits {atThreeQuarters:0.00}x at three-quarters health, "
                 + $"{atAQuarter:0.00}x at a quarter");

        Check(atThreeQuarters > 1f, "it hits harder once hurt");
        Check(atAQuarter > atThreeQuarters, "and harder still the further it goes");
        Check(atAQuarter < 3f, "without becoming a one-shot");

        // Cannot be killed while it runs, only worn down.
        bool died = owner.TakeDamage(owner.MaxHealth * 10f);

        Check(!died && owner.Alive, "and it cannot be killed while the act is running");
        Check(owner.Health <= 1.01f, $"only worn down to a sliver ({owner.Health:0.0})");

        // And the moment it ends, it is an ordinary fighter on one hit point.
        var idle = new PawnInput { Aim = MathU.FromAngle(0f) };
        for (int i = 0; i < (int)(SpecialClasses.Tragedian.SpecialDuration * 60f) + 30; i++)
            owner.Tick(1f / 60f, idle, m);

        Check(!owner.LastStand, "the act ends");
        Check(MathF.Abs(owner.DesperationScale - 1f) < 0.001f, "and the bite goes with it");

        owner.ClearSpawnProtectionForTest();
        Check(owner.TakeDamage(50f), "and then anything at all finishes it");

        // ---- the Apologist ----
        owner.Respawn(m.Arena.SpawnPoints[0]);
        owner.ClearSpawnProtectionForTest();
        owner.BecomeClass(SpecialClasses.Apologist);
        owner.Respawn(m.Arena.SpawnPoints[0]);
        owner.ClearSpawnProtectionForTest();

        Check(m.BarrierCount == 0, "no wall to begin with");

        // Posed in clear air so the only thing between the two of them is the barrier. The lane is
        // found rather than assumed for the same reason the saber block's is.
        var lane = ClearLane(m.Arena, 12f);
        Check(lane.HasValue, "the harness has somewhere to plant one");

        if (lane is not { } at) { m.QueueFree(); return; }

        owner.Respawn(at);
        owner.ClearSpawnProtectionForTest();
        owner.Tick(1f / 60f, new PawnInput { Aim = MathU.FromAngle(0f) }, m);

        owner.StartClassAbilityForTest();
        m.UseClassAbility(owner);

        Check(m.BarrierCount == 1, "the ability plants a wall");

        // Planting again replaces rather than stacks. A player who can stack them turns a doorway
        // into a bunker.
        owner.StartClassAbilityForTest();
        m.UseClassAbility(owner);

        Check(m.BarrierCount == 1, "and a second one replaces the first rather than stacking");

        // ---- their fire stops, yours does not ----
        //
        // Injected at a known point and heading rather than fired, because what is under test is
        // the wall, and firing drags in the whole ray sweep and everything else in the shared
        // physics world that happens to be standing behind it.
        Vector3 wallAt = at + new Vector3(2.6f, 1.2f, 0f);
        Vector3 fromFar = wallAt + new Vector3(8f, 0f, 0f);

        int blockedBefore = m.BarrierBlocks;

        m.ClearShotsForTest();
        m.InjectShotForTest(enemy, fromFar, new Vector3(-40f, 0f, 0f));

        for (int i = 0; i < 30; i++) m.StepShotsForTest(1f / 60f);

        Check(m.BarrierBlocks > blockedBefore,
              $"an enemy round is stopped by it ({m.BarrierBlocks - blockedBefore})");
        Check(m.ShotsInFlight == 0, "and does not carry on through");

        // The assertion the whole ability exists for.
        blockedBefore = m.BarrierBlocks;

        m.ClearShotsForTest();
        m.InjectShotForTest(owner, fromFar, new Vector3(-40f, 0f, 0f));

        // Long enough to actually reach the wall. At forty metres a second the round needs a fifth
        // of a second to cross the eight metres to it — the first version of this stepped four
        // frames, carried it two and a half metres, and then asserted it had come out the far side.
        // The check failed on correct code, which is the most expensive kind of test failure there
        // is: it looks exactly like the feature not working.
        for (int i = 0; i < 14; i++) m.StepShotsForTest(1f / 60f);

        Check(m.BarrierBlocks == blockedBefore, "your own fire is not stopped by your own wall");
        Check(m.ShotsInFlight > 0, "the round is still going");
        Check(m.ShotsInFlight > 0 && m.ShotPositionForTest(0).X < wallAt.X,
              "and it is out the other side of the wall");

        m.ClearShotsForTest();

        // ---- and it does not stand for ever ----
        for (int i = 0; i < (int)(SpecialClasses.Apologist.SpecialDuration * 60f) + 60; i++)
            m.StepBarriersForTest(1f / 60f);

        Check(m.BarrierCount == 0,
              $"the wall comes down on its own after {SpecialClasses.Apologist.SpecialDuration:0}s");

        foreach (var p in m.Pawns) p.GlobalPosition = new Vector3(0f, 800f, 0f);
        m.QueueFree();
    }

    /// <summary>
    /// Conquest: posts change hands, the side holding fewer bleeds, and you come back where your
    /// own side is standing.
    ///
    /// The three rules are tested separately because they fail separately, and two of them fail
    /// silently. A capture that never completes looks like a slow capture. A bleed that never
    /// starts looks like a long match. Only the third — spawning at a post — is visible from the
    /// inside, and only once you have lost every post and find yourself walking back from a corner.
    ///
    /// Ticked directly rather than played, so each rule is exercised with everything else held
    /// still. The live scenario alongside this proves bots do something sensible with them.
    /// </summary>

    /// <summary>
    /// Portal mode: one gun for everybody, and two games that share it.
    ///
    /// The elimination half is the strange one and worth stating plainly, because it looks broken
    /// from the outside: nobody can damage anybody. The portal gun does no damage at all, so every
    /// kill in that mode comes from the arena — a gate over a pit, a gate over the lava, a gate at
    /// the edge of the world. It is the only mode here where the map is the weapon, and the portal
    /// kill credit written earlier is what makes it score at all.
    ///
    /// The puzzle half runs on chambers with no floor between the islands, which every connectivity
    /// rule elsewhere in this project exists to prevent. That is why they are built by a separate
    /// path and why this checks they really are disconnected — a puzzle map you can walk across is
    /// not a puzzle map.
    /// </summary>
    static void CheckPortalMode()
    {
        var def = Modes.Get(GameMode.Portal);

        Check(!def.Teams, "portal mode is a free-for-all");
        Check(Weapons.PortalGun.IsUtility, "and its only weapon cannot hurt anybody");

        // ---- the chambers are chambers ----
        int combat = Arena.Names.Length - Arena.PuzzleLayouts;

        Check(Arena.PuzzleLayouts >= 2, "there is more than one chamber to play");

        for (int layout = 0; layout < Arena.Names.Length; layout++)
        {
            bool puzzle = Arena.IsPuzzle(layout);
            Check(puzzle == (layout >= combat), $"{Arena.Names[layout]} is sorted correctly");
        }

        for (int layout = combat; layout < Arena.Names.Length; layout++)
        {
            var a = new Arena(layout);

            TestLog.Line($"    {a.Name}: {a.Checkpoints.Count} checkpoints, "
                     + $"{a.SpawnPoints.Count} spawns, {a.Blocks.Count} blocks");

            Check(a.Puzzle, $"{a.Name} knows it is a chamber");
            Check(a.Checkpoints.Count >= 3, $"{a.Name} has checkpoints to reach ({a.Checkpoints.Count})");
            Check(a.SpawnPoints.Count >= 2, $"{a.Name} can start two players");

            // No vehicles, no crates, no launch pads. All of those belong to a fight.
            Check(a.VehicleSpawns.Count == 0, $"{a.Name} has no vehicles in it");
            Check(a.LaunchPads.Count == 0, $"{a.Name} has no launch pads");

            // The one that matters: the islands are genuinely separate. Every checkpoint has to be
            // somewhere you cannot simply walk to, or the portal gun is decoration.
            int stranded = 0;
            foreach (var c in a.Checkpoints)
                if (!a.IsConnected(c)) stranded++;

            TestLog.Line($"    {a.Name}: {stranded} of {a.Checkpoints.Count} checkpoints unwalkable");

            Check(stranded > 0,
                  $"{a.Name} has checkpoints you cannot walk to ({stranded})");

            // And the spawns are on solid ground, which is the one thing that must be true.
            foreach (var sp in a.SpawnPoints)
                Check(a.InPlay(sp), $"{a.Name} spawn {sp} is inside the world");
        }

        // ---- the map picker cannot hand a mode the wrong kind of map ----
        //
        // The arena list holds combat arenas and puzzle chambers in one array, so every path that
        // picks a map — the menu, the random roll, an explicit choice carried over from a previous
        // mode — can in principle produce a deathmatch on a map with no floor, or a puzzle on a map
        // with no checkpoints. The match refuses such a pairing, and this proves it refuses rather
        // than assuming it.
        foreach (var mode in new[] { GameMode.Deathmatch, GameMode.Dominion, GameMode.Portal })
        foreach (var variant in new[] { PortalVariant.Elimination, PortalVariant.Puzzle })
        {
            // Deliberately asking for the wrong kind, in both directions.
            for (int wrong = 0; wrong < Arena.Names.Length; wrong++)
            {
                var ask = new MatchSettings { Mode = mode, Portal = variant, ArenaIndex = wrong };
                var got = new Arena(Match.ChooseArenaForTest(ask));

                Check(got.Puzzle == ask.IsPuzzle,
                      $"{mode}/{variant} asking for {Arena.Names[wrong]} lands on the right kind "
                      + $"({got.Name})");
            }

            // And a random roll, repeatedly, because a roll that is right most of the time is a
            // roll that ruins one match in ten.
            for (int roll = 0; roll < 30; roll++)
            {
                var ask = new MatchSettings { Mode = mode, Portal = variant, ArenaIndex = -1 };
                var got = new Arena(Match.ChooseArenaForTest(ask));

                Check(got.Puzzle == ask.IsPuzzle,
                      $"{mode}/{variant} rolls a map of the right kind ({got.Name})");
            }
        }

        // ---- a match, with the loadout forced ----
        var roster = new List<LobbySlot>();
        for (int i = 0; i < 2; i++)
            roster.Add(new LobbySlot { IsBot = true, ClassIndex = i, FactionIndex = i });

        var m = new Match();
        m.Build(app, new MatchSettings
        {
            Mode = GameMode.Portal, Portal = PortalVariant.Puzzle,
            ScoreLimit = 5, BotSkill = 1, ArenaIndex = -1,
        }, roster, visuals: false);

        m.Paused = true;

        Check(m.Arena.Puzzle, $"a puzzle match lands on a chamber ({m.Arena.Name})");
        Check(m.CheckpointCount > 0, $"with checkpoints ({m.CheckpointCount})");

        foreach (var p in m.Pawns)
        {
            Check(p.PortalOnly, $"{p.Name2} is carrying the portal gun");
            Check(p.Weapon == Weapons.PortalGun, $"{p.Name2} has nothing else");
            Check(p.Weapon.Damage <= 0f, $"{p.Name2} cannot shoot anybody");
        }

        // ---- reaching them finishes it ----
        var runner = m.Pawns[0];

        Check(m.CheckpointsReached == 0, "nothing is claimed to begin with");

        for (int i = 0; i < m.CheckpointCount; i++)
        {
            runner.Respawn(m.Arena.Checkpoints[i] + Vector3.Up * 0.5f);
            runner.ClearSpawnProtectionForTest();
            m.TickCheckpointsForTest(1f / 60f);

            Check(m.CheckpointDone(i), $"standing on checkpoint {i} claims it");
        }

        Check(m.CheckpointsReached == m.CheckpointCount, "all of them claimed");

        m.CheckWinForTest();
        Check(m.Finished, "and the chamber is finished");

        // Claiming is permanent — a chamber you have to hold rather than solve would be a
        // completely different mode, and a co-operative one at that.
        int was = m.CheckpointsReached;
        foreach (var p in m.Pawns) p.GlobalPosition = new Vector3(0f, 700f, 0f);
        m.TickCheckpointsForTest(1f / 60f);

        Check(m.CheckpointsReached == was, "and stays finished once nobody is standing there");

        Check(new MatchSettings { Mode = GameMode.Portal, Portal = PortalVariant.Puzzle }
                  .MinimumFighters >= 2,
              "the puzzle half needs two players");

        foreach (var p in m.Pawns) p.GlobalPosition = new Vector3(0f, 800f, 0f);
        m.QueueFree();
    }

   static void CheckDominion()
    {
        var def = Modes.Get(GameMode.Dominion);

        Check(def.Teams, "dominion is a team mode");
        Check(def.DefaultLimit >= 50, "and plays to a real reinforcement pool");

        var roster = new List<LobbySlot>();
        for (int i = 0; i < 4; i++)
            roster.Add(new LobbySlot { IsBot = true, ClassIndex = i, FactionIndex = i });

        var m = new Match();
        m.Build(app, new MatchSettings
        {
            Mode = GameMode.Dominion, ScoreLimit = 60, BotSkill = 1, ArenaIndex = 0,
        }, roster, visuals: false);

        TestLog.Line($"    dominion: {m.Posts.Count} command posts on {m.Arena.Name}");

        Check(m.Posts.Count == Match.PostTarget,
              $"the map carries a front line rather than a scatter ({m.Posts.Count} posts)");
        Check(m.Tickets[0] == 60f && m.Tickets[1] == 60f,
              "both sides start with the same pool");
        Check(m.PostsHeld(0) == 1 && m.PostsHeld(1) == 1,
              "and one post each, so neither opens with nowhere to spawn");

        // Every post has to be somewhere a fighter can actually stand, or the mode has a circle in
        // it that nobody can ever take.
        var seen = new HashSet<string>();
        foreach (var post in m.Posts)
        {
            Check(m.Arena.Contains(post.Centre), $"post {post.Name} is in bounds");
            Check(!m.Arena.IsOverPit(post.Centre), $"post {post.Name} is not over a pit");
            Check(seen.Add(post.Name), $"post {post.Name} is named once");
        }

        // Park everyone somewhere they cannot interfere. Pawns left standing at their spawns sit
        // inside whatever post happens to be nearest and quietly capture things mid-test.
        foreach (var p in m.Pawns) p.GlobalPosition = new Vector3(0f, 700f, 0f);

        // ---- taking a post ----
        var contested = m.Posts[m.Posts.Count / 2];
        Check(contested.Owner < 0, $"{contested.Name} starts neutral");

        var blue = m.Pawns[0];      // slot 0 → team 0
        var orange = m.Pawns[1];    // slot 1 → team 1

        Check(Match.TeamOf(blue.Slot) == 0 && Match.TeamOf(orange.Slot) == 1,
              "the two probes are on opposite sides");

        blue.GlobalPosition = contested.Centre;

        // Half the capture time, so this proves progress accrues rather than snapping.
        for (int i = 0; i < 60 * 4; i++) m.TickDominionForTest(1f / 60f);

        Check(contested.Owner < 0, "a post is not taken instantly");
        Check(contested.Progress > 0.2f, $"but it is being taken ({contested.Progress * 100f:0}%)");
        Check(contested.Contender == 0, "by the side actually standing in it");

        // ---- contesting freezes it ----
        orange.GlobalPosition = contested.Centre;
        float frozen = contested.Progress;

        for (int i = 0; i < 60 * 3; i++) m.TickDominionForTest(1f / 60f);

        Check(contested.Contested, "two sides in a post contest it");
        Check(MathF.Abs(contested.Progress - frozen) < 0.01f,
              $"and the capture stops dead while they do ({contested.Progress * 100f:0}%)");
        Check(contested.Owner < 0, "so nobody takes it");

        // ---- winning the fight finishes it ----
        orange.GlobalPosition = new Vector3(0f, 700f, 0f);

        for (int i = 0; i < 60 * 10; i++) m.TickDominionForTest(1f / 60f);

        Check(contested.Owner == 0, $"clearing the post finishes the capture ({contested.Name})");
        Check(m.PostCaptures > 0, "and it is counted");
        Check(m.PostsHeld(0) == 2, "the taking side is now two posts up on the board");

        // ---- the bleed ----
        //
        // Blue holds two, orange holds one. Orange should be losing reinforcements simply by
        // standing still, which is the entire reason this mode is not King of the Hill.
        float before = m.Tickets[1];
        float blueBefore = m.Tickets[0];

        blue.GlobalPosition = new Vector3(0f, 700f, 0f);
        for (int i = 0; i < 60 * 10; i++) m.TickDominionForTest(1f / 60f);

        float lost = before - m.Tickets[1];
        TestLog.Line($"    dominion: one post down cost orange {lost:0.0} reinforcements in 10s");

        Check(lost > 3f, $"the side holding fewer posts bleeds ({lost:0.0} in ten seconds)");
        Check(m.Tickets[0] == blueBefore, "and the side holding more does not");

        // The same post the other way round, which should now bleed the other side. Without this
        // the check above would pass just as happily on code that always drained team one.
        m.ForcePostOwnerForTest(contested, 1);
        float flipBlue = m.Tickets[0];
        float flipOrange = m.Tickets[1];

        for (int i = 0; i < 60 * 10; i++) m.TickDominionForTest(1f / 60f);

        Check(flipBlue - m.Tickets[0] > 3f,
              $"and it runs the other way just as well ({flipBlue - m.Tickets[0]:0.0} off blue)");
        Check(m.Tickets[1] == flipOrange, "with the side ahead untouched");

        // Held level, nobody bleeds. Without this both checks above would pass on code that simply
        // drained everybody all the time.
        m.ForcePostOwnerForTest(contested, -1);
        float evenBlue = m.Tickets[0];
        float evenOrange = m.Tickets[1];

        Check(m.PostsHeld(0) == m.PostsHeld(1), "the map is level");

        for (int i = 0; i < 60 * 5; i++) m.TickDominionForTest(1f / 60f);

        Check(m.Tickets[0] == evenBlue && m.Tickets[1] == evenOrange,
              "an even map bleeds nobody");

        // ---- you come back where you asked to ----
        //
        // The whole point of taking a post. Driven through the real respawn so the choice has to
        // survive the spawn timer, the class swap and the automatic picker that used to override
        // everything — and asserted against *every* post the side holds rather than one, because a
        // picker that ignores the request and happens to agree with it once proves nothing.
        var chooser = m.Pawns[0];
        int myTeam = Match.TeamOf(chooser.Slot);

        for (int i = 0; i < m.Posts.Count; i++) m.ForcePostOwnerForTest(m.Posts[i], myTeam);

        for (int i = 0; i < m.Posts.Count; i++)
        {
            Down(chooser);
            chooser.NextSpawn = Reinforcements.Trooper;
            chooser.SpawnPost = i;
            chooser.SpawnConfirmed = true;

            for (int step = 0; step < 300 && !chooser.Alive; step++) m._PhysicsProcess(1.0 / 60.0);

            float off = chooser.GlobalPosition.DistanceTo(m.Posts[i].Centre);
            Check(off < Match.PostRadius,
                  $"asking for {m.Posts[i].Name} puts you at {m.Posts[i].Name} ({off:0.0}m off)");
        }

        // A post lost between choosing and spawning must not strand you. This is not an edge case
        // in a mode about posts changing hands.
        m.ForcePostOwnerForTest(m.Posts[0], 1 - myTeam);

        Down(chooser);
        chooser.NextSpawn = Reinforcements.Trooper;
        chooser.SpawnPost = 0;
        chooser.SpawnConfirmed = true;

        for (int step = 0; step < 300 && !chooser.Alive; step++) m._PhysicsProcess(1.0 / 60.0);

        Check(chooser.Alive, "losing the post you chose still brings you back");
        Check(chooser.GlobalPosition.DistanceTo(m.Posts[0].Centre) > Match.PostRadius,
              "and not into the middle of the people who took it");

        // And with nothing held at all, the ordinary spawns take over rather than the mode refusing
        // to spawn anybody.
        foreach (var post in m.Posts) m.ForcePostOwnerForTest(post, 1 - myTeam);

        Down(chooser);
        chooser.NextSpawn = Reinforcements.Trooper;
        chooser.SpawnPost = 2;
        chooser.SpawnConfirmed = true;

        for (int step = 0; step < 300 && !chooser.Alive; step++) m._PhysicsProcess(1.0 / 60.0);

        Check(chooser.Alive, "a side that holds nothing still comes back");
        Check(m.Arena.Contains(chooser.GlobalPosition), "somewhere inside the arena");

        // Put the map back before the steering check reads it.
        for (int i = 0; i < m.Posts.Count; i++)
            m.ForcePostOwnerForTest(m.Posts[i], i == 0 ? 0 : i == m.Posts.Count - 1 ? 1 : -1);

        // ---- bots are actually steered at the posts ----
        //
        // Asked of the objective directly rather than waited for in a live match. Whether a capture
        // completes inside a test window depends on whether the bots got shot on the way, which is
        // a coin toss; where they are being *sent* is a decision the code makes every frame and can
        // be asked for an answer.
        foreach (var p in m.Pawns)
        {
            p.Respawn(m.Arena.SpawnPoints[p.Slot]);
            p.ClearSpawnProtectionForTest();
        }

        int steered = 0;
        foreach (var p in m.Pawns)
        {
            if (m.ObjectiveFor(p) is not { } want) continue;

            bool isAPost = false;
            foreach (var post in m.Posts)
                if (post.Centre.DistanceTo(want) < 0.01f) { isAPost = true; break; }

            Check(isAPost, $"{p.Name2} is sent to a command post, not somewhere else");
            if (isAPost) steered++;
        }

        TestLog.Line($"    dominion: {steered} of {m.Pawns.Count} fighters steered at a post");
        Check(steered == m.Pawns.Count, $"every fighter has a post to go to ({steered})");

        // And not all at the same one, or the mode is King of the Hill with extra circles.
        var targets = new HashSet<string>();
        foreach (var p in m.Pawns)
            if (m.ObjectiveFor(p) is { } want) targets.Add($"{want.X:0}/{want.Z:0}");

        Check(targets.Count > 1, $"and not all at the same one ({targets.Count} distinct)");

        Check(Match.ObjectiveOutranksFightingFor(GameMode.Dominion),
              "and the objective outranks standing and trading shots");

        // ---- a death costs a body ----
        float pool = m.Tickets[0];
        m.AwardKillForTest(orange, blue);

        Check(m.Tickets[0] == pool - 1f,
              $"a death spends one of your side's reinforcements ({m.Tickets[0]:0})");

        // ---- running out loses it ----
        m.SetTicketsForTest(1, 0f);
        m.TickDominionForTest(1f / 60f);
        m.CheckWinForTest();

        Check(m.Finished, "an empty pool ends the match");
        Check(m.Winner != null && Match.TeamOf(m.Winner.Slot) == 0,
              "and the other side wins it");

        foreach (var p in m.Pawns) p.GlobalPosition = new Vector3(0f, 800f, 0f);
        m.QueueFree();
    }

    /// <summary>
    /// Juggernaut: the crown, the four figures, and the scoring rule that makes it a mode.
    ///
    /// Posed a step at a time rather than waited for. Every rule here is a rule about state — who
    /// wears it, what happens when they die, who gets it when nobody killed them — and waiting for
    /// twelve bots to produce each of those inside a test window would be a coin flip wearing a
    /// coverage badge.
    /// </summary>
    static void CheckJuggernaut()
    {
        var def = Modes.Get(GameMode.Juggernaut);

        Check(!def.Teams, "juggernaut is a free-for-all");
        Check(def.LimitStep == 1, "crown kills step one at a time");

        // Four factions, four figures, four different games. Reskins would make the crown changing
        // hands mean nothing, which is the whole point of the mode.
        var kinds = new HashSet<JuggernautKind>();
        foreach (var f in Factions.All)
        {
            var j = f.Juggernaut;
            TestLog.Line($"    {f.Name}: {j.Name} — {j.Epithet}");

            Check(kinds.Add(j.Kind), $"{f.Name} fields a juggernaut nobody else does");
            Check(j.Name.Length > 0 && j.Blurb.Length > 0, $"{j.Name} is named and explained");
            Check(j.HealthScale > 1.5f, $"{j.Name} is genuinely tougher than a fighter");
        }

        // They must actually differ in the numbers, or "four different games" is a claim about
        // flavour text.
        var health = new HashSet<float>();
        var speed = new HashSet<float>();
        foreach (var j in Juggernauts.All) { health.Add(j.HealthScale); speed.Add(j.SpeedScale); }

        Check(health.Count >= 3, "the four are not the same size as each other");
        Check(speed.Count >= 3, "and not the same speed");
        Check(Juggernauts.Scheherazade.HealthScale < Juggernauts.Achilles.HealthScale * 0.6f,
              "the glass cannon really is fragile next to the tank");

        var roster = new List<LobbySlot>();
        for (int i = 0; i < 4; i++)
            roster.Add(new LobbySlot { IsBot = true, ClassIndex = i, FactionIndex = i });

        var m = new Match();
        m.Build(app, new MatchSettings
        {
            Mode = GameMode.Juggernaut, ScoreLimit = 20, BotSkill = 1, ArenaIndex = 0,
        }, roster, visuals: false);

        var a = m.Pawns[0];   // Vessels  → Achilles
        var b = m.Pawns[1];   // Custodians → Prometheus
        var c = m.Pawns[3];   // Ingenuity → Scheherazade

        foreach (var p in m.Pawns) p.Respawn(m.Arena.SpawnPoints[p.Slot % m.Arena.SpawnPoints.Count]);
        foreach (var p in m.Pawns) p.ClearSpawnProtectionForTest();

        Check(m.Juggernaut == null, "nobody wears the crown before first blood");

        // ---- first blood takes it ----
        float plainHealth = a.MaxHealth;
        m.AwardKillForTest(a, b);

        Check(m.Juggernaut == a, "first blood takes the crown");
        Check(a.IsJuggernaut && a.Crown!.Kind == JuggernautKind.Achilles,
              "and you wear your own faction's figure");
        Check(a.MaxHealth > plainHealth * 2f, "the crown is worth real health");
        Check(a.Health == a.MaxHealth, "handed over at full strength");
        Check(m.ScoreOf(a) == 0, "the promotion itself is not a point");

        // ---- only the juggernaut's kills score ----
        b.Respawn(m.Arena.SpawnPoints[1]);
        m.AwardKillForTest(b, c);
        Check(m.ScoreOf(b) == 0, "an ordinary fighter killing another scores nothing");

        c.Respawn(m.Arena.SpawnPoints[2]);
        m.AwardKillForTest(a, c);
        Check(m.ScoreOf(a) == 1, "a kill by the juggernaut scores");
        Check(a.CrownKills == 1, "and is counted as a crown kill");

        // ---- killing the juggernaut takes it, as your own figure ----
        c.Respawn(m.Arena.SpawnPoints[2]);
        m.AwardKillForTest(c, a);

        Check(m.Juggernaut == c, "killing the juggernaut promotes the killer");
        Check(!a.IsJuggernaut, "and demotes the old one");
        Check(c.Crown!.Kind == JuggernautKind.Scheherazade,
              "the new juggernaut is the killer's faction, not the old one's");
        Check(a.Health <= a.MaxHealth, "a demoted juggernaut is not left over its own ceiling");
        Check(m.ScoreOf(a) == 1, "and keeps what it scored");

        // ---- Scheherazade's clock ----
        Check(c.NightsLeft > 0f, "Scheherazade starts with time on the clock");

        float before = c.NightsLeft;
        c.SpendNight(3f);
        Check(c.NightsLeft < before, "which runs down");

        c.BuyAnotherNight();
        Check(c.NightsLeft > before - 3f, "and a kill buys another night");

        Check(a.NightsLeft == 0f, "nobody else has a clock at all");

        // Running out hands the crown on rather than simply killing her.
        c.SpendNight(999f);
        Check(c.NightsLeft == 0f, "the clock can run out");

        foreach (var p in m.Pawns) if (!p.Alive) p.Respawn(m.Arena.SpawnPoints[p.Slot]);
        a.GlobalPosition = c.GlobalPosition + new Vector3(4f, 0f, 0f);

        int changes = m.CrownChanges;
        m.TickJuggernautForTest(1f / 60f);

        Check(m.CrownChanges > changes, "running out of story passes the crown on");
        Check(m.Juggernaut != c, "she is no longer wearing it");

        // ---- Achilles' heel ----
        m.ForceCrownForTest(a);
        Check(a.Crown!.Kind == JuggernautKind.Achilles, "Achilles is wearing it again");

        float heel = m.JuggernautHitScale(a, a.CurrentHeight * 0.1f);
        float chest = m.JuggernautHitScale(a, a.CurrentHeight * 0.5f);
        float head = m.JuggernautHitScale(a, a.CurrentHeight * 0.9f);

        Check(heel > chest, $"the heel takes more than the body ({heel:0.0}x vs {chest:0.0}x)");
        Check(chest == 1f && head == 1f, "and everywhere else is ordinary");

        // Nobody else has one. The heel is the reason Achilles is beatable, not a general rule.
        m.ForceCrownForTest(b);
        Check(m.JuggernautHitScale(b, b.CurrentHeight * 0.1f) == 1f,
              "no other juggernaut has a weak point");

        // ---- Prometheus lights everyone up ----
        foreach (var p in m.Pawns) { p.Respawn(m.Arena.SpawnPoints[p.Slot]); p.RevealedFor = 0f; }
        m.ForceCrownForTest(b);
        m.TickJuggernautForTest(1f / 60f);

        int lit = 0;
        foreach (var p in m.Pawns) if (p.RevealedFor > 0f) lit++;

        Check(lit == m.Pawns.Count, $"while Prometheus reigns nobody hides ({lit} of {m.Pawns.Count})");

        // ---- the powers ----
        //
        // Reported as "it just feels like you get a lot of health when you become the juggernaut".
        // That was fair: the crown was a stat block and a passive, and a stat block does not change
        // how anyone plays. Each of these has to be plainly, measurably enormous.
        foreach (var j in Juggernauts.All)
        {
            Check(j.PowerName.Length > 0 && j.PowerBlurb.Length > 0,
                  $"{j.Name} names and explains its power");
            Check(j.PowerCooldown > 5f, $"{j.Name}'s power has a real cooldown");
        }

        Check(Match.WrathDamage > Weapons.RocketLauncher.BlastDamage,
              "Wrath hits harder than the heaviest weapon on the floor");
        Check(Match.WrathRadius > Weapons.RocketLauncher.BlastRadius * 2f,
              "and reaches a great deal further");

        // ---- Wrath: everyone near him is hurt and thrown ----
        foreach (var p in m.Pawns) { p.Respawn(m.Arena.SpawnPoints[p.Slot]); p.ClearSpawnProtectionForTest(); }
        m.ForceCrownForTest(a);

        var victim = m.Pawns[2];
        victim.GlobalPosition = a.GlobalPosition + new Vector3(6f, 0f, 0f);

        float hp = victim.Health;
        int fired = m.CrownPowersUsed;

        Check(a.CrownPowerReady, "Achilles has his power ready");
        a.StartCrownPower();
        m.UseCrownPower(a);

        Check(m.CrownPowersUsed > fired, "Wrath fires");
        Check(victim.Health < hp - 40f,
              $"and badly hurts someone six metres away ({hp - victim.Health:0} damage)");
        Check(victim.Knockback.LengthSquared() > 1f, "and throws them");
        Check(a.Health == a.MaxHealth, "without hurting the juggernaut himself");
        Check(!a.CrownPowerReady, "and goes on cooldown");

        // ---- the Ark: almost nothing gets through ----
        var noah = m.Pawns[2];
        noah.Respawn(m.Arena.SpawnPoints[2]);
        noah.ClearSpawnProtectionForTest();
        m.ForceCrownForTest(noah);

        Check(noah.Crown!.Kind == JuggernautKind.Noah, "Noah has the crown");

        float bare = noah.Health;
        noah.TakeDamage(100f);
        float openLoss = bare - noah.Health;

        noah.Respawn(m.Arena.SpawnPoints[2]);
        noah.ClearSpawnProtectionForTest();
        noah.StartCrownPower();
        m.UseCrownPower(noah);
        m.TickJuggernautForTest(1f / 60f);

        float sealedStart = noah.Health;
        noah.TakeDamage(100f);
        float sealedLoss = sealedStart - noah.Health;

        TestLog.Line($"    the Ark: {openLoss:0} damage taken open, {sealedLoss:0} sealed");
        Check(sealedLoss < openLoss * 0.4f, "the Ark stops most of an incoming shot");
        Check(sealedLoss > 0f, "but not all of it — it is a shield, not immunity");

        // And it lapses rather than lasting forever.
        //
        // The pawn has to be ticked as well as the match: the duration runs down on the pawn's own
        // clock and the effect is applied from the match's, and only ticking one of them leaves a
        // shield that never expires.
        var sealedIdle = new PawnInput { Aim = MathU.FromAngle(0f) };

        for (int i = 0; i < (int)((noah.Crown.PowerDuration + 1f) * 60f); i++)
        {
            noah.Tick(1f / 60f, sealedIdle, m);
            m.TickJuggernautForTest(1f / 60f);
        }

        Check(!noah.CrownPowerActive, "the Ark runs out");
        Check(noah.DamageResist == 1f, "and the resistance goes with it");

        // ---- the Thousand: a crowd, and hard to find ----
        var muse = m.Pawns[3];
        muse.Respawn(m.Arena.SpawnPoints[3]);
        muse.ClearSpawnProtectionForTest();
        m.ForceCrownForTest(muse);

        Check(!muse.HardToFind, "Scheherazade is an ordinary target to begin with");

        int decoysBefore = m.DecoyCount;
        muse.StartCrownPower();
        m.UseCrownPower(muse);

        Check(m.DecoyCount >= decoysBefore + 4 || !m.Visuals, "the Thousand leaves a crowd behind");
        Check(muse.DamageResist < 1f, "and she is harder to hurt inside it");
        Check(muse.HardToFind, "and hard to pick out of it");

        // ---- the Fire: Prometheus rises ----
        var titan = m.Pawns[1];
        titan.Respawn(m.Arena.SpawnPoints[1]);
        titan.ClearSpawnProtectionForTest();
        m.ForceCrownForTest(titan);

        Check(!titan.Flying, "Prometheus starts on the ground");

        titan.StartCrownPower();
        m.UseCrownPower(titan);
        Check(titan.Flying, "and the Fire takes him off it");

        float floor = titan.GlobalPosition.Y;
        var idle = new PawnInput { Aim = MathU.FromAngle(0f) };
        for (int i = 0; i < 90; i++) titan.Tick(1f / 60f, idle, m);

        float risen = titan.GlobalPosition.Y - floor;
        TestLog.Line($"    the Fire lifted Prometheus {risen:0.0}m");
        Check(risen > 3f, $"he genuinely leaves the ground ({risen:0.0}m)");

        float bestClassDps = 0f;
        foreach (var cl in Classes.All) bestClassDps = MathF.Max(bestClassDps, cl.Dps);

        Check(Match.FireDamagePerSecond > bestClassDps,
              $"and the beam out-damages every class weapon ({Match.FireDamagePerSecond:0} vs {bestClassDps:0})");

        CheckTheSaber(m);

        // Parked far above the world before letting go of it. QueueFree is deferred, so this
        // match's pawns are still standing in the shared physics world for the rest of the frame —
        // on the same spawn coordinates the live scenario uses, which makes every later check that
        // asks "is anything overlapping this spawn?" answer yes.
        foreach (var p in m.Pawns) p.GlobalPosition = new Vector3(0f, 800f, 0f);

        m.QueueFree();
    }

    /// <summary>
    /// The crown takes your gun away and hands you a blade you can hide behind.
    ///
    /// Two halves, and the second is the one worth having a test for. Losing the gun is easy to
    /// assert and easy to get right. The block is a change to the projectile sweep — the one piece
    /// of code every weapon in the game runs through — and the failure mode is not "the block does
    /// not work", it is "the round vanishes", which from inside a match is indistinguishable from
    /// a block that works perfectly. So this fires a real round into a real raised blade and then
    /// goes looking for it coming back the other way.
    ///
    /// The other half of that: it also fires into the juggernaut's *back*, because a block that
    /// covers all three hundred and sixty degrees is not a block, it is invulnerability, and it
    /// would read identically to the player standing behind them.
    /// </summary>
    static void CheckTheSaber(Match m)
    {
        var hero = m.Pawns[0];
        var shooter = m.Pawns[1];

        // Every pawn in this match has worn the crown by now — the power checks above passed it
        // around all four of them. Stripping them all first is not tidying: the shooter was still
        // crowned, so it was holding a saber with five metres of reach and being asked to shoot
        // someone ten metres away. The rounds it fired expired in mid-air and the block test
        // reported that nothing had been deflected, which was quite true.
        foreach (var p in m.Pawns) p.LoseCrown();

        // ---- the crown takes the guns ----
        hero.Respawn(m.Arena.SpawnPoints[0]);
        hero.ClearSpawnProtectionForTest();
        hero.ResetLoadout();
        hero.TakeWeapon(Weapons.Railgun);

        Check(hero.Weapon == Weapons.Railgun, "an uncrowned fighter holds what they picked up");

        // Crowned through the pawn rather than the match. Match.CrownPawn short-circuits when the
        // pawn already holds the crown, so pairing it with a bare LoseCrown — which this test does
        // deliberately, to watch the gun come back — leaves the two out of step and the next
        // coronation silently does nothing. Going through the pawn keeps this test about the
        // saber rather than about the match's bookkeeping.
        hero.TakeCrown(hero.Faction.Juggernaut);

        Check(hero.HasSaber, "the crown puts a blade in your hands");
        Check(hero.Weapon == Weapons.Saber, "and it is the only thing you are carrying");
        Check(hero.Weapon.Range < Match.MeleeRange * 3f,
              $"which reaches nowhere ({hero.Weapon.Range:0.0}m)");
        Check(!hero.WouldTake(Weapons.Minigun), "a juggernaut has no use for a crate");

        foreach (var cl in Classes.All)
            Check(Weapons.Saber.Damage * Weapons.Saber.Pellets > cl.Health,
                  $"a full swing kills a {cl.Name} outright");

        // The gun comes back, with its ammo, rather than having been quietly spent.
        int ammoBefore = hero.PickupAmmo;
        for (int i = 0; i < 6; i++) m.FireWeapon(hero);
        hero.LoseCrown();

        Check(hero.Weapon == Weapons.Railgun, "and losing the crown gives you your gun back");
        Check(hero.PickupAmmo == ammoBefore,
              $"with the rounds you had ({hero.PickupAmmo} of {ammoBefore})");

        // ---- the block turns rounds around ----
        //
        // Handed off to a probe that straddles a physics step rather than run here.
        //
        // This is not fastidiousness. A pawn moved in script is not visible to a ray query until
        // the physics server has stepped, so posing two fighters in a clear lane and firing between
        // them on the same frame produces a round that passes through the target as if it were not
        // there. Every assertion in the first version of this passed or failed for that reason and
        // none of them were about blocking: the "shot from behind lands" control reported no
        // damage, which was the tell.
        BeginSaberProbe();
    }

    static Match? saberMatch;
    static Pawn? saberHero;
    static Pawn? saberShooter;
    static Vector3 saberLane;

    /// <summary>
    /// Set up the deflection probe: its own match, two fighters ten clear metres apart.
    ///
    /// Its own match because the assertions land a frame later, and the juggernaut checks free
    /// theirs on the way out — the pawns have to outlive the physics step this is waiting for.
    /// </summary>
    static void BeginSaberProbe()
    {
        var roster = new List<LobbySlot>();
        for (int i = 0; i < 2; i++)
            roster.Add(new LobbySlot { IsBot = true, ClassIndex = i, FactionIndex = i });

        var m = new Match();
        m.Build(app, new MatchSettings
        {
            Mode = GameMode.Juggernaut, ScoreLimit = 20, BotSkill = 1, ArenaIndex = 0,
        }, roster, visuals: false);

        // Ten clear metres somewhere in the arena, found rather than assumed. Hard-coding the
        // middle of the map at twenty-six metres up put the lane exactly where the Reliquary's two
        // skybridges cross.
        // Frozen for the duration.
        //
        // A probe that straddles a physics step has a whole match frame happening inside it, and
        // this match is in the scene tree like any other — so Godot was calling _PhysicsProcess on
        // it between the pose and the assertion, and its four bots were fighting each other in
        // there. Whatever they did to the pose was invisible from the assertion side, which is the
        // definition of a flaky test.
        m.Paused = true;

        var spot = ClearLane(m.Arena, 10f);
        Check(spot.HasValue, "the harness can find ten clear metres to fire across");

        if (spot is not { } lane) { m.QueueFree(); return; }

        saberMatch = m;
        saberHero = m.Pawns[0];
        saberShooter = m.Pawns[1];
        saberLane = lane;

        PoseSaberProbe(fromInFront: true);
        NextFrame(() => FinishSaberProbe(fromInFront: true));
    }

    /// <summary>
    /// Stand the two fighters in the lane, the hero either looking at the shooter or away.
    ///
    /// One tick each, which settles the facing from the input and pushes the transform through
    /// MoveAndSlide. The physics step that follows is what actually makes them targets.
    /// </summary>
    static void PoseSaberProbe(bool fromInFront)
    {
        var m = saberMatch!;
        var hero = saberHero!;
        var shooter = saberShooter!;

        hero.LoseCrown();
        hero.Respawn(saberLane);
        hero.ClearSpawnProtectionForTest();
        hero.TakeCrown(hero.Faction.Juggernaut);
        hero.Tick(1f / 60f, new PawnInput
        {
            Aim = MathU.FromAngle(fromInFront ? 0f : MathF.PI),
            Ads = true,
        }, m);

        shooter.LoseCrown();
        shooter.Respawn(saberLane + new Vector3(10f, 0f, 0f));
        shooter.ClearSpawnProtectionForTest();
        shooter.Tick(1f / 60f, new PawnInput { Aim = MathU.FromAngle(MathF.PI) }, m);
    }

    /// <summary>
    /// Fire one real round down the lane and see which way it comes out.
    ///
    /// No pawn ticks once the round is away, so nothing moves and nothing falls — the only thing
    /// changing is the round, which is what makes the damage attributable.
    /// </summary>
    static void FinishSaberProbe(bool fromInFront)
    {
        var m = saberMatch!;
        var hero = saberHero!;
        var shooter = saberShooter!;

        Check(hero.Blocking, "the blade is up");
        Check(!hero.Ads, "without trying to put a scope on a sword");
        Check(hero.Deflects(new Vector3(-1f, 0f, 0f)) == fromInFront,
              fromInFront ? "a shot from the front is inside the arc"
                          : "a shot from behind is not");

        float heroHp = hero.Health;
        float shooterHp = shooter.Health;
        int deflectedBefore = m.Deflections;

        // A burst rather than a single round, because one round is a coin flip. The SMG's cone is
        // four and a half degrees, which at ten metres is most of a metre of scatter against a
        // target under two metres tall — roughly one shot in four sails over the shoulder. The
        // first version fired once and failed about that often, on a build where nothing was
        // wrong. This suite has a standing rule against tests that teach you to ignore red.
        for (int burst = 0; burst < 12; burst++)
        {
            m.FireWeapon(shooter);
            for (int i = 0; i < 12; i++) m.StepShotsForTest(1f / 60f);
        }

        for (int i = 0; i < 60; i++) m.StepShotsForTest(1f / 60f);

        if (fromInFront)
        {
            Check(m.Deflections >= deflectedBefore + 6,
                  $"a raised blade turns rounds around ({m.Deflections - deflectedBefore} of 12)");
            Check(hero.Health >= heroHp, "the juggernaut is not hurt by them");
            Check(shooter.Health < shooterHp,
                  $"and it goes back into the person who fired it ({shooterHp - shooter.Health:0} damage)");

            PoseSaberProbe(fromInFront: false);
            NextFrame(() => FinishSaberProbe(fromInFront: false));
            return;
        }

        Check(m.Deflections == deflectedBefore, "a blade facing the wrong way turns nothing");
        Check(hero.Health < heroHp, $"and the shot lands ({heroHp - hero.Health:0} damage)");

        Check(Match.DeflectDamageScale > 1f, "a returned round hits harder than it left");
        Check(Pawn.BlockSlow < 1f, "and holding the blade up costs you speed");
        Check(Pawn.BlockArc < MathF.PI * 0.75f, "the block does not cover your back");

        foreach (var p in m.Pawns) p.GlobalPosition = new Vector3(0f, 800f, 0f);
        m.QueueFree();

        saberMatch = null;
        saberHero = null;
        saberShooter = null;
    }

    /// <summary>
    /// Find a stretch of clear air <paramref name="length"/> metres long, running along +X.
    ///
    /// Used to pose a shot with nothing in the way. Everything in these arenas is axis-aligned
    /// boxes, so a handful of samples along the line is an exact answer rather than an estimate.
    /// </summary>
    static Vector3? ClearLane(Arena arena, float length)
    {
        for (float y = 30f; y >= 12f; y -= 4f)
        for (float z = -60f; z <= 60f; z += 20f)
        for (float x = -60f; x <= 60f - length; x += 20f)
        {
            bool clear = true;

            for (int i = 0; i <= 10 && clear; i++)
            {
                var at = new Vector3(x + length * i / 10f, y, z);
                clear = arena.Contains(at) && arena.IsClearOfBlocks(at, Pawn.Radius + 1f, Pawn.Height);
            }

            if (clear) return new Vector3(x, y, z);
        }

        return null;
    }

    /// <summary>
    /// What a bot walks towards when it wants a gun.
    ///
    /// This is the check that would have caught the worst regression of the batch. Spreading med
    /// kits across the map took an arena from three to twenty-eight, and the bot's "free weapon
    /// slot, go and fill it" branch was asking for the nearest pickup of *any* kind — so every bot
    /// walked to a med kit, took it, still had a free slot, and set off for the next one. Damage
    /// across the whole suite fell by about nine tenths.
    ///
    /// The lesson is not "filter the query". It is that a number nobody thought of as a difficulty
    /// lever — how many med kits are on the floor — turned out to be one, because a piece of bot
    /// logic quietly depended on health being scarce.
    /// </summary>
    static void CheckBotsSeekGunsNotMedKits()
    {
        var m = current!;
        var p = m.Pawns[0];

        if (p.InVehicle) m.ToggleVehicle(p);
        p.Respawn(m.Arena.SpawnPoints[0]);
        p.ResetLoadout();

        Check(m.Arena.HealthSpawns.Count > 10, "the arena is carrying plenty of med kits");

        // Asked from every med kit on the map, with a free weapon slot and full health: the answer
        // must never be another med kit.
        int asked = 0, wrong = 0;

        foreach (var med in m.Arena.HealthSpawns)
        {
            p.GlobalPosition = med;
            asked++;

            if (m.NearestPickup(p, 55f, 12f) is not { } target) continue;

            foreach (var other in m.Arena.HealthSpawns)
                if (other.DistanceTo(target) < 0.01f) { wrong++; break; }
        }

        TestLog.Line($"    bot loot search: asked from {asked} med kits, {wrong} pointed at another one");
        Check(wrong == 0, "a bot looking for a gun is never sent to a med kit");

        // And a utility weapon is not a gun either — a bot with a portal gun is a bot that cannot
        // shoot back.
        Check(Weapons.PortalGun.IsUtility, "the portal gun counts as utility");

        bool wantsPortalCrate = false;
        foreach (var at in m.Arena.WeaponSpawns)
        {
            p.GlobalPosition = at;
            if (m.WeaponCrateInReach(p, combatOnly: true) && !m.WeaponCrateInReach(p))
                wantsPortalCrate = true;
        }

        Check(!wantsPortalCrate, "the bot crate filter is never wider than the player's");

        p.Respawn(m.Arena.SpawnPoints[0]);
    }

    /// <summary>
    /// The rocket launcher: splash is the larger half, it hurts the person holding it, and bots
    /// will not fire one into their own blast.
    /// </summary>
    static void CheckRocketLauncher()
    {
        var rocket = Weapons.RocketLauncher;

        Check(rocket.Explodes, "the rocket launcher fires explosive rounds");
        Check(rocket.BlastDamage > rocket.Damage,
              "its splash outweighs its direct hit — it is aimed at the floor, not threaded");
        Check(rocket.BlastRadius > 4f, "and the blast has real reach");

        // Slow enough to see coming. A rocket that arrives instantly is a hitscan gun that also
        // kills everyone standing near the person it hit.
        Check(rocket.ProjectileSpeed < 80f, "a rocket is slow enough to dodge");
        Check(rocket.Ammo > 0 && rocket.Ammo < 15, "and carries few enough rounds to be an event");

        var m = current!;
        var a = m.Pawns[0];
        var b = m.Pawns[1];

        if (a.InVehicle) m.ToggleVehicle(a);
        if (b.InVehicle) m.ToggleVehicle(b);

        a.Respawn(m.Arena.SpawnPoints[0]);
        b.Respawn(m.Arena.SpawnPoints[0]);
        a.ClearSpawnProtectionForTest();
        b.ClearSpawnProtectionForTest();

        a.TakeWeapon(rocket);
        Check(a.Weapon.Explodes, "a picked-up launcher really is the active weapon");

        // The bot rule: no firing an explosive at something inside your own blast. Asked of the
        // brain directly rather than waiting for a bot to happen to pick one up.
        var brain = new BotBrain(BotBrain.Skills.Length - 1);
        a.IsBot = true;

        b.GlobalPosition = a.GlobalPosition + new Vector3(rocket.BlastRadius * 0.5f, 0f, 0f);
        a.Facing = 0f;
        a.Pitch = 0f;

        bool firedPointBlank = false;
        for (int i = 0; i < 120; i++)
            if (brain.Think(1f / 60f, a, m).Fire) { firedPointBlank = true; break; }

        Check(!firedPointBlank, "a bot will not put a rocket into someone standing on top of it");

        a.IsBot = false;
        a.ResetLoadout();
    }

    /// <summary>
    /// The portal gun.
    ///
    /// Planted directly rather than by firing one: a round has to actually strike a surface, and
    /// where it strikes depends on whatever geometry happens to face the spawn the probe started
    /// on. The thing under test is the gate behaviour, not the ballistics, which the shared
    /// projectile sweep already covers.
    /// </summary>
    static void CheckPortalGun()
    {
        var gun = Weapons.PortalGun;

        Check(gun.PlantsPortal, "the portal gun plants gates");
        Check(gun.IsUtility, "and cannot hurt anybody");
        Check(gun.Damage == 0f && gun.BlastDamage == 0f, "no damage at all, direct or splash");

        // Reach, expressed as what it is for rather than as a number.
        //
        // Range on a gun is how far you can hurt someone. Range on this is how far apart the two
        // ends of a shortcut can be, so the bar is the map: a gate you can only put down within
        // sight of the last one is not a shortcut. Half the arena's long axis is the least that
        // means anything on a floor 278 metres across.
        float longAxis = Arena.HalfWidth * 2f;

        TestLog.Line($"    portal gun reaches {gun.Range:0}m on a {longAxis:0}m arena, "
                     + $"about {gun.Range / gun.ProjectileSpeed:0.0}s to the far end");

        Check(gun.Range > longAxis * 0.5f,
              $"a gate can be placed a useful distance away ({gun.Range:0}m of {longAxis:0}m)");

        foreach (var lethal in Weapons.Pickups)
            if (!lethal.IsUtility)
                Check(gun.Range > lethal.Range,
                      $"the portal gun outranges the {lethal.Name}, which shoots people");

        // Slow on purpose. A gate you can flick out mid-fight is a get-out-of-jail card; one that
        // takes seconds to arrive is a placement you commit to and can be punished for.
        Check(gun.Range / gun.ProjectileSpeed > 2f, "a long portal shot takes real time to land");

        var m = current!;
        var p = m.Pawns[0];

        if (p.InVehicle) m.ToggleVehicle(p);
        p.Respawn(m.Arena.SpawnPoints[0]);
        p.ClearSpawnProtectionForTest();

        m.ClearPortalsForTest();
        Check(m.PortalCount == 0, "the arena starts with no gates");

        // Two spots far apart, both on open floor a pawn fits in.
        var one = m.Arena.SpawnPoints[0];
        var two = m.Arena.SpawnPoints[1];

        Check(one.DistanceTo(two) > 30f, "the two test gates are genuinely far apart");

        m.PlantPortalForTest(one, Vector3.Up);
        Check(m.PortalCount == 1, "a shot plants a gate");

        // One gate on its own must do nothing. The first shot is a commitment, not a teleport.
        p.GlobalPosition = m.PortalSpot(0);
        int tripsBefore = m.PortalTrips;
        m.StepPortalsForTest(1f / 60f);
        Check(m.PortalTrips == tripsBefore, "a single gate takes nobody anywhere");

        m.PlantPortalForTest(two, Vector3.Up);
        Check(m.PortalCount == 2, "a second shot plants its pair");

        // Standing in the first gate now sends you to the second.
        p.GlobalPosition = m.PortalSpot(0);
        var far = m.PortalSpot(1);

        m.StepPortalsForTest(1f / 60f);

        Check(m.PortalTrips > tripsBefore, "walking into a gate takes you through it");
        Check(p.GlobalPosition.DistanceTo(far) < Match.PortalReach + 0.5f,
              $"and puts you down at the other one (landed {p.GlobalPosition.DistanceTo(far):0.0}m away)");
        Check(!m.OverlapsSolid(p), "not inside the scenery");
        Check(m.Arena.InPlay(p.GlobalPosition), "and inside the map");

        // The cooldown is what stops the exit gate throwing you straight back. Without it this
        // loops at whatever frame rate the machine happens to run at.
        int afterFirst = m.PortalTrips;
        for (int i = 0; i < 30; i++) m.StepPortalsForTest(1f / 60f);
        Check(m.PortalTrips == afterFirst, "and does not bounce you back and forth");

        // A third gate closes the oldest rather than making three.
        m.PlantPortalForTest(one, Vector3.Up);
        Check(m.PortalCount == 2, "a third shot closes the oldest gate rather than adding to them");

        // Gates are the arena's, not the shooter's — anyone can use them.
        var other = m.Pawns[1];
        if (other.InVehicle) m.ToggleVehicle(other);
        other.Respawn(m.Arena.SpawnPoints[2]);
        other.ClearSpawnProtectionForTest();
        other.GlobalPosition = m.PortalSpot(0);

        int before = m.PortalTrips;
        m.StepPortalsForTest(1f / 60f);
        Check(m.PortalTrips > before, "anyone can use a gate, not only whoever made it");

        m.ClearPortalsForTest();
        Check(m.PortalCount == 0, "and they can all be closed");

        p.Respawn(m.Arena.SpawnPoints[0]);
        other.Respawn(m.Arena.SpawnPoints[1]);
    }

    /// <summary>
    /// The grappling hook: it pulls you to the anchor, and it always lets go.
    ///
    /// The letting go is the half worth testing. A winch that runs forever holds you against a
    /// wall with the movement stick doing nothing, which is not a bug you notice in a unit test
    /// and is unmistakable in play — so every one of the four ways out gets its own check.
    /// </summary>
    static void CheckGrapple()
    {
        var hook = Weapons.Grapple;

        Check(hook.Grapples, "the grapple anchors where it lands");
        Check(hook.IsUtility, "and cannot hurt anybody");
        Check(hook.Ammo > 0, "it has ammo");
        Check(hook.ProjectileSpeed > 120f, "the line snaps out rather than lobbing");

        var m = current!;
        var p = m.Pawns[0];

        if (p.InVehicle) m.ToggleVehicle(p);
        p.Respawn(m.Arena.SpawnPoints[0] + Vector3.Up * 12f);
        p.ClearSpawnProtectionForTest();

        Check(!p.Grappling, "a fresh pawn is not grappling");

        // ---- point blank is refused ----
        p.StartGrapple(p.GlobalPosition + new Vector3(0.4f, 0f, 0f));
        Check(!p.Grappling, "an anchor inside arm's reach is not worth a round");

        // ---- it pulls, and it pulls upward ----
        var start = p.GlobalPosition;
        var anchor = start + new Vector3(0f, 16f, 0f);

        p.StartGrapple(anchor);
        Check(p.Grappling, "a real anchor starts the winch");

        var idle = new PawnInput { Aim = MathU.FromAngle(0f) };
        for (int i = 0; i < 20; i++) p.Tick(1f / 60f, idle, m);

        float climbed = p.GlobalPosition.Y - start.Y;
        TestLog.Line($"    grapple lifted {climbed:0.0}m in a third of a second");

        Check(climbed > 3f, $"the grapple hauls you toward the anchor (moved {climbed:0.0}m)");

        // ---- and it arrives ----
        for (int i = 0; i < 240 && p.Grappling; i++) p.Tick(1f / 60f, idle, m);

        Check(!p.Grappling, "the winch lets go rather than running forever");
        Check(p.GlobalPosition.DistanceTo(anchor) < 6f,
              $"and leaves you at the anchor ({p.GlobalPosition.DistanceTo(anchor):0.0}m away)");

        // ---- jumping cuts the line ----
        p.Respawn(m.Arena.SpawnPoints[0] + Vector3.Up * 12f);
        p.ClearSpawnProtectionForTest();
        p.StartGrapple(p.GlobalPosition + new Vector3(0f, 30f, 0f));
        Check(p.Grappling, "grappling again");

        p.Tick(1f / 60f, new PawnInput { Aim = MathU.FromAngle(0f), Jump = true }, m);
        Check(!p.Grappling, "jumping cuts the line");

        // ---- an anchor you cannot reach gives up rather than pinning you ----
        //
        // Aimed at the middle of the floor from above: the controller stops on the surface and the
        // anchor stays below it forever. Without the stall check this holds you there for the full
        // timeout with no way to move.
        p.Respawn(m.Arena.SpawnPoints[0] + Vector3.Up * 12f);
        p.ClearSpawnProtectionForTest();
        p.StartGrapple(p.GlobalPosition - new Vector3(0f, 40f, 0f));

        int ticks = 0;
        for (; ticks < 400 && p.Grappling; ticks++) p.Tick(1f / 60f, idle, m);

        Check(!p.Grappling, "an unreachable anchor eventually lets go");
        TestLog.Line($"    unreachable anchor released after {ticks / 60f:0.0}s");

        // ---- boarding a vehicle drops it ----
        p.Respawn(m.Arena.SpawnPoints[0]);
        p.StartGrapple(p.GlobalPosition + new Vector3(0f, 25f, 0f));

        if (m.VehicleList.Count > 0)
        {
            m.VehicleList[0].Restore(m.VehicleList[0].HomePosition + Vector3.Up * 0.5f, 0f);
            m.VehicleList[0].Board(p);
            Check(!p.Grappling, "climbing into a vehicle cuts the line");
            m.VehicleList[0].Eject();
        }

        p.ReleaseGrapple();
        p.Respawn(m.Arena.SpawnPoints[0]);
    }

    /// <summary>
    /// The jetpack is now four times what it was, and must still not carry anyone out of the map.
    ///
    /// Leaving the arena is fatal with no exceptions, and the play boundary tops out a little over
    /// forty metres. Killing a player for holding the jump button would be the strictly worse bug.
    /// </summary>
    static void CheckJetpack()
    {
        var m = current!;
        var p = m.Pawns[0];

        if (p.InVehicle) m.ToggleVehicle(p);
        p.Respawn(m.Arena.SpawnPoints[0]);
        p.ClearSpawnProtectionForTest();
        p.GiveJetpack();

        Check(p.HasJetpack, "the pack has fuel in it");
        Check(Pawn.JetFuelMax > 12f, "and enough of it to move around the arena on");

        // Held down for the whole tank, from the ground, going nowhere but up.
        var up = new PawnInput { Aim = MathU.FromAngle(0f), Jump = true, JumpHeld = true };

        float ceiling = p.GlobalPosition.Y;
        int burn = (int)((Pawn.JetFuelMax + 4f) * 60f);

        for (int i = 0; i < burn; i++)
        {
            p.Tick(1f / 60f, up, m);
            ceiling = MathF.Max(ceiling, p.GlobalPosition.Y);
        }

        TestLog.Line($"    jetpack topped out at {ceiling:0}m on a full tank");

        Check(ceiling > 18f, $"a full tank gets you genuinely high ({ceiling:0}m)");
        Check(m.Arena.InPlay(new Vector3(0f, ceiling, 0f)),
              $"and never above the play boundary ({ceiling:0}m)");
        Check(p.JetFuel <= 0.01f, "and the tank actually empties");

        // ---- and a tap has to be a tap ----
        //
        // Reported from play: "if I press it for like .25 secs, it flings me so high I die." The
        // ceiling only clamps while the button is *held*, so a quarter-second burst at the old
        // 34 m/s left you coasting a further twenty-six metres with nothing to stop you — straight
        // out through the top of the play boundary, which is fatal with no exceptions.
        //
        // Flown to its apex after release rather than measured during the burst, because the whole
        // failure was in the coast.
        foreach (float tap in new[] { 0.15f, 0.25f, 0.5f })
        {
            p.Respawn(m.Arena.SpawnPoints[0]);
            p.ClearSpawnProtectionForTest();
            p.GiveJetpack();

            float from = p.GlobalPosition.Y;

            for (int i = 0; i < (int)(tap * 60f); i++) p.Tick(1f / 60f, up, m);

            var released = new PawnInput { Aim = MathU.FromAngle(0f) };
            float apex = p.GlobalPosition.Y;

            for (int i = 0; i < 300 && p.GlobalPosition.Y >= apex - 0.01f; i++)
            {
                p.Tick(1f / 60f, released, m);
                apex = MathF.Max(apex, p.GlobalPosition.Y);
            }

            float climb = apex - from;
            TestLog.Line($"    a {tap:0.00}s tap carries you {climb:0.0}m");

            Check(m.Arena.InPlay(new Vector3(0f, apex, 0f)),
                  $"a {tap:0.00}s tap does not throw you out of the world (apex {apex:0}m)");

            // Bounded against what the physics should give rather than against a flat number, which
            // was the first version of this check and failed the half-second burst for being
            // correct. Burn at the rise speed, then coast v²/2g once the button is released.
            float expected = tap * Pawn.JetRise + Pawn.JetRise * Pawn.JetRise / (2f * Pawn.Gravity);

            Check(climb < expected + 4f,
                  $"a {tap:0.00}s tap climbs about what it should ({climb:0.0}m, expected ~{expected:0.0}m)");
        }

        // The reported case, on its own, because it is the one a player actually complained about:
        // "if I press it for like .25 secs, it flings me so high I die." At the old rise that burst
        // was worth about thirty-five metres and could leave the world entirely.
        {
            p.Respawn(m.Arena.SpawnPoints[0]);
            p.ClearSpawnProtectionForTest();
            p.GiveJetpack();

            float from = p.GlobalPosition.Y;
            for (int i = 0; i < 15; i++) p.Tick(1f / 60f, up, m);

            var released = new PawnInput { Aim = MathU.FromAngle(0f) };
            float apex = p.GlobalPosition.Y;

            for (int i = 0; i < 300 && p.GlobalPosition.Y >= apex - 0.01f; i++)
            {
                p.Tick(1f / 60f, released, m);
                apex = MathF.Max(apex, p.GlobalPosition.Y);
            }

            Check(apex - from < 16f,
                  $"a quarter-second tap is a boost, not a launch ({apex - from:0.0}m)");
        }

        p.Respawn(m.Arena.SpawnPoints[0]);
    }

    /// <summary>
    /// Getting out of a vehicle that is under way.
    ///
    /// Reported twice. The first fix covered being blown up in one, which the suite now proves at
    /// twenty-two sites — and the report came back as "when I leave the tank, it keeps putting me
    /// under the tank". So this is the other half: a voluntary dismount, at speed, with the hull
    /// still coasting afterwards, checked over the following half second rather than on the frame
    /// it happened. A placement that is clear on tick one and run over on tick ten is not a fix.
    /// </summary>
    static void CheckLeavingAVehicleUnderWay()
    {
        var m = current!;
        var p = m.Pawns[0];

        foreach (var rig in m.VehicleList)
        {
            foreach (float facing in new[] { 0.6f, 2.4f, 4.1f })
            {
                if (p.InVehicle) m.ToggleVehicle(p);
                if (!p.Alive) p.Respawn(m.Arena.SpawnPoints[0]);

                rig.Restore(rig.HomePosition + Vector3.Up * 0.5f, facing);
                rig.Board(p);
                rig.Facing = facing;

                // Under way, and turning: a hull rotating as the driver steps out sweeps its own
                // corners through the ground beside it, which is four metres further than its flank.
                var driving = new PawnInput { RawMove = new Vector2(0.8f, -1f) };
                for (int i = 0; i < 45; i++) { rig.Tick(1f / 60f, driving, m); p.RideAlong(); }

                m.ToggleVehicle(p);
                Check(!p.InVehicle, $"{rig.Def.Name} lets its driver out at speed");

                // Half a second of the world carrying on: the hull coasts, the pawn falls and
                // settles, and anything that was going to go wrong has time to.
                var idle = new PawnInput { Aim = MathU.FromAngle(p.Facing) };

                for (int i = 0; i < 30; i++)
                {
                    rig.Tick(1f / 60f, default, m);
                    p.Tick(1f / 60f, idle, m);

                    if (!p.Alive) break;

                    var d = p.GlobalPosition + Vector3.Up * (Pawn.Height * 0.5f)
                            - (rig.GlobalPosition + Vector3.Up * rig.Def.HalfExtents.Y);

                    float cf = MathF.Cos(rig.Facing), sf = MathF.Sin(rig.Facing);
                    float alongNose = d.X * cf + d.Z * sf;
                    float alongSide = -d.X * sf + d.Z * cf;

                    bool insideHull = MathF.Abs(alongNose) < rig.Def.HalfExtents.X
                                      && MathF.Abs(d.Y) < rig.Def.HalfExtents.Y
                                      && MathF.Abs(alongSide) < rig.Def.HalfExtents.Z;

                    Check(!insideHull,
                          $"a driver who steps out of a moving {rig.Def.Name} at {facing:0.0} rad "
                          + "never ends up inside it");
                }
            }

            rig.Restore(rig.HomePosition + Vector3.Up * 0.5f, 0f);
        }

        if (!p.Alive) p.Respawn(m.Arena.SpawnPoints[0]);
    }

    // ---- push walls ----

    const float DrivePushTime = 7f;

    static Pawn? pushSubject;
    static Vector3 pushStart;

    /// <summary>
    /// Park a pawn in the path of a sweeping wall and leave it there.
    ///
    /// The pawn is one of the harness's stub-driven "humans", and the stub is feeding an empty
    /// stick by this point, so it stands exactly where it is put. Anything that moves it was the
    /// wall.
    /// </summary>
    static void BeginPushWallProbe()
    {
        var m = current!;

        MovingPlatformDef? wall = null;
        foreach (var def in m.Arena.MovingPlatforms) if (def.Pushes) { wall = def; break; }

        Check(wall != null, "the arena has a wall that sweeps");
        if (wall is not { } w) return;

        // Standing at the far end of the sweep, so the wall has the whole run to arrive.
        pushSubject = m.Pawns[0];
        pushStart = w.B + Vector3.Up * 1.2f;

        pushSubject.GlobalPosition = pushStart;
    }

    static bool FinishPushWallProbe()
    {
        var m = current!;
        if (pushSubject is not { } p) return true;

        float moved = new Vector2(p.GlobalPosition.X - pushStart.X,
                                  p.GlobalPosition.Z - pushStart.Z).Length();

        TestLog.Line($"    push wall trench: moved {moved:0.0}m, fell "
                 + $"{pushStart.Y - p.GlobalPosition.Y:0.0}m, alive={p.Alive}");

        // What this can honestly assert, which is much less than it used to claim.
        //
        // It used to say "a sweeping wall moves a pawn standing in its way" and accept `!p.Alive`
        // as proof. That made it unfalsifiable, and it was: the probe stands the pawn at the end of
        // the wall's sweep, which is over a *carved trench* with no floor under it, so the pawn
        // falls and dies whether or not a wall ever arrives. It duly passed on a run reporting zero
        // metres of movement. There is no solid ground anywhere under the sweep, so "standing in
        // its way" is not a state this arena can be posed into at all — the shove itself is now
        // tested directly in CheckPushWallShoves, where it can be.
        //
        // What is left is still worth having, because it is the bug that actually shipped: a push
        // wall used to fire people out of the world. Ending up outside the arena is the failure.
        Check(m.Arena.InPlay(p.GlobalPosition) || !p.Alive,
              $"the push-wall trench never throws anyone out of the world ({p.GlobalPosition})");
        Check(p.GlobalPosition.Y < pushStart.Y + 4f,
              "and never launches them upward");

        pushSubject = null;
        return true;
    }

    /// <summary>
    /// The shove itself, posed rather than played.
    ///
    /// The live probe above stands a pawn over a trench and waits for a wall to arrive, which
    /// depends on where in its cycle the wall happened to be — so it is a coin toss dressed as a
    /// test, and it spent a while passing for the wrong reason. This asks the mechanic directly:
    /// here is a wall, here is where it was, here is where it is now, did the pawn come with it.
    /// </summary>
    static void CheckPushWallShoves()
    {
        var m = current!;
        var p = m.Pawns[0];

        if (p.InVehicle) m.ToggleVehicle(p);

        var idle = new PawnInput { Aim = MathU.FromAngle(0f) };
        if (!Land(p, m, idle)) { Check(false, "the harness can stand a pawn up to be shoved"); return; }

        Vector3 stood = p.GlobalPosition;

        // A wall the size of the real ones, arriving from behind the pawn and passing through it.
        var wall = new MovingPlatformDef(
            stood - new Vector3(6f, 0f, 0f), stood + new Vector3(6f, 0f, 0f),
            new Vector3(1.2f, 1.6f, 11f), period: 4f, dwell: 0f, pushes: true);

        Check(wall.Pushes, "the harness built a wall that pushes");

        Vector3 was = stood - new Vector3(1.5f, -1.2f, 0f);
        Vector3 now = stood - new Vector3(0.4f, -1.2f, 0f);

        for (int i = 0; i < 6; i++)
        {
            m.ShoveAlongForTest(wall, was, now, 1f / 60f);
            was = now;
            now += new Vector3(1.1f, 0f, 0f);
        }

        float shoved = p.GlobalPosition.X - stood.X;
        TestLog.Line($"    a wall sweeping +X shoved a standing pawn {shoved:0.00}m");

        Check(shoved > 0.5f, $"a wall pushes the pawn it sweeps through ({shoved:0.00}m)");
        Check(p.Alive, "without killing them on flat ground");

        // The direction is the wall's, not the pawn's. An earlier version of this mechanic assigned
        // position directly and ejected people through the wall in whatever direction the physics
        // solver preferred, which is what was firing players out of the map.
        Check(MathF.Abs(p.GlobalPosition.Z - stood.Z) < 2f, "and pushes them the way it is going");

        // Negative control: a wall that has not moved shoves nobody.
        Vector3 parked = p.GlobalPosition;
        for (int i = 0; i < 6; i++) m.ShoveAlongForTest(wall, parked, parked, 1f / 60f);

        Check(p.GlobalPosition.DistanceTo(parked) < 0.5f, "a wall standing still pushes nobody");

        p.Respawn(m.Arena.SpawnPoints[0]);
    }

    /// <summary>
    /// Bot driving, asked of the brain directly.
    ///
    /// Waiting for a bot to happen to walk to a vehicle inside a twenty-second window would make
    /// this a coin flip, and a coin-flip test teaches you to ignore red. Every decision here is
    /// posed to the brain with the world arranged so there is a right answer.
    /// </summary>
    static void CheckBotsDrive()
    {
        var m = current!;

        // Appetite has to order with difficulty, or it is not a difficulty lever.
        for (int i = 1; i < BotBrain.Skills.Length; i++)
            Check(BotBrain.Skills[i].VehicleAppetite > BotBrain.Skills[i - 1].VehicleAppetite,
                  $"{BotBrain.Skills[i].Name} takes vehicles more readily than {BotBrain.Skills[i - 1].Name}");

        // The drive scenario's fourth slot is the only bot in it.
        int botIndex = -1;
        for (int i = 0; i < m.Pawns.Count; i++) if (m.Pawns[i].IsBot) botIndex = i;

        Check(botIndex >= 0, "the drive scenario has a bot to interrogate");
        if (botIndex < 0) return;

        var bot = m.Pawns[botIndex];
        var brain = m.BrainForTest(botIndex);
        Check(brain != null, "the bot has a brain");
        if (brain == null) return;

        // Find a free hull with a gun, and stand the bot beside it.
        Vehicle? gunned = null;
        foreach (var v in m.VehicleList) if (v.Alive && !v.Occupied && v.Def.Gun != null) { gunned = v; break; }

        Check(gunned != null, "a gunned vehicle is free to be taken");
        if (gunned == null) return;

        bot.GlobalPosition = gunned.GlobalPosition + new Vector3(gunned.Def.HalfExtents.X + 1.2f, 0.6f, 0f);

        var decision = brain.Think(1f / 60f, bot, m);
        Check(decision.Use, "a bot standing beside a free vehicle decides to board it");

        // Now drive it.
        gunned.Board(bot);
        Check(bot.InVehicle, "the bot is aboard");

        var driving = brain.DriveThink(1f / 60f, bot, gunned, m);
        Check(MathF.Abs(driving.RawMove.Y) > 0.01f || MathF.Abs(driving.RawMove.X) > 0.01f,
              "a driving bot works the sticks rather than sitting still");
        Check(!driving.Use, "a healthy hull is not abandoned");

        // Line of sight from inside a hull. This was the bug that kept every bot driver silent:
        // the ray left the driver's eye, struck the driver's own vehicle, and reported cover.
        // Stood just clear of the hull rather than out at some distance across the map. Twice now
        // a fixed offset has put the target behind whatever happened to be built there — first the
        // perimeter wall, then one of the outer redoubts — and failed against correct code. At
        // three metres the only thing that can be in the way is the hull itself, which is exactly
        // the property under test.
        var other = m.Pawns[botIndex == 0 ? 1 : 0];
        var inward = new Vector3(-gunned.GlobalPosition.X, 0f, -gunned.GlobalPosition.Z).Normalized();

        other.GlobalPosition = gunned.GlobalPosition
                               + inward * (gunned.Def.HalfExtents.X + 3f)
                               + Vector3.Up * 1f;

        Check(m.HasLineOfSight(bot, other),
              "a bot inside a vehicle can see out of it");

        // Hurt enough and it gets out rather than going down with the hull.
        gunned.TakeDamage(gunned.Def.Health * 0.85f);
        var bailing = brain.DriveThink(1f / 60f, bot, gunned, m);
        Check(bailing.Use, "a bot bails out of a hull that is nearly gone");

        m.ToggleVehicle(bot);
        Check(!bot.InVehicle, "and gets out");

        // Straight back in would be the obvious failure. It should walk away first.
        var after = brain.Think(1f / 60f, bot, m);
        Check(!after.Use, "a bot that just bailed does not climb straight back in");

        gunned.Restore(gunned.HomePosition + Vector3.Up * 0.5f, 0f);
    }

    /// <summary>
    /// Blow a walkway out and check it leaves the world properly — collision, mesh, and the
    /// navigation graph.
    ///
    /// The graph is the part that matters. It is built once from arena data at match start and
    /// never rebuilt, so without explicitly disabling the nodes a bot would route happily across a
    /// bridge that was no longer there and walk straight into the drop.
    /// </summary>
    static void CheckWalkwaysCanBeBroken()
    {
        var m = current!;

        TestLog.Line($"    {m.BreakableCount} destructible pieces on {m.Arena.Name}");
        Check(m.BreakableCount > 50, $"the whole map comes apart, not one bridge ({m.BreakableCount})");

        // The one piece that must not. Out of bounds is fatal with no appeal, so a hole in the
        // perimeter is not a play, it is a way to delete somebody.
        int fragile = 0, solid = 0;
        foreach (var b in m.Arena.Blocks) { if (b.Fragile) fragile++; else solid++; }

        Check(solid >= Arena.PerimeterBlocks, "the outer wall is still standing");
        Check(fragile > solid, "and almost everything else is not");

        // Take one down and prove the graph noticed.
        TestLog.Line($"    {m.RoutableBreakableCount()} of them carry navigation nodes");

        var target = m.FirstRoutableBreakableForTest();
        Check(target >= 0, "at least one destructible walkway is somewhere bots route over");
        if (target < 0) return;

        // Asked of the graph rather than derived from a world position. The first attempt at this
        // probed the walkway's centre and failed against correct code: on the Reliquary the crow's nest
        // sits directly over the middle of the bridge, so that column legitimately carries no node,
        // and NearestNode answered with the floor forty feet below.
        int node = m.Nav.FirstNodeOn(target);
        Check(node >= 0, "the walkway carries a navigation node");
        if (node < 0) return;

        Vector3 onDeck = m.Nav.NodePosition(node);
        Check(m.Nav.NodeEnabled(node), "the intact walkway is routable");
        Check(m.Nav.NearestNode(onDeck) == node, "standing on it routes from it");

        // Measured as a delta rather than against zero.
        //
        // This asserted an absolute count of one, which was safe while the map offered five things
        // to blow up and nothing else on it was destructible. With the whole arena breakable the
        // drive scenario has been putting tank shells into the scenery for several seconds by the
        // time this runs, so the map legitimately has pieces down already — and the check failed
        // on correct code, which is the most expensive kind of test there is.
        int downBefore = m.BreakablesDown;

        m.DropBreakableForTest(target);

        Check(!m.Nav.NodeEnabled(node), "a downed walkway stops being routable");
        Check(m.Nav.NearestNode(onDeck) != node,
              "nothing snaps to a walkway that is no longer there");
        Check(m.BreakablesDown == downBefore + 1, "the match counts it as down");

        m.RaiseBreakableForTest(target);
        Check(m.Nav.NodeEnabled(node), "a rebuilt walkway is routable again");
        Check(m.Nav.NearestNode(onDeck) == node, "and can be routed from again");
        Check(m.BreakablesDown == downBefore, "the match counts it as back");

        Check(Match.PlatformRespawnTime > 10f && Match.PlatformRespawnTime < 120f,
              "a walkway comes back on a sane timer");

        // A walkway has to be worth a shell rather than incidental to one.
        var cannon = Vehicles.Tank.Gun!;
        Check(cannon.BlastDamage >= Match.PlatformHealth,
              "one well-placed shell brings a walkway down");

        CheckWallsComeDownAndBackUp(m);
    }

    /// <summary>
    /// A real blast, at a real wall, until it falls over — and then it comes back.
    ///
    /// Everything above this drops walkways by calling <c>DropBreakableForTest</c>, which proves
    /// the navigation graph reacts and proves nothing whatever about whether a player can bring a
    /// wall down. That is the entire feature, and the path it runs through — blast, distance
    /// falloff, per-piece health, rebuild clock — has no coverage at all otherwise. So this one
    /// goes through <see cref="Match.Blast"/> the way a rocket does.
    ///
    /// Aimed at the heaviest standing piece on the map rather than the nearest, because heavy
    /// structure is where the feature is most likely to be quietly impossible: a catwalk falls over
    /// if you look at it, and a nine-hundred-point citadel tier is the case that tells you whether
    /// the cap is somewhere a player can actually reach.
    /// </summary>
    static void CheckWallsComeDownAndBackUp(Match m)
    {
        var (index, at, health) = m.HeaviestBreakableForTest();

        Check(index >= 0, "the arena has something heavy standing in it");
        if (index < 0) return;

        TestLog.Line($"    heaviest standing piece: {health:0} hp at {at}");

        var rocket = Weapons.RocketLauncher;
        var shooter = m.Pawns[0];

        // Shells into the same spot until it goes, counted rather than assumed. The count is the
        // number that says whether heavy structure is a decision or a formality.
        int shells = 0;
        while (m.BreakableHealthForTest(index) > 0f && shells < 40)
        {
            m.BlastForTest(shooter, at, rocket.BlastDamage, rocket.BlastRadius);
            shells++;
        }

        TestLog.Line($"    it took {shells} rocket hits to bring down");

        Check(m.BreakableHealthForTest(index) < 0f,
              $"a heavy wall can actually be destroyed ({shells} rockets)");
        Check(shells > 1, "but not by one rocket, or nothing on the map is worth cover");
        Check(shells <= 12, $"and not so many that nobody would try ({shells})");

        // And it rebuilds on its own clock, which is the whole reason a fully destructible map does
        // not erode into a flat box over a long match.
        float wait = Match.StructureRebuildTime(health);

        m.StepBreakablesForTest(wait * 0.5f);
        Check(m.BreakableHealthForTest(index) < 0f, "it stays down for a while");

        m.StepBreakablesForTest(wait * 0.6f);
        Check(m.BreakableHealthForTest(index) >= health,
              "and then it puts itself back, at full health");

        Check(wait > Match.PlatformRespawnTime,
              $"heavy structure stays down longer than a catwalk ({wait:0}s)");
    }

    static void CheckSteering()
    {
        foreach (var (hull, _, startFacing) in driveProbes)
        {
            float turned = MathU.AngleDiff(hull.Facing, startFacing);
            TestLog.Line($"    {hull.Def.Name}: turned {turned:0.00} rad");

            // Signed, not absolute. Stick right has to turn right — an inverted axis passes any
            // magnitude check happily, which is exactly how it shipped inverted.
            Check(turned > 0.4f, $"{hull.Def.Name} steers right when the stick goes right");

            // The mesh and its collision box have to follow the heading, or the hull renders
            // sliding sideways however correct the velocity is.
            Check(MathF.Abs(MathU.AngleDiff(-hull.Rotation.Y, hull.Facing)) < 0.05f,
                  $"{hull.Def.Name} is oriented the way it drives");

            TestLog.Line($"    {hull.Def.Name}: {hull.Health:0}/{hull.Def.Health:0} hull at "
                     + $"{hull.GlobalPosition}, driver={hull.Driver != null}");

            // Conditional on surviving, deliberately. Two seconds of hard-locked turn on a map
            // with trenches in it sometimes ends in one, and that is the arena working rather than
            // the driving failing. Asserting survival here would have been a test demanding that
            // pits not be dangerous.
            Check(!hull.Alive || hull.Driver != null,
                  $"{hull.Def.Name} kept its driver, having survived the run");

            if (!hull.Alive)
                Check(hull.GlobalPosition.Y >= Arena.KillPlaneY - 0.01f,
                      $"{hull.Def.Name} came to rest at the kill plane rather than falling on");
        }
    }

    static void StartScenario()
    {
        var settings = scenarios[scenarioIndex];

        // One of each class and one of each faction, cycling, so every weapon shape and every
        // ability is exercised in every scenario.
        //
        // Most scenarios run the old four. One runs a full roster of twelve, because that is now
        // what a default match fields and because everything that scales with pawn count — spawn
        // separation, render layers, the projectile list, the bot brains, the frame budget — is
        // only ever tested by actually building that many.
        int fighters = scenarioIndex == crowdScenario || scenarioIndex == juggernautScenario
            ? LobbyScreen.MaxFighters
            : 4;

        var roster = new List<LobbySlot>();
        for (int i = 0; i < fighters; i++)
            roster.Add(new LobbySlot
            {
                IsBot = !(scenarioIndex == driveScenario && i < 3),
                ClassIndex = i,
                FactionIndex = i,
            });

        current = new Match();
        current.Build(app, settings, roster, visuals: false);
        elapsed = 0f;

        if (scenarioIndex == driveScenario) SetUpDriveProbes();

        TestLog.Line($"- [{scenarioIndex + 1}/{scenarios.Count}] {settings.Def.Name} on "
                 + $"{Arena.Names[settings.ArenaIndex]} (bot skill {settings.BotSkillName})");
    }

    /// <summary>Pumped once per frame. Returns true when every scenario has finished.</summary>
    /// <summary>
    /// Work a check has deferred to the next physics step. See <see cref="NextFrame"/>.
    /// </summary>
    static readonly List<Action> nextFrame = new();

    /// <summary>
    /// Run something on the following physics step rather than now.
    ///
    /// There is one reason this exists and it is worth stating plainly, because it will otherwise
    /// be rediscovered the hard way a third time: a body moved in script is not visible to a ray
    /// query until the physics server has stepped. Everything in this suite that poses pawns and
    /// then asks the world about them — anything that fires a round, anything that traces line of
    /// sight at a fighter — has to let a step happen in between, or the query answers as though
    /// the pawn were not there. That answer is indistinguishable from the feature being broken.
    /// </summary>
    static void NextFrame(Action work) => nextFrame.Add(work);

    public static bool Step(float dt, out int failed)
    {
        failed = failures;
        if (done) return true;
        if (current == null) return false;

        if (nextFrame.Count > 0)
        {
            var due = nextFrame.ToArray();
            nextFrame.Clear();
            foreach (var work in due) Once(work);
        }

        elapsed += dt;
        CheckInvariants(current);

        bool driving = scenarioIndex == driveScenario;
        if (driving && !StepDriveProbes(dt)) return false;

        bool over = driving || current.Finished || elapsed >= SecondsPerMatch;
        if (!over) return false;

        // A match where the bots never engaged is technically invariant-clean but worthless as a
        // test. Engagement is measured as shots fired and damage landed rather than as kills:
        // whether anyone actually dies inside the window is a difficulty outcome, and Recruit bots
        // are meant to be slow enough that they often do not.
        //
        // The scenario name is part of each message so failures from different scenarios are not
        // collapsed together by the duplicate suppression below.
        var settings = scenarios[scenarioIndex];
        string tag = $"{settings.Def.Name}/{settings.BotSkillName}";
        if (!driving) Check(current.ShotsFired > 0, $"{tag}: bots opened fire");
        // Damage is accumulated across the whole run rather than demanded of every scenario.
        //
        // Whether anybody actually connects inside a twenty-eight second window is a behaviour
        // outcome, not an invariant, and on the enclosed maps it is close to a coin toss: twelve
        // fighters spread over five command posts or two flag bases on the Thousand Rooms often
        // never see each other at all. Both Dominion and Capture the Flag failed this on runs where
        // nothing was wrong, which is exactly the flake this suite has a standing rule against.
        //
        // What is kept is the thing that would actually catch a regression. If bots ever stop
        // fighting — the sprint bug earlier in this project had them running past each other with
        // their guns down for an entire match — the total across thirteen scenarios goes to nearly
        // nothing, and that is asserted at the end of the run. Opening fire is still demanded of
        // every scenario individually, because that one is reliable.
        bool shortWindow = settings.TimeLimitSeconds > 0 && settings.TimeLimitSeconds < 10;
        if (!driving && !shortWindow) damageAcrossTheRun += current.DamageDealt;

        // A timed match must end on the clock rather than run past it.
        if (settings.TimeLimitSeconds > 0)
        {
            Check(current.Finished, $"{tag}: the time limit ended the match");
            Check(current.Elapsed >= settings.TimeLimitSeconds,
                  $"{tag}: the match ran the full clock");
            Check(current.Elapsed < settings.TimeLimitSeconds + 1f,
                  $"{tag}: the match stopped promptly at the limit");
        }

        // Printed so the difficulty curve is visible in the harness output rather than being an
        // article of faith — damage per second is the clearest single read on how hard the CPUs hit.
        int kills = 0;
        foreach (var p in current.Pawns) kills += p.Deaths;
        int specials = 0;
        foreach (var n in current.SpecialsUsed.Values) specials += n;

        TestLog.Line($"    {current.ShotsFired} shots, {current.DamageDealt:0} damage "
                 + $"({current.DamageDealt / MathF.Max(0.001f, elapsed):0.0}/s), {kills} kills, "
                 + $"{specials} specials {Describe(current.SpecialsUsed)}");

        // Whether the bots actually played the mode, rather than merely surviving it. Walking a
        // long way is not the same as taking a post, and every other number on this line would
        // look identical if the objective steering were doing nothing at all.
        if (scenarioIndex == dominionScenario)
        {
            TestLog.Line($"    dominion: {current.PostCaptures} posts changed hands, "
                     + $"blue {current.PostsHeld(0)}/orange {current.PostsHeld(1)}, "
                     + $"pools {current.Tickets[0]:0} vs {current.Tickets[1]:0}");

            // Reported, not asserted.
            //
            // Whether a post actually changes hands inside a twenty-eight second window is a coin
            // toss: a capture takes seven seconds of standing still, and the bots are also being
            // shot at. It came up 1, then 0, then 1 on three consecutive runs of identical code,
            // which is precisely the flake this suite has a standing rule against. What the bots
            // are *steered* at is deterministic, and that is checked in CheckDominion instead.
            Check(current.Tickets[0] < settings.ScoreLimit
                  || current.Tickets[1] < settings.ScoreLimit,
                  "dominion: somebody is losing reinforcements");
        }

        // How much of the map was on the floor when the whistle went.
        //
        // The one number that says whether making everything destructible was a mistake. The worry
        // it answers is real — an arena that erodes ends every long match in an empty box — and the
        // rebuild timer is the whole of the defence, so it is worth watching rather than assuming.
        TestLog.Line($"    {current.BreakablesDown} of {current.BreakableCount} pieces down at the end");

        Check(current.BreakablesDown < current.BreakableCount / 4,
              $"{tag}: the arena is still standing ({current.BreakablesDown} of "
              + $"{current.BreakableCount} down)");

        // Bots that never touch their special would leave every ability path untested while the
        // suite still reported green.
        //
        // Accumulated across the run rather than demanded of every scenario. Whether one of four
        // bots meets its special's conditions inside a twenty-eight second window is a genuine coin
        // flip — Second Wind needs the user to have been hurt recently, Revelation needs an enemy
        // in sight — and a scenario occasionally coming up empty says nothing about the code. This
        // failed once on Elimination having passed on the identical build minutes earlier, which is
        // exactly the flake this project has a standing rule against: a coin-flip test teaches you
        // to ignore red.
        if (!driving && !shortWindow) specialsAcrossTheRun += specials;
        TestLog.Line($"    crates: {current.PickupMix()}");
        Check(current.HealthCratesInCore > 0, $"{tag}: a med kit sits where the fighting is");
        TestLog.Line($"    {current.DistanceWalked / MathF.Max(1f, current.Pawns.Count):0} m walked per pawn");
        TestLog.Line($"    {current.DistanceWalked / MathF.Max(1f, current.Pawns.Count):0} m walked per pawn");
        TestLog.Line($"    {current.VehicleBoardings} vehicle boardings, {current.VehicleShotsFired} vehicle rounds fired");
        TestLog.Line($"    {current.Headshots} headshots, {current.WeaponsTaken} weapons and "
                 + $"{current.HealthTaken} med kits taken"
                 + (settings.Mode == GameMode.Elimination
                        ? $", reached round {current.Round}, finished={current.Finished}"
                        : ""));

        // A full roster has to be a denser fight, not merely a longer roster. This is the whole
        // point of raising the cap, so it is measured rather than assumed.
        if (scenarioIndex == crowdScenario)
        {
            float perFighter = Arena.HalfWidth * 2f * Arena.HalfDepth * 2f / current.Pawns.Count;

            TestLog.Line($"    {current.Pawns.Count} fighters, {perFighter:0} m² each, "
                         + $"{current.DamageDealt / MathF.Max(1f, elapsed):0.0} damage/s");

            Check(current.Pawns.Count == LobbyScreen.MaxFighters,
                  $"{tag}: the full roster actually got built");

            // Halo's Big Team Battle runs at roughly 4,400-5,600 m² a head. Anything much above
            // that and people stop finding each other, which is the fault this was fixing.
            Check(perFighter < 6000f, $"{tag}: the arena is no longer emptier than Big Team ({perFighter:0} m²)");

            // Every fighter needs somewhere of its own to arrive.
            Check(current.Arena.SpawnPoints.Count >= current.Pawns.Count,
                  $"{tag}: there is a spawn point for every fighter "
                  + $"({current.Arena.SpawnPoints.Count} for {current.Pawns.Count})");
        }

        // Juggernaut has to be played, not merely set up: the crown has to actually change hands
        // between bots who found each other on their own.
        if (scenarioIndex == juggernautScenario)
        {
            TestLog.Line($"    crown changed hands {current.CrownChanges} times, "
                         + $"held by {current.Juggernaut?.Name2 ?? "nobody"}");

            Check(current.CrownChanges > 0, $"{tag}: somebody took the crown");
            Check(current.Juggernaut is { }, $"{tag}: and it is being worn at the end");
        }

        // Capture the flag has to be *played*, not merely set up. The unit test proves the rules
        // hold when a situation is posed by hand; this is the one that proves four bots left alone
        // in an arena actually converge on a flag rather than milling about near it.
        //
        // Measured as flag contact rather than as captures. A capture is a 122-metre round trip
        // with an enemy team in the way, and whether one lands inside a 28-second window is a
        // question about bot speed, not about whether the mode works.
        if (settings.Mode == GameMode.CaptureTheFlag)
        {
            TestLog.Line($"    {current.Captures} captures, {current.FlagTouches} flag pickups");
            Check(current.Flags.Count == 2, $"{tag}: the arena has both flags");
            Check(current.FlagTouches > 0, $"{tag}: bots actually go for the flag");
        }

        // Elimination has to play in rounds. It used to end the whole match the first time
        // someone was left standing, despite scoring in rounds.
        //
        // Asserted as an implication rather than "reached round 2": whether a round resolves
        // inside the window depends on how fast the bots kill each other, and a flaky test is
        // worse than a narrow one. If a round *was* won, the loop must have moved on.
        if (settings.Mode == GameMode.Elimination)
        {
            int roundsWon = 0;
            foreach (var pawn in current.Pawns) roundsWon += pawn.RoundsWon;

            TestLog.Line($"    rounds won: {roundsWon}, now on round {current.Round}");

            if (roundsWon > 0)
                Check(current.Round > 1 || current.Finished || current.BetweenRounds,
                      $"{tag}: a won round advanced the match");

            Check(roundsWon <= current.Round,
                  $"{tag}: rounds won never exceeds rounds played");
        }

        // A team mode is won on the combined score, so the individual totals have to add up to it.
        if (settings.Def.Teams)
        {
            int sum = 0;
            foreach (var pawn in current.Pawns) sum += pawn.Score;
            Check(sum == current.TeamScore(0) + current.TeamScore(1),
                  $"{tag}: team scores account for every frag");
        }

        // Deliberately a printed number, not an assertion.
        //
        // It was a check when nothing else proved crates were reachable. The navigation graph test
        // now proves that deterministically, for every crate in every arena. What remains here is
        // whether bots happened to detour for one inside the window, which is a behaviour outcome:
        // a team match where they simply kept fighting scores zero and is not a defect.

        current.QueueFree();
        current = null;

        scenarioIndex++;
        if (scenarioIndex < scenarios.Count)
        {
            StartScenario();
            return false;
        }

        // Every ability path has to have been walked by the end of the run, even though no single
        // scenario is obliged to walk one.
        TestLog.Line($"    {specialsAcrossTheRun} specials used across the run");
        Check(specialsAcrossTheRun >= scenarios.Count, "bots use their specials across a full run");

        TestLog.Line($"    {damageAcrossTheRun:0} damage landed across the run");
        Check(damageAcrossTheRun > 1000f,
              $"bots actually fight across a full run ({damageAcrossTheRun:0} damage)");

        // Deferred work that never came due is a silently skipped check, which is worse than a
        // failing one: the suite reports green for assertions it never made.
        Check(nextFrame.Count == 0,
              $"every deferred check ran before the suite ended ({nextFrame.Count} left over)");

        TestLog.Line($"=== {checks - failures}/{checks} simulation checks passed ===");
        if (failures > 0) TestLog.Fail($"{failures} simulation check(s) FAILED");

        done = true;
        failed = failures;
        return true;
    }

    static string Describe(Dictionary<SpecialKind, int> used)
    {
        if (used.Count == 0) return "";

        var parts = new List<string>();
        foreach (var kv in used) parts.Add($"{kv.Key}:{kv.Value}");
        parts.Sort();
        return "(" + string.Join(" ", parts) + ")";
    }

    static void CheckInvariants(Match m)
    {
        foreach (var pawn in m.Pawns)
        {
            Vector3 pos = pawn.GlobalPosition;

            Check(!float.IsNaN(pos.X) && !float.IsNaN(pos.Y) && !float.IsNaN(pos.Z),
                  $"{pawn.Name2} position is not NaN");

            Check(!float.IsNaN(pawn.Health), $"{pawn.Name2} health is not NaN");
            // Against the pawn's own ceiling, not the class's. A juggernaut's crown multiplies it —
            // Achilles carries four and a half times a Trooper's pool — so an invariant written
            // against the class number calls every crowned fighter a bug.
            Check(pawn.Health >= 0f && pawn.Health <= pawn.MaxHealth + 0.01f,
                  $"{pawn.Name2} health {pawn.Health:0.0} within 0..{pawn.MaxHealth:0}");

            // Catches the tunnelling class of bug: a body punched through a wall by stacked
            // knockback or a bad dash step.
            Check(m.Arena.Contains(pos), $"{pawn.Name2} stayed inside the arena (at {pos})");
        }

        Check(m.ShotCount <= MaxReasonableShots,
              $"projectile count {m.ShotCount} stays bounded");

        // The kill feed grows on every death and is only trimmed on the way in, so it is exactly
        // the kind of list that quietly becomes a leak across a long match.
        Check(m.KillFeed.Count <= 8, $"kill feed count {m.KillFeed.Count} stays bounded");

        // Grenades are retired on contact or fuse; an unbounded list would mean neither is firing.
        Check(m.GrenadeCount <= 32, $"grenade count {m.GrenadeCount} stays bounded");

        foreach (var pawn in m.Pawns)
        {
            Check(pawn.SpecialCooldownFrac is >= 0f and <= 1f,
                  $"{pawn.Name2} special cooldown fraction in range");
            // Against the faction's duration, which is where the special lives. This invariant was
            // comparing to the class's — and passed only because the activation code was reading
            // the class's too. Two wrongs agreeing is not a passing test.
            Check(pawn.BuffTime <= pawn.Faction.SpecialDuration + 0.01f,
                  $"{pawn.Name2} buff never exceeds its duration");

            // Eye height must stay between the two stances. Drifting outside would mean the
            // crouch easing is unstable, and shots originate from the eye.
            Check(pawn.CurrentEyeHeight >= Pawn.CrouchEyeHeight - 0.02f
                  && pawn.CurrentEyeHeight <= Pawn.EyeHeight + 0.02f,
                  $"{pawn.Name2} eye height {pawn.CurrentEyeHeight:0.00} stays within its stances");

            // The collision capsule follows the stance too, so a crouched pawn is genuinely a
            // smaller target rather than only looking like one.
            Check(pawn.CurrentHeight >= Pawn.CrouchHeight - 0.02f
                  && pawn.CurrentHeight <= Pawn.Height + 0.02f,
                  $"{pawn.Name2} hitbox height {pawn.CurrentHeight:0.00} stays within its stances");

            // The eye must always sit inside the body it belongs to.
            Check(pawn.CurrentEyeHeight <= pawn.CurrentHeight + 0.02f,
                  $"{pawn.Name2} eye stays inside its own hitbox");

            // Sprinting and aiming are mutually exclusive by design; so are sprinting and sliding.
            Check(!(pawn.Sprinting && pawn.Ads), $"{pawn.Name2} never sprints and aims at once");
            Check(!(pawn.Sprinting && pawn.Sliding), $"{pawn.Name2} never sprints and slides at once");

            Check(pawn.FallProgress is >= 0f and <= 1f, $"{pawn.Name2} fall progress in range");

            // A corpse has to actually be falling over. The topple used to never run at all,
            // because dead pawns were skipped before their tick — so bodies stood upright and
            // players kept shooting them.
            if (!pawn.Alive && pawn.DeadFor > Pawn.FallDuration + 0.1f)
                Check(pawn.FallProgress >= 1f,
                      $"{pawn.Name2} has finished toppling {pawn.DeadFor:0.0}s after dying");
        }
    }

    /// <summary>
    /// Only the first instance of each distinct failure is reported. An invariant checked every
    /// frame would otherwise produce thousands of identical lines and bury everything else.
    /// </summary>
    static readonly HashSet<string> reported = new();

    static void Check(bool ok, string what)
    {
        checks++;
        if (ok) return;
        failures++;
        if (reported.Add(what)) TestLog.Fail($"  FAIL: {what}");
    }
}
