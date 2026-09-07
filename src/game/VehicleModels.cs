using System.Collections.Generic;
using Godot;

namespace HitboxClone;

/// <summary>
/// Loads the vehicle models, in two pieces.
///
/// Same runtime <see cref="GltfDocument"/> load as the characters, for the same reason: Godot's
/// import pipeline only runs when the editor opens the project, so an imported model means a new
/// export does nothing until you launch the editor once. Drop a file in, next launch uses it.
///
/// The split into hull and turret is the whole point. A tank whose gun cannot traverse independently
/// of its body is a tank that has to point its whole self at you, and the turret is already a
/// separate node in the simulation — <see cref="Vehicle.TurretYaw"/> has driven a box on a pivot
/// since the cannon was written. The two exports drop straight onto that existing pivot.
/// </summary>
public static class VehicleModels
{
    public const string Folder = "res://assets/vehicles";

    /// <summary>Set at startup. A headless run has no rendering server to build a mesh on.</summary>
    public static bool Enabled = true;

    /// <summary>Same reasoning as the characters: 2048 is four times the memory for nothing.</summary>
    public const int MaxTextureSize = 1024;

    sealed class Loaded
    {
        public PackedScene? Scene;
        public Aabb Bounds;
    }

    static readonly Dictionary<string, Loaded> cache = new();

    /// <summary>
    /// A fresh instance of a vehicle part, scaled so its longest axis matches <paramref name="want"/>.
    ///
    /// Scaled from measured bounds rather than by a hardcoded factor. The exports are not normalised
    /// to any scale, and the hull has to end up the same size as the collision box the simulation
    /// has always used — a model that merely looks like a tank while the thing you collide with is
    /// a different size is worse than a box, because now the box lies.
    /// </summary>
    public static Node3D? Instance(string part, float scale)
    {
        if (!Enabled) return null;

        var loaded = Get(part);
        if (loaded?.Scene == null) return null;

        var node = loaded.Scene.Instantiate<Node3D>();
        node.Scale = Vector3.One * scale;

        // Centred on its own bounds, so a part whose origin sits at one corner of the export still
        // pivots where the simulation expects it to.
        node.Position = -loaded.Bounds.GetCenter() * scale;

        return node;
    }

    /// <summary>
    /// The scale that makes a part's longest axis <paramref name="want"/> metres.
    ///
    /// Longest axis rather than a named one, because the exports do not agree on which axis they
    /// lie along: the hull measures 0.64 x 0.30 x 1.00 and the turret 1.00 x 0.35 x 0.58, so one
    /// runs down Z and the other down X. Anything that assumed a shared axis would size one of
    /// them by its width.
    /// </summary>
    public static float ScaleFor(string part, float want)
    {
        var loaded = Get(part);
        if (loaded?.Scene == null) return 1f;

        var s = loaded.Bounds.Size;
        float longest = Mathf.Max(s.X, Mathf.Max(s.Y, s.Z));

        return longest > 0.01f ? want / longest : 1f;
    }

    /// <summary>
    /// The scale that makes a part <paramref name="want"/> metres tall.
    ///
    /// The right measure for a part that has to sit on top of another one. The two tank exports are
    /// not in a consistent scale with each other — matching their longest axes put an eight metre
    /// turret on an eight metre hull — and height is the dimension that decides whether a turret
    /// reads as mounted or as hovering.
    /// </summary>
    public static float ScaleForHeight(string part, float want)
    {
        var loaded = Get(part);
        if (loaded?.Scene == null) return 1f;

        float h = loaded.Bounds.Size.Y;
        return h > 0.01f ? want / h : 1f;
    }

    /// <summary>The measured size of a part in its own units, or zero when it is missing.</summary>
    public static Vector3 SizeOf(string part) => Get(part)?.Bounds.Size ?? Vector3.Zero;

    public static bool Has(string part) => Get(part)?.Scene != null;

    static Loaded? Get(string part)
    {
        if (cache.TryGetValue(part, out var hit)) return hit;

        var built = Build(part);
        cache[part] = built ?? new Loaded();
        return cache[part];
    }

