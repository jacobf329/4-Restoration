using Godot;

namespace HitboxClone;

/// <summary>What a class's special button does.</summary>
public enum SpecialKind
{
    /// <summary>Lobbed explosive. Flushes cover and punishes a crowd.</summary>
    Frag,

    /// <summary>Instant radial blast around the user, damaging and shoving.</summary>
    Shockwave,

    /// <summary>Timed movement and fire-rate boost.</summary>
    Overdrive,

    /// <summary>Timed accuracy and projectile-speed boost, with the view zoomed in.</summary>
    Focus,

    // ---- faction specials ----
    //
    // These belong to the faction rather than the class, which is the split the setting wants:
    // class is how you shoot, faction is what you are. Each one is the faction's philosophy
    // expressed as a verb rather than a stat line.

    /// <summary>
    /// Vessels. Damage taken recently is banked and paid back as healing — the faction that
    /// believes humanity was mortality gets the ability that is *about* having been hurt.
    /// </summary>
    SecondWind,

    /// <summary>
    /// Custodians. A pulse that outlines every enemy through walls, for the whole team. Knowledge
    /// as a weapon, and shared, because an archive nobody can read is not an archive.
    /// </summary>
    Revelation,

    /// <summary>
    /// Garden. A thrown seed grows a patch that heals whoever stands in it and slows whoever does
    /// not. The only special that makes ground worth standing on rather than worth avoiding.
    /// </summary>
    Bloom,

    /// <summary>
    /// Muses. A decoy walks off in the direction you were facing while you go quiet. The only one
    /// of the four that lies, from the faction that says the others reduced humanity to a
    /// specification sheet.
    /// </summary>
    Understudy,

    // ---- reinforcement abilities ----

    /// <summary>
    /// The Tragedian. Cannot be killed while it runs, and hits harder the closer to death it gets.
    /// The only ability in the game that wants the fight to have gone badly.
    /// </summary>
    LastStand,

    /// <summary>
    /// The Apologist. Plants a wall of inscribed plate that stops enemy fire and lets your own
    /// through — the one piece of cover in the game that somebody decided to put there.
    /// </summary>
    Barrier,
}

/// <summary>
/// One playable class. Every tuning number for the game lives in this file, the way
/// <c>H:\BattleArena\Chars.cs</c> holds all of BattleArena's — when the feel is wrong, there is
/// exactly one place to go.
/// </summary>
public sealed class ClassDef
{
    public string Name = "";
    public string Role = "";
    public string WeaponName = "";

    /// <summary>How this class's own gun reads in the hands.</summary>
    public WeaponSilhouette Silhouette = WeaponSilhouette.Rifle;
    public string Blurb = "";

    public float Health = 100f;
    public float Speed = 7.0f;          // metres/second
    public float Damage = 20f;          // per pellet/bullet
    public int Pellets = 1;             // >1 spreads into a cone
    public float SpreadDeg = 0f;
    public float FireInterval = 0.35f;  // seconds between shots
    public float Range = 60f;           // metres before the shot expires
    public float ProjectileSpeed = 90f;
    public float DashDistance = 6f;
    public float DashCooldown = 1.4f;

    /// <summary>Upward camera kick per shot, in radians. Decays back to where you were aiming.</summary>
    public float Recoil = 0.018f;

    /// <summary>Vertical field of view while aiming down sights. Lower is more zoomed.</summary>
    public float AdsFov = 42f;

    /// <summary>
    /// Whether aiming raises a true scope — a magnified view through an occluding sight picture,
    /// rather than just leaning in. Only the Marksman has one.
    /// </summary>
    public bool HasScope;

    /// <summary>Whether this weapon mends teammates it lands on. Only the Anatomist's beam does.</summary>
    public bool HealsFriendlies;

    /// <summary>Model base name for this class's own gun. See <see cref="WeaponDef.Model"/>.</summary>
    public string WeaponModel = "";

    /// <summary>
    /// Whether this class can sprint at all.
    ///
    /// False for the heavies, whose whole bargain is that they do not out-manoeuvre anything —
    /// they arrive, and then they are a problem. The Sinew's card had claimed this since the day it
    /// was written and nothing had ever enforced it, which is a card telling the player something
    /// untrue about the thing they just paid five hundred points for.
    /// </summary>
    public bool CanSprint = true;

