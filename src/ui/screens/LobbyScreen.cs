using System.Collections.Generic;
using Godot;

namespace HitboxClone;

/// <summary>One of the four player slots. Ported from <c>H:\BattleArena\Program.cs:32</c>.</summary>
public sealed class LobbySlot
{
    /// <summary>Id of the device that claimed this slot, or null if unclaimed.</summary>
    public string? DeviceId;

    public int ClassIndex;

    /// <summary>
    /// Which of the four peoples this player belongs to. A separate axis from class on purpose:
    /// class is how you shoot, faction is what you can do that nobody else can.
    /// </summary>
    public int FactionIndex;

    public bool Ready;
    public bool IsBot;

    public bool Claimed => DeviceId != null;
    public bool Occupied => Claimed || IsBot;
    public InputDevice? Device => DeviceId is null ? null : Devices.ById(DeviceId);
    public ClassDef Class => Classes.ByIndex(ClassIndex);
    public FactionDef Faction => Factions.ByIndex(FactionIndex);

    public void Clear() { DeviceId = null; Ready = false; IsBot = false; }
}

/// <summary>
/// The lobby, and the reason this project does not use Godot's Control focus system: four devices
/// each drive their own slot at the same time, which a single per-viewport focus cursor cannot
/// express. Each slot tracks its own owning device and its own selection.
///
/// Join model from BattleArena: any unclaimed device that presses attack or start takes the next
/// free slot, left/right picks the class, attack readies, back un-readies and then leaves.
/// </summary>
public sealed class LobbyScreen : UiScreen
{
    /// <summary>
    /// Seats in the lobby, and the hard cap on humans. Four splitscreen viewports is as far as a
    /// television goes.
    /// </summary>
    public const int MaxPlayers = 4;

    /// <summary>
    /// Fighters in a match, humans and bots together.
    ///
    /// This used to be the same number as the seats, which meant four people on an arena of
    /// fifty-seven thousand square metres — about fourteen thousand each. Halo's sparsest
    /// competitive format, Big Team Battle, runs at roughly five thousand per player, and its 4v4
    /// arenas at under a thousand. Four fighters on a map this size is why a bot match could
    /// produce nine shots in twenty-eight seconds.
    ///
    /// Twelve puts this arena at about forty-eight hundred each, which is Big Team territory —
    /// the game these maps look like they were always for.
    /// </summary>
    public const int MaxFighters = 12;

    public override string Title => "LOBBY";

    readonly MatchSettings settings;
    readonly Main app;
    public readonly LobbySlot[] Slots = new LobbySlot[MaxPlayers];

    /// <summary>Names a player whose pad vanished, so the lobby explains itself rather than just losing a slot.</summary>
    string? lostDeviceNote;
    float lostNoteTimer;

    public LobbyScreen(MatchSettings settings, Main app)
    {
        this.settings = settings;
        this.app = app;
        for (int i = 0; i < Slots.Length; i++) Slots[i] = new LobbySlot();
    }

    public int ClaimedCount
    {
        get { int n = 0; foreach (var s in Slots) if (s.Claimed) n++; return n; }
    }

    public bool AllClaimedReady
    {
        get
        {
            foreach (var s in Slots) if (s.Claimed && !s.Ready) return false;
            return ClaimedCount > 0;
        }
    }

    IEnumerable<string> ClaimedIds()
    {
        foreach (var s in Slots) if (s.DeviceId != null) yield return s.DeviceId;
    }

    protected override void UpdateScreen(float dt, IReadOnlyList<InputDevice> devices)
    {
        if (lostNoteTimer > 0f) { lostNoteTimer -= dt; if (lostNoteTimer <= 0f) lostDeviceNote = null; }

        DropDisconnected();
        Join();
        DriveSlots();
        SyncBots();

        // Start needs at least one human, everyone claimed to be ready, and a deliberate press
        // from someone already in the match — a spectator pad cannot launch it.
        if (AllClaimedReady)
        {
            foreach (var s in Slots)
            {
                var d = s.Device;
                if (s.Claimed && d is { StartPressed: true })
                {
                    Stack.Push(new MatchScreen(settings, Slots, app));
                    return;
                }
            }
        }
    }

    /// <summary>A pad unplugged in the lobby frees its slot and says so.</summary>
    void DropDisconnected()
    {
        foreach (var s in Slots)
        {
            if (!s.Claimed) continue;
            var d = s.Device;
            if (d is null || !d.Connected)
            {
                lostDeviceNote = $"{d?.Label ?? s.DeviceId} disconnected — slot freed";
                lostNoteTimer = 4f;
                s.Clear();
            }
        }
    }

