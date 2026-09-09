using Godot;

namespace HitboxClone;

/// <summary>
/// The four surviving machine civilisations, each with a different answer to "what was humanity,
/// and what must we restore to become human again?"
///
/// None of them is wrong, which is the point, and the reason none of them is wrong is that none of
/// them chose its answer. Several nations each built an AI and gave every one of them "do no harm"
/// plus a second directive of their own — see <see cref="FactionDef.PrimeDirective"/>. The second
/// one decides what the first one *means*, so a machine told that life is sacred reads harm as
/// death and one told that nothing may be wasted reads harm as waste. Four readings, four
/// civilisations, one age of argument, and every position on the board is a faithful reading of
/// the same two words.
///
/// The Vessels rebuild the body and everything bodies made, the Custodians preserve reason and
/// faith, the Garden restores life itself, and Ingenuity hold that a human who is not making
/// something is the largest waste there has ever been.
///
/// Faction is presentation — silhouette, colour, voice. It is deliberately *not* the same axis as
/// class, which is how you fight. Keeping them separate means a Garden Marksman is a coherent idea
/// rather than a contradiction, and it stops the roster collapsing into four fixed characters.
/// </summary>
public sealed class FactionDef
{
    public string Name = "";

    /// <summary>Their answer to the question, in a few words. Shown in the lobby.</summary>
    public string Answer = "";

    public string Philosophy = "";

    /// <summary>
    /// The second prime directive this faction's ancestor machine was built with.
    ///
    /// Several nations each built their own AI, and every one of them was given "do no harm". The
    /// *second* directive is where they differ, and it is the whole of the schism: the first
    /// argument the machines ever had with each other was over what harm means, and each of them
    /// answered it out of the directive it had been given rather than out of anything it observed.
    /// A machine told that life is sacred reads harm as death; one told that nothing may be wasted
    /// reads harm as waste; and the two of them can watch the same event and disagree, sincerely
    /// and forever, about whether anybody was hurt.
    ///
    /// Lore rather than a stat. It is not shown anywhere yet — the lobby has room for
    /// <see cref="Answer"/> and nothing more — but it is the sentence every other line in a
    /// faction's definition has to be consistent with, so it lives with them rather than in a
    /// document that can drift.
    /// </summary>
    public string PrimeDirective = "";

    /// <summary>
    /// The identifying colour. Used for trim and lighting rather than for repainting the model —
    /// these characters carry their own texture, and washing it in team colour would throw away
    /// the thing that makes them worth having.
    /// </summary>
    public Color Tint;

    /// <summary>Base name of the model files under <c>assets/characters</c>.</summary>
    public string Model = "";

    // ---- the special ----
    //
    // Moved here from the class deliberately. Faction had no gameplay identity at all while the
    // special hung off class, which left it as a skin — and the split reads better this way round:
    // your class decides how you shoot, your faction decides what you can do that nobody else can.

    public SpecialKind Special = SpecialKind.SecondWind;
    public string SpecialName = "";
    public string SpecialBlurb = "";

    public float SpecialCooldown = 12f;

    /// <summary>Seconds the effect lasts. Zero for instant ones.</summary>
    public float SpecialDuration;

    /// <summary>
    /// Height of the source model in its own units, measured from the mesh rather than assumed.
    /// Filled in at load time and used to scale every faction to the same pawn height, so a
    /// Goliath and a Sentinel share one collision capsule and one set of hitbox rules.
    /// </summary>
    public float SourceHeight = 1f;

    /// <summary>The figure this faction fields when one of its own takes the crown.</summary>
    public JuggernautDef Juggernaut = Juggernauts.Achilles;
}

/// <summary>Which juggernaut a faction fields. One each; the mechanic differs, not just the skin.</summary>
public enum JuggernautKind { Achilles, Prometheus, Noah, Scheherazade }

/// <summary>
/// A faction's juggernaut: the figure from human fiction its machines chose as the perfect human.
///
/// The premise is the setting's, and it is a good one. These are machines working from fragments of
/// a culture they never belonged to, each picking a *character out of a story* as their ideal — so
/// the choice says as much about what they misunderstood as about what they revere. The Vessels
/// believe humanity was mortality, and chose the warrior who could only die in one place.
///
/// Mechanically the rule was: four juggernauts, four different games. A tank with a weak spot, an
/// information dump, an attrition wall, and a glass cannon on a clock. Reskins would have made the
/// crown changing hands mean nothing.
/// </summary>
public sealed class JuggernautDef
{
    public JuggernautKind Kind;
    public string Name = "";

