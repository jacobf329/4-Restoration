using System.Collections.Generic;
using Godot;

namespace HitboxClone;

/// <summary>
/// Application root: owns the devices, the screen stack and the offline harnesses.
///
/// The node layout is deliberate. 3D cameras render into SubViewports parented to
/// <see cref="ViewportHost"/>, and <see cref="UiRoot"/> draws menus and HUDs over the top. That
/// separation is what lets one to four players each have their own view without the UI layer
/// caring how many there are.
///
/// Command line (after a bare <c>--</c>):
///   <c>--selftest</c>       drive every screen with scripted devices, then run a headless bot
///                           match checking simulation invariants. Exits non-zero on failure.
///   <c>--shots &lt;dir&gt;</c>  pose each screen and write a PNG, for eyeballing UI changes
/// </summary>
public partial class Main : Node
{
    public readonly MatchSettings Settings = new();
    public ScreenStack Stack { get; } = new();

    /// <summary>Parent for the splitscreen SubViewportContainers.</summary>
    public Control ViewportHost { get; private set; } = null!;

    public UiRoot Ui { get; private set; } = null!;

    // --shots state
    string? shotDir;
    readonly List<(string name, UiScreen screen, int settle)> shots = new();

    /// <summary>Scripted devices used to pose captures. Real hardware is never polled during a shot.</summary>
    readonly List<InputDevice> shotDevices = new();
    int shotIndex;
    int shotTimer;

    // --selftest state
    bool selfTestRunning;

