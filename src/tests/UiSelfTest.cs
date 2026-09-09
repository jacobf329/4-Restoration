using System;
using System.Collections.Generic;
using Godot;

namespace HitboxClone;

/// <summary>
/// Drives the real screens with a <see cref="ScriptedDevice"/> and asserts the project's central
/// premise: every screen is reachable and exitable using nav, confirm and back alone. If a screen
/// ever needs a mouse, or swallows back with no way out, this fails.
///
/// It runs the production code path — the same <see cref="InputDevice"/> edge detection, the same
/// <see cref="ScreenStack"/>, the same screen classes. Only the source of the button states is
/// synthetic.
///
/// Drawing is not exercised here: headless has no rendering context. The <c>--shots</c> harness
/// covers the draw path.
/// </summary>
public static class UiSelfTest
{
    const float Dt = 1f / 60f;

    static int failures;
    static int checks;
    static Main app = null!;

    public static int Run(Main main)
    {
        app = main;
        failures = 0;
        checks = 0;

        TestLog.Line("=== HitboxClone UI self-test ===");

        TestMenuNavigation();
        TestBackNeverDeadEnds();
        TestLobbyClaimAndReady();
        TestLobbyBackSemantics();
        TestFourDevicesClaimIndependently();
        TestPadDisconnectFreesSlot();
        TestTriggerAndNavTiming();
        TestKeyboardSchemesDoNotOverlap();
        TestInputDefencesAreReachableAndEffective();
        TestRenderLayersFitTheRoster();
        TestSpawnPointsAreClear();
        TestPadBindings();
        TestEveryClassHasASpecial();
        TestJumpReachesPlatforms();
        TestTeamsAreDistinguishable();
        TestNavigationGraph();
        TestVehiclesAndJetpack();

        TestLog.Line($"=== {checks - failures}/{checks} checks passed ===");
        if (failures > 0) TestLog.Fail($"{failures} check(s) FAILED");
        return failures;
    }

    // ---- harness ----

    sealed class Harness
    {
        public readonly ScreenStack Stack = new();
        public readonly MatchSettings Settings = new();
        public readonly List<InputDevice> Devs = new();
        readonly List<ScriptedDevice> scripted = new();

        public Harness(int deviceCount = 1)
        {
            for (int i = 0; i < deviceCount; i++)
            {
                var d = new ScriptedDevice($"test{i}");
                scripted.Add(d);
                Devs.Add(d);
                Devices.Register(d);
            }
            Stack.Push(new TitleScreen(Settings, app));
        }

        public ScriptedDevice D(int i = 0) => scripted[i];

        public void Dispose()
        {
            foreach (var d in scripted) Devices.Unregister(d);
        }

        /// <summary>One frame: poll every device, then update the stack, exactly as the app does.</summary>
        public void Frame()
        {
            foreach (var d in scripted) d.Poll(Dt);
            Stack.Update(Dt, Devs);
        }

        /// <summary>Hold a button for one frame, then release for one — exactly one press edge.</summary>
        public void Tap(Action<ScriptedDevice> set, int device = 0)
        {
            var d = scripted[device];
            set(d);
            Frame();
            d.Release();
            Frame();
        }

        // Confirm is its own input, not Attack. Menus used to be driven by the attack button while
        // every hint bar in the game said "A", so the prompt was simply lying.
        public void TapConfirm(int device = 0) => Tap(d => d.HoldConfirm = true, device);
        public void TapBack(int device = 0) => Tap(d => d.HoldBack = true, device);
        public void TapStart(int device = 0) => Tap(d => d.HoldStart = true, device);
        public void Nav(int dx, int dy, int device = 0) => Tap(d => { d.HoldNavX = dx; d.HoldNavY = dy; }, device);

        public string TopName => Stack.Top.GetType().Name;

        /// <summary>
        /// Walk the cursor to a row by name and stop on it. False if there is no such row.
        ///
        /// Bounded by a generous frame budget rather than by the item count, because the harness
        /// cannot see the list — it can only see where the cursor is now, which is the same
        /// information a player has.
        /// </summary>
        public bool NavTo(string label, int device = 0)
        {
            for (int i = 0; i < 24; i++)
            {
                if (Stack.Top.SelectedLabel == label) return true;
                Nav(0, 1, device);
            }
            return Stack.Top.SelectedLabel == label;
        }

        /// <summary>Navigate to a row by name and confirm it.</summary>
        public bool Open(string label, int device = 0)
        {
            if (!NavTo(label, device)) return false;
            TapConfirm(device);
            return true;
        }
    }

    static void Check(bool ok, string what)
    {
        checks++;
        if (ok) return;
        failures++;
        TestLog.Fail($"  FAIL: {what}");
    }

    // ---- tests ----

    static void TestMenuNavigation()
    {
        TestLog.Line("- menu navigation");
        var h = new Harness();

        Check(h.TopName == nameof(TitleScreen), "starts on the title screen");

        // Title menu is Play / Controls / Quit. Confirm on the first row opens mode select.
        h.TapConfirm();
        Check(h.TopName == nameof(ModeSelectScreen), "confirm on Play reaches mode select");

        h.TapBack();
        Check(h.TopName == nameof(TitleScreen), "back returns to title");

        // By name rather than by row. The comment above used to say the menu was
        // "Play / Controls / Quit", which had been untrue for a long time and nothing noticed
        // because the test counted rows and the count still happened to work.
        Check(h.Open("Controls"), "the controls screen is reachable by name");
        Check(h.TopName == nameof(ControlsScreen), "and confirming on it opens the controls screen");
        h.TapBack();

        // Fourth row is the device diagnostic.
        Check(h.Open("Devices"), "the device row is reachable");
        Check(h.TopName == nameof(DeviceTestScreen), "the device screen is still reachable");
        h.TapBack();
        h.Nav(0, -1); h.Nav(0, -1); h.Nav(0, -1);

        // Navigating up from the top must wrap rather than stick.
        h.Nav(0, -1);
        Check(h.TopName == nameof(TitleScreen), "wrapping up stays on title");

        h.Dispose();
    }

    static void TestBackNeverDeadEnds()
    {
        TestLog.Line("- back never dead-ends");
        var h = new Harness();

        // Walk to the deepest screen the menus can reach, then back all the way out.
        GoToLobby(h);
        Check(h.TopName == nameof(LobbyScreen), "mode select reaches the lobby");

        h.TapConfirm();                       // claim a slot
        h.TapConfirm();                       // ready up

        // Deliberately stops short of pressing Start. Launching the match builds a live world with
        // viewports and physics bodies, which belongs to MatchSelfTest — this test covers the menu
        // graph, so it only asserts the match is now startable.
        Check(((LobbyScreen)h.Stack.Top).AllClaimedReady, "the match is startable from here");

        int guard = 0;
        while (h.TopName != nameof(TitleScreen) && guard++ < 40) h.TapBack();
        Check(h.TopName == nameof(TitleScreen), "back repeatedly always returns to the title screen");

        // The root must absorb back rather than emptying the stack or quitting.
        h.TapBack();
        Check(h.Stack.Depth == 1, "back at the root leaves the stack intact");
        Check(!h.Stack.QuitRequested, "back at the root does not quit the game");

        h.Dispose();
    }

    static void TestLobbyClaimAndReady()
    {
        TestLog.Line("- lobby claim and ready");
        var h = new Harness();
        var lobby = GoToLobby(h);

        Check(lobby.ClaimedCount == 0, "lobby starts with no claimed slots");

        h.TapConfirm();
        Check(lobby.ClaimedCount == 1, "confirm claims a slot");
        Check(lobby.Slots[0].DeviceId == "test0", "the claiming device owns the leftmost slot");
        Check(!lobby.Slots[0].Ready, "a freshly claimed slot is not ready");

        int before = lobby.Slots[0].ClassIndex;
        h.Nav(1, 0);
        Check(lobby.Slots[0].ClassIndex != before, "left/right changes the class");

        h.TapConfirm();
        Check(lobby.Slots[0].Ready, "confirm readies the slot");
        Check(lobby.AllClaimedReady, "the lobby reports everyone ready");

        // Bots fill the remaining seats from the back.
        Check(lobby.Slots[3].IsBot, "bots fill from the rightmost slot");

        h.Dispose();
    }

    static void TestLobbyBackSemantics()
    {
        TestLog.Line("- lobby back semantics");
        var h = new Harness();
        var lobby = GoToLobby(h);

        h.TapConfirm();          // claim
        h.TapConfirm();          // ready
        Check(lobby.Slots[0].Ready, "slot is ready before backing out");

        h.TapBack();
        Check(h.TopName == nameof(LobbyScreen), "back from a ready slot stays in the lobby");
        Check(!lobby.Slots[0].Ready, "back un-readies first");

        h.TapBack();
        Check(h.TopName == nameof(LobbyScreen), "back from a claimed slot stays in the lobby");
        Check(lobby.ClaimedCount == 0, "back again leaves the slot");

        h.TapBack();
        Check(h.TopName == nameof(ModeSelectScreen), "back with no slot leaves the lobby");

        h.Dispose();
    }

    static void TestFourDevicesClaimIndependently()
    {
        TestLog.Line("- four devices claim independently");
        var h = new Harness(4);
        var lobby = GoToLobby(h);

        for (int i = 0; i < 4; i++) h.TapConfirm(i);
        Check(lobby.ClaimedCount == 4, "four devices claim four slots");

        for (int i = 0; i < 4; i++)
            Check(lobby.Slots[i].DeviceId == $"test{i}", $"device test{i} owns slot {i}");

        // Each pad drives only its own slot — this is what Godot's single-focus Control system
        // could not express, and the reason for the custom UI layer.
        int c1 = lobby.Slots[1].ClassIndex;
        int c2 = lobby.Slots[2].ClassIndex;
        h.Nav(1, 0, device: 1);
        Check(lobby.Slots[1].ClassIndex != c1, "device 1 changed its own class");
        Check(lobby.Slots[2].ClassIndex == c2, "device 1 did not disturb slot 2");

        // Faction is the other axis, and it is per-slot the same way class is.
        //
        // It used to be assigned from the seat: player one was always the Vessels, whatever they
        // wanted. That was tolerable while faction was a colour and a model, and stopped being so
        // the moment faction started deciding which special you get.
        // Four seats, four different peoples out of the box. Checked before anything touches the
        // axis, since the whole point of the next two lines is to move one of them.
        var seen = new HashSet<int>();
        for (int i = 0; i < 4; i++) seen.Add(lobby.Slots[i].FactionIndex);
        Check(seen.Count == 4, "four players default to four different factions");

        int f1 = lobby.Slots[1].FactionIndex;
        int f2 = lobby.Slots[2].FactionIndex;
        h.Nav(0, 1, device: 1);
        Check(lobby.Slots[1].FactionIndex != f1, "up/down picks the faction");
        Check(lobby.Slots[2].FactionIndex == f2, "and only for the pad that pressed it");

        Check(lobby.Slots[1].Faction.SpecialBlurb.Length > 0,
              "every faction says what its special does, not just what it is called");

        // One player readying must not ready anyone else.
        h.TapConfirm(0);
        Check(lobby.Slots[0].Ready, "device 0 readied");
        Check(!lobby.Slots[1].Ready, "device 1 is still choosing");
        Check(!lobby.AllClaimedReady, "the match cannot start while one player is choosing");

        // A readied player is locked in — neither axis moves any more.
        int lockedClass = lobby.Slots[0].ClassIndex;
        int lockedFaction = lobby.Slots[0].FactionIndex;
        h.Nav(1, 0, device: 0);
        h.Nav(0, 1, device: 0);
        Check(lobby.Slots[0].ClassIndex == lockedClass, "a readied slot cannot change class");
        Check(lobby.Slots[0].FactionIndex == lockedFaction, "or faction");

        h.Dispose();
    }

    static void TestPadDisconnectFreesSlot()
    {
        TestLog.Line("- disconnect frees the slot");
        var h = new Harness(2);
        var lobby = GoToLobby(h);

        h.TapConfirm(0);
        h.TapConfirm(1);
        Check(lobby.ClaimedCount == 2, "two devices claimed");

        // Yank device 1 out of the registry, the same way a real pad vanishing looks to the lobby.
        Devices.Unregister(h.Devs[1]);
        h.Frame();

        Check(lobby.ClaimedCount == 1, "the vanished device's slot is freed");
        Check(lobby.Slots[0].DeviceId == "test0", "the surviving player keeps their slot");

        Devices.Register(h.Devs[1]);
        h.Dispose();
    }

    static void TestTriggerAndNavTiming()
    {
        TestLog.Line("- nav repeat timing");
        var d = new ScriptedDevice("timing");

        // Holding a direction fires once, then waits out the first delay before repeating.
        d.HoldNavY = 1;
        d.Poll(Dt);
        Check(d.NavY == 1, "the first frame of a held direction fires immediately");

        d.Poll(Dt);
        Check(d.NavY == 0, "the very next frame does not repeat");

        int repeats = 0;
        for (int i = 0; i < 60; i++) { d.Poll(Dt); if (d.NavY != 0) repeats++; }
        Check(repeats >= 1, "holding eventually repeats");
        Check(repeats <= 10, $"repeat rate is bounded (saw {repeats} in one second)");

        d.Release();
        d.Poll(Dt);
        Check(d.NavY == 0, "releasing stops the repeat");

        // A press edge must fire exactly once for a held button.
        d.HoldAttack = true;
        d.Poll(Dt);
        Check(d.AttackPressed, "attack press edge fires");
        d.Poll(Dt);
        Check(!d.AttackPressed && d.AttackHeld, "a held attack does not re-fire the edge");
    }

