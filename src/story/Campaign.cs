using Godot;

namespace HitboxClone;

/// <summary>
/// The six acts of story mode, in order. See <c>STORY.md</c>, which is the authority on what each
/// one is *for*; this enum only fixes the order and gives the rest of the code something to switch
/// on.
/// </summary>
public enum Act
{
    /// <summary>A normal life. The simulated childhood, and the movement tutorial inside it.</summary>
    Childhood,

    /// <summary>The harvest question, and the only choice that changes the rest of the game.</summary>
    Harvest,

    /// <summary>The Garden's vault, and the meeting the Vessels arrange there.</summary>
    Vault,

    /// <summary>The Custodians' library, and what they finally tell him he is for.</summary>
    Library,

    /// <summary>Ingenuity's camps, preserved, by people who are not ashamed of them.</summary>
    Camps,

    /// <summary>The ruling. What harm is, said out loud by the only thing that can say it.</summary>
    Arbiter,
}

/// <summary>
/// What John decided when the Garden asked for his sperm.
///
/// Deliberately three values rather than a bool. "Not yet asked" is a real state that the first two
/// acts are played in, and collapsing it into one of the answers would mean the game had to pretend
/// he had already decided something during his own childhood.
/// </summary>
public enum HarvestChoice
{
    Undecided,

    /// <summary>He allowed it. The Garden and Ingenuity got what they wanted.</summary>
    Harvested,

    /// <summary>He refused, and waited for the Vessels to find him someone.</summary>
    Waited,
}

/// <summary>
/// Whose side he took when the question was put to him.
///
/// Separate from <see cref="HarvestChoice"/> because two delegations share each answer, and which
/// of the two he walked over to is not the same fact as what he decided. The Garden wants his body
/// because life is sacred and Ingenuity want it because an idle asset is a waste; a man who stood
/// with one of them has said something different about himself than a man who stood with the
/// other, and Act III is owed that difference even though both of them harvested him.
/// </summary>
public enum Delegation
{
    /// <summary>Nobody. The state the first two acts are played in.</summary>
    None,

    Garden,
    Ingenuity,
    Custodians,
    Vessels,
}

/// <summary>
/// Where a player is in the campaign, and the two things about them that the acts read.
///
/// Held apart from <see cref="MatchSettings"/> on purpose: a campaign is not a versus mode. It has
/// no score limit, no bot skill and no arena picker, and adding it to <see cref="Modes.All"/> would
/// have meant inventing a limit and a noun for something that counts nothing — as well as putting a
/// story in the list a player cycles through looking for Deathmatch.
/// </summary>
public sealed class CampaignState
{
    const string Path = "user://campaign.cfg";

    public Act Act { get; private set; } = Act.Childhood;

    public HarvestChoice Choice { get; private set; } = HarvestChoice.Undecided;

    /// <summary>Whose floor he was standing on when he answered. See <see cref="Delegation"/>.</summary>
    public Delegation Sided { get; private set; } = Delegation.None;

    /// <summary>
    /// How far she has come round, from 0 to 1.
    ///
    /// One number, because the thing it drives is one thing: how she behaves in a fight. Early she
    /// takes her own line and ignores his marks; high, she covers the door he turned his back on.
    /// A relationship expressed as a stat is a poor relationship, but a relationship expressed as
    /// *what somebody does when it matters* is the only kind this game has the vocabulary for, and
    /// this is the number that decides it.
    ///
    /// Starts above zero. She is hesitant, not hostile, and the difference matters: the story is
    /// about someone in an impossible position rather than someone who dislikes him.
    /// </summary>
    public float Affinity { get; private set; } = StartingAffinity;

    public const float StartingAffinity = 0.2f;

    /// <summary>
    /// Above this she covers him rather than merely fighting near him.
    ///
    /// Named rather than sprinkled through the bot brain as 0.6f, because it is the moment the
    /// relationship becomes visible to the player and it will be re-tuned by feel.
    /// </summary>
    public const float CoveringAt = 0.6f;

    /// <summary>Whether she is present at all. She joins at the vault and stays.</summary>
    public bool SheIsWith => Act >= Act.Vault;

    /// <summary>
    /// Whether she would say yes if he asked now.
    ///
    /// Deliberately a high bar, and deliberately reachable in both branches. She can refuse him at
    /// the end and be right to — a romance the player cannot fail is not one he can win.
    /// </summary>
    public bool WouldSayYes => Affinity >= 0.85f;

    /// <summary>Record the answer to the harvest question. It is asked once and never revisited.</summary>
    public void Decide(HarvestChoice choice)
    {
        if (choice == HarvestChoice.Undecided) return;
        if (Choice != HarvestChoice.Undecided) return;
        Choice = choice;
    }

    /// <summary>
    /// Record whose side he took. Guarded the same way <see cref="Decide"/> is and for the same
    /// reason: the room asks once, and a second answer would be the game changing its mind about
    /// something the player already lived through.
    /// </summary>
    public void SideWith(Delegation who)
    {
        if (who == Delegation.None) return;
        if (Sided != Delegation.None) return;
        Sided = who;
    }