    public override void _Ready()
    {
        ViewportHost = GetNode<Control>("Viewports");
        Ui = GetNode<UiRoot>("Ui");

        UserSettings.Load();

        // Skipped in headless: there is no audio device, and the harness has no use for sound.
        bool headless = DisplayServer.GetName().Contains("headless");
        if (!headless) Sfx.Init(this);
        if (!headless) Music.Init(this);

        // Menu backdrops are the one thing in the project that reads a file. A headless run has no
        // rendering server to build a texture on and never draws a menu anyway.
        MenuBackdrop.Enabled = !headless;
        CharacterModels.Enabled = !headless;
        WeaponModels.Enabled = !headless;
        VehicleModels.Enabled = !headless;

        // Both are on by default for a node that overrides them, but the self-test depends on the
        // physics callback specifically and a silent "never called" looks exactly like a hang.
        SetProcess(true);
        SetPhysicsProcess(true);

        var args = OS.GetCmdlineUserArgs();

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] is "--selftest" or "-t")
            {
                int failed = UiSelfTest.Run(this);
                if (failed > 0) { GetTree().Quit(1); return; }

                // The simulation half needs real frames, so it runs across the physics step rather
                // than finishing here.
                selfTestRunning = true;
                MatchSelfTest.Begin(this);
                return;
            }
            if (args[i] == "--shots" && i + 1 < args.Length)
                shotDir = args[i + 1];
        }

        Stack.Push(new TitleScreen(Settings, this));

        if (shotDir != null) BuildShotQueue();
    }

    public override void _ExitTree()
    {
        // Static Godot references have to go before the engine tears down the C# bindings.
        Sfx.Shutdown();
        Music.Shutdown();
        CharacterModels.Shutdown();
        WeaponModels.Shutdown();
        VehicleModels.Shutdown();
    }

    /// <summary>
    /// The simulation half of the self-test, stepped in lockstep with the physics it is testing.
    ///
    /// It used to be pumped from <see cref="_Process"/>, which quietly cost the suite most of its
    /// teeth. A space query — <c>IntersectShape</c>, <c>IntersectRay</c> — is only valid inside the
    /// physics step; outside it Godot refuses the query and hands back an empty result. An
    /// assertion that something is *not* inside geometry therefore passed unconditionally, whether
    /// or not it was, which is worse than having no assertion at all. <c>MoveAndSlide</c> has the
    /// same restriction, so any check that ticks a pawn directly was measuring nothing either.
    ///
    /// Stepping on the physics clock also means <c>elapsed</c> advances at the same rate as
    /// <see cref="Match.Elapsed"/> instead of at whatever frame rate the machine happened to hit.
    /// </summary>
    public override void _PhysicsProcess(double delta)
    {
        if (!selfTestRunning) return;

        if (MatchSelfTest.Step((float)delta, out int failed))
        {
            GetTree().Quit(failed == 0 ? 0 : 1);
            selfTestRunning = false;
        }
    }

    public override void _Process(double delta)
    {
        float dt = (float)delta;

        if (selfTestRunning) return;

        if (shotDir != null) { ProcessShots(); return; }

        Chrome.Tick(dt);
        Music.Tick(dt);
        Devices.PollAll(dt);
        Stack.Update(dt, Devices.All);

        if (Stack.QuitRequested) GetTree().Quit();

        Ui.QueueRedraw();
    }

    // ---- screenshot harness ----

    /// <summary>
    /// Poses each screen in a representative state. The lobby is the interesting one: it is set
    /// up mid-decision, with one player ready, one still choosing, a bot and an empty seat, since
    /// that is the layout most likely to break when the slot drawing changes.
    /// </summary>
    void BuildShotQueue()
    {
        shots.Add(("00-factions", new FactionLineupScreen(this), 30));

        {
            var closeSlots = new LobbySlot[LobbyScreen.MaxPlayers];
            for (int i = 0; i < closeSlots.Length; i++) closeSlots[i] = new LobbySlot { FactionIndex = i };
            closeSlots[0].DeviceId = Devices.Keyboards[0].Id;
            closeSlots[0].ClassIndex = 0;
            for (int i = 1; i < closeSlots.Length; i++) { closeSlots[i].IsBot = true; closeSlots[i].ClassIndex = i; }

            var cSettings = new MatchSettings
            {
                Mode = GameMode.Deathmatch, ArenaIndex = 0, ScoreLimit = 15, BotCount = 3, BotSkill = 0,
            };
            shots.Add(("00b-closeup", new MatchScreen(cSettings, closeSlots, this), 8));
        }
        shots.Add(("01-title", new TitleScreen(Settings, this), 3));
        shots.Add(("02-mode-select", new ModeSelectScreen(Settings, this), 3));
        shots.Add(("02b-controls", new ControlsScreen(), 3));
        shots.Add(("03-devices", new DeviceTestScreen(), 3));
        shots.Add(("03b-options", new OptionsScreen(), 3));
        shots.Add(("03c-bindings", new RebindScreen(), 3));
        shots.Add(("03c-bindings", new RebindScreen(), 3));

        var lobby = new LobbyScreen(Settings, this);
        lobby.Slots[0].DeviceId = Devices.Keyboards[0].Id;
        lobby.Slots[0].ClassIndex = 0;
        lobby.Slots[0].FactionIndex = 0;
        lobby.Slots[0].Ready = true;
        lobby.Slots[1].DeviceId = Devices.Keyboards[1].Id;
        lobby.Slots[1].ClassIndex = 2;
        lobby.Slots[1].FactionIndex = 2;      // mid-choice, so the faction arrows are in the shot
        lobby.Slots[1].Ready = false;
        lobby.Slots[3].IsBot = true;
        lobby.Slots[3].ClassIndex = 3;
        lobby.Slots[3].FactionIndex = 3;
        shots.Add(("04-lobby", lobby, 3));

        // A live two-human match, so the capture exercises the splitscreen layout as well as the
        // HUD. It settles for a few seconds of simulated time first, letting the bots close and
        // start shooting — an empty arena on frame one would show nothing worth reviewing.
        var mSlots = new LobbySlot[LobbyScreen.MaxPlayers];
        for (int i = 0; i < mSlots.Length; i++) mSlots[i] = new LobbySlot { FactionIndex = i };
        mSlots[0].DeviceId = Devices.Keyboards[0].Id;
        mSlots[0].ClassIndex = 0;
        mSlots[1].DeviceId = Devices.Keyboards[1].Id;
        mSlots[1].ClassIndex = 1;
        mSlots[2].IsBot = true; mSlots[2].ClassIndex = 2;
        mSlots[3].IsBot = true; mSlots[3].ClassIndex = 3;
        // Veteran bots on a timed match, settled long enough for kills to reach the feed — the
        // capture is there to review the HUD, and an empty feed reviews nothing.
        var dmSettings = new MatchSettings
        {
            Mode = GameMode.Deathmatch,
            ArenaIndex = 0,
            ScoreLimit = 15,
            TimeLimitSeconds = 300,

            // A full roster, because this is the capture that stands for "what a match looks
            // like" — and what a match looks like is now twelve fighters, not four.
            BotCount = LobbyScreen.MaxFighters - 2,
            BotSkill = 3,
        };
        shots.Add(("05-match-splitscreen", new MatchScreen(dmSettings, mSlots, this), 520));

        // A second live match on another arena in an objective mode, so the capture covers the
        // Glasshouse layout and the King of the Hill zone as well as the default pairing.
        var kothSettings = new MatchSettings
        {
            Mode = GameMode.KingOfTheHill,
            ArenaIndex = 2,   // Glasshouse
            ScoreLimit = 12,
            BotCount = 2,
            BotSkill = 1,
        };
        var kSlots = new LobbySlot[LobbyScreen.MaxPlayers];
        for (int i = 0; i < kSlots.Length; i++) kSlots[i] = new LobbySlot { FactionIndex = i };
        kSlots[0].DeviceId = Devices.Keyboards[0].Id;
        kSlots[0].ClassIndex = 3;
        kSlots[1].IsBot = true; kSlots[1].ClassIndex = 1;
        kSlots[2].IsBot = true; kSlots[2].ClassIndex = 2;
        shots.Add(("06-koth-glasshouse", new MatchScreen(kothSettings, kSlots, this), 260));

        // Coldstore, in Dominion, because the thing worth capturing about this arena is the open
        // ground - and an objective mode is what puts fighters out on it rather than in the base.
        var coldSettings = new MatchSettings
        {
            Mode = GameMode.Dominion,
            ArenaIndex = Arena.ColdstoreLayout,
            ScoreLimit = 50,
            BotCount = 3,
            BotSkill = 2,
        };
        var cdSlots = new LobbySlot[LobbyScreen.MaxPlayers];
        for (int i = 0; i < cdSlots.Length; i++) cdSlots[i] = new LobbySlot { FactionIndex = i };
        cdSlots[0].DeviceId = Devices.Keyboards[0].Id;
        cdSlots[0].ClassIndex = 2;
        cdSlots[1].IsBot = true; cdSlots[1].ClassIndex = 0;
        cdSlots[2].IsBot = true; cdSlots[2].ClassIndex = 3;
        shots.Add(("06b-coldstore", new MatchScreen(coldSettings, cdSlots, this), 300));

        // Team Deathmatch here, so the capture covers team colours as well as the Thousand Rooms
        // layout.
        var gauntlet = new MatchSettings
        {
            Mode = GameMode.TeamDeathmatch,
            ArenaIndex = 3,   // Thousand Rooms
            ScoreLimit = 15,
            BotCount = 3,
            BotSkill = 2,
        };
        var gSlots = new LobbySlot[LobbyScreen.MaxPlayers];
        for (int i = 0; i < gSlots.Length; i++) gSlots[i] = new LobbySlot { FactionIndex = i };
        gSlots[0].DeviceId = Devices.Keyboards[0].Id;
        gSlots[0].ClassIndex = 2;
        for (int i = 1; i < gSlots.Length; i++) { gSlots[i].IsBot = true; gSlots[i].ClassIndex = i; }
        shots.Add(("07-thousand-rooms", new MatchScreen(gauntlet, gSlots, this), 240));

        // Dominion, because its whole HUD — the ticket pools, the post rows, who is bleeding — had
        // never once been looked at. Every other mode's panel was in this queue and the newest one
        // was not, which is exactly the panel most likely to be wrong.
        var conquest = new MatchSettings
        {
            Mode = GameMode.Dominion,
            ArenaIndex = 1,   // Furnace
            ScoreLimit = 100,
            BotCount = 7,
            BotSkill = 2,
        };
        var domSlots = new LobbySlot[LobbyScreen.MaxPlayers];
        for (int i = 0; i < domSlots.Length; i++) domSlots[i] = new LobbySlot { FactionIndex = i };
        domSlots[0].DeviceId = Devices.Keyboards[0].Id;
        for (int i = 1; i < domSlots.Length; i++) { domSlots[i].IsBot = true; domSlots[i].ClassIndex = i; }
        shots.Add(("07b-dominion", new MatchScreen(conquest, domSlots, this), 600));

        // A Marksman holding the trigger down on aim, so the capture shows the scope. Posed with
        // a ScriptedDevice rather than a debug flag on MatchScreen — the input layer already has
        // a device for exactly this, and it keeps the production path free of test hooks.
        var scopeDevice = new ScriptedDevice("shotcam") { HoldAds = true };
        Devices.Register(scopeDevice);
        shotDevices.Add(scopeDevice);

        var scopeSettings = new MatchSettings
        {
            Mode = GameMode.Deathmatch, ArenaIndex = 0, ScoreLimit = 15, BotCount = 3, BotSkill = 1,
        };
        var sSlots = new LobbySlot[LobbyScreen.MaxPlayers];
        for (int i = 0; i < sSlots.Length; i++) sSlots[i] = new LobbySlot { FactionIndex = i };
        sSlots[0].DeviceId = scopeDevice.Id;
        sSlots[0].ClassIndex = 3;                       // Marksman
        for (int i = 1; i < sSlots.Length; i++) { sSlots[i].IsBot = true; sSlots[i].ClassIndex = i - 1; }
        shots.Add(("08-scope", new MatchScreen(scopeSettings, sSlots, this), 200));

        // Driving a tank, throttle down and trigger held, so the capture covers the chase camera,
        // the projected reticle, the reload gauge and — the point of it — whether the camera stays
        // out of the scenery while the hull is moving.
        var tankDevice = new ScriptedDevice("tankcam") { HoldAttack = true, NextMove = new Vector2(0f, -1f) };
        Devices.Register(tankDevice);
        shotDevices.Add(tankDevice);

        var tankSettings = new MatchSettings
        {
            Mode = GameMode.Deathmatch, ArenaIndex = 1, ScoreLimit = 15, BotCount = 3, BotSkill = 1,
        };
        var tSlots = new LobbySlot[LobbyScreen.MaxPlayers];
        for (int i = 0; i < tSlots.Length; i++) tSlots[i] = new LobbySlot { FactionIndex = i };
        tSlots[0].DeviceId = tankDevice.Id;
        tSlots[0].ClassIndex = 0;
        for (int i = 1; i < tSlots.Length; i++) { tSlots[i].IsBot = true; tSlots[i].ClassIndex = i; }
        shots.Add(("08b-tank", new MatchScreen(tankSettings, tSlots, this), 150));

        // A team mode, close up. Two claims only a picture can settle: that the two sides are
        // telling colours apart, and that a flag reads as a flag from across an arena.
        {
            var fSlots = new LobbySlot[LobbyScreen.MaxPlayers];
            for (int i = 0; i < fSlots.Length; i++) fSlots[i] = new LobbySlot { FactionIndex = i };
            fSlots[0].DeviceId = Devices.Keyboards[0].Id;
            fSlots[0].ClassIndex = 0;
            for (int i = 1; i < fSlots.Length; i++) { fSlots[i].IsBot = true; fSlots[i].ClassIndex = i; }

            var fSettings = new MatchSettings
            {
                Mode = GameMode.CaptureTheFlag, ArenaIndex = 0, ScoreLimit = 3,
                BotCount = 3, BotSkill = 1,
            };

            shots.Add(("08d-flag", new MatchScreen(fSettings, fSlots, this), 8));
        }

        // Juggernaut, mid-reign. Two claims only a picture can settle: that the crown reads on the
        // body from across an arena, and that the waypoint finds it when it does not.
        {
            var jSlots = new LobbySlot[LobbyScreen.MaxPlayers];
            for (int i = 0; i < jSlots.Length; i++) jSlots[i] = new LobbySlot { FactionIndex = i };
            jSlots[0].DeviceId = Devices.Keyboards[0].Id;
            jSlots[0].ClassIndex = 0;

            var jSettings = new MatchSettings
            {
                Mode = GameMode.Juggernaut, ArenaIndex = 0, ScoreLimit = 12,
                BotCount = LobbyScreen.MaxFighters - 1, BotSkill = 2,
            };

            shots.Add(("08e-juggernaut", new MatchScreen(jSettings, jSlots, this), 900));
        }

        // One capture per distinctive held weapon, because "the model changes when you pick
        // something up" is a claim only a picture can settle.
        foreach (string held in new[] { "sword", "minigun", "rocket", "portal", "grapple" })
        {
            var wSlots = new LobbySlot[LobbyScreen.MaxPlayers];
            for (int i = 0; i < wSlots.Length; i++) wSlots[i] = new LobbySlot { FactionIndex = i };
            wSlots[0].DeviceId = Devices.Keyboards[0].Id;
            wSlots[0].ClassIndex = 3;
            for (int i = 1; i < wSlots.Length; i++) { wSlots[i].IsBot = true; wSlots[i].ClassIndex = i; }

            var wSettings = new MatchSettings
            {
                Mode = GameMode.Deathmatch, ArenaIndex = 0, ScoreLimit = 15, BotCount = 3, BotSkill = 1,
            };

            shots.Add(($"08c-{held}", new MatchScreen(wSettings, wSlots, this), 90));
        }

        // One overview per arena. These exist so "are the four maps actually different?" is a
        // question I can answer by looking, rather than by trusting the code that built them.
        for (int a = 0; a < Arena.Names.Length; a++)
            shots.Add(($"09-arena-{a}-{Arena.Names[a].ToLowerInvariant()}",
                       new ArenaPreviewScreen(a, this), 12));

        DirAccess.MakeDirRecursiveAbsolute(shotDir);
    }

    void ProcessShots()
    {
        if (shotIndex >= shots.Count)
        {
            GD.Print($"Wrote {shots.Count} screenshots to {shotDir}");
            GetTree().Quit();
            return;
        }

        // Pose on the first tick, let the frame settle, then capture.
        if (shotTimer == 0)
        {
            var s = shots[shotIndex].screen;
            s.Stack = Stack;
            Stack.Reset(s);

            // Put the posed player in a tank when the capture is meant to show driving. Done from
            // the harness through the ordinary public Board call rather than by adding a debug
            // flag to MatchScreen, the same way the scope capture uses a scripted device instead
            // of a test hook on the production screen.
            if (shots[shotIndex].name.Contains("tank") && s is MatchScreen ms)
            {
                GD.Print("  " + VehicleModels.Describe("tank_hull"));
                GD.Print("  " + VehicleModels.Describe("tank_turret"));

                foreach (var rig in ms.Sim.VehicleList)
                    if (rig.Def.Kind == VehicleKind.Tank)
                    {
                        rig.Board(ms.Sim.Pawns[0]);

                        // Traversed well off the hull axis, because "it's in two parts so we can
                        // still turn the gun" is a claim about articulation and a photograph of a
                        // turret pointing dead ahead proves nothing about it.
                        rig.TurretYaw = rig.Facing + 0.9f;
                        rig.TurretPitch = 0.18f;
                        break;
                    }
            }

            // Same idea for the held-weapon captures: hand the posed player a pickup through the
            // ordinary public call, so what gets photographed is the real swap path.
            // Stand a second pawn a few metres in front of the viewing one and point the camera at
            // it. Every other match capture happens to look at scenery, which is no way to check
            // whether the characters are being worn.
            if (shots[shotIndex].name.Contains("closeup") && s is MatchScreen cs)
            {
                var me = cs.Sim.Pawns[0];
                var them = cs.Sim.Pawns[1];

                me.GlobalPosition = cs.Sim.Arena.SpawnPoints[0] + Vector3.Up * 0.2f;
                me.Facing = 0f;
                them.GlobalPosition = me.GlobalPosition + new Vector3(4.2f, 0f, 0f);
                them.Facing = Mathf.Pi;
            }

            // Stood at one flag base looking at it, with one of each side in shot: the flag on the
            // left, a team-mate and an opponent side by side on the right. Whether the two sides
            // read as two sides is the whole question.
            if (shots[shotIndex].name.Contains("flag") && s is MatchScreen fs && fs.Sim.Flags.Count == 2)
            {
                var me = fs.Sim.Pawns[0];
                var mate = fs.Sim.Pawns[2];      // slots alternate teams, so 2 is on mine
                var foe = fs.Sim.Pawns[1];

                var home = fs.Sim.Flags[0].Home;

                me.GlobalPosition = home + new Vector3(-9f, 0.2f, 0f);
                me.Facing = 0f;

                // Stood well short of the flag rather than beside it. The first attempt put them
                // level with the base, which parked the blue player directly in front of a pale
                // blue banner and a blue ground ring — a fair test of nothing.
                mate.GlobalPosition = home + new Vector3(-6.5f, 0f, -1.4f);
                foe.GlobalPosition = home + new Vector3(-6.5f, 0f, 1.8f);
                mate.Facing = foe.Facing = Mathf.Pi;

                fs.Sim.Pawns[3].GlobalPosition = home + new Vector3(0f, 40f, 0f);
            }

            // Matched on the weapon's first word, so a two-word name like "Rocket Launcher" still
            // has a key that can live in a filename.
            if (s is MatchScreen ws)
                foreach (var w in Weapons.Pickups)
                    if (shots[shotIndex].name.Contains(w.Name.ToLowerInvariant().Split(' ')[0]))
                        { ws.Sim.Pawns[0].TakeWeapon(w); break; }

            // A pair of gates in front of the camera, for the one capture that is about them.
            // Planted through the ordinary path so what gets photographed is the real thing.
            if (shots[shotIndex].name.Contains("portal") && s is MatchScreen ps)
            {
                var me = ps.Sim.Pawns[0];
                me.GlobalPosition = ps.Sim.Arena.SpawnPoints[0] + Vector3.Up * 0.2f;
                me.Facing = 0f;

                var ahead = me.GlobalPosition + new Vector3(7f, 0f, 0f);
                ps.Sim.PlantPortalForTest(ahead, Vector3.Up);
                ps.Sim.PlantPortalForTest(ahead + new Vector3(4.5f, 0f, 3.5f), Vector3.Up);
            }

            Ui.QueueRedraw();
        }

        shotTimer++;

        // A posed match keeps simulating while it settles, so the capture shows a real fight.
        //
        // Only the harness's own scripted devices are polled and passed along. Polling real
        // hardware here made captures depend on whatever the keyboard happened to be doing — a
        // key still held from the terminal that launched the game registered as Start and paused
        // the match mid-capture.
        if (Stack.Top is MatchScreen)
        {
            float dt = (float)GetProcessDeltaTime();
            foreach (var d in shotDevices) d.Poll(dt);
            Stack.Update(dt, shotDevices);
        }

        if (shotTimer >= shots[shotIndex].settle)
        {
            var img = GetViewport().GetTexture().GetImage();
            string path = $"{shotDir}/{shots[shotIndex].name}.png";
            img.SavePng(path);
            GD.Print($"  {path}");
            shotIndex++;
            shotTimer = 0;
        }

        Ui.QueueRedraw();
    }
}
