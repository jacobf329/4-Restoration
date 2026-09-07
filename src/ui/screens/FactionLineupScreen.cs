using System.Collections.Generic;
using Godot;

namespace HitboxClone;

/// <summary>
/// The four faction characters stood side by side at pawn scale.
///
/// Written before wiring the models into gameplay at all. Loading a rigged, animated, textured model
/// through five separate systems — glTF import, texture downscale, height normalisation, animation
/// merge, render layers — and only finding out whether any of it worked by starting a match would
/// be guessing five times over. This looks at them.
///
/// The reference box beside each one is the pawn's real collision capsule dimensions, so any model
/// that has been scaled wrongly is obvious rather than merely suspicious.
/// </summary>
public sealed class FactionLineupScreen : UiScreen
{
    public override string Title => "FACTIONS";

    readonly Main app;
    SubViewportContainer? container;
    readonly List<AnimationPlayer> players = new();
    readonly List<string> notes = new();

    public FactionLineupScreen(Main app) => this.app = app;

    public override void OnEnter()
    {
        var size = app.Ui.GetViewportRect().Size;

        container = new SubViewportContainer
        {
            Stretch = true,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Position = Vector2.Zero,
            Size = size,
        };
        app.ViewportHost.AddChild(container);

        var vp = new SubViewport
        {
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
            HandleInputLocally = false,
            World3D = new World3D(),
            Msaa3D = Graphics.Msaa,
        };
        container.AddChild(vp);

        var root = new Node3D();
        vp.AddChild(root);

        root.AddChild(Graphics.BuildSun());
        root.AddChild(Graphics.BuildFill());
        root.AddChild(Graphics.BuildEnvironment(fog: false));

        // A floor to cast onto, so contact with the ground is visible.
        var floor = new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(24f, 0.4f, 12f) },
            MaterialOverride = Graphics.Surface(new Color(0.34f, 0.38f, 0.46f)),
            Position = new Vector3(0f, -0.2f, 0f),
        };
        root.AddChild(floor);

        for (int i = 0; i < Factions.All.Length; i++)
        {
            var faction = Factions.All[i];
            float x = (i - 1.5f) * 3.4f;

            notes.Add(CharacterModels.Describe(faction));

            // The reference capsule: exactly the pawn's real dimensions, drawn beside each model.
            root.AddChild(new MeshInstance3D
            {
                Mesh = new BoxMesh { Size = new Vector3(Pawn.Radius * 2f, Pawn.Height, Pawn.Radius * 2f) },
                MaterialOverride = Graphics.Hot(faction.Tint, 0.35f),
                Position = new Vector3(x - 1.3f, Pawn.Height * 0.5f, -1.2f),
            });

            if (CharacterModels.Instance(faction, out float sourceHeight) is not { } model) continue;

            var holder = new Node3D { Position = new Vector3(x, 0f, 0f) };
            root.AddChild(holder);
            holder.AddChild(model);

            // Every faction ends up exactly pawn height, whatever it measured in its own units.
            model.Scale = Vector3.One * (Pawn.Height / sourceHeight);
            model.RotationDegrees = new Vector3(0f, 180f, 0f);

            if (CharacterModels.FindPlayer(model) is { } player)
            {
                players.Add(player);

                foreach (var lib in player.GetAnimationLibraryList())
                    foreach (var anim in player.GetAnimationLibrary(lib).GetAnimationList())
                    {
                        player.Play(lib.ToString().Length > 0 ? $"{lib}/{anim}" : anim);
                        player.Advance(0.35f + i * 0.2f);      // offset so they are not in lockstep
                        break;
                    }
            }
        }

        var cam = new Camera3D
        {
            KeepAspect = Camera3D.KeepAspectEnum.Height,
            Fov = 42f,
            Near = 0.05f,
        };
        vp.AddChild(cam);

        cam.GlobalPosition = new Vector3(0f, 1.5f, 8.5f);
        cam.LookAt(new Vector3(0f, 0.95f, 0f), Vector3.Up);
    }

    public override void OnExit()
    {
        container?.QueueFree();
        container = null;
    }

    protected override void UpdateScreen(float dt, IReadOnlyList<InputDevice> devices) { }

    public override void Draw(UiPainter p)
    {
        // A scrim under the header. Grey type over a bright sky is unreadable, and this screen
        // draws straight onto a live 3D view whose brightness is not ours to choose.
        p.Rect(0f, 0f, p.Size.X, 124f, new Color(0.04f, 0.05f, 0.07f, 0.72f));

        p.Text("FACTIONS", 42f, 46f, 34, Pal.Text);
        p.Text("Your faction decides your special. Your class decides how you shoot.",
               44f, 86f, 17, Pal.TextDim);

        float colW = p.Size.X * 0.19f;
        float panelW = colW + 30f;
        float panelY = p.Size.Y * 0.58f;
        float panelH = 214f;

        for (int i = 0; i < Factions.All.Length; i++)
        {
            var f = Factions.All[i];
            float cx = p.Size.X * (0.5f + (i - 1.5f) * 0.20f);

            // A solid panel behind the text, not text laid straight onto the render.
            //
            // This screen began as a diagnostic for the model pipeline and the type was dim grey
            // over whatever the 3D view happened to be — which against a bright sky was very
            // nearly invisible. It is on the title menu now, so it has to read like a menu.
            p.Panel(cx - panelW * 0.5f, panelY, panelW, panelH, Pal.Panel, f.Tint);

            float ty = panelY + 18f;

            p.TextCentered(f.Name.ToUpperInvariant(), cx, ty, 20, f.Tint);
            ty += 26f;
            p.TextCentered(f.Answer, cx, ty, 15, Pal.TextDim);
            ty += 28f;

            p.Rect(cx - panelW * 0.5f + 16f, ty, panelW - 32f, 2f, Pal.PanelHi);
            ty += 12f;

            // The special, spelled out. This is the only place in the game where all four powers
            // can be compared side by side, and a lineup that names four peoples without saying
            // what any of them can do is wasting the one thing it is uniquely good at.
            p.TextCentered(f.SpecialName.ToUpperInvariant(), cx, ty, 18, Pal.Accent);
            ty += 24f;

            foreach (string line in Wrap(p, f.SpecialBlurb, colW, 15))
            {
                p.TextCentered(line, cx, ty, 15, Pal.Text);
                ty += 20f;
            }

            p.TextCentered($"{f.SpecialCooldown:0}s cooldown", cx, ty + 6f, 14, Pal.TextDim);
        }

        // The model-pipeline notes this screen was originally written for. Kept, small, at the
        // bottom: they are the only place a mis-scaled or un-animated character shows up as a
        // number rather than as a thing you have to notice by eye.
        float ny = p.Size.Y - 96f - notes.Count * 20f;

        if (notes.Count > 0)
            p.Rect(30f, ny - 10f, 560f, notes.Count * 20f + 16f,
                   new Color(0.04f, 0.05f, 0.07f, 0.62f));

        foreach (var line in notes)
        {
            p.Text(line, 42f, ny, 14, Pal.TextDim);
            ny += 20f;
        }

        p.HintBar((Glyphs.For(Prompt.Cancel, (InputDevice?)null), "Back"));
    }

    /// <summary>Greedy word wrap to a pixel width.</summary>
    static List<string> Wrap(UiPainter p, string text, float maxW, int size)
    {
        var lines = new List<string>();
        string line = "";

        foreach (string word in text.Split(' '))
        {
            string candidate = line.Length == 0 ? word : line + " " + word;
            if (line.Length > 0 && p.Measure(candidate, size).X > maxW)
            {
                lines.Add(line);
                line = word;
            }
            else line = candidate;
        }

        if (line.Length > 0) lines.Add(line);
        return lines;
    }
}
