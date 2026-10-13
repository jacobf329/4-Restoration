using Godot;
using HitboxClone;

namespace Battlefront;

/// <summary>
/// The new game's entry point, and deliberately not the old one's.
///
/// This shares the asset layer with what came before — the model loaders, the surface library,
/// the pad handling, the audio buses — because those are libraries and rewriting them would buy
/// nothing. It shares none of the game: no Match, no Arena, no modes. The thing being built here
/// is one mode on purpose.
///
/// Run it with:  godot --path . res://scenes/Battlefront.tscn
/// </summary>
public partial class Field : Node3D
{
    public Station Station = null!;
    public Battle Battle = null!;

    Trooper player = null!;
    Camera3D cam = null!;
    Hud hud = null!;

    float camYaw = -Mathf.Pi * 0.5f;
    float camPitch = -0.18f;

    /// <summary>Over the right shoulder, the way the game this clones frames a soldier.</summary>
    const float Boom = 4.6f;
    const float Shoulder = 0.9f;

    /// <summary>Screenshot mode: write a PNG after N frames and quit. See --shots.</summary>
    string? shotPath;
    int shotFrames = 30;

    /// <summary>
    /// --fire: hold the trigger down for N seconds through the real input path and report.
    ///
    /// This exists because "can the player shoot" was answered wrong twice from reading the
    /// code, and both times the code read correctly. Once the pad was being polled and then
    /// never looked at, because Devices.All[0] is the keyboard; once the shot was fired and its
    /// tracer deleted before anything drew it. Neither is visible in PlayerFire, which is the
    /// function anybody checking would open.
    ///
    /// So this drives a scripted pad through Driver, _PhysicsProcess and PlayerFire - the whole
    /// chain, in the real scene, with the real station loaded - and prints what came out the
    /// far end. A number that is zero is the bug; there is nothing to interpret.
    /// </summary>
    float fireSeconds;
    float fireRan;
    ScriptedDevice? trigger;
    Trooper? mark;

    /// <summary>--sim: run the battle with nobody watching and report what happened.</summary>
    float simSeconds;
    float simRan;
    float simReport;
    int frame;

    public override void _Ready()
    {
        bool headless = DisplayServer.GetName().Contains("headless");

        PropModels.Enabled = !headless;
        CharacterModels.Enabled = !headless;
        WeaponModels.Enabled = !headless;

        var args = OS.GetCmdlineUserArgs();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--shot" && i + 1 < args.Length) shotPath = args[i + 1];
            if (args[i] == "--at" && i + 3 < args.Length)
                startAt = new Vector3(args[i + 1].ToFloat(), 1f, args[i + 2].ToFloat());
            if (args[i] == "--yaw" && i + 1 < args.Length) camYaw = Mathf.DegToRad(args[i + 1].ToFloat());
            if (args[i] == "--after" && i + 1 < args.Length) shotFrames = args[i + 1].ToInt();
            if (args[i] == "--sim" && i + 1 < args.Length) simSeconds = args[i + 1].ToFloat();
            if (args[i] == "--fire" && i + 1 < args.Length) fireSeconds = args[i + 1].ToFloat();
        }

        Station = new Station();
        BuildVisuals();

        Battle = new Battle(Station);
        Fill();

        player = Battle.Troopers[0];
        if (startAt is { } where) player.GlobalPosition = where;

        cam = new Camera3D { Current = true, Fov = 78f, Far = 600f };
        AddChild(cam);
        PlaceCamera(0f, snap: true);

        hud = new Hud { Field = this };
        AddChild(hud);

        BuildTracers();
        BuildMuzzle();

        // The interior rig, not the arena's. See Graphics.BuildInterior for why a daylight sun
        // and a sky-blue ambient inside a sealed battle station was half of what looked like a
        // texture problem.
        AddChild(Graphics.BuildCeilingLight());
        AddChild(Graphics.BuildInteriorFill());
        AddChild(Graphics.BuildInterior(Station.HalfSpan * 2f));