    /// <summary>
    /// Devices that claimed a slot on this frame. The press that claims a seat must not also ready
    /// it — otherwise a single tap locks a player into whatever class happened to be selected,
    /// with no chance to look at the roster.
    /// </summary>
    readonly HashSet<string> joinedThisFrame = new();

    void Join()
    {
        joinedThisFrame.Clear();

        foreach (var d in Devices.AvailableUnclaimed(ClaimedIds()))
        {
            if (!d.JoinPressed) continue;
            foreach (var s in Slots)
            {
                if (s.Claimed) continue;
                s.DeviceId = d.Id;
                s.Ready = false;
                s.IsBot = false;

                // Seed the faction from the seat, so four players who never touch the up/down axis
                // still field all four peoples rather than four Vessels.
                s.FactionIndex = SlotIndexOf(s);
                joinedThisFrame.Add(d.Id);
                d.Rumble(0.25f, 0.35f, 0.12f);
                break;
            }
        }
    }

    void DriveSlots()
    {
        foreach (var s in Slots)
        {
            var d = s.Device;
            if (!s.Claimed || d is null || !d.Connected) continue;
            if (joinedThisFrame.Contains(d.Id)) continue;

            if (!s.Ready)
            {
                // Left/right picks the class, up/down picks the faction. Two axes because they are
                // two independent choices — a Garden Marksman is a coherent idea, and collapsing
                // them onto one list would force players to pick a fighting style to get a power.
                if (d.NavX != 0) s.ClassIndex = Mathf.PosMod(s.ClassIndex + d.NavX, Classes.All.Length);
                if (d.NavY != 0) s.FactionIndex = Mathf.PosMod(s.FactionIndex + d.NavY, Factions.All.Length);
                if (d.ConfirmPressed) { s.Ready = true; d.Rumble(0.2f, 0.4f, 0.1f); }
            }
        }
    }

    /// <summary>
    /// Bots fill from the back, so a human joining always lands in the leftmost free slot and
    /// bots give way rather than blocking the seat someone just sat down in.
    /// </summary>
    /// <summary>Bots that fit on the roster alongside the humans who have claimed a seat.</summary>
    public int ActiveBots => Mathf.Min(settings.BotCount, MaxFighters - ClaimedCount);

    /// <summary>Bots with no seat to be shown in. They still fight; there is just no card for them.</summary>
    public int UnseatedBots => Mathf.Max(0, ActiveBots - (MaxPlayers - ClaimedCount));

    void SyncBots()
    {
        foreach (var s in Slots) if (!s.Claimed) s.IsBot = false;

        // Seats are for showing fighters, not for holding them. Bots beyond the four seats have no
        // card and are counted underneath instead — the roster is twelve now and the lobby is not
        // going to grow eight more panels for them.
        int want = Mathf.Min(ActiveBots, MaxPlayers - ClaimedCount);

        for (int i = Slots.Length - 1; i >= 0 && want > 0; i--)
        {
            if (Slots[i].Claimed) continue;
            Slots[i].IsBot = true;
            Slots[i].FactionIndex = i;
            want--;
        }
    }

    int SlotIndexOf(LobbySlot s)
    {
        for (int i = 0; i < Slots.Length; i++) if (ReferenceEquals(Slots[i], s)) return i;
        return 0;
    }

    /// <summary>
    /// Back means something different depending on who pressed it. A player in a slot un-readies,
    /// then leaves; only a device with no slot backs out to mode select. Without this, one player
    /// tapping back would yank the whole couch out of the lobby.
    /// </summary>
    protected override bool OnBack(InputDevice d)
    {
        foreach (var s in Slots)
        {
            if (s.DeviceId != d.Id) continue;
            if (s.Ready) s.Ready = false;
            else s.Clear();
            return true;
        }
        return base.OnBack(d);
    }

