namespace HitboxClone;

/// <summary>
/// What a reinforcement costs, and therefore how often you see one.
///
/// Three tiers rather than a continuous price list, because the number a player actually holds in
/// their head is "can I afford the good one yet", and three answers is as many as that question
/// supports. The costs are set against the earn rates in <see cref="BattlePoints"/>: a competent
/// player clears Line in a couple of minutes, Elite once or twice a match, and Hero perhaps once —
/// which is roughly the shape of a Battlefront match and the reason the mode feels like it builds.
/// </summary>
public enum ReinforcementTier
{
    /// <summary>The four basic classes. Always available, always free.</summary>
    Basic,

    /// <summary>One step up. Common enough to be a normal part of a match.</summary>
    Line,

    /// <summary>Expensive, and a real presence when one arrives.</summary>
    Elite,

    /// <summary>The faction's figure out of the great fiction. Once a match, if that.</summary>
    Hero,
}

/// <summary>
/// A character you can spend battle points to spawn as.
///
/// Deliberately built *around* <see cref="ClassDef"/> rather than replacing it. A reinforcement is
/// a class — it has health, a speed, a weapon and a dash like everything else — plus a price, a
/// faction that fields it, and a look. Doing it this way means every system that already reads
/// <c>pawn.Class</c> keeps working unchanged: the weapon path, the damage numbers, the bot's
/// preferred range, the HUD. The alternative was a parallel hierarchy of special units, and
/// parallel hierarchies are how a codebase ends up with two of everything and one of them wrong.
///
/// Heroes are the exception that proves it: they are the existing juggernauts, and they arrive
/// through <see cref="Pawn.TakeCrown"/> rather than through a class swap, because a juggernaut is
/// already a fully-formed thing with its own health scaling, its own power and its own saber.
/// </summary>
public sealed class ReinforcementDef
{
    public string Name = "";

    /// <summary>Who they are, in a line. Shown on the spawn card.</summary>
    public string Epithet = "";

    /// <summary>What they do, in a sentence. Shown when the card is selected.</summary>
    public string Blurb = "";

    public ReinforcementTier Tier = ReinforcementTier.Line;

    /// <summary>Battle points to spawn as one. Zero for the basic classes.</summary>
    public int Cost;

    /// <summary>The faction that fields them, or null for the four everyone can use.</summary>
    public FactionDef? Faction;

    /// <summary>The stat line and weapon. This is what actually goes onto the pawn.</summary>
    public ClassDef Class = Classes.Trooper;

    /// <summary>
    /// Model base name under <c>assets/characters</c>, or empty to wear the faction's own body.
    ///
    /// Empty for every one of these today, on purpose. The stat lines, the costs and the spawn
    /// screen are all real and playable now; the models are eight pairs of GLB exports that do not
    /// exist yet, and gating the mechanic on the art would have meant shipping neither. A
    /// reinforcement in a faction body plays correctly and reads as a stronger version of your
    /// side, which is a perfectly good place to stand while the art is made.
    /// </summary>
    public string Model = "";

    /// <summary>True for the four basic classes, which cost nothing and are always offered.</summary>
    public bool IsBasic => Tier == ReinforcementTier.Basic;
}

/// <summary>
/// What earns battle points, and how many.
///
/// Every number here is a statement about what the game wants people to do, so they are gathered in
/// one place rather than scattered across the scoring paths that award them. The ratios matter more
/// than the absolute values: an objective is worth more than a kill in every case, which is the
/// whole reason a conquest mode has objectives rather than just a scoreboard.
/// </summary>
public static class BattlePoints
{
    /// <summary>An ordinary kill.</summary>
    public const int Kill = 100;

    /// <summary>
    /// Killing whoever is wearing the crown.
    ///
    /// Three kills' worth, because it takes about that much doing and because the person who lands
    /// it has usually spent the fight being shot at by a titan.
    /// </summary>
    public const int HeroKill = 300;

    /// <summary>Taking a command post off the other side.</summary>
    public const int PostCapture = 200;

    /// <summary>Bringing a flag home.</summary>
    public const int FlagCapture = 250;

    /// <summary>Per second held, in King of the Hill.</summary>
    public const int ZoneSecond = 12;

    /// <summary>
    /// Wrecking a vehicle. Worth more than the kill it usually contains, because a hull is a
    /// problem the whole team has and somebody had to stop shooting at people to deal with it.
    /// </summary>
    public const int VehicleKill = 175;
}

/// <summary>
/// The spawn roster: four basic classes everyone shares, then two characters per faction, then the
/// faction's hero.
///
/// The design rule for the eight faction characters was that each has to do something no basic
/// class can, and that the four factions' pairs must not rhyme with each other. A heavy, a healer,
/// a spotter, a shield, an area-denial engineer, a walking garden, a swarm and a duellist — eight
/// answers to eight different questions, none of which is "the Trooper but better", which is the
/// failure mode every unlock system in every shooter falls into eventually.
/// </summary>
public static class Reinforcements
{
    // ---- the four everyone has ----

    public static readonly ReinforcementDef Trooper = new()
    {
        Name = "Trooper", Epithet = "All-rounder", Blurb = Classes.Trooper.Blurb,
        Tier = ReinforcementTier.Basic, Cost = 0, Class = Classes.Trooper,
    };