    /// <summary>
    /// Two people sharing one keyboard must never share a key. An overlap means one keypress
    /// drives two players at once, which is exactly the class of bug this guards against — the
    /// schemes originally both used Shift.
    /// </summary>
    static void TestKeyboardSchemesDoNotOverlap()
    {
        TestLog.Line("- keyboard schemes do not overlap");

        var s0 = new HashSet<Key>();
        foreach (var group in KeyboardDevice.Schemes[0].All)
            foreach (var k in group) s0.Add(k);

        var s1 = new HashSet<Key>();
        foreach (var group in KeyboardDevice.Schemes[1].All)
            foreach (var k in group) s1.Add(k);

        Check(s0.Count > 0 && s1.Count > 0, "both schemes declare bindings");

        foreach (var k in s1)
            Check(!s0.Contains(k), $"key {k} is bound in both keyboard schemes");

        // Bare modifiers are what remappers such as Steam Input emit for pad buttons when a
        // controller is running a desktop layout, so no scheme may depend on them.
        foreach (var k in new[] { Key.Ctrl, Key.Alt, Key.Meta })
        {
            Check(!s0.Contains(k), $"scheme 0 avoids the bare {k} modifier");
            Check(!s1.Contains(k), $"scheme 1 avoids the bare {k} modifier");
        }
    }

    /// <summary>
    /// The two anti-translation switches must be reachable with a pad alone and must actually
    /// change behaviour. A defence buried behind a mouse-only settings dialog would be useless in
    /// precisely the situation it exists for.
    /// </summary>
    static void TestInputDefencesAreReachableAndEffective()
    {
        TestLog.Line("- input defences reachable and effective");

        UserSettings.Load();
        bool original = UserSettings.KeyboardAndMouse;

        // Reachable: title -> Options is two nav steps and a confirm, no mouse involved.
        var h = new Harness();
        Check(h.Open("Options"), "the options row is reachable with a pad alone");
        Check(h.TopName == nameof(OptionsScreen), "options is reachable with a pad alone");

        bool before = UserSettings.KeyboardAndMouse;
        h.Nav(1, 0);
        Check(UserSettings.KeyboardAndMouse != before, "left/right toggles keyboard & mouse players");

        h.TapBack();
        Check(h.TopName == nameof(TitleScreen), "options backs out to the title");
        h.Dispose();

        // Effective: with the switch off, neither keyboard scheme is a player and the mouse is
        // inert — whatever the OS is synthesising from a translated pad.
        UserSettings.KeyboardAndMouse = false;
        Check(!Devices.CanClaimSlot(Devices.Keyboards[0]), "WASD cannot claim a slot while off");
        Check(!Devices.CanClaimSlot(Devices.Keyboards[1]), "arrows cannot claim a slot while off");

        var kb = new KeyboardDevice(0);
        Check(!kb.MouseInUse, "mouse is inert while keyboard & mouse is off");
        Check(!kb.UseMouseAim, "mouse aim is inert while keyboard & mouse is off");

        // Gamepads are never gated by it.
        Check(Devices.CanClaimSlot(Devices.Gamepads[0]) || !Devices.Gamepads[0].Connected,
              "gamepads are not gated by the keyboard switch");

        UserSettings.KeyboardAndMouse = true;
        Check(Devices.CanClaimSlot(Devices.Keyboards[0]), "WASD can claim a slot when switched on");
        Check(Devices.CanClaimSlot(Devices.Keyboards[1]), "arrows can claim a slot when switched on");

        // Restore and re-persist. Toggling a row through the real menu calls Save(), so without
        // this the test would leave the player's settings file holding whatever it flipped to.
        UserSettings.KeyboardAndMouse = original;
        UserSettings.Save();
    }

    /// <summary>
    /// No spawn may drop a player inside a block. This is cheap to get wrong when the layout
    /// moves — an earlier pass slid the spawns inward and landed them on the corner pillars, so
    /// players began each life embedded in cover, looking at its inside face.
    /// </summary>
    /// <summary>
    /// Med kits have to be reachable from wherever the fight is, not merely numerous.
    ///
    /// "More health pickups" is easy to satisfy badly — sixteen of them clustered in one district
    /// is the same problem as three. So this measures the thing that actually matters: it walks a
    /// grid over the standable parts of the arena and asks, from each of them, how far the nearest
    /// med kit is. The worst answer on the map is the number under test.
    ///
    /// Health used to be every fourth weapon crate, which gave three med kits on a 278x206m arena.
    /// </summary>
    static void CheckHealthIsSpreadOut(Arena arena)
    {
        Check(arena.HealthSpawns.Count >= 10,
              $"{arena.Name} carries a real number of med kits ({arena.HealthSpawns.Count})");

        foreach (var h in arena.HealthSpawns)
        {
            Check(arena.Contains(h), $"{arena.Name} med kit {h} is in bounds");
            Check(!arena.IsOverPit(h), $"{arena.Name} med kit {h} is not over a pit");
            Check(arena.IsClearOfBlocks(h, Pawn.Radius, Pawn.Height),
                  $"{arena.Name} med kit {h} is reachable");
        }

        // The worst walk to health, over every standable sample on the floor.
        float worst = 0f;
        var worstAt = Vector3.Zero;
        int samples = 0;

        for (float z = -Arena.HalfDepth + 8f; z <= Arena.HalfDepth - 8f; z += 12f)
        for (float x = -Arena.HalfWidth + 8f; x <= Arena.HalfWidth - 8f; x += 12f)
        {
            var at = new Vector3(x, 1f, z);
            if (!arena.IsClearOfBlocks(new Vector3(x, 0.6f, z), Pawn.Radius, Pawn.Height)) continue;
            if (arena.IsOverPit(at)) continue;

            samples++;
            float nearest = float.MaxValue;

            foreach (var h in arena.HealthSpawns)
                nearest = MathF.Min(nearest, new Vector2(h.X - x, h.Z - z).Length());

            if (nearest > worst) { worst = nearest; worstAt = at; }
        }

        TestLog.Line($"    {arena.Name}: {arena.HealthSpawns.Count} med kits, "
                     + $"furthest standable point is {worst:0}m from one ({samples} samples)");

        // Forty metres is about six seconds at a walk. Anything past that and being hurt in that
        // corner means leaving the fight entirely rather than making a decision about it.
        Check(samples > 0, $"{arena.Name} has standable floor to sample");
        Check(worst < 40f, $"{arena.Name}: nowhere is further than 40m from a med kit "
                           + $"(worst {worst:0}m at {worstAt})");
    }

    /// <summary>
    /// The render layers a roster of twelve depends on.
    ///
    /// This is the check that would have caught the old cap. Body layers were <c>1 &lt;&lt; (slot+1)</c>
    /// and view models <c>1 &lt;&lt; (slot+5)</c>, so a fifth fighter's body would have landed on the
    /// first player's view model — their rifle and someone else's torso sharing a bit. Nothing in
    /// the suite said so, because nothing ever built a fifth pawn.
    /// </summary>
    static void TestRenderLayersFitTheRoster()
    {
        TestLog.Line("- render layers fit a full roster");

        var used = new Dictionary<uint, string>();

        void Claim(uint layer, string who)
        {
            Check(layer != 0u, $"{who} has a layer");
            Check(!used.ContainsKey(layer),
                  $"{who} does not share a layer with {(used.TryGetValue(layer, out var other) ? other : who)}");
            used[layer] = who;
        }

        // Layer 1 is the shared world. Nothing may take it.
        used[1u] = "world geometry";

        for (int view = 0; view < LobbyScreen.MaxPlayers; view++)
        {
            Claim(Pawn.VisualLayerFor(view), $"P{view + 1} body");
            Claim(Pawn.ViewModelLayerFor(view), $"P{view + 1} view model");
        }

        Claim(Pawn.BotBodyLayer, "every bot's body");

        // Every bot really does share, rather than each wanting one.
        Check(Pawn.VisualLayerFor(-1) == Pawn.BotBodyLayer, "a bot uses the shared body layer");
        Check(Pawn.ViewModelLayerFor(-1) == 0u, "and has no view model layer at all");

        // Godot exposes twenty. Everything claimed has to fit inside them.
        foreach (var layer in used.Keys)
            Check(layer < 1u << 20, $"layer {layer} is within Godot's twenty");

        // And the culling still does its job: a player sees bots, sees other players, sees their
        // own weapon, and never their own body or anyone else's weapon.
        for (int view = 0; view < LobbyScreen.MaxPlayers; view++)
        {
            uint mask = Pawn.FirstPersonCullMask(view);

            Check((mask & Pawn.VisualLayerFor(view)) == 0, $"P{view + 1} cannot see their own body");
            Check((mask & Pawn.ViewModelLayerFor(view)) != 0, $"P{view + 1} can see their own weapon");
            Check((mask & Pawn.BotBodyLayer) != 0, $"P{view + 1} can see bots");
            Check((mask & 1u) != 0, $"P{view + 1} can see the arena");

            for (int other = 0; other < LobbyScreen.MaxPlayers; other++)
            {
                if (other == view) continue;
                Check((mask & Pawn.VisualLayerFor(other)) != 0, $"P{view + 1} can see P{other + 1}");
                Check((mask & Pawn.ViewModelLayerFor(other)) == 0,
                      $"P{view + 1} cannot see P{other + 1}'s weapon");
            }
        }

        Check(LobbyScreen.MaxFighters > LobbyScreen.MaxPlayers,
              "the roster is bigger than the number of seats");
    }