    /// <summary>
    /// Move her opinion of him, and keep it inside the range.
    ///
    /// Clamped here rather than at the call sites, so a scene that hands out a large swing cannot
    /// silently park the value out of bounds and make every threshold above meaningless.
    /// </summary>
    public void Warm(float delta) => Affinity = MathU.Clamp01(Affinity + delta);

    /// <summary>
    /// Finish the current act and move to the next, or report that there is no next.
    ///
    /// The harvest act refuses to advance while the choice is outstanding. That is the one piece of
    /// sequencing worth enforcing in code rather than trusting the mission script to remember:
    /// every act after it reads <see cref="Choice"/>, and an Undecided leaking into the vault would
    /// not crash anything — it would quietly play the neutral version of every scene from there on.
    /// </summary>
    public bool Advance()
    {
        if (Act == Act.Harvest && Choice == HarvestChoice.Undecided) return false;
        if (Act == Act.Arbiter) return false;

        Act++;
        return true;
    }

    public bool Finished => Act == Act.Arbiter;

    /// <summary>Start again from the beginning, forgetting the choice and her along with it.</summary>
    public void Reset()
    {
        Act = Act.Childhood;
        Choice = HarvestChoice.Undecided;
        Sided = Delegation.None;
        Affinity = StartingAffinity;
    }

    // ---- persistence ----
    //
    // Its own file rather than a section in settings.cfg. Settings are about the machine — bindings,
    // quality, whether keyboards can play — and are worth keeping when a player wipes a save. A
    // campaign is a story someone is part-way through, and the two have no business sharing a file
    // that either of them can clobber.

    public void Save()
    {
        var cfg = new ConfigFile();
        cfg.SetValue("campaign", "act", (int)Act);
        cfg.SetValue("campaign", "choice", (int)Choice);
        cfg.SetValue("campaign", "sided", (int)Sided);
        cfg.SetValue("campaign", "affinity", Affinity);
        cfg.Save(Path);
    }

    /// <summary>
    /// Read a saved campaign, or a fresh one if there is not one.
    ///
    /// Every field is clamped on the way in rather than trusted. A save file is a text file in a
    /// folder the player can open, and an act index of 97 should start somebody at the beginning
    /// rather than throw on a cast that has no valid enum to land on.
    /// </summary>
    public static CampaignState Load()
    {
        var state = new CampaignState();

        var cfg = new ConfigFile();
        if (cfg.Load(Path) != Error.Ok) return state;

        int act = cfg.GetValue("campaign", "act", (int)Act.Childhood).AsInt32();
        int choice = cfg.GetValue("campaign", "choice", (int)HarvestChoice.Undecided).AsInt32();
        int sided = cfg.GetValue("campaign", "sided", (int)Delegation.None).AsInt32();
        float affinity = (float)cfg.GetValue("campaign", "affinity", StartingAffinity).AsDouble();

        state.Act = (Act)Mathf.Clamp(act, (int)Act.Childhood, (int)Act.Arbiter);
        state.Choice = (HarvestChoice)Mathf.Clamp(choice, (int)HarvestChoice.Undecided,
                                                  (int)HarvestChoice.Waited);
        state.Sided = (Delegation)Mathf.Clamp(sided, (int)Delegation.None, (int)Delegation.Vessels);
        state.Affinity = MathU.Clamp01(affinity);

        // A save that claims to be past the harvest with nothing decided is corrupt rather than
        // merely odd, and the acts after it would all play their neutral version. Sending it back
        // to the question is the only repair that does not invent an answer on the player's behalf.
        if (state.Act > Act.Harvest && state.Choice == HarvestChoice.Undecided)
            state.Act = Act.Harvest;

        return state;
    }
}

/// <summary>
/// What an act is called and whose it is. Presentation data, kept beside the enum so that adding an
/// act cannot leave a gap that only shows up as a blank chapter title.
/// </summary>
public sealed class ActDef
{
    public Act Act;
    public string Name = "";

    /// <summary>The faction whose case this act is, or null for the two that belong to nobody.</summary>
    public FactionDef? Host;

    /// <summary>One line, for a chapter card.</summary>
    public string Blurb = "";
}

public static class Acts
{
    public static readonly ActDef[] All =
    {
        new() { Act = Act.Childhood, Host = Factions.Vessels, Name = "A Normal Life",
                Blurb = "A town, a school, a family. All of it built for you." },

        new() { Act = Act.Harvest, Host = null, Name = "The Harvest Question",
                Blurb = "Four civilisations argue about your body while you stand there." },

        new() { Act = Act.Vault, Host = Factions.Garden, Name = "The Vault",
                Blurb = "Everything they saved, catalogued and cold. Including your mother." },

        new() { Act = Act.Library, Host = Factions.Custodians, Name = "The Library",
                Blurb = "The first people to answer your questions, and what they want for it." },

        new() { Act = Act.Camps, Host = Factions.Ingenuity, Name = "The Camps",
                Blurb = "Preserved, like everything else they made. They are not sorry." },

        new() { Act = Act.Arbiter, Host = null, Name = "The Arbiter",
                Blurb = "Say what harm is. They have waited an age to be told." },
    };

    public static ActDef Get(Act act) => All[(int)act];
}
