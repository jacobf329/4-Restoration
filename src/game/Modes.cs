namespace HitboxClone;

public enum GameMode
{
    Deathmatch, TeamDeathmatch, Elimination, KingOfTheHill, CaptureTheFlag, Juggernaut, Dominion,

    /// <summary>Portal guns and nothing else. See <see cref="PortalVariant"/>.</summary>
    Portal,
}

/// <summary>
/// The two halves of Portal mode, which are genuinely different games sharing one loadout.
///
/// Elimination is a fight in which nobody can shoot anybody: the gun does no damage at all, so
/// every kill has to come from the *map* — a gate over a pit, a gate over the lava, a gate at
/// the edge of the world. It is the only mode here where the arena is the weapon.
///
/// Puzzle is co-operative, on chambers built for it, and has no opponents at all.
/// </summary>
public enum PortalVariant { Elimination, Puzzle }

public sealed class ModeDef
{
    public GameMode Mode;
    public string Name = "";
    public string Blurb = "";
    public bool Teams;

    /// <summary>What the score limit counts, for the lobby's limit row.</summary>
    public string LimitNoun = "frags";

    /// <summary>
    /// The limit's default and the range the lobby can set it over.
    ///
    /// Per mode, because the modes do not count the same thing. One shared 5-to-50 scale meant
    /// King of the Hill — which scores one point per second held — offered fifteen seconds of
    /// holding as its default match, over before anyone had crossed the arena to contest it. And
    /// Elimination counts *rounds*, where a step of five is a jump from a short match to an
    /// interminable one.
    /// </summary>
    public int DefaultLimit = 15;
    public int LimitStep = 5;
    public int MinLimit = 5;
    public int MaxLimit = 50;
}

public static class Modes
{
    public static readonly ModeDef[] All =
    {
        new() { Mode = GameMode.Deathmatch, Name = "Deathmatch",
                Blurb = "Everyone for themselves. First to the frag limit wins.",
                Teams = false, LimitNoun = "frags" },

        new() { Mode = GameMode.TeamDeathmatch, Name = "Team Deathmatch",
                Blurb = "Two teams, shared score. Friendly fire is off.",
                Teams = true, LimitNoun = "frags",
                DefaultLimit = 30, LimitStep = 5, MinLimit = 10, MaxLimit = 90 },

        new() { Mode = GameMode.Elimination, Name = "Elimination",
                Blurb = "No respawns. Last one standing takes the round.",
                Teams = false, LimitNoun = "rounds",
                DefaultLimit = 5, LimitStep = 1, MinLimit = 2, MaxLimit = 15 },

        new() { Mode = GameMode.KingOfTheHill, Name = "King of the Hill",
                Blurb = "Hold the zone to score. It moves when someone holds it too long.",
                Teams = false, LimitNoun = "points",
                DefaultLimit = 150, LimitStep = 25, MinLimit = 50, MaxLimit = 500 },

        // Captures are worth a lot of work each, so the limit is small and its step is one. A
        // five-frag jump between settings would be the difference between a short match and an
        // hour of it.
        new() { Mode = GameMode.CaptureTheFlag, Name = "Capture the Flag",
                Blurb = "Take theirs, defend yours. Your own flag must be home to score.",
                Teams = true, LimitNoun = "captures",
                DefaultLimit = 3, LimitStep = 1, MinLimit = 1, MaxLimit = 10 },

        // Only kills made *as* the juggernaut count, so the limit is small and steps by one.
        // Counting ordinary frags too would make it a deathmatch with a decoration on it.
        new() { Mode = GameMode.Juggernaut, Name = "Juggernaut",
                Blurb = "Kill the juggernaut to become one. Only their kills score.",
                Teams = false, LimitNoun = "kills",
                DefaultLimit = 12, LimitStep = 1, MinLimit = 3, MaxLimit = 40 },

        // The gun does no damage, so the limit counts the only thing that can happen to anybody:
        // falling out of the world. In the puzzle half it counts checkpoints instead, which is why
        // the noun here is deliberately plain and the HUD says what it actually means.
        new() { Mode = GameMode.Portal, Name = "Portal",
                Blurb = "Portal guns only. Drop them into the void, or solve the chambers together.",
                Teams = false, LimitNoun = "falls",
                DefaultLimit = 5, LimitStep = 1, MinLimit = 1, MaxLimit = 20 },

        // The limit is a *reinforcement pool* rather than a target: both sides start with this many
        // and spend one per death, and the side that runs out loses. It is the one mode here whose
        // number counts down, which is exactly why it plays differently from the rest — you are
        // never chasing a score, you are managing a supply.
        //
        // A hundred is about right for eight to twelve fighters: long enough that a bad push is
        // survivable and short enough that holding four posts to two visibly ends it.
        new() { Mode = GameMode.Dominion, Name = "Dominion",
                Blurb = "Take the command posts. Whoever holds more bleeds the other side dry.",
                Teams = true, LimitNoun = "reinforcements",
                DefaultLimit = 100, LimitStep = 25, MinLimit = 50, MaxLimit = 300 },
    };