    public static readonly ReinforcementDef Flanker = new()
    {
        Name = "Flanker", Epithet = "Skirmisher", Blurb = Classes.Flanker.Blurb,
        Tier = ReinforcementTier.Basic, Cost = 0, Class = Classes.Flanker,
    };

    public static readonly ReinforcementDef Tactician = new()
    {
        Name = "Tactician", Epithet = "Close quarters", Blurb = Classes.Tactician.Blurb,
        Tier = ReinforcementTier.Basic, Cost = 0, Class = Classes.Tactician,
    };

    public static readonly ReinforcementDef Marksman = new()
    {
        Name = "Marksman", Epithet = "Precision", Blurb = Classes.Marksman.Blurb,
        Tier = ReinforcementTier.Basic, Cost = 0, Class = Classes.Marksman,
    };

    public static readonly ReinforcementDef[] Basic = { Trooper, Flanker, Tactician, Marksman };

    // ---- The Vessels: the body as the argument ----

    public static readonly ReinforcementDef Sinew = new()
    {
        Name = "Sinew",
        Epithet = "the body, insisted upon",
        Blurb = "Enormous and slow, with a gun that does not stop. Cannot sprint.",
        Tier = ReinforcementTier.Line,
        Cost = 500,
        Faction = Factions.Vessels,
        Class = SpecialClasses.Sinew,
        Model = "sinew",
    };

    public static readonly ReinforcementDef Anatomist = new()
    {
        Name = "The Anatomist",
        Epithet = "who keeps the bodies working",
        Blurb = "A surgical beam: it opens anything at arm's length, and mends your own side instead.",
        Tier = ReinforcementTier.Elite,
        Cost = 1100,
        Faction = Factions.Vessels,
        Class = SpecialClasses.Anatomist,
        Model = "anatomist",
    };

    // ---- The Custodians: knowledge as a weapon ----

    public static readonly ReinforcementDef Lector = new()
    {
        Name = "The Lector",
        Epithet = "who reads the room aloud",
        Blurb = "Long rifle. Everything it hits stays lit up for your whole team.",
        Tier = ReinforcementTier.Line,
        Cost = 500,
        Faction = Factions.Custodians,
        Class = SpecialClasses.Lector,
        Model = "lector",
    };

    public static readonly ReinforcementDef Apologist = new()
    {
        Name = "The Apologist",
        Epithet = "an argument you cannot get around",
        Blurb = "Plants a wall their fire cannot cross and yours can. Very hard to shift off a post.",
        Tier = ReinforcementTier.Elite,
        Cost = 1100,
        Faction = Factions.Custodians,
        Class = SpecialClasses.Apologist,
        Model = "apologist",
    };

    // ---- The Garden: ground worth standing on ----

    public static readonly ReinforcementDef Grafter = new()
    {
        Name = "The Grafter",
        Epithet = "who plants where the fighting is",
        Blurb = "Lobs seed pods that burst into thorns. Denies ground rather than crossing it.",
        Tier = ReinforcementTier.Line,
        Cost = 500,
        Faction = Factions.Garden,
        Class = SpecialClasses.Grafter,
        Model = "grafter",
    };

    public static readonly ReinforcementDef Orchard = new()
    {
        Name = "The Orchard",
        Epithet = "a garden that walks",
        Blurb = "Vast and slow, and cannot sprint. Everything of yours near it heals.",
        Tier = ReinforcementTier.Elite,
        Cost = 1100,
        Faction = Factions.Garden,
        Class = SpecialClasses.Orchard,
        Model = "orchard",
    };

    // ---- The Muses: the performance ----

    public static readonly ReinforcementDef Chorus = new()
    {
        Name = "The Chorus",
        Epithet = "never only one of them",
        Blurb = "Fastest thing on the map, and you are never sure how many of it there are.",
        Tier = ReinforcementTier.Line,
        Cost = 500,
        Faction = Factions.Muses,
        Class = SpecialClasses.Chorus,
        Model = "chorus",
    };

    public static readonly ReinforcementDef Tragedian = new()
    {
        Name = "The Tragedian",
        Epithet = "who is best in the last act",
        Blurb = "A blade, and eight seconds in which it cannot be killed and hits ever harder.",
        Tier = ReinforcementTier.Elite,
        Cost = 1100,
        Faction = Factions.Muses,
        Class = SpecialClasses.Tragedian,
        Model = "tragedian",
    };

    /// <summary>
    /// What a faction's hero costs.
    ///
    /// Once a match for a good player, which is where it should sit. Cheaper and the arena is never
    /// without one; dearer and nobody ever sees the thing the whole setting is built around.
    /// </summary>
    public const int HeroCost = 2600;

    public static readonly ReinforcementDef[] All =
    {
        Trooper, Flanker, Tactician, Marksman,
        Sinew, Anatomist, Lector, Apologist,
        Grafter, Orchard, Chorus, Tragedian,
    };

    /// <summary>
    /// Everything this fighter may spawn as: the four basics, then their own faction's characters,
    /// cheapest first.
    ///
    /// Their own faction's only. A shared pool would make faction choice cosmetic again, which is
    /// the exact failure the specials were moved off class to fix.
    /// </summary>
    public static System.Collections.Generic.List<ReinforcementDef> For(FactionDef faction)
    {
        var list = new System.Collections.Generic.List<ReinforcementDef>(Basic);

        foreach (var r in All)
            if (r.Faction == faction) list.Add(r);

        list.Sort((l, r) => l.Cost.CompareTo(r.Cost));
        return list;
    }
}
