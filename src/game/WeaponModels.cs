using System.Collections.Generic;
using Godot;

namespace HitboxClone;

/// <summary>
/// Loads the held weapon models.
///
/// Same runtime <see cref="GltfDocument"/> route as <see cref="CharacterModels"/>, and for the same
/// reason: Godot's import pipeline only runs when the editor opens the project, so an imported
/// asset means a new export does nothing until somebody launches the editor once. Dropping a file
/// in and relaunching is the whole workflow.
///
/// Every weapon falls back to the box silhouette it has always had. That is not a courtesy — it is
/// the reason this could be built before a single model existed. Twenty-three weapons will arrive
/// one at a time over weeks, and each one has to slot in without a code change and without the
/// other twenty-two looking broken in the meantime.
/// </summary>
public static class WeaponModels
{
    public const string Folder = "res://assets/weapons";

    /// <summary>Set at startup. A headless run has no rendering server to build a mesh on.</summary>
    public static bool Enabled = true;

    /// <summary>
    /// A held gun occupies a few hundred pixels at the bottom of a splitscreen quarter. Meshy hands
    /// back 2K, which is four times what that can show.
    /// </summary>
    public const int MaxTextureSize = 512;

    sealed class Loaded
    {
        public PackedScene? Scene;

        /// <summary>Longest dimension in the file's own units, for scaling to the held length.</summary>
        public float Length = 1f;

        /// <summary>Which local axis the barrel runs along, as a unit vector.</summary>
        public Vector3 Along = Vector3.Forward;
    }

    static readonly Dictionary<string, Loaded> cache = new();

    /// <summary>
    /// A fresh instance of a weapon's model, or null if there is no file for it.
    ///
    /// The caller owns the returned node. Length and axis come back so the view model can scale it
    /// to the silhouette it is replacing and turn it to point forwards — text-to-3D has no idea how
    /// big a rifle is, and no consistent opinion about which way it faces.
    /// </summary>
    public static Node3D? Instance(WeaponDef weapon, out float sourceLength, out Vector3 along,
                                   out float facing)
    {
        sourceLength = 1f;
        along = Vector3.Forward;
        facing = 1f;

        if (!Enabled || weapon.Model.Length == 0) return null;

        var loaded = Get(weapon.Model);
        if (loaded?.Scene == null) return null;

        sourceLength = loaded.Length;
        along = loaded.Along;
        facing = MuzzleDirection(weapon);
        return loaded.Scene.Instantiate<Node3D>();
    }

    static Loaded? Get(string model)
    {
        if (cache.TryGetValue(model, out var hit)) return hit;

        var built = Build(model);

        // A miss is cached too. Without this every pawn spawning with a modelless weapon retries
        // the file system, which on a twelve-fighter roster is a lot of failed lookups a second.
        cache[model] = built ?? new Loaded();
        return cache[model];
    }

    static Loaded? Build(string model)
    {
        string path = $"{Folder}/{model}.glb";
        if (!Godot.FileAccess.FileExists(path)) return null;

        var doc = new GltfDocument();
        var state = new GltfState();

        if (doc.AppendFromFile(path, state) != Error.Ok) return null;
        if (doc.GenerateScene(state) is not Node3D scene) return null;

        ShrinkTextures(scene);

        var box = Bounds(scene);
        var size = box.Size;

        // The longest axis is the barrel. True of every gun and every blade in this game, and a
        // safer assumption than trusting whichever way the generator happened to face it.
        Vector3 along = size.X >= size.Y && size.X >= size.Z ? Vector3.Right
                      : size.Y >= size.Z ? Vector3.Up
                      : Vector3.Back;

        float length = Mathf.Max(size.X, Mathf.Max(size.Y, size.Z));

        var packed = new PackedScene();
        packed.Pack(scene);

        return new Loaded
        {
            Scene = packed,
            Length = Mathf.Max(length, 0.001f),
            Along = along,
        };
    }