    public static ModeDef Get(GameMode m)
    {
        foreach (var d in All) if (d.Mode == m) return d;
        return All[0];
    }
}

/// <summary>Everything the lobby configures and the match then reads.</summary>
public sealed class MatchSettings
{
    public GameMode Mode = GameMode.Deathmatch;

    /// <summary>Arena to play, or -1 for a random pick each match.</summary>
    public int ArenaIndex = -1;

    /// <summary>
    /// A story set to play instead, or -1 for none.
    ///
    /// Separate from <see cref="ArenaIndex"/> and honoured ahead of it, because they answer
    /// different questions. ArenaIndex is a request from the lobby and is checked against the kind
    /// of match being started - a versus match asking for a town is refused. This is not a request,
    /// it is story mode saying where its scene happens, and nothing in the lobby can set it.
    /// </summary>
    public int StoryLayout = -1;

    /// <summary>True when this match is a scene rather than a fight.</summary>
    public bool IsStoryMission => StoryLayout >= 0;

    /// <summary>Which half of Portal mode. Meaningless in every other mode.</summary>
    public PortalVariant Portal = PortalVariant.Elimination;

    /// <summary>True when this is the co-operative puzzle half, which changes almost everything.</summary>
    public bool IsPuzzle => Mode == GameMode.Portal && Portal == PortalVariant.Puzzle;

    /// <summary>
    /// Fewest fighters this mode is playable with.
    ///
    /// Two for the puzzle chambers, because the Orrery is built around one person opening a gate
    /// onto a face the other cannot see — a solo player is not under-powered there, they are
    /// stuck. Everything else is playable alone against bots.
    /// </summary>
    public int MinimumFighters => IsPuzzle ? 2 : 1;

    public string ArenaName => ArenaIndex < 0 ? "Random" : Arena.Names[ArenaIndex];
    public int ScoreLimit = 15;

    /// <summary>Match length in seconds, or 0 for no limit. Stored in seconds so the harness can
    /// use a limit short enough to actually reach.</summary>
    public int TimeLimitSeconds;
    /// <summary>
    /// Bots to field alongside the humans.
    ///
    /// Seven by default rather than three. Three was the number that filled the remaining lobby
    /// seats, back when seats and roster were the same thing; it has nothing to do with how many
    /// fighters this arena wants. Eight in the match is roughly seven thousand square metres each
    /// — still airier than Halo's Big Team Battle, and a long way from the fourteen thousand a
    /// four-hander was getting.
    /// </summary>
    public int BotCount = 7;

    /// <summary>Index into <see cref="BotBrain.Skills"/>. Defaults to Easy.</summary>
    public int BotSkill = 1;

    public ModeDef Def => Modes.Get(Mode);

    public BotSkillDef Bots => BotBrain.Skill(BotSkill);
    public string BotSkillName => Bots.Name;

    public static readonly int[] TimeLimitChoices = { 0, 120, 180, 300, 600 };

    public string TimeLimitName => TimeLimitSeconds <= 0 ? "Off" : $"{TimeLimitSeconds / 60} min";

    public static string Clock(float seconds)
    {
        if (seconds < 0f) seconds = 0f;
        int total = (int)seconds;
        return $"{total / 60}:{total % 60:00}";
    }
}
