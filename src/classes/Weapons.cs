using Godot;

namespace HitboxClone;

/// <summary>
/// What a weapon looks like in the holder's hands.
///
/// A silhouette rather than a model. Everything in this game is boxes, and at the field of view a
/// held weapon occupies, the only things that actually register are overall length, bulk, and one
/// or two distinguishing lumps — a scope, a barrel cluster, a blade. Picking a shape from this
/// list is enough to tell a minigun from a sword at a glance, which is the whole job.
/// </summary>
public enum WeaponSilhouette { Rifle, Shotgun, Smg, Sniper, Minigun, Blade, Launcher, Portal, Grapple }

/// <summary>
/// A gun, separated from the class that normally carries it.
///
/// Splitting this out is what makes pickups possible: a pickup swaps the weapon and leaves health,
/// speed, dash and special alone, so a Flanker with a railgun is still a fast, fragile Flanker
/// rather than turning into a Marksman.
/// </summary>
public sealed class WeaponDef
{
    public string Name = "";

    /// <summary>How this reads in the hands. See <see cref="WeaponSilhouette"/>.</summary>
    public WeaponSilhouette Silhouette = WeaponSilhouette.Rifle;

    public float Damage = 20f;
    public int Pellets = 1;
    public float SpreadDeg;
    public float FireInterval = 0.35f;
    public float Range = 60f;
    public float ProjectileSpeed = 90f;
    public float Recoil = 0.018f;
    public float AdsFov = 42f;
    public bool HasScope;

    /// <summary>Blast damage at the centre of the impact. Zero means the round does not explode.</summary>
    public float BlastDamage;

    /// <summary>Metres the blast reaches. Damage falls off linearly to nothing at the edge.</summary>
    public float BlastRadius;

    public bool Explodes => BlastDamage > 0f && BlastRadius > 0f;

    /// <summary>
    /// Seconds a round waits before going off, or zero to detonate on contact.
    ///
    /// This is the whole difference between a rocket and a grenade, and it is a difference of kind
    /// rather than degree. A rocket is aimed at a person: you have to see them, and the trade is
    /// that seeing them means they can see you. A grenade is aimed at a *place* — around a corner,
    /// through a doorway, over a wall — and what you buy with the fuse is the ability to hurt
    /// something you are not looking at.
    /// </summary>
    public float FuseTime;

    /// <summary>
    /// Whether a round bounces off the world instead of expiring on it.
    ///
    /// Only meaningful with a fuse: a round that bounces and detonates on contact would simply
    /// detonate on the first bounce. The two together are what make a grenade a thinking weapon —
    /// the throw you want is usually the one off a wall.
    /// </summary>
    public bool Bounces;

    /// <summary>How hard gravity pulls on a round in flight, as a fraction of a pawn's own.</summary>
    public float Weight;

    /// <summary>
    /// Whether a round landing on your own side mends them instead of hurting them.
    ///
    /// One flag rather than a healing weapon, because the projectile path is the one loop every
    /// weapon in the game runs through and a second one for a medic would be a second set of
    /// tunnelling bugs. It also gives the Anatomist exactly the shape it wants: the beam does not
    /// care who it is pointed at, and choosing is the whole job.
    /// </summary>
    public bool HealsFriendlies;

    /// <summary>A weapon that cannot hurt anyone. The portal gun and the grapple.</summary>
    public bool IsUtility => Damage <= 0f && BlastDamage <= 0f;

    /// <summary>Shots before a picked-up weapon runs dry. Zero means unlimited — a class weapon.</summary>
    public int Ammo;

    /// <summary>
    /// Model base name under <c>assets/weapons</c>, or empty to use the box silhouette.
    ///
    /// The loader falls back to <see cref="Silhouette"/> whenever a file is missing, so models can
    /// arrive one at a time without a code change and without the rest of the armoury looking
    /// broken while they do — which is how these were built before a single one existed.
    /// </summary>
    public string Model = "";

    /// <summary>
    /// Whether a round anchors where it lands and hauls the shooter to it.
    ///
    /// Same shape as <see cref="PlantsPortal"/> and for the same reason: the projectile path is
    /// one ray sweep that every weapon in the game shares, and a second one for a grapple would be
    /// a second set of tunnelling bugs to find.
    /// </summary>
    public bool Grapples;

    /// <summary>
    /// Whether a round plants a portal where it lands instead of doing damage.
    ///
    /// A flag rather than a subclass because the projectile path is one loop that every weapon in
    /// the game shares — bullets, pellets, shells and swings all resolve through the same ray
    /// sweep, and a second projectile system for one gun would be a second set of tunnelling bugs.
    /// </summary>
    public bool PlantsPortal;

