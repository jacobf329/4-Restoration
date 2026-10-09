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

        AddChild(Graphics.BuildSun());
        AddChild(Graphics.BuildFill());
        AddChild(Graphics.BuildEnvironment(fog: true, reach: Station.HalfSpan * 2f));

        SetPhysicsProcess(true);
    }

    Vector3? startAt;

    /// <summary>For the HUD, which should not have to reach into the roster.</summary>
    public bool PlayerAlive => player is { Alive: true };

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
                    MaterialOverride = Graphics.SurfaceAt(b.Tint, b.Centre, b.Top, false, b.Surface),
                });
            }

            AddChild(body);
        }

        // A marker on each command post. Placeholder geometry: the real thing is a console you
        // stand next to, and it arrives with the capture rules.
        foreach (var post in Station.Posts)
        {
            AddChild(new MeshInstance3D
            {
                Position = post.Centre + Vector3.Up * 2.2f,
                Mesh = new CylinderMesh { TopRadius = 0.5f, BottomRadius = 0.9f, Height = 4.4f },
                MaterialOverride = Graphics.Hot(
                    post.Owner switch { 0 => new Color(0.35f, 0.62f, 1f),
                                        1 => new Color(1f, 0.55f, 0.2f),
                                        _ => new Color(0.8f, 0.8f, 0.8f) }, 1.4f),
            });
        }
    }

    public override void _PhysicsProcess(double delta)
    {
        float dt = (float)delta;

        Devices.PollAll(dt);
        var pad = Devices.All.Count > 0 ? Devices.All[0] : null;

        if (pad != null && player.Alive)
        {
            // Right stick looks. Inverted Y is the convention the rest of the project uses.
            camYaw += pad.Look.X * 2.6f * dt;
            camPitch = Mathf.Clamp(camPitch - pad.Look.Y * 2.0f * dt, -1.1f, 0.9f);

            player.AimYaw = camYaw;
            player.Step(dt, Trooper.FromPad(pad, camYaw));
        }

        if (simSeconds > 0f) { Simulate(dt); return; }

        Battle.Step(dt, player);

        PlaceCamera(dt, snap: false);

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
        var from = focus + right * Shoulder;
        var want = from - dir * Boom;

        var hit = GetWorld3D().DirectSpaceState.IntersectRay(
            new PhysicsRayQueryParameters3D
            {
                From = from,
                To = want,
                Exclude = new Godot.Collections.Array<Rid> { player.GetRid() },
            });

        if (hit.Count > 0 && hit.ContainsKey("position"))
            want = from + (want - from).Normalized()
                 * (from.DistanceTo((Vector3)hit["position"]) - 0.3f);

        cam.GlobalPosition = snap ? want : cam.GlobalPosition.Lerp(want, 1f - Mathf.Exp(-18f * dt));
        cam.LookAt(focus, Vector3.Up);
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

    void Capture()
    {
        var img = GetViewport().GetTexture().GetImage();
        img.SavePng(shotPath);
        GD.Print($"wrote {shotPath}");
        GetTree().Quit();
    }
}