    /// <summary>
    /// Which end of the long axis the muzzle is at, as +1 or -1.
    ///
    /// A convention plus an override list, which is not what this used to be. It measured the
    /// geometry: a gun is thin at the muzzle and fat at the breech, so compare how far the mesh
    /// spreads from the long axis at each end and call the slender end the front. The reasoning is
    /// sound and the measurement does not work.
    ///
    /// Run over all twenty-four models it splits 13/11, with margins inside the noise — the
    /// railgun 0.099 against 0.092, the shotgun 0.090 against 0.092. Three other discriminators
    /// were tried on the same files (90th-percentile radius, maximum radius, vertex count at each
    /// end) and they agree with the batch 50%, 54%, 58% and 54% of the time. Every prompt in
    /// meshy-weapons.json asks for the barrel pointing the same way, so a test that worked would
    /// come out near-unanimous. None of them does. It is a coin flip wearing a justification.
    ///
    /// The old comment defended this as "right far more often than a coin, needs no maintenance",
    /// against "twenty-three hand-checked flip flags that go stale". The first half is measurably
    /// untrue, and that changes the trade: a stale flag is one visible thing to fix, and a coin
    /// flip is a gun that faces a different way each time somebody regenerates it, which cannot be
    /// fixed at all. So the batch shares one convention and a wrong model is one bool.
    /// </summary>
    static float MuzzleDirection(WeaponDef weapon)
        => weapon.MuzzleFlip ? -MuzzleConvention : MuzzleConvention;

    /// <summary>
    /// Which end of its own long axis a generated gun puts the muzzle at.
    ///
    /// One number for the whole batch, because one prompt shape generated the whole batch.
    ///
    /// MEASURED, not guessed. This was guessed once and that was not good enough, so all
    /// twenty-four models were parsed straight out of their .glb buffers and their vertices
    /// rasterised to orthographic side-on silhouettes, which a gun is extremely legible in — you
    /// can see the pistol grip and the stock. Every one of them puts the muzzle at NEGATIVE X:
    /// the assault rifle hangs its grip and magazine right of centre with the handguard reaching
    /// left, the portal gun the same, the minigun's barrel bundle is the left half. Two geometric
    /// heuristics tried first (grip mass low and aft, barrel taper) disagreed with each other on
    /// the assault rifle, which is why looking at them won over measuring proxies for them.
    ///
    /// Worth knowing what this rules out: with one shared constant a *partial* failure is not
    /// possible. If some guns point the right way and others do not, the build is old — it is
    /// still running the per-model bounds heuristic this replaced, which scored 13/11 with margins
    /// inside the noise floor and so got roughly half of them wrong. The title screen's build
    /// stamp settles which is which.
    ///
    /// The sign is fixed by Pawn's rotation, and the two must be read together. Godot's view model
    /// looks down -Z; a +90 degree turn about Y sends model +X to -Z, so a muzzle at -X needs the
    /// extra half turn, so facing must be negative, so this is -1.
    /// </summary>
    const float MuzzleConvention = -1f;

    /// <summary>Union of every mesh's bounds, in the scene's own space.</summary>
    static Aabb Bounds(Node3D scene)
    {
        var box = new Aabb();
        bool first = true;

        foreach (var mesh in AllMeshes(scene))
        {
            var m = mesh.GetAabb();
            box = first ? m : box.Merge(m);
            first = false;
        }

        return box;
    }

    public static IEnumerable<MeshInstance3D> AllMeshes(Node node)
    {
        if (node is MeshInstance3D m) yield return m;
        foreach (var child in node.GetChildren())
            foreach (var found in AllMeshes(child))
                yield return found;
    }

    /// <summary>Downscale the base colour maps. Same trick the character loader uses.</summary>
    static void ShrinkTextures(Node3D scene)
    {
        foreach (var mesh in AllMeshes(scene))
        {
            for (int i = 0; i < mesh.GetSurfaceOverrideMaterialCount(); i++)
            {
                if (mesh.Mesh?.SurfaceGetMaterial(i) is not StandardMaterial3D mat) continue;
                if (mat.AlbedoTexture is not { } tex) continue;

                var img = tex.GetImage();
                if (img == null) continue;

                int longest = Mathf.Max(img.GetWidth(), img.GetHeight());
                if (longest <= MaxTextureSize) continue;

                float k = (float)MaxTextureSize / longest;
                img.Resize(Mathf.Max(1, (int)(img.GetWidth() * k)),
                           Mathf.Max(1, (int)(img.GetHeight() * k)));

                mat.AlbedoTexture = ImageTexture.CreateFromImage(img);
            }
        }
    }

    /// <summary>Drop every cached scene. Called at shutdown, before the engine tears down.</summary>
    public static void Shutdown() => cache.Clear();
}