    public override void Draw(UiPainter p)
    {
        Chrome.Background(p, MenuBackdrop.Lobby);
        Chrome.Header(p, Title);
        p.TextRight($"{settings.Def.Name}  ·  {settings.ArenaName}  ·  {settings.ScoreLimit} {settings.Def.LimitNoun}",
                    p.Size.X - 56f, 50f, 22, Pal.TextDim);

        float pad = 26f;
        // Height is content-driven rather than a fraction of the viewport: the slot only ever
        // holds a fixed set of rows, and sizing it to the screen just left a pool of dead space.
        float h = 470f;
        // Centred vertically, biased slightly up to leave room for the status line beneath.
        float top = (p.Size.Y - h) * 0.5f - 40f;
        float w = (p.Size.X - pad * (MaxPlayers + 1)) / MaxPlayers;

        for (int i = 0; i < MaxPlayers; i++)
            DrawSlot(p, Slots[i], i, pad + i * (w + pad), top, w, h);

        float footY = top + h + 30f;
        float cx = p.Size.X * 0.5f;

        // Hints follow whichever pad is furthest along, so the labels match a real player's device.
        InputDevice? hintDev = null;
        foreach (var s in Slots) if (s.Claimed && s.Device is not null) { hintDev = s.Device; break; }

        bool anyoneCanJoin = Devices.ConnectedGamepadCount() > 0 || UserSettings.KeyboardAndMouse;

        if (lostDeviceNote != null)
            p.TextCentered(lostDeviceNote, cx, footY, 20, Pal.Warn);
        else if (!anyoneCanJoin)
        {
            // Without this the lobby is a silent dead end: no pads connected and keyboard players
            // switched off means nothing a player presses can possibly claim a slot.
            p.TextCentered("No gamepads connected — plug one in to join",
                           cx, footY, 22, Pal.Warn);
            p.TextCentered("or enable keyboard & mouse players in Options",
                           cx, footY + 30f, 19, Pal.TextDim);
        }
        else if (ClaimedCount == 0)
        {
            string join = Glyphs.For(Prompt.Confirm, hintDev);
            p.TextCentered(UserSettings.KeyboardAndMouse
                               ? $"Press {join} or Start on any controller or keyboard to join"
                               : $"Press {join} or Start on any controller to join",
                           cx, footY, 22, Pal.Accent);
        }
        else if (!AllClaimedReady)
            p.TextCentered("Waiting for everyone to ready up", cx, footY, 22, Pal.TextDim);
        else
            p.TextCentered("Press Start to begin", cx, footY, 24, Pal.Ready);

        // What the match will actually field. The four cards stopped being the whole roster when
        // the roster grew to twelve, and a lobby that shows four fighters before dropping you into
        // a match with twelve is lying by omission.
        int fighters = ClaimedCount + ActiveBots;

        string roster = UnseatedBots > 0
            ? $"{fighters} fighters  ·  {ClaimedCount} human, {ActiveBots} CPU  ·  +{UnseatedBots} with no card"
            : $"{fighters} fighters  ·  {ClaimedCount} human, {ActiveBots} CPU";

        p.TextCentered(roster, cx, footY + 30f, 18, Pal.TextDim);

        p.HintBar(
            (Glyphs.For(Prompt.NavHorz, hintDev), "Class"),
            (Glyphs.For(Prompt.NavVert, hintDev), "Faction"),
            (Glyphs.For(Prompt.Confirm, hintDev), "Ready"),
            (Glyphs.For(Prompt.Cancel, hintDev), "Un-ready / Leave"),
            (Glyphs.For(Prompt.Start, hintDev), "Begin"));
    }

