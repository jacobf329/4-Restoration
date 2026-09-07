using System;
using Godot;

namespace HitboxClone;

public enum Sound
{
    ShotTrooper, ShotFlanker, ShotTactician, ShotMarksman,
    Hit, Death, Dash, Respawn,
    MenuMove, MenuConfirm, MenuBack,
    ZoneCapture, MatchEnd,
}

/// <summary>
/// Every sound in the game, synthesised into in-memory PCM at startup. There are no audio files
/// anywhere in the project, matching how the geometry is built — the whole thing stays a single
/// self-contained build with nothing to lose track of.
///
/// Playback is deliberately non-positional. Godot mixes 3D audio against one listener per viewport,
/// and with four splitscreen views sharing a world that produces a mess. Instead world sounds are
/// attenuated against whichever human player is nearest, which is the mix a couch actually wants:
/// if it is happening near anyone on the sofa, everyone hears it.
/// </summary>
public static class Sfx
{
    const int SampleRate = 22050;
    const int Voices = 16;

    static readonly AudioStreamWav?[] streams = new AudioStreamWav?[Enum.GetValues<Sound>().Length];
    static AudioStreamPlayer[] voices = Array.Empty<AudioStreamPlayer>();
    static int nextVoice;
    static bool ready;

    /// <summary>Where the human players are, refreshed each frame by the match. Empty in menus.</summary>
    public static Vector3[] Listeners = Array.Empty<Vector3>();

    /// <summary>Beyond this distance a world sound is inaudible.</summary>
    const float MaxAudible = 55f;

    public static void Init(Node parent)
    {
        if (ready) return;
        ready = true;

        Build();

        voices = new AudioStreamPlayer[Voices];
        for (int i = 0; i < Voices; i++)
        {
            var p = new AudioStreamPlayer { Name = $"Voice{i}", Bus = "Master" };
            parent.AddChild(p);
            voices[i] = p;
        }
    }

    /// <summary>
    /// Releases every Godot object held in static state.
    ///
    /// Required, not housekeeping: these statics outlive the engine's C# script bindings during
    /// shutdown, which Godot reports as hundreds of leaked references followed by a fatal
    /// assertion. The player would see that on every quit.
    /// </summary>
    public static void Shutdown()
    {
        if (!ready) return;
        ready = false;

        foreach (var v in voices)
        {
            if (!GodotObject.IsInstanceValid(v)) continue;

            // Clear the player's own reference first: nulling the streams array is not enough
            // while a player still points at the buffer it last played.
            v.Stop();
            v.Stream = null;

            // Freed immediately, not queued. During shutdown the deferred queue may never be
            // drained, which left the players alive past the C# bindings and produced a fatal
            // "leaked unsafe reference" on exit.
            v.GetParent()?.RemoveChild(v);
            v.Free();
        }

        voices = Array.Empty<AudioStreamPlayer>();
        Listeners = Array.Empty<Vector3>();

        for (int i = 0; i < streams.Length; i++) streams[i] = null;
    }

    /// <summary>Plays a UI or global sound at full volume.</summary>
    public static void Play(Sound s, float volumeDb = 0f, float pitch = 1f)
    {
        if (!ready) return;

        var player = voices[nextVoice];
        nextVoice = (nextVoice + 1) % voices.Length;

        player.Stream = streams[(int)s];
        player.VolumeDb = volumeDb;
        player.PitchScale = pitch;
        player.Play();
    }

    /// <summary>Plays a world sound, attenuated by distance to the nearest human player.</summary>
    public static void PlayAt(Sound s, Vector3 where, float volumeDb = 0f, float pitch = 1f)
    {
        if (!ready) return;

        if (Listeners.Length == 0) { Play(s, volumeDb, pitch); return; }

        float nearest = float.PositiveInfinity;
        foreach (var l in Listeners) nearest = MathF.Min(nearest, l.DistanceTo(where));

        if (nearest >= MaxAudible) return;

        // Linear falloff in decibels rather than amplitude: it keeps distant shots audible enough
        // to tell you a fight is happening somewhere, without them competing with your own gun.
        float fade = MathU.Clamp01(nearest / MaxAudible);
        Play(s, volumeDb - fade * 26f, pitch);
    }

    /// <summary>
    /// Picks a report by the weapon's shape rather than by class name, so a picked-up gun sounds
    /// like what it is: a scattergun booms, a minigun ticks, a railgun cracks.
    /// </summary>
    public static Sound ShotFor(WeaponDef w)
    {
        if (w.Pellets > 1) return Sound.ShotTactician;
        if (w.FireInterval <= 0.12f) return Sound.ShotFlanker;
        if (w.FireInterval >= 0.9f || w.HasScope) return Sound.ShotMarksman;
        return Sound.ShotTrooper;
    }