    static void TestSpawnPointsAreClear()
    {
        TestLog.Line("- spawn points are clear of cover");

        for (int layout = 0; layout < Arena.Names.Length; layout++)
        {
            // Only combat arenas. Puzzle chambers are checked in CheckPortalMode and story sets
            // have no fight on them at all; every invariant below is about a map that does.
            if (!Arena.IsArena(layout)) continue;

            var arena = new Arena(layout);
            Check(arena.SpawnPoints.Count >= LobbyScreen.MaxPlayers,
                  $"{arena.Name} has a spawn for every player");
            Check(arena.ZoneSpots.Count > 0, $"{arena.Name} declares King of the Hill zones");

            // Negative control: the check has to be capable of failing. Outside the arena there
            // is nowhere to walk, so a confined point really does report as confined — without
            // this, "every spawn is connected" could be a function that always says yes.
            Check(!arena.IsConnected(new Vector3(Arena.HalfWidth + 40f, 1f, 0f)),
                  $"{arena.Name}: the connectivity check can tell when there is nowhere to go");

            foreach (var sp in arena.SpawnPoints)
            {
                Check(arena.Contains(sp), $"{arena.Name} spawn {sp} is inside the arena");
                Check(arena.IsClearOfBlocks(sp, Pawn.Radius + 0.25f, Pawn.Height),
                      $"{arena.Name} spawn {sp} is clear of cover");
            }

            // A zone centred inside a block, or hanging over a pit, would be uncapturable.
            foreach (var z in arena.ZoneSpots)
            {
                Check(arena.Contains(z), $"{arena.Name} zone {z} is inside the arena");
                Check(arena.IsClearOfBlocks(z, Pawn.Radius + 0.25f, Pawn.Height),
                      $"{arena.Name} zone {z} is standable");
                Check(!arena.IsOverPit(z), $"{arena.Name} zone {z} is not over a pit");
            }

            foreach (var sp in arena.SpawnPoints)
            {
                Check(!arena.IsOverPit(sp), $"{arena.Name} spawn {sp} is not over a pit");

                // The one that matters most, and the one that was missing.
                //
                // A player reported spawning in a room with no exit. Every other check here asks
                // whether a spawn is somewhere legal to stand; none of them asked whether you can
                // leave. Being sealed in is worse than any unfair fight, because it is not a fight.
                int room = arena.ReachableFrom(sp);
                Check(arena.IsConnected(sp),
                      $"{arena.Name} spawn {sp} can walk out (reached {room} cells)");
            }

            // Launch pads and platform endpoints have to be inside the world too, or they fling
            // players into a wall or out of the arena.
            foreach (var pad in arena.LaunchPads)
            {
                Check(arena.Contains(pad.Centre), $"{arena.Name} launch pad {pad.Centre} is in bounds");
                Check(!arena.IsOverPit(pad.Centre), $"{arena.Name} launch pad {pad.Centre} is not over a pit");
                Check(pad.Impulse > 0f, $"{arena.Name} launch pad actually launches");

                // The one that matters. A pad under a skybridge is not a route up, it is a way to
                // hit your head — and there is no feedback telling you which kind you stepped on,
                // because both of them look like a glowing plate on the floor.
                //
                // Measured against the pad's own arc rather than a fixed number, because a pad
                // that carries fifteen metres and has fourteen is a different bug from one that
                // carries fifteen and has two.
                float room = arena.PadHeadroom(pad.Centre);
                float apex = Arena.PadApex(pad.Impulse);

                Check(room >= apex,
                      $"{arena.Name} launch pad {pad.Centre} has sky above it "
                      + $"({(room == float.MaxValue ? "open" : room.ToString("0.0"))}m clear, throws {apex:0.0}m)");
            }

            TestLog.Line($"    {arena.Name}: {arena.RoomsBuilt} interior chambers, "
                     + $"{arena.Hazards.Count} hazards, {arena.Pits.Count} pits");

            Check(arena.RoomsBuilt >= 6,
                  $"{arena.Name} has real interiors ({arena.RoomsBuilt} chambers)");

            CheckHealthIsSpreadOut(arena);

            // Enough spawns for a full roster, spread far enough apart that arriving is not an
            // ambush. Four corner decks was exactly the old roster; twelve fighters on four spawns
            // is three people materialising on the same deck.
            TestLog.Line($"    {arena.Name}: {arena.SpawnPoints.Count} spawn points");

            Check(arena.SpawnPoints.Count >= LobbyScreen.MaxFighters,
                  $"{arena.Name} has a spawn for every fighter ({arena.SpawnPoints.Count})");

            float tightest = float.MaxValue;
            for (int a = 0; a < arena.SpawnPoints.Count; a++)
            for (int b = a + 1; b < arena.SpawnPoints.Count; b++)
            {
                var pa = arena.SpawnPoints[a];
                var pb = arena.SpawnPoints[b];
                tightest = MathF.Min(tightest, new Vector2(pa.X - pb.X, pa.Z - pb.Z).Length());
            }

            Check(tightest > 18f,
                  $"{arena.Name}: no two spawns are on top of each other (closest {tightest:0}m)");

            // Two flag bases, standable, on opposite sides, and far enough apart that carrying a
            // flag between them is a journey rather than a step.
            Check(arena.FlagBases.Count == 2, $"{arena.Name} has two flag bases");

            if (arena.FlagBases.Count == 2)
            {
                var a = arena.FlagBases[0];
                var b = arena.FlagBases[1];

                foreach (var at in arena.FlagBases)
                {
                    Check(arena.Contains(at), $"{arena.Name} flag base {at} is in bounds");
                    Check(!arena.IsOverPit(at), $"{arena.Name} flag base {at} is not over a pit");

                    // Room to fight over, not merely room to stand.
                    Check(arena.IsClearOfBlocks(at with { Y = at.Y - 0.4f }, 2.6f, 2.2f),
                          $"{arena.Name} flag base {at} has room around it");
                }

                float apart = new Vector2(a.X - b.X, a.Z - b.Z).Length();
                TestLog.Line($"    {arena.Name}: flag bases {apart:0}m apart");

                Check(apart > 70f, $"{arena.Name} flag bases are a real run apart ({apart:0}m)");
                Check(a.X * b.X < 0f, $"{arena.Name} flag bases are on opposite sides of the map");
            }

            // Weapon spawns have to be somewhere a player can actually stand.
            foreach (var ws in arena.WeaponSpawns)
            {
                Check(arena.Contains(ws), $"{arena.Name} weapon spawn {ws} is in bounds");
                Check(!arena.IsOverPit(ws), $"{arena.Name} weapon spawn {ws} is not over a pit");
                Check(arena.IsClearOfBlocks(ws, Pawn.Radius, Pawn.Height),
                      $"{arena.Name} weapon spawn {ws} is reachable");
            }

            // A hazard must be somewhere a player can be pushed into, and must be survivable —
            // an instant-kill hazard is a pit with extra steps.
            foreach (var hz in arena.Hazards)
            {
                var c = hz.Area.GetCenter();
                Check(arena.Contains(new Vector3(c.X, 1f, c.Y)),
                      $"{arena.Name} hazard at {c} is in bounds");
                Check(hz.DamagePerSecond > 0f, $"{arena.Name} hazard actually hurts");
                Check(hz.DamagePerSecond < 100f,
                      $"{arena.Name} hazard is survivable rather than an instant kill");
            }

            foreach (var mp in arena.MovingPlatforms)
            {
                Check(arena.Contains(mp.A) && arena.Contains(mp.B),
                      $"{arena.Name} moving platform stays in bounds");
                Check(mp.Period > 0f, $"{arena.Name} moving platform has a period");
            }
        }
    }

    /// <summary>
    /// Rebinding must never be able to strand a player. Every action keeps at least one binding,
    /// and an input taken by a new action is removed from whatever held it before.
    /// </summary>
    static void TestPadBindings()
    {
        TestLog.Line("- pad bindings stay escapable");

        PadBindings.ResetAll();
        Check(PadBindings.AllDefault(), "reset restores every default");

        foreach (var a in PadBindings.Actions)
            Check(PadBindings.For(a).Count > 0, $"{a} starts with a binding");

        // No default input may mean two things at once. This is checked over the *defaults* rather
        // than over whatever the player has since rebound, because a stock pad is the only layout
        // the game ships with an opinion about.
        var claimed = new Dictionary<string, PadAction>();

        foreach (var a in PadBindings.Actions)
            foreach (var b in PadBindings.DefaultsFor(a))
            {
                string key = b.Serialise();
                Check(!claimed.ContainsKey(key),
                      $"{key} is bound once, not to both {a} and {(claimed.TryGetValue(key, out var other) ? other : a)}");
                claimed[key] = a;
            }

        // The face buttons a shooter player reaches for, in the places they reach for them.
        Check(PadBindings.DefaultsFor(PadAction.SwapWeapon)[0].Equals(new PadBinding(JoyButton.Y)),
              "swap weapon is on Y, where a shooter player expects it");

        // Melee goes on the stick click, not a face button. You melee whenever someone closes the
        // distance, and that is exactly the moment you cannot afford to take a thumb off the aim
        // stick — which is why every shooter that has settled the question puts it here.
        Check(PadBindings.DefaultsFor(PadAction.Melee)[0].Equals(new PadBinding(JoyButton.RightStick)),
              "melee is on the right stick click, reachable without leaving the aim stick");

        Check(PadBindings.DefaultsFor(PadAction.Use)[0].Equals(new PadBinding(JoyButton.X)),
              "interact is on X");

        // Only Start opens the menu. Y used to be a second Back binding, so it opened the pause
        // menu mid-fight — which is exactly the button that now swaps weapons.
        foreach (var b in PadBindings.DefaultsFor(PadAction.Back))
            Check(!b.Equals(new PadBinding(JoyButton.Y)), "Y no longer backs out of anything");

        // Menus confirm on Jump, which is A. This is the pairing every hint bar in the game
        // already claimed while the code was reading the right trigger.
        Check(PadBindings.DefaultsFor(PadAction.Jump)[0].Equals(new PadBinding(JoyButton.A)),
              "jump — and therefore menu confirm — is on A");

        Check(Glyphs.For(Prompt.Confirm, PadKind.Xbox) == "A",
              "and the confirm prompt says so");

        // Menus leave on B, the way every console shooter does it. Derived from the Crouch binding
        // rather than named, which is what gets the label right on all three pad vocabularies:
        // Nintendo's east button is labelled A, and east is where cancel belongs on all of them.
        Check(PadBindings.DefaultsFor(PadAction.Crouch)[0].Equals(new PadBinding(JoyButton.B)),
              "crouch — and therefore menu back — is on B");

        Check(Glyphs.For(Prompt.Cancel, PadKind.Xbox) == "B", "the cancel prompt says B on Xbox");
        Check(Glyphs.For(Prompt.Cancel, PadKind.PlayStation) == "Circle", "Circle on PlayStation");
        Check(Glyphs.For(Prompt.Cancel, PadKind.Nintendo) == "A", "and A on a Nintendo pad");

        // The prompts are derived from the bindings rather than written out a second time, so a
        // rebind has to move the label with it. This is the drift that put "X" on every Special
        // prompt after Special moved to the left bumper.
        PadBindings.Rebind(PadAction.Jump, new PadBinding(JoyButton.RightShoulder));
        Check(Glyphs.For(Prompt.Confirm, PadKind.Xbox) == "RB",
              "rebinding confirm moves the prompt with it");
        PadBindings.ResetAll();

        Check(Glyphs.For(Prompt.Special, PadKind.Xbox)
                  == PadBindings.DefaultsFor(PadAction.Special)[0].Label(PadKind.Xbox),
              "the special prompt names the button special is actually on");

        Check(Glyphs.For(Prompt.Melee, PadKind.Xbox) == "RS", "the melee prompt names the stick click");
        Check(Glyphs.For(Prompt.Swap, PadKind.Xbox) == "Y", "the swap prompt names Y");

        Check(PadBindings.DefaultsFor(PadAction.Start).Count > 0, "Start is bound");

        // The d-pad carries exactly one action: the class ability, on Up.
        //
        // It used to carry none, and that was the right rule while there was nothing that needed a
        // seat. A second ability had nowhere else to go — every trigger, bumper, face button and
        // stick click was taken — so the rule is now "one, deliberately, and Up" rather than
        // "none", which is still a rule that catches something creeping back onto the other three.
        Check(PadBindings.DefaultsFor(PadAction.ClassAbility)[0].Equals(new PadBinding(JoyButton.DpadUp)),
              "the class ability is on d-pad up");

        foreach (var a in PadBindings.Actions)
            foreach (var b in PadBindings.DefaultsFor(a))
                foreach (var pad in new[] { JoyButton.DpadUp, JoyButton.DpadDown,
                                            JoyButton.DpadLeft, JoyButton.DpadRight })
                {
                    if (a == PadAction.ClassAbility && pad == JoyButton.DpadUp) continue;
                    Check(!b.Equals(new PadBinding(pad)), $"{a} is not on the d-pad");
                }

        // Rebinding is reachable and takes effect.
        PadBindings.Rebind(PadAction.Attack, new PadBinding(JoyButton.Y));
        Check(PadBindings.For(PadAction.Attack).Count == 1, "rebinding replaces the binding list");
        Check(PadBindings.For(PadAction.Attack)[0].Equals(new PadBinding(JoyButton.Y)),
              "the captured input is what gets bound");
        Check(!PadBindings.IsDefault(PadAction.Attack), "a rebound action reports as changed");

        // Y was Back's alternate. It must have been taken away, and Back must still be usable.
        foreach (var b in PadBindings.For(PadAction.Back))
            Check(!b.Equals(new PadBinding(JoyButton.Y)), "the input is removed from its old action");
        Check(PadBindings.For(PadAction.Back).Count > 0, "Back still has a binding after losing one");

        // Steal every input Back has, one at a time; it must never end up with nothing.
        foreach (var steal in new[] { JoyButton.Back, JoyButton.Y, JoyButton.Start, JoyButton.A })
        {
            PadBindings.Rebind(PadAction.Dash, new PadBinding(steal));
            Check(PadBindings.For(PadAction.Back).Count > 0,
                  $"Back survives losing {steal}");
            Check(PadBindings.For(PadAction.Start).Count > 0,
                  $"Start survives losing {steal}");
        }

        // Round-trips through the settings file unchanged.
        PadBindings.ResetAll();
        PadBindings.Rebind(PadAction.Dash, new PadBinding(JoyButton.LeftStick));
        var cfg = new ConfigFile();
        PadBindings.WriteTo(cfg);

        PadBindings.ResetAll();
        PadBindings.ReadFrom(cfg);
        Check(PadBindings.For(PadAction.Dash)[0].Equals(new PadBinding(JoyButton.LeftStick)),
              "bindings survive a save and load");

        // A corrupt entry falls back to the default rather than leaving an action unbound.
        var bad = new ConfigFile();
        bad.SetValue("bindings", PadAction.Attack.ToString(), "garbage:nonsense");
        PadBindings.ReadFrom(bad);
        Check(PadBindings.For(PadAction.Attack).Count > 0, "a corrupt binding falls back to a default");

        PadBindings.ResetAll();
    }

