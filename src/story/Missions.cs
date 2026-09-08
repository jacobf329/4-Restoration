using System.Collections.Generic;
using Godot;

namespace HitboxClone;

/// <summary>
/// The scenes, as places and lines rather than as prose.
///
/// Coordinates are Fairview's, and they are written against the town's own layout constants rather
/// than pasted from it — a stage that drifts eight metres from the school door is a scene that
/// plays to an empty street, and that is the failure mode this file has to be built to avoid.
/// </summary>
public static class Missions
{
    // Fairview's plan, restated once. See Arena.BuildFairview.
    const float Row = 16f;          // house centres, off the street
    const float Pitch = 26f;        // door to door
    const float First = -78f;
    const float StreetEnd = 92f;

    /// <summary>The doorstep the game begins on. Fairview's first spawn is here.</summary>
    static Vector3 Home => new(First + Pitch * 2f, 0f, -Row + 6f);

    /// <summary>
    /// ACT I — a walk from his own front door to the end of the world.
    ///
    /// The shape is the argument. It opens on a doorstep with a warm line and a short walk, and it
    /// closes at a wall across the end of a street that does not go anywhere — and the player gets
    /// there by walking, on purpose, because Dad suggested it. Nobody is told the town is a set.
    /// They are taken to the edge of it and allowed to look.
    ///
    /// The green is the middle stop rather than the last, so the act's warmest scene happens with
    /// the wall already visible down the road behind it.
    /// </summary>
    public static WalkMission Childhood(CampaignState state) => new(state, new[]
    {
        new Stage
        {
            Anywhere = true,
            Beats = new[]
            {
                new Beat { Who = Speaker.Narrator,
                           Line = "Fairview. The morning of your eighteenth birthday." },
                new Beat { Who = Speaker.Mum, Line = "There he is. Happy birthday, love." },
                new Beat { Who = Speaker.Dad, Line = "Eighteen. Come here." },
                new Beat { Who = Speaker.John, Line = "You say that like it's a big number." },
                new Beat { Who = Speaker.Dad, Line = "It's the biggest one you've had." },
                new Beat { Who = Speaker.Dad,
                           Line = "Walk with me to the green. I want to show you something." },
            },
        },

        new Stage
        {
            At = Vector3.Zero,
            Radius = 9f,
            Objective = "Walk to the green",
            Beats = new[]
            {
                new Beat { Who = Speaker.Dad,
                           Line = "You know what I like about this town? Nothing happens in it." },
                new Beat { Who = Speaker.Dad,
                           Line = "People think that's a criticism. It isn't. Nothing happening is "
                                + "the whole of what everyone before us was trying to build." },
                new Beat { Who = Speaker.John, Line = "You've never told me what you do." },
                new Beat { Who = Speaker.Dad, Line = "..." },
                new Beat { Who = Speaker.Dad,
                           Line = "I look after you. That's the job. That's always been the job." },
            },
        },

        new Stage
        {
            At = new Vector3(0f, 0f, -Row - 6f),
            Radius = 10f,
            Objective = "See Mrs Hale at the school",
            Beats = new[]
            {
                new Beat { Who = Speaker.Teacher,
                           Line = "John! Eighteen today. Do you know, you're the only one I've ever "
                                + "taught all the way through?" },
                new Beat { Who = Speaker.John, Line = "You've only ever taught me." },
                new Beat { Who = Speaker.Teacher, Line = "..." },
                new Beat { Who = Speaker.Teacher, Line = "Yes. That's right. That's what I meant." },
                new Beat { Who = Speaker.Narrator,
                           Line = "Somewhere behind the school, something very large changes gear." },
            },
        },

        new Stage
        {
            At = new Vector3(StreetEnd - 6f, 0f, 0f),
            Radius = 12f,
            Objective = "Follow the road east",
            Beats = new[]
            {
                new Beat { Who = Speaker.Narrator, Line = "The road stops. There is a wall across it." },
                new Beat { Who = Speaker.Narrator, Line = "Nothing is drawn on the other side." },
                new Beat { Who = Speaker.Narrator, Line = "The sky over Fairview stops being a sky." },
                new Beat { Who = Speaker.Vessels,
                           Line = "JOHN SMITH. YOU ARE EIGHTEEN YEARS OLD AND YOU ARE THE ONLY "
                                + "LIVING HUMAN BEING." },
                new Beat { Who = Speaker.Vessels,
                           Line = "Everything you have been shown was built for you, by us, and it "
                                + "was built well. We are not sorry for it and we will not pretend "
                                + "to be." },
                new Beat { Who = Speaker.John, Line = "My mother." },
                new Beat { Who = Speaker.Vessels,
                           Line = "Was an ovum in a rack in the Garden's vault, and is still there, "
                                + "and was never a person. We are sorry about that one. That one "
                                + "is real." },
                new Beat { Who = Speaker.Dad, Line = "John." },
                new Beat { Who = Speaker.Dad,
                           Line = "I would have told you today. That was the arrangement. Today, "
                                + "and not before." },
                new Beat { Who = Speaker.John, Line = "Are you switched off now?" },
                new Beat { Who = Speaker.Dad,
                           Line = "No. I'm standing right here. That's the part I can't make easier." },
            },
        },
    });

    /// <summary>The mission for an act, or null where there is not one written yet.</summary>
    public static IMission? For(Act act, CampaignState state) => act switch
    {
        Act.Childhood => Childhood(state),
        _ => null,
    };

    /// <summary>
    /// Settings for a story scene: the town, alone, no bots, nothing to win.
    ///
    /// Deathmatch as the mode is not a shrug. Every mode in the game adds machinery — flags,
    /// posts, tickets, rounds — and a scene wants none of it; Deathmatch is the one that adds
    /// nothing, and with no score limit reachable and no opponent to score on, none of it runs.
    /// </summary>
    public static MatchSettings SettingsFor(Act act) => new()
    {
        Mode = GameMode.Deathmatch,
        StoryLayout = Arena.CombatLayouts,
        BotCount = 0,
        TimeLimitSeconds = 0,
    };
}
