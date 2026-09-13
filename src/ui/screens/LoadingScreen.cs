using System.Collections.Generic;
using Godot;

namespace HitboxClone;

/// <summary>
/// What the player looks at while a match is being built.
///
/// The problem it solves is not slowness, it is silence. Building a siege map is seconds of work -
/// a thousand metres of geometry and a navigation graph over two hundred thousand cells - and all
/// of it used to happen inside the single frame that pressed Start. The lobby stayed on screen,
/// frozen, with the cursor still on the row the player had just chosen, and the game looked
/// exactly like a game that had hung.
///
/// The fix is not to make it faster. It is to draw something first. Godot keeps presenting the
/// last frame it was given, so one drawn frame saying LOADING covers a freeze of any length - and
/// because <see cref="MatchScreen.LoadStages"/> hands its work back a piece at a time, the bar
/// actually moves rather than sitting at a made-up percentage.
///
/// A screen and not an overlay, so it owns the stack while it runs: input during a build would
/// reach a match that does not exist yet.
/// </summary>
public sealed class LoadingScreen : UiScreen
{
    public override string Title => "LOADING";

    /// <summary>Silence rather than the menu bed, so the arena's own cue starts clean.</summary>
    public override string MusicCue => Music.Playing;

    readonly MatchScreen next;
    IEnumerator<string>? stages;

    string stage = "Preparing";
    int done;
    float clock;

    /// <summary>
    /// Roughly how many stages a build yields, used only to draw the bar.
    ///
    /// An estimate on purpose. The real count depends on the roster and the mode, and asking the
    /// iterator for its length would mean running the build to find out how long the build is. A
    /// bar that fills to nine tenths and waits is honest about what it knows; a bar that claims to
    /// know exactly is usually lying anyway.
    /// </summary>
    const int Estimate = 17;

    public LoadingScreen(MatchScreen next)
    {
        this.next = next;
    }

    protected override void UpdateScreen(float dt, IReadOnlyList<InputDevice> devices)
    {
        clock += dt;

        // Nothing on the first update. This is the whole point of the screen: the stack draws
        // after it updates, so the first stage must not run until a frame carrying the word
        // LOADING has been put on the glass.
        if (stages == null)
        {
            if (clock < 0.02f) return;
            stages = next.LoadStages().GetEnumerator();
            return;
        }

        // One stage a frame. Several would be faster overall and would also be back to a freeze,
        // which is the thing being fixed.
        if (stages.MoveNext())
        {
            stage = stages.Current;
            done++;
            return;
        }

        stages.Dispose();
        stages = null;

        // Replaced rather than pushed over: backing out of a match should reach the lobby, not a
        // loading screen that would immediately try to build a second one.
        Stack.Pop();
        Stack.Push(next);
    }

    /// <summary>A build cannot be cancelled part way through, so back does nothing here.</summary>
    protected override bool OnBack(InputDevice d) => true;

    public override void Draw(UiPainter p)
    {
        p.Clear(Pal.Bg);

        float cx = p.Size.X * 0.5f;
        float cy = p.Size.Y * 0.5f;

        p.TextCentered("LOADING", cx, cy - 96f, 52, Pal.Text);
        p.TextCentered(stage.ToUpperInvariant(), cx, cy - 44f, 22, Pal.TextDim);

        // The bar. Width is fixed so the fill reads as progress rather than as the bar itself
        // growing, which at a glance look the same and mean opposite things.
        const float w = 620f, h = 16f;
        float x = cx - w * 0.5f, y = cy;

        p.Rect(x, y, w, h, Pal.Panel);

        float filled = MathU.Clamp01(done / (float)Estimate);
        p.Rect(x, y, w * filled, h, Pal.Accent);
        p.RectOutline(x, y, w, h, Pal.PanelHi);

        // A moving pip under the bar. A progress bar that stalls on one long stage - the navigation
        // graph is most of the wait on the big maps - is indistinguishable from a bar that has
        // stopped, and this is the only thing on screen that can say otherwise.
        float t = clock * 1.6f % 1f;
        p.Rect(x + (w - 26f) * t, y + h + 10f, 26f, 4f, Pal.TextDim);

        p.TextCentered("Building the arena. This takes a moment on the larger maps.",
                       cx, cy + 64f, 19, Pal.TextDim);
    }
}
