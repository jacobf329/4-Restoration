using System.Collections.Generic;
using Godot;

namespace HitboxClone;

/// <summary>
/// The set dressing: rocks, drifts, wreckage, the clutter a base accumulates.
///
/// Same runtime <see cref="GltfDocument"/> load as the vehicles and the characters, for the same
/// reason - Godot's import pipeline only runs when the editor opens the project, so an imported
/// model does nothing until somebody launches the editor once. Drop a .glb in assets/props and the
/// next launch uses it.
///
/// What is different here is the fitting. A vehicle part is scaled so its longest axis matches the
/// hull it stands in for; a prop is scaled so it fits *inside* a box, on whichever axis binds
/// first. That is not a detail: a prop's box is what the player collides with, what the navigation
/// graph reads, and what every spawn and reachability check in the harness measures. A mesh that
/// overhangs its box is cover you can shoot through and stand inside, which is worse than a box,
/// because now the box lies.
/// </summary>
public static class PropModels
{
    public const string Folder = "res://assets/props";

    /// <summary>Set at startup. A headless run has no rendering server to build a mesh on.</summary>
    public static bool Enabled = true;

    /// <summary>
    /// Smaller than the vehicles get, because there are going to be a great many more of these.
    ///
    /// Scenery is the one category where the count grows without limit - a map wants dozens of
    /// boulders and exactly one tank - and every one of them is drawn once per splitscreen
    /// viewport. 512 is plenty for something read at twenty metres.
    /// </summary>
    public const int MaxTextureSize = 512;

    sealed class Loaded
    {
        public PackedScene? Scene;
        public Aabb Bounds;
    }

    static readonly Dictionary<string, Loaded> cache = new();

    /// <summary>
    /// A fresh instance of a prop, scaled to sit inside <paramref name="halfExtents"/> and centred
    /// in it, with an optional yaw.
    ///
    /// The yaw turns the mesh and not the box, which is a deliberate and limited licence. On
    /// something organic - a boulder, a drift, a crag - the box was always an approximation of an
    /// irregular shape and turning the mesh inside it costs nothing anybody can see. On something
    /// with flat sides it would be a visible lie, so the arena only spins the organic ones.
    /// </summary>
    public static Node3D? Instance(string name, Vector3 halfExtents, float yawDegrees = 0f)
    {
        if (!Enabled || name.Length == 0) return null;

        var loaded = Get(name);
        if (loaded?.Scene == null) return null;

        var size = loaded.Bounds.Size;
        if (size.X <= 0.0001f || size.Y <= 0.0001f || size.Z <= 0.0001f) return null;

        // Whichever axis runs out of room first. Fitting to the average, or to the longest axis
        // alone, lets the other two poke out of the collider.
        float scale = Mathf.Min(halfExtents.X * 2f / size.X,
                      Mathf.Min(halfExtents.Y * 2f / size.Y,
                                halfExtents.Z * 2f / size.Z));

        var node = new Node3D { RotationDegrees = new Vector3(0f, yawDegrees, 0f) };

        var model = loaded.Scene.Instantiate<Node3D>();
        model.Scale = Vector3.One * scale;

        // Centred across, and sat on the floor of its box rather than centred in it.
        //
        // The distinction only shows when a prop is fitted on X or Z - then it is shorter than its
        // box is tall, and centring leaves it hovering with a gap under it. Putting its underside
        // on the box floor is what makes a boulder rest on the ground instead of above it.
        var centre = loaded.Bounds.GetCenter() * scale;
        float bottom = centre.Y - size.Y * scale * 0.5f;

        model.Position = new Vector3(-centre.X, -halfExtents.Y - bottom, -centre.Z);

        node.AddChild(model);
        return node;
    }

    public static bool Has(string name) => name.Length > 0 && Get(name)?.Scene != null;

    static Loaded? Get(string name)
    {
        if (cache.TryGetValue(name, out var hit)) return hit;

        var built = Build(name);
        cache[name] = built ?? new Loaded();
        return cache[name];
    }

    static Loaded? Build(string name)
    {
        string path = $"{Folder}/{name}.glb";
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

    static Aabb Measure(Node3D scene)
    {
        bool any = false;
        Aabb total = default;

        foreach (var mi in VehicleModels.AllMeshes(scene))
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
        foreach (var mi in VehicleModels.AllMeshes(scene))
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

    /// <summary>
    /// Drop every cached model before the engine tears down, for the reason the other two loaders
    /// do: a PackedScene holds GPU resources that outlive the rendering server if left in a static.
    /// </summary>
    public static void Shutdown()
    {
        foreach (var loaded in cache.Values) loaded.Scene = null;
        cache.Clear();
    }
}
