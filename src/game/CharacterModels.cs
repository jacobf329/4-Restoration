using System.Collections.Generic;
using Godot;

namespace HitboxClone;

/// <summary>
/// Loads the faction character models.
///
/// Loaded at runtime through <see cref="GltfDocument"/> rather than imported as scenes, for the
/// same reason the menu backdrops are: Godot's import pipeline only runs when the editor opens the
/// project, so an imported model means a new export does nothing until you launch the editor once.
/// This way a file is dropped in and the next launch uses it.
///
/// Every model is loaded once and instanced per pawn. Four factions share one mesh and one texture
/// each however many pawns are wearing them, which is what keeps four splitscreen viewports
/// affordable.
/// </summary>
public static class CharacterModels
{
    public const string Folder = "res://assets/characters";

    /// <summary>Set at startup. A headless run has no rendering server to build a mesh on.</summary>
    public static bool Enabled = true;

    /// <summary>
    /// Textures come out of Meshy at 2048 square. At splitscreen distances a character occupies a
    /// couple of hundred pixels of a quarter-screen viewport, where 1024 is already generous and
    /// 2048 is four times the memory for nothing. Downscaled on load rather than in the file, so
    /// the originals stay untouched and the number is one edit away.
    /// </summary>
    public const int MaxTextureSize = 1024;

    sealed class Loaded
    {
        public PackedScene? Scene;
        public float Height = 1f;
    }

    static readonly Dictionary<string, Loaded> cache = new();

    /// <summary>
    /// A fresh instance of a faction's character, or null if the model is missing.
    ///
    /// The caller owns the returned node and is expected to parent it. Height is reported back so
    /// the pawn can scale it: the four models are not the same size in their own units, and every
    /// pawn has to end up the same height as its collision capsule.
    /// </summary>
    public static Node3D? Instance(FactionDef faction, out float sourceHeight)
        => Instance(faction.Model, out sourceHeight);

    /// <summary>
    /// The same, by model name rather than by faction.
    ///
    /// A reinforcement has a body of its own — a Sinew is not a Vessel in a different colour — so
    /// the model a pawn wears stopped being a property of its faction alone the moment the spawn
    /// screen could hand you somebody else's character.
    /// </summary>
    public static Node3D? Instance(string model, out float sourceHeight)
    {
        sourceHeight = 1f;
        if (!Enabled || model.Length == 0) return null;

        var loaded = Get(model);
        if (loaded?.Scene == null) return null;

        sourceHeight = loaded.Height;
        return loaded.Scene.Instantiate<Node3D>();
    }

    static Loaded? Get(string model)
    {
        if (cache.TryGetValue(model, out var hit)) return hit;

        var built = Build(model);
        cache[model] = built ?? new Loaded();
        return cache[model];
    }

    static Loaded? Build(string model)
    {
        string walk = $"{Folder}/{model}_walk.glb";
        if (!Godot.FileAccess.FileExists(walk)) return null;

        Node3D? scene = LoadGlb(walk);
        if (scene == null) return null;

        // The run cycle lives in its own export, carrying a duplicate of the whole mesh and
        // texture. Only its animation is wanted, so the rest of that file is loaded, robbed and
        // thrown away rather than kept in memory alongside the walk.
        string run = $"{Folder}/{model}_run.glb";
        if (Godot.FileAccess.FileExists(run)) MergeAnimations(scene, run);

        ShrinkTextures(scene);

        float height = MeasureHeight(scene);

        var packed = new PackedScene();
        packed.Pack(scene);

        return new Loaded { Scene = packed, Height = height };
    }

    static Node3D? LoadGlb(string path)
    {
        var doc = new GltfDocument();
        var state = new GltfState();

        if (doc.AppendFromFile(path, state) != Error.Ok) return null;
        return doc.GenerateScene(state) as Node3D;
    }

