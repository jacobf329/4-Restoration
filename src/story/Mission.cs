using System.Collections.Generic;
using Godot;

namespace HitboxClone;

/// <summary>
/// A scripted scene running inside a live match.
///
/// The seam between story mode and the game. <see cref="MatchScreen"/> knows how to run an arena,
/// place cameras, split the screen and draw a HUD, and none of that wants rewriting for a story —
/// so a mission is a small object the match screen ticks and draws, rather than a second screen
/// that reimplements two thousand lines of it.
///
/// It owns no input. A mission watches where the player *is* and what they have done, and that is
/// deliberate: Act I is a walk through a town while somebody talks to you, and a scene that stops
/// dead waiting for a button press every line is a slideshow with a walk button.
/// </summary>
public interface IMission
{
    /// <summary>What the player is being asked to do, for the HUD. Empty for none.</summary>
    string Objective { get; }

    /// <summary>The line currently being spoken, or null between them.</summary>
    Beat? Speaking { get; }

    /// <summary>True once the scene is over and the screen should hand back to the campaign.</summary>
    bool Complete { get; }

    /// <summary>
    /// Whether to draw the combat HUD over this scene.
    ///
    /// On the mission rather than fixed for story mode, because later acts have fights in them and
    /// will want it back. Act I does not: an eighteen-year-old walking to the green does not have
    /// a health bar, three cooldown gauges and a tactical minimap, and putting them there says
    /// what the town is before the act has finished not saying it.
    /// </summary>
    bool ShowsCombatHud { get; }

    void Step(float dt, Match match);
}

/// <summary>
/// One stop on a walk: somewhere to be, and what is said when you get there.
/// </summary>
public sealed class Stage
{
    /// <summary>Where the player has to reach. Ignored when <see cref="Anywhere"/>.</summary>
    public Vector3 At;

    /// <summary>True for a stage that plays where the player is standing, with nowhere to go.</summary>
    public bool Anywhere;

    /// <summary>How close counts, in metres.</summary>
    public float Radius = 7f;

    /// <summary>Shown on the HUD while this stage is waiting to be reached.</summary>
    public string Objective = "";

    public Beat[] Beats = System.Array.Empty<Beat>();
}

/// <summary>
/// A walk-and-talk: reach a place, hear some lines, reach the next place.
///
/// The whole of Act I is this shape, and most of the story probably is. What makes it work rather
/// than being a waypoint chore is that the *lines* are the reward for arriving, so the pace of the
/// scene is set by how fast the player wants to walk — somebody who runs the length of Fairview
/// gets the same scene, faster, and somebody who wanders into the gardens gets to wander.
/// </summary>
public sealed class WalkMission : IMission
{
    readonly Stage[] stages;
    readonly CampaignState state;

    int stage;
    int beat = -1;
    float showing;

    /// <summary>
    /// Seconds a line stays up.
    ///
    /// Scaled by its own length rather than fixed, because a fixed hold makes short lines drag and
    /// long ones unreadable, and Act I has both. The floor is what stops a two-word line flashing
    /// past before it is seen.
    /// </summary>
    static float HoldFor(string line) => Mathf.Clamp(1.4f + line.Length * 0.045f, 2.2f, 7f);

    public WalkMission(CampaignState state, IEnumerable<Stage> stages)
    {
        this.state = state;
        this.stages = new List<Stage>(stages).ToArray();
    }

    public bool Complete { get; private set; }

    /// <summary>A walk has no fight in it and no HUD over it.</summary>
    public bool ShowsCombatHud => false;

    public string Objective
        => Complete || beat >= 0 || stage >= stages.Length ? "" : stages[stage].Objective;

    public Beat? Speaking
        => beat >= 0 && stage < stages.Length && beat < Playable(stages[stage]).Count
            ? Playable(stages[stage])[beat]
            : null;

    /// <summary>
    /// The beats of a stage that this playthrough hears.
    ///
    /// Filtered against the harvest answer every time rather than once at construction, because a
    /// mission outlives the choice in the acts after it and the branch has to be read from the
    /// campaign rather than baked in when the scene was built.
    /// </summary>
    List<Beat> Playable(Stage s)
    {
        var list = new List<Beat>();
        foreach (var b in s.Beats) if (b.PlaysFor(state.Choice)) list.Add(b);
        return list;
    }

    /// <summary>
    /// Where the living humans are.
    ///
    /// The only question a mission asks of a match, which is why the step below takes the answer
    /// rather than the match. It makes the scene testable without a physics world - a Match needs
    /// a scene tree, and building thirteen arenas' worth of one to find out whether a waypoint is
    /// near the school would be a strange way to answer that.
    /// </summary>
    static List<Vector3> People(Match match)
    {
        var at = new List<Vector3>();
        foreach (var p in match.Pawns)
            if (!p.IsBot && p.Alive) at.Add(p.GlobalPosition);
        return at;
    }

    public void Step(float dt, Match match) => Step(dt, People(match));

    /// <summary>Advance the scene with the player standing at one place. For the harness.</summary>
    public void StepForTest(float dt, Vector3 at) => Step(dt, new List<Vector3> { at });

    void Step(float dt, List<Vector3> people)
    {
        if (Complete || stages.Length == 0) return;

        if (stage >= stages.Length) { Complete = true; return; }

        var current = stages[stage];

        // Waiting to be reached.
        if (beat < 0)
        {
            if (!current.Anywhere && !Reached(current, people)) return;

            beat = 0;
            showing = 0f;

            // A stage with nothing to say is a waypoint on the way to one. Fall through rather
            // than stalling on it, or a silent stage is a scene that never ends.
            if (Playable(current).Count == 0) { NextStage(); return; }
            return;
        }

        // Talking.
        var beats = Playable(current);
        showing += dt;
        if (showing < HoldFor(beats[beat].Line)) return;

        showing = 0f;
        beat++;

        if (beat >= beats.Count) NextStage();
    }

    void NextStage()
    {
        stage++;
        beat = -1;
        showing = 0f;
        if (stage >= stages.Length) Complete = true;
    }

    /// <summary>
    /// Whether any living player is standing on this stage.
    ///
    /// Measured flat. A player on a roof directly above the green has reached the green as far as
    /// a conversation is concerned, and a scene that refuses to continue because somebody climbed
    /// something is a scene that punishes the exact curiosity Act I is trying to reward.
    /// </summary>
    static bool Reached(Stage s, List<Vector3> people)
    {
        foreach (var at in people)
        {
            var d = at - s.At;
            if (new Vector2(d.X, d.Z).Length() <= s.Radius) return true;
        }

        return false;
    }

    // ---- harness ----

    /// <summary>Which stage is live, so the harness can watch a scene progress.</summary>
    public int StageForTest => stage;

    public int StageCount => stages.Length;

    /// <summary>Where the player is being sent, or null when the scene is talking.</summary>
    public Vector3? TargetForTest
        => Complete || beat >= 0 || stage >= stages.Length || stages[stage].Anywhere
            ? null
            : stages[stage].At;
}
