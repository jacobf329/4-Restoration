using System;
using System.Collections.Generic;
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

    /// <summary>
    /// Every take of every sound. More than one take means the game picks between them, which is
    /// what stops four footsteps a second from turning into a machine gun of the same click.
    /// </summary>
    static readonly List<AudioStreamWav>[] banks =
        new List<AudioStreamWav>[Enum.GetValues<Sound>().Length];

    /// <summary>Where generated audio is looked for. Missing files fall back to synthesis.</summary>
    public const string Folder = "res://assets/sfx";

    /// <summary>
    /// Sounds addressed by file name rather than by enum value, loaded the first time they are
    /// asked for.
    ///
    /// The enum stayed at thirteen values while the asset roster went to a hundred and sixty, and
    /// growing it to match would mean a hundred and sixty enum members, a hundred and sixty
    /// entries in a mapping table, and a rename every time a sound is renamed. Worse, most of the
    /// new sounds are chosen at runtime from data - which weapon you are holding, which surface
    /// you are standing on - so the call site has a string in its hand either way.
    ///
    /// So: the enum keeps the sounds the game refers to by name, and everything driven by data is
    /// addressed by key. A key with no file behind it is silent rather than an error, which is
    /// what lets a weapon reference its own sound before that sound has been generated.
    /// </summary>
    static readonly Dictionary<string, List<AudioStreamWav>> keyed = new();
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
        LoadBanks();

        voices = new AudioStreamPlayer[Voices];
        for (int i = 0; i < Voices; i++)
        {
            var p = new AudioStreamPlayer { Name = $"Voice{i}", Bus = Audio.SfxBus };
            parent.AddChild(p);
            voices[i] = p;
        }

        for (int i = 0; i < LoopVoices; i++)
        {
            var p = new AudioStreamPlayer { Name = $"Loop{i}", Bus = Audio.SfxBus, VolumeDb = -80f };
            parent.AddChild(p);
            loops.Add(new LoopVoice { Player = p });
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

        foreach (var v in loops)
        {
            if (!GodotObject.IsInstanceValid(v.Player)) continue;
            v.Player.Stop();
            v.Player.Stream = null;
            v.Player.GetParent()?.RemoveChild(v.Player);
            v.Player.Free();
        }

        loops.Clear();
        looping.Clear();

        voices = Array.Empty<AudioStreamPlayer>();
        Listeners = Array.Empty<Vector3>();

        for (int i = 0; i < banks.Length; i++) banks[i] = null!;
        keyed.Clear();
    }

    /// <summary>Plays a UI or global sound at full volume.</summary>
    public static void Play(Sound s, float volumeDb = 0f, float pitch = 1f)
    {
        if (!ready) return;

        var bank = banks[(int)s];
        if (bank == null || bank.Count == 0) return;

        var player = voices[nextVoice];
        nextVoice = (nextVoice + 1) % voices.Length;

        // A random take, not a rotating one: rotation is itself a pattern, and the ear finds it.
        player.Stream = bank.Count == 1 ? bank[0] : bank[rng.Next(bank.Count)];
        player.VolumeDb = volumeDb + Trim(FileKey(s));
        player.PitchScale = pitch;
        player.Play();
    }

    /// <summary>
    /// Every take of a key, loaded on first use. An unknown key caches an empty bank, so a miss
    /// costs one file-system probe for the life of the process rather than one per shot.
    /// </summary>
    static List<AudioStreamWav> Bank(string key)
    {
        if (keyed.TryGetValue(key, out var hit)) return hit;

        var takes = new List<AudioStreamWav>();
        if (Load($"{Folder}/{key}.wav") is { } single) takes.Add(single);

        for (int n = 1; n <= 8; n++)
            if (Load($"{Folder}/{key}_{n:00}.wav") is { } take) takes.Add(take);

        keyed[key] = takes;
        return takes;
    }

    /// <summary>Whether any audio exists for a key. Lets a caller fall back to a synthesised one.</summary>
    public static bool Has(string key) => ready && key.Length > 0 && Bank(key).Count > 0;

    /// <summary>Plays a sound by file name. Silent, not an error, when there is no such file.</summary>
    public static void PlayKey(string key, float volumeDb = 0f, float pitch = 1f)
    {
        if (!ready || key.Length == 0) return;

        var bank = Bank(key);
        if (bank.Count == 0) return;

        var player = voices[nextVoice];
        nextVoice = (nextVoice + 1) % voices.Length;

        player.Stream = bank.Count == 1 ? bank[0] : bank[rng.Next(bank.Count)];
        player.VolumeDb = volumeDb + Trim(key);
        player.PitchScale = pitch;
        player.Play();
    }

    /// <summary>The world-space form of <see cref="PlayKey"/>, attenuated like every world sound.</summary>
    public static void PlayKeyAt(string key, Vector3 where, float volumeDb = 0f, float pitch = 1f)
    {
        if (!ready || key.Length == 0) return;
        if (Bank(key).Count == 0) return;

        if (Listeners.Length == 0) { PlayKey(key, volumeDb, pitch); return; }

        float nearest = float.PositiveInfinity;
        foreach (var l in Listeners) nearest = MathF.Min(nearest, l.DistanceTo(where));
        if (nearest >= MaxAudible) return;

        PlayKey(key, volumeDb - MathU.Clamp01(nearest / MaxAudible) * 26f, pitch);
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
    /// <summary>
    /// The report for a weapon: its own recording where one exists, the synthesised shape
    /// otherwise.
    ///
    /// Keyed off the model name, so a weapon's sound and its mesh are the same word and the file
    /// convention is simply w_&lt;model&gt;. That is what lets twenty-six guns each have their own
    /// voice without twenty-six entries in a table somewhere - drop w_railgun.wav in and the
    /// railgun starts using it, exactly as dropping railgun.glb in gave it a shape.
    /// </summary>
    public static void Shot(WeaponDef w, Vector3 at, float pitch = 1f)
    {
        if (Has(ShotKey(w))) PlayKeyAt(ShotKey(w), at, 0f, pitch);
        else PlayAt(ShotFor(w), at, pitch: pitch);
    }

    /// <summary>The file a weapon's report lives in: its own if it names one, else w_&lt;model&gt;.</summary>
    public static string ShotKey(WeaponDef w)
        => w.Sound.Length > 0 ? w.Sound
         : w.Model.Length > 0 ? "w_" + w.Model
         : "";

    public static Sound ShotFor(WeaponDef w)
    {
        if (w.Pellets > 1) return Sound.ShotTactician;
        if (w.FireInterval <= 0.12f) return Sound.ShotFlanker;
        if (w.FireInterval >= 0.9f || w.HasScope) return Sound.ShotMarksman;
        return Sound.ShotTrooper;
    }

    // ---- loops ----
    //
    // A different kind of sound and so a different mechanism. Everything above is fired and
    // forgotten; an engine, a jetpack and an open portal are states, and they have to start, hold,
    // follow the thing making them and stop when it does.
    //
    // Stated rather than commanded. A caller says "this is running, here, this loudly" every frame
    // it is true and says nothing when it is not, and the mixer works out the rest. That is the
    // only shape that survives contact with the game: a vehicle can be destroyed, ejected from,
    // respawned or simply removed between one frame and the next, and every one of those would be
    // a missed Stop() call and an engine note left running over an empty map.
    //
    // Their own voices, not the pool above. A loop that can be evicted by the sixteenth gunshot of
    // a firefight is a loop that cuts out exactly when the firefight starts.

    const int LoopVoices = 8;

    /// <summary>
    /// How long a loop keeps playing after its last claim, in seconds.
    ///
    /// Not one frame. Loops are claimed from the physics step and faded here on the frame clock,
    /// and above sixty frames a second there are display frames with no physics step behind them -
    /// so "claimed since last tick" would drop every engine in the game every other frame on a
    /// fast machine. A tenth of a second is longer than any gap either clock can produce.
    /// </summary>
    const float LoopHold = 0.12f;

    /// <summary>Seconds a loop takes to reach full level, and to fall away again.</summary>
    const float LoopFade = 0.18f;

    sealed class LoopVoice
    {
        public string Id = "";
        public string Key = "";
        public AudioStreamPlayer Player = null!;
        public float TargetDb;
        public float Gain;
        public float SinceClaim = 99f;
    }

    static readonly List<LoopVoice> loops = new();
    static readonly Dictionary<string, AudioStreamWav?> looping = new();

    /// <summary>
    /// Says that <paramref name="id"/> is making <paramref name="key"/> at a place, right now.
    ///
    /// Call it every frame the thing is running. Stop calling it and the sound fades out on its
    /// own - there is no Stop, deliberately, because every caller that would need one is a caller
    /// that can stop existing.
    /// </summary>
    public static void Loop(string id, string key, Vector3 where, float volumeDb = 0f,
                            float pitch = 1f)
    {
        if (!ready || key.Length == 0) return;

        float db = volumeDb + Trim(key);

        // The same distance rule world one-shots use, so a tank you can hear across the map is not
        // a different tank from the one you can hear shooting.
        if (Listeners.Length > 0)
        {
            float nearest = float.PositiveInfinity;
            foreach (var l in Listeners) nearest = MathF.Min(nearest, l.DistanceTo(where));
            if (nearest >= MaxAudible) return;

            db -= MathU.Clamp01(nearest / MaxAudible) * 26f;
        }

        var voice = Find(id);
        if (voice == null) return;

        if (voice.Key != key)
        {
            if (LoopStream(key) is not { } stream) return;

            voice.Key = key;
            voice.Player.Stream = stream;
            voice.Player.Play();
        }
        else if (!voice.Player.Playing) voice.Player.Play();

        voice.TargetDb = db;
        voice.Player.PitchScale = pitch;
        voice.SinceClaim = 0f;
    }

    /// <summary>The voice already holding this id, or a free one, or null when all are busy.</summary>
    static LoopVoice? Find(string id)
    {
        foreach (var v in loops) if (v.Id == id) return v;

        // Free means faded out, not merely unclaimed: taking a voice that is still audible would
        // cut one engine off mid-note to start another.
        foreach (var v in loops)
        {
            if (v.SinceClaim < LoopHold || v.Gain > 0.01f) continue;

            v.Id = id;
            v.Key = "";
            v.Gain = 0f;
            return v;
        }

        return null;
    }

    /// <summary>
    /// Advances every loop's fade. Driven from the frame clock, like the music crossfade.
    /// </summary>
    public static void TickLoops(float dt)
    {
        if (!ready) return;

        foreach (var v in loops)
        {
            v.SinceClaim += dt;

            float want = v.SinceClaim <= LoopHold ? 1f : 0f;
            v.Gain = Mathf.MoveToward(v.Gain, want, dt / LoopFade);

            if (v.Gain <= 0.001f)
            {
                if (v.Player.Playing) { v.Player.Stop(); v.Player.Stream = null; }
                v.Key = "";
                v.Id = "";
                continue;
            }

            // Amplitude, converted to decibels, so a fade sounds like a fade rather than like the
            // level jumping the last twenty decibels at the end.
            v.Player.VolumeDb = v.TargetDb + 20f * MathF.Log10(MathF.Max(v.Gain, 0.0001f));
        }
    }

    /// <summary>
    /// A cue loaded with its loop points set, cached separately from the one-shot banks.
    ///
    /// Separately on purpose: the banks hand the same AudioStreamWav to every voice that plays it,
    /// and setting LoopMode on a shared instance would make the one-shot form of the same file
    /// play until the heat death of the universe.
    /// </summary>
    static AudioStreamWav? LoopStream(string key)
    {
        if (looping.TryGetValue(key, out var hit)) return hit;

        var w = Load($"{Folder}/{key}.wav");
        if (w != null)
        {
            w.LoopMode = AudioStreamWav.LoopModeEnum.Forward;
            w.LoopBegin = 0;
            w.LoopEnd = w.Data.Length / 2;      // 16-bit mono: two bytes a sample
        }

        looping[key] = w;
        return w;
    }

    // ---- the mix ----

    /// <summary>
    /// How far under full scale each family of sounds sits, in decibels.
    ///
    /// This table is the whole answer to "the sounds are mixed way too loud". Every generated
    /// effect is mastered to a decibel below full scale, which is right for the FILE - it is how
    /// you keep a quiet recording out of the noise and a loud one out of the clipper - and wrong
    /// for the GAME, because it makes a footstep exactly as loud as a tank shell. Normalisation
    /// gives every sound the same level; a mix is the business of taking that back away.
    ///
    /// Keyed by the file-name prefix rather than by enum or by call site, for the same reason the
    /// banks are: the roster grows by dropping a file in, and a new footstep should arrive already
    /// at footstep level without anyone remembering to say so. A prefix with no entry plays at
    /// unity, so an unrecognised sound is audible and obviously unmixed rather than silent.
    /// </summary>
    static readonly (string Prefix, float Db)[] Trims =
    {
        ("x_",   -1f),    // explosions: the loudest thing that happens, and the reference
        ("v_",  -10f),    // vehicles, mostly loops and engine noise
        ("w_",   -6f),    // weapon reports
        ("ob_",  -8f),    // objectives: flags, dominion flips
        ("ab_",  -8f),    // abilities
        ("m_",   -8f),    // melee
        ("st_",  -9f),    // the player's own state: hurt, heal, death
        ("po_", -10f),    // portals
        ("mc_", -10f),    // machinery: lifts, push walls, launch pads
        ("gr_", -12f),    // grapple
        ("ui_", -12f),    // menu and lobby
        ("i_",  -13f),    // impacts, which fire on every single bullet that lands
        ("mv_", -14f),    // jumps, slides, landings
        ("jp_", -14f),    // jetpack
        ("f_",  -20f),    // footsteps: four a second, per pawn, for the whole match
    };

    /// <summary>
    /// Sounds whose family level is wrong for them specifically.
    ///
    /// Short and meant to stay short. A cannon is not a tank engine and a headshot is not a body
    /// shot, but if this list starts growing past a dozen it means a family in
    /// <see cref="Trims"/> is drawn in the wrong place.
    /// </summary>
    static readonly Dictionary<string, float> Overrides = new()
    {
        ["v_tank_cannon"] = -2f,
        ["v_wreck"] = -4f,
        ["v_plane_guns"] = -8f,
        ["v_tank_idle"] = -16f,
        ["v_buggy_idle"] = -16f,
        ["i_headshot"] = -7f,
        ["x_small"] = -4f,
        ["x_breakable"] = -7f,
        ["st_low_health"] = -6f,
        ["jp_loop"] = -18f,
        ["po_idle"] = -18f,
    };

    /// <summary>Where a sound sits in the mix, in decibels below full scale.</summary>
    public static float Trim(string key)
    {
        if (key.Length == 0) return 0f;
        if (Overrides.TryGetValue(key, out float exact)) return exact;

        // Longest prefix wins, so "st_" cannot be shadowed by a hypothetical "s_".
        float db = 0f;
        int best = 0;
        foreach (var (prefix, value) in Trims)
            if (key.StartsWith(prefix, StringComparison.Ordinal) && prefix.Length > best)
            {
                best = prefix.Length;
                db = value;
            }
        return db;
    }

    // ---- synthesis ----

    static void Set(Sound s, AudioStreamWav w) => banks[(int)s] = new List<AudioStreamWav> { w };

    /// <summary>
    /// The file each sound looks for, without extension. Empty means "synthesised only".
    ///
    /// A table rather than the enum's own names, because the two vocabularies are not the same
    /// and should not be forced to be: the enum is named for what the GAME does (a shot from a
    /// fast weapon) and the files are named for what the SOUND is (a submachine gun). Renaming
    /// the enum to match would spread audio-pipeline naming through every call site in the match.
    /// </summary>
    static string FileKey(Sound s) => s switch
    {
        Sound.ShotTrooper => "w_assault_rifle",
        Sound.ShotFlanker => "w_smg",
        Sound.ShotTactician => "w_shotgun",
        Sound.ShotMarksman => "w_sniper_rifle",
        Sound.Hit => "i_flesh",
        Sound.Death => "st_death",
        Sound.Dash => "mv_slide",
        Sound.Respawn => "st_heal",

        // The menu blips stay synthesised on purpose. They are forty milliseconds long, they fire
        // constantly, and the procedural ones are already exactly right - a generated click would
        // be a larger file doing the same job slightly worse.
        _ => "",
    };

    /// <summary>
    /// Replaces any synthesised bank for which real audio has been generated.
    ///
    /// The same bargain the weapon models, the character models and the surface textures all run
    /// on, and for the same reason: a hundred and ninety audio files arrive over days, and each
    /// one has to start working the moment it lands without a code change and without the ones
    /// that have not arrived yet sounding broken in the meantime. Synthesis is the floor, not a
    /// placeholder to be torn out.
    /// </summary>
    static void LoadBanks()
    {
        foreach (var s in Enum.GetValues<Sound>())
        {
            string key = FileKey(s);
            if (key.Length == 0) continue;

            var takes = new List<AudioStreamWav>();

            // A bare name is one take; numbered files are variants of the same sound.
            if (Load($"{Folder}/{key}.wav") is { } single) takes.Add(single);

            for (int n = 1; n <= 8; n++)
                if (Load($"{Folder}/{key}_{n:00}.wav") is { } take) takes.Add(take);

            if (takes.Count > 0) banks[(int)s] = takes;
        }
    }

    /// <summary>
    /// Parse one file, for the harness. Audio never initialises in a headless run - there is no
    /// mixer to build players on - so this is the only way the asset pipeline gets checked at all.
    /// </summary>
    public static bool CanLoadForTest(string path, out int rate, out int samples)
    {
        var w = Load(path);
        rate = w?.MixRate ?? 0;
        samples = (w?.Data?.Length ?? 0) / 2;
        return w != null;
    }

    /// <summary>
    /// Every sound the game fires by a name written out in full, for the harness.
    ///
    /// This list is the inventory of what has a voice, and it is maintained by hand on purpose.
    /// A hundred and sixty files were generated and fifteen of them were ever played; nothing said
    /// so, because a file nobody asks for is silence and silence looks exactly like a sound that
    /// has not happened yet. The check that uses this asks the opposite question - is there
    /// anything on disk that nothing can reach - and it can only ask it if something knows the
    /// answer for the keys that are not composed from data.
    ///
    /// Keys built at runtime are NOT here and must not be: w_&lt;model&gt; for a weapon's report,
    /// the same with a spool suffix, f_&lt;material&gt; and i_&lt;material&gt; for the ground and for
    /// what a round struck. Those the harness composes for itself from the same tables the game
    /// does, which is the only way that check means anything.
    /// </summary>
    public static readonly string[] NamedEvents =
    {
        "ab_decoy_blast", "ab_decoy_spawn",
        "gr_reel", "gr_release",
        "jp_cutout", "jp_ignite", "jp_loop",
        "m_blade_deflect", "m_blade_hit", "m_punch", "m_saber_swing", "m_sword_swing",
        "mc_launchpad", "mc_platform", "mc_platform_stop", "mc_pushwall",
        "mv_jump", "mv_land_hard", "mv_land_soft", "mv_slide", "mv_slip",
        "ob_dominion_flip", "ob_flag_drop", "ob_flag_take",
        "po_close", "po_idle", "po_open", "po_travel",
        "st_death", "st_heal", "st_hurt", "st_low_health", "st_round_end",
        "ui_claim", "ui_denied", "ui_pause", "ui_ready", "ui_unpause", "ui_unready",
        "v_board", "v_buggy_drive", "v_buggy_idle", "v_buggy_skid", "v_eject",
        "v_plane_engine", "v_plane_guns", "v_tank_cannon", "v_tank_drive", "v_tank_idle",
        "v_tank_turret", "v_wreck",
        "x_breakable", "x_large", "x_small",
    };

    /// <summary>Every file a sound would look for, so the harness can check they all parse.</summary>
    public static List<string> ExpectedFilesForTest()
    {
        var want = new List<string>();
        foreach (var s in Enum.GetValues<Sound>())
        {
            string key = FileKey(s);
            if (key.Length == 0) continue;

            if (Godot.FileAccess.FileExists($"{Folder}/{key}.wav")) want.Add($"{Folder}/{key}.wav");
            for (int n = 1; n <= 8; n++)
            {
                string p = $"{Folder}/{key}_{n:00}.wav";
                if (Godot.FileAccess.FileExists(p)) want.Add(p);
            }
        }
        return want;
    }

    /// <summary>
    /// Reads one canonical PCM wav off disk, or null.
    ///
    /// Parsed here rather than through Godot's importer for the reason every other asset in this
    /// project is: the importer only runs when the editor opens, so an imported sound means a new
    /// file does nothing until somebody launches the editor once.
    ///
    /// Only the format tools/audio.py writes is accepted - 16-bit mono PCM - because that is the
    /// format the whole mixer already assumes. Anything else is refused rather than played at the
    /// wrong speed, which is a bug that sounds like a design decision.
    /// </summary>
    static AudioStreamWav? Load(string path)
    {
        if (!Godot.FileAccess.FileExists(path)) return null;

        byte[] raw = Godot.FileAccess.GetFileAsBytes(path);
        if (raw.Length < 44) return null;
        if (raw[0] != 'R' || raw[1] != 'I' || raw[2] != 'F' || raw[3] != 'F') return null;

        int channels = BitConverter.ToInt16(raw, 22);
        int rate = BitConverter.ToInt32(raw, 24);
        int bits = BitConverter.ToInt16(raw, 34);
        if (channels != 1 || bits != 16) return null;

        // Walk the chunks rather than assuming 44 bytes: a generator that writes a LIST chunk
        // would otherwise have its metadata played as audio, which is a burst of noise.
        int at = 12;
        while (at + 8 <= raw.Length)
        {
            int size = BitConverter.ToInt32(raw, at + 4);
            if (raw[at] == 'd' && raw[at + 1] == 'a' && raw[at + 2] == 't' && raw[at + 3] == 'a')
            {
                int len = Mathf.Min(size, raw.Length - at - 8);
                if (len <= 0) return null;

                var pcm = new byte[len];
                Array.Copy(raw, at + 8, pcm, 0, len);

                return new AudioStreamWav
                {
                    Format = AudioStreamWav.FormatEnum.Format16Bits,
                    MixRate = rate,
                    Stereo = false,
                    Data = pcm,
                };
            }
            at += 8 + size + (size & 1);
        }
        return null;
    }

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