    /// <summary>
    /// The special button is bound on every pad, shown in the rebinding screen and listed in the
    /// lobby, so every class has to actually do something with it. It was inert for a long time
    /// precisely because nothing checked.
    /// </summary>
    static void TestEveryClassHasASpecial()
    {
        TestLog.Line("- every class has a working special");

        foreach (var c in Classes.All)
        {
            Check(!string.IsNullOrWhiteSpace(c.SpecialName), $"{c.Name} names its special");
            Check(!string.IsNullOrWhiteSpace(c.SpecialBlurb), $"{c.Name} explains its special");
            Check(c.SpecialCooldown > 0f, $"{c.Name} special has a cooldown");

            // Timed abilities must be shorter than their own cooldown, or they would be permanent.
            if (c.SpecialDuration > 0f)
                Check(c.SpecialDuration < c.SpecialCooldown,
                      $"{c.Name} special cannot be held up permanently");

            // Blast abilities need a blast to deliver.
            if (c.Special is SpecialKind.Frag or SpecialKind.Shockwave)
            {
                Check(c.BlastDamage > 0f, $"{c.Name} blast does damage");
                Check(c.BlastRadius > 0f, $"{c.Name} blast has a radius");
            }
        }

        // Four classes, four different abilities, and none of them a faction's.
        //
        // These four spent weeks defined, named, blurbed, tuned and completely unreachable: the
        // special moved to the faction and the class kept the data. Every check above passed the
        // whole time, because every one of them tested the *definition* rather than whether
        // anything could fire it. That is the exact failure this project keeps rediscovering.
        var classKinds = new HashSet<SpecialKind>();
        foreach (var c in Classes.All) classKinds.Add(c.Special);

        Check(classKinds.Count == Classes.All.Length,
              "every class has an ability no other class has");

        foreach (var f in Factions.All)
            Check(!classKinds.Contains(f.Special),
                  $"no class ability duplicates {f.Name}'s special");

        // Every pickup weapon must be usable and distinctly coloured, since colour is how you tell
        // one crate from another across the arena.
        // Keyed by colour and *checked against the weapon that claimed it*, rather than a set that
        // rejects any repeat. The table deliberately lists the portal gun three times, and a plain
        // uniqueness check reads that as the portal gun clashing with itself — which is not a
        // thing that can confuse anybody looking at two crates. What matters is that two different
        // weapons never share a colour.
        var tints = new Dictionary<string, string>();
        foreach (var w in Weapons.Pickups)
        {
            Check(w.Ammo > 0, $"{w.Name} has ammo");
            Check(w.FireInterval > 0f, $"{w.Name} has a fire rate");

            // A pickup has to do *something* when you pull the trigger. Damage is the usual answer
            // and no longer the only one: the portal gun and the grapple both do none.
            Check(w.Damage > 0f || w.BlastDamage > 0f || w.PlantsPortal || w.Grapples,
                  $"{w.Name} does something when fired");
            Check(w.Range > 0f, $"{w.Name} has reach");

            // Pickups lie on the floor as the gun itself now, so a weapon with no model named is
            // the one crate in the arena still shaped like a box. That is a content gap rather
            // than a crash, and it is invisible from anywhere except standing next to it - which
            // is exactly the kind of thing that reaches release.
            Check(w.Model.Length > 0, $"{w.Name} names a model to lie on the floor as");
            string tint = Weapons.TintFor(w).ToHtml();
            if (tints.TryGetValue(tint, out string? claimed))
                Check(claimed == w.Name, $"{w.Name} has its own colour, not {claimed}'s");
            else
                tints[tint] = w.Name;
        }

        // A dash has to hurt enough to matter without being a one-shot.
        Check(Match.DashDamage > 0f && Match.DashDamage < 60f,
              "a dash slam hurts but does not delete");

        // All four kinds are represented, so no ability path goes untested by the match harness.
        var seen = new HashSet<SpecialKind>();
        foreach (var c in Classes.All) seen.Add(c.Special);
        Check(seen.Count == Classes.All.Length, "every class has a distinct special");
    }

    /// <summary>
    /// The jump has to actually clear the step heights the arenas are built from, or the whole
    /// platforming premise fails quietly — the geometry would simply be scenery.
    /// </summary>
    static void TestJumpReachesPlatforms()
    {
        TestLog.Line("- jump clears the platform steps");

        // Projectile apex: v^2 / 2g.
        float apex = Pawn.JumpVelocity * Pawn.JumpVelocity / (2f * Pawn.Gravity);
        float airtime = 2f * Pawn.JumpVelocity / Pawn.Gravity;

        TestLog.Line($"    apex {apex:0.00}m, airtime {airtime:0.00}s");

        Check(apex > 3f, $"jump apex {apex:0.00}m clears a three-metre step");
        Check(airtime > 1.2f, $"airtime {airtime:0.00}s is floaty enough to steer in");

        // Ramps climb in even increments, so any single step must be inside the jump height too —
        // otherwise a player who misses the ramp cannot recover by jumping.
        Check(apex > 2.5f, "a missed ramp step can be jumped back onto");

        foreach (var pad in new Arena(0).LaunchPads)
            Check(pad.Impulse > Pawn.JumpVelocity,
                  "a launch pad throws you higher than your own jump");
    }

    /// <summary>
    /// Team assignment has to be consistent and the two team colours have to be clearly apart.
    /// Telling friend from foe is the entire job of those colours, and four per-slot player
    /// colours previously made a team mode unreadable.
    /// </summary>
    static void TestTeamsAreDistinguishable()
    {
        TestLog.Line("- teams are readable");

        Check(Match.TeamOf(0) == Match.TeamOf(2), "slots 0 and 2 share a team");
        Check(Match.TeamOf(1) == Match.TeamOf(3), "slots 1 and 3 share a team");
        Check(Match.TeamOf(0) != Match.TeamOf(1), "adjacent slots are opponents");

        var a = Pal.Teams[0];
        var b = Pal.Teams[1];

        // Separated in hue rather than only in brightness, so the pair survives a greyscale test.
        float dr = MathF.Abs(a.R - b.R), dg = MathF.Abs(a.G - b.G), db = MathF.Abs(a.B - b.B);
        Check(dr + dg + db > 0.9f, "team colours are far apart in RGB");

        float lumA = a.R * 0.299f + a.G * 0.587f + a.B * 0.114f;
        float lumB = b.R * 0.299f + b.G * 0.587f + b.B * 0.114f;
        Check(MathF.Abs(lumA - lumB) > 0.05f, "team colours also differ in brightness");

        Check(Pal.TeamName(0) != Pal.TeamName(1), "teams are named distinctly");
    }

    /// <summary>
    /// The walkable graph has to actually connect the arena. A graph that builds but leaves the
    /// far side unreachable would look fine and quietly strand every bot behind a pit.
    /// </summary>
    static void TestNavigationGraph()
    {
        TestLog.Line("- navigation graph connects the arenas");

        for (int layout = 0; layout < Arena.Names.Length; layout++)
        {
            // Only combat arenas. Puzzle chambers are checked in CheckPortalMode and story sets
            // have no fight on them at all; every invariant below is about a map that does.
            if (!Arena.IsArena(layout)) continue;

            var arena = new Arena(layout);
            var nav = new NavGraph(arena);

            TestLog.Line($"    {arena.Name}: {nav.NodeCount} nodes");
            Check(nav.NodeCount > 200, $"{arena.Name} graph has a usable number of nodes");

            // Every spawn must reach every other spawn, or a bot can start a match already stuck.
            foreach (var a in arena.SpawnPoints)
            foreach (var b in arena.SpawnPoints)
            {
                if (a == b) continue;
                Check(nav.AreConnected(a, b), $"{arena.Name}: spawn {a} reaches spawn {b}");
            }

            // Everything worth walking to has to be walkable to.
            foreach (var w in arena.WeaponSpawns)
                Check(nav.AreConnected(arena.SpawnPoints[0], w),
                      $"{arena.Name}: weapon spawn {w} is reachable");

            foreach (var z in arena.ZoneSpots)
                Check(nav.AreConnected(arena.SpawnPoints[0], z),
                      $"{arena.Name}: capture zone {z} is reachable");

            // A route must never be laid across a hole.
            var route = new List<Vector3>();
            bool found = nav.TryFindPath(arena.SpawnPoints[0], arena.SpawnPoints[1], route);
            Check(found && route.Count > 1, $"{arena.Name}: a corner-to-corner route exists");

            foreach (var wp in route)
                Check(!arena.IsOverPit(wp), $"{arena.Name}: route avoids pits at {wp}");
        }
    }

    /// <summary>
    /// Vehicles and the jetpack both add ways to leave the ground, and both can be got wrong in
    /// ways that only show up in play. These are the invariants that can be checked without one.
    /// </summary>
    /// <summary>
    /// Picking something up has to change what is in your hands.
    ///
    /// The view model used to be built once from the class and never rebuilt, so a minigun and a
    /// sword looked exactly like the rifle you started with. A silhouette is only worth having if
    /// the weapons actually differ, so this checks they do.
    /// </summary>
    static void TestWeaponsLookDifferent()
    {
        TestLog.Line("- weapons look like what they are");

        var seen = new Dictionary<WeaponSilhouette, string>();

        foreach (var w in Weapons.Pickups)
        {
            TestLog.Line($"    {w.Name}: {w.Silhouette}");

            // Two pickups sharing a silhouette is allowed, but the sword must not look like a gun
            // and the minigun must not look like a rifle — those are the reads that matter.
            if (w == Weapons.Sword)
                Check(w.Silhouette == WeaponSilhouette.Blade, "the sword is held as a blade");

            if (w == Weapons.Minigun)
                Check(w.Silhouette == WeaponSilhouette.Minigun, "the minigun has its own shape");

            seen[w.Silhouette] = w.Name;
        }

        Check(seen.Count >= 3, "the pickups are not all the same shape");

        // And a class weapon has to differ from the pickups it will be swapped for, or picking one
        // up looks like nothing happened.
        foreach (var c in Classes.All)
            Check(c.Weapon.Silhouette == c.Silhouette,
                  $"{c.Name} carries its own declared silhouette");

        Check(Classes.Marksman.Silhouette != Classes.Tactician.Silhouette,
              "a sniper and a shotgun are held differently");
    }

    /// <summary>
    /// Each mode's score limit has to mean something in that mode's own units.
    ///
    /// King of the Hill scores one point per second held, so the shared 5-to-50 scale offered
    /// fifteen seconds of holding as a default match — over before anyone had crossed the arena to
    /// contest it. Elimination counts rounds, where a step of five is a jump from a short match to
    /// an interminable one.
    /// </summary>

    /// <summary>
    /// The campaign's state machine: act order, the one choice, and her.
    ///
    /// Worth testing before a single mission exists, because every one of them will be written
    /// against these rules and a mission cannot notice that the rule it relied on was never
    /// enforced. The sequencing bug this is really guarding is silent by nature: an Undecided
    /// harvest leaking into the later acts crashes nothing and simply plays the neutral version of
    /// every scene from there on, which is the kind of thing that gets shipped.
    /// </summary>

    /// <summary>
    /// Fairview: a town, and specifically *not* an arena.
    ///
    /// The checks that matter here are the absences. Every other map in the game is scored on what
    /// it has on it; this one is only right if it has none of that, and an absence is exactly the
    /// kind of property that quietly stops holding when somebody adds a pass to the constructor
    /// and does not think about the third kind of map.
    /// </summary>

    /// <summary>
    /// The script, and the shape of the acts it is written into.
    ///
    /// Content checks rather than prose criticism: that every act has a scene, that the branch
    /// filter does what it claims, and that the two written acts are actually written. The last one
    /// matters because an empty scene is a legal state - four of the six are deliberately empty
    /// today - so "the script loaded" is not evidence that anything is in it.
    /// </summary>

    /// <summary>
    /// The environment material library, and the fallback that lets it be empty.
    ///
    /// The fallback is the part under test. Eight materials will arrive one at a time, and the
    /// whole arrangement is worth nothing if the game looks broken while seven of them are still
    /// missing — so "no files on disk" has to be an ordinary, working state rather than the state
    /// nobody tried.
    /// </summary>

    /// <summary>
    /// Act I as a scene rather than as text: walk somewhere, hear something, walk on.
    ///
    /// Driven by moving a pawn rather than by calling the mission's own methods, because the thing
    /// most likely to be wrong is not the state machine - it is whether the stages are anywhere
    /// near the town. A stage written eight metres from the school door is a scene that plays to an
    /// empty street, and only walking to it finds that out.
    /// </summary>

    /// <summary>
    /// The needler: nearly nothing per needle, and everything at seven.
    ///
    /// The checks are all about the threshold, because the threshold is the weapon. A needler
    /// whose individual needles are competitive is a homing SMG that also explodes, and the
    /// interesting decision - keep pouring into one target while everything says switch - only
    /// exists while a single needle is beneath notice.
    /// </summary>

    /// <summary>
    /// Two rules that used to be one number each, and a convention that used to be a measurement.
    ///
    /// The headshot half is about what a scope buys. The muzzle half is about a heuristic that was
    /// wrong on purpose - see WeaponModels.MuzzleDirection - and the check that matters now is
    /// that a weapon cannot get a facing without somebody having decided on one.
    /// </summary>
    static void TestHeadshotsAndMuzzles()
    {
        TestLog.Line("- a scope is what a full headshot costs");

        Check(Match.UnscopedHeadshotMultiplier < Match.HeadshotMultiplier,
              $"a headshot is worth less without a scope "
              + $"({Match.UnscopedHeadshotMultiplier:0.#}x against {Match.HeadshotMultiplier:0.#}x)");

        Check(Match.UnscopedHeadshotMultiplier > 1f,
              "and still worth more than a body shot, or nobody would aim high at all");

        // The shot a scope exists for still has to be the one that ends somebody, or the trade -
        // your field of view, your pace, your awareness of what is beside you - buys nothing.
        float toughest = 0f;
        foreach (var c in Classes.All) toughest = MathF.Max(toughest, c.Health);

        foreach (var w in Weapons.Pickups)
        {
            if (!w.HasScope) continue;
            float head = w.Damage * Match.HeadshotMultiplier;
            TestLog.Line($"    {w.Name}: {head:0} to the head, toughest class has {toughest:0}");
            Check(head >= toughest, $"a {w.Name} headshot drops anybody in the game");
        }

        // And the unscoped rate is exactly half, which is the whole of the rule. An earlier
        // version of this check asserted that six unscoped minigun headshots should not drop the
        // toughest class; they do, by nine points, and that was an opinion invented here rather
        // than a rule anybody set. Six headshots in a row is a third of a second of sustained fire
        // held on a head, and a kill is a fair price for it.
        Check(MathF.Abs(Match.UnscopedHeadshotMultiplier - Match.HeadshotMultiplier * 0.5f) < 0.01f,
              "an unscoped headshot is worth exactly half a scoped one");

        // Every pickup that carries a model has a decided facing rather than a measured one.
        // MuzzleFlip defaulting false is the shared convention; this is only here so that adding a
        // weapon which needs the override cannot silently skip it by never being looked at.
        int flipped = 0;
        foreach (var w in Weapons.Pickups) if (w.MuzzleFlip) flipped++;

        TestLog.Line($"    {flipped} of {Weapons.Pickups.Length} pickup entries override the "
                 + $"muzzle convention");
        Check(flipped < Weapons.Pickups.Length,
              "the muzzle convention is a convention, not a list of exceptions");
    }