    // ---- synthesis ----

    static void Set(Sound s, AudioStreamWav w) => streams[(int)s] = w;

    static void Build()
    {
        // Weapons are noise bursts shaped by an envelope, pitched to match the class: the shotgun
        // is a low thump, the SMG a dry tick, the rifle a sharp crack with a tail.
        Set(Sound.ShotTrooper, Make(0.13f, (t, u) =>
            Noise() * Decay(u, 14f) * 0.55f + Sine(t, 180f - 90f * u) * Decay(u, 20f) * 0.35f));

        Set(Sound.ShotFlanker, Make(0.07f, (t, u) =>
            Noise() * Decay(u, 26f) * 0.42f + Sine(t, 320f - 140f * u) * Decay(u, 30f) * 0.22f));

        Set(Sound.ShotTactician, Make(0.26f, (t, u) =>
            Noise() * Decay(u, 8f) * 0.68f + Sine(t, 95f - 45f * u) * Decay(u, 9f) * 0.5f));

        Set(Sound.ShotMarksman, Make(0.34f, (t, u) =>
            Noise() * Decay(u, 30f) * 0.6f + Sine(t, 620f - 480f * u) * Decay(u, 6f) * 0.3f));

        Set(Sound.Hit, Make(0.09f, (t, u) =>
            Sine(t, 760f - 260f * u) * Decay(u, 24f) * 0.5f + Noise() * Decay(u, 40f) * 0.2f));

        Set(Sound.Death, Make(0.55f, (t, u) =>
            Sine(t, 300f - 220f * u) * Decay(u, 4.5f) * 0.5f + Noise() * Decay(u, 12f) * 0.15f));

        // A filtered noise sweep, which reads as movement rather than as an impact.
        Set(Sound.Dash, Make(0.22f, (t, u) =>
            Noise() * MathF.Sin(u * MathF.PI) * 0.34f + Sine(t, 220f + 380f * u) * Decay(u, 6f) * 0.16f));

        Set(Sound.Respawn, Make(0.34f, (t, u) =>
            Sine(t, 260f + 340f * u) * Decay(u, 4f) * 0.34f));

        Set(Sound.MenuMove, Make(0.045f, (t, u) => Sine(t, 520f) * Decay(u, 30f) * 0.22f));
        Set(Sound.MenuConfirm, Make(0.10f, (t, u) => Sine(t, 620f + 260f * u) * Decay(u, 16f) * 0.26f));
        Set(Sound.MenuBack, Make(0.10f, (t, u) => Sine(t, 460f - 180f * u) * Decay(u, 16f) * 0.24f));

        Set(Sound.ZoneCapture, Make(0.42f, (t, u) =>
            (Sine(t, 440f) + Sine(t, 660f)) * 0.5f * Decay(u, 5f) * 0.34f));

        // A rising major triad — unmistakably an ending, without needing a jingle.
        Set(Sound.MatchEnd, Make(1.0f, (t, u) =>
            (Sine(t, 392f) + Sine(t, 494f) * Gate(u, 0.18f) + Sine(t, 587f) * Gate(u, 0.36f))
            * 0.33f * Decay(u, 2.2f) * 0.42f));
    }

    static readonly Random rng = new(20260815);

    static float Noise() => (float)(rng.NextDouble() * 2.0 - 1.0);
    static float Sine(float t, float hz) => MathF.Sin(t * hz * MathF.Tau);
    static float Decay(float u, float rate) => MathF.Exp(-u * rate);
    static float Gate(float u, float after) => u < after ? 0f : 1f;

    /// <summary>
    /// Renders a mono 16-bit buffer. The generator gets absolute time and normalised progress,
    /// since envelopes want the latter and oscillators the former.
    /// </summary>
    static AudioStreamWav Make(float seconds, Func<float, float, float> gen)
    {
        int count = Mathf.Max(1, (int)(seconds * SampleRate));
        var data = new byte[count * 2];

        for (int i = 0; i < count; i++)
        {
            float t = (float)i / SampleRate;
            float u = (float)i / count;

            float v = gen(t, u);

            // A short fade at the tail stops any envelope that has not quite reached zero from
            // clicking when the buffer ends.
            if (u > 0.92f) v *= (1f - u) / 0.08f;

            short sample = (short)(MathU.Clamp(v, -1f, 1f) * short.MaxValue);
            data[i * 2] = (byte)(sample & 0xFF);
            data[i * 2 + 1] = (byte)((sample >> 8) & 0xFF);
        }

        return new AudioStreamWav
        {
            Format = AudioStreamWav.FormatEnum.Format16Bits,
            MixRate = SampleRate,
            Stereo = false,
            Data = data,
        };
    }
}
