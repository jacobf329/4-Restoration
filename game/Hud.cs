using Godot;

namespace Battlefront;

/// <summary>
/// The only readout the game has: who holds what, and how long each side has left.
///
/// Reinforcements and posts, in that order, because those are the two numbers conquest is played
/// on and everything else is noise until the thing is fun. Drawn rather than laid out in scene
/// nodes so a change is one edit in one file.
/// </summary>
public partial class Hud : Control
{
    public Field Field = null!;

    static readonly Color Blue = new(0.42f, 0.68f, 1f);
    static readonly Color Orange = new(1f, 0.60f, 0.25f);
    static readonly Color Neutral = new(0.78f, 0.78f, 0.80f);

    Font font = null!;

    public override void _Ready()
    {
        font = ThemeDB.FallbackFont;
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
        SetProcess(true);
    }

    public override void _Process(double delta) => QueueRedraw();

    public override void _Draw()
    {
        if (Field?.Battle is not { } battle) return;

        // The viewport, not Size: a Control drawing before its anchors have been resolved has a
        // Size of zero, which put the blue bar off the left edge and stacked the post labels in
        // the corner.
        var screen = GetViewportRect().Size;
        float w = screen.X;

        // Everything below is in pixels at 1080p, scaled to whatever the window actually is.
        //
        // The engine used to do this: project.godot asks for canvas_items stretching against a
        // 1920x1080 base, which scales the whole 2D layer. Field now turns that off, because
        // that same setting was deriving the 3D viewport from the base ASPECT and rendering the
        // map at a reduced resolution on any window that is not 16:9. Turning it off fixes the
        // 3D and leaves this drawing at fixed pixel sizes, which would be a HUD that shrinks to
        // nothing on a big display - so it does its own scaling, off the viewport it is
        // actually being drawn into.
        float k = Mathf.Clamp(screen.Y / 1080f, 0.5f, 3f);

        // The two reinforcement counts, as bars that visibly drain. A number alone does not tell
        // you that you are losing; a bar shrinking next to a bar that is not does.
        Side(battle, 0, new Vector2(w * 0.5f - 150f * k, 24f * k), Blue, -1f, k);
        Side(battle, 1, new Vector2(w * 0.5f + 150f * k, 24f * k), Orange, 1f, k);

        // The post row, in map order, so it reads as the station rather than as a list.
        float x = w * 0.5f - (battle.Station.Posts.Count - 1) * 60f * k * 0.5f;

        for (int i = 0; i < battle.Station.Posts.Count; i++)
        {
            var post = battle.Station.Posts[i];
            var tint = post.Owner switch { 0 => Blue, 1 => Orange, _ => Neutral };

            DrawRect(new Rect2(x - 22f * k, 86f * k, 44f * k, 8f * k), tint);
            DrawString(font, new Vector2(x - 34f * k, 116f * k), post.Name,
                       HorizontalAlignment.Center, 68f * k, Mathf.RoundToInt(13 * k), tint);

            x += 60f * k;
        }

        var mid = screen * 0.5f;

        // The crosshair. Four ticks round a gap rather than a dot, so it stays readable against
        // both the near-black deck and the near-white hull.
        if (Field.PlayerAlive && battle.Winner < 0)
        {
            var ink = new Color(1f, 1f, 1f, 0.85f);

            foreach (var step in new[] { Vector2.Right, Vector2.Left, Vector2.Up, Vector2.Down })
                DrawLine(mid + step * 7f * k, mid + step * 15f * k, ink, 2f * k);

            // The hit marker: four diagonals that flare out of the crosshair when a shot lands.
            //
            // The one piece of feedback a third-person shooter cannot do without. A soldier
            // thirty metres away is a hundred pixels tall and their health is not drawn, so
            // without this there is no difference on screen between a shot that hit and a shot
            // that missed - and a gun you cannot tell is working is a gun that is not working,
            // as far as the person holding it is concerned.
            if (battle.HitMark > 0f)
            {
                float t = battle.HitMark / Battle.HitMarkTime;
                var hot = new Color(1f, 0.92f, 0.55f, 0.35f + t * 0.65f);

                foreach (var step in new[] { new Vector2(1f, 1f), new Vector2(-1f, 1f),
                                             new Vector2(1f, -1f), new Vector2(-1f, -1f) })
                {
                    var d = step.Normalized();
                    DrawLine(mid + d * (6f + (1f - t) * 5f) * k,
                             mid + d * (14f + (1f - t) * 5f) * k, hot, 2.5f * k);
                }
            }
        }

        // Health, bottom left, where it is out of the way and still in peripheral vision.
        if (Field.PlayerHealth is { } health)
        {
            DrawRect(new Rect2(40f * k, screen.Y - 64f * k, 240f * k, 16f * k),
                     new Color(0f, 0f, 0f, 0.45f));
            DrawRect(new Rect2(40f * k, screen.Y - 64f * k, 240f * k * health, 16f * k),
                     health > 0.35f ? new Color(0.55f, 0.85f, 0.55f) : new Color(1f, 0.42f, 0.38f));
        }

        if (battle.Winner >= 0)
        {
            var tint = battle.Winner == 0 ? Blue : Orange;
            string who = battle.Winner == 0 ? "BLUE" : "ORANGE";

            DrawString(font, new Vector2(mid.X - 300f * k, mid.Y - 10f * k),
                       $"{who} HOLDS THE STATION",
                       HorizontalAlignment.Center, 600f * k, Mathf.RoundToInt(40 * k), tint);
        }
        else if (!Field.PlayerAlive)
        {
            DrawString(font, new Vector2(mid.X - 120f * k, mid.Y), "DOWN",
                       HorizontalAlignment.Center, 240f * k, Mathf.RoundToInt(34 * k),
                       new Color(1f, 0.4f, 0.4f));
        }
    }

    void Side(Battle battle, int team, Vector2 at, Color tint, float dir, float k)
    {
        float wide = 240f * k, tall = 18f * k;

        float frac = battle.Tickets[team] / Battle.StartTickets;
        float left = dir < 0f ? at.X - wide : at.X;

        DrawRect(new Rect2(left, at.Y, wide, tall), new Color(0f, 0f, 0f, 0.45f));

        // Drains from the middle outward, so both bars shrink toward their own side.
        float lit = wide * frac;
        DrawRect(new Rect2(dir < 0f ? at.X - lit : at.X, at.Y, lit, tall), tint);

        DrawString(font, new Vector2(left, at.Y + tall + 20f * k),
                   $"{Mathf.CeilToInt(battle.Tickets[team])}   {battle.Held(team)} posts",
                   dir < 0f ? HorizontalAlignment.Left : HorizontalAlignment.Right,
                   wide, Mathf.RoundToInt(15 * k), tint);
    }
}
