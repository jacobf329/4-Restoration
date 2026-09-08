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

    /// <summary>
    /// A meter the scene wants drawn, or null for none.
    ///
    /// On the mission rather than on the screen because only the mission knows what it is
    /// measuring. It exists for one reason: a decision made by standing somewhere has to be
    /// visibly in progress, or a player who wandered into a bay and out again never learns that
    /// the game was about to accept an answer from him.
    /// </summary>
    MissionGauge? Gauge { get; }

    void Step(float dt, Match match);
}

/// <summary>A labelled bar, filling from 0 to 1. The whole of what a scene can put on the HUD.</summary>
public readonly struct MissionGauge
{
    public readonly string Label;
    public readonly float Progress;
    public readonly Color Tint;

    public MissionGauge(string label, float progress, Color tint)
    {
        Label = label;
        Progress = Mathf.Clamp(progress, 0f, 1f);
        Tint = tint;
    }
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

    /// <summary>A walk measures nothing.</summary>
    public MissionGauge? Gauge => null;

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

/// <summary>
/// One delegation's floor in a chamber scene: what they say, and what standing there means.
/// </summary>
public sealed class Station
{
    /// <summary>Where they are, and how much floor counts as theirs.</summary>
    public Vector3 At;
    public float Radius = 9f;

    /// <summary>Who this is, for the objective text and the gauge.</summary>
    public FactionDef Host = Factions.Vessels;

    /// <summary>Their case, heard once when the player first walks in.</summary>
    public Beat[] Beats = System.Array.Empty<Beat>();

    /// <summary>Which delegation this is, recorded when the player sides with them.</summary>
    public Delegation Is;

    /// <summary>The answer that siding with them gives. Two of the four share each answer.</summary>
    public HarvestChoice Answer;

    /// <summary>What is said once he has stood there long enough for it to count.</summary>
    public Beat[] Commit = System.Array.Empty<Beat>();
}

/// <summary>
/// A scene in a room with several parties in it: hear everyone, then go and stand with one.
///
/// The reason this exists rather than another <see cref="WalkMission"/> is the shape of Act II. A
/// walk is a sequence somebody is led along, and being led is precisely what Act II must not be —
/// four civilisations are arguing about his body and the only fact none of them can argue with is
/// that it is his. So the four cases are heard in whatever order he chooses to hear them, and then
/// the question is answered by walking into one of the four floors rather than by picking a line
/// off a menu.
///
/// Making the choice a *place* is the whole point. A menu asks which option you prefer; a room asks
/// who you are willing to go and stand next to while the other three watch. They are not the same
/// question, and the second one is the one this story is about.
/// </summary>
public sealed class ChamberMission : IMission
{
    /// <summary>How long he has to hold a delegation's floor before it counts as an answer.</summary>
    public const float CommitSeconds = 3.0f;

    /// <summary>
    /// How fast the meter falls back when he steps off.
    ///
    /// Slower than it fills, on purpose. Backing out of the most important decision in the game
    /// should not be instant — a player who takes a step away and then steps back has not started
    /// again, he has hesitated, and the meter should say so.
    /// </summary>
    public const float CommitDecay = 0.6f;

    enum Phase { Opening, Hearing, Closing, Committing, Sealing }

    readonly CampaignState state;
    readonly Station[] stations;
    readonly Beat[] opening;
    readonly Beat[] closing;

    readonly bool[] heard;

    Phase phase = Phase.Opening;

    /// <summary>The delegation currently talking, or -1 for the room at large.</summary>
    int talking = -1;

    List<Beat> lines = new();
    int beat;
    float showing;

    /// <summary>How far into committing to <see cref="leaning"/> he is, in seconds.</summary>
    float dwell;
    int leaning = -1;

    public ChamberMission(CampaignState state, Beat[] opening, Station[] stations, Beat[] closing)
    {
        this.state = state;
        this.opening = opening;
        this.stations = stations;
        this.closing = closing;
        heard = new bool[stations.Length];

        Say(opening, -1);
    }

    public bool Complete { get; private set; }

    /// <summary>
    /// No combat HUD. There is nothing in this room to shoot and nothing in it that can hurt him,
    /// and a health bar over a hearing says the wrong thing about what kind of scene it is.
    /// </summary>
    public bool ShowsCombatHud => false;

    public Beat? Speaking => beat < lines.Count ? lines[beat] : null;

    public string Objective
    {
        get
        {
            if (Complete || beat < lines.Count) return "";

            return phase switch
            {
                Phase.Hearing => $"Hear them out  ({Heard}/{stations.Length})",
                Phase.Committing => "Go and stand with one of them",
                _ => "",
            };
        }
    }

    /// <summary>
    /// The commit meter, shown only while he is actually standing on somebody's floor.
    ///
    /// Hidden at zero rather than drawn empty. An empty bar on the HUD for the whole of the final
    /// phase reads as a timer running out, and nothing here is running out — the room will wait
    /// for as long as he needs.
    /// </summary>
    public MissionGauge? Gauge
        => phase == Phase.Committing && leaning >= 0 && dwell > 0.05f
            ? new MissionGauge($"Standing with {stations[leaning].Host.Name}",
                               dwell / CommitSeconds, stations[leaning].Host.Tint)
            : null;

    int Heard
    {
        get
        {
            int n = 0;
            foreach (bool h in heard) if (h) n++;
            return n;
        }
    }

    /// <summary>Which delegation's floor he is on, or -1 for the middle of the room.</summary>
    int StandingIn(List<Vector3> people)
    {
        for (int i = 0; i < stations.Length; i++)
        {
            var s = stations[i];
            foreach (var at in people)
            {
                var d = at - s.At;
                if (new Vector2(d.X, d.Z).Length() <= s.Radius) return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Start a run of lines, filtered to this playthrough's branch.
    ///
    /// Reports whether there is anything to say. Every caller has to do something different when
    /// the answer is no, and none of them may do nothing: a run that came out empty leaves the
    /// scene sitting in a phase waiting for a line that will never be spoken, which is a room the
    /// player cannot get out of.
    /// </summary>
    bool Say(Beat[] beats, int who)
    {
        lines = new List<Beat>();
        foreach (var b in beats) if (b.PlaysFor(state.Choice)) lines.Add(b);

        beat = 0;
        showing = 0f;
        talking = who;
        return lines.Count > 0;
    }

    /// <summary>
    /// Everyone has been heard. Sum up, then open the floor.
    ///
    /// The phase moves *before* the closing run rather than after it, and that is the whole of
    /// what was wrong with the first version of this: leaving the scene in Hearing while the
    /// closing lines played meant the end of those lines was read as the end of the last case,
    /// which started the closing run again. The room summed up forever and the floor never opened.
    /// </summary>
    void OpenFloor()
    {
        phase = Phase.Closing;
        if (!Say(closing, -1)) phase = Phase.Committing;
    }

    static float HoldFor(string line) => Mathf.Clamp(1.4f + line.Length * 0.045f, 2.2f, 7f);

    public void Step(float dt, Match match) => Step(dt, People(match));

    /// <summary>Advance the scene with the player standing at one place. For the harness.</summary>
    public void StepForTest(float dt, Vector3 at) => Step(dt, new List<Vector3> { at });

    static List<Vector3> People(Match match)
    {
        var at = new List<Vector3>();
        foreach (var p in match.Pawns)
            if (!p.IsBot && p.Alive) at.Add(p.GlobalPosition);
        return at;
    }

    void Step(float dt, List<Vector3> people)
    {
        if (Complete) return;

        // Somebody is talking. Nothing else in the room happens until they have finished, which is
        // what keeps a player from hearing two delegations at once by running between them.
        if (beat < lines.Count)
        {
            showing += dt;
            if (showing < HoldFor(lines[beat].Line)) return;

            showing = 0f;
            beat++;

            if (beat < lines.Count) return;

            // The run just ended. Cleared first, so a transition below that starts somebody else
            // talking is not immediately contradicted.
            talking = -1;

            // Where the end of a run leaves the scene depends on what was being said.
            if (phase == Phase.Opening) phase = Phase.Hearing;
            else if (phase == Phase.Closing) phase = Phase.Committing;
            else if (phase == Phase.Sealing) Complete = true;
            else if (phase == Phase.Hearing && Heard >= stations.Length) OpenFloor();

            return;
        }

        int inside = StandingIn(people);

        switch (phase)
        {
            case Phase.Hearing:
                // Walking into a bay is asking that delegation to make its case. Once.
                if (inside >= 0 && !heard[inside])
                {
                    heard[inside] = true;

                    // A delegation with nothing written for it still counts as heard, and if it
                    // was the last one the floor has to open here — nothing is going to speak, so
                    // there is no end-of-run for the transition above to hang off.
                    if (!Say(stations[inside].Beats, inside) && Heard >= stations.Length)
                        OpenFloor();
                }

                break;

            case Phase.Committing:
                if (inside != leaning) { leaning = inside; dwell = 0f; }

                if (inside < 0)
                {
                    dwell = Mathf.Max(0f, dwell - dt * CommitDecay);
                    break;
                }

                dwell += dt;
                if (dwell < CommitSeconds) break;

                Decide(stations[inside]);
                break;
        }
    }

    void Decide(Station s)
    {
        state.Decide(s.Answer);
        state.SideWith(s.Is);

        // A delegation with nothing written to say when he picks it still ends the act. The
        // campaign has the answer by this point and a silent bay must not strand him in the room.
        phase = Phase.Sealing;
        if (!Say(s.Commit, -1)) Complete = true;
    }

    // ---- harness ----

    /// <summary>How many cases have been heard, so a test can watch the room fill up.</summary>
    public int HeardForTest => Heard;

    /// <summary>How far into committing he is, from 0 to 1.</summary>
    public float DwellForTest => Mathf.Clamp(dwell / CommitSeconds, 0f, 1f);

    /// <summary>True once every case has been made and the floor is open. For the harness.</summary>
    public bool OpenForTest => phase is Phase.Committing or Phase.Sealing;

    /// <summary>Who is speaking, as a station index, or -1 for the room. For the harness.</summary>
    public int TalkingForTest => talking;
}