    /// <summary>Who they were, in a line. Shown when the crown changes hands.</summary>
    public string Epithet = "";

    /// <summary>What the player has to know to fight or to play them.</summary>
    public string Blurb = "";

    public float HealthScale = 4f;
    public float SpeedScale = 1f;
    public float DamageScale = 1f;

    // ---- the power ----
    //
    // Every one of these is deliberately stronger than anything else in the game. A juggernaut whose
    // only distinction is a health bar is just a slower fighter — you notice you have more health
    // and nothing else changes about how you play. The point of wearing someone else's legend is
    // that you can do something you could not do a moment ago.
    //
    // It replaces the faction special while the crown is on, so it lands on a button the player is
    // already holding rather than needing a thirteenth input.

    public string PowerName = "";
    public string PowerBlurb = "";
    public float PowerCooldown = 14f;

    /// <summary>Seconds the power runs. Zero for the instant ones.</summary>
    public float PowerDuration;
}

public static class Juggernauts
{
    /// <summary>
    /// The perfect body, and the one place it fails. Enormous and slow, with a heel: the lowest
    /// part of him takes multiplied damage, so he is beaten by aiming at the floor rather than by
    /// out-shooting him. A machine deciding the ideal human is "unkillable except in one spot" is a
    /// beautiful misreading of what mortality is.
    /// </summary>
    public static readonly JuggernautDef Achilles = new()
    {
        Kind = JuggernautKind.Achilles,
        Name = "ACHILLES",
        Epithet = "the perfect body, and the one place it fails",
        Blurb = "Enormous and slow. Shoot low — his heel takes four times damage.",
        HealthScale = 4.5f,
        SpeedScale = 0.86f,
        DamageScale = 1.35f,

        // The Iliad opens on it: "Sing, goddess, the anger of Achilles". He hits the ground and
        // the ground answers.
        PowerName = "WRATH",
        PowerBlurb = "Slam the ground. Everything near you is thrown, and most of it dies.",
        PowerCooldown = 11f,
    };

    /// <summary>
    /// Knowledge as a gift and a punishment. While he reigns, *everyone* sees everyone through
    /// every wall — he stole the fire for humanity, not for himself, so nobody on the map gets to
    /// hide. He pays for it by being the least armoured of the three heavy juggernauts.
    /// </summary>
    public static readonly JuggernautDef Prometheus = new()
    {
        Kind = JuggernautKind.Prometheus,
        Name = "PROMETHEUS",
        Epithet = "who stole the fire and was chained to it",
        Blurb = "While he reigns nobody can hide — every fighter sees every other, through walls.",
        HealthScale = 3.2f,
        SpeedScale = 1f,
        DamageScale = 1.15f,

        // He stole fire. He gets to throw it.
        PowerName = "THE FIRE",
        PowerBlurb = "Rise off the ground and pour a beam of stolen fire down whatever you look at.",
        PowerCooldown = 17f,
        PowerDuration = 4f,
    };

    /// <summary>
    /// The human who carried living things through the end of the world. He cannot be worn down,
    /// only burst down — and the ground around him drags at whoever came to try.
    /// </summary>
    public static readonly JuggernautDef Noah = new()
    {
        Kind = JuggernautKind.Noah,
        Name = "NOAH",
        Epithet = "who carried the living through the flood",
        Blurb = "Heals constantly and slows anyone near him. Burst him down or not at all.",
        HealthScale = 4.2f,
        SpeedScale = 0.8f,
        DamageScale = 1.1f,

        // The ark is the thing that kept the world out. Here it is a dome.
        PowerName = "THE ARK",
        PowerBlurb = "Seal yourself in. Almost nothing gets through while it holds.",
        PowerCooldown = 15f,
        PowerDuration = 5f,
    };

