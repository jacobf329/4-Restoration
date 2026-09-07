using Godot;

namespace HitboxClone;

public enum GraphicsQuality { Low, Medium, High }

/// <summary>
/// Rendering setup, expressed as three quality tiers.
///
/// A tier exists because splitscreen multiplies everything: four viewports each pay for shadows and
/// screen-space effects, so what is comfortable in one view can be four times too expensive in four.
/// Low turns off every per-pixel effect and is the safe fallback.
///
/// Nothing here needs an asset file. The sky is procedural, the materials are generated, and the
/// look still comes from flat colour and shape — this sharpens the minimalist style rather than
/// replacing it.
/// </summary>
public static class Graphics
{
    public static readonly string[] QualityNames = { "Low", "Medium", "High" };

    public static GraphicsQuality Quality => UserSettings.Quality;

    public static bool Shadows => Quality != GraphicsQuality.Low;
    public static bool Glow => Quality != GraphicsQuality.Low;

    /// <summary>
    /// Ambient occlusion earns its cost on stacked boxes — it draws the seam where two surfaces
    /// meet, which flat shading leaves invisible — so Medium gets a cheap version rather than
    /// none at all.
    /// </summary>
    public static bool AmbientOcclusion => Quality != GraphicsQuality.Low;

    public static bool HighQualityAo => Quality == GraphicsQuality.High;

    public static Viewport.Msaa Msaa => Quality switch
    {
        GraphicsQuality.Low => Viewport.Msaa.Disabled,
        GraphicsQuality.High => Viewport.Msaa.Msaa4X,
        _ => Viewport.Msaa.Msaa2X,
    };

    /// <summary>The sun. Shadows are the single biggest readability win on box geometry — without
    /// them a block sitting on the floor and a block floating above it look identical.</summary>
    public static DirectionalLight3D BuildSun()
    {
        var sun = new DirectionalLight3D
        {
            Name = "Sun",
            // Restrained on purpose. In splitscreen two players routinely look at opposite faces
            // of the same block, so a strong sun means one of them sees near-white and the other
            // sees near-black. The range has to stay narrow enough that both reads are legible.
            LightEnergy = 0.95f,
            LightColor = new Color(1.0f, 0.97f, 0.90f),
            ShadowEnabled = Shadows,

            // A single orthogonal split covering the whole arena, rather than cascades. Cascades
            // are fitted to the active camera's frustum, and with four splitscreen viewports
            // sharing one World3D that fit belongs to whichever camera drew last — leaving the
            // other views uniformly shadowed. One split wide enough for the entire arena is the
            // same for every camera.
            DirectionalShadowMode = DirectionalLight3D.ShadowMode.Orthogonal,

            // One split has to cover everything it is going to cover, and stretching it across the
            // full 345-metre diagonal would spend the whole shadow map on ground nobody is looking
            // at closely. Held at a middle distance instead, where shadows are doing readability
            // work, and the re-ranged fog takes over past it — which is roughly where a shadow
            // would have become a grey smudge anyway.
            DirectionalShadowMaxDistance = 240f,
            ShadowBlur = 1.0f,
        };

        // Steep enough that shadows read as contact with the floor, angled enough that the three
        // visible faces of every box get distinct values.
        sun.RotationDegrees = new Vector3(-56f, 38f, 0f);
        return sun;
    }

    /// <summary>
    /// A dim, cool light from the opposite side, casting no shadows.
    ///
    /// Sky ambient alone leaves every unlit face the same flat value, so a box turned away from
    /// the sun has no shape at all. A fill from the other side gives those faces a gradient, which
    /// is most of what makes the geometry look solid rather than like coloured paper.
    /// </summary>
    public static DirectionalLight3D BuildFill()
    {
        var fill = new DirectionalLight3D
        {
            Name = "Fill",
            LightEnergy = 0.42f,
            LightColor = new Color(0.72f, 0.82f, 1.0f),
            ShadowEnabled = false,
        };

        fill.RotationDegrees = new Vector3(-24f, -142f, 0f);
        return fill;
    }

    /// <summary>
    /// The world environment. <paramref name="fog"/> is off for the map preview: that camera sits
    /// two hundred and fifty metres back, where aerial perspective tuned for a player standing in
    /// the arena washes the entire map to one flat haze. Depth cueing is for looking *through* a
    /// space, not at one.
    /// </summary>
    public static WorldEnvironment BuildEnvironment(bool fog = true)
    {
        var sky = new ProceduralSkyMaterial
        {
            SkyTopColor = new Color(0.28f, 0.40f, 0.60f),
            SkyHorizonColor = new Color(0.62f, 0.71f, 0.83f),

            // The ground half of the sky is what fills shaded faces. Keeping it light and close to
            // neutral is what stops a face turned away from the sun reading as navy.
            GroundHorizonColor = new Color(0.60f, 0.65f, 0.73f),
            GroundBottomColor = new Color(0.52f, 0.57f, 0.65f),
            SunAngleMax = 24f,
            SunCurve = 0.18f,
        };

        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Sky,
            Sky = new Sky { SkyMaterial = sky },

            // Sky-based ambient keeps shaded faces coloured by the environment rather than going
            // flat grey, which is what sells the flat-shaded look.
            AmbientLightSource = Godot.Environment.AmbientSource.Sky,
            AmbientLightSkyContribution = 1f,
            AmbientLightEnergy = 1.55f,

            TonemapMode = Godot.Environment.ToneMapper.Aces,
            TonemapExposure = 0.92f,
            TonemapWhite = 6f,
        };

