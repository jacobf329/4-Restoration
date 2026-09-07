using Godot;

namespace HitboxClone;

/// <summary>The game's palette. Minimalist and high-contrast, in the spirit of the original.</summary>
public static class Pal
{
    public static readonly Color Bg = new(0.067f, 0.078f, 0.098f);
    public static readonly Color Panel = new(0.114f, 0.129f, 0.157f);
    public static readonly Color PanelHi = new(0.161f, 0.184f, 0.220f);
    public static readonly Color Text = new(0.925f, 0.937f, 0.957f);
    public static readonly Color TextDim = new(0.545f, 0.580f, 0.639f);
    public static readonly Color Accent = new(0.290f, 0.780f, 0.980f);
    public static readonly Color Ready = new(0.290f, 0.850f, 0.510f);
    public static readonly Color Warn = new(0.980f, 0.706f, 0.290f);
    public static readonly Color Danger = new(0.937f, 0.353f, 0.373f);

    /// <summary>Player colours, in seat order. Four, because there are four lobby seats.</summary>
    public static readonly Color[] Players =
    {
        new(0.290f, 0.780f, 0.980f),   // cyan
        new(0.980f, 0.408f, 0.400f),   // red
        new(0.478f, 0.878f, 0.443f),   // green
        new(0.980f, 0.780f, 0.310f),   // amber
    };

    /// <summary>
    /// A colour for any fighter on the roster, not just the first four.
    /// </summary>
    /// <remarks>
    /// The four seat colours come first so a human always wears the colour their lobby card wore.
    /// Beyond that the hue is walked by the golden angle, which is the standard trick for spreading
    /// an arbitrary number of hues: successive values never cluster, however many you ask for.
    /// Cycling the four instead would put two identical fighters on the same screen, which in a
    /// free-for-all is the one thing a colour has to prevent.
    /// </remarks>
    public static Color FighterColour(int index)
    {
        if (index < Players.Length) return Players[index];

        const float GoldenAngle = 137.507764f;
        float hue = Mathf.PosMod(index * GoldenAngle, 360f) / 360f;

        // Held well off both white and black so it reads against pale arenas and in shadow alike.
        return Color.FromHsv(hue, 0.62f, 0.95f);
    }

    /// <summary>
    /// Team colours. Blue against orange rather than red against green, which is the one pairing
    /// a red-green colourblind player cannot separate — and telling friend from foe is the whole
    /// job of these two colours.
    /// </summary>
    public static readonly Color[] Teams =
    {
        new(0.259f, 0.596f, 0.980f),
        new(0.980f, 0.541f, 0.220f),
    };

    public static string TeamName(int team) => team == 0 ? "BLUE" : "ORANGE";
}

/// <summary>
/// Immediate-mode drawing over a <see cref="CanvasItem"/>.
///
/// The UI is drawn rather than built from Control nodes on purpose: Godot's Control focus system
/// is single-focus per viewport and cannot express four pads driving four lobby slots at once.
/// Drawing directly also makes the screenshot harness trivial — pose a screen, draw, capture.
/// </summary>
public sealed class UiPainter
{
    readonly CanvasItem ci;
    readonly Font font;

    public Vector2 Size { get; private set; }

    public UiPainter(CanvasItem ci, Font font, Vector2 size)
    {
        this.ci = ci;
        this.font = font;
        Size = size;
    }

    public void Resize(Vector2 size) => Size = size;

    public void Clear(Color c) => ci.DrawRect(new Rect2(Vector2.Zero, Size), c);

    /// <summary>
    /// Draw a texture filling the screen, cropped rather than squashed.
    ///
    /// Cover-fit rather than stretch: a menu backdrop is a photograph of a place, and a place with
    /// the wrong aspect ratio reads as broken in a way a cropped one never does. The overflow is
    /// centred, so whatever the artist put in the middle survives every window shape.
    /// </summary>
    public void TextureCover(Texture2D tex, float alpha = 1f)
    {
        Vector2 src = tex.GetSize();
        if (src.X <= 0f || src.Y <= 0f) return;

        float scale = MathF.Max(Size.X / src.X, Size.Y / src.Y);
        Vector2 drawn = src * scale;

        ci.DrawTextureRect(tex, new Rect2((Size - drawn) * 0.5f, drawn), false,
                           new Color(1f, 1f, 1f, alpha));
    }