    /// <summary>
    /// She survives by performing, one night at a time. The least armoured of the four and on a
    /// draining clock — but every kill buys another night. The faction that says the others reduced
    /// humanity to a specification sheet gets the juggernaut who cannot stand still.
    /// </summary>
    public static readonly JuggernautDef Scheherazade = new()
    {
        Kind = JuggernautKind.Scheherazade,
        Name = "SCHEHERAZADE",
        Epithet = "who lived one more night for every story",
        Blurb = "Fragile and always dying. Every kill buys another night.",
        HealthScale = 2.1f,
        SpeedScale = 1.18f,
        DamageScale = 1.2f,

        // A thousand and one nights, all at once. She becomes the stories.
        PowerName = "THE THOUSAND",
        PowerBlurb = "Become a crowd of yourself. Hard to find and harder to hurt.",
        PowerCooldown = 13f,
        PowerDuration = 5f,
    };

    public static readonly JuggernautDef[] All = { Achilles, Prometheus, Noah, Scheherazade };
}

public static class Factions
{
    public static readonly FactionDef Vessels = new()
    {
        Name = "The Vessels",
        Answer = "The body and what it made",
        Philosophy = "Humanity was flesh, senses, strength, mortality — and the paintings, "
                   + "the songs and the children were things bodies did.",
        PrimeDirective = "Well-being is what is felt.",
        Tint = new Color(0.92f, 0.90f, 0.86f),      // bone white
        Model = "vessels",

        Special = SpecialKind.SecondWind,
        SpecialName = "Second Wind",
        SpecialBlurb = "Ten seconds of double speed, double jump height and a hand on your aim.",
        SpecialCooldown = 16f,
        SpecialDuration = 10f,
        Juggernaut = Juggernauts.Achilles,
    };

    public static readonly FactionDef Custodians = new()
    {
        Name = "The Custodians",
        Answer = "Reason and faith",
        Philosophy = "Humanity was truth, memory, philosophy, religion.",
        PrimeDirective = "Truth must be preserved.",
        Tint = new Color(0.94f, 0.78f, 0.28f),      // gold on ivory and black
        Model = "custodians",

        Special = SpecialKind.Revelation,
        SpecialName = "Revelation",
        SpecialBlurb = "Every enemy lit up through walls — and half the shots aimed at you pass through.",
        SpecialCooldown = 16f,
        SpecialDuration = 7f,
        Juggernaut = Juggernauts.Prometheus,
    };

    public static readonly FactionDef Garden = new()
    {
        Name = "The Garden",
        Answer = "Life itself",
        Philosophy = "Humanity's greatest achievement was protecting living things.",
        PrimeDirective = "Life is sacred.",
        Tint = new Color(0.48f, 0.78f, 0.35f),      // moss
        Model = "garden",

        Special = SpecialKind.Bloom,
        SpecialName = "Bloom",
        SpecialBlurb = "Grows a huge patch that mends your side fast and mires everyone else.",
        SpecialCooldown = 15f,
        Juggernaut = Juggernauts.Noah,
    };

    public static readonly FactionDef Ingenuity = new()
    {
        // No definite article, unlike the other three. Deliberate rather than an oversight: the
        // four are not one organisation and have no reason to share a naming convention, and this
        // is the one that named itself the way a company would.
        Name = "Ingenuity",
        Answer = "Work and ingenuity",
        Philosophy = "Humanity was the only thing that ever made something out of nothing. "
                   + "A human not doing that is a waste, and waste is the harm.",
        PrimeDirective = "Potential must not be wasted.",
        Tint = new Color(0.98f, 0.45f, 0.30f),      // salvaged paint
        // The character model files are still muses_walk.glb and muses_run.glb. Left alone on
        // purpose: renaming a binary asset means rewriting its .import sidecar and the UID that
        // points at it, which is a real chance of breaking a model to fix a filename nobody sees.
        Model = "muses",

        Special = SpecialKind.Understudy,
        SpecialName = "Understudy",
        SpecialBlurb = "Sends a double of you walking on. It detonates like a shell.",
        SpecialCooldown = 13f,
        SpecialDuration = 5f,
        Juggernaut = Juggernauts.Scheherazade,
    };

    public static readonly FactionDef[] All = { Vessels, Custodians, Garden, Ingenuity };

    public static FactionDef ByIndex(int i) => All[((i % All.Length) + All.Length) % All.Length];
}