    static void TestNeedler()
    {
        TestLog.Line("- the needler is worth nothing until it is worth everything");

        var n = Weapons.Needler;

        Check(n.Needles, "needles stick");
        Check(n.Seeks, "and steer");
        Check(n.SeekTurnRate < Weapons.Seeker.SeekTurnRate * 2f,
              "gently, rather than as an aim button");
        Check(n.SeekConeDeg < 45f, "and only at what you were roughly pointing at");

        // A single needle has to be beneath notice, or there is no reason to commit to one target.
        Check(n.Damage < Weapons.Minigun.Damage,
              $"one needle is beneath notice ({n.Damage:0.#} against the minigun's {Weapons.Minigun.Damage:0.#})");

        // And the payoff has to be worth the commitment, which means beating what the same time
        // spent on a rocket would have done.
        float burst = n.Damage * Match.SupercombineNeedles + Match.SupercombineDamage;
        Check(burst > Weapons.RocketLauncher.BlastDamage,
              $"seven of them beat a rocket ({burst:0} against {Weapons.RocketLauncher.BlastDamage:0})");

        // Reachable inside the window it has to be reached in, or the threshold is decoration.
        float toFire = n.FireInterval * (Match.SupercombineNeedles - 1);
        TestLog.Line($"    seven needles take {toFire:0.00}s to fire, window is {Match.SupercombineWindow:0.0}s");
        Check(toFire < Match.SupercombineWindow,
              "seven needles can be fired inside the window they have to land in");

        Check(n.Ammo > Match.SupercombineNeedles * 4,
              "and the magazine holds several attempts");

        // The counting itself. A bare pawn, never added to the tree: AddNeedle touches two fields
        // and nothing else, and standing up a match to count to seven would be a strange way to
        // find out whether an integer increments.
        var pawn = new Pawn();
        pawn.ClearNeedles();

        for (int i = 1; i < Match.SupercombineNeedles; i++)
            Check(!pawn.AddNeedle(Match.SupercombineNeedles, Match.SupercombineWindow),
                  $"needle {i} does not set them off");

        Check(pawn.AddNeedle(Match.SupercombineNeedles, Match.SupercombineWindow),
              $"needle {Match.SupercombineNeedles} does");
        Check(pawn.Needles == 0, "and the count resets rather than chaining");

        pawn.ClearNeedles();
        Check(pawn.Needles == 0, "needles can be cleared off a pawn");

        pawn.Free();
    }


    static void TestChildhoodMission()
    {
        TestLog.Line("- Act I plays as a walk through Fairview");

        var state = new CampaignState();
        var mission = Missions.Childhood(state);

        Check(mission.StageCount >= 4, $"the walk has somewhere to go ({mission.StageCount} stages)");
        Check(!mission.Complete, "and is not over before it starts");

        // No combat HUD over a childhood. A health bar, three cooldown gauges and a tactical
        // minimap say what Fairview is well before the act has finished not saying it.
        Check(!mission.ShowsCombatHud, "and no health bar over a walk to the shops");

        // The settings a scene runs under: the town, alone, nothing to win.
        var settings = Missions.SettingsFor(Act.Childhood);
        Check(settings.IsStoryMission, "a scene knows it is a scene");
        Check(settings.BotCount == 0, "and is played alone");
        Check(Arena.IsStory(Match.ChooseArenaForTest(settings)),
              "and it lands in Fairview, which nothing else may do");

        var arena = new Arena(Arena.CombatLayouts);

        // Every place the scene sends the player has to be somewhere they can stand, inside the
        // town. This is the check that catches a stage drifting off the map.
        var seen = new List<Vector3>();
        var walker = new CampaignState();
        var probe = Missions.Childhood(walker);

        for (int guard = 0; guard < 200 && !probe.Complete; guard++)
        {
            if (probe.TargetForTest is { } target)
            {
                Check(arena.InPlay(target with { Y = 2f }),
                      $"stage target ({target.X:0}, {target.Z:0}) is inside the town");
                seen.Add(target);

                // Standing on it is what advances the scene; the harness cannot walk, so it
                // teleports and then lets the timer run the dialogue out.
                probe.StepForTest(1f / 60f, target);
            }
            else
            {
                // Talking. Stepped by more than the longest a line can hold, so one call is one
                // beat - at a realistic dt the twenty-seven lines of Act I are thousands of frames
                // and the loop's guard would run out long before the scene did, which is what
                // happened the first time this was written.
                probe.StepForTest(8f, Vector3.Zero);
            }
        }

        Check(probe.Complete, "the scene reaches its end");
        Check(seen.Count >= 3, $"and sends the player to several places on the way ({seen.Count})");

        // The places are actually apart. A walk whose stops are all in one spot is a cutscene.
        float furthest = 0f;
        foreach (var a in seen)
        foreach (var b in seen)
            furthest = MathF.Max(furthest, a.DistanceTo(b));

        TestLog.Line($"    the walk spans {furthest:0}m of Fairview");
        Check(furthest > 40f, $"the walk crosses the town ({furthest:0}m)");
    }



    /// <summary>
    /// Act II: the four cases, in any order, and a decision made by standing somewhere.
    ///
    /// The thing worth testing here is not the dialogue, it is the claim the act makes about
    /// itself — that all four delegations can be heard in whichever order the player finds them,
    /// that the floor does not open until they have been, and that walking onto one of them is
    /// what answers the question. Every one of those is a way for the only choice in the game to
    /// become unreachable, and an unreachable choice is a campaign that cannot be finished.
    /// </summary>
    static void TestHarvestMission()
    {
        TestLog.Line("- Act II is answered by walking, not by a menu");

        var settings = Missions.SettingsFor(Act.Harvest);
        Check(settings.IsStoryMission, "the hearing is a scene");
        Check(settings.BotCount == 0, "and nobody else is in the room");
        Check(Match.ChooseArenaForTest(settings) == Arena.ConvocationLayout,
              "and it lands in the Convocation rather than the town");
        Check(Arena.ConvocationLayout != Arena.FairviewLayout,
              "which is a different set from Act I's");

        var arena = new Arena(Arena.ConvocationLayout);

        // Each bay has to be somewhere a person can actually stand, and far enough from the next
        // one that standing in one is not standing in two. That second check is the one that
        // matters: overlapping bays would let a player answer the question by accident, from the
        // middle of the room, without ever choosing anybody.
        for (int i = 0; i < 4; i++)
        {
            var seat = Arena.ConvocationSeat(i);
            Check(arena.InPlay(seat with { Y = 2f }),
                  $"the {Arena.ConvocationHost(i).Name} bay is inside the room");

            for (int j = i + 1; j < 4; j++)
                Check(seat.DistanceTo(Arena.ConvocationSeat(j)) > Arena.ConvocationBay * 2f,
                      $"bay {i} and bay {j} do not overlap");
        }

        // Hear them in an order nobody would design for, to prove the act does not secretly want
        // one. The Vessels last is the interesting case - they are the ones who raised him, and a
        // scene written as a sequence would have put them first.
        var state = new CampaignState();
        var mission = Missions.Harvest(state);

        Check(!mission.OpenForTest, "the floor is shut before anybody has been heard");

        int[] order = { 2, 0, 3, 1 };
        var middle = new Vector3(0f, 0f, 0f);

        for (int n = 0; n < order.Length; n++)
        {
            // Walk in, then let the case run out. Stepped by more than the longest a line can
            // hold, so one call is one beat.
            for (int guard = 0; guard < 60 && mission.HeardForTest <= n; guard++)
                mission.StepForTest(8f, Arena.ConvocationSeat(order[n]));

            Check(mission.HeardForTest == n + 1,
                  $"{Arena.ConvocationHost(order[n]).Name} made their case ({mission.HeardForTest} heard)");

            // Back to the middle, or the next step would be measured against the bay he is still
            // standing in.
            if (n < order.Length - 1) mission.StepForTest(0.01f, middle);
        }

        // The closing run has to play itself out before the floor opens.
        for (int guard = 0; guard < 40 && !mission.OpenForTest; guard++)
            mission.StepForTest(8f, middle);

        Check(mission.OpenForTest, "and the floor opens once all four have spoken");
        Check(state.Choice == HarvestChoice.Undecided, "with nothing decided yet");

        // Standing in the middle decides nothing, however long he stands there. This is the check
        // that a scene which merely waits long enough cannot answer for him.
        for (int i = 0; i < 20; i++) mission.StepForTest(1f, middle);
        Check(state.Choice == HarvestChoice.Undecided,
              "waiting in the middle of the room is not an answer");

        // Step onto a floor, but not for long enough, and then step off.
        mission.StepForTest(ChamberMission.CommitSeconds * 0.5f, Arena.ConvocationSeat(3));
        Check(mission.DwellForTest > 0.3f, "standing with somebody starts to count");
        Check(state.Choice == HarvestChoice.Undecided, "but half a hold is not a decision");

        mission.StepForTest(0.5f, middle);
        Check(mission.DwellForTest < 1f, "and stepping away gives it back");

        // Then commit properly. The Vessels, so the answer is Waited and the delegation is theirs
        // rather than the Custodians' - the two share an answer and must not share an identity.
        for (int guard = 0; guard < 40 && state.Choice == HarvestChoice.Undecided; guard++)
            mission.StepForTest(1f, Arena.ConvocationSeat(3));

        Check(state.Choice == HarvestChoice.Waited, "standing with the Vessels is an answer");
        Check(state.Sided == Delegation.Vessels, "and the room remembers whose floor he stood on");

        for (int guard = 0; guard < 40 && !mission.Complete; guard++)
            mission.StepForTest(8f, Arena.ConvocationSeat(3));
        Check(mission.Complete, "and the act ends after they have answered him back");

        // The campaign was blocked on this and now is not. Act II being unfinishable was the
        // state the game was actually in before this scene existed.
        Check(state.Advance(), "the campaign can leave Act II once the question is answered");

        // Every bay is reachable and the two answers are both reachable. Run each in its own
        // campaign, because the question is asked once and a state that has answered it cannot
        // answer it again - which is itself the thing being checked.
        var answers = new HarvestChoice[4];
        var sides = new Delegation[4];

        for (int bay = 0; bay < 4; bay++)
        {
            var run = new CampaignState();
            var m = Missions.Harvest(run);

            for (int guard = 0; guard < 400 && !m.Complete; guard++)
                m.StepForTest(m.OpenForTest ? 1f : 8f,
                              m.HeardForTest >= 4 ? Arena.ConvocationSeat(bay)
                                                  : Arena.ConvocationSeat(m.HeardForTest));

            answers[bay] = run.Choice;
            sides[bay] = run.Sided;

            Check(run.Choice != HarvestChoice.Undecided,
                  $"siding with {Arena.ConvocationHost(bay).Name} answers the question");
            TestLog.Line($"    {Arena.ConvocationHost(bay).Name}: {run.Choice}, sided {run.Sided}");
        }

        Check(answers[0] == HarvestChoice.Harvested && answers[1] == HarvestChoice.Harvested,
              "the Garden and Ingenuity both take him");
        Check(answers[2] == HarvestChoice.Waited && answers[3] == HarvestChoice.Waited,
              "the Custodians and the Vessels both let him wait");

        // Four bays, four identities. Two of them agree about the harvest and none of them are
        // each other, which is the whole reason Sided is a separate fact from Choice.
        for (int i = 0; i < 4; i++)
        for (int j = i + 1; j < 4; j++)
            Check(sides[i] != sides[j], $"bay {i} and bay {j} are told apart");

        // And it survives the game closing. A choice this size that a restart forgets would be
        // worse than no choice at all.
        var saved = new CampaignState();
        saved.Decide(HarvestChoice.Harvested);
        saved.SideWith(Delegation.Ingenuity);
        saved.Save();

        var reloaded = CampaignState.Load();
        Check(reloaded.Choice == HarvestChoice.Harvested, "the answer survives a restart");
        Check(reloaded.Sided == Delegation.Ingenuity, "and so does whose floor he was standing on");

        // The room asks once. A second answer would be the game changing its mind about something
        // the player already lived through.
        reloaded.SideWith(Delegation.Garden);
        Check(reloaded.Sided == Delegation.Ingenuity, "and it cannot be answered twice");
    }

