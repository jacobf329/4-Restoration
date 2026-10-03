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

    /// <summary>
    /// One prop, held as the mesh resources themselves rather than as a scene to copy.
    ///
    /// This is the whole performance story of the set dressing, so it is worth being plain about.
    /// The first version cached a PackedScene and called Instantiate for each prop in the map,
    /// which is the obvious thing to do and quietly gives every copy its own mesh, its own
    /// material and its own texture: a .glb loaded at runtime has no resource path, so there is
    /// nothing for the engine to share them BY, and it duplicates them instead. Measured with
    /// --perf: Coldstore was asking the renderer for 814 MB of texture memory for a library of
    /// twenty-eight props whose textures come to about forty. On a card with less than that free
    /// it is not slow, it is a freeze.
    ///
    /// Holding the meshes directly and pointing every instance at the same ones is what makes the
    /// cost of a prop the cost of ONE prop, however many of them are standing on the map.
    /// </summary>
    sealed class Loaded
    {
        public readonly List<(Mesh Mesh, Transform3D At)> Parts = new();
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
        if (loaded == null || loaded.Parts.Count == 0) return null;

        var size = loaded.Bounds.Size;
        if (size.X <= 0.0001f || size.Y <= 0.0001f || size.Z <= 0.0001f) return null;

        // Whichever axis runs out of room first. Fitting to the average, or to the longest axis
        // alone, lets the other two poke out of the collider.
        float scale = Mathf.Min(halfExtents.X * 2f / size.X,
                      Mathf.Min(halfExtents.Y * 2f / size.Y,
                                halfExtents.Z * 2f / size.Z));

        var node = new Node3D { RotationDegrees = new Vector3(0f, yawDegrees, 0f) };

        // Centred across, and sat on the floor of its box rather than centred in it.
        //
        // The distinction only shows when a prop is fitted on X or Z - then it is shorter than its
        // box is tall, and centring leaves it hovering with a gap under it. Putting its underside
        // on the box floor is what makes a boulder rest on the ground instead of above it.
        var centre = loaded.Bounds.GetCenter() * scale;
        float bottom = centre.Y - size.Y * scale * 0.5f;

        var model = new Node3D
        {
            Scale = Vector3.One * scale,
            Position = new Vector3(-centre.X, -halfExtents.Y - bottom, -centre.Z),
        };

        // The same Mesh object every time. Not a copy of it - see Loaded.
        foreach (var (mesh, at) in loaded.Parts)
            model.AddChild(new MeshInstance3D { Mesh = mesh, Transform = at });

        node.AddChild(model);
        return node;
    }

    public static bool Has(string name) => name.Length > 0 && Get(name)?.Parts.Count > 0;

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

        TextureBudget.Fit(scene, MaxTextureSize);

        var loaded = new Loaded();
        bool any = false;

        foreach (var mi in VehicleModels.AllMeshes(scene))
        {
            if (mi.Mesh is not { } mesh) continue;

            // The transform all the way up to the scene root, not just this node's own. A .glb
            // puts its meshes under whatever node hierarchy the exporter felt like, and a part
            // that sits two nodes down is placed by all three.
            var at = Relative(scene, mi);
            loaded.Parts.Add((mesh, at));

            Aabb box = at * mesh.GetAabb();
            loaded.Bounds = any ? loaded.Bounds.Merge(box) : box;
            any = true;
        }

        // The loaded scene was only ever scaffolding: the meshes it carried are held above and
        // keep themselves alive, and nothing is going to instantiate this node tree again.
        scene.Free();

        if (!any) return null;
        return loaded;
    }

    /// <summary>A node's transform relative to the model root, parents included.</summary>
    static Transform3D Relative(Node root, Node3D node)
    {
        var at = Transform3D.Identity;

        for (Node3D? n = node; n != null && n != root; n = n.GetParent() as Node3D)
            at = n.Transform * at;

        return at;
    }

    /// <summary>
    /// Drop every cached model before the engine tears down, for the reason the other two loaders
    /// do: these hold GPU resources that outlive the rendering server if left in a static.
    /// </summary>
    public static void Shutdown()
    {
        foreach (var loaded in cache.Values) loaded.Parts.Clear();
        cache.Clear();
    }
}