    // ---- special ability ----

    public SpecialKind Special = SpecialKind.Frag;
    public string SpecialName = "";
    public string SpecialBlurb = "";
    public float SpecialCooldown = 9f;

    /// <summary>Seconds a timed special lasts. Zero for instant ones.</summary>
    public float SpecialDuration;

    /// <summary>Damage at the centre of a blast, falling off to nothing at its edge.</summary>
    public float BlastDamage = 55f;
    public float BlastRadius = 6f;

    /// <summary>Damage per second at point blank, ignoring travel and reloads. Shown in the lobby.</summary>
    public float Dps => Damage * Pellets / FireInterval;

    WeaponDef? weapon;

    /// <summary>
    /// This class's own gun, as a <see cref="WeaponDef"/> so weapon code has one shape to deal
    /// with whether the pawn is carrying its class weapon or something picked up off the floor.
    /// Built once and cached — the values here never change at runtime.
    /// </summary>
    public WeaponDef Weapon => weapon ??= new WeaponDef
    {
        Name = WeaponName,
        Silhouette = Silhouette,
        Damage = Damage,
        Pellets = Pellets,
        SpreadDeg = SpreadDeg,
        FireInterval = FireInterval,
        Range = Range,
        ProjectileSpeed = ProjectileSpeed,
        Recoil = Recoil,
        AdsFov = AdsFov,
        HasScope = HasScope,
        HealsFriendlies = HealsFriendlies,
        Model = WeaponModel,
        Ammo = 0,
    };
}

/// <summary>
/// The four classes, matching the original's roster: a shotgunner, a fast-firing close-range
/// class, a precision class and an all-rounder. Each is meant to beat one of the others at its
/// preferred range, so class choice in the lobby is a real decision.
/// </summary>
public static class Classes
{
    public static readonly ClassDef Tactician = new()
    {
        Name = "Tactician",
        WeaponModel = "shotgun",
        Silhouette = WeaponSilhouette.Shotgun,
        Role = "Close quarters",
        WeaponName = "Shotgun",
        Blurb = "Blows enemies away up close. Punishing at range.",
        Health = 120f,
        Speed = 6.6f,
        // Eight pellets at seventeen is 136 with the whole pattern on target, which kills every
        // class in the game outright — the toughest is the Tactician's own 120. Half a pattern is
        // 68, so two shots still finish anyone.
        //
        // It was 11 a pellet: 88 for a perfect point-blank hit, which is not a kill on anybody, so
        // the reward for closing to shotgun range and landing everything was "now shoot them
        // again". The bargain is meant to be that getting there is the hard part.
        //
        // The cone is tighter and the reach slightly longer to make "all of it hit" a thing that
        // can actually happen rather than a theoretical maximum.
        Damage = 17f,
        Pellets = 8,
        SpreadDeg = 11f,
        FireInterval = 0.78f,
        Range = 30f,
        ProjectileSpeed = 78f,
        DashDistance = 5.5f,
        DashCooldown = 1.5f,
        Recoil = 0.055f,
        AdsFov = 50f,

        Special = SpecialKind.Shockwave,
        SpecialName = "Shockwave",
        SpecialBlurb = "Blast everything around you back. No aiming, no travel time.",
        SpecialCooldown = 9f,
        BlastDamage = 42f,
        BlastRadius = 7.5f,
    };

    public static readonly ClassDef Flanker = new()
    {
        Name = "Flanker",
        WeaponModel = "smg",
        Silhouette = WeaponSilhouette.Smg,
        Role = "Skirmisher",
        WeaponName = "Rapid SMG",
        Blurb = "Fastest on the map. Unloads a lot of bullets, none of them heavy.",
        Health = 85f,
        Speed = 8.4f,
        Damage = 7.5f,
        Pellets = 1,
        SpreadDeg = 4.5f,
        FireInterval = 0.085f,
        Range = 42f,
        ProjectileSpeed = 105f,
        DashDistance = 8f,
        DashCooldown = 1.0f,
        Recoil = 0.009f,
        AdsFov = 48f,

        Special = SpecialKind.Overdrive,
        SpecialName = "Overdrive",
        SpecialBlurb = "Four seconds of far more speed and a much faster trigger.",
        SpecialCooldown = 12f,
        SpecialDuration = 4f,
    };

