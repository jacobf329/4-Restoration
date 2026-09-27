using System.Collections.Generic;
using Godot;

namespace HitboxClone;

/// <summary>
/// What shape each prop actually is, read from the manifest the generator writes.
///
/// This exists because of a mistake worth keeping written down. The prop boxes were first authored
/// by hand, before any mesh existed - a snowdrift was written twelve metres long because that is
/// what a drift ought to be. The mesh that came back was nearly square in plan, so fitted inside
/// that box it would have filled a third of it, and the collider would have been a lie: cover you
/// could shoot through and stand inside, which is worse than a plain box because a plain box at
/// least tells the truth about itself.
///
/// You cannot know a generated mesh's proportions before you generate it. So the arena states the
/// SIZE it wants and this answers with the mesh's own proportions at that size, and the box always
/// matches the thing standing in it.
///
/// Read from JSON rather than compiled in, because the numbers are measurements and the generator
/// owns them - regenerating a prop rewrites its entry, and nothing has to be kept in step by hand.
/// Read through FileAccess and Godot's own parser, so it works in a headless run: the harness has
/// to build the same geometry the game does, and a prop shape that only existed when there was a
/// rendering server would mean the tests measured a different map from the one people play.
/// </summary>
public static class PropShapes
{
    public const string Manifest = "res://assets/props-manifest.json";

    static Dictionary<string, Vector3>? measured;

    static Dictionary<string, Vector3> Table()
    {
        if (measured != null) return measured;

        measured = new Dictionary<string, Vector3>();

        if (!Godot.FileAccess.FileExists(Manifest)) return measured;

        string text = Godot.FileAccess.GetFileAsString(Manifest);
        if (Json.ParseString(text).AsGodotDictionary() is not { } doc) return measured;
        if (!doc.ContainsKey("props")) return measured;

        foreach (var entry in doc["props"].AsGodotArray())
        {
            var prop = entry.AsGodotDictionary();
            if (!prop.ContainsKey("key") || !prop.ContainsKey("measured")) continue;

            var size = prop["measured"].AsGodotArray();
            if (size.Count != 3) continue;

            measured[prop["key"].AsString()] =
                new Vector3((float)size[0].AsDouble(),
                            (float)size[1].AsDouble(),
                            (float)size[2].AsDouble());
        }

        return measured;
    }

    /// <summary>Whether this prop has been generated and measured.</summary>
    public static bool Known(string key) => Table().ContainsKey(key);

    /// <summary>
    /// Half-extents of <paramref name="key"/> when its longest axis is <paramref name="longest"/>
    /// metres. A prop with no measurement yet is a cube of that size, which is what it will be
    /// drawn as until its mesh lands.
    /// </summary>
    public static Vector3 HalfExtents(string key, float longest)
    {
        if (!Table().TryGetValue(key, out var size) || size.X <= 0f || size.Y <= 0f || size.Z <= 0f)
            return Vector3.One * (longest * 0.5f);

        float biggest = Mathf.Max(size.X, Mathf.Max(size.Y, size.Z));
        return size * (longest / biggest) * 0.5f;
    }

    /// <summary>Forget the table, so the harness can prove the missing-manifest path works.</summary>
    public static void ClearCacheForTest() => measured = null;
}