    /// <summary>
    /// The scope lock: help onto a target, then get out of the way.
    ///
    /// Tested as the relationships between the numbers rather than by flying a camera around,
    /// because what went wrong last time was not arithmetic — it was a design that applied its
    /// pull every frame, forever, whoever was in the cone. Each check below is one sentence of
    /// that design written so it cannot quietly stop being true.
    /// </summary>
    static void TestScopeLock()
    {
        TestLog.Line("- the scope hands you a target and then lets go");

        Check(MatchScreen.ScopeBreakConeForTest > MatchScreen.ScopeAcquireConeForTest,
              "a lock is harder to lose than it was to get");

        Check(MatchScreen.ScopeSnapRateForTest > MatchScreen.ScopeTrackForTest * 4f,
              "the swing onto a target is far faster than the following afterwards");

        // The snap has to cover the whole acquire cone inside its own time budget, or the scope
        // comes up, starts turning, and hands over still pointing somewhere in between.
        float covered = MatchScreen.ScopeSnapRateForTest * MatchScreen.ScopeSnapTimeForTest;
        TestLog.Line($"    the snap covers {covered:0.00} rad, cone is "
                   + $"{MatchScreen.ScopeAcquireConeForTest:0.00} rad");
        Check(covered > MatchScreen.ScopeAcquireConeForTest * 2f,
              $"the snap finishes the swing it starts ({covered:0.00} rad)");

        // The override has to be a deadzone rather than a threshold somebody has to lean on.
        Check(MatchScreen.ScopeOverrideForTest > 0f && MatchScreen.ScopeOverrideForTest < 0.25f,
              $"a light touch is enough to take over ({MatchScreen.ScopeOverrideForTest:0.00})");

        // And the lock must actually end. This is the check that would have failed against the
        // version being replaced, which held on for as long as the scope was up.
        Check(MatchScreen.ScopeGripForTest(0f) >= 0.99f, "the lock is at full strength when taken");
        Check(MatchScreen.ScopeGripForTest(MatchScreen.ScopeHoldFullForTest * 0.5f) >= 0.99f,
              "and stays there while you settle");

        float ends = MatchScreen.ScopeHoldFullForTest + MatchScreen.ScopeHoldFadeForTest;
        Check(MatchScreen.ScopeGripForTest(ends + 0.01f) <= 0f,
              $"and is gone by {ends:0.0}s however still your hands are");

        // Monotone, so the fade is a fade rather than a shape somebody has to reason about.
        float last = 2f;
        for (float t = 0f; t <= ends + 0.5f; t += 0.05f)
        {
            float g = MatchScreen.ScopeGripForTest(t);
            if (g > last + 0.001f) { Check(false, $"the lock never strengthens again (at {t:0.00}s)"); break; }
            last = g;
        }
        Check(last <= 0f, "and the fade only ever runs one way");

        TestLog.Line($"    full for {MatchScreen.ScopeHoldFullForTest:0.0}s, "
                   + $"gone by {ends:0.0}s, override at {MatchScreen.ScopeOverrideForTest:0.00} stick");
    }

    /// <summary>
    /// The dressing: every arena carries some, none of it can be touched, and it stays in budget.
    ///
    /// The middle one is the check worth having. Decoration that quietly acquired collision would
    /// be a gameplay change nobody asked for and nobody would look for — a crate you can hide
    /// behind is cover, and cover is balance. So this asserts the separation directly rather than
    /// trusting that nobody adds a body to the render pass later.
    /// </summary>
    static void TestDressing()
    {
        TestLog.Line("- the arenas are dressed, and none of it is solid");

        for (int layout = 0; layout < Arena.Names.Length; layout++)
        {
            var arena = new Arena(layout);
            int props = arena.Decorations.Count;

            Check(props <= Arena.DecorBudget,
                  $"{Arena.Names[layout]} stays inside the dressing budget ({props})");

            // Two kinds of map are deliberately bare, and the test says so rather than being
            // relaxed to accommodate them.
            //
            // A puzzle chamber is about the portal and the gap; litter in a void would be scenery
            // for a place that is not one. The Convocation is bare for a stronger reason: it is a
            // sealed room four civilisations built to be right in, containing nothing but the man
            // they are arguing about, and a stack of barrels in the corner would undercut the one
            // thing the whole act is doing.
            if (Arena.IsPuzzle(layout) || layout == Arena.ConvocationLayout)
            {
                Check(props == 0, $"{Arena.Names[layout]} is meant to be bare and is");
                continue;
            }

            Check(props > 0, $"{Arena.Names[layout]} has something in it ({props} pieces)");
            TestLog.Line($"    {Arena.Names[layout]}: {arena.Blocks.Count} blocks, {props} props");

            // Every piece has real size and sits inside the map.
            foreach (var d in arena.Decorations)
            {
                Check(d.HalfExtents.X > 0f && d.HalfExtents.Y > 0f && d.HalfExtents.Z > 0f,
                      "a prop has size");
                Check(arena.Contains(d.Centre), "a prop is inside the arena");
            }
        }

        // Every combat arena actually reaches the material library, which it did not for as long
        // as its blocks all defaulted to plating. Counted through the same derivation the renderer
        // uses, so this cannot pass while the screen stays grey.
        var lit = new Arena(0);
        var used = new System.Collections.Generic.HashSet<SurfaceKind> { Arena.GroundSurface };
        foreach (var b in lit.Blocks) used.Add(Arena.MaterialForTest(b, false));
        foreach (var b in lit.Blocks) used.Add(Arena.MaterialForTest(b, true));

        {
            var tally = new System.Collections.Generic.Dictionary<SurfaceKind, int>();
            foreach (var b in lit.Blocks)
            {
                bool outerBlock = MathF.Abs(b.Centre.X) > 40f || MathF.Abs(b.Centre.Z) > 40f;
                var kind = Arena.MaterialForTest(b, outerBlock);
                tally[kind] = tally.GetValueOrDefault(kind) + 1;
            }

            var parts = new System.Collections.Generic.List<string>();
            foreach (var (kind, n) in tally) parts.Add($"{n} {kind}");
            TestLog.Line($"    Reliquary is built from {string.Join(", ", parts)}");
        }

        Check(used.Count >= 4, $"an arena is built from several materials ({used.Count})");
        Check(used.Contains(SurfaceKind.Concrete) && used.Contains(SurfaceKind.Brick),
              "including the two the walls are meant to be");

        // An explicit choice by a builder survives everything, including being made breakable -
        // which rebuilds the block, and used to drop the material while doing it.
        var town = new Arena(Arena.FairviewLayout);
        var chosen = new System.Collections.Generic.HashSet<SurfaceKind>();
        foreach (var b in town.Blocks) chosen.Add(Arena.MaterialForTest(b, false));

        Check(chosen.Contains(SurfaceKind.Plaster) && chosen.Contains(SurfaceKind.RoofTile),
              "a builder's own materials are never overridden");

        // The separation, stated as a test. Dressing is invisible to every query the simulation
        // makes, so a point standing in the middle of a barrel is still clear ground.
        var dressed = new Arena(0);
        Check(dressed.Decorations.Count > 0, "there is something to stand in");

        int solid = 0;
        foreach (var d in dressed.Decorations)
        {
            // Only test props sitting on open floor; one against a wall is inside the wall's
            // clearance and would fail for a reason that has nothing to do with the prop.
            if (!dressed.IsClearOfBlocks(d.Centre with { Y = 0.1f }, 0.9f, 1.8f)) continue;
            if (!dressed.IsClearOfBlocks(d.Centre with { Y = 0.1f }, 0.4f, 1.8f)) solid++;
        }

        Check(solid == 0, $"no piece of dressing is solid ({solid} were)");

        // And the same arena dresses identically twice, or players cannot learn a map.
        var again = new Arena(0);
        Check(again.Decorations.Count == dressed.Decorations.Count,
              "an arena dresses the same way every time");

        bool same = true;
        for (int i = 0; i < again.Decorations.Count && same; i++)
            same = again.Decorations[i].Centre.IsEqualApprox(dressed.Decorations[i].Centre);
        Check(same, "down to where every piece of it is");
    }

    static void TestSurfaces()
    {
        TestLog.Line("- surfaces fall back to plating when there is no art");

        Surfaces.ClearCacheForTest();

        foreach (SurfaceKind kind in System.Enum.GetValues<SurfaceKind>())
        {
            var spec = Surfaces.SpecFor(kind);

            Check(spec.Metres > 0.1f && spec.Metres < 12f,
                  $"{kind} tiles at a believable size ({spec.Metres:0.0}m)");

            // Every kind but the default names a file stem, and the default names none - it is
            // the procedural plating and there is nothing to look for.
            if (kind == SurfaceKind.Panel)
                Check(spec.Name.Length == 0, "the panel kind is procedural and asks for no files");
            else
                Check(spec.Name.Length > 0, $"{kind} knows what its files are called");

            // Asking for a material with nothing on disk must be quiet and must be cached, or a
            // missing texture becomes a file probe per block per frame.
            var set = Surfaces.For(kind);
            Check(set != null, $"{kind} resolves to a set");
            Check(ReferenceEquals(set, Surfaces.For(kind)), $"{kind} is cached, hit or miss");
        }

        // Two kinds must not share a stem, or one material silently becomes another.
        var stems = new HashSet<string>();
        foreach (SurfaceKind kind in System.Enum.GetValues<SurfaceKind>())
        {
            string name = Surfaces.SpecFor(kind).Name;
            if (name.Length == 0) continue;
            Check(stems.Add(name), $"{kind} has its own files, not {name} again");
        }

        // And the town says what it is made of, which is the only reason any of this exists yet.
        var town = new Arena(Arena.CombatLayouts);
        var used = new HashSet<SurfaceKind>();
        foreach (var b in town.Blocks) used.Add(b.Surface);

        TestLog.Line($"    Fairview is built from {used.Count} materials");
        Check(used.Count >= 4, $"Fairview is made of several materials, not one ({used.Count})");
        Check(used.Contains(SurfaceKind.Plaster), "its houses are rendered");
        Check(used.Contains(SurfaceKind.RoofTile), "and roofed");
        Check(used.Contains(SurfaceKind.Tarmac), "and it has a road");

        // Arenas are untouched: they were built before materials existed and still ask for none.
        var arena = new Arena(0);
        bool allPanel = true;
        foreach (var b in arena.Blocks) if (b.Surface != SurfaceKind.Panel) allPanel = false;
        Check(allPanel, "the arenas still ask for the plating they were built with");
    }


    static void TestStoryScript()
    {
        TestLog.Line("- the story script is wired to the acts");

        Check(Scripts.All.Length == Acts.All.Length, "every act has a scene");

        foreach (var scene in Scripts.All)
        {
            Check(Scripts.For(scene.Act) == scene, $"{scene.Name} is reachable by its act");
            Check(scene.Name.Length > 0, $"act {scene.Act} names its scene");
        }

        // The two that are written.
        Check(Scripts.Childhood.Beats.Length > 15,
              $"Act I is written ({Scripts.Childhood.Beats.Length} beats)");
        Check(Scripts.Harvest.Beats.Length > 10,
              $"Act II is written ({Scripts.Harvest.Beats.Length} beats)");

        // All four make their case at the harvest, or the scene is not the scene.
        var heard = new HashSet<Speaker>();
        foreach (var b in Scripts.Harvest.Beats) heard.Add(b.Who);

        foreach (var who in new[] { Speaker.Vessels, Speaker.Garden,
                                    Speaker.Custodians, Speaker.Ingenuity })
            Check(heard.Contains(who), $"{Scripts.NameOf(who)} argue their case at the harvest");

        foreach (var scene in Scripts.All)
        foreach (var b in scene.Beats)
        {
            Check(b.Line.Length > 0, $"every beat in {scene.Name} says something");

            // Somebody owns every line, including the narrator, and every speaker has a colour.
            // A beat drawn in the default tint is a speaker somebody forgot to add.
            Check(Scripts.TintOf(b.Who) != default, $"{scene.Name}: {b.Who} has a colour");
        }

        // The Fairview cast wear the Vessels' own colour from the first line, before John is told
        // anything. That is the one clue in the presentation layer and it should not rot.
        foreach (var who in new[] { Speaker.Dad, Speaker.Mum, Speaker.Teacher })
            Check(Scripts.TintOf(who) == Factions.Vessels.Tint,
                  $"{Scripts.NameOf(who)} is drawn in the Vessels' colour");

        // The branch filter. An unmarked beat plays for everyone; a marked one plays for one side.
        var always = new Beat { Who = Speaker.John, Line = "x" };
        var onlyHarvested = new Beat { Who = Speaker.John, Line = "x",
                                       Only = HarvestChoice.Harvested };

        Check(always.PlaysFor(HarvestChoice.Undecided) && always.PlaysFor(HarvestChoice.Waited),
              "an unmarked beat plays whatever he chose");
        Check(onlyHarvested.PlaysFor(HarvestChoice.Harvested), "a marked beat plays on its own side");
        Check(!onlyHarvested.PlaysFor(HarvestChoice.Waited), "and not on the other");
    }