    public static readonly ClassDef Marksman = new()
    {
        Name = "Marksman",
        WeaponModel = "sniper_rifle",
        Silhouette = WeaponSilhouette.Sniper,
        Role = "Precision",
        WeaponName = "Rifle",
        Blurb = "Devastating when you hit. Hard to play, harder to ignore.",
        Health = 80f,
        Speed = 6.9f,
        Damage = 62f,
        Pellets = 1,
        SpreadDeg = 0f,
        FireInterval = 1.15f,
        Range = 120f,
        ProjectileSpeed = 190f,
        DashDistance = 6f,
        DashCooldown = 1.6f,
        Recoil = 0.075f,
        AdsFov = 16f,
        HasScope = true,

        Special = SpecialKind.Focus,
        SpecialName = "Focus",
        SpecialBlurb = "Zoom in. Dead accurate and near-instant shots, while it lasts.",
        SpecialCooldown = 11f,
        SpecialDuration = 5f,
    };

    public static readonly ClassDef Trooper = new()
    {
        Name = "Trooper",
        WeaponModel = "assault_rifle",
        Role = "All-rounder",
        WeaponName = "Assault Rifle",
        Blurb = "No bad matchups and no free wins. Start here.",
        Health = 100f,
        Speed = 7.4f,
        Damage = 16f,
        Pellets = 1,
        SpreadDeg = 2.2f,
        FireInterval = 0.16f,
        Range = 65f,
        ProjectileSpeed = 130f,
        DashDistance = 6.5f,
        DashCooldown = 1.25f,

        Special = SpecialKind.Frag,
        SpecialName = "Frag",
        SpecialBlurb = "Lob a grenade. The only way to hit someone you cannot see.",
        SpecialCooldown = 8f,
        BlastDamage = 62f,
        BlastRadius = 6.5f,
    };

    public static readonly ClassDef[] All = { Trooper, Flanker, Tactician, Marksman };

    public static ClassDef ByIndex(int i) => All[((i % All.Length) + All.Length) % All.Length];
}

/// <summary>
/// The stat lines behind the faction reinforcements.
///
/// Kept apart from <see cref="Classes"/> because the two lists answer different questions. The four
/// in <c>Classes</c> are a balance problem: each must beat one of the others at its preferred range,
/// and they are tuned against each other constantly. These eight are a *cost* problem — each is
/// meant to be plainly better than a basic class, because you paid for it, and what keeps them
/// honest is the price and the fact that you lose it when you die.
///
/// They are still ordinary <see cref="ClassDef"/>s, so every system that reads a pawn's class works
/// on them unchanged. That is the entire reason the reinforcement system needed almost no new
/// machinery: spawning as the Orchard is a class swap and a health number.
/// </summary>
public static class SpecialClasses
{
    // ---- The Vessels ----

    /// <summary>
    /// The body, insisted upon. Twice a Trooper's health and two thirds its speed, with a gun that
    /// does not stop — the argument being that you deal with it rather than out-manoeuvre it.
    /// </summary>
    public static readonly ClassDef Sinew = new()
    {
        Name = "Sinew",
        WeaponModel = "repeater",
        Silhouette = WeaponSilhouette.Minigun,
        Role = "Heavy",
        WeaponName = "Repeater",
        Blurb = "Enormous and slow, with a gun that does not stop.",
        Health = 220f,
        Speed = 5.0f,
        CanSprint = false,
        Damage = 11f,
        Pellets = 1,
        SpreadDeg = 5.5f,
        FireInterval = 0.075f,
        Range = 46f,
        ProjectileSpeed = 115f,
        DashDistance = 4f,
        DashCooldown = 2.6f,
        Recoil = 0.006f,
        AdsFov = 52f,

        Special = SpecialKind.Shockwave,
        SpecialName = "Stand",
        SpecialBlurb = "Shove everything around you off you.",
        SpecialCooldown = 10f,
        BlastDamage = 38f,
        BlastRadius = 8f,
    };