    public float Dps => Damage * Pellets / FireInterval;
}

/// <summary>
/// The weapons that exist as map pickups. Deliberately more extreme than any class weapon: a
/// pickup should feel like an event, and something you route across the arena to reach.
/// </summary>
public static class Weapons
{
    public static readonly WeaponDef Railgun = new()
    {
        Name = "Railgun",
        Model = "railgun",
        Silhouette = WeaponSilhouette.Sniper,
        Damage = 105f,
        FireInterval = 1.5f,
        SpreadDeg = 0f,
        Range = 160f,
        ProjectileSpeed = 320f,
        Recoil = 0.11f,
        AdsFov = 14f,
        HasScope = true,
        Ammo = 5,
    };

    /// <summary>
    /// A scoped battle rifle, and the reason it exists: the Marksman's sight was the one thing on
    /// the map you could not go and take. The railgun already had <c>HasScope</c> set, but the
    /// scope was drawn from the *class* rather than from the weapon in hand, so picking it up as
    /// anyone else gave you its damage and none of its optics.
    ///
    /// Deliberately not a railgun. It fires four times as fast for less than half the damage, so
    /// it rewards a steady hand at range rather than one perfect shot, and it carries enough ammo
    /// to hold a lane with.
    /// </summary>
    public static readonly WeaponDef Longshot = new()
    {
        Name = "Longshot",
        Model = "longshot",
        Silhouette = WeaponSilhouette.Sniper,
        Damage = 44f,
        FireInterval = 0.58f,
        SpreadDeg = 0.5f,
        Range = 130f,
        ProjectileSpeed = 240f,
        Recoil = 0.055f,
        AdsFov = 19f,
        HasScope = true,
        Ammo = 18,
    };

    public static readonly WeaponDef Minigun = new()
    {
        Name = "Minigun",
        Model = "minigun",
        Silhouette = WeaponSilhouette.Minigun,
        Damage = 6.5f,
        FireInterval = 0.055f,
        SpreadDeg = 7.5f,
        Range = 55f,
        ProjectileSpeed = 120f,
        Recoil = 0.006f,
        AdsFov = 50f,
        Ammo = 160,
    };

    public static readonly WeaponDef Scattergun = new()
    {
        Name = "Scattergun",
        Model = "scattergun",
        Silhouette = WeaponSilhouette.Shotgun,
        Damage = 15f,
        Pellets = 11,
        SpreadDeg = 16f,
        FireInterval = 0.62f,
        Range = 30f,
        ProjectileSpeed = 85f,
        Recoil = 0.085f,
        AdsFov = 52f,
        Ammo = 12,
    };

    /// <summary>
    /// The rocket launcher.
    ///
    /// The direct hit is deliberately the smaller half of it, the same way the tank cannon is: this
    /// is a weapon you aim at the floor under someone, not one you thread at them. A slow shell you
    /// can see coming is also what makes it fair — at 52 m/s a rocket takes most of a second to
    /// cross open ground, which is long enough to move.
    ///
    /// It hurts the person holding it. Firing at your own feet is a rocket jump and a mistake in
    /// equal measure, and which one it was is the player's business.
    /// </summary>
    public static readonly WeaponDef RocketLauncher = new()
    {
        Name = "Rocket Launcher",
        Model = "rocket_launcher",
        Silhouette = WeaponSilhouette.Launcher,
        Damage = 40f,
        FireInterval = 1.15f,
        SpreadDeg = 0.8f,
        Range = 110f,
        ProjectileSpeed = 52f,
        Recoil = 0.10f,
        AdsFov = 46f,
        BlastDamage = 105f,
        BlastRadius = 7f,
        Ammo = 6,
    };

    /// <summary>
    /// The portal gun.
    ///
    /// Not a weapon — it does no damage at all. Each shot plants a gate where it lands, and once
    /// two are up, anything that walks into one comes out of the other. Both belong to the arena
    /// rather than to whoever made them: an enemy can use your portals, which is what turns it from
    /// a movement tool into a decision.
    ///
    /// Worth being plain about what this is not: these are gates, not windows. You cannot see or
    /// shoot through them. A true see-through portal means rendering the world a second time from
    /// the far gate, per portal, per viewport — on a four-way splitscreen that is eight extra
    /// renders of the arena for one pickup.
    /// </summary>
    public static readonly WeaponDef PortalGun = new()
    {
        Name = "Portal Gun",
        Model = "portal_gun",
        Silhouette = WeaponSilhouette.Portal,
        Damage = 0f,
        FireInterval = 0.85f,
        SpreadDeg = 0f,
        ProjectileSpeed = 70f,
        Recoil = 0.01f,
        AdsFov = 50f,
        PlantsPortal = true,
        Ammo = 10,

        // Twice the reach of anything else on the floor, and the one weapon that should have it.
        // Range on a gun is how far you can hurt someone; range on this is how far apart you can
        // put the two ends of a shortcut, which is the whole point of carrying it. At 120m you
        // could only ever link two places already within sight of each other on a map 278m across.
        //
        // The round still travels at 70 m/s, so a shot to the far end of that range takes about
        // three and a half seconds to land. That is deliberate: it is a placement you commit to
        // and can be punished for, not a thing you flick out mid-fight.
        Range = 240f,
    };

