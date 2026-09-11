using System;
using System.Collections.Generic;
using Godot;

namespace HitboxClone;

/// <summary>
/// The soundtrack: one stream at a time, crossfaded.
///
/// Separate from <see cref="Sfx"/> rather than folded into it, because the two want opposite
/// things. An effect is a short buffer held in memory, fired and forgotten, and sixteen of them
/// can be in flight at once. A cue is three minutes of MP3 that loops, has to fade rather than
/// cut, and must be the only one playing. Sharing a voice pool between them would mean a
/// firefight could evict the music.
///
/// Everything here degrades to silence. A missing file is not an error and not a placeholder
/// tone - it is simply no music, the way a missing weapon model is a box and a missing surface
/// texture is the procedural panel. That is what lets twenty-five cues arrive over days.
/// </summary>
public static class Music
{
    public const string Folder = "res://assets/music";

    /// <summary>Set at startup. A headless run has no mixer to build players on.</summary>
    public static bool Enabled = true;

    /// <summary>
    /// How loud the soundtrack sits under the game.
    ///
    /// Well down. These are beds, not songs: the mix has twelve fighters, a tank and a hundred
    /// and sixty effects on top, and music that competes with a weapon report is music a player
    /// turns off.
    /// </summary>
    public const float BedDb = -14f;

    /// <summary>Seconds to cross from one cue to the next. Long enough to read as a change of place.</summary>
    const float Crossfade = 1.6f;

    static AudioStreamPlayer? active;
    static AudioStreamPlayer? fading;
    static readonly Dictionary<string, AudioStream?> cache = new();

    static string playing = "";
    static float fade;
    static float targetDb = BedDb;
    static bool ready;

    /// <summary>What is playing, so the harness can assert the right cue for the right screen.</summary>
    public static string Playing => playing;

    public static void Init(Node parent)
    {
        if (ready || !Enabled) return;
        ready = true;

        active = new AudioStreamPlayer { Name = "MusicA", Bus = "Master", VolumeDb = BedDb };
        fading = new AudioStreamPlayer { Name = "MusicB", Bus = "Master", VolumeDb = -80f };
        parent.AddChild(active);
        parent.AddChild(fading);
    }

    /// <summary>
    /// Releases the players. Same reason <see cref="Sfx.Shutdown"/> exists: static Godot objects
    /// outlive the C# bindings during shutdown and are reported as leaks followed by an assertion.
    /// </summary>
    public static void Shutdown()
    {
        if (!ready) return;
        ready = false;

        foreach (var p in new[] { active, fading })
        {
            if (p == null || !GodotObject.IsInstanceValid(p)) continue;
            p.Stop();
            p.Stream = null;
            p.GetParent()?.RemoveChild(p);
            p.Free();
        }

        active = fading = null;
        cache.Clear();
        playing = "";
    }

    /// <summary>
    /// Starts a cue, or does nothing if it is already the one playing.
    ///
    /// Idempotent on purpose: this is called every frame from whichever screen is up, so "play
    /// the arena bed" can be stated as a fact about the current screen rather than tracked as an
    /// event. A screen that forgets to stop the music is then impossible.
    /// </summary>
    public static void Play(string key, bool loop = true)
    {
        if (!ready || active == null || fading == null) return;
        if (key == playing) return;

        if (Stream(key) is not { } stream)
        {
            // No file for this cue yet. Leave whatever is playing rather than cutting to silence:
            // during the weeks the roster is filling up, the alternative is music that stops every
            // time you open a screen nobody has written a cue for.
            return;
        }

        SetLoop(stream, loop);

        // The outgoing player becomes the fading one, so a third cue during a crossfade replaces
        // the incoming rather than stacking a third voice.
        (active, fading) = (fading, active);

        active.Stream = stream;
        active.VolumeDb = -80f;
        active.Play();

        fade = 0f;
        playing = key;
    }

    /// <summary>Fades the soundtrack out and forgets what was playing.</summary>
    public static void Stop()
    {
        if (!ready || active == null) return;
        playing = "";
        fade = 0f;
        (active, fading) = (fading, active);
        active!.Stream = null;
    }

    /// <summary>Pulls the bed down while something louder happens, in decibels below its usual level.</summary>
    public static void Duck(float db) => targetDb = BedDb - MathF.Abs(db);

    /// <summary>Releases a duck.</summary>
    public static void Unduck() => targetDb = BedDb;

    /// <summary>Advances the crossfade. Driven from the frame clock, not the physics clock.</summary>
    public static void Tick(float dt)
    {
        if (!ready || active == null || fading == null) return;

        fade = MathF.Min(1f, fade + dt / Crossfade);

        // Equal-power rather than linear: two linear ramps dip in the middle, which is audible as
        // the music briefly going away at exactly the moment it is meant to be changing.
        float up = MathF.Sin(fade * MathF.Tau * 0.25f);
        float down = MathF.Cos(fade * MathF.Tau * 0.25f);

        active.VolumeDb = targetDb + Db(up);
        fading.VolumeDb = targetDb + Db(down);

        if (fade >= 1f && fading.Playing) fading.Stop();
    }

    static float Db(float gain) => gain <= 0.0005f ? -80f : 20f * MathF.Log10(gain);

    /// <summary>
    /// Loads a cue off disk, cached. Null when there is no file, which is the normal state for
    /// most of the roster most of the time.
    /// </summary>
    static AudioStream? Stream(string key)
    {
        if (cache.TryGetValue(key, out var hit)) return hit;

        string path = $"{Folder}/{key}.mp3";
        AudioStream? made = null;

        // Read and construct rather than ResourceLoader.Load, for the reason every asset in this
        // project is read that way: the import pipeline only runs when the editor opens, so an
        // imported cue does nothing until somebody launches the editor once.
        if (Godot.FileAccess.FileExists(path))
        {
            byte[] raw = Godot.FileAccess.GetFileAsBytes(path);
            if (raw.Length > 128) made = AudioStreamMP3.LoadFromBuffer(raw);
        }

        cache[key] = made;
        return made;
    }

    static void SetLoop(AudioStream stream, bool loop)
    {
        if (stream is AudioStreamMP3 mp3) mp3.Loop = loop;
    }

    /// <summary>Whether a cue has audio behind it. For the harness, and for callers with a choice.</summary>
    public static bool Has(string key) => Stream(key) != null;
}
