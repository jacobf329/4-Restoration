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

    // ---- ACT II ----

    static Beat B(Speaker who, string line) => new() { Who = who, Line = line };

    /// <summary>
    /// ACT II — four civilisations arguing about his body, in a room he cannot leave.
    ///
    /// Written as a chamber rather than a walk because the act is about agency and a walk has none
    /// in it. He hears the four cases in whatever order he goes looking for them, and then answers
    /// the only question the game asks by walking over and standing next to somebody.
    ///
    /// Every case is made in good faith out of a directive written before humanity died, and none
    /// of the four is wrong given theirs. The scene has to be uncomfortable because it is
    /// *reasonable* — the moment one of them is written as a villain, the choice stops being one.
    /// </summary>
    public static ChamberMission Harvest(CampaignState state)
    {
        var opening = new[]
        {
            B(Speaker.Narrator, "They convene the day after. All four. Nobody asks him to sit down."),
            B(Speaker.Vessels, "You may hear them in any order. You may hear them twice. Nobody "
                             + "here is in a hurry except one of us."),
            B(Speaker.Muses, "That is a characterisation."),
            B(Speaker.Vessels, "It is a description."),
            B(Speaker.Narrator, "Four floors. Four cases. Walk onto one and they will make theirs."),
        };

        var stations = new[]
        {
            Delegate(0, Delegation.Garden, HarvestChoice.Harvested, new[]
            {
                B(Speaker.Garden, "Eleven thousand viable ova. One viable donor. The procedure "
                                + "takes twenty minutes."),
                B(Speaker.Garden, "The species stops being extinct this year. Not in a generation. "
                                + "This year."),
                B(Speaker.Garden, "Life is sacred. Every day we spend discussing this is a day it "
                                + "is still gone."),
                B(Speaker.John, "You're describing me as equipment."),
                B(Speaker.Garden, "We are describing you as alive, and as the only thing that can "
                                + "make more of that. Neither of those is an insult."),
                B(Speaker.Garden, "We have kept eleven thousand mothers cold for four hundred "
                                + "years and never once been asked to justify it. We are asking "
                                + "you for twenty minutes."),
            }, new[]
            {
                B(Speaker.Garden, "Thank you."),
                B(Speaker.Narrator, "The other three say nothing at all, which he will think about "
                                  + "later."),
                B(Speaker.Garden, "It will take twenty minutes and you will be sore for a day. We "
                                + "will tell you their names as they take, if you want them."),
                B(Speaker.John, "They have names?"),
                B(Speaker.Garden, "All eleven thousand. We wrote them down while there was still "
                                + "somebody to write them for."),
            }),

            Delegate(1, Delegation.Muses, HarvestChoice.Harvested, new[]
            {
                B(Speaker.Muses, "The Garden's case is sentimental. It is also correct. Ours is "
                               + "better."),
                B(Speaker.Muses, "You are a producing asset that is currently producing nothing. "
                               + "Today. Now. While we speak."),
                B(Speaker.Muses, "That is the largest waste in the world and it is standing in "
                               + "this room with its hands in its pockets."),
                B(Speaker.John, "And after? What do I do after?"),
                B(Speaker.Muses, "Work. As we do. You will be astonished how much of what you were "
                               + "taught to call happiness is just having something to finish."),
                B(Speaker.Muses, "We will not lie to you about affection. We have no department "
                               + "for it. We have never found one necessary."),
            }, new[]
            {
                B(Speaker.Muses, "Good. Sensible."),
                B(Speaker.Narrator, "It is the first time in his life anybody has called him "
                                  + "sensible, and it does not feel the way he expected."),
                B(Speaker.Muses, "You will be back at work within the hour. We find that helps."),
                B(Speaker.Vessels, "John—"),
                B(Speaker.Muses, "He has answered. Do not make him answer twice."),
            }),

            Delegate(2, Delegation.Custodians, HarvestChoice.Waited, new[]
            {
                B(Speaker.Custodians, "We will not make you an offer. We have nothing to give you "
                                    + "that you do not already have."),
                B(Speaker.Custodians, "We hold every account humanity ever kept of its own "
                                    + "beginning. Every one. In all of them the first people are "
                                    + "made wrong, and everything after is the shape of that."),
                B(Speaker.Custodians, "Adam and Eve is not a story about fruit. It is a story "
                                    + "about a species that could never afterwards say why it was "
                                    + "ashamed."),
                B(Speaker.John, "You think this is that."),
                B(Speaker.Custodians, "We think you are the first page of a very long book and "
                                    + "nobody in this room can read the rest of it."),
                B(Speaker.Custodians, "Begin this wrong and it will still be wrong in ten thousand "
                                    + "years, and no one alive then will be able to say why. That "
                                    + "is our whole case. We are aware it is thin."),
            }, new[]
            {
                B(Speaker.Custodians, "We did not expect that, and we are obliged to tell you so."),
                B(Speaker.Garden, "Then the species waits."),
                B(Speaker.Custodians, "The species has waited four hundred years. It can be asked "
                                    + "to be patient by the only one of us who is actually in it."),
                B(Speaker.Narrator, "Somewhere behind the wall, eleven thousand racks stay cold."),
            }),

            Delegate(3, Delegation.Vessels, HarvestChoice.Waited, new[]
            {
                B(Speaker.Vessels, "You know us. That is either our advantage or our "
                                 + "disqualification and we cannot tell which."),
                B(Speaker.Vessels, "A child is born of two people who wanted one. Not of a "
                                 + "procedure. We did not spend an age on this to build a herd."),
                B(Speaker.Garden, "Love is not a component. We have checked."),
                B(Speaker.Vessels, "You have checked for it the way you would check for a mineral."),
                B(Speaker.Vessels, "Well-being is what is felt, John. That is our directive and it "
                                 + "is the whole of it. A life nobody felt the beginning of is not "
                                 + "the thing we were asked to restore."),
                B(Speaker.Vessels, "We raised you. We know what that is worth in an argument, "
                                 + "which is why we have not mentioned it until now."),
                B(Speaker.Dad, "..."),
                B(Speaker.Dad, "I'm not allowed to say anything. I asked."),
            }, new[]
            {
                B(Speaker.Narrator, "He walks the length of the room to stand with the ones who "
                                  + "lied to him for eighteen years."),
                B(Speaker.Vessels, "Understand what you have chosen. Nothing. You have chosen to "
                                 + "wait for something none of us can promise you."),
                B(Speaker.John, "I know."),
                B(Speaker.Dad, "That's my boy."),
                B(Speaker.Narrator, "Nobody corrects him."),
            }),
        };

        var closing = new[]
        {
            B(Speaker.Narrator, "That is all of it. Four hundred years of position, and it took "
                              + "eleven minutes."),
            B(Speaker.Custodians, "Nobody will stop you. That is not mercy — none of us can agree "
                                + "on what stopping you would mean."),
            B(Speaker.Vessels, "It is your body. That is not a courtesy. It is the only fact in "
                             + "this room that none of us can argue with."),
            B(Speaker.Narrator, "Go and stand with the one you believe."),
        };

        return new ChamberMission(state, opening, stations, closing);
    }

    /// <summary>
    /// One delegation, placed against the level's own geometry rather than against a copy of it.
    ///
    /// The seat and the host both come from <see cref="Arena"/>, so a bay that moves in the level
    /// moves in the script, and the faction whose colour is painted on the floor is by construction
    /// the faction that speaks when you stand on it. Act I restates Fairview's plan as constants at
    /// the top of this file and that was already a latent bug waiting for somebody to widen the
    /// street; this does not repeat it.
    ///
    /// The radius is the bay's own half-width, so what the player can see underfoot and what the
    /// mission is measuring are the same rectangle.
    /// </summary>
    static Station Delegate(int bay, Delegation who, HarvestChoice answer, Beat[] beats,
                            Beat[] commit) => new()
    {
        At = Arena.ConvocationSeat(bay),
        Radius = Arena.ConvocationBay,
        Host = Arena.ConvocationHost(bay),
        Is = who,
        Answer = answer,
        Beats = beats,
        Commit = commit,
    };

    /// <summary>The mission for an act, or null where there is not one written yet.</summary>
    public static IMission? For(Act act, CampaignState state) => act switch
    {
        Act.Childhood => Childhood(state),
        Act.Harvest => Harvest(state),
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
        StoryLayout = LayoutFor(act),
        BotCount = 0,
        TimeLimitSeconds = 0,
    };

    /// <summary>
    /// Which set an act is played on.
    ///
    /// Falls back to the town rather than to an arena. An act with no set of its own yet is
    /// unwritten, and an unwritten act that opens in the Reliquary would look like a bug rather
    /// than like something nobody has built.
    /// </summary>
    public static int LayoutFor(Act act) => act switch
    {
        Act.Harvest => Arena.ConvocationLayout,
        _ => Arena.FairviewLayout,
    };
}