    /// <summary>Copy every animation out of another export into this one's player.</summary>
    static void MergeAnimations(Node3D into, string otherPath)
    {
        if (FindPlayer(into) is not { } player) return;
        if (LoadGlb(otherPath) is not { } other) return;

        if (FindPlayer(other) is { } fromPlayer)
        {
            foreach (var libName in fromPlayer.GetAnimationLibraryList())
            {
                var lib = fromPlayer.GetAnimationLibrary(libName);

                foreach (var animName in lib.GetAnimationList())
                {
                    string key = animName;
                    if (player.HasAnimation(key)) key = $"run_{animName}";

                    player.GetAnimationLibrary(player.GetAnimationLibraryList()[0])
                          .AddAnimation(key, lib.GetAnimation(animName));
                }
            }
        }

        other.QueueFree();
    }

    public static AnimationPlayer? FindPlayer(Node n)
    {
        if (n is AnimationPlayer p) return p;

        foreach (var child in n.GetChildren())
            if (FindPlayer(child) is { } found) return found;

        return null;
    }

    public static MeshInstance3D? FindMesh(Node n)
    {
        if (n is MeshInstance3D m) return m;

        foreach (var child in n.GetChildren())
            if (FindMesh(child) is { } found) return found;

        return null;
    }

    /// <summary>Halve 2048-square source textures down to the budget.</summary>
    static void ShrinkTextures(Node3D scene)
    {
        if (FindMesh(scene) is not { Mesh: { } mesh }) return;

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

    /// <summary>
    /// How tall the model is in its own units, taken from the mesh bounds rather than assumed.
    ///
    /// Meshy exports are not normalised to any particular scale and the four are not the same size
    /// as each other, so scaling by a hardcoded factor would leave one faction knee-high and
    /// another clipping the ceiling — while all four still shared one collision capsule.
    /// </summary>
    static float MeasureHeight(Node3D scene)
    {
        if (FindMesh(scene) is not { Mesh: { } mesh } mi) return 1f;

        Aabb box = mesh.GetAabb();
        float h = box.Size.Y * mi.Scale.Y;

        return h > 0.01f ? h : 1f;
    }

    /// <summary>
    /// Drop every cached model before the engine tears down.
    ///
    /// A PackedScene holds meshes, materials and textures, which are GPU resources with their own
    /// lifetime. Left in a static dictionary they outlive the rendering server and Godot reports
    /// them as leaked RIDs at exit — the same shape of problem the audio player had.
    /// </summary>
    public static void Shutdown()
    {
        // Disposed, not merely dropped. Clearing the dictionary only removes the managed reference,
        // and Godot tears the rendering server down before the garbage collector would ever get to
        // it — so the meshes and textures were still reported as leaked.
        foreach (var loaded in cache.Values) loaded.Scene?.Dispose();

        cache.Clear();
        Enabled = false;
    }

    /// <summary>Diagnostics for the harness: what loaded, how big, how many triangles.</summary>
    public static string Describe(FactionDef f)
    {
        var loaded = Get(f.Model);
        if (loaded?.Scene == null) return $"{f.Name}: missing";

        var inst = loaded.Scene.Instantiate<Node3D>();
        var mesh = FindMesh(inst);
        var player = FindPlayer(inst);

        int tris = 0;
        int tex = 0;

        if (mesh?.Mesh is { } m)
        {
            for (int s = 0; s < m.GetSurfaceCount(); s++)
            {
                var arrays = m.SurfaceGetArrays(s);
                if (arrays.Count > (int)Mesh.ArrayType.Index
                    && arrays[(int)Mesh.ArrayType.Index].Obj is int[] idx)
                    tris += idx.Length / 3;

                if (m.SurfaceGetMaterial(s) is StandardMaterial3D sm && sm.AlbedoTexture is { } t)
                    tex = Mathf.Max(tex, t.GetWidth());
            }
        }

        int anims = 0;
        if (player != null)
            foreach (var lib in player.GetAnimationLibraryList())
                anims += player.GetAnimationLibrary(lib).GetAnimationList().Count;

        inst.QueueFree();

        return $"{f.Name}: {tris} tris, {tex}px texture, {anims} animations, "
               + $"{loaded.Height:0.00} units tall";
    }
}