        // Sound, which the station had none of at all.
        //
        // Half of "allow me to shoot" is hearing the gun go off. A trigger that produces no
        // noise reads as a trigger that is not connected to anything, whatever the screen says
        // - which is most of what was wrong here even once the bolt was being drawn. The banks
        // and the buses already exist and are already mixed; the new game simply never started
        // them.
        Audio.Init();
        Sfx.Init(this);

        if (fireSeconds > 0f)
        {
            trigger = new ScriptedDevice("trigger") { HoldAttack = true };

            // A shade below level, and the camera re-placed to match before anything is
            // measured off its axis.
            //
            // Not zero, and the reason is a fact about this camera worth knowing. The view sits
            // at the player's eyes, 1.8m up, and a standing soldier's capsule stops at 1.8m -
            // so a perfectly level shot grazes the top of an enemy's head and mostly misses.
            // The game's own resting pitch is -0.18 for exactly this reason. Aiming slightly
            // down is what pointing at a chest IS, and a probe firing level was measuring a
            // player who never looks where they are shooting.
            camPitch = -0.055f;
            PlaceCamera(0f, snap: true);

            // An empty range: one enemy fifteen metres in front and everybody else moved well
            // out of the way.
            //
            // Clearing the range is the part that makes this evidence. The first version left
            // the match as it was, which meant the player stood in a crowd of nineteen friendly
            // soldiers with the target behind them - so the shots that mattered hit a teammate,
            // the target was killed by somebody else's bot within a second, and the probe
            // reported "hits 0" for a gun that was working perfectly. A test whose answer
            // depends on where forty soldiers happen to be standing is not a test.
            foreach (var t in Battle.Troopers)
            {
                if (t == player) continue;
                t.GlobalPosition = new Vector3(0f, 2f, -Station.HalfSpan + 6f);
            }

            foreach (var t in Battle.Troopers)
            {
                if (t.Team == player.Team) continue;

                // On the CAMERA's line, not on camYaw's. Over the shoulder, the two are about
                // eleven degrees apart: the camera sits 0.9m to the right of the head and looks
                // back in at it, so its forward axis converges on the player rather than
                // running parallel to where they are facing. The crosshair is on the camera's
                // axis and so is the shot, which is correct and is also why the first version
                // of this probe reported no hits - it had stood the target where the SOLDIER
                // was pointing, three metres off where the gun actually shoots.
                var axis = -cam.GlobalBasis.Z with { Y = 0f };
                t.GlobalPosition = (cam.GlobalPosition + axis.Normalized() * 15f) with { Y = 0.1f };
                mark = t;
                GD.Print($"fire: target {t.Name} placed at {t.GlobalPosition}, "
                       + $"player at {player.GlobalPosition}, cam at {cam.GlobalPosition}");
                break;
            }
        }