    /// <summary>
    /// A vertical gradient wash, drawn as horizontal bands.
    ///
    /// The immediate-mode painter has no gradient primitive and this needs no shader — twenty-odd
    /// bands across a screen height are indistinguishable from a smooth ramp at this alpha.
    /// </summary>
    public void VerticalWash(float y, float h, Color top, Color bottom, int bands = 24)
    {
        if (h <= 0f) return;

        float step = h / bands;

        for (int i = 0; i < bands; i++)
        {
            float t = (i + 0.5f) / bands;
            ci.DrawRect(new Rect2(0f, y + i * step, Size.X, step + 1f), top.Lerp(bottom, t));
        }
    }

    public void Rect(float x, float y, float w, float h, Color c)
        => ci.DrawRect(new Rect2(x, y, w, h), c);

    public void RectOutline(float x, float y, float w, float h, Color c, float thickness = 2f)
        => ci.DrawRect(new Rect2(x, y, w, h), c, false, thickness);

    public Vector2 Measure(string s, int size)
        => font.GetStringSize(s, HorizontalAlignment.Left, -1, size);

    /// <summary>Draws with <paramref name="y"/> as the text's top edge, not its baseline.</summary>
    public void Text(string s, float x, float y, int size, Color c)
        => ci.DrawString(font, new Vector2(x, y + font.GetAscent(size)), s,
                         HorizontalAlignment.Left, -1, size, c);

    public void TextCentered(string s, float cx, float y, int size, Color c)
        => Text(s, cx - Measure(s, size).X * 0.5f, y, size, c);

    public void TextRight(string s, float rx, float y, int size, Color c)
        => Text(s, rx - Measure(s, size).X, y, size, c);

    /// <summary>
    /// Draws text broken across lines to fit <paramref name="width"/>, and returns the height used.
    ///
    /// Greedy word wrapping, measured against the real font rather than guessed from a character
    /// count — the font is proportional, so "WWW" and "iii" are nothing like the same width and a
    /// character budget overflows on one and wastes half the box on the other. A single word longer
    /// than the box is left to overhang rather than broken: hyphenating a name is worse than a line
    /// that runs a little wide, and at these sizes it does not happen.
    /// </summary>
    public float TextWrapped(string s, float x, float y, float width, int size, Color c)
    {
        float line = Measure("Ag", size).Y;
        float used = 0f;
        string current = "";

        void Flush()
        {
            if (current.Length == 0) return;
            Text(current, x, y + used, size, c);
            used += line;
            current = "";
        }

        foreach (string word in s.Split(' '))
        {
            string next = current.Length == 0 ? word : current + " " + word;

            if (current.Length > 0 && Measure(next, size).X > width) Flush();

            current = current.Length == 0 ? word : current + " " + word;
        }

        Flush();
        return used;
    }

    /// <summary>A filled panel with a subtle border, the base of every menu surface.</summary>
    public void Panel(float x, float y, float w, float h, Color fill, Color? border = null)
    {
        Rect(x, y, w, h, fill);
        if (border.HasValue) RectOutline(x, y, w, h, border.Value);
    }

    /// <summary>
    /// The persistent hint bar along the bottom. Every screen draws one — it is how the
    /// "no dead ends" rule stays visible to the player rather than merely being true.
    /// </summary>
    public void HintBar(params (string button, string label)[] hints)
    {
        const int FontSize = 17;
        float h = 46f;
        float y = Size.Y - h;
        Rect(0, y, Size.X, h, Pal.Panel);

        float x = 32f;
        foreach (var (button, label) in hints)
        {
            var bs = Measure(button, FontSize);
            Panel(x - 8f, y + 11f, bs.X + 16f, 24f, Pal.PanelHi);
            Text(button, x, y + 13f, FontSize, Pal.Accent);
            x += bs.X + 16f;

            Text(label, x, y + 13f, FontSize, Pal.TextDim);
            x += Measure(label, FontSize).X + 34f;
        }
    }
}
