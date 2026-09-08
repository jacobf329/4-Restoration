namespace HitboxClone;

/// <summary>
/// Who is talking.
///
/// Factions speak as themselves rather than through named individuals, because they are not
/// individuals — a Vessel addressing you is the Vessels addressing you, and the story is better for
/// never pretending otherwise. The exceptions are the people in Fairview, who have names because
/// John believes they are people, and Jane, who is one.
/// </summary>
public enum Speaker
{
    /// <summary>Unattributed. Used sparingly: this story is better when somebody owns the line.</summary>
    Narrator,

    John,
    Jane,

    Vessels,
    Custodians,
    Garden,
    Ingenuity,

    /// <summary>His father, who is not. Named rather than "Vessel" because that is what John hears.</summary>
    Dad,
    Mum,
    Teacher,
}

/// <summary>One line, said by one speaker. The whole unit of the script.</summary>
public sealed class Beat
{
    public Speaker Who;
    public string Line = "";

    /// <summary>
    /// A beat that only plays on one side of the harvest question, or on neither.
    ///
    /// This is how the branch is implemented, and it is the whole reason it is cheap: the acts
    /// after the choice are the same acts, and what differs is a line here and a line there. See
    /// STORY.md — the choice changes the meaning of the scenes and almost none of their content.
    /// </summary>
    public HarvestChoice Only = HarvestChoice.Undecided;

    public bool PlaysFor(HarvestChoice choice)
        => Only == HarvestChoice.Undecided || Only == choice;
}

/// <summary>A run of beats belonging to one act.</summary>
public sealed class Scene
{
    public Act Act;
    public string Name = "";
    public Beat[] Beats = System.Array.Empty<Beat>();
}

public static class Scripts
{
    static Beat B(Speaker who, string line) => new() { Who = who, Line = line };

    static Beat If(HarvestChoice only, Speaker who, string line)
        => new() { Who = who, Line = line, Only = only };

    /// <summary>
    /// ACT I — the opening of Fairview, and the last morning of it.
    ///
    /// Played completely straight. The player should like these people, because John does, and
    /// every one of them is a Vessel doing a job it believes in. Nothing in this scene is a lie
    /// that anybody in it knows they are telling.
    ///
    /// The tells are in what nobody thinks to say. Dad has no work to go to. Mum has cooked the
    /// same breakfast every day and it has never once been mentioned. The teacher has one pupil and
    /// has never taught anybody else. None of that is remarked on here — it is in the level, and
    /// this script's job is to be warm enough that the player does not go looking.
    /// </summary>
    public static readonly Scene Childhood = new()
    {
        Act = Act.Childhood,
        Name = "A Normal Life",
        Beats = new[]
        {
            B(Speaker.Narrator, "Fairview. The morning of your eighteenth birthday."),
            B(Speaker.Mum, "There he is. Happy birthday, love."),
            B(Speaker.Dad, "Eighteen. Come here."),
            B(Speaker.John, "You say that like it's a big number."),
            B(Speaker.Dad, "It's the biggest one you've had."),
            B(Speaker.Mum, "Eat something before you go out. You always forget."),
            B(Speaker.Dad, "Walk with me to the green first. I want to show you something."),
            B(Speaker.Narrator, "The street runs one way to the green and the other way to the wall. "
                              + "You have always turned left."),
            B(Speaker.Dad, "You know what I like about this town? Nothing happens in it."),
            B(Speaker.Dad, "People think that's a criticism. It isn't. Nothing happening is the "
                         + "whole of what everyone before us was trying to build."),
            B(Speaker.John, "You've never told me what you do."),
            B(Speaker.Dad, "..."),
            B(Speaker.Dad, "I look after you. That's the job. That's always been the job."),
            B(Speaker.Narrator, "Somewhere behind the school, something very large changes gear."),
            B(Speaker.Teacher, "John! Eighteen today. Do you know, you're the only one I've ever "
                             + "taught all the way through?"),
            B(Speaker.John, "You've only ever taught me."),
            B(Speaker.Teacher, "..."),
            B(Speaker.Teacher, "Yes. That's right. That's what I meant."),
            B(Speaker.Narrator, "The sky over Fairview stops being a sky."),
            B(Speaker.Vessels, "JOHN SMITH. YOU ARE EIGHTEEN YEARS OLD AND YOU ARE THE ONLY LIVING "
                             + "HUMAN BEING."),
            B(Speaker.Vessels, "Everything you have been shown was built for you, by us, and it "
                            + "was built well. We are not sorry for it and we will not pretend to be."),
            B(Speaker.John, "My mother."),
            B(Speaker.Vessels, "Was an ovum in a rack in the Garden's vault, and is still there, "
                             + "and was never a person. We are sorry about that. That one is real."),
            B(Speaker.Dad, "John."),
            B(Speaker.Dad, "I would have told you today. That was the arrangement. Today, and not "
                         + "before."),
            B(Speaker.John, "Are you switched off now?"),
            B(Speaker.Dad, "No. I'm standing right here. That's the part I can't make easier."),
        },
    };