    static void TestFairview()
    {
        TestLog.Line("- Fairview is a town, not an arena");

        // The three kinds partition the list, with nothing in two of them and nothing in none.
        int arenas = 0, story = 0, puzzles = 0;
        for (int i = 0; i < Arena.Names.Length; i++)
        {
            if (Arena.IsArena(i)) arenas++;
            if (Arena.IsStory(i)) story++;
            if (Arena.IsPuzzle(i)) puzzles++;
        }

        Check(arenas + story + puzzles == Arena.Names.Length,
              $"every layout is exactly one kind ({arenas} arenas, {story} story, {puzzles} puzzle)");
        Check(arenas == Arena.CombatLayouts, "and the combat count agrees with the predicate");
        Check(story == Arena.StoryLayouts, "and the story count does too");

        int layout = Arena.CombatLayouts;
        Check(Arena.IsStory(layout), $"{Arena.Names[layout]} is a story set");

        var town = new Arena(layout);
        TestLog.Line($"    {town.Name}: {town.Blocks.Count} blocks, {town.SpawnPoints.Count} spawns");

        Check(town.Name == "Fairview", "and it is Fairview");
        Check(town.SpawnPoints.Count > 0, "somebody can stand in it");

        // The absences. A town with a rocket-launcher crate on the corner says what it is louder
        // than any amount of dialogue can say otherwise.
        Check(town.WeaponSpawns.Count == 0, "there are no weapon crates on the green");
        Check(town.HealthSpawns.Count == 0, "and no med kits");
        Check(town.VehicleSpawns.Count == 0, "and no tank parked outside the school");
        Check(town.LaunchPads.Count == 0, "and nothing to bounce off");
        Check(town.Checkpoints.Count == 0, "and nothing to race through");

        int fragile = 0;
        foreach (var b in town.Blocks) if (b.Fragile) fragile++;
        Check(fragile == 0, $"and nothing in it can be blown up ({fragile})");

        // It has a floor, unlike a puzzle chamber, because people live on it.
        Check(town.FloorSlabs.Count > 0, "it has ground under it");

        // Enough building to be a place rather than a diagram. The houses alone are six a side.
        Check(town.Blocks.Count > 60, $"there is a town here ({town.Blocks.Count} blocks)");

        // The index the puzzles start at is not the number of combat arenas, and was until
        // Fairview sat between them.
        Check(Arena.FirstPuzzleLayout == Arena.CombatLayouts + Arena.StoryLayouts,
              "the puzzle chambers start after the story sets, not after the arenas");
        Check(Arena.IsPuzzle(Arena.FirstPuzzleLayout), "and that index really is a puzzle chamber");

        // And no match of any kind may land on the town, however the picker is asked.
        //
        // Both modes and both paths, which is the shape this test was missing the first time: it
        // covered a deathmatch by name and a deathmatch at random, and the bug that got through was
        // a *puzzle* at random — the fallback counted up from the end of the arenas, which had been
        // where the puzzles began right up until it wasn't.
        foreach (var mode in new[] { GameMode.Deathmatch, GameMode.Portal })
        {
            string which = mode == GameMode.Portal ? "Portal" : "a deathmatch";

            var named = new MatchSettings { Mode = mode, ArenaIndex = layout };
            Check(!Arena.IsStory(Match.ChooseArenaForTest(named)),
                  $"{which} asked for Fairview by name is given something else");

            bool wantPuzzle = new MatchSettings { Mode = mode }.IsPuzzle;
            bool wrong = false;

            for (int i = 0; i < 60 && !wrong; i++)
            {
                int got = Match.ChooseArenaForTest(new MatchSettings { Mode = mode, ArenaIndex = -1 });
                wrong = Arena.IsStory(got) || Arena.IsPuzzle(got) != wantPuzzle;
                if (wrong) Check(false, $"{which} rolled {Arena.Names[got]}, which is the wrong kind");
            }

            if (!wrong) Check(true, $"{which} only ever rolls a map of its own kind");
        }
    }


    static void TestCampaignState()
    {
        TestLog.Line("- the campaign runs in order and remembers one choice");

        // Every act has a card. A gap here is a blank chapter title and nothing else, which is
        // exactly the sort of thing that survives to release.
        Check(Acts.All.Length == System.Enum.GetValues<Act>().Length,
              "every act has a definition");

        foreach (var a in Acts.All)
        {
            Check(a.Name.Length > 0, $"act {a.Act} has a name");
            Check(a.Blurb.Length > 0, $"{a.Name} has a chapter line");
            Check(Acts.Get(a.Act) == a, $"{a.Name} is reachable by its own enum value");
        }

        // Four of the six are a faction making its case; the harvest and the ruling belong to
        // nobody, which is the whole point of both of them.
        int hosted = 0;
        foreach (var a in Acts.All) if (a.Host != null) hosted++;
        Check(hosted == 4, $"four acts are a faction's case, two are nobody's ({hosted})");

        var c = new CampaignState();

        Check(c.Act == Act.Childhood, "a new campaign starts at the beginning");
        Check(c.Choice == HarvestChoice.Undecided, "with nothing decided");
        Check(!c.SheIsWith, "and alone");
        Check(c.Affinity > 0f && !c.WouldSayYes,
              "she is hesitant rather than hostile, and nowhere near saying yes");

        Check(c.Advance(), "the childhood ends");
        Check(c.Act == Act.Harvest, "and the question is asked");

        // The sequencing rule, and the reason this test exists.
        Check(!c.Advance(), "the harvest act will not end while the question is open");
        Check(c.Act == Act.Harvest, "and it has not moved on regardless");

        c.Decide(HarvestChoice.Waited);
        Check(c.Choice == HarvestChoice.Waited, "he answers");

        c.Decide(HarvestChoice.Harvested);
        Check(c.Choice == HarvestChoice.Waited, "and cannot un-answer it later");

        Check(c.Advance() && c.Act == Act.Vault, "the vault opens once he has decided");
        Check(c.SheIsWith, "and she is there from here on");

        // Her, over the rest of the game.
        c.Warm(-5f);
        Check(c.Affinity >= 0f, "she cannot be driven below nothing");
        c.Warm(5f);
        Check(c.Affinity <= 1f, "or flattered past everything");
        Check(c.WouldSayYes, "and at the top of the range she would say yes");

        while (c.Advance()) { }
        Check(c.Act == Act.Arbiter && c.Finished, "the acts run out at the ruling");
        Check(!c.Advance(), "and there is nothing after it");

        c.Reset();
        Check(c.Act == Act.Childhood && c.Choice == HarvestChoice.Undecided
              && !c.WouldSayYes, "starting again forgets the choice and her with it");

        // A save file is a text file in a folder the player can open. Nonsense in it should put
        // somebody at the start of a chapter, not throw on a cast with nowhere to land.
        var saved = new CampaignState();
        saved.Advance();
        saved.Decide(HarvestChoice.Harvested);
        saved.Advance();
        saved.Warm(0.4f);
        saved.Save();

        var loaded = CampaignState.Load();
        Check(loaded.Act == saved.Act, "a saved campaign comes back at the same act");
        Check(loaded.Choice == saved.Choice, "with the same answer");
        Check(Mathf.Abs(loaded.Affinity - saved.Affinity) < 0.001f, "and her where she was");
    }


    static void TestModeLimitsMakeSense()
    {
        TestLog.Line("- score limits suit their modes");

        foreach (var m in Modes.All)
        {
            TestLog.Line($"    {m.Name}: {m.DefaultLimit} {m.LimitNoun} "
                     + $"({m.MinLimit}-{m.MaxLimit} step {m.LimitStep})");

            Check(m.MinLimit <= m.DefaultLimit && m.DefaultLimit <= m.MaxLimit,
                  $"{m.Name}: the default sits inside its own range");
            Check(m.LimitStep > 0 && m.LimitStep <= m.MaxLimit - m.MinLimit,
                  $"{m.Name}: the step can actually walk the range");

            // Reachable in both directions from the default, in whole steps.
            Check((m.DefaultLimit - m.MinLimit) % m.LimitStep == 0,
                  $"{m.Name}: the minimum is reachable from the default");
        }

        // A point a second means the limit is a duration. Anything under a minute is not a mode.
        var koth = Modes.Get(GameMode.KingOfTheHill);
        Check(koth.DefaultLimit >= 100, "holding the hill is a match, not a moment");
        Check(koth.DefaultLimit > Modes.Get(GameMode.Deathmatch).DefaultLimit * 4,
              "the hill's limit is on its own scale, not the deathmatch one");

        Check(Modes.Get(GameMode.Elimination).LimitStep == 1, "rounds step one at a time");
    }

    /// <summary>
    /// The play boundary has to be tight enough that being outside it is genuinely impossible
    /// rather than merely unusual — it is a death sentence with no appeal, so it must not fire on
    /// anyone who is simply near the edge.
    /// </summary>
    static void TestPlayBoundsAreTight()
    {
        TestLog.Line("- the play boundary is where it should be");

        var arena = new Arena(0);

        Check(arena.InPlay(Vector3.Zero), "the middle of the map is in play");
        Check(arena.InPlay(new Vector3(0f, 12f, 0f)), "so is the top of the tallest structure");

        foreach (var spawn in arena.SpawnPoints)
            Check(arena.InPlay(spawn), $"spawn {spawn} is in play");

        foreach (var v in arena.VehicleSpawns)
            Check(arena.InPlay(v), $"vehicle spawn {v} is in play");

        // Just inside the wall is fine; well outside it is not.
        Check(arena.InPlay(new Vector3(Arena.HalfWidth - 1f, 1f, 0f)), "hugging the wall is in play");
        Check(!arena.InPlay(new Vector3(Arena.HalfWidth + 6f, 1f, 0f)), "through the wall is not");
        Check(!arena.InPlay(new Vector3(0f, Arena.KillPlaneY - 4f, 0f)), "below the world is not");
        Check(!arena.InPlay(new Vector3(0f, 200f, 0f)), "far above the world is not");

        // A jetpack has to be able to climb without the boundary killing the player using it.
        Check(arena.InPlay(new Vector3(0f, Arena.WallHeight + 8f, 0f)),
              "there is headroom above the walls for a jetpack");
    }

    /// <summary>
    /// Every vehicle spawn has to have room to turn around in, and a route out of the district it
    /// is parked in.
    ///
    /// The existing spawn check asked whether a 3.5-metre circle was clear. A tank is eight metres
    /// long and nearly five wide, so it passed that check while being wedged between two blocks it
    /// could not drive between — which is exactly what a player found. Worse, a spawn can be
    /// perfectly clear and still be walled in: nothing was asking whether the hull could get
    /// anywhere from there.
    ///
    /// A vehicle cannot climb. It is a CharacterBody3D with no step handling at all, so anything
    /// taller than a kerb is a wall to it — which is why this uses its own reachability sweep
    /// rather than the pawn navigation graph.
    /// </summary>
    static void TestVehiclesCanLeaveTheirSpawns()
    {
        TestLog.Line("- vehicles can get out of their spawns");

        // Half-width of the widest ground hull, plus clearance. Turning room is the half-diagonal,
        // which is a stiffer requirement and only applies where the hull has to manoeuvre.
        float lane = 0f, turn = 0f;

        foreach (var v in Vehicles.Spawnable)
        {
            if (v.Flies) continue;
            lane = MathF.Max(lane, v.HalfExtents.Z + 0.4f);
            turn = MathF.Max(turn, new Vector2(v.HalfExtents.X, v.HalfExtents.Z).Length() + 0.3f);
        }

        for (int layout = 0; layout < Arena.Names.Length; layout++)
        {
            // Only combat arenas. Puzzle chambers are checked in CheckPortalMode and story sets
            // have no fight on them at all; every invariant below is about a map that does.
            if (!Arena.IsArena(layout)) continue;

            var arena = new Arena(layout);

            var seed = arena.NearestDrivable(Vector3.Zero, lane);
            int drivable = arena.DrivableRegion(seed, lane).Count;

            TestLog.Line($"    {arena.Name}: seed {seed}, {drivable} drivable cells, "
                     + $"{arena.VehicleSpawns.Count} vehicle spawns");

            foreach (var spawn in arena.VehicleSpawns)
            {
                Check(Drivable(arena, spawn, turn),
                      $"{arena.Name}: vehicle spawn {spawn} has room for a hull to turn");

                bool escapes = ReachesCore(arena, spawn, lane, out int reached);
                TestLog.Line($"    {arena.Name} {spawn}: {reached} cells reachable, escapes={escapes}");

                Check(escapes, $"{arena.Name}: a vehicle at {spawn} can drive out of its district");
            }
        }
    }

    /// <summary>Whether a hull of the given half-width fits here, standing on the ground.</summary>
    static bool Drivable(Arena arena, Vector3 at, float radius)
    {
        if (arena.IsOverPit(at)) return false;

        foreach (var b in arena.Blocks)
        {
            float top = b.Centre.Y + b.HalfExtents.Y;
            float bottom = b.Centre.Y - b.HalfExtents.Y;

            // Anything from kerb height up to hull height is a wall. Below that a hull rides over
            // it; above that it drives underneath.
            if (top <= 0.35f || bottom >= 2.6f) continue;

            if (MathF.Abs(at.X - b.Centre.X) < b.HalfExtents.X + radius
                && MathF.Abs(at.Z - b.Centre.Z) < b.HalfExtents.Z + radius) return false;
        }
        return true;
    }

