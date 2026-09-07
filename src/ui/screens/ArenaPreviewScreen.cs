using System;
using System.Collections.Generic;
using Godot;

namespace HitboxClone;

/// <summary>
/// One arena, viewed from above, with nobody in it.
///
/// Written because a player reported that all four maps felt like the same place, and the
/// screenshot harness gave me no way to check: every capture was a first-person eye-level view from
/// a spawn, which shows the two metres in front of you and tells you nothing about a layout. Four
/// maps could have been identical and the captures would have looked the same either way.
///
/// It builds only the arena — no pawns, no vehicles, no HUD — so what you see is exactly the
/// geometry the layout produced.
/// </summary>
public sealed class ArenaPreviewScreen : UiScreen
{
    public override string Title => Arena.Names[arenaIndex].ToUpperInvariant();

    readonly int arenaIndex;
    readonly Main app;

    SubViewportContainer? container;
    Arena arena = null!;
    bool built;

    public ArenaPreviewScreen(int arenaIndex, Main app)
    {
        this.arenaIndex = arenaIndex;
        this.app = app;
    }

    public override void OnEnter()
    {
        if (built) return;
        built = true;

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

        var root = new Node3D { Name = "Preview" };
        vp.AddChild(root);

        arena = new Arena(arenaIndex);
        arena.Build(root, visuals: true, fog: false);

        // High and tilted rather than straight down. A true plan view flattens every height in the
        // map to nothing, which would hide the exact thing this screen exists to show.
        var cam = new Camera3D
        {
            KeepAspect = Camera3D.KeepAspectEnum.Height,
            Fov = 52f,
            Near = 0.5f,
            Far = 900f,
        };
        vp.AddChild(cam);

        cam.GlobalPosition = new Vector3(0f, 215f, 250f);
        cam.LookAt(new Vector3(0f, 6f, 0f), Vector3.Up);
    }

    public override void OnExit()
    {
        container?.QueueFree();
        container = null;
    }

    protected override void UpdateScreen(float dt, IReadOnlyList<InputDevice> devices) { }

    public override void Draw(UiPainter p)
    {
        var size = p.Size;

        p.Text(Arena.Names[arenaIndex].ToUpperInvariant(), 42f, 54f, 40, Pal.Text);
        p.Text($"Layout {arenaIndex + 1} of {Arena.Names.Length}", 44f, 92f, 20, Pal.TextDim);

        p.TextRight($"{arena.Blocks.Count} blocks", size.X - 42f, 54f, 22, Pal.TextDim);
    }
}