        SetPhysicsProcess(true);
    }

    Vector3? startAt;

    /// <summary>
    /// Whichever device is actually driving: a connected pad if there is one, else the keyboard.
    ///
    /// Not Devices.All[0], which is what this used to read and is the reason the controller did
    /// nothing at all. Devices builds its list keyboards-first and appends the eight gamepads
    /// after them, so All[0] is ALWAYS KeyboardDevice 0 however many pads are plugged in - the
    /// pad was being polled every frame and then never looked at. This is a controller-first
    /// game and its entry point was reading the keyboard.
    /// </summary>
    InputDevice? Driver()
    {
        if (trigger != null) return trigger;

        foreach (var d in Devices.All)
            if (d.IsGamepad && d.Connected) return d;

        return Devices.Keyboards[0];
    }

    /// <summary>For the HUD, which should not have to reach into the roster.</summary>
    public bool PlayerAlive => player is { Alive: true };

    /// <summary>The player's health as a fraction, or null before there is a player.</summary>
    public float? PlayerHealth => player == null ? null : player.Health / Trooper.MaxHealth;

    /// <summary>
    /// Forty soldiers, twenty a side.
    ///
    /// The two sides wear different generated characters so you can tell at a glance who is
    /// shooting at you, which at this range and in this light is most of what team colour has to
    /// do. Every one of them is the same Trooper the player is, driven by the same movement code
    /// through the same Order - see Trooper.Order for why that matters.
    /// </summary>
    void Fill()
    {
        for (int team = 0; team < 2; team++)
        for (int i = 0; i < Battle.PerSide; i++)
        {
            var t = new Trooper
            {
                Team = team,
                Model = team == 0 ? "vessels" : "custodians",
            };

            AddChild(t);
            t.GlobalPosition = Battle.SpawnFor(team);
            Battle.Add(t);
        }
    }

    /// <summary>Every box in the station, as collision plus something to look at.</summary>
    void BuildVisuals()
    {
        foreach (var b in Station.Boxes)
        {
            var body = new StaticBody3D { Position = b.Centre };

            body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = b.Half * 2f } });

            if (PropModels.Instance(b.Model, b.Half, b.Yaw) is { } prop)
            {
                body.AddChild(prop);
            }
            else
            {
                body.AddChild(new MeshInstance3D
                {
                    Mesh = new BoxMesh { Size = b.Half * 2f },
                    MaterialOverride = b.Glow
                        ? Graphics.Hot(b.Tint, 1.9f)
                        : Graphics.SurfaceAt(b.Tint, b.Centre, b.Top, false, b.Surface),

                    // A light fitting that casts a shadow is a light fitting that puts a dark
                    // bar underneath itself, which is the opposite of what it is there for.
                    CastShadow = b.Glow
                        ? GeometryInstance3D.ShadowCastingSetting.Off
                        : GeometryInstance3D.ShadowCastingSetting.On,
                });
            }

            AddChild(body);
        }

        // A marker on each command post.
        //
        // Thin, tall and see-through, because you SPAWN on these. The first version was a
        // solid 4.4m cylinder a metre thick sitting exactly where players arrive, so the
        // first thing anybody saw on respawning was a wall of flat colour filling the screen
        // - the post you had just spawned at was the one thing you could not see past.
        //
        // A marker is for finding a post from across the station, which wants height and
        // nothing else. Width only gets in the way, and at arm's length so does opacity.
        foreach (var post in Station.Posts)
        {
            var tint = post.Owner switch
            {
                0 => new Color(0.35f, 0.62f, 1f),
                1 => new Color(1f, 0.55f, 0.2f),
                _ => new Color(0.8f, 0.8f, 0.8f),
            };

            var glow = Graphics.Hot(tint, 1.4f);
            glow.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
            glow.AlbedoColor = tint with { A = 0.35f };

            AddChild(new MeshInstance3D
            {
                Position = post.Centre + Vector3.Up * 4f,
                Mesh = new CylinderMesh { TopRadius = 0.18f, BottomRadius = 0.3f, Height = 8f },
                MaterialOverride = glow,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            });
        }
    }

    public override void _PhysicsProcess(double delta)
    {
        float dt = (float)delta;

        Devices.PollAll(dt);

        // The scripted trigger is not in Devices.All, so it polls itself.
        trigger?.Poll(dt);

        var pad = Driver();

        if (pad != null && player.Alive)
        {
            // Right stick looks. Inverted Y is the convention the rest of the project uses.
            camYaw += pad.Look.X * 2.6f * dt;
            camPitch = Mathf.Clamp(camPitch - pad.Look.Y * 2.0f * dt, -1.1f, 0.9f);

            player.AimYaw = camYaw;
            player.Step(dt, Trooper.FromPad(pad, camYaw));
        }

        if (simSeconds > 0f) { Simulate(dt); return; }

        // Where the ear is, so world sounds attenuate against the player rather than the origin.
        Sfx.Listeners = new[] { player.GlobalPosition };

        Battle.Step(dt, player);

        // The trigger, and it has to be read AFTER the battle steps.
        //
        // This was the bug behind "allow me to shoot", and it was never in the firing code.
        // Battle.Step opens by clearing the tracer list, because that list is one frame's worth
        // of shots and the view redraws it every frame. Firing before the step meant the
        // player's bolt was added and then deleted, every single time, before anything had a
        // chance to draw it - so the one piece of evidence that the trigger worked at all was
        // destroyed a few microseconds after it was created. The raycast ran, the damage landed,
        // and the screen showed nothing. Twenty bots firing looked fine throughout, because
        // THEIR shots are added by the step, after the clear.
        //
        // Found by reading the order of operations rather than the firing code, which had been
        // read three times and was correct every time.
        if (pad is { AttackHeld: true } && player.Alive)
        {
            var dir = -cam.GlobalBasis.Z;
            Battle.PlayerFire(player, cam.GlobalPosition + dir * 0.6f, dir);
        }

        PlaceCamera(dt, snap: false);
        DrawTracers();
        DrawMuzzle();

        if (fireSeconds > 0f)
        {
            fireRan += dt;

            if (fireRan >= fireSeconds)
            {
                GD.Print($"fire: trigger held {fireRan:0.0}s as {pad?.Label ?? "nothing"}"
                       + $"  shots {Battle.PlayerShots}  hits {Battle.PlayerHits}"
                       + $"  (wall {Battle.PlayerIntoWall} friend {Battle.PlayerIntoFriend}"
                       + $" air {Battle.PlayerIntoAir})"
                       + $"  tracers-this-frame {Battle.Tracers.Count}"
                       + $"  muzzle {(Battle.Muzzle == null ? "none" : "drawn")}"
                       + $"  enemy tickets {Battle.Tickets[1]:0}"
                       + $"\n      target now {(mark == null ? "none" : mark.GlobalPosition.ToString())}"
                       + $" alive {mark?.Alive} hp {mark?.Health:0}"
                       + $"\n      last shot {Battle.LastShot.From} -> {Battle.LastShot.To}"
                       + $"  ({Battle.LastShot.From.DistanceTo(Battle.LastShot.To):0.0}m)");

                GetTree().Quit();
                return;
            }
        }

        frame++;
        if (shotPath != null && frame >= shotFrames) Capture();
    }

    /// <summary>
    /// The chase camera: behind and above the shoulder, pulled in when a wall is in the way.
    ///
    /// The pull-in is not optional on a map made of corridors. A fixed boom puts the camera
    /// inside the wall behind you every time you back into a doorway, and the view either clips
    /// through the station or goes black.
    /// </summary>
    void PlaceCamera(float dt, bool snap)
    {
        // Keep watching the body while it is down, rather than cutting to nothing.
        var focus = player.GlobalPosition + Vector3.Up * (player.EyeHeight + 0.25f);

        var dir = new Vector3(Mathf.Cos(camYaw) * Mathf.Cos(camPitch),
                              Mathf.Sin(camPitch),
                              Mathf.Sin(camYaw) * Mathf.Cos(camPitch));

        var right = new Vector3(-Mathf.Sin(camYaw), 0f, Mathf.Cos(camYaw));
        var want = focus + right * Shoulder - dir * Boom;

        // Resolved every frame, and the SMOOTHED position is resolved too.
        //
        // This went wrong in three ways at once and the result was a black screen indoors.
        // The cast used to start at the shoulder rather than at the head: a shoulder offset
        // that is already inside a wall gives a ray that begins inside a body, which Godot
        // does not report a hit for, so the camera took the whole 4.6m boom and ended up
        // buried in the station. From inside a box you see through its back faces, so the
        // near walls and the floor vanish and only distant unoccluded geometry draws - which
        // is exactly what a player saw and I had never once hit, because every capture I took
        // posed the camera somewhere open.
        //
        // The head is the right origin because it is the one point guaranteed to be in clear
        // air: the player is standing there. And the lerp needs resolving as well, or the
        // camera slides through a wall on its way to a position that was itself fine.
        var safe = Unblocked(focus, want);

        cam.GlobalPosition = snap
            ? safe
            : Unblocked(focus, cam.GlobalPosition.Lerp(safe, 1f - Mathf.Exp(-18f * dt)));

        cam.LookAt(focus, Vector3.Up);
    }

    /// <summary>
    /// The furthest point along <paramref name="from"/> → <paramref name="to"/> that is not
    /// through something solid, kept a little clear of whatever it hit.
    /// </summary>
    Vector3 Unblocked(Vector3 from, Vector3 to)
    {
        var span = to - from;
        float reach = span.Length();

        if (reach < 0.01f) return to;

        var hit = GetWorld3D().DirectSpaceState.IntersectRay(
            new PhysicsRayQueryParameters3D
            {
                From = from,
                To = to,
                Exclude = new Godot.Collections.Array<Rid> { player.GetRid() },
            });

        if (hit.Count == 0 || !hit.ContainsKey("position")) return to;

        // Pulled in off the surface by more than the near plane, so the wall it stopped at
        // does not clip through the front of the view. Never closer than a metre, because a
        // camera inside the soldier's own head is its own kind of broken.
        float gap = Mathf.Clamp(from.DistanceTo((Vector3)hit["position"]) - 0.4f, 1f, reach);

        return from + span / reach * gap;
    }

    /// <summary>
    /// Run the battle flat out with no player and no camera, and say what it did.
    ///
    /// This exists because the first screenshot of forty soldiers answered almost nothing: it
    /// showed a clump of orange standing still and three hundred reinforcements untouched, and
    /// none of "are they pathing", "are they shooting" or "is anybody dying" could be read off
    /// it. A render is also the slowest possible way to ask - 420 physics frames is seven
    /// seconds of battle and took minutes to draw.
    ///
    /// Numbers every ten seconds, so a battle that stalls is visible as a line that stops
    /// changing rather than as a picture that looks much like the last one.
    /// </summary>
    void Simulate(float dt)
    {
        Battle.Step(dt, null);

        simRan += dt;
        simReport -= dt;

        if (simReport <= 0f)
        {
            simReport = 10f;

            int blueUp = 0, orangeUp = 0;
            foreach (var t in Battle.Troopers)
                if (t.Alive) { if (t.Team == 0) blueUp++; else orangeUp++; }

            var held = "";
            foreach (var post in Battle.Station.Posts)
                held += post.Owner switch { 0 => "B", 1 => "O", _ => "." };

            // Who is actually STANDING on each post, which is the only way to tell a post nobody
            // can take from a post nobody ever walks to.
            var crowd = "";
            foreach (var post in Battle.Station.Posts)
            {
                int n = 0;
                foreach (var t in Battle.Troopers)
                    if (t.Alive && t.GlobalPosition.DistanceTo(post.Centre) < Post.Radius) n++;
                crowd += n.ToString() + " ";
            }

            var sent = "";
            for (int i = 0; i < Battle.Station.Posts.Count; i++)
                sent += Battle.AssignedTo(i).ToString() + " ";

            GD.Print($"t={simRan,5:0}s  tickets {Battle.Tickets[0],5:0}/{Battle.Tickets[1],-5:0}"
                   + $"  up {blueUp,2}/{orangeUp,-2}  posts {held}"
                   + $"  spread {Spread(),4:0}m  on-post {crowd} shot {Battle.Shot} fell {Battle.Fell}"
                   + $"  reach E{Battle.MaxEast:0} W{Battle.MaxWest:0}");
        }

        if (simRan >= simSeconds) { GD.Print("sim done"); GetTree().Quit(); }
    }

    /// <summary>
    /// How far apart the two sides are, on average. The single most useful number for "are they
    /// actually advancing on each other" — if it never falls, nobody is going anywhere.
    /// </summary>
    float Spread()
    {
        Vector3 blue = Vector3.Zero, orange = Vector3.Zero;
        int nb = 0, no = 0;

        foreach (var t in Battle.Troopers)
        {
            if (!t.Alive) continue;
            if (t.Team == 0) { blue += t.GlobalPosition; nb++; } else { orange += t.GlobalPosition; no++; }
        }

        if (nb == 0 || no == 0) return 0f;
        return (blue / nb).DistanceTo(orange / no);
    }

    /// <summary>
    /// A fixed pool of tracer segments, reused every frame.
    ///
    /// Pooled rather than spawned: forty soldiers firing twice a second is eighty nodes a second
    /// created and freed, and the allocation would cost more than the drawing. Any shot beyond
    /// the pool simply is not drawn, which nobody can tell in a firefight.
    /// </summary>
    const int MaxTracers = 72;
    readonly MeshInstance3D[] tracers = new MeshInstance3D[MaxTracers];

    void BuildTracers()
    {
        var blue = Graphics.Hot(new Color(0.55f, 0.8f, 1f), 6f);
        var orange = Graphics.Hot(new Color(1f, 0.62f, 0.25f), 6f);

        for (int i = 0; i < MaxTracers; i++)
        {
            var bolt = new MeshInstance3D
            {
                Mesh = new BoxMesh { Size = new Vector3(0.13f, 0.13f, 1f) },
                MaterialOverride = i % 2 == 0 ? blue : orange,
                Visible = false,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            };

            tracers[i] = bolt;
            AddChild(bolt);
        }
    }

    /// <summary>Stretch one pooled segment along each shot fired this frame.</summary>
    void DrawTracers()
    {
        int n = 0;

        foreach (var (from, to, team) in Battle.Tracers)
        {
            // Pairs are kept blue-then-orange so a side always has segments of its own colour
            // available, however lopsided the shooting is.
            int slot = n * 2 + team;
            if (slot >= MaxTracers) break;

            var bolt = tracers[slot];
            var span = to - from;
            float len = span.Length();

            if (len < 0.2f) continue;

            // A shot fired straight up or straight down has no yaw for LookAt to resolve
            // against Vector3.Up, and Godot warns about it once per bolt per frame. Leaning the
            // reference a hair off vertical costs nothing visible on a 9cm bolt.
            var up = Mathf.Abs(span.Normalized().Y) > 0.999f ? Vector3.Forward : Vector3.Up;

            bolt.Visible = true;
            bolt.GlobalPosition = from + span * 0.5f;
            bolt.LookAt(to, up);
            bolt.Scale = new Vector3(1f, 1f, len);

            n++;
        }

        for (int i = n * 2; i < MaxTracers; i++) tracers[i].Visible = false;
    }

    /// <summary>
    /// A flash at the gun for the frame it goes off.
    ///
    /// Small and very bright rather than large: it is a confirmation, not an effect, and the
    /// thing it is confirming is in the player's peripheral vision while they watch the
    /// crosshair. One pooled node, hidden on every frame the trigger is not down.
    /// </summary>
    MeshInstance3D flash = null!;

    void BuildMuzzle()
    {
        flash = new MeshInstance3D
        {
            Mesh = new SphereMesh { Radius = 0.14f, Height = 0.28f, RadialSegments = 8, Rings = 4 },
            MaterialOverride = Graphics.Hot(new Color(1f, 0.86f, 0.55f), 9f),
            Visible = false,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };

        AddChild(flash);
    }

    void DrawMuzzle()
    {
        if (Battle.Muzzle is { } shot)
        {
            flash.Visible = true;

            // Battle already resolved the gun hand; the flash goes exactly where the bolt
            // leaves from, or the two read as two different weapons.
            flash.GlobalPosition = shot.At;
        }
        else
        {
            flash.Visible = false;
        }
    }

    void Capture()
    {
        var img = GetViewport().GetTexture().GetImage();
        img.SavePng(shotPath);
        GD.Print($"wrote {shotPath}");
        GetTree().Quit();
    }
}
