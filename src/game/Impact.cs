using Godot;

namespace HitboxClone;

/// <summary>
/// One-shot particle bursts for hits and deaths.
///
/// Each burst is a self-freeing node: it emits once, waits out its own lifetime and removes itself.
/// Nothing has to track them, which matters because they are spawned from the physics step and can
/// overlap heavily during a four-way fight.
///
/// Skipped entirely on Low, where the point is to spend nothing per-pixel.
/// </summary>
public static class Impact
{
    /// <summary>
    /// A spray where a shot landed. Deliberately loud: at range, on a moving target, a small puff
    /// was easy to miss entirely, and landing a shot needs to be unmistakable.
    /// </summary>
    public static void Hit(Node parent, Vector3 at, Vector3 normalish, Color tint)
        => Spawn(parent, at, tint, count: 22, speed: 11f, size: 0.11f, life: 0.42f, dir: normalish);

    /// <summary>A headshot throws a bigger, faster burst than a body hit.</summary>
    public static void Headshot(Node parent, Vector3 at, Vector3 normalish, Color tint)
        => Spawn(parent, at, tint, count: 40, speed: 16f, size: 0.15f, life: 0.55f, dir: normalish);

    /// <summary>A big upward burst in the victim's colour when someone dies.</summary>
    public static void Death(Node parent, Vector3 at, Color tint)
        => Spawn(parent, at + Vector3.Up * 0.9f, tint, count: 64, speed: 13f, size: 0.20f,
                 life: 1.0f, dir: Vector3.Up);

    /// <summary>
    /// Debris thrown outward along the ground. Paired with the upward burst it gives a death a
    /// silhouette that reads from across the arena and from any angle, which a single vertical
    /// plume does not.
    /// </summary>
    public static void DeathRing(Node parent, Vector3 at, Color tint)
        => Spawn(parent, at + Vector3.Up * 0.35f, tint, count: 40, speed: 15f, size: 0.14f,
                 life: 0.8f, dir: Vector3.Up, spread: 180f, gravity: -6f);

    /// <summary>
    /// A vehicle going up.
    ///
    /// Deliberately much bigger than a pawn death: a hull is eight metres long and worth two
    /// hundred to seven hundred health, and it used to be marked by exactly the same puff of debris
    /// a person makes. Three layers — a fireball, heavy debris thrown wide and low, and a slow
    /// smoke column — plus a light flash, so it reads from across the arena and tells everyone
    /// nearby that the thing they were sheltering behind has gone.
    /// </summary>
    public static void VehicleWreck(Node parent, Vector3 at, Color tint)
    {
        var fire = new Color(1f, 0.62f, 0.22f);

        Spawn(parent, at + Vector3.Up * 1.2f, fire, count: 90, speed: 20f, size: 0.55f,
              life: 1.1f, dir: Vector3.Up, spread: 120f, gravity: -8f);

        Spawn(parent, at + Vector3.Up * 0.5f, tint, count: 70, speed: 24f, size: 0.30f,
              life: 1.5f, dir: Vector3.Up, spread: 180f, gravity: -20f);

        Spawn(parent, at + Vector3.Up * 2.0f, new Color(0.22f, 0.20f, 0.19f), count: 40, speed: 5f,
              size: 1.1f, life: 2.4f, dir: Vector3.Up, spread: 60f, gravity: 2f);

        Flash(parent, at + Vector3.Up * 1.4f, fire);
    }

    /// <summary>
    /// A brief point light at the blast. One light for a fraction of a second is affordable even in
    /// four-way splitscreen, and it is what makes an explosion light the walls around it rather
    /// than being a sprite pasted over them.
    /// </summary>
    static void Flash(Node parent, Vector3 at, Color colour)
    {
        if (UserSettings.Quality == GraphicsQuality.Low) return;

        var light = new OmniLight3D
        {
            LightColor = colour,
            LightEnergy = 14f,
            OmniRange = 22f,
            ShadowEnabled = false,
            Position = at,
        };
        parent.AddChild(light);

        var tween = light.CreateTween();
        tween.TweenProperty(light, "light_energy", 0f, 0.45f);
        tween.TweenCallback(Callable.From(light.QueueFree));
    }

    static void Spawn(Node parent, Vector3 at, Color tint, int count, float speed, float size,
                      float life, Vector3 dir, float spread = 88f, float gravity = -14f)
    {
        if (UserSettings.Quality == GraphicsQuality.Low) return;

        var process = new ParticleProcessMaterial
        {
            Direction = dir.LengthSquared() < 0.01f ? Vector3.Up : dir.Normalized(),

            // Near-hemispherical by default, so debris genuinely sprays outward from the impact
            // rather than travelling as a narrow jet that only reads from one viewing angle.
            Spread = spread,
            InitialVelocityMin = speed * 0.35f,
            InitialVelocityMax = speed,
            Gravity = new Vector3(0f, gravity, 0f),
            ScaleMin = 0.5f,
            ScaleMax = 1.0f,
            Damping = new Vector2(1.5f, 3.5f),
        };

        // Debris in the colour of whatever it came off, so a hit on a green opponent reads as a
        // hit on that opponent and not as generic sparks.
        var ramp = new Gradient();
        ramp.SetColor(0, tint.Lightened(0.35f));
        ramp.SetColor(1, new Color(tint.R, tint.G, tint.B, 0f));
        process.ColorRamp = new GradientTexture1D { Gradient = ramp };

        var particles = new GpuParticles3D
        {
            Amount = count,
            Lifetime = life,
            OneShot = true,
            Explosiveness = 1f,
            ProcessMaterial = process,
            DrawPass1 = new BoxMesh { Size = new Vector3(size, size, size) },
            MaterialOverride = Graphics.Hot(tint, 1.6f),
            Emitting = true,
        };

        parent.AddChild(particles);
        particles.GlobalPosition = at;

        // Self-cleaning: the timer outlives the longest possible particle, then takes the whole
        // node with it.
        var timer = new Godot.Timer { OneShot = true, WaitTime = life + 0.4f, Autostart = true };
        particles.AddChild(timer);
        timer.Timeout += particles.QueueFree;
    }
}