    /// <summary>
    /// Flood fill of drivable ground from a spawn, asking whether it reaches the core. Four-metre
    /// cells — fine enough to find a gap a hull fits through, coarse enough to stay cheap over a
    /// map this size.
    /// </summary>
    static bool ReachesCore(Arena arena, Vector3 from, float radius, out int reached)
    {
        const float Cell = 4f;

        int Ix(float x) => Mathf.RoundToInt(x / Cell);
        int Iz(float z) => Mathf.RoundToInt(z / Cell);

        var seen = new HashSet<(int, int)>();
        var queue = new Queue<(int, int)>();

        var start = (Ix(from.X), Iz(from.Z));
        seen.Add(start);
        queue.Enqueue(start);

        bool core = false;

        while (queue.Count > 0)
        {
            var (cx, cz) = queue.Dequeue();

            float wx = cx * Cell, wz = cz * Cell;
            if (MathF.Abs(wx) < 58f && MathF.Abs(wz) < 42f) core = true;

            foreach (var (dx, dz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
            {
                var next = (cx + dx, cz + dz);
                if (!seen.Add(next)) continue;

                float nx = next.Item1 * Cell, nz = next.Item2 * Cell;

                if (MathF.Abs(nx) > Arena.HalfWidth - 2f || MathF.Abs(nz) > Arena.HalfDepth - 2f) continue;
                if (!Drivable(arena, new Vector3(nx, 0f, nz), radius)) continue;

                queue.Enqueue(next);
            }
        }

        reached = seen.Count;
        return core;
    }

    static void TestVehiclesAndJetpack()
    {
        TestLog.Line("- vehicles and jetpack are sane");

        // Three distinct vehicles, each usable.
        var seen = new HashSet<VehicleKind>();
        foreach (var v in Vehicles.All)
        {
            Check(seen.Add(v.Kind), $"{v.Name} is a distinct kind");
            Check(v.Health > 0f, $"{v.Name} has health");
            Check(v.MaxSpeed > 0f && v.Accel > 0f, $"{v.Name} can move");
            Check(v.TurnRate > 0f, $"{v.Name} can turn");
        }
        Check(seen.Count == 3, "car, tank and plane all exist");

        // The car has to be worth taking over running, and the tank worth its slowness.
        Check(Vehicles.Car.MaxSpeed > Pawn.Speed15(), "the car outruns a sprinting player");
        Check(Vehicles.Tank.Health > Vehicles.Car.Health, "the tank is the tough one");
        Check(Vehicles.Plane.MaxSpeed > Vehicles.Car.MaxSpeed, "the plane is the fast one");
        Check(Vehicles.Plane.Flies && !Vehicles.Car.Flies, "only the plane flies");

        // Armament: the car rams, the other two shoot.
        Check(Vehicles.Car.Gun is null && Vehicles.Car.RamDamage > 0f, "the car is a battering ram");
        Check(Vehicles.Tank.Gun is not null, "the tank has a cannon");
        Check(Vehicles.Plane.Gun is not null, "the plane has guns");

        // A mounted gun must never run dry: an ammo count on a fixed weapon would silently
        // disarm the vehicle for the rest of the match.
        foreach (var v in Vehicles.All)
            if (v.Gun is { } gun)
                Check(gun.Ammo == 0, $"{v.Name} gun has unlimited ammo");

        // Every arena has to actually park them somewhere legal.
        for (int layout = 0; layout < Arena.Names.Length; layout++)
        {
            // Only combat arenas. Puzzle chambers are checked in CheckPortalMode and story sets
            // have no fight on them at all; every invariant below is about a map that does.
            if (!Arena.IsArena(layout)) continue;

            var arena = new Arena(layout);
            Check(arena.VehicleSpawns.Count >= Vehicles.Spawnable.Length,
                  $"{arena.Name} parks one of every vehicle");

            foreach (var at in arena.VehicleSpawns)
            {
                Check(arena.Contains(at), $"{arena.Name} vehicle spawn {at} is in bounds");
                Check(!arena.IsOverPit(at), $"{arena.Name} vehicle spawn {at} is not over a pit");
                Check(arena.IsClearOfBlocks(at, 3.5f, 2f),
                      $"{arena.Name} vehicle spawn {at} has room for a hull");
            }
        }

        // The jetpack has to be a climb, not a second jump, and has to run out.
        // Generous now — eighteen seconds rather than four and a half — but still a budget you can
        // run out of. A jetpack you never have to think about is a movement speed, not a pickup.
        Check(Pawn.JetFuelMax > 1f && Pawn.JetFuelMax < 40f,
              "jetpack fuel is a meaningful but finite budget");

        // A hull is only a threat if it can be threatened back.
        foreach (var v in Vehicles.All)
            Check(v.Health <= 900f, $"{v.Name} is destructible in a reasonable number of hits");

        // The cannon must out-range its own blast by a wide margin, or firing at anything nearby
        // kills the driver — the shell would land inside its own splash every time.
        var tankGun = Vehicles.Tank.Gun!;
        Check(tankGun.Range > tankGun.BlastRadius * 8f,
              "the cannon out-ranges its own blast by enough to be usable");

        // Five times the floor of the box before it. Checked as a ratio against the recorded old
        // size rather than against the constants, so a later resize has to come here and say so.
        const float OldArea = 124f * 92f;
        float width = Arena.HalfWidth * 2f, depth = Arena.HalfDepth * 2f;
        float area = width * depth;

        TestLog.Line($"    arena floor {width:0}x{depth:0}m, {area / OldArea:0.00} times the old area");
        Check(area / OldArea is > 4.5f and < 5.5f, "the arena is five times the floor it was");

        // The machinery of the outer districts. Every arena gets both, because they are built in
        // the shared district pass rather than per layout.
        for (int layout = 0; layout < Arena.Names.Length; layout++)
        {
            // Only combat arenas. Puzzle chambers are checked in CheckPortalMode and story sets
            // have no fight on them at all; every invariant below is about a map that does.
            if (!Arena.IsArena(layout)) continue;

            var arena = new Arena(layout);

            int elevators = 0, pushers = 0, shuttles = 0;

            foreach (var p in arena.MovingPlatforms)
            {
                bool vertical = MathF.Abs(p.B.Y - p.A.Y) > MathF.Abs(p.B.X - p.A.X)
                                + MathF.Abs(p.B.Z - p.A.Z);

                if (p.Pushes) pushers++;
                else if (vertical) elevators++;
                else shuttles++;

                Check(p.Period > 1f, $"{arena.Name}: platform period is sane");

                // An elevator has to wait at the ends or it is a timing puzzle rather than a lift.
                if (vertical && !p.Pushes)
                    Check(p.Dwell > 0.15f, $"{arena.Name}: an elevator waits to be boarded");

                // A push wall that does not sweep across a hole is just an inconvenience.
                if (p.Pushes)
                {
                    Check(MathF.Abs(p.B.Y - p.A.Y) < 0.01f, $"{arena.Name}: a push wall sweeps level");
                    Check(arena.IsOverPit(p.A.Lerp(p.B, 0.5f)),
                          $"{arena.Name}: a push wall sweeps over something worth being pushed into");
                }
            }

            TestLog.Line($"    {arena.Name}: {elevators} elevators, {pushers} push walls, {shuttles} shuttles");

            Check(elevators >= 4, $"{arena.Name} has elevators");
            Check(pushers >= 4, $"{arena.Name} has push walls");
            Check(arena.RallySpots.Count > 0, $"{arena.Name} keeps rally points in the core");

            foreach (var spot in arena.RallySpots)
                Check(MathF.Abs(spot.X) <= 62f && MathF.Abs(spot.Z) <= 46f,
                      $"{arena.Name}: bots rally in the core, not the far corners");
        }

        // Dwell is what separates an elevator from a shuttle, so it is worth proving rather than
        // trusting: a platform with dwell has to be genuinely stationary at the start of a leg.
        var lift = new MovingPlatformDef(Vector3.Zero, Vector3.Up * 10f, Vector3.One, 10f, dwell: 0.3f);
        var shuttle = new MovingPlatformDef(Vector3.Zero, Vector3.Up * 10f, Vector3.One, 10f);

        Check(lift.Travel(0f) == lift.Travel(1.4f), "an elevator holds still at the bottom");
        Check(lift.Travel(5f) == lift.Travel(6.4f), "an elevator holds still at the top");
        Check(shuttle.Travel(0f) != shuttle.Travel(1.4f), "a shuttle never stops");
        Check(lift.Travel(2.5f) is > 0f and < 1f, "an elevator is somewhere in between mid-run");

        TestVehiclesCanLeaveTheirSpawns();
        TestWeaponsLookDifferent();
        TestModeLimitsMakeSense();
        TestCampaignState();
        TestFairview();
        TestStoryScript();
        TestSurfaces();
        TestChildhoodMission();
        TestHarvestMission();
        TestScopeLock();
        TestDressing();
        TestNeedler();
        TestHeadshotsAndMuzzles();
        TestPlayBoundsAreTight();

        // Every wall and platform on the map comes down, and the two things that must not are the
        // edge of the world and the paint on the floor.
        //
        // This check used to assert the exact opposite — that most of the map was *not*
        // destructible, on the reasoning that a map which erodes ends in a flat box. The reasoning
        // was right and the answer was wrong: what it produced was one skybridge per arena that
        // everybody shot at and three hundred blocks that were scenery. Erosion is now prevented by
        // the rebuild timer instead, which is why every assertion here is about the two exceptions
        // and about things coming back rather than about how little is breakable.
        for (int layout = 0; layout < Arena.Names.Length; layout++)
        {
            // Only combat arenas. Puzzle chambers are checked in CheckPortalMode and story sets
            // have no fight on them at all; every invariant below is about a map that does.
            if (!Arena.IsArena(layout)) continue;

            var arena = new Arena(layout);

            int fragile = 0, solid = 0;
            foreach (var b in arena.Blocks) { if (b.Fragile) fragile++; else solid++; }

            TestLog.Line($"    {arena.Name}: {fragile} of {arena.Blocks.Count} blocks destructible, "
                     + $"{solid} permanent");

            Check(fragile > arena.Blocks.Count * 0.8f,
                  $"{arena.Name} is a map you can take apart ({fragile}/{arena.Blocks.Count})");

            // The perimeter, by index. A hole in the outer wall is a way out of the match, and out
            // of bounds is fatal with no exceptions — so this one is not a taste question.
            for (int i = 0; i < Arena.PerimeterBlocks; i++)
                Check(!arena.Blocks[i].Fragile,
                      $"{arena.Name}: the edge of the world stays up");

            foreach (var b in arena.Blocks)
            {
                if (b.Fragile) continue;

                // Everything else that survived the pass has to be paint: flush with the floor,
                // with nothing standing proud of it to knock down.
                bool perimeter = MathF.Abs(b.Centre.X) > Arena.HalfWidth
                                 || MathF.Abs(b.Centre.Z) > Arena.HalfDepth;

                Check(perimeter || b.Centre.Y + b.HalfExtents.Y <= 0.5f,
                      $"{arena.Name}: the only permanent blocks are the wall and the floor markings");
            }

            // A catwalk and a citadel tier must not cost the same to bring down, or the map stops
            // telling you which is which.
            float thinnest = float.MaxValue, thickest = 0f;
            foreach (var b in arena.Blocks)
            {
                if (!b.Fragile) continue;
                float hp = Match.StructureHealth(b.HalfExtents);
                thinnest = MathF.Min(thinnest, hp);
                thickest = MathF.Max(thickest, hp);
            }

            // The spread matters as much as the range. A map where everything is either the floor
            // value or the cap has no scale on it at all — it just has two kinds of wall — and the
            // two extremes alone cannot tell you which you have.
            int atFloor = 0, atCap = 0, between = 0;
            foreach (var b in arena.Blocks)
            {
                if (!b.Fragile) continue;
                float hp = Match.StructureHealth(b.HalfExtents);
                if (hp <= Match.PlatformHealth + 0.01f) atFloor++;
                else if (hp >= Match.StructureHealthCap - 0.01f) atCap++;
                else between++;
            }

            TestLog.Line($"    {arena.Name}: {thinnest:0}-{thickest:0} hp — "
                     + $"{atFloor} flimsy, {between} in between, {atCap} heavy");

            Check(thickest > thinnest * 2f,
                  $"{arena.Name}: heavy structure is genuinely harder to drop ({thinnest:0} vs {thickest:0})");
            Check(between > fragile / 5,
                  $"{arena.Name}: toughness is a scale, not two categories ({between} in between)");
        }

        // One tank shell takes a walkway. Nothing in the game takes a citadel tier in one, which is
        // the point of the cap being where it is.
        //
        // Measured through the vehicle multiplier rather than off the weapon table. The raw
        // BlastDamage is no longer what a shell does to a wall, so checking it would have gone on
        // passing while saying nothing about the rule it exists to protect.
        var shell = Vehicles.Tank.Gun!;
        float againstStructure = shell.BlastDamage * Match.VehicleStructureMultiplier;

        Check(againstStructure >= Match.PlatformHealth,
              $"one shell brings a walkway down ({againstStructure:0} against {Match.PlatformHealth:0})");
        Check(againstStructure < Match.StructureHealthCap,
              $"and nothing in the game drops heavy structure in one hit "
              + $"({againstStructure:0} against {Match.StructureHealthCap:0})");
        Check(Match.StructureRebuildTime(Match.StructureHealthCap)
              > Match.StructureRebuildTime(Match.PlatformHealth),
              "heavy structure stays down longer than a catwalk");
    }

    static LobbyScreen GoToLobby(Harness h)
    {
        h.TapConfirm();                                        // title -> mode select

        // Walk to the last row rather than hardcoding a count, so adding a settings row to mode
        // select does not silently break every lobby test.
        var mode = (ModeSelectScreen)h.Stack.Top;
        for (int i = 0; i < mode.RowCount - 1; i++) h.Nav(0, 1);

        h.TapConfirm();                                        // -> lobby
        return (LobbyScreen)h.Stack.Top;
    }
}
