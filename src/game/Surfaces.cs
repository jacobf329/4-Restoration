using System.Collections.Generic;
using Godot;

namespace HitboxClone;

/// <summary>
/// What a block is made of.
///
/// A role rather than a look. A block asks for "plaster" and the surface library decides what
/// plaster is today — a procedural panel now, a photographed material the moment somebody drops
/// three files in a folder. Nothing in the arena builders has to change when that happens, which
/// is the entire point of naming the material instead of the texture.
/// </summary>
public enum SurfaceKind
{
    /// <summary>The original plating. Every arena is built from this and still is.</summary>
    Panel,

    /// <summary>Rendered house walls. Fairview is mostly this.</summary>
    Plaster,

    Brick,
    RoofTile,

    /// <summary>Civic and industrial: the school, the hall, retaining walls.</summary>
    Concrete,

    /// <summary>Roads and yards.</summary>
    Tarmac,

    /// <summary>Hedges, verges, the green.</summary>
    Foliage,

    Timber,
}

/// <summary>
/// The environment's material library.
///
/// Loaded at runtime from image files rather than through Godot's import pipeline, for exactly the
/// reason <see cref="WeaponModels"/> loads meshes that way: the importer only runs when the editor
/// opens the project, so an imported asset means a new texture does nothing until somebody launches
/// the editor once. Dropping three files in and relaunching is the whole workflow.
///
/// Every kind falls back to the procedural panel. That is not a courtesy — it is what lets the
/// arena builders be written against a vocabulary of materials that do not exist yet, and what
/// lets eight materials arrive one at a time without the other seven looking broken in between.
/// It is the same bargain the weapon models were built on and it worked there.
/// </summary>
public static class Surfaces
{
    public const string Folder = "res://assets/surfaces";

    /// <summary>Set at startup. A headless run has no rendering server to upload a texture to.</summary>
    public static bool Enabled = true;

    /// <summary>
    /// What one kind is called on disk, and how many metres of world one tile covers.
    ///
    /// Texel density is per-material and has to be, because the thing being represented has a real
    /// size. Brick courses are about a quarter of a metre; a tarmac tile can be four metres before
    /// anybody notices it repeating. One shared number would make one of them wrong.
    /// </summary>
    public readonly struct Spec
    {
        public readonly string Name;
        public readonly float Metres;

        public Spec(string name, float metres) { Name = name; Metres = metres; }
    }

    public static Spec SpecFor(SurfaceKind kind) => kind switch
    {
        SurfaceKind.Plaster => new Spec("plaster", 2.4f),
        SurfaceKind.Brick => new Spec("brick", 1.2f),
        SurfaceKind.RoofTile => new Spec("roof_tile", 1.6f),
        SurfaceKind.Concrete => new Spec("concrete", 3.0f),
        SurfaceKind.Tarmac => new Spec("tarmac", 4.0f),
        SurfaceKind.Foliage => new Spec("foliage", 1.8f),
        SurfaceKind.Timber => new Spec("timber", 1.4f),
        _ => new Spec("", Graphics.PanelMetres),
    };

    /// <summary>One material's three maps, or nulls where a file was not there.</summary>
    public sealed class Set
    {
        public Texture2D? Albedo;
        public Texture2D? Normal;

        /// <summary>Packed the way every exporter in this project's pipeline writes it: G is roughness.</summary>
        public Texture2D? Roughness;

        public bool Any => Albedo != null || Normal != null || Roughness != null;
    }

    static readonly Dictionary<SurfaceKind, Set> cache = new();

    /// <summary>
    /// The maps for a kind, loading them the first time and remembering the answer — including
    /// when the answer is "there are none", so a missing material is not re-probed once per block.
    /// </summary>
    public static Set For(SurfaceKind kind)
    {
        if (cache.TryGetValue(kind, out var hit)) return hit;

        var set = new Set();
        cache[kind] = set;

        if (!Enabled) return set;

        string name = SpecFor(kind).Name;
        if (name.Length == 0) return set;

        set.Albedo = Load($"{name}_base_color");
        set.Normal = Load($"{name}_normal");
        set.Roughness = Load($"{name}_metallic_roughness");

        if (set.Any) GD.Print($"surfaces: {name} loaded");
        return set;
    }

    /// <summary>
    /// One image off disk, or null.
    ///
    /// Both extensions are tried because the pipeline that produced every other texture in this
    /// project writes .jpg, and anything hand-made is likely to be .png. Asking for both costs one
    /// failed file check per material per run.
    /// </summary>
    static Texture2D? Load(string stem)
    {
        foreach (string ext in new[] { "jpg", "png" })
        {
            string path = $"{Folder}/{stem}.{ext}";
            if (!Godot.FileAccess.FileExists(path)) continue;

            var img = new Image();
            if (img.Load(path) != Error.Ok) continue;

            // Mipmaps are not optional here. These are world-projected onto surfaces seen at very
            // glancing angles from across a map, which is the exact case that turns into speckle
            // without them.
            img.GenerateMipmaps();
            return ImageTexture.CreateFromImage(img);
        }

        return null;
    }

    /// <summary>Forget everything loaded, so the harness can prove the fallback path works.</summary>
    public static void ClearCacheForTest() => cache.Clear();
}