    /// <summary>
    /// A surgical beam: very short range, very high rate, and the only weapon in the game whose
    /// user wants to be standing next to their own side rather than away from them.
    /// </summary>
    public static readonly ClassDef Anatomist = new()
    {
        Name = "The Anatomist",
        WeaponModel = "surgical_beam",
        Silhouette = WeaponSilhouette.Smg,
        Role = "Surgeon",
        WeaponName = "Surgical Beam",
        Blurb = "Opens anything at arm's length, and mends your own side instead.",
        Health = 145f,
        Speed = 7.3f,
        Damage = 9f,
        Pellets = 1,
        SpreadDeg = 1.2f,
        FireInterval = 0.07f,
        Range = 16f,
        ProjectileSpeed = 150f,
        DashDistance = 7f,
        DashCooldown = 1.2f,
        Recoil = 0.004f,
        AdsFov = 50f,
        HealsFriendlies = true,

        Special = SpecialKind.Overdrive,
        SpecialName = "Triage",
        SpecialBlurb = "Work faster and move faster while you do it.",
        SpecialCooldown = 12f,
        SpecialDuration = 5f,
    };

    // ---- The Custodians ----

    /// <summary>
    /// A spotter with a rifle rather than a sniper with a gimmick. Hits are worth less than the
    /// Marksman's; what they are worth is everyone on your side knowing where that person is.
    /// </summary>
    public static readonly ClassDef Lector = new()
    {
        Name = "The Lector",
        WeaponModel = "marking_rifle",
        Silhouette = WeaponSilhouette.Sniper,
        Role = "Spotter",
        WeaponName = "Marking Rifle",
        Blurb = "Everything it hits stays lit up for your whole team.",
        Health = 105f,
        Speed = 7.7f,
        Damage = 48f,
        Pellets = 1,
        SpreadDeg = 0f,
        FireInterval = 0.9f,
        Range = 140f,
        ProjectileSpeed = 200f,
        DashDistance = 7f,
        DashCooldown = 1.4f,
        Recoil = 0.055f,
        AdsFov = 18f,
        HasScope = true,

        Special = SpecialKind.Revelation,
        SpecialName = "Read the Room",
        SpecialBlurb = "Outlines every enemy through walls, for your whole team.",
        SpecialCooldown = 13f,
        SpecialDuration = 5.5f,
    };

    /// <summary>
    /// The hardest thing in the game to shift off a command post, and almost useless anywhere else.
    /// Slow enough that arriving is the whole play.
    /// </summary>
    public static readonly ClassDef Apologist = new()
    {
        Name = "The Apologist",
        WeaponModel = "sidearm",
        Silhouette = WeaponSilhouette.Shotgun,
        Role = "Shield",
        WeaponName = "Sidearm",
        Blurb = "Plants a wall their fire cannot cross. Slow, and very hard to move.",
        Health = 300f,
        Speed = 5.6f,
        Damage = 14f,
        Pellets = 5,
        SpreadDeg = 9f,
        FireInterval = 0.62f,
        Range = 24f,
        ProjectileSpeed = 80f,
        DashDistance = 4.5f,
        DashCooldown = 2.4f,
        Recoil = 0.04f,
        AdsFov = 52f,

        Special = SpecialKind.Barrier,
        SpecialName = "The Argument",
        SpecialBlurb = "Plant a wall their fire cannot cross and yours can.",
        SpecialCooldown = 14f,
        SpecialDuration = 12f,
    };

    // ---- The Garden ----

