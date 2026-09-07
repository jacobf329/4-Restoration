using System;
using Godot;

namespace HitboxClone;

/// <summary>
/// Shared menu furniture: the background, the header and the section rules.
///
/// It lives in one place so every screen is framed identically. Before this each screen drew its
/// own flat fill and a bare title, and the menus read as a list of options rather than as a game.
///
/// The motif is the same boxes the arenas are built from, drifting slowly behind the content — it
/// costs nothing, needs no art, and ties the menus to what the game actually looks like.
/// </summary>
public static class Chrome
{
    /// <summary>Seconds since launch, advanced by the app. Drives the background drift.</summary>
    public static float Clock { get; private set; }

    public static void Tick(float dt) => Clock += dt;

    readonly struct Drifter
    {
        public readonly float X, Y, Size, Speed, Alpha;
        public readonly bool Filled;

        public Drifter(float x, float y, float size, float speed, float alpha, bool filled)
        {
            X = x; Y = y; Size = size; Speed = speed; Alpha = alpha; Filled = filled;
        }
    }

    // Fixed rather than random: a menu background that reshuffles every time you open a screen
    // is distracting, and this way the layout is the same every run.
    static readonly Drifter[] drifters =
    {
        new(0.08f, 0.18f, 190f, 7f, 0.055f, true),
        new(0.72f, 0.10f, 260f, -5f, 0.045f, false),
        new(0.86f, 0.62f, 150f, 9f, 0.060f, true),
        new(0.20f, 0.74f, 320f, -4f, 0.035f, false),
        new(0.52f, 0.40f, 110f, 6f, 0.050f, false),
        new(0.34f, 0.06f, 130f, -8f, 0.040f, true),
        new(0.64f, 0.84f, 200f, 5f, 0.045f, true),
    };

    /// <summary>
    /// Background for a screen that has a painted backdrop, falling back to the procedural one.
    ///
    /// The scrim is the whole job here. These images are dark but they are not *empty* — banners,
    /// lit panels, mech silhouettes — and menu text laid straight over them is unreadable wherever
    /// it happens to cross something bright. So the image is dimmed overall, washed darker toward
    /// the top where the header sits, and given a heavier column down the left where the rows live.
    /// What survives is the middle and right of the frame, which is where these compositions put
    /// their subject anyway.
    /// </summary>
    public static void Background(UiPainter p, string backdrop)
    {
        if (MenuBackdrop.Get(backdrop) is not { } tex)
        {
            Background(p);
            return;
        }

        p.Clear(Pal.Bg);
        p.TextureCover(tex, 0.95f);

        // A light overall dim only. These backdrops are already dark; the first pass laid nearly
        // half a stop of black over the whole frame on top of that and turned them to mud.
        p.Rect(0f, 0f, p.Size.X, p.Size.Y, new Color(0.03f, 0.04f, 0.06f, 0.26f));

        // Heavier at the top, under the header.
        p.VerticalWash(0f, p.Size.Y * 0.30f,
                       new Color(0.02f, 0.03f, 0.05f, 0.70f),
                       new Color(0.02f, 0.03f, 0.05f, 0f));

        // And along the bottom, under the hint bar.
        p.VerticalWash(p.Size.Y * 0.80f, p.Size.Y * 0.20f,
                       new Color(0.02f, 0.03f, 0.05f, 0f),
                       new Color(0.02f, 0.03f, 0.05f, 0.82f));

        CentreScrim(p);
    }

    /// <summary>
    /// A soft dark band down the middle, where the menus actually sit.
    ///
    /// The first version darkened the left half, on the assumption that rows are left-aligned. They
    /// are not — the title, mode select and options screens all centre their menu block, so the
    /// scrim was protecting empty frame and leaving the text over whatever the art put in the
    /// middle. Fading out toward both edges leaves the sides of the picture visible, which is where
    /// these compositions keep their banners.
    /// </summary>
    static void CentreScrim(UiPainter p)
    {
        const int Steps = 28;
        float band = p.Size.X * 0.72f;
        float x0 = (p.Size.X - band) * 0.5f;
        float step = band / Steps;

        for (int i = 0; i < Steps; i++)
        {
            // Distance from the middle of the band, 0 at centre and 1 at either edge.
            float t = MathF.Abs((i + 0.5f) / Steps - 0.5f) * 2f;
            float a = 0.42f * (1f - t) * (1f - t);

            p.Rect(x0 + i * step, 0f, step + 1f, p.Size.Y, new Color(0.02f, 0.03f, 0.05f, a));
        }
    }

    public static void Background(UiPainter p)
    {
        p.Clear(Pal.Bg);

        // A wide diagonal band, lighter than the base, giving the empty middle of a menu some
        // structure without competing with the text.
        DiagonalBand(p);

        foreach (var d in drifters)
        {
            float drift = MathF.Sin(Clock * 0.11f + d.X * 9f) * d.Speed * 4f;
            float x = d.X * p.Size.X + drift;
            float y = d.Y * p.Size.Y - drift * 0.6f;

            var c = new Color(Pal.Accent.R, Pal.Accent.G, Pal.Accent.B, d.Alpha);

            if (d.Filled) p.Rect(x, y, d.Size, d.Size * 0.62f, c);
            else p.RectOutline(x, y, d.Size, d.Size * 0.62f, c, 3f);
        }
    }

    /// <summary>Two long parallel slabs crossing the screen at a shallow angle.</summary>
    static void DiagonalBand(UiPainter p)
    {
        const int Steps = 26;
        float h = p.Size.Y / Steps;

        for (int i = 0; i < Steps; i++)
        {
            float t = i / (float)Steps;
            float y = t * p.Size.Y;

            // Shear each row a little further across, which draws a diagonal edge out of
            // axis-aligned rectangles — the same trick the scope mask uses.
            float x = p.Size.X * (0.14f + t * 0.30f);
            float w = p.Size.X * 0.30f;

            p.Rect(x, y, w, h + 1f, new Color(1f, 1f, 1f, 0.012f));
            p.Rect(x + w + p.Size.X * 0.05f, y, w * 0.35f, h + 1f, new Color(1f, 1f, 1f, 0.008f));
        }
    }

    /// <summary>Screen title with an accent rule beneath it. Returns the y to start content at.</summary>
    public static float Header(UiPainter p, string title, string? subtitle = null)
    {
        const float X = 56f;
        float y = 44f;

        p.Text(title, X, y, 34, Pal.Text);
        y += 44f;

        p.Rect(X, y, 74f, 3f, Pal.Accent);
        p.Rect(X + 82f, y, 260f, 3f, Pal.PanelHi);
        y += 14f;

        if (subtitle != null)
        {
            p.Text(subtitle, X, y, 18, Pal.TextDim);
            y += 30f;
        }

        return y + 20f;
    }

    /// <summary>A labelled divider for grouping rows on a dense screen.</summary>
    public static void SectionRule(UiPainter p, string label, float x, float y, float width)
    {
        p.Text(label, x, y, 16, Pal.Accent);
        float lx = x + p.Measure(label, 16).X + 14f;
        p.Rect(lx, y + 10f, MathF.Max(0f, width - (lx - x)), 1f, Pal.PanelHi);
    }
}