        if (Glow)
        {
            // Only the deliberately bright things — muzzle flashes, tracers, the capture zone —
            // sit above this threshold, so the arena itself never blooms.
            env.GlowEnabled = true;
            env.GlowIntensity = 0.55f;
            env.GlowStrength = 1.0f;
            env.GlowBloom = 0.12f;
            env.GlowHdrThreshold = 1.05f;
            env.GlowBlendMode = Godot.Environment.GlowBlendModeEnum.Additive;
        }

        if (AmbientOcclusion)
        {
            env.SsaoEnabled = true;
            env.SsaoRadius = HighQualityAo ? 1.6f : 1.0f;
            env.SsaoIntensity = HighQualityAo ? 2.8f : 2.0f;
            env.SsaoPower = 1.6f;
            env.SsaoDetail = HighQualityAo ? 0.7f : 0.3f;
        }

        // A gentle grade. Flat-shaded boxes lit by a weak sun sit in a narrow band of values, and
        // a little contrast and saturation is what stops the whole arena reading as one grey.
        env.AdjustmentEnabled = true;
        env.AdjustmentContrast = 1.09f;
        env.AdjustmentSaturation = 1.14f;
        env.AdjustmentBrightness = 1.02f;

        // Aerial perspective, re-ranged for the arena as it now is. The old numbers had fog fully
        // saturated by 140 metres, which was most of the way across the previous map and barely a
        // third of the way across this one — so everything beyond the middle distance sat at one
        // uniform haze and the far half of the arena had no depth in it at all.
        //
        // The colour is matched to the sky horizon rather than picked independently, so distance
        // dissolves into the sky instead of into a blue-grey band in front of it.
        env.FogEnabled = fog;
        env.FogMode = Godot.Environment.FogModeEnum.Depth;
        env.FogLightColor = new Color(0.62f, 0.71f, 0.83f);
        env.FogLightEnergy = 1f;
        env.FogDepthBegin = 45f;
        env.FogDepthEnd = 330f;
        env.FogDensity = 0.26f;

        // Deliberately below full: at 1.0 a distant enemy disappears entirely, and on a map this
        // size that would mean long-range fights against targets you cannot see. Depth cueing, not
        // concealment.
        env.FogDepthCurve = 0.7f;