    /// <summary>
    /// The only weapon in the game aimed at the floor on purpose. Lobs pods that burst where they
    /// land, so it denies a doorway rather than winning a duel down one.
    /// </summary>
    public static readonly ClassDef Grafter = new()
    {
        Name = "The Grafter",
        WeaponModel = "seed_launcher",
        Silhouette = WeaponSilhouette.Launcher,
        Role = "Denial",
        WeaponName = "Seed Launcher",
        Blurb = "Lobs pods that burst into thorns. Denies ground rather than crossing it.",
        Health = 135f,
        Speed = 7.0f,
        Damage = 22f,
        Pellets = 1,
        SpreadDeg = 1.5f,
        FireInterval = 1.0f,
        Range = 55f,
        ProjectileSpeed = 42f,
        DashDistance = 6f,
        DashCooldown = 1.5f,
        Recoil = 0.03f,
        AdsFov = 48f,

        Special = SpecialKind.Bloom,
        SpecialName = "Thicket",
        SpecialBlurb = "Grows ground that mends your side and mires everyone else.",
        SpecialCooldown = 12f,

        BlastDamage = 34f,
        BlastRadius = 5.5f,
    };

    /// <summary>
    /// A walking greenhouse. The slowest thing on two legs, and the only one that makes the ground
    /// around it worth standing on for everybody wearing your colour.
    /// </summary>
    public static readonly ClassDef Orchard = new()
    {
        Name = "The Orchard",
        WeaponModel = "pod_thrower",
        Silhouette = WeaponSilhouette.Shotgun,
        Role = "Sustain",
        WeaponName = "Pod Thrower",
        Blurb = "Vast and slow. Everything of yours near it heals.",
        Health = 340f,
        Speed = 4.6f,
        CanSprint = false,
        Damage = 13f,
        Pellets = 6,
        SpreadDeg = 13f,
        FireInterval = 0.85f,
        Range = 22f,
        ProjectileSpeed = 60f,
        DashDistance = 3.5f,
        DashCooldown = 3f,
        Recoil = 0.05f,
        AdsFov = 55f,

        Special = SpecialKind.Bloom,
        SpecialName = "Season",
        SpecialBlurb = "Everything of yours nearby mends. Everything else wades.",
        SpecialCooldown = 14f,
    };

    // ---- The Muses ----

    /// <summary>
    /// The fastest thing in the game and the flimsiest. Two guns and no answer to being hit.
    /// </summary>
    public static readonly ClassDef Chorus = new()
    {
        Name = "The Chorus",
        WeaponModel = "machine_pistols",
        Silhouette = WeaponSilhouette.Smg,
        Role = "Swarm",
        WeaponName = "Paired Machine Pistols",
        Blurb = "Fastest thing on the map, and never quite alone.",
        Health = 95f,
        Speed = 9.6f,
        Damage = 6.5f,
        Pellets = 2,
        SpreadDeg = 6.5f,
        FireInterval = 0.1f,
        Range = 34f,
        ProjectileSpeed = 110f,
        DashDistance = 9.5f,
        DashCooldown = 0.85f,
        Recoil = 0.008f,
        AdsFov = 50f,

        Special = SpecialKind.Understudy,
        SpecialName = "Ensemble",
        SpecialBlurb = "Sends copies walking on while you go quiet.",
        SpecialCooldown = 11f,
        SpecialDuration = 6f,
    };

    /// <summary>
    /// A duellist that wants the fight to have gone badly. Ordinary at full health and frightening
    /// under a third, which is the only stat line in the game that rewards being nearly dead.
    /// </summary>
    public static readonly ClassDef Tragedian = new()
    {
        Name = "The Tragedian",
        WeaponModel = "prop_blade",
        Silhouette = WeaponSilhouette.Blade,
        Role = "Duellist",
        WeaponName = "Prop Blade",
        Blurb = "Cannot be killed while the act runs, and hits harder the nearer it gets.",
        Health = 150f,
        Speed = 8.2f,
        Damage = 46f,
        Pellets = 2,
        SpreadDeg = 22f,
        FireInterval = 0.4f,
        Range = 5f,
        ProjectileSpeed = 65f,
        DashDistance = 8.5f,
        DashCooldown = 1.0f,
        Recoil = 0.02f,
        AdsFov = 55f,

        Special = SpecialKind.LastStand,
        SpecialName = "Final Act",
        SpecialBlurb = "Cannot be killed while it runs, and hits harder the nearer it gets.",
        SpecialCooldown = 16f,
        SpecialDuration = 7f,
    };

    public static readonly ClassDef[] All =
        { Sinew, Anatomist, Lector, Apologist, Grafter, Orchard, Chorus, Tragedian };
}
