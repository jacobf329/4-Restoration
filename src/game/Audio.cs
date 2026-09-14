using Godot;

namespace HitboxClone;

/// <summary>
/// The mixer: two buses under Master, and the player's levels on top of them.
///
/// Why buses at all, when both <see cref="Sfx"/> and <see cref="Music"/> already set a volume on
/// every player they own? Because a slider has to move a whole category at once. Without a bus,
/// "music quieter" means remembering to add the offset in <see cref="Music.Tick"/>, in the
/// crossfade, in the duck, and in anything added later - and the first place somebody forgets is
/// the place the slider stops working. A bus is one multiplication the engine applies after all of
/// that, so the offsets stay local decisions and the slider stays a fact about the whole category.
///
/// Three levels rather than one, because the complaint that produced them was specifically about
/// the balance between two of them: the effects were mastered to a decibel below full scale and
/// the soundtrack was not mastered at all, so a player could either hear the guns or hear the
/// music. The files are fixed now, but the balance is taste, and taste belongs to the player.
/// </summary>
public static class Audio
{
    public const string MusicBus = "Music";
    public const string SfxBus = "Sfx";

    /// <summary>
    /// Room tone: its own bus, and so its own slider.
    ///
    /// Neither of the other two, and it took playing it to see why. Put it on the music bus and a
    /// player who turns the score off loses the world with it - a crypt that sounds like nothing
    /// is not a crypt. Put it on effects and anyone who pulls the guns down to hear footsteps
    /// pulls the room down too, which is the opposite of what they were after. It is a third
    /// thing, and the mixer says so.
    /// </summary>
    public const string AmbienceBus = "Ambience";

    /// <summary>Ten steps, so a stick press is a noticeable but not drastic change.</summary>
    public const int Steps = 10;

    /// <summary>
    /// A step below full is unity gain, and the steps below that are 3.5 dB apart down to silence.
    ///
    /// Linear in decibels rather than in amplitude, because loudness is: halving an amplitude
    /// slider from 10 to 5 is a 6 dB drop, and halving it again from 5 to 2.5 is another 6, so the
    /// bottom half of a linear slider does almost nothing audible and the top half does everything.
    /// One step above unity exists so a player on quiet speakers is not stuck at the ceiling.
    /// </summary>
    public static float Db(int step)
    {
        step = Mathf.Clamp(step, 0, Steps);
        if (step == 0) return -80f;
        return (step - 9) * 3.5f;
    }

    static bool ready;

    /// <summary>
    /// Builds the buses. Called before <see cref="Sfx.Init"/> and <see cref="Music.Init"/>, which
    /// name them when they create their players — a player assigned to a bus that does not exist
    /// is silently routed to Master, which is exactly the failure this ordering prevents.
    /// </summary>
    public static void Init()
    {
        if (ready) return;
        ready = true;

        foreach (string name in new[] { MusicBus, SfxBus, AmbienceBus })
        {
            if (AudioServer.GetBusIndex(name) >= 0) continue;

            int i = AudioServer.BusCount;
            AudioServer.AddBus(i);
            AudioServer.SetBusName(i, name);
            AudioServer.SetBusSend(i, "Master");
        }

        Apply();
    }

    /// <summary>Pushes the player's levels onto the buses. Call after changing any of them.</summary>
    public static void Apply()
    {
        if (!ready) return;

        Set("Master", UserSettings.MasterVolume);
        Set(MusicBus, UserSettings.MusicVolume);
        Set(SfxBus, UserSettings.SfxVolume);
        Set(AmbienceBus, UserSettings.AmbienceVolume);
    }

    static void Set(string bus, int step)
    {
        int i = AudioServer.GetBusIndex(bus);
        if (i < 0) return;

        AudioServer.SetBusVolumeDb(i, Db(step));

        // Muting as well as attenuating: -80 dB is inaudible but still mixes, and a bus left
        // running at -80 keeps decoding an MP3 nobody is listening to.
        AudioServer.SetBusMute(i, step == 0);
    }
}