        return new WorldEnvironment { Environment = env };
    }

    /// <summary>Standard surface material for arena geometry.</summary>
    public static StandardMaterial3D Surface(Color c) => new()
    {
        AlbedoColor = c,
        Roughness = 0.82f,
        Metallic = 0.0f,
        SpecularMode = BaseMaterial3D.SpecularModeEnum.SchlickGgx,
    };

    /// <summary>Top of the playable volume, for the purposes of shading by height.</summary>
    const float ShadeCeiling = 14f;

    static ImageTexture? panelTexture;

    /// <summary>Edge of one panel in metres. Sets how big the plating reads across the arena.</summary>
    const float PanelMetres = 2.6f;

    /// <summary>
    /// A tiling plate pattern, generated rather than loaded.
    ///
    /// Every arena surface used to be one flat colour, which is why the blocks needed a glowing bar
    /// stuck along each edge to read as objects at all. A panel pattern does that job in the
    /// material: seams give a face internal structure, so a wall is legibly a wall and its corners
    /// are legible corners, without a single extra triangle.
    ///
    /// Kept greyscale and multiplied by the albedo tint, so one texture serves every block colour
    /// in every arena — one upload, shared by all two hundred-odd blocks.
    /// </summary>
    static ImageTexture PanelTexture()
    {
        if (panelTexture != null) return panelTexture;

        const int N = 256;
        var img = Image.CreateEmpty(N, N, false, Image.Format.Rgb8);

        for (int y = 0; y < N; y++)
        for (int x = 0; x < N; x++)
        {
            // Two panels across the tile, so a seam lands every half tile rather than only at the
            // tile boundary — a single seam per tile reads as a repeat, two reads as plating.
            float u = x / (float)N * 2f, v = y / (float)N * 2f;
            float du = MathF.Min(u % 1f, 1f - u % 1f);
            float dv = MathF.Min(v % 1f, 1f - v % 1f);

            float edge = MathF.Min(du, dv);

            // The grout: a narrow dark line, softened over a couple of texels so it survives
            // mipmapping at distance instead of shimmering away to nothing.
            float seam = Mathf.Clamp(edge / 0.028f, 0f, 1f);
            float shade = Mathf.Lerp(0.62f, 1f, seam);

            // A gentle dome across each panel, so a large flat face is not one dead value.
            shade *= 0.965f + 0.035f * MathF.Min(du, dv) * 4f;

            // And fine grain, deterministic so the arena looks the same every run.
            float n = MathF.Sin(x * 12.9898f + y * 78.233f) * 43758.5453f;
            shade += ((n - MathF.Floor(n)) - 0.5f) * 0.045f;

            float c = Mathf.Clamp(shade, 0f, 1f);
            img.SetPixel(x, y, new Color(c, c, c));
        }

        img.GenerateMipmaps();

        panelTexture = ImageTexture.CreateFromImage(img);
        return panelTexture;
    }

    /// <summary>
    /// The colour a piece of arena geometry is actually painted, given where it is.
    ///
    /// Three things happen here, all of them about reading the map rather than about prettiness.
    ///
    /// **Height.** Surfaces get lighter and a little warmer as they rise. On a map with a second
    /// storey, skybridges, citadel tiers and a crow's nest, the most useful thing shading can tell
    /// you is how high something is — and flat colour told you nothing, so a bridge ten metres up
    /// and the floor under it were the same swatch.
    ///
    /// **District.** Everything outside the old arena edge shifts slightly warm. Two hundred and
    /// seventy-eight metres of the same blue-grey is disorienting; a change of ground colour is how
    /// you know you have left the core without looking at anything in particular.
    ///
    /// **Variation.** A deterministic per-block wobble of a few percent. A sixty-metre causeway
    /// painted one exact value reads as a texture-less plane; the wobble is far too small to notice
    /// as colour and just enough to stop adjacent blocks fusing into one shape.
    /// </summary>
    public static StandardMaterial3D SurfaceAt(Color c, Vector3 centre, float topY, bool outer)
    {
        float h = Mathf.Clamp(topY / ShadeCeiling, 0f, 1f);

        c = c.Lerp(new Color(0.88f, 0.89f, 0.91f), h * 0.40f);
        if (outer) c = c.Lerp(new Color(0.76f, 0.71f, 0.62f), 0.16f);

        float wobble = Jitter(centre) * 0.030f;
        c = new Color(Mathf.Clamp(c.R + wobble, 0f, 1f),
                      Mathf.Clamp(c.G + wobble, 0f, 1f),
                      Mathf.Clamp(c.B + wobble, 0f, 1f));

        var m = Surface(c);

        // Higher surfaces are a touch glossier, so a raised walkway catches a highlight the floor
        // does not. It is another quiet cue for the same thing the tint is saying.
        m.Roughness = Mathf.Lerp(0.86f, 0.62f, h);

        // Plating, projected from world space rather than from UVs.
        //
        // Triplanar because these are boxes of wildly different sizes sharing one BoxMesh: a
        // sixty-metre causeway and a one-metre crate have identical UVs, so any UV-mapped texture
        // would stretch across the first and be invisible on the second. Projecting from world
        // space gives every surface in the arena the same texel size, which is the only way the
        // plating reads as one material rather than as a scale error.
        m.AlbedoTexture = PanelTexture();
        m.Uv1Triplanar = true;
        m.Uv1Scale = Vector3.One / PanelMetres;

        // Anisotropic, not plain mipmapping. A long wall or a causeway floor is seen at a very
        // glancing angle from across the arena, where plain trilinear picks one mip for the whole
        // footprint and the seams break up into speckle. This is the exact case anisotropy exists
        // for, and on one shared texture it costs nothing worth measuring.
        m.TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic;

        return m;
    }

    /// <summary>
    /// A stable pseudo-random value in −1..1 from a position. Deterministic on purpose: the
    /// headless harness and the renderer have to agree on the world, and a wobble seeded from a
    /// random number generator would differ between the two.
    /// </summary>
    static float Jitter(Vector3 p)
    {
        float n = Mathf.Sin(p.X * 12.9898f + p.Y * 78.233f + p.Z * 37.719f) * 43758.5453f;
        return (n - Mathf.Floor(n)) * 2f - 1f;
    }

    /// <summary>
    /// A pawn's material. Players carry a little emission so they stay findable against the arena
    /// — with everything flat-shaded, a coloured box in shadow can otherwise vanish into a wall.
    /// </summary>
    public static StandardMaterial3D Player(Color c)
    {
        var m = Surface(c);
        m.Roughness = 0.55f;
        m.EmissionEnabled = true;
        m.Emission = c;
        m.EmissionEnergyMultiplier = 0.45f;
        return m;
    }

    /// <summary>Deliberately bright: tracers, muzzle flashes and the capture zone.</summary>
    public static StandardMaterial3D Hot(Color c, float energy = 3.2f)
    {
        var m = new StandardMaterial3D
        {
            AlbedoColor = c,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            EmissionEnabled = true,
            Emission = c,
            EmissionEnergyMultiplier = energy,
        };
        return m;
    }
}
