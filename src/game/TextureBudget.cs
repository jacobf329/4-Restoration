using System.Collections.Generic;
using Godot;

namespace HitboxClone;

/// <summary>
/// Hold every texture on a loaded model down to a size budget.
///
/// This exists because of a bug that cost a gigabyte of video memory and froze a machine, and the
/// shape of it is worth keeping written down: all four model loaders already had a MaxTextureSize
/// and a ShrinkTextures, all four were measured and tuned, and every one of them resized the
/// ALBEDO and nothing else.
///
/// A Meshy export carries three images — base colour, normal, and metallic/roughness — and they
/// are all the same size. So a weapon with a 512 budget kept one 512 map and two 2048 ones, which
/// is 44MB rather than the 4MB the budget was written to buy, and twenty-four weapons came to a
/// gigabyte on their own. Nothing looked wrong, because the one texture anybody looks at in a
/// screenshot was exactly the size it claimed to be.
///
/// The lesson is the usual one here: the number in the constant was never the number on the card,
/// and only --perf asking the renderer what it was actually holding found the difference.
/// </summary>
public static class TextureBudget
{
    /// <summary>
    /// Resize every map on every surface of <paramref name="scene"/> to fit <paramref name="max"/>.
    ///
    /// Each distinct texture is resized once, however many slots point at it. Exporters routinely
    /// pack metallic and roughness into one image and hand it to both slots, and resizing it twice
    /// would halve it twice.
    /// </summary>
    public static void Fit(Node3D scene, int max)
    {
        var done = new Dictionary<ulong, Texture2D>();

        foreach (var mi in VehicleModels.AllMeshes(scene))
        {
            if (mi.Mesh is not { } mesh) continue;

            for (int s = 0; s < mesh.GetSurfaceCount(); s++)
            {
                if (mesh.SurfaceGetMaterial(s) is not StandardMaterial3D mat) continue;

                mat.AlbedoTexture = Fit(mat.AlbedoTexture, max, done);
                mat.NormalTexture = Fit(mat.NormalTexture, max, done);
                mat.RoughnessTexture = Fit(mat.RoughnessTexture, max, done);
                mat.MetallicTexture = Fit(mat.MetallicTexture, max, done);
                mat.AOTexture = Fit(mat.AOTexture, max, done);
                mat.EmissionTexture = Fit(mat.EmissionTexture, max, done);
            }
        }
    }

    static Texture2D? Fit(Texture2D? tex, int max, Dictionary<ulong, Texture2D> done)
    {
        if (tex == null) return null;

        ulong id = tex.GetInstanceId();
        if (done.TryGetValue(id, out var already)) return already;

        var img = tex.GetImage();
        if (img == null) return tex;

        int w = img.GetWidth(), h = img.GetHeight();
        if (w <= max && h <= max) { done[id] = tex; return tex; }

        float k = max / (float)Mathf.Max(w, h);
        img.Resize(Mathf.Max(1, Mathf.RoundToInt(w * k)),
                   Mathf.Max(1, Mathf.RoundToInt(h * k)), Image.Interpolation.Lanczos);

        // Mipmaps rebuilt after the resize, not carried over from the original. A texture sampled
        // at a glancing angle across an arena needs them, and the resize drops whatever was there.
        img.GenerateMipmaps();

        var smaller = ImageTexture.CreateFromImage(img);
        done[id] = smaller;
        return smaller;
    }
}
