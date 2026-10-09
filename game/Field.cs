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
    Trooper player = null!;
    Camera3D cam = null!;

    float camYaw = -Mathf.Pi * 0.5f;
    float camPitch = -0.18f;

    /// <summary>Over the right shoulder, the way the game this clones frames a soldier.</summary>
    const float Boom = 4.6f;
    const float Shoulder = 0.9f;

    /// <summary>Screenshot mode: write a PNG after N frames and quit. See --shots.</summary>
    string? shotPath;
    int shotFrames = 30;
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
        }

        Station = new Station();
        BuildVisuals();

        player = new Trooper { Team = 0 };
        AddChild(player);
        player.GlobalPosition = startAt ?? Station.HomeSpawn[0];

        cam = new Camera3D { Current = true, Fov = 78f, Far = 600f };
        AddChild(cam);
        PlaceCamera(0f, snap: true);

        AddChild(Graphics.BuildSun());
        AddChild(Graphics.BuildFill());
        AddChild(Graphics.BuildEnvironment(fog: true, reach: Station.HalfSpan * 2f));

        SetPhysicsProcess(true);
    }

    Vector3? startAt;

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

        if (pad != null)
        {
            // Right stick looks. Inverted Y is the convention the rest of the project uses.
            camYaw += pad.Look.X * 2.6f * dt;
            camPitch = Mathf.Clamp(camPitch - pad.Look.Y * 2.0f * dt, -1.1f, 0.9f);

            player.Step(dt, pad, camYaw);
        }

        PlaceCamera(dt, snap: false);

        if (player.GlobalPosition.Y < Station.KillFloor)
            player.GlobalPosition = Station.HomeSpawn[player.Team];

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

    void Capture()
    {
        var img = GetViewport().GetTexture().GetImage();
        img.SavePng(shotPath);
        GD.Print($"wrote {shotPath}");
        GetTree().Quit();
    }
}
