using System.Collections.Generic;
using Godot;

namespace HitboxClone;

/// <summary>
/// Painted backdrops for the menu screens.
///
/// This is the one place in the project that touches an asset file. Everything else — arenas,
/// weapons, pawns, the sky, the sounds — is generated, and that stays true: these are menu
/// wallpaper, not game content, and nothing in a match depends on them.
///
/// Loaded at runtime with <c>Image.LoadFromFile</c> rather than through <c>res://</c> preloading,
/// deliberately. Godot's import pipeline only runs when the editor opens the project, so a preloaded
/// texture would mean dropping a new image in did nothing until you launched the editor once. This
/// way a file appears in the folder and the next launch uses it.
///
/// Every lookup is allowed to fail. A missing folder, a missing file or a corrupt image all fall
/// back to the procedural background, so the menus work exactly as before with no assets present.
/// </summary>
public static class MenuBackdrop
{
    /// <summary>Where the images live, relative to the project root.</summary>
    public const string Folder = "res://assets/menu";

    /// <summary>
    /// Which backdrop each screen asks for. Names rather than an enum so adding one is a file plus
    /// a string, and a screen with no entry simply gets the procedural background.
    /// </summary>
    public const string Title = "title";
    public const string ModeSelect = "mode";
    public const string Lobby = "lobby";
    public const string Options = "options";
    public const string Results = "results";

    static readonly Dictionary<string, Texture2D?> cache = new();

    /// <summary>Set once at startup. Headless runs never load or draw an image.</summary>
    public static bool Enabled = true;

    /// <summary>
    /// The backdrop for a screen, or null if there is not one. Cached, including the failures —
    /// a missing file should cost one stat call for the whole session, not one per frame.
    /// </summary>
    public static Texture2D? Get(string key)
    {
        if (!Enabled) return null;
        if (cache.TryGetValue(key, out var cached)) return cached;

        Texture2D? tex = Load(key);
        cache[key] = tex;
        return tex;
    }

    static Texture2D? Load(string key)
    {
        foreach (string ext in new[] { ".png", ".jpg", ".jpeg", ".webp" })
        {
            string path = $"{Folder}/{key}{ext}";
            if (!Godot.FileAccess.FileExists(path)) continue;

            var img = Image.LoadFromFile(path);
            if (img == null || img.IsEmpty()) continue;

            return ImageTexture.CreateFromImage(img);
        }
        return null;
    }

    /// <summary>Forget everything, so a dropped-in file is picked up without a restart.</summary>
    public static void Reload() => cache.Clear();

    /// <summary>How many backdrops are actually present. Reported by the harness.</summary>
    public static int Available()
    {
        int n = 0;
        foreach (string key in new[] { Title, ModeSelect, Lobby, Options, Results })
            if (Get(key) != null) n++;
        return n;
    }
}