    /// <summary>
    /// The grappling hook.
    ///
    /// Fire it at anything solid and it hauls you to where it stuck. Not a swing — a winch. A
    /// pendulum needs rope constraints, a rope needs a solver, and a solver needs to agree with a
    /// character controller that resolves its own collisions; a straight pull to the anchor is a
    /// tenth of the machinery and reads as the same verb from inside the helmet.
    ///
    /// It is the counterpart to the portal gun rather than a duplicate of it. The portal gun links
    /// two places you have already been and pays off later; the grapple gets you somewhere *now*,
    /// including places nothing else on the map can reach. Both do no damage, which is the price.
    ///
    /// Fast and cheap to fire, because the fun is in using it constantly.
    /// </summary>
    public static readonly WeaponDef Grapple = new()
    {
        Name = "Grapple",
        Model = "grapple_gun",
        // Five times the reach it had. A rope that only works across a courtyard is a rope you
        // never quite have when you want it; at this range it crosses most of an arena, which is
        // what makes it a movement tool rather than a novelty.
        Silhouette = WeaponSilhouette.Grapple,
        Damage = 0f,
        FireInterval = 1.1f,
        SpreadDeg = 0f,
        Range = 425f,
        ProjectileSpeed = 165f,      // the line snaps out; it is the travel that takes the time
        Recoil = 0.01f,
        AdsFov = 50f,
        Grapples = true,
        Ammo = 14,
    };

    /// <summary>
    /// A melee weapon, built out of the same projectile system as everything else: a very fast,
    /// very short-ranged shot reads as a swing and needs no separate hit-detection path.
    /// </summary>
    public static readonly WeaponDef Sword = new()
    {
        Name = "Sword",
        Model = "sword",
        Silhouette = WeaponSilhouette.Blade,
        Damage = 88f,
        FireInterval = 0.42f,
        SpreadDeg = 26f,      // a wide arc rather than a point — it is a swing
        Pellets = 3,
        Range = 4.2f,
        ProjectileSpeed = 55f,
        Recoil = 0.03f,
        AdsFov = 55f,
        Ammo = 24,
    };

    /// <summary>
    /// What a juggernaut carries, and the only thing they carry.
    ///
    /// A hero with a gun is just a player with more health, and the numbers say so: a crowned
    /// Achilles with a rifle was four and a half times as hard to kill and fought exactly like
    /// everyone else, which is why becoming one felt like a stat bonus rather than an event.
    ///
    /// Taking the guns away is what makes the whole mode work. It forces the juggernaut to close,
    /// which gives everyone else a reason to hold ground and a reason to run, and it makes the
    /// swarm the correct answer rather than a desperate one. Nobody trades with this at arm's
    /// length; you trade with it by never being at arm's length.
    ///
    /// Two hundred and forty over three swings in a wide arc kills any class outright and takes a
    /// third off another juggernaut, and unlike the pickup sword it never runs out.
    /// </summary>
    public static readonly WeaponDef Saber = new()
    {
        Name = "Saber",
        Model = "saber",
        Silhouette = WeaponSilhouette.Blade,
        Damage = 80f,
        FireInterval = 0.55f,
        SpreadDeg = 34f,      // a wider arc than the pickup sword — it clears a crowd
        Pellets = 3,
        Range = 5.4f,
        ProjectileSpeed = 70f,
        Recoil = 0.02f,
        AdsFov = 60f,
        Ammo = 0,             // never runs dry: it is not a pickup, it is what you are
    };


