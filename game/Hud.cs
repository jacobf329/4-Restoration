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

        // The two reinforcement counts, as bars that visibly drain. A number alone does not tell
        // you that you are losing; a bar shrinking next to a bar that is not does.
        Side(battle, 0, new Vector2(w * 0.5f - 150f, 24f), Blue, -1f);
        Side(battle, 1, new Vector2(w * 0.5f + 150f, 24f), Orange, 1f);

        // The post row, in map order, so it reads as the station rather than as a list.
        float x = w * 0.5f - (battle.Station.Posts.Count - 1) * 60f * 0.5f;

        for (int i = 0; i < battle.Station.Posts.Count; i++)
        {
            var post = battle.Station.Posts[i];
            var tint = post.Owner switch { 0 => Blue, 1 => Orange, _ => Neutral };

            DrawRect(new Rect2(x - 22f, 86f, 44f, 8f), tint);
            DrawString(font, new Vector2(x - 34f, 116f), post.Name,
                       HorizontalAlignment.Center, 68f, 13, tint);

            x += 60f;
        }

        if (!Field.PlayerAlive)
            DrawString(font, new Vector2(w * 0.5f - 120f, screen.Y * 0.5f), "DOWN",
                       HorizontalAlignment.Center, 240f, 34, new Color(1f, 0.4f, 0.4f));
    }

    void Side(Battle battle, int team, Vector2 at, Color tint, float dir)
    {
        const float Wide = 240f, Tall = 18f;

        float frac = battle.Tickets[team] / Battle.StartTickets;
        float left = dir < 0f ? at.X - Wide : at.X;

        DrawRect(new Rect2(left, at.Y, Wide, Tall), new Color(0f, 0f, 0f, 0.45f));

        // Drains from the middle outward, so both bars shrink toward their own side.
        float lit = Wide * frac;
        DrawRect(new Rect2(dir < 0f ? at.X - lit : at.X, at.Y, lit, Tall), tint);

        DrawString(font, new Vector2(left, at.Y + Tall + 20f),
                   $"{Mathf.CeilToInt(battle.Tickets[team])}   {battle.Held(team)} posts",
                   dir < 0f ? HorizontalAlignment.Left : HorizontalAlignment.Right, Wide, 15, tint);
    }
}
