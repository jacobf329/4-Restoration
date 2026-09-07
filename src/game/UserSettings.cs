using Godot;

namespace HitboxClone;

/// <summary>
/// Persisted user settings.
///
/// This is a controller game. Keyboard and mouse are an opt-in extra rather than a peer input
/// method, which is also what makes the game immune to controller-to-keyboard translation layers
/// (Steam Input's desktop layout, DS4Windows, JoyToKey): with keyboard players switched off, a pad
/// being translated into keypresses and mouse clicks cannot claim a slot or act for anyone.
///
/// Keyboards can always drive *menus* regardless of this setting, so someone with no pad plugged
/// in yet is never locked out of reaching the options.
/// </summary>
public static class UserSettings
{
    const string Path = "user://settings.cfg";

    /// <summary>
    /// Whether keyboard and mouse count as a player that can claim a lobby slot and fight.
    /// Off by default.
    /// </summary>
    public static bool KeyboardAndMouse { get; set; }

    /// <summary>
    /// Rendering tier. Medium by default: splitscreen makes every per-pixel effect cost up to four
    /// times what it does in a single view, so the safe middle is the honest default.
    /// </summary>
    public static GraphicsQuality Quality { get; set; } = GraphicsQuality.Medium;

    /// <summary>
    /// How much help the stick gets when the crosshair is near someone. Standard by default,
    /// because this is a controller-first game and a pad without aim assist is not competing with
    /// a mouse — it is competing with every console shooter the player has ever felt.
    /// </summary>
    public static int AimAssist { get; set; } = 2;

    public static readonly string[] AimAssistNames = { "Off", "Light", "Standard", "Strong" };

    public static string AimAssistName => AimAssistNames[Mathf.Clamp(AimAssist, 0, 3)];

    public static void Load()
    {
        var cfg = new ConfigFile();
        if (cfg.Load(Path) != Error.Ok) return;   // no file yet: keep the defaults

        KeyboardAndMouse = cfg.GetValue("input", "keyboard_and_mouse", false).AsBool();
        PadBindings.ReadFrom(cfg);

        AimAssist = Mathf.Clamp(cfg.GetValue("input", "aim_assist", 2).AsInt32(), 0, 3);

        int q = cfg.GetValue("video", "quality", (int)GraphicsQuality.Medium).AsInt32();
        Quality = (GraphicsQuality)Mathf.Clamp(q, 0, 2);
    }

    /// <summary>
    /// Writes the whole file. Pad bindings go through here rather than owning a file of their own,
    /// so a save from either side can never clobber the other's section.
    /// </summary>
    public static void Save()
    {
        var cfg = new ConfigFile();
        cfg.SetValue("input", "keyboard_and_mouse", KeyboardAndMouse);
        cfg.SetValue("input", "aim_assist", AimAssist);
        cfg.SetValue("video", "quality", (int)Quality);
        PadBindings.WriteTo(cfg);
        cfg.Save(Path);
    }
}