    /// <summary>
    /// The grenade launcher.
    ///
    /// A third again as heavy as the rocket launcher, and much harder to land, which is the bargain.
    /// A rocket is a straight line at somebody you can see. A grenade arcs, bounces, and waits two
    /// seconds — so it is aimed at a place rather than a person, and the places worth aiming at are
    /// the ones you cannot see into. On a map full of rooms and doorways that is a different weapon
    /// from anything else on the floor, and it is the reason the interiors needed one.
    ///
    /// It is genuinely dangerous to its owner. Two seconds is long enough to walk into your own
    /// shot, the blast hurts the thrower like every other explosive here, and a bounce off a
    /// doorframe comes back at you. That is the cost of the biggest number in the game.
    /// </summary>
    public static readonly WeaponDef GrenadeLauncher = new()
    {
        Name = "Grenade Launcher",
        Model = "grenade_launcher",
        Silhouette = WeaponSilhouette.Launcher,
        Damage = 18f,                 // the shell itself barely matters; the blast is the weapon
        FireInterval = 1.35f,
        SpreadDeg = 1.2f,
        Range = 130f,
        ProjectileSpeed = 34f,        // slow and lobbed — you lead with it, or you bank it
        Recoil = 0.11f,
        AdsFov = 46f,

        // A third again the rocket's 105. This one-shots every class in the game at the centre and
        // still kills most of them at the edge, which is exactly what "very powerful" has to mean
        // for a weapon this hard to place.
        BlastDamage = 137f,
        BlastRadius = 8.5f,

        FuseTime = 2f,
        Bounces = true,
        Weight = 1f,

        Ammo = 5,
    };

    /// <summary>
    /// The flamethrower.
    ///
    /// The only weapon here that is not really aimed. It fills a volume rather than hitting a
    /// point, which makes it the natural answer to everything the interiors introduced — a doorway,
    /// a corridor, a room somebody is holding — and useless the moment you step outside.
    ///
    /// Built out of the same pellets everything else uses. A wide cone of very short-ranged,
    /// individually feeble rounds fired very fast reads as a stream, and needs no separate
    /// simulation to do it: at nine metres with a thirty-degree spread the pellets overlap into a
    /// cloud, and the damage you take is a function of how long you stood in it. Which is what a
    /// flamethrower is.
    ///
    /// A hundred and forty a second at point blank is above every class weapon in the game, and it
    /// falls off a cliff past nine metres, where every one of them beats it.
    /// </summary>
    public static readonly WeaponDef Flamethrower = new()
    {
        Name = "Flamethrower",
        Model = "flamethrower",
        Silhouette = WeaponSilhouette.Launcher,
        Damage = 10f,
        Pellets = 4,
        SpreadDeg = 30f,
        FireInterval = 0.18f,
        Range = 9f,
        ProjectileSpeed = 26f,        // slow enough to see the cloud arrive
        Recoil = 0.002f,
        AdsFov = 55f,
        Ammo = 90,
    };
    // Ordered so that consecutive crates are as unlike each other as possible: a sniper, then a
    // bullet hose, then an explosive, and so on. The crate list is walked in order, so putting the
    // two scoped rifles next to each other would mean whole corners of a map offering the same
    // fight twice.
    //
    // The portal gun appears three times, which is the only entry that repeats.
    //
    // One slot in ten meant a given arena laid out perhaps one of them, in one corner, and a
    // crate that has already been taken is indistinguishable from a crate that was never there —
    // so the gun that changes how you move around a map was something players never reliably
    // found. At three in twelve it is a quarter of the floor and you can go looking for one with
    // some expectation of success. The three are spread across the order rather than adjacent, so
    // the "unlike your neighbours" rule above still holds at every position.
    public static readonly WeaponDef[] Pickups =
        {
            Railgun, Minigun, PortalGun, RocketLauncher, Grapple, Longshot,
            Flamethrower, PortalGun, Scattergun, GrenadeLauncher, PortalGun, Sword,
        };

    public static WeaponDef ByIndex(int i)
        => Pickups[((i % Pickups.Length) + Pickups.Length) % Pickups.Length];

    /// <summary>Colour used for a pickup's crate and its HUD label.</summary>
    public static Color TintFor(WeaponDef w)
    {
        if (w == Railgun) return new Color(0.62f, 0.45f, 0.98f);
        if (w == Minigun) return new Color(0.98f, 0.62f, 0.25f);
        if (w == Longshot) return new Color(0.42f, 0.72f, 0.98f);
        if (w == RocketLauncher) return new Color(0.98f, 0.34f, 0.22f);
        if (w == PortalGun) return new Color(0.30f, 0.88f, 0.98f);
        if (w == Grapple) return new Color(0.85f, 0.90f, 0.42f);
        if (w == Sword) return new Color(0.35f, 0.95f, 0.85f);
        if (w == GrenadeLauncher) return new Color(0.72f, 0.85f, 0.30f);
        if (w == Flamethrower) return new Color(0.99f, 0.55f, 0.16f);
        if (w == Saber) return new Color(0.98f, 0.86f, 0.30f);
        return new Color(0.98f, 0.35f, 0.55f);
    }
}