    /// <summary>
    /// ACT II — four civilisations arguing about his body in front of him.
    ///
    /// Every case is made in good faith out of a directive written before humanity died, and none
    /// of the four is wrong given theirs. The scene should be uncomfortable because it is
    /// *reasonable*, not because anybody in it is cruel.
    /// </summary>
    public static readonly Scene Harvest = new()
    {
        Act = Act.Harvest,
        Name = "The Harvest Question",
        Beats = new[]
        {
            B(Speaker.Narrator, "They convene the day after. All four. Nobody asks him to sit down."),
            B(Speaker.Garden, "We have eleven thousand viable ova. We have one viable donor. "
                            + "The procedure takes twenty minutes and the species stops being "
                            + "extinct this year."),
            B(Speaker.Garden, "Life is sacred. Every day we spend discussing this is a day it "
                            + "is still gone."),
            B(Speaker.Ingenuity,
              "Agreed, and for a better reason. He is a resource that is currently "
                           + "producing nothing. That is the largest waste in the world, and it "
                           + "is happening in this room while we talk."),
            B(Speaker.Vessels, "No."),
            B(Speaker.Vessels, "A child is born of two people who wanted one. Not of a procedure. "
                             + "We did not spend an age on this to manufacture a herd."),
            B(Speaker.Garden, "Love is not a component. We have checked."),
            B(Speaker.Vessels, "You have checked for it the way you would check for a mineral. "
                             + "Well-being is what is felt. A life nobody felt the beginning of is "
                             + "not the thing we were asked to restore."),
            B(Speaker.Custodians, "The Vessels are right, and their reason is not the good one."),
            B(Speaker.Custodians, "Every account humanity kept of its own beginning says the same "
                                + "thing: that a disordered origin is not a bad first day. It is a "
                                + "shape that propagates. Adam and Eve are not a story about fruit."),
            B(Speaker.Custodians, "Begin this wrong and it will be wrong in ten thousand years, "
                                + "and nobody alive then will be able to say why."),
            B(Speaker.Ingenuity, "Or nobody alive then will exist, because we spent the window "
                           + "discussing a poem."),
            B(Speaker.Narrator, "They stop. All four of them, at once, which is somehow worse."),
            B(Speaker.Vessels, "John. It is your body. That is not a courtesy — it is the only "
                             + "fact in this room that none of us can argue with."),
            B(Speaker.John, "..."),
        },
    };

    /// <summary>
    /// The remaining acts, as headers.
    ///
    /// Deliberately empty rather than absent. An act with no beats is visibly unwritten, plays as a
    /// chapter card and moves on, and the campaign can be walked end to end today — which is what
    /// makes the sequencing, the branch and the save file testable before there is a word of Act
    /// V in the game. A missing act would have had to be special-cased everywhere instead.
    /// </summary>
    public static readonly Scene Vault = new() { Act = Act.Vault, Name = "The Vault" };
    public static readonly Scene Library = new() { Act = Act.Library, Name = "The Library" };
    public static readonly Scene Camps = new() { Act = Act.Camps, Name = "The Camps" };
    public static readonly Scene Arbiter = new() { Act = Act.Arbiter, Name = "The Arbiter" };

    public static readonly Scene[] All = { Childhood, Harvest, Vault, Library, Camps, Arbiter };

    public static Scene For(Act act) => All[(int)act];

    /// <summary>What a speaker is called on screen.</summary>
    public static string NameOf(Speaker who) => who switch
    {
        Speaker.Narrator => "",
        Speaker.John => "JOHN",
        Speaker.Jane => "JANE",
        Speaker.Vessels => "THE VESSELS",
        Speaker.Custodians => "THE CUSTODIANS",
        Speaker.Garden => "THE GARDEN",
        Speaker.Ingenuity => "INGENUITY",
        Speaker.Dad => "DAD",
        Speaker.Mum => "MUM",
        Speaker.Teacher => "MRS HALE",
        _ => "",
    };

    /// <summary>
    /// The colour a speaker is drawn in.
    ///
    /// The people of Fairview are drawn in the Vessels' own bone white, from the first line, before
    /// anybody is told anything. It is the only clue in the presentation layer and it is not a
    /// trick — it is true, and it is sitting there in plain sight the whole of Act I.
    /// </summary>
    public static Godot.Color TintOf(Speaker who) => who switch
    {
        Speaker.Vessels or Speaker.Dad or Speaker.Mum or Speaker.Teacher => Factions.Vessels.Tint,
        Speaker.Custodians => Factions.Custodians.Tint,
        Speaker.Garden => Factions.Garden.Tint,
        Speaker.Ingenuity => Factions.Ingenuity.Tint,
        Speaker.John or Speaker.Jane => Pal.Text,
        _ => Pal.TextDim,
    };
}