    void DrawSlot(UiPainter p, LobbySlot s, int index, float x, float y, float w, float h)
    {
        Color accent = Pal.Players[index];

        if (!s.Occupied)
        {
            p.Panel(x, y, w, h, Pal.Panel, Pal.PanelHi);
            p.TextCentered("EMPTY", x + w * 0.5f, y + h * 0.5f - 14f, 24, Pal.TextDim);
            p.TextCentered($"press {Glyphs.For(Prompt.Confirm, (InputDevice?)null)} to join",
                           x + w * 0.5f, y + h * 0.5f + 18f, 15, Pal.TextDim);
            return;
        }

        Color border = s.Ready ? Pal.Ready : (s.IsBot ? Pal.PanelHi : accent);
        p.Panel(x, y, w, h, Pal.Panel, border);

        // Header stripe carries the player colour, so slots stay distinguishable at a glance.
        p.Rect(x, y, w, 8f, s.IsBot ? Pal.PanelHi : accent);

        float cx = x + w * 0.5f;
        float ty = y + 26f;

        p.TextCentered(s.IsBot ? $"CPU {index + 1}" : $"PLAYER {index + 1}", cx, ty, 24,
                       s.IsBot ? Pal.TextDim : accent);
        ty += 34f;

        string devLabel = s.IsBot ? $"CPU · {settings.BotSkillName}" : s.Device?.Label ?? "?";
        p.TextCentered(Trim(p, devLabel, w - 20f, 15), cx, ty, 15, Pal.TextDim);
        ty += 40f;

        var cls = s.Class;
        var fac = s.Faction;
        bool choosing = !s.IsBot && !s.Ready;

        // Arrows sit level with the name they cycle, not floating at the panel's midpoint where
        // they collided with the stat bars.
        if (choosing)
        {
            p.Text("<", x + 14f, ty + 2f, 28, accent);
            p.TextRight(">", x + w - 14f, ty + 2f, 28, accent);
        }

        p.TextCentered(cls.Name, cx, ty, 30, Pal.Text);
        ty += 36f;
        p.TextCentered(cls.Role, cx, ty, 16, Pal.TextDim);
        ty += 26f;
        p.TextCentered(cls.WeaponName, cx, ty, 18, accent);
        ty += 22f;

        // The class ability. It is half of what picking a class now means, so it belongs on the
        // class half of the card rather than being something you discover on the D-pad mid-match.
        p.TextCentered(cls.SpecialName.ToUpperInvariant(), cx, ty, 15, Pal.Ready);
        ty += 22f;

        Stat(p, "HP", cls.Health / 130f, x + 18f, ty, w - 36f, accent); ty += 21f;
        Stat(p, "SPD", cls.Speed / 9f, x + 18f, ty, w - 36f, accent); ty += 21f;
        Stat(p, "DPS", cls.Dps / 100f, x + 18f, ty, w - 36f, accent); ty += 30f;

        // ---- faction ----
        //
        // The special used to be one line reading "Special: Second Wind", which is a name and not
        // an explanation — nobody could tell what it did without pressing it in a live match. The
        // blurb is the point of this block; the name alone was the bug.
        p.Rect(x + 14f, ty - 4f, w - 28f, 2f, Pal.PanelHi);
        ty += 10f;

        if (choosing)
        {
            p.Text("^", x + 18f, ty + 1f, 20, fac.Tint);
            p.TextRight("v", x + w - 18f, ty + 1f, 20, fac.Tint);
        }

        p.TextCentered(fac.Name, cx, ty, 22, fac.Tint);
        ty += 26f;
        p.TextCentered(fac.Answer, cx, ty, 14, Pal.TextDim);
        ty += 26f;

        p.TextCentered(fac.SpecialName.ToUpperInvariant(), cx, ty, 17, Pal.Accent);
        ty += 22f;

        foreach (string line in Wrap(p, fac.SpecialBlurb, w - 30f, 14))
        {
            p.TextCentered(line, cx, ty, 14, Pal.TextDim);
            ty += 19f;
        }

        string status = s.IsBot ? "CPU" : (s.Ready ? "READY" : "CHOOSING");
        p.TextCentered(status, cx, y + h - 34f, 22, s.Ready ? Pal.Ready : Pal.TextDim);
    }

    /// <summary>
    /// Greedy word wrap to a pixel width. The blurbs are one sentence each, so this never has to
    /// deal with a word wider than the column.
    /// </summary>
    static List<string> Wrap(UiPainter p, string text, float maxW, int size)
    {
        var lines = new List<string>();
        string line = "";

        foreach (string word in text.Split(' '))
        {
            string candidate = line.Length == 0 ? word : line + " " + word;
            if (line.Length > 0 && p.Measure(candidate, size).X > maxW)
            {
                lines.Add(line);
                line = word;
            }
            else line = candidate;
        }

        if (line.Length > 0) lines.Add(line);
        return lines;
    }

    static void Stat(UiPainter p, string label, float frac, float x, float y, float w, Color c)
    {
        p.Text(label, x, y, 13, Pal.TextDim);
        float bx = x + 42f, bw = w - 42f;
        p.Rect(bx, y + 5f, bw, 8f, Pal.PanelHi);
        p.Rect(bx, y + 5f, bw * MathU.Clamp01(frac), 8f, c);
    }

    static string Trim(UiPainter p, string s, float maxW, int size)
    {
        if (p.Measure(s, size).X <= maxW) return s;
        while (s.Length > 1 && p.Measure(s + "...", size).X > maxW) s = s[..^1];
        return s + "...";
    }
}