    static Loaded? Build(string part)
    {
        string path = $"{Folder}/{part}.glb";
        if (!Godot.FileAccess.FileExists(path)) return null;

        var doc = new GltfDocument();
        var state = new GltfState();

        if (doc.AppendFromFile(path, state) != Error.Ok) return null;
        if (doc.GenerateScene(state) is not Node3D scene) return null;

        ShrinkTextures(scene);

        var bounds = Measure(scene);

        var packed = new PackedScene();
        packed.Pack(scene);

        return new Loaded { Scene = packed, Bounds = bounds };
    }

    /// <summary>Union of every mesh's bounds, in the scene's own space.</summary>
    static Aabb Measure(Node3D scene)
    {
        bool any = false;
        Aabb total = default;

        foreach (var mi in AllMeshes(scene))
        {
            if (mi.Mesh == null) continue;

            Aabb box = mi.GetTransform() * mi.Mesh.GetAabb();
            total = any ? total.Merge(box) : box;
            any = true;
        }

        return any ? total : new Aabb(Vector3.Zero, Vector3.One);
    }

    static void ShrinkTextures(Node3D scene)
    {
        foreach (var mi in AllMeshes(scene))
        {
            if (mi.Mesh is not { } mesh) continue;

            for (int s = 0; s < mesh.GetSurfaceCount(); s++)
            {
                if (mesh.SurfaceGetMaterial(s) is not StandardMaterial3D mat) continue;
                if (mat.AlbedoTexture is not { } tex) continue;

                var img = tex.GetImage();
                if (img == null) continue;

                int w = img.GetWidth(), h = img.GetHeight();
                if (w <= MaxTextureSize && h <= MaxTextureSize) continue;

                float k = MaxTextureSize / (float)Mathf.Max(w, h);
                img.Resize(Mathf.RoundToInt(w * k), Mathf.RoundToInt(h * k), Image.Interpolation.Lanczos);

                mat.AlbedoTexture = ImageTexture.CreateFromImage(img);
            }
        }
    }

    public static IEnumerable<MeshInstance3D> AllMeshes(Node n)
    {
        if (n is MeshInstance3D m) yield return m;

        foreach (var child in n.GetChildren())
            foreach (var found in AllMeshes(child)) yield return found;
    }

    /// <summary>
    /// Drop every cached model before the engine tears down. A PackedScene holds GPU resources
    /// with their own lifetime, and left in a static dictionary they outlive the rendering server.
    /// </summary>
    public static void Shutdown()
    {
        foreach (var loaded in cache.Values) loaded.Scene?.Dispose();

        cache.Clear();
        Enabled = false;
    }

    /// <summary>Diagnostics for the harness: what loaded, how big, how many triangles.</summary>
    public static string Describe(string part)
    {
        var loaded = Get(part);
        if (loaded?.Scene == null) return $"{part}: missing";

        var inst = loaded.Scene.Instantiate<Node3D>();

        int tris = 0, meshes = 0;
        foreach (var mi in AllMeshes(inst))
        {
            if (mi.Mesh == null) continue;
            meshes++;

            // Counted from the index array where there is one, and from the vertex array where the
            // surface is unindexed. Meshy exports are indexed, but a fallback costs nothing.
            for (int s = 0; s < mi.Mesh.GetSurfaceCount(); s++)
            {
                var arrays = mi.Mesh.SurfaceGetArrays(s);
                if (arrays.Count <= (int)Mesh.ArrayType.Index) continue;

                var indices = arrays[(int)Mesh.ArrayType.Index].AsInt32Array();
                if (indices.Length > 0) { tris += indices.Length / 3; continue; }

                tris += arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array().Length / 3;
            }
        }

        var size = loaded.Bounds.Size;
        inst.QueueFree();

        return $"{part}: {meshes} mesh(es), {tris} tris, "
             + $"{size.X:0.00} x {size.Y:0.00} x {size.Z:0.00} units";
    }
}
