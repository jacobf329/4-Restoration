using System;
using System.Collections.Generic;
using Godot;

namespace HitboxClone;

/// <summary>
/// The match in progress: splitscreen viewports, cameras, HUD, and the translation from device
/// state into world-space intent.
///
/// All simulation lives in <see cref="Match"/>. This class owns only presentation and input, which
/// is why the headless harness can play full matches without any of it.
/// </summary>
public sealed class MatchScreen : UiScreen
{
    public override string Title => "MATCH";

    /// <summary>One human player's view: their pawn, their device, their camera and their yaw.</summary>
    sealed class View
    {
        public int PawnIndex;
        public string DeviceId = "";
        public Camera3D Camera = null!;
        public SubViewportContainer Container = null!;
        public Rect2 Rect;
        public float Yaw;
        public float Pitch;

        /// <summary>Transient upward kick from firing, added on top of <see cref="Pitch"/>.</summary>
        public float Recoil;
        public int LastShotSeen;

        /// <summary>Previous frame's kill-banner timer, so a fresh kill can be detected.</summary>
        public float LastKillBanner;

        /// <summary>
        /// How far out the chase camera boom is, 0 to 1. The only thing that smooths the vehicle
        /// camera, and the reason it no longer flips.
        /// </summary>
        public float Boom = 1f;

        /// <summary>
        /// Third person on foot. Off by default: this is a first-person shooter and the aim model
        /// assumes the view is the aim. The toggle exists because seeing your own character is
        /// most of the point of having spent the art budget on one.
        /// </summary>
        public bool ThirdPerson;

        /// <summary>The on-foot boom, kept apart from the vehicle's so neither yanks the other.</summary>
        public float FootBoom = 1f;

        /// <summary>Which spawn card the cursor is on while dead.</summary>
        public int SpawnPick;

        /// <summary>Which of the posts your side holds is selected, as an index into that list.</summary>
        public int PostPick;

        /// <summary>Who the aim assist is currently helping onto, and for how long.</summary>
        public Pawn? AssistTarget;
        public float AssistHeld;

        /// <summary>Whether the scope was up last frame, so scoping in can be seen as an event.</summary>
        public bool WasScoped;

        /// <summary>Who the scope locked onto when it came up. Null once the lock is gone.</summary>
        public Pawn? ScopeLock;

        /// <summary>Seconds left of the fast swing onto a freshly acquired target.</summary>
        public float ScopeSnapping;

        /// <summary>How long the current lock has been held, so it can be let go of on a clock.</summary>
        public float ScopeHeld;

        /// <summary>
        /// Set once a lock has been lost, so it cannot silently grab the same target again.
        ///
        /// Without this the lock would come straight back the moment the crosshair drifted near
        /// anybody, which is the behaviour being removed: a lock you cannot get rid of without
        /// lowering the scope is a lock that is aiming for you.
        /// </summary>
        public bool ScopeLockSpent;

        public InputDevice? Device => Devices.ById(DeviceId);
    }

    readonly MatchSettings settings;
    readonly Main app;
    readonly List<LobbySlot> roster = new();

    /// <summary>The lobby as it was, kept so the results screen can start the same match again.</summary>
    readonly LobbySlot[] slots;
    readonly List<View> views = new();

    Match match = null!;
    World3D world = null!;
    bool built;

    /// <summary>Names a player whose pad vanished, so a mid-match disconnect explains itself.</summary>
    string? disconnectNote;

    public MatchScreen(MatchSettings settings, LobbySlot[] slots, Main app)
    {
        this.settings = settings;
        this.app = app;
        this.slots = slots;

        // Humans first, so a pawn's roster position matches its view index for the ones that have
        // a view. Everything downstream — the splitscreen layout, the cull masks — walks the
        // roster in order and takes the humans as it finds them.
        foreach (var s in slots)
            if (s.Claimed)
                roster.Add(new LobbySlot
                {
                    DeviceId = s.DeviceId,
                    ClassIndex = s.ClassIndex,
                    FactionIndex = s.FactionIndex,
                    IsBot = false,
                });

        // Then every bot: the ones with a seat to be shown in, and the ones without. A bot needs no
        // viewport and no cull layer of its own, which is the whole reason the roster can be three
        // times the seat count.
        int humans = roster.Count;
        int bots = Mathf.Min(settings.BotCount, LobbyScreen.MaxFighters - humans);

        for (int i = 0; i < bots; i++)
            roster.Add(new LobbySlot
            {
                ClassIndex = i,
                FactionIndex = i,
                IsBot = true,
            });
    }

    public Match Sim => match;

    /// <summary>
    /// A scripted scene running in this match, or null for an ordinary fight.
    ///
    /// Set after construction rather than passed in, because it needs the match that construction
    /// builds. A mission owns no input and no rendering — it watches where the player is and says
    /// what is being said — so this is the entire surface story mode needs from the match screen.
    /// </summary>
    public IMission? Mission;

    /// <summary>Called once the mission's last line is read, so the campaign can move on.</summary>
    public System.Action? OnMissionDone;

    public override void OnEnter()
    {
        if (built) return;
        built = true;

        var size = app.Ui.GetViewportRect().Size;
        var humanIndices = new List<int>();
        for (int i = 0; i < roster.Count; i++) if (!roster[i].IsBot) humanIndices.Add(i);

        var rects = Layout(humanIndices.Count, size);
        world = new World3D();

        for (int v = 0; v < humanIndices.Count; v++)
        {
            var container = new SubViewportContainer { Stretch = true, MouseFilter = Control.MouseFilterEnum.Ignore };
            container.Position = rects[v].Position;
            container.Size = rects[v].Size;
            app.ViewportHost.AddChild(container);

            var vp = new SubViewport
            {
                RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
                HandleInputLocally = false,
                World3D = world,          // every view renders the one shared 3D world
                Msaa3D = Graphics.Msaa,
            };
            container.AddChild(vp);

            int pawnIndex = humanIndices[v];

            // First person, so the camera needs no collision handling at all — it sits inside the
            // pawn's own capsule, which is already kept out of walls by the character controller.
            // The cull mask hides this player's own body from their own view.
            // Vertical FOV held constant, which is what a wide splitscreen strip wants: the extra
            // width becomes extra horizontal view rather than a zoom. Holding *width* constant
            // instead squeezes a 1920x540 strip down to about 34 degrees vertically, which reads
            // as being shoved up against the scenery.
            //
            // 60 vertical is roughly 91 horizontal at 16:9. The original 88 vertical was far too
            // wide and was the real cause of the fisheye look.
            var cam = new Camera3D
            {
                KeepAspect = Camera3D.KeepAspectEnum.Height,
                Fov = BaseFov,
                Near = 0.05f,
                CullMask = Pawn.FirstPersonCullMask(pawnIndex),
            };
            vp.AddChild(cam);

            views.Add(new View
            {
                PawnIndex = pawnIndex,
                DeviceId = roster[pawnIndex].DeviceId ?? "",
                Camera = cam,
                Container = container,
                Rect = rects[v],
            });
        }

        // The simulation is parented under the first viewport so it lands in the shared World3D.
        Node simParent = views.Count > 0 ? views[0].Container.GetChild<SubViewport>(0) : app;
        match = new Match();
        match.Build(simParent, settings, roster, visuals: true);
        match.InputSource = ResolveInput;

        // Whatever was being pressed on the way out of the menus does not carry into the arena.
        // Without this the latch raised by the last lobby frame is spent on the match's first
        // physics step, which is a jump, or a boarding, nobody asked for.
        Devices.ConsumeGameplayEdges();

        foreach (var v in views)
        {
            v.Yaw = match.Pawns[v.PawnIndex].Facing;
            v.Pitch = 0f;
        }
    }

    public override void OnExit()
    {
        foreach (var v in views) v.Container.QueueFree();
        views.Clear();
        match?.QueueFree();
    }

    /// <summary>
    /// Viewport rectangles. Two players split top/bottom rather than left/right: a 3D view wants
    /// horizontal room far more than vertical, and a tall narrow slice is miserable to aim in.
    /// </summary>
    static Rect2[] Layout(int players, Vector2 size)
    {
        float w = size.X, h = size.Y;

        return players switch
        {
            <= 1 => new[] { new Rect2(0, 0, w, h) },
            2 => new[] { new Rect2(0, 0, w, h / 2), new Rect2(0, h / 2, w, h / 2) },
            _ => new[]
            {
                new Rect2(0, 0, w / 2, h / 2),
                new Rect2(w / 2, 0, w / 2, h / 2),
                new Rect2(0, h / 2, w / 2, h / 2),
                new Rect2(w / 2, h / 2, w / 2, h / 2),
            },
        };
    }

    // ---- input ----

    const float TurnRate = 3.4f;    // radians/second at full deflection
    const float PitchRate = 2.2f;   // slower than yaw, as is conventional for stick aiming
    const float RecoilRecovery = 0.55f;   // radians/second the view settles back down
    const float BaseFov = 60f;            // vertical, hip-fire

    protected override void UpdateScreen(float dt, IReadOnlyList<InputDevice> devices)
    {
        if (match == null) return;

        disconnectNote = null;

        foreach (var v in views)
        {
            var d = DeviceFor(v);
            var c = ReadControls(v);

            // A pad vanishing mid-match auto-pauses and names the player, rather than leaving a
            // frozen body in the arena for everyone else to shoot.
            //
            // Not in a scene, though, and that exemption is a bug fix rather than a nicety. There
            // is nobody else in a story mission to be disadvantaged by a still body, and this
            // branch is reached whenever the bound device id fails to resolve for any reason at
            // all - which pauses the game on the frame it opens and looks, from the outside,
            // exactly like a player who cannot move or do anything.
            if (!SoloScene && (d is null || !d.Connected))
            {
                disconnectNote = $"Player {v.PawnIndex + 1}'s controller disconnected";
                Pause();
                return;
            }

            // Look: right stick on a pad, turn keys on a keyboard. Both arrive on the same axis,
            // so there is no special case here beyond keyboards having no pitch.
            // Aiming steadies the look rate, and a scope steadies it far more — a 16-degree field
            // magnifies every stick twitch, so without this the Marksman would be unusable.
            var pawn = match.Pawns[v.PawnIndex];
            float steady = pawn.LookRateScale;
            if (pawn.Ads && pawn.Weapon.HasScope) steady *= 0.45f;

            // Slipped on a butter trail: the view goes over backwards with the player and the
            // stick is dead for the second it lasts. Handled here rather than in the pawn because
            // the pawn does not own a human's view — it is told where the camera is pointing, so
            // leaving this out would have the body on the floor and the camera still level.
            if (pawn.Slipping && pawn.Alive)
            {
                v.Pitch = Mathf.Lerp(v.Pitch, Pawn.MaxPitch, 1f - MathF.Exp(-11f * dt));
                if (c.Start) { Pause(); return; }
                continue;
            }

            TrackScopeState(v, pawn);
            ApplyAimAssist(v, pawn, c.Look, dt, ref steady);

            v.Yaw += c.Look.X * TurnRate * steady * dt;
            v.Pitch = MathU.Clamp(v.Pitch - c.Look.Y * PitchRate * steady * dt,
                                  -Pawn.MaxPitch, Pawn.MaxPitch);

            // Rumble on the moment of a kill. The match cannot do this itself — it has no idea
            // which pad, if any, is behind a given pawn.
            var pawnForRumble = match.Pawns[v.PawnIndex];
            if (pawnForRumble.KillBanner > v.LastKillBanner) d?.Rumble(0.3f, 0.55f, 0.18f);
            v.LastKillBanner = pawnForRumble.KillBanner;

            if (!pawn.Alive && d != null) StepSpawnChoice(v, pawn, d);

            // Down on the d-pad flips this view between first and third person. Per view, not per
            // match: in splitscreen one player wanting to see their character is no reason for
            // anyone else's camera to move.
            if (c.CameraToggle && !pawn.InVehicle)
            {
                v.ThirdPerson = !v.ThirdPerson;
                v.Camera.CullMask = v.ThirdPerson
                    ? Pawn.ThirdPersonCullMask
                    : Pawn.FirstPersonCullMask(v.PawnIndex);
                v.FootBoom = 1f;
            }

            if (c.Start) { Pause(); return; }
        }

        UpdateCameras(dt);

        // Tell the audio mixer where the humans are, so world sounds attenuate against the
        // nearest player rather than against nothing.
        var listeners = new Vector3[views.Count];
        for (int i = 0; i < views.Count; i++)
            listeners[i] = match.Pawns[views[i].PawnIndex].GlobalPosition;
        Sfx.Listeners = listeners;

        // A scene ends when its script does, not on a score. Stepped after the views so the
        // stage test sees where the player actually got to this frame.
        if (Mission is { } mission)
        {
            mission.Step(dt, match);

            if (mission.Complete)
            {
                Mission = null;
                Stack.Pop();
                OnMissionDone?.Invoke();
                return;
            }
        }

        if (match.Finished)
        {
            Sfx.Play(Sound.MatchEnd);
            Sfx.Listeners = System.Array.Empty<Vector3>();
            Stack.Push(new ResultsScreen(match, settings, app, slots));
        }
    }

    void Pause()
    {
        match.Paused = true;
        Stack.Push(new PauseScreen(match, disconnectNote, app));
    }

    /// <summary>
    /// Back does nothing during a match. Start is the only thing that opens the menu.
    ///
    /// It used to pause, and Back is bound to both the Back button and Y — so Y opened the pause
    /// menu mid-fight, which is exactly the button a shooter player expects to swap weapons with.
    /// Consumed rather than passed up, so it cannot fall through to the screen stack and drop the
    /// match either.
    /// </summary>
    protected override bool OnBack(InputDevice d) => true;

    // ---- aim assist ----
    //
    // Two effects, which is what console shooters actually do and what makes a pad feel like the
    // ones people are used to.
    //
    // **Friction** slows the look rate while the crosshair is near someone, so the last few degrees
    // onto a target are fine control instead of the same coarse sweep as looking around the room.
    // This is most of the feel. Sensitivity was never the problem — one rate has to serve both
    // spinning to face a doorway and holding a head at forty metres, and no single number does.
    //
    // **Magnetism** rotates the view gently toward the target, but *only in proportion to how much
    // the player is already moving the stick*. That condition is the whole trick: help while you
    // are tracking, nothing at all while you are still. Applied unconditionally it takes the aim
    // off you, which reads as the game fighting your hands rather than helping them.
    //
    // Neither one fires the gun for you. There is no bullet bending here: shots still go exactly
    // where the crosshair is.

    /// <summary>Half-angle of the cone a target has to be inside to attract any help.</summary>
    const float AssistCone = 0.20f;          // radians, about 11 degrees

    /// <summary>How much the look rate is cut when the crosshair is right on someone.</summary>
    const float AssistFriction = 0.28f;

    /// <summary>Radians per second of pull at full stick, right on the edge of the cone.</summary>
    const float AssistPull = 0.68f;

    /// <summary>
    /// Seconds of pull after the crosshair first finds a target, before it fades to nothing.
    ///
    /// This is what stops it being lock-on. The help is for the moment you swing onto someone —
    /// after that the tracking is yours, and a target that keeps moving does not drag your aim
    /// along behind it. The timer resets whenever the assisted target changes, so acquiring a new
    /// enemy gets the same brief hand and holding one does not.
    /// </summary>
    const float AssistAcquire = 0.45f;

    /// <summary>Beyond this a target is too far away to be worth helping with.</summary>
    const float AssistRange = 70f;

    // ---- the scope lock ----
    //
    // Raising a scope grabs whoever you were already looking at, once. After that the gun is
    // yours: the stick always wins, the lock only tracks while your hands are still, and a target
    // who moves enough gets away.
    //
    // The previous version applied its pull every frame, without decaying and without caring
    // whether the stick was moving, for as long as anybody was inside the cone. That is not an
    // assist, it is a turret — the report was "I can't even move the crosshairs", and it was
    // exactly right. Worse, it made the shot the weapon exists for impossible: you cannot climb
    // from the chest to the head if something is pulling you back to the chest.
    //
    // A scope still needs help that hip-fire does not. At a 14-degree field of view every stick
    // twitch is six times the angle it would be at the hip, so the initial swing onto a distant
    // body is below what a thumbstick can comfortably resolve. That is what the snap is for, and
    // it is the whole of what it is for. Nothing here fires the gun or bends a bullet.

    /// <summary>Half-angle you must already be looking within for the scope to grab somebody.</summary>
    const float ScopeAcquireCone = 0.16f;    // radians, about 9 degrees

    /// <summary>
    /// Half-angle beyond which a lock is lost and does not come back.
    ///
    /// Wider than the acquire cone, so a target has to genuinely get away — or you have to
    /// genuinely aim off them — rather than being dropped by a step sideways.
    /// </summary>
    const float ScopeBreakCone = 0.30f;      // radians, about 17 degrees

    /// <summary>Radians per second of the swing onto a freshly acquired target.</summary>
    ///
    /// Fast enough to read as instant and slow enough to see happen, which matters: a view that
    /// teleports leaves you unable to tell whether it moved or the world did.
    const float ScopeSnapRate = 16f;

    /// <summary>How long that swing may last before it gives up and hands over.</summary>
    const float ScopeSnapTime = 0.16f;

    /// <summary>
    /// Radians per second the lock follows its target while the stick is untouched.
    ///
    /// Deliberately beatable. It holds somebody walking across your scope so that lining up a
    /// shot does not mean fighting their pace, and loses somebody who breaks into a sprint or
    /// changes direction — which is what makes it a lock a target can escape rather than a
    /// sentence they cannot.
    /// </summary>
    const float ScopeTrack = 1.2f;

    /// <summary>Seconds the lock follows at full rate before it starts letting go.</summary>
    ///
    /// The geometry alone will not lose anybody. At forty metres a target sprinting flat out
    /// across your view subtends about a fifth of a radian per second, so any tracking rate worth
    /// having holds them forever — "they can outrun it" is true at five metres and a fiction at
    /// forty. A lock that never lets go is the turret again, just a politer one.
    ///
    /// So it lets go on a clock instead. Full help while you settle, then a fade to nothing, and
    /// after that the shot is entirely yours however still you kept your hands. That matches what
    /// was actually asked for: scoping in locks on, and staying scoped does not keep it.
    const float ScopeHoldFull = 1.1f;

    /// <summary>Seconds after that over which the lock fades from full to nothing.</summary>
    const float ScopeHoldFade = 1.4f;

    /// <summary>Stick deflection past which the player is steering and the lock does nothing.</summary>
    const float ScopeOverride = 0.12f;

    // ---- harness ----
    //
    // The scope lock's numbers, exposed so the suite can assert the design rather than the
    // arithmetic. Nothing here is used by the game.

    public static float ScopeAcquireConeForTest => ScopeAcquireCone;
    public static float ScopeBreakConeForTest => ScopeBreakCone;
    public static float ScopeSnapRateForTest => ScopeSnapRate;
    public static float ScopeSnapTimeForTest => ScopeSnapTime;
    public static float ScopeTrackForTest => ScopeTrack;
    public static float ScopeOverrideForTest => ScopeOverride;
    public static float ScopeHoldFullForTest => ScopeHoldFull;
    public static float ScopeHoldFadeForTest => ScopeHoldFade;
    public static float ScopeGripForTest(float held) => ScopeGrip(held);

    void ApplyAimAssist(View v, Pawn self, Vector2 look, float dt, ref float steady)
    {
        int level = UserSettings.AimAssist;
        if (level <= 0 || !self.Alive || self.InVehicle) return;

        // Off for anyone on a keyboard and mouse: a mouse does not need it and it feels like drag.
        if (v.Device is not { IsGamepad: true }) return;

        float strength = level switch { 1 => 0.5f, 2 => 1f, _ => 1.45f };

        // Aiming down sights leans on it harder, which is the convention and also where the fine
        // control actually matters.
        if (self.Ads) strength *= 1.3f;

        // Down a scope the lock replaces the magnetism below outright rather than stacking with
        // it. Two pulls on the same axis, one of them decaying and one of them not, is a fight
        // between them that shows up as the crosshair easing off a target it just arrived on.
        if (self.Ads && self.Weapon.HasScope)
        {
            StepScopeLock(v, self, look, dt);
            return;
        }

        var (target, offYaw, offPitch, angle) = BestAssistTarget(v, self);
        if (target == null) return;

        float closeness = 1f - MathU.Clamp01(angle / AssistCone);
        if (closeness <= 0f) return;

        // Friction: eased rather than linear, so it comes on as you arrive rather than as a step.
        // This half stays on the whole time — it is fine control, not assistance, and it never
        // moves your aim anywhere you did not push it.
        steady *= 1f - AssistFriction * strength * closeness * closeness;

        // Magnetism: scaled by stick deflection, so it never moves the aim on its own.
        float effort = MathU.Clamp01(look.Length());
        if (effort < 0.06f) return;

        // And decayed since this target was acquired, so it hands you the last few degrees onto
        // someone and then gets out of the way. Held on a target it falls to nothing within half a
        // second, which is the difference between an aim assist and a lock.
        if (target != v.AssistTarget)
        {
            v.AssistTarget = target;
            v.AssistHeld = 0f;
        }
        else v.AssistHeld += dt;

        float fresh = 1f - MathU.Clamp01(v.AssistHeld / AssistAcquire);
        if (fresh <= 0f) return;

        float pull = AssistPull * strength * closeness * effort * fresh * dt;

        v.Yaw = MathU.MoveAngleToward(v.Yaw, v.Yaw + offYaw, pull);
        v.Pitch = MathU.Clamp(v.Pitch + MathU.Clamp(offPitch, -pull, pull),
                              -Pawn.MaxPitch, Pawn.MaxPitch);
    }

    /// <summary>
    /// One lock, acquired when the scope comes up, released the moment you disagree with it.
    ///
    /// The three rules, in order of precedence, because the order is the design:
    ///
    ///   1. The stick wins. Any real deflection and the lock does nothing at all this frame —
    ///      it does not fight, halve, or resist. Climbing from the chest to the head has to be
    ///      exactly as easy as it would be with no assist switched on, or the assist has taken
    ///      away the shot the rifle exists for.
    ///   2. Hands off, it follows — slowly enough that a target who breaks pace gets away.
    ///   3. Lost is lost. Once the target is outside the break cone, dead, or behind something,
    ///      the lock is spent until the scope comes down and goes up again.
    /// </summary>
    void StepScopeLock(View v, Pawn self, Vector2 look, float dt)
    {
        var locked = v.ScopeLock;

        // Still a legal thing to be locked to?
        if (locked != null && (!locked.Alive || locked == self)) locked = null;

        // Where the lock is, relative to where the player is looking.
        float offYaw = 0f, offPitch = 0f, angle = 0f;
        if (locked != null && !OffsetTo(v, self, locked, out offYaw, out offPitch, out angle))
            locked = null;

        // Broken by distance, by them moving, or by the player deliberately aiming elsewhere.
        // All three are the same test, which is the point: the game does not need to know which
        // of you moved, only that the crosshair and the target are no longer together.
        if (locked != null && angle > ScopeBreakCone) locked = null;

        if (locked == null && v.ScopeLock != null)
        {
            v.ScopeLock = null;
            v.ScopeSnapping = 0f;
            v.ScopeHeld = 0f;
            v.ScopeLockSpent = true;
        }

        // Acquire, once, on the frame the scope comes up. Not continuously: a lock that
        // re-acquires whenever the crosshair drifts near somebody is the turret this replaced.
        if (v.ScopeLock == null && !v.ScopeLockSpent)
        {
            var found = BestScopeTarget(v, self);
            if (found != null)
            {
                v.ScopeLock = found;
                v.ScopeSnapping = ScopeSnapTime;
                v.ScopeHeld = 0f;
            }
            else
            {
                // Nobody in the cone when the scope came up. The scope is yours for this look;
                // wait for it to come down rather than watching for somebody to wander in.
                v.ScopeLockSpent = true;
            }

            return;
        }

        if (v.ScopeLock == null) return;

        // Rule 1. Steering cancels the swing outright rather than pausing it — a snap that
        // resumes after you stop pushing would drag you back off the head you just climbed to.
        if (look.Length() >= ScopeOverride)
        {
            v.ScopeSnapping = 0f;
            return;
        }

        v.ScopeHeld += dt;

        float rate;
        if (v.ScopeSnapping > 0f)
        {
            v.ScopeSnapping -= dt;
            rate = ScopeSnapRate;
        }
        else rate = ScopeTrack * ScopeGrip(v.ScopeHeld);

        if (rate <= 0f) return;

        float step = rate * dt;
        v.Yaw = MathU.MoveAngleToward(v.Yaw, v.Yaw + offYaw, step);
        v.Pitch = MathU.Clamp(v.Pitch + MathU.Clamp(offPitch, -step, step),
                              -Pawn.MaxPitch, Pawn.MaxPitch);
    }

    /// <summary>
    /// How much of the tracking rate is left after holding a lock for this long: 1 then 0.
    /// </summary>
    static float ScopeGrip(float held)
        => held <= ScopeHoldFull ? 1f
         : 1f - MathU.Clamp01((held - ScopeHoldFull) / ScopeHoldFade);

    /// <summary>
    /// Reset the lock when the scope comes down, so raising it again is a fresh acquisition.
    ///
    /// Called every frame from the view update rather than hooked to a key, because the scope can
    /// also drop for reasons the player did not ask for — dying, being knocked out of it, swapping
    /// weapons — and every one of those should hand the next look back clean.
    /// </summary>
    static void TrackScopeState(View v, Pawn self)
    {
        bool scoped = self.Alive && self.Ads && self.Weapon.HasScope;

        if (!scoped && v.WasScoped)
        {
            v.ScopeLock = null;
            v.ScopeSnapping = 0f;
            v.ScopeHeld = 0f;
            v.ScopeLockSpent = false;
        }

        v.WasScoped = scoped;
    }

    /// <summary>
    /// Where a specific pawn sits relative to the crosshair. False when it cannot be shot at.
    /// </summary>
    bool OffsetTo(View v, Pawn self, Pawn other, out float offYaw, out float offPitch,
                  out float angle)
    {
        offYaw = offPitch = angle = 0f;

        // The chest, matching BestAssistTarget, so the lock holds where a shot would land rather
        // than at the feet. The climb to the head is then the player's, which is the whole point.
        Vector3 at = other.GlobalPosition + Vector3.Up * (other.CurrentHeight * 0.55f);
        Vector3 to = at - self.Eye;

        float dist = to.Length();
        if (dist < 0.5f || dist > AssistRange) return false;
        if (!match.HasLineOfSight(self, other)) return false;

        float aimPitch = MathU.Clamp(v.Pitch + v.Recoil, -Pawn.MaxPitch, Pawn.MaxPitch);
        float cp = MathF.Cos(aimPitch);
        var forward = new Vector3(MathF.Cos(v.Yaw) * cp, MathF.Sin(aimPitch), MathF.Sin(v.Yaw) * cp);

        to /= dist;
        angle = MathF.Acos(MathU.Clamp(forward.Dot(to), -1f, 1f));

        offYaw = MathU.AngleDiff(MathF.Atan2(to.Z, to.X), v.Yaw);
        offPitch = MathF.Asin(MathU.Clamp(to.Y, -1f, 1f)) - aimPitch;
        return true;
    }

    /// <summary>Whoever the scope should grab as it comes up, or null for nobody near enough.</summary>
    Pawn? BestScopeTarget(View v, Pawn self)
    {
        Pawn? best = null;
        float bestAngle = ScopeAcquireCone;

        foreach (var other in match.Pawns)
        {
            if (other == self || !other.Alive) continue;
            if (settings.Def.Teams && Match.SameTeam(self, other)) continue;
            if (!OffsetTo(v, self, other, out _, out _, out float angle)) continue;
            if (angle >= bestAngle) continue;

            bestAngle = angle;
            best = other;
        }

        return best;
    }

    /// <summary>
    /// The enemy nearest the crosshair, and how far off it is. Null when nobody qualifies.
    ///
    /// Line of sight is required, so the crosshair never sticks to someone through a wall — which
    /// would both feel wrong and quietly tell you where people are.
    /// </summary>
    (Pawn? Target, float Yaw, float Pitch, float Angle) BestAssistTarget(View v, Pawn self)
    {
        Pawn? best = null;
        float bestAngle = AssistCone, bestYaw = 0f, bestPitch = 0f;

        float aimPitch = MathU.Clamp(v.Pitch + v.Recoil, -Pawn.MaxPitch, Pawn.MaxPitch);
        float cp = MathF.Cos(aimPitch);
        var forward = new Vector3(MathF.Cos(v.Yaw) * cp, MathF.Sin(aimPitch), MathF.Sin(v.Yaw) * cp);

        foreach (var other in match.Pawns)
        {
            if (other == self || !other.Alive) continue;
            if (settings.Def.Teams && Match.SameTeam(self, other)) continue;

            // Aimed at the chest rather than the origin, so the assist pulls where a shot lands.
            Vector3 at = other.GlobalPosition + Vector3.Up * (other.CurrentHeight * 0.55f);
            Vector3 to = at - self.Eye;

            float dist = to.Length();
            if (dist > AssistRange || dist < 0.5f) continue;

            to /= dist;
            float angle = MathF.Acos(MathU.Clamp(forward.Dot(to), -1f, 1f));
            if (angle >= bestAngle) continue;

            if (!match.HasLineOfSight(self, other)) continue;

            bestAngle = angle;
            best = other;
            bestYaw = MathU.AngleDiff(MathF.Atan2(to.Z, to.X), v.Yaw);
            bestPitch = MathF.Asin(MathU.Clamp(to.Y, -1f, 1f)) - aimPitch;
        }

        return (best, bestYaw, bestPitch, bestAngle);
    }

    /// <summary>Kept off the geometry, so the chase camera never ends up behind a wall.</summary>
    const float CameraSkin = 0.55f;

    /// <summary>Shortest the boom may get, as a fraction. Below this the view is inside the hull.</summary>
    const float MinBoom = 0.3f;

    /// <summary>
    /// How fast the boom extends again once the way is clear, in fractions per second.
    ///
    /// Deliberately slow, and deliberately *only* applied outward. Pulling in has to be immediate —
    /// a camera easing into a wall spends several frames inside it, which is the fault the pull-in
    /// exists to prevent. Easing back out is what stops a doorway or a passing crate snapping the
    /// view in and out twice a second.
    /// </summary>
    const float BoomReturnRate = 1.1f;

    /// <summary>
    /// How far behind the head the on-foot chase camera sits, in metres.
    ///
    /// Short. A pawn is under two metres and the arenas are full of two-metre doorways, so a long
    /// boom spends most of a match pulled in against something anyway - and a camera that is
    /// constantly retracting reads worse than one that was never far out.
    /// </summary>
    const float FootBoomLength = 4.2f;

    /// <summary>
    /// Where the chase camera actually sits: along its own boom, at a length that changes smoothly.
    ///
    /// The old version had three separate ways of teleporting the view, and driving near a wall hit
    /// all three at once. It picked a clear position each frame with no memory, so the length
    /// jittered frame to frame; it had a branch that gave up on the boom entirely and jumped to a
    /// point nine metres overhead whenever more than half of it was blocked; and it snapped rather
    /// than eased whenever the new position was more than four metres from the old one — which the
    /// overhead branch guaranteed. That is the flipping.
    ///
    /// There is one continuous quantity now: how far out the boom is. It shortens instantly when
    /// something gets in the way and lengthens slowly when the way clears, and the camera rides a
    /// little higher the shorter it gets, so a wall directly behind you tips the view over the hull
    /// instead of into it. No branch, nothing to snap between.
    /// </summary>
    Vector3 ChaseCameraSpot(View v, Vehicle ride, Vector3 focus, Vector3 back, float dt)
    {
        Vector3 want = focus + back * (ride.Def.HalfExtents.Length() + 6f) + Vector3.Up * 3f;

        float clear = MathF.Max(
            ClearFraction(focus, want, ride.GetRid(), ride.Driver!.GetRid()), MinBoom);

        v.Boom = clear < v.Boom ? clear : Mathf.MoveToward(v.Boom, clear, BoomReturnRate * dt);

        // Rising as it shortens. Continuous, so there is no threshold to flip across.
        float rise = (1f - v.Boom) * 5.5f;

        return focus + (want - focus) * v.Boom + Vector3.Up * rise;
    }

    /// <summary>
    /// How far along the boom the camera can sit before something is in the way, as a fraction.
    ///
    /// Sweeping a sphere rather than casting a ray: a ray threads gaps the camera's near plane
    /// cannot, which shows up as the wall flickering in and out of view rather than as a clean
    /// pull-in.
    /// </summary>
    float ClearFraction(Vector3 focus, Vector3 want, params Rid[] ignore)
    {
        var space = match.GetWorld3D().DirectSpaceState;

        using var query = new PhysicsShapeQueryParameters3D
        {
            Shape = new SphereShape3D { Radius = CameraSkin },
            Transform = new Transform3D(Basis.Identity, focus),
            Motion = want - focus,

            // Whatever the camera belongs to is behind it, not in front of it — colliding with
            // the thing you are looking at would jam the boom at zero length.
            Exclude = new Godot.Collections.Array<Rid>(ignore),
        };

        // CastMotion returns {safe, unsafe} as fractions of the motion. Backed off slightly from
        // the contact point so the near plane clears the surface rather than grazing it.
        float[] hit = space.CastMotion(query);
        if (hit.Length < 2) return 1f;

        return MathU.Clamp01(hit[0] - 0.06f);
    }

    /// <summary>
    /// Whether this is a scene rather than a fight, and therefore has exactly one player in it.
    ///
    /// The distinction earns its keep in <see cref="ReadControls"/>: with one pawn and one player
    /// there is nothing to arbitrate, so there is no reason to insist on knowing which device the
    /// player meant to use.
    /// </summary>
    bool SoloScene => Mission != null;

    /// <summary>
    /// Everything a pawn needs from a person this frame, from one device or from all of them.
    ///
    /// A versus match has to know whose stick is whose — four people share a screen and a pawn
    /// that answered to any of them would be unplayable. A scene has one player, so binding it to
    /// a specific device buys nothing and costs everything: story mode has now been unplayable
    /// twice for two different reasons, both of them "the pawn is listening to a device that is
    /// not the one in your hands".
    ///
    /// The first was the keyboard being registered before any gamepad. The second could not be
    /// reproduced from here at all - the simulation walks perfectly when driven directly, so the
    /// fault was always in this last hop - and the list of ways it can go wrong is longer than the
    /// list of ways it can go right: two keyboard schemes where only one has your movement keys, a
    /// menu navigated with the mouse, a pad plugged in after the act began, a device id that no
    /// longer resolves.
    ///
    /// So in a scene, every connected device drives the one pawn. There is no wrong answer to give
    /// because there is no longer a question being asked.
    /// </summary>
    readonly struct Controls
    {
        public readonly Vector2 Move, Look;
        public readonly bool Attack, Ads, Dash, Melee, ClassAbility, Special;
        public readonly bool Jump, JumpHeld, Sprint, Crouch, CrouchPressed;
        public readonly bool Use, UseHeld, Swap, Start, CameraToggle;

        public Controls(InputDevice d)
        {
            Move = d.Move; Look = d.Look;
            Attack = d.AttackHeld; Ads = d.AdsHeld;
            // Gameplay presses come off the latched edges, not the per-poll ones. This struct is
            // built during the physics step, which runs on a different clock from the polling, so
            // a plain edge is only sometimes still standing when the match asks. Start stays on
            // the plain edge: pausing is handled up in the render frame, where that edge lives.
            Dash = d.DashLatched; Melee = d.MeleeLatched;
            ClassAbility = d.ClassAbilityLatched; Special = d.SpecialLatched;
            Jump = d.JumpLatched; JumpHeld = d.JumpHeld;
            Sprint = d.SprintHeld; Crouch = d.CrouchHeld; CrouchPressed = d.CrouchLatched;
            Use = d.UseLatched; UseHeld = d.UseHeld; Swap = d.SwapLatched; Start = d.StartPressed;
            CameraToggle = d.CameraTogglePressed;   // see InputDevice: not latched, on purpose
        }

        Controls(Controls a, Controls b)
        {
            // Sticks merge by whichever is pushed further, rather than by adding. Adding lets an
            // idle device with a drifting stick fight a deliberate one, and two devices pushed in
            // opposite directions would cancel to nothing instead of one of them winning.
            Move = b.Move.LengthSquared() > a.Move.LengthSquared() ? b.Move : a.Move;
            Look = b.Look.LengthSquared() > a.Look.LengthSquared() ? b.Look : a.Look;

            Attack = a.Attack | b.Attack; Ads = a.Ads | b.Ads;
            Dash = a.Dash | b.Dash; Melee = a.Melee | b.Melee;
            ClassAbility = a.ClassAbility | b.ClassAbility; Special = a.Special | b.Special;
            Jump = a.Jump | b.Jump; JumpHeld = a.JumpHeld | b.JumpHeld;
            Sprint = a.Sprint | b.Sprint; Crouch = a.Crouch | b.Crouch;
            CrouchPressed = a.CrouchPressed | b.CrouchPressed;
            Use = a.Use | b.Use; UseHeld = a.UseHeld | b.UseHeld;
            Swap = a.Swap | b.Swap; Start = a.Start | b.Start;
            CameraToggle = a.CameraToggle | b.CameraToggle;
        }

        public static Controls Merge(Controls a, Controls b) => new(a, b);
    }

    /// <summary>What is being asked of this view's pawn this frame. See <see cref="Controls"/>.</summary>
    Controls ReadControls(View v)
    {
        if (!SoloScene)
            return v.Device is { } bound ? new Controls(bound) : default;

        var all = default(Controls);
        foreach (var d in Devices.All)
            if (d.Connected)
                all = Controls.Merge(all, new Controls(d));

        return all;
    }

    /// <summary>
    /// A device to rumble and to drive the spawn cards with.
    ///
    /// Only ever used for things that need a specific piece of hardware rather than an intent.
    /// In a scene it falls back to anything connected, so a lost device id cannot strand a player.
    /// </summary>
    InputDevice? DeviceFor(View v)
    {
        if (v.Device is { Connected: true } bound) return bound;
        if (!SoloScene) return v.Device;

        foreach (var d in Devices.All) if (d.Connected && d.IsGamepad) return d;
        foreach (var d in Devices.All) if (d.Connected) return d;
        return null;
    }

    PawnInput ResolveInput(int pawnIndex)
    {
        var v = views.Find(x => x.PawnIndex == pawnIndex);
        if (v == null) return default;

        var c = ReadControls(v);
        var input = new PawnInput();

        // Movement is relative to the view, so "up" always means "away from the camera".
        float yaw = v.Yaw;
        var forward = new Vector2(MathF.Cos(yaw), MathF.Sin(yaw));
        var right = new Vector2(-forward.Y, forward.X);

        input.Move = MathU.ClampLen(forward * -c.Move.Y + right * c.Move.X, 1f);
        input.RawMove = c.Move;
        input.RawLook = c.Look;

        // First person: you always shoot where you are looking, whatever the device — recoil
        // included, so a climbing view really does throw your shots high.
        input.Aim = forward;
        input.Pitch = MathU.Clamp(v.Pitch + v.Recoil, -Pawn.MaxPitch, Pawn.MaxPitch);

        input.Fire = c.Attack;
        input.Dash = c.Dash;
        input.Melee = c.Melee;
        input.ClassAbility = c.ClassAbility;
        input.Special = c.Special;
        input.Ads = c.Ads;
        input.Jump = c.Jump;
        input.JumpHeld = c.JumpHeld;
        input.Sprint = c.Sprint;
        input.Crouch = c.Crouch;
        input.CrouchPressed = c.CrouchPressed;
        input.Use = c.Use;
        input.UseHeld = c.UseHeld;
        input.SwapWeapon = c.Swap;
        return input;
    }

    /// <summary>
    /// How far behind and above the pawn the camera sits. High enough to see over the cover you
    /// are standing behind, back far enough that the pawn does not eat the middle of the screen.
    /// </summary>
    void UpdateCameras(float dt)
    {
        foreach (var v in views)
        {
            var pawn = match.Pawns[v.PawnIndex];

            // Each trigger pull kicks the view up, then it settles back to where you were aiming.
            // Aim genuinely moves while it lasts, so sustained fire climbs and has to be ridden —
            // but it recovers on its own rather than leaving you permanently off target.
            if (pawn.ShotCounter != v.LastShotSeen)
            {
                v.Recoil += pawn.Weapon.Recoil * (pawn.ShotCounter - v.LastShotSeen);
                v.LastShotSeen = pawn.ShotCounter;
            }

            v.Recoil = Mathf.MoveToward(v.Recoil, 0f, RecoilRecovery * dt);
            v.Recoil = MathU.Clamp(v.Recoil, 0f, 0.35f);

            float aimPitch = MathU.Clamp(v.Pitch + v.Recoil, -Pawn.MaxPitch, Pawn.MaxPitch);

            // Snapped to the eye, never damped. Camera lag behind where you are looking is
            // tolerable in third person and nauseating in first. Eye height follows the stance,
            // so crouching and sliding genuinely lower the view.
            //
            // A rider sits in the vehicle's seat, not at their own eye height.
            //
            // This is the whole of "when I leave the tank, it keeps putting me under the tank",
            // and it was never about leaving. `RideAlong` parks the pawn at the hull's *origin*,
            // which is its base — so a driver's eye was 1.25m above the floor of a vehicle 2.4m
            // tall. You spent the entire drive inside the hull looking out through its back faces,
            // which is exactly what being under a tank looks like. `Vehicle.Seat` and
            // `VehicleDef.EyeHeight` existed the whole time; nothing had ever read them.
            //
            // Lifted above the seat itself. `Seat` is the turret ring, which on the tank is exactly
            // the roof plane, and a camera sitting on it looks along four metres of its own hull:
            // measured at about forty percent of the screen filled with the vehicle you are driving.
            // At 1.2m over the ring the hull reads as yours without being the view.
            Vector3 eye = pawn.GlobalPosition + Vector3.Up * pawn.CurrentEyeHeight;

            float cp = MathF.Cos(aimPitch);
            var forward = new Vector3(MathF.Cos(v.Yaw) * cp, MathF.Sin(aimPitch), MathF.Sin(v.Yaw) * cp);

            if (!pawn.Alive)
            {
                // Dead: the view sinks to the ground and rolls back to stare at the sky, which is
                // both the conventional read for "you died" and a moment to see who got you.
                float t = pawn.FallProgress;
                eye = pawn.GlobalPosition + Vector3.Up * Mathf.Lerp(pawn.CurrentEyeHeight, 0.35f, t);

                float deadPitch = Mathf.Lerp(aimPitch, 1.35f, Mathf.SmoothStep(0f, 1f, t));
                float dcp = MathF.Cos(deadPitch);
                forward = new Vector3(MathF.Cos(v.Yaw) * dcp, MathF.Sin(deadPitch), MathF.Sin(v.Yaw) * dcp);
            }

            // Driving: the vehicle owns the view. Chase camera rather than first person, because a
            // hull four metres wide cannot be flown or parked from inside its own cockpit.
            if (pawn.Riding is { } ride)
            {
                // The camera looks where the gun points, not where the hull points. On a turreted
                // vehicle those are different, and following the hull would mean aiming blind.
                var rfwd = ride.AimDir;

                Vector3 focus = ride.GlobalPosition + Vector3.Up * ride.Def.EyeHeight;

                // Set outright rather than lerped toward. All the smoothing that matters now lives
                // in the boom length; lerping the world position on top of it meant the camera was
                // also chasing a point that swings as the turret traverses, which is lag on the one
                // axis you are actively aiming with.
                v.Camera.GlobalPosition = ChaseCameraSpot(v, ride, focus, -rfwd, dt);
                v.Camera.LookAt(focus + rfwd * 6f, Vector3.Up);
                v.Camera.Fov = Mathf.Lerp(v.Camera.Fov, BaseFov + 8f, 1f - MathF.Exp(-8f * dt));

                // Keep the on-foot yaw glued to the hull, so stepping out leaves you facing the way
                // you were driving instead of wherever the stick happened to be left pointing.
                v.Yaw = ride.Facing;


                if (pawn.ViewModel != null) pawn.ViewModel.Visible = false;
                continue;
            }

            if (v.ThirdPerson && pawn.Alive)
            {
                // Over the shoulder, on the same boom machinery the vehicle camera uses: it pulls
                // in hard against anything behind you and eases back out, which is what keeps a
                // chase camera from clipping through walls in rooms this tight.
                //
                // Aim is unchanged - the pawn still faces and shoots along v.Yaw and the pitch
                // above. The camera moved; where you are pointing did not. That matters, because
                // every weapon in the game fires from the pawn's eye along its aim, not from the
                // camera, so a shot goes where the crosshair is in both modes.
                Vector3 focus = pawn.GlobalPosition + Vector3.Up * (pawn.CurrentEyeHeight + 0.25f);
                Vector3 want = focus - forward * FootBoomLength + Vector3.Up * 0.65f;

                float clear = MathF.Max(ClearFraction(focus, want, pawn.GetRid()), MinBoom);
                v.FootBoom = clear < v.FootBoom
                    ? clear
                    : Mathf.MoveToward(v.FootBoom, clear, BoomReturnRate * dt);

                v.Camera.GlobalPosition = focus + (want - focus) * v.FootBoom;
                v.Camera.LookAt(focus + forward * 8f, Vector3.Up);
            }
            else
            {
                v.Camera.GlobalPosition = eye;
                v.Camera.LookAt(eye + forward, Vector3.Up);
            }

            // Field of view answers to the stance: aiming pulls in to the class's sight picture,
            // Focus pulls in further still. Eased rather than snapped, so raising a scope reads as
            // a movement rather than a jump cut.
            float wantFov = BaseFov;
            if (pawn.Ads) wantFov = pawn.Weapon.AdsFov;
            if (pawn.Focused) wantFov = MathF.Min(wantFov, 30f);
            if (!pawn.Alive) wantFov = BaseFov;

            v.Camera.Fov = Mathf.Lerp(v.Camera.Fov, wantFov, 1f - MathF.Exp(-13f * dt));

            // The view model rides the camera exactly, since the camera carries pitch and the
            // pawn body only carries yaw. It is hidden behind a scope, where a rifle across the
            // screen would cover the sight picture.
            if (pawn.ViewModel != null)
            {
                pawn.ViewModel.GlobalTransform = v.Camera.GlobalTransform * MeleeSwingOffset(pawn);

                // Hidden behind a scope, where a rifle across the screen would cover the sight
                // picture — but never during a swing, which is the one thing melee has to show.
                bool scopedAway = pawn.Ads && pawn.Weapon.HasScope && pawn.MeleeSwing <= 0f;

                // The third-person cull mask already hides every view model from this camera, so
                // this is belt and braces - but it also keeps the node from being drawn into any
                // other player's viewport during splitscreen.
                pawn.ViewModel.Visible = pawn.Alive && !scopedAway && !v.ThirdPerson;
            }

            // In first person the view *is* the aim, so the pawn is kept in lockstep with it
            // rather than the other way round.
            if (pawn.Alive)
            {
                pawn.Facing = v.Yaw;
                pawn.Pitch = aimPitch;
            }
        }
    }

    /// <summary>
    /// The juggernaut, from this player's point of view: either you are it, or you need to find it.
    ///
    /// A waypoint is not a nicety here. Twelve fighters on fifty-seven thousand square metres means
    /// a juggernaut nobody can locate is hide and seek, and the mode dies of it.
    /// </summary>
    void DrawCrownState(UiPainter p, View v, Pawn pawn, float x, float y)
    {
        if (settings.Mode != GameMode.Juggernaut) return;
        if (match.Juggernaut is not { } king || !pawn.Alive) return;

        if (king == pawn)
        {
            var mine = pawn.Crown!;

            // The power, first and largest. It is the reason the crown is worth taking, and a
            // juggernaut who does not know they are holding one is a juggernaut playing a health
            // bar.
            string power = pawn.CrownPowerActive
                ? $"{mine.PowerName} — {pawn.CrownPowerTime:0.0}s"
                : pawn.CrownPowerReady
                    ? $"{Glyphs.For(Prompt.Special, v.Device)}: {mine.PowerName} — {mine.PowerBlurb}"
                    : $"{mine.PowerName} ready in {pawn.CrownPowerFrac * mine.PowerCooldown:0.0}s";

            var psize = p.Measure(power, 17);
            p.Rect(x - 6f, y - 100f, psize.X + 12f, psize.Y + 8f, new Color(0.04f, 0.05f, 0.07f, 0.78f));
            p.Text(power, x, y - 96f, 17,
                   pawn.CrownPowerActive ? Pal.Warn : pawn.CrownPowerReady ? Pal.Ready : Pal.TextDim);

            string line = $"YOU ARE {mine.Name} — {mine.Blurb}";
            var size = p.Measure(line, 17);

            p.Rect(x - 6f, y - 76f, size.X + 12f, size.Y + 8f, new Color(0.04f, 0.05f, 0.07f, 0.72f));
            p.Text(line, x, y - 72f, 17, pawn.Faction.Tint);

            // The blade and the block. A juggernaut whose gun has silently been taken away needs
            // telling what replaced it, and the block in particular is not discoverable — the
            // button that used to raise a scope now raises a sword, and nothing about the crown
            // says so.
            string blade = pawn.Blocking
                ? "BLOCKING — shots from the front go back where they came from"
                : $"{Glyphs.For(Prompt.Ads, v.Device)}: block — turn fire back on whoever sent it";

            var bsize = p.Measure(blade, 15);
            p.Rect(x - 6f, y - 52f, bsize.X + 12f, bsize.Y + 8f, new Color(0.04f, 0.05f, 0.07f, 0.72f));
            p.Text(blade, x, y - 48f, 15, pawn.Blocking ? Pal.Ready : Pal.TextDim);

            // Scheherazade's clock, and only hers. A bar nobody else has needs saying out loud.
            if (pawn.NightsLeft > 0f)
                p.Text($"{pawn.NightsLeft:0.0}s of story left — every kill buys more",
                       x, y - 120f, 15, pawn.NightsLeft < 4f ? Pal.Danger : Pal.Warn);

            return;
        }

        // Everyone else gets a marker on them, wherever they are.
        var cam = v.Camera;
        Vector3 head = king.GlobalPosition + Vector3.Up * (king.CurrentHeight + 0.9f);

        var r = v.Rect;
        Vector2 at;

        if (cam.IsPositionBehind(head))
        {
            // Behind you: pinned to the edge on the side it is actually on, rather than vanishing.
            Vector3 local = cam.GlobalTransform.AffineInverse() * head;
            at = new Vector2(local.X < 0f ? r.Position.X + 40f : r.Position.X + r.Size.X - 40f,
                             r.Position.Y + r.Size.Y * 0.5f);
        }
        else
        {
            Vector2 screen = cam.UnprojectPosition(head);
            at = new Vector2(
                Mathf.Clamp(r.Position.X + screen.X, r.Position.X + 40f, r.Position.X + r.Size.X - 40f),
                Mathf.Clamp(r.Position.Y + screen.Y, r.Position.Y + 40f, r.Position.Y + r.Size.Y - 60f));
        }

        var tint = king.Faction.Tint;

        // A diamond rather than a dot: it reads at a glance against arena geometry that is all
        // squares, and it does not look like a health pickup.
        p.Rect(at.X - 9f, at.Y - 2f, 18f, 4f, tint);
        p.Rect(at.X - 2f, at.Y - 9f, 4f, 18f, tint);

        p.TextCentered(king.Crown!.Name, at.X, at.Y + 14f, 15, tint);

        float dist = pawn.GlobalPosition.DistanceTo(king.GlobalPosition);
        p.TextCentered($"{dist:0}m", at.X, at.Y + 32f, 13, Pal.TextDim);
    }

    /// <summary>
    /// One teaching line on a dark scrim, returning the y for the next one.
    ///
    /// The arenas are pale and the sky is paler, so grey type over either is unreadable at exactly
    /// the moment it is meant to be teaching someone something.
    /// </summary>
    static float DrawTip(UiPainter p, string tip, float x, float y)
    {
        var size = p.Measure(tip, 15);
        p.Rect(x - 6f, y - 4f, size.X + 12f, size.Y + 8f, new Color(0.04f, 0.05f, 0.07f, 0.68f));
        p.Text(tip, x, y, 15, Pal.Text);
        return y + size.Y + 8f;
    }

    /// <summary>
    /// The view model's offset from the camera during a melee swing.
    ///
    /// A single out-and-back arc: the weapon comes across the screen, right to left and slightly
    /// up, then returns. It is the only feedback a swing that misses gets, which is why it plays
    /// even when the strike connects with nothing.
    /// </summary>
    static Transform3D MeleeSwingOffset(Pawn pawn)
    {
        if (pawn.MeleeSwing <= 0f) return Transform3D.Identity;

        float t = 1f - pawn.MeleeSwing / Pawn.MeleeSwingTime;   // 0 at the start, 1 at the end
        float a = MathF.Sin(t * MathF.PI);                      // out and back

        var basis = new Basis(Vector3.Up, a * 0.85f) * new Basis(Vector3.Forward, -a * 0.75f);
        return new Transform3D(basis, new Vector3(a * 0.28f, -a * 0.05f, a * 0.22f));
    }

    // ---- HUD ----

    public override void Draw(UiPainter p)
    {
        if (match == null) return;

        // The combat HUD is the match's, not the game's, and a scene may want none of it. Checked
        // before it is drawn rather than after, which is where this was and it was wrong: Act I
        // was played with a health bar, a jetpack gauge and a minimap over a childhood.
        if (Mission is not { } scene || scene.ShowsCombatHud)
            foreach (var v in views) DrawPlayerHud(p, v);

        // And a scene has no score, no kill feed and no intermission either.
        if (Mission != null) { DrawMission(p); return; }

        DrawScoreboard(p);
        DrawKillFeed(p);
        DrawIntermission(p);
    }

    /// <summary>
    /// The scene's objective and whatever is being said, over the whole screen.
    ///
    /// Drawn once rather than per view. Story mode is one player by construction, and a dialogue
    /// panel repeated into four splitscreen quarters is a thing nobody would ever want to look at.
    /// </summary>
    void DrawMission(UiPainter p)
    {
        var mission = Mission!;

        if (mission.Objective.Length > 0)
            p.TextCentered(mission.Objective.ToUpperInvariant(), p.Size.X * 0.5f,
                           p.Size.Y * 0.11f, 22, Pal.TextDim);

        // The commit meter, under the objective. Above the dialogue panel rather than beside it,
        // because the two are never fighting for the same attention: the room stops talking before
        // it asks him to choose.
        if (mission.Gauge is { } gauge)
        {
            float gw = 320f;
            float gx = p.Size.X * 0.5f - gw * 0.5f;
            float gy = p.Size.Y * 0.11f + 34f;

            p.TextCentered(gauge.Label.ToUpperInvariant(), p.Size.X * 0.5f, gy, 17, gauge.Tint);

            p.Rect(gx, gy + 26f, gw, 8f, Pal.Panel);
            p.Rect(gx, gy + 26f, gw * gauge.Progress, 8f, gauge.Tint);
        }

        if (mission.Speaking is not { } beat) return;

        float w = MathF.Min(940f, p.Size.X - 140f);
        float x = p.Size.X * 0.5f - w * 0.5f;
        float y = p.Size.Y - 226f;

        var tint = Scripts.TintOf(beat.Who);
        p.Panel(x, y, w, 150f, Pal.Panel * new Color(1, 1, 1, 0.92f),
                tint * new Color(1, 1, 1, 0.55f));

        string name = Scripts.NameOf(beat.Who);
        if (name.Length > 0) p.Text(name, x + 26f, y + 28f, 17, tint);

        float ty = y + (name.Length > 0 ? 62f : 44f);
        foreach (string line in WrapLine(p, beat.Line, w - 52f, 21))
        {
            p.Text(line, x + 26f, ty, 21, beat.Who == Speaker.Narrator ? Pal.TextDim : Pal.Text);
            ty += 28f;
        }
    }

    static List<string> WrapLine(UiPainter p, string text, float width, int size)
    {
        var lines = new List<string>();
        string line = "";

        foreach (string word in text.Split(' '))
        {
            string next = line.Length == 0 ? word : line + " " + word;
            if (p.Measure(next, size).X <= width) { line = next; continue; }

            if (line.Length > 0) lines.Add(line);
            line = word;
        }

        if (line.Length > 0) lines.Add(line);
        return lines;
    }

    void DrawPlayerHud(UiPainter p, View v)
    {
        var pawn = match.Pawns[v.PawnIndex];
        var r = v.Rect;
        var tint = pawn.Tint;

        bool scoped = pawn.Ads && pawn.Weapon.HasScope;

        // The scope mask goes down before anything else. It is opaque, so drawing it later would
        // black out the health bars and score sitting underneath.
        if (scoped) DrawScope(p, pawn, r);

        // Border so each player can find their own slice instantly.
        p.RectOutline(r.Position.X, r.Position.Y, r.Size.X, r.Size.Y, tint * new Color(1, 1, 1, 0.5f), 3f);

        DrawMinimap(p, v, pawn, r);
        DrawBattlePoints(p, pawn, r);

        float x = r.Position.X + 22f;

        // Anchored far enough off the bottom for the whole stack to fit under it.
        //
        // Health, three cooldown gauges, the jetpack gauge and two discoverability tips hang below
        // this line, and spacing the gauges out properly made that taller than the old 116 allowed
        // — the bottom row ran off the edge of the viewport. Stated as the sum rather than as a
        // number somebody has to keep in their head.
        float y = r.Position.Y + r.Size.Y - 160f;

        // Width of the health bar. Declared here rather than at the bar itself because the weapon
        // slot column is placed relative to it, and that column is drawn first.
        float bw = MathF.Min(300f, r.Size.X * 0.34f);

        p.Text($"{pawn.Name2}  {pawn.Class.Name}", x, y - 26f, 18, tint);

        DrawCrownState(p, v, pawn, x, y);

        // Carrying the flag changes what you are doing entirely, so it says so where you cannot
        // miss it rather than only on the scoreboard at the top of the screen.
        if (match.FlagCarriedBy(pawn) != null)
        {
            var home = Pal.Teams[Match.TeamOf(pawn.Slot)];
            string line = "YOU HAVE THEIR FLAG — take it to your base";
            var size = p.Measure(line, 17);

            p.Rect(x - 6f, y - 76f, size.X + 12f, size.Y + 8f, new Color(0.04f, 0.05f, 0.07f, 0.72f));
            p.Text(line, x, y - 72f, 17, home);
        }

        // A picked-up gun is named and counted down in its own colour, so it is obvious both that
        // you have it and that it is about to run out.
        // Both slots, with the one in your hands bright and the stowed one dim. Carrying two is
        // only a decision if you can see what the other one is without swapping to find out.
        for (int s = 0; s < 2; s++)
        {
            var w = pawn.SlotWeapon(s);
            if (w == null) continue;

            bool active = s == pawn.ActiveSlot;
            bool classGun = ReferenceEquals(w, pawn.Class.Weapon);

            string ammo = classGun ? "" : $"  ×{pawn.SlotAmmo(s)}";
            Color slotTint = classGun ? Pal.Text : Weapons.TintFor(w);
            if (!active) slotTint = new Color(slotTint.R, slotTint.G, slotTint.B, 0.45f);

            // Clear of the health number, which sits at the end of the health bar. At x+190 the
            // second slot ran straight through it the moment a weapon had a name as long as
            // "Rocket Launcher" — the column was sized for "Rifle" and nothing else.
            p.Text($"{w.Name}{ammo}", x + bw + 56f, y - 26f + s * 20f, active ? 18 : 15, slotTint);
        }

        // The interact hold, shown as it fills. Without it a hold-to-take is indistinguishable
        // from a pickup that is simply not working.
        if (pawn.PickupHold > 0.02f && match.WeaponCrateInReach(pawn))
        {
            float frac = MathU.Clamp01(pawn.PickupHold / Match.PickupHoldTime);
            float cx = r.Position.X + r.Size.X * 0.5f;
            float by = r.Position.Y + r.Size.Y * 0.5f + 46f;

            p.Rect(cx - 44f, by, 88f, 5f, new Color(0.10f, 0.12f, 0.16f, 0.8f));
            p.Rect(cx - 44f, by, 88f * frac, 5f, Pal.Ready);
        }

        // Health.
        p.Rect(x, y, bw, 16f, Pal.PanelHi);
        p.Rect(x, y, bw * pawn.HealthFrac, 16f, pawn.HealthFrac < 0.3f ? Pal.Danger : tint);
        p.Text($"{Mathf.CeilToInt(pawn.Health)}", x + bw + 12f, y - 3f, 18, Pal.Text);

        // Cooldown gauges under health, one row each, every one of them labelled.
        //
        // The rows used to be ten pixels apart with fifteen-point labels beside them, which is
        // about nineteen pixels of text in ten pixels of space — so "Second Wind" and "Frag" were
        // drawn straight through each other, on every viewport, all match. It reads as a smear
        // rather than as two abilities, and a splitscreen capture is where it finally became
        // obvious. Named constants now, so a fourth gauge cannot quietly land on the third.
        const float GaugeStep = 17f;
        const float GaugeTop = 24f;
        const int GaugeLabel = 13;

        float dashY = y + GaugeTop;
        float specialY = dashY + GaugeStep;
        float classY = specialY + GaugeStep;
        float jetY = classY + GaugeStep;

        p.Rect(x, dashY, bw, 6f, Pal.PanelHi);
        p.Rect(x, dashY, bw * (1f - pawn.DashCooldownFrac), 6f,
               pawn.DashCooldownFrac <= 0f ? Pal.Ready : Pal.TextDim);

        p.Rect(x, specialY, bw, 6f, Pal.PanelHi);
        p.Rect(x, specialY, bw * (1f - pawn.SpecialCooldownFrac), 6f,
               pawn.SpecialCooldownFrac <= 0f ? Pal.Accent : Pal.TextDim);

        // Faction and special share this line. The faction used to have a line of its own above the
        // class name, which is a row the stance readout and the timed-special banner already own —
        // so it simply drew on top of them. It belongs next to the special anyway: the special is
        // the only thing faction does.
        p.Text(pawn.Faction.SpecialName, x + bw + 12f, specialY - 5f, GaugeLabel,
               pawn.SpecialCooldownFrac <= 0f ? Pal.Accent : Pal.TextDim);

        p.Text(pawn.Faction.Name,
               x + bw + 12f + p.Measure(pawn.Faction.SpecialName, GaugeLabel).X + 10f,
               specialY - 4f, 12, pawn.Faction.Tint);

        // The class ability gets its own bar under the faction one. Two abilities on two clocks
        // means two gauges — sharing one would leave you guessing which of them was ready.
        p.Rect(x, classY, bw, 6f, Pal.PanelHi);
        p.Rect(x, classY, bw * (1f - pawn.ClassCooldownFrac), 6f,
               pawn.ClassCooldownFrac <= 0f ? Pal.Ready : Pal.TextDim);

        p.Text(pawn.Class.SpecialName, x + bw + 12f, classY - 5f, GaugeLabel,
               pawn.ClassCooldownFrac <= 0f ? Pal.Ready : Pal.TextDim);

        // What the special actually does, spelled out until the player has used it once. A name on
        // its own — "Second Wind" — tells you nothing, and the lobby is a long way back by the time
        // you are wondering what the button on your left bumper is for.
        // Both abilities explain themselves until you have used them. Two abilities on two buttons
        // is exactly twice the discoverability problem that put this line here in the first place.
        if (pawn.Alive)
        {
            float tipY = jetY + GaugeStep;

            if (pawn.SpecialUses == 0)
                tipY = DrawTip(p, $"{Glyphs.For(Prompt.Special, v.Device)}: {pawn.Faction.SpecialBlurb}",
                                x, tipY);

            if (pawn.ClassAbilityUses == 0)
                DrawTip(p, $"{Glyphs.For(Prompt.ClassAbility, v.Device)}: {pawn.Class.SpecialBlurb}",
                        x, tipY);
        }

        // Jetpack fuel, shown only while carrying one — a permanently empty gauge would just be
        // one more bar to ignore.
        if (pawn.HasJetpack)
        {
            var jet = new Color(0.45f, 0.95f, 0.98f);
            p.Rect(x, jetY, bw, 6f, Pal.PanelHi);
            p.Rect(x, jetY, bw * (pawn.JetFuel / Pawn.JetFuelMax), 6f,
                   pawn.Thrusting ? Colors.White : jet);
            p.Text($"JET {pawn.JetFuel:0.0}s", x + bw + 12f, jetY - 5f, GaugeLabel, jet);
        }

        // A timed special needs its remaining duration visible, not just its cooldown.
        if (pawn.BuffTime > 0f)
            p.Text($"{pawn.Faction.SpecialName.ToUpperInvariant()} {pawn.BuffTime:0.0}",
                   x, y - 48f, 20, Pal.Ready);

        p.TextRight($"{pawn.Score}", r.Position.X + r.Size.X - 22f, y - 30f, 40, tint);

        if (!pawn.Alive)
        {
            DrawSpawnScreen(p, v, pawn, r);
            return;
        }

        // A real scope replaces the crosshair; every other gun gets a proper sight picture when
        // aimed, and the loose hip crosshair only when firing from the hip.
        //
        // None of it while driving: the vehicle draws its own reticle at the projected aim point,
        // and the on-foot crosshair sat at screen centre next to it pointing somewhere else. Two
        // crosshairs disagreeing is worse than either alone.
        if (!scoped && !pawn.InVehicle)
        {
            if (pawn.Ads) DrawIronSight(p, pawn, r);
            else DrawCrosshair(p, pawn, r);
        }

        DrawVehiclePrompt(p, v, pawn, r);
        DrawPickupLabels(p, v, r);
        DrawDamageDirection(p, pawn, v, r);
        DrawHeadshotBanner(p, pawn, r);
        DrawKillBanner(p, pawn, r);

        if (pawn.Alive)
        {
            string stance = pawn.Sliding ? "SLIDING"
                          : pawn.Sprinting ? "SPRINT"
                          : pawn.Crouching ? "CROUCH"
                          : pawn.Ads ? "AIMING"
                          : null!;

            if (stance != null)
                p.Text(stance, x, y - 48f, 18, pawn.Sliding ? Pal.Accent : Pal.TextDim);
        }
    }

    /// <summary>
    /// The sight picture for a gun without a scope: a ring dead centre with a front post, and the
    /// hit marker still flaring through it. Every weapon now has something precise at the middle
    /// of the screen when aimed, rather than only the Marksman.
    /// </summary>
    /// <summary>
    /// Either "you can get in this" or "here is what you are driving".
    ///
    /// Without the first half a vehicle is only drivable by someone who already knows the button,
    /// which is not a discoverable game — the hulls were being walked past.
    /// </summary>
    void DrawVehiclePrompt(UiPainter p, View v, Pawn pawn, Rect2 r)
    {
        float cx = r.Position.X + r.Size.X * 0.5f;
        var kind = v.Device?.Kind ?? PadKind.Generic;
        string button = PadBindings.Describe(PadAction.Use, kind);

        if (pawn.Riding is { } ride)
        {
            // The chase camera sits behind and above the hull, so the gun's line does not run
            // through the middle of the screen. Project where the round will actually go rather
            // than parking a reticle at the centre and hoping — a crosshair that lies about where
            // you are aiming is worse than having none, which is what driving had.
            if (ride.Def.Gun != null)
            {
                Vector2 at = v.Camera.UnprojectPosition(ride.Muzzle + ride.AimDir * 70f)
                             + r.Position;

                Color reticle = ride.ReadyToFire ? Pal.Ready : Pal.TextDim;
                float s = 13f;

                p.Rect(at.X - s, at.Y - 1.5f, s - 4f, 3f, reticle);
                p.Rect(at.X + 4f, at.Y - 1.5f, s - 4f, 3f, reticle);
                p.Rect(at.X - 1.5f, at.Y - s, 3f, s - 4f, reticle);
                p.Rect(at.X - 1.5f, at.Y + 4f, 3f, s - 4f, reticle);
                p.Rect(at.X - 1.5f, at.Y - 1.5f, 3f, 3f, reticle);

                // A reload bar under the reticle. With a two-second cannon, "can I fire yet" is
                // the single most useful thing the driving HUD can tell you.
                if (!ride.ReadyToFire)
                {
                    float bar = 42f;
                    p.Rect(at.X - bar * 0.5f, at.Y + 22f, bar, 4f, new Color(0.10f, 0.12f, 0.16f, 0.8f));
                    p.Rect(at.X - bar * 0.5f, at.Y + 22f, bar * ride.ReloadProgress, 4f, Pal.TextDim);
                }

                // Muzzle flash, drawn as a bloom on the reticle rather than in the world: it has to
                // be visible from the chase camera at any angle, and a light in the scene is not.
                if (ride.MuzzleFlash > 0f)
                {
                    float k = ride.MuzzleFlash / Vehicle.MuzzleFlashTime;
                    float rad = 10f + 26f * k;
                    p.Rect(at.X - rad, at.Y - rad * 0.18f, rad * 2f, rad * 0.36f,
                           new Color(1f, 0.85f, 0.45f, 0.55f * k));
                }
            }

            // Hull integrity, not the driver's own health — the thing that decides whether to keep
            // driving or bail out.
            float frac = MathU.Clamp01(ride.Health / ride.Def.Health);
            float w = 190f, h = 9f;
            float y = r.Position.Y + r.Size.Y - 96f;

            p.TextCentered(ride.Def.Name.ToUpperInvariant(), cx, y - 26f, 22, Pal.Text);
            p.Rect(cx - w * 0.5f, y, w, h, new Color(0.10f, 0.12f, 0.16f, 0.75f));
            p.Rect(cx - w * 0.5f, y, w * frac, h, ride.Def.Tint);
            p.RectOutline(cx - w * 0.5f, y, w, h, new Color(0f, 0f, 0f, 0.5f), 1f);
            p.TextCentered($"{button}  EXIT", cx, y + 18f, 17, Pal.TextDim);
            return;
        }

        if (match.NearestBoardable(pawn) is { } near)
            p.TextCentered($"{button}   ENTER {near.Def.Name.ToUpperInvariant()}",
                           cx, r.Position.Y + r.Size.Y - 118f, 21, Pal.Ready);
    }

    static void DrawIronSight(UiPainter p, Pawn pawn, Rect2 r)
    {
        float cx = r.Position.X + r.Size.X * 0.5f;
        float cy = r.Position.Y + r.Size.Y * 0.5f;

        float hit = MathU.Clamp01(pawn.HitConfirm / Pawn.HitConfirmTime);
        var col = hit > 0f
            ? Pal.Danger.Lerp(new Color(0.92f, 0.95f, 1f, 0.92f), 1f - hit)
            : new Color(0.92f, 0.95f, 1f, 0.92f);

        // The ring, drawn as short chords around a circle — the project has no line primitive and
        // no textures, so a ring is built the same way the scope mask is.
        const float Radius = 26f;
        const int Segments = 40;

        for (int i = 0; i < Segments; i++)
        {
            float a = i * MathF.Tau / Segments;
            float sx = cx + MathF.Cos(a) * Radius;
            float sy = cy + MathF.Sin(a) * Radius;
            p.Rect(sx - 1.2f, sy - 1.2f, 2.4f, 2.4f, col);
        }

        // Front post rising from the bottom of the ring to the aim point.
        p.Rect(cx - 1.5f, cy - 1f, 3f, Radius - 4f, col);

        // Fine centre dot: the actual point of aim.
        p.Rect(cx - 1.5f, cy - 1.5f, 3f, 3f, hit > 0f ? Pal.Danger : Pal.Accent);

        // Side ticks, giving the eye something to level the ring against.
        p.Rect(cx - Radius - 12f, cy - 1f, 8f, 2f, col);
        p.Rect(cx + Radius + 4f, cy - 1f, 8f, 2f, col);
    }

    /// <summary>
    /// The Marksman's sight picture: a circular aperture masked by opaque surround. Drawn as a
    /// ring of chords rather than a texture, since the project has no image files — each row of
    /// the screen gets the two rectangles that lie outside the circle at that height.
    /// </summary>
    static void DrawScope(UiPainter p, Pawn pawn, Rect2 r)
    {
        float cx = r.Position.X + r.Size.X * 0.5f;
        float cy = r.Position.Y + r.Size.Y * 0.5f;
        float radius = MathF.Min(r.Size.X, r.Size.Y) * 0.44f;

        var black = new Color(0.02f, 0.025f, 0.035f);
        const float step = 4f;

        for (float yy = r.Position.Y; yy < r.Position.Y + r.Size.Y; yy += step)
        {
            float dy = yy + step * 0.5f - cy;

            // Outside the circle's vertical extent the whole row is masked.
            if (MathF.Abs(dy) >= radius)
            {
                p.Rect(r.Position.X, yy, r.Size.X, step, black);
                continue;
            }

            float half = MathF.Sqrt(radius * radius - dy * dy);
            p.Rect(r.Position.X, yy, cx - half - r.Position.X, step, black);
            p.Rect(cx + half, yy, r.Position.X + r.Size.X - (cx + half), step, black);
        }

        // Reticle: fine crosshair with a centre gap, plus range ticks below.
        var line = new Color(0.85f, 0.90f, 0.95f, 0.85f);
        p.Rect(cx - radius, cy - 0.5f, radius - 16f, 1f, line);
        p.Rect(cx + 16f, cy - 0.5f, radius - 16f, 1f, line);
        p.Rect(cx - 0.5f, cy - radius, 1f, radius - 16f, line);
        p.Rect(cx - 0.5f, cy + 16f, 1f, radius - 16f, line);
        p.Rect(cx - 1.5f, cy - 1.5f, 3f, 3f, Pal.Danger);

        for (int i = 1; i <= 3; i++)
        {
            float ty = cy + 22f * i;
            if (ty > cy + radius - 8f) break;
            p.Rect(cx - 7f, ty, 14f, 1f, line * new Color(1, 1, 1, 0.6f));
        }
    }

    /// <summary>
    /// Names every visible pickup in world space, so you know what a crate is before committing to
    /// the run across the arena for it. Colour alone told you a weapon was there but not which.
    /// </summary>
    void DrawPickupLabels(UiPainter p, View v, Rect2 r)
    {
        var cam = v.Camera;
        var eye = cam.GlobalPosition;

        foreach (var (at, label, tint) in match.AvailablePickups())
        {
            // Behind the camera unprojects to a nonsense point, so cull those first.
            if (cam.IsPositionBehind(at)) continue;

            float dist = eye.DistanceTo(at);
            if (dist > 70f) continue;

            Vector2 screen = cam.UnprojectPosition(at);

            // UnprojectPosition is relative to this camera's viewport, so shift into window space.
            float sx = r.Position.X + screen.X;
            float sy = r.Position.Y + screen.Y;

            if (sx < r.Position.X || sx > r.Position.X + r.Size.X) continue;
            if (sy < r.Position.Y || sy > r.Position.Y + r.Size.Y) continue;

            // Fades with distance rather than vanishing, so a far crate is a hint and a near one
            // is a label.
            float alpha = MathU.Clamp01(1.15f - dist / 70f);
            

            int size = dist < 26f ? 18 : 15;
            var textCol = new Color(tint.R, tint.G, tint.B, alpha);

            var w = p.Measure(label, size);
            p.Rect(sx - w.X * 0.5f - 7f, sy - 3f, w.X + 14f, w.Y + 6f,
                   new Color(0.04f, 0.05f, 0.07f, alpha * 0.6f));

            p.TextCentered(label, sx, sy, size, textCol);

            // A small caret under the label pointing at the crate itself.
            p.Rect(sx - 2f, sy + w.Y + 4f, 4f, 6f, new Color(tint.R, tint.G, tint.B, alpha * 0.8f));
        }
    }


    // ---- the spawn screen ----
    //
    // Dying was two and a half seconds of a red wash and a countdown. It is now the one moment in
    // the match where you decide what kind of fight to have next, which is what makes battle points
    // worth earning and what makes a death feel like a turn rather than a penalty.

    /// <summary>
    /// Move the cursor and take the choice. Runs only while the player is dead.
    ///
    /// The respawn timer is a floor, not a countdown to something automatic: you cannot come back
    /// before it expires, and after that you come back when you press the button. There is still a
    /// grace limit in the match itself, because a player who walks away must not leave their side a
    /// body down for the rest of the round.
    /// </summary>
    void StepSpawnChoice(View v, Pawn pawn, InputDevice d)
    {
        // Once you have confirmed, the choice is made and this screen stops touching it.
        //
        // Without this the screen kept rewriting NextSpawn and clearing HeroBought every frame
        // between the confirmation and the actual spawn — so a confirmed hero was unbought again
        // before the match ever saw it, and nudging the stick after pressing A spawned you as
        // whatever the cursor drifted onto.
        if (pawn.SpawnConfirmed) return;

        var options = Reinforcements.For(pawn.Faction);
        bool heroOffered = match.HeroAvailableTo(pawn);
        int count = options.Count + (heroOffered ? 1 : 0);

        if (d.NavX != 0)
        {
            v.SpawnPick = ((v.SpawnPick + d.NavX) % count + count) % count;
            Sfx.Play(Sound.MenuMove, -8f);
        }

        // Clamped rather than wrapped when the list shrinks under the cursor — the hero row can
        // disappear mid-choice when someone else on your side buys one first.
        if (v.SpawnPick >= count) v.SpawnPick = count - 1;

        StepPostChoice(v, pawn, d);

        bool hero = heroOffered && v.SpawnPick == options.Count;
        var pick = hero ? Reinforcements.Trooper : options[v.SpawnPick];
        int cost = hero ? Reinforcements.HeroCost : pick.Cost;

        pawn.NextSpawn = pick;
        pawn.HeroBought = false;

        if (!d.ConfirmPressed) return;
        if (pawn.RespawnIn > 0f) return;
        if (cost > pawn.BattlePoints) { Sfx.Play(Sound.MenuBack, -6f); return; }

        pawn.HeroBought = hero;
        pawn.SpawnConfirmed = true;
        Sfx.Play(Sound.MenuConfirm, -6f);
    }


    /// <summary>
    /// Choosing which command post to come back at. Dominion only.
    ///
    /// Vertical for *where*, horizontal for *what*, which is the split that makes both fit on one
    /// screen without a mode toggle: the two questions are genuinely independent — any character
    /// can arrive at any post you hold — so they get an axis each rather than taking turns.
    ///
    /// Only posts your side owns are offered. Somewhere you do not hold is not a spawn, it is an
    /// objective, and offering it would be offering something the match would then have to refuse.
    /// </summary>
    void StepPostChoice(View v, Pawn pawn, InputDevice d)
    {
        if (settings.Mode != GameMode.Dominion) { pawn.SpawnPost = -1; return; }

        var mine = OwnedPosts(pawn);

        // Nothing held: the automatic pick takes over, and the panel says so rather than offering
        // an empty list. Losing every post is a real state in this mode and it has to read as one.
        if (mine.Count == 0) { pawn.SpawnPost = -1; return; }

        if (d.NavY != 0)
        {
            v.PostPick = ((v.PostPick + d.NavY) % mine.Count + mine.Count) % mine.Count;
            Sfx.Play(Sound.MenuMove, -8f);
        }

        // The list shrinks under the cursor whenever a post is lost, which in this mode is
        // constantly.
        if (v.PostPick >= mine.Count) v.PostPick = mine.Count - 1;

        pawn.SpawnPost = mine[v.PostPick];
    }

    /// <summary>Indices of the posts this pawn's side currently holds, in map order.</summary>
    List<int> OwnedPosts(Pawn pawn)
    {
        var mine = new List<int>();
        int team = Match.TeamOf(pawn.Slot);

        for (int i = 0; i < match.Posts.Count; i++)
            if (match.Posts[i].Owner == team) mine.Add(i);

        return mine;
    }

    /// <summary>
    /// The front line, drawn as a strip of posts west to east.
    ///
    /// Deliberately not a top-down map. A minimap would have to be read — worked out against an
    /// arena you cannot see from the spawn screen — and the only thing you actually need to decide
    /// is which end of the front to arrive at, which a strip in map order answers directly. The
    /// posts are laid out as a chain on purpose, so a chain is the honest picture of them.
    ///
    /// Every post is shown, not just yours, because "they hold Echo and we hold Alpha" is the shape
    /// of the match; the ones you cannot spawn at are simply not selectable.
    /// </summary>
    void DrawPostStrip(UiPainter p, View v, Pawn pawn, float cx, float y, float width)
    {
        if (settings.Mode != GameMode.Dominion || match.Posts.Count == 0) return;

        var mine = OwnedPosts(pawn);

        if (mine.Count == 0)
        {
            p.TextCentered("YOUR SIDE HOLDS NO POSTS — you come back where you can",
                           cx, y, 15, Pal.Danger);
            return;
        }

        p.TextCentered($"{Glyphs.For(Prompt.NavVert, v.Device?.Kind ?? PadKind.Keyboard)}: "
                       + "choose where you come back", cx, y, 14, Pal.TextDim);

        y += 20f;

        int n = match.Posts.Count;
        float gap = 6f;
        float cellW = MathF.Min(110f, (width - gap * (n - 1)) / n);
        float rowW = cellW * n + gap * (n - 1);
        float x = cx - rowW * 0.5f;

        int chosen = v.PostPick < mine.Count ? mine[v.PostPick] : -1;

        for (int i = 0; i < n; i++)
        {
            var post = match.Posts[i];
            bool ours = post.Owner == Match.TeamOf(pawn.Slot);
            bool selected = i == chosen;

            Color tint = Match.PostTint(post.Owner);

            p.Panel(x, y, cellW, 40f,
                    selected ? new Color(0.13f, 0.17f, 0.20f, 0.94f)
                             : new Color(0.07f, 0.08f, 0.11f, 0.82f),
                    selected ? Pal.Ready : Pal.PanelHi);

            // A bar of the owner's colour along the top, so the front line reads as colour before
            // anybody reads a word of it.
            p.Rect(x, y, cellW, 4f, tint);

            p.TextCentered(post.Name, x + cellW * 0.5f, y + 9f, 13,
                           ours ? Pal.Text : Pal.TextDim);

            string state = post.Contested ? "CONTESTED"
                         : ours ? (selected ? "SPAWN HERE" : "held")
                         : post.Owner < 0 ? "neutral"
                         : "theirs";

            p.TextCentered(state, x + cellW * 0.5f, y + 24f, 11,
                           post.Contested ? Pal.Warn : selected ? Pal.Ready : Pal.TextDim);

            x += cellW + gap;
        }
    }
    /// <summary>
    /// The death screen, which is now a shop.
    ///
    /// Laid out as a row of cards rather than a list, because the choice is between things of
    /// different *kinds* rather than different values — a card can carry a stat line, a price and a
    /// sentence of why, and a list row cannot. The row is ordered by cost, so the thing you cannot
    /// afford yet is always to the right of the thing you can, which makes the bank a progress bar
    /// without needing to draw one.
    /// </summary>
    void DrawSpawnScreen(UiPainter p, View v, Pawn pawn, Rect2 r)
    {
        float cx = r.Position.X + r.Size.X * 0.5f;

        // Below the scoreboard, never behind it. In a full-screen viewport the two both sit at the
        // top of the same screen and the scoreboard is drawn last, so it went straight through the
        // cards. In splitscreen the lower views already start below it and this changes nothing.
        float top = MathF.Max(r.Position.Y + r.Size.Y * 0.10f, ScoreboardBottom() + 18f);

        // Strongest on the instant of death, easing off as the body settles.
        float wash = 0.42f * (1f - pawn.FallProgress) + 0.16f;
        p.Rect(r.Position.X, r.Position.Y, r.Size.X, r.Size.Y,
               new Color(Pal.Danger.R * 0.5f, 0.03f, 0.06f, wash));

        int size = (int)(MathF.Min(r.Size.X, r.Size.Y) * 0.085f);

        p.TextCentered("ELIMINATED", cx + 2f, top + 2f, size, new Color(0f, 0f, 0f, 0.6f));
        p.TextCentered("ELIMINATED", cx, top, size, Pal.Danger);

        float y = top + size + 6f;

        p.TextCentered(pawn.KilledBy.Length > 0 ? $"by {pawn.KilledBy}" : "you fell",
                       cx, y, 20, Pal.Text);
        y += 30f;

        // The bank, large. It is the number the whole screen is about.
        p.TextCentered($"{pawn.BattlePoints} BATTLE POINTS", cx, y, 26,
                       pawn.BattlePoints > 0 ? Pal.Ready : Pal.TextDim);
        y += 40f;

        var options = Reinforcements.For(pawn.Faction);
        bool heroOffered = match.HeroAvailableTo(pawn);
        int count = options.Count + (heroOffered ? 1 : 0);

        // Sized to the viewport, because this has to work in a quarter of a splitscreen as well as
        // full frame.
        float gap = 6f;
        float cardW = MathF.Min(150f, (r.Size.X - 40f - gap * (count - 1)) / count);
        float cardH = MathF.Min(150f, r.Size.Y * 0.34f);
        float rowW = cardW * count + gap * (count - 1);
        float x = cx - rowW * 0.5f;

        for (int i = 0; i < count; i++)
        {
            bool hero = heroOffered && i == options.Count;
            var def = hero ? null : options[i];

            string name = hero ? pawn.Faction.Juggernaut.Name.ToUpperInvariant()
                               : def!.Name.ToUpperInvariant();
            string epithet = hero ? pawn.Faction.Juggernaut.Epithet : def!.Epithet;
            int cost = hero ? Reinforcements.HeroCost : def!.Cost;

            bool selected = i == v.SpawnPick;
            bool afford = cost <= pawn.BattlePoints;

            Color edge = selected ? Pal.Ready : Pal.PanelHi;
            Color body = selected
                ? new Color(0.13f, 0.17f, 0.20f, 0.94f)
                : new Color(0.09f, 0.10f, 0.13f, 0.86f);

            p.Panel(x, y, cardW, cardH, body, edge);

            // Unaffordable cards are drawn dim rather than hidden. Seeing what you are saving for
            // is most of what makes saving feel like anything.
            Color ink = afford ? Pal.Text : Pal.TextDim;
            Color tierTint = hero ? pawn.Faction.Tint
                           : def!.Tier == ReinforcementTier.Basic ? Pal.TextDim
                           : def.Tier == ReinforcementTier.Elite ? Pal.Warn
                           : Pal.Ready;

            p.Text(hero ? "HERO" : def!.Tier.ToString().ToUpperInvariant(),
                   x + 10f, y + 8f, 12, afford ? tierTint : Pal.TextDim);

            p.Text(name, x + 10f, y + 24f, 15, ink);
            p.TextWrapped(epithet, x + 10f, y + 44f, cardW - 20f, 12, Pal.TextDim);

            string price = cost == 0 ? "FREE" : $"{cost}";
            p.Text(price, x + 10f, y + cardH - 24f, 17,
                   cost == 0 ? Pal.TextDim : afford ? Pal.Ready : Pal.Danger);

            x += cardW + gap;
        }

        y += cardH + 12f;

        // What the selected card actually does, under the row rather than on it — a card is too
        // small to carry a sentence, and the sentence is the reason to pick one.
        bool heroPicked = heroOffered && v.SpawnPick == options.Count;
        string blurb = heroPicked
            ? pawn.Faction.Juggernaut.Blurb
            : options[Mathf.Min(v.SpawnPick, options.Count - 1)].Blurb;

        p.TextWrapped(blurb, cx - r.Size.X * 0.35f, y, r.Size.X * 0.7f, 14, Pal.Text);
        y += 30f;

        // Where, under what. Only in Dominion, and it takes the space back when the mode has no
        // posts to offer.
        if (settings.Mode == GameMode.Dominion)
        {
            DrawPostStrip(p, v, pawn, cx, y, r.Size.X * 0.8f);
            y += match.Posts.Count > 0 && OwnedPosts(pawn).Count > 0 ? 74f : 22f;
        }

        if (pawn.SpawnConfirmed)
        {
            p.TextCentered("DEPLOYING", cx, y, 20, Pal.Ready);
            return;
        }

        if (pawn.RespawnIn > 0f)
        {
            p.TextCentered($"Ready in {MathF.Max(0f, pawn.RespawnIn):0.0}", cx, y, 18, Pal.TextDim);
            return;
        }

        int wantCost = heroPicked
            ? Reinforcements.HeroCost
            : options[Mathf.Min(v.SpawnPick, options.Count - 1)].Cost;

        p.TextCentered(
            wantCost > pawn.BattlePoints
                ? $"{wantCost - pawn.BattlePoints} more points needed"
                : $"{Glyphs.For(Prompt.Confirm, v.Device?.Kind ?? PadKind.Keyboard)}: deploy"
                  + $"   ·   {Glyphs.For(Prompt.NavHorz, v.Device?.Kind ?? PadKind.Keyboard)}: choose",
            cx, y, 18,
            wantCost > pawn.BattlePoints ? Pal.Danger : Pal.Ready);
    }

    /// <summary>The killer's confirmation, kept clear of the centre so it never covers your aim.</summary>
    static void DrawKillBanner(UiPainter p, Pawn pawn, Rect2 r)
    {
        if (pawn.KillBanner <= 0f || !pawn.Alive) return;

        float t = MathU.Clamp01(pawn.KillBanner / Match.KillBannerTime);
        float alpha = MathU.Clamp01(t * 4f);

        float cx = r.Position.X + r.Size.X * 0.5f;
        float cy = r.Position.Y + r.Size.Y * 0.20f;

        int size = (int)(MathF.Min(r.Size.X, r.Size.Y) * 0.075f);

        p.TextCentered("ELIMINATED", cx + 2f, cy + 2f, size, new Color(0f, 0f, 0f, alpha * 0.55f));
        p.TextCentered("ELIMINATED", cx, cy, size, new Color(Pal.Ready.R, Pal.Ready.G, Pal.Ready.B, alpha));

        p.TextCentered(pawn.KillBannerName, cx, cy + size + 8f, (int)(size * 0.55f),
                       new Color(Pal.Text.R, Pal.Text.G, Pal.Text.B, alpha));
    }

    /// <summary>The HEADSHOT call-out, sized to be unmissable in a quarter-screen viewport.</summary>
    static void DrawHeadshotBanner(UiPainter p, Pawn pawn, Rect2 r)
    {
        if (pawn.HeadshotBanner <= 0f) return;

        float t = MathU.Clamp01(pawn.HeadshotBanner / Match.HeadshotBannerTime);

        // Punches in quickly then holds, fading only at the very end.
        float pop = 1f + (1f - MathU.Clamp01((1f - t) * 6f)) * 0.25f;
        float alpha = MathU.Clamp01(t * 3f);

        int size = (int)(MathF.Min(r.Size.X, r.Size.Y) * 0.11f * pop);
        float cx = r.Position.X + r.Size.X * 0.5f;
        float cy = r.Position.Y + r.Size.Y * 0.30f;

        var shadow = new Color(0f, 0f, 0f, alpha * 0.55f);
        var face = new Color(Pal.Warn.R, Pal.Warn.G, Pal.Warn.B, alpha);

        p.TextCentered("HEADSHOT", cx + 3f, cy + 3f, size, shadow);
        p.TextCentered("HEADSHOT", cx, cy, size, face);
    }

    /// <summary>
    /// The crosshair, which doubles as the hit marker: it flares red and spreads outward for a
    /// fifth of a second when a shot lands. In first person that confirmation is the only way to
    /// tell a hit from a miss at range — the sound alone is easily lost in a four-way firefight.
    /// </summary>
    static void DrawCrosshair(UiPainter p, Pawn pawn, Rect2 r)
    {
        float cx = r.Position.X + r.Size.X * 0.5f;
        float cy = r.Position.Y + r.Size.Y * 0.5f;

        float hit = MathU.Clamp01(pawn.HitConfirm / Pawn.HitConfirmTime);
        var colour = hit > 0f ? Pal.Danger.Lerp(new Color(1f, 1f, 1f, 0.75f), 1f - hit) : new Color(1f, 1f, 1f, 0.75f);

        float gap = 4f + hit * 5f;
        float len = 7f + hit * 4f;
        float thick = 2f + hit * 1f;

        p.Rect(cx - gap - len, cy - thick * 0.5f, len, thick, colour);
        p.Rect(cx + gap, cy - thick * 0.5f, len, thick, colour);
        p.Rect(cx - thick * 0.5f, cy - gap - len, thick, len, colour);
        p.Rect(cx - thick * 0.5f, cy + gap, thick, len, colour);
    }

    /// <summary>
    /// A wedge at the screen edge pointing at whoever last hurt you. Without it, being shot from
    /// behind in first person tells you nothing except that your health dropped.
    /// </summary>
    static void DrawDamageDirection(UiPainter p, Pawn pawn, View v, Rect2 r)
    {
        if (pawn.DamageFlash <= 0f) return;

        float strength = MathU.Clamp01(pawn.DamageFlash / Pawn.DamageFlashTime);

        Vector3 d = pawn.LastAttacker - pawn.GlobalPosition;
        var flat = new Vector2(d.X, d.Z);
        if (flat.LengthSquared() < 0.01f) return;

        // Angle relative to where the player is facing: 0 is straight ahead, positive to the right.
        float rel = MathU.AngleDiff(MathU.Angle(flat), v.Yaw);

        float cx = r.Position.X + r.Size.X * 0.5f;
        float cy = r.Position.Y + r.Size.Y * 0.5f;
        float radius = MathF.Min(r.Size.X, r.Size.Y) * 0.32f;

        // Screen space: rel of 0 puts the marker above centre, and it swings clockwise from there.
        float sx = cx + MathF.Sin(rel) * radius;
        float sy = cy - MathF.Cos(rel) * radius;

        var c = new Color(Pal.Danger.R, Pal.Danger.G, Pal.Danger.B, strength * 0.85f);
        p.Rect(sx - 26f, sy - 5f, 52f, 10f, c);
        p.Rect(sx - 16f, sy - 13f, 32f, 8f, c * new Color(1, 1, 1, 0.55f));
    }

    /// <summary>
    /// The break between Elimination rounds. Drawn across the whole window rather than per
    /// viewport: it is a match-wide event, and everyone is looking at it at the same moment.
    /// </summary>
    void DrawIntermission(UiPainter p)
    {
        if (!match.BetweenRounds) return;

        float cx = p.Size.X * 0.5f;
        float cy = p.Size.Y * 0.40f;

        p.Rect(0f, cy - 70f, p.Size.X, 210f, new Color(0.04f, 0.05f, 0.07f, 0.82f));
        p.Rect(0f, cy - 70f, p.Size.X, 3f, Pal.Accent);
        p.Rect(0f, cy + 137f, p.Size.X, 3f, Pal.Accent);

        p.TextCentered(match.RoundResult, cx, cy, 54, Pal.Text);
        p.TextCentered($"Next round in {MathF.Max(0f, match.IntermissionLeft):0.0}",
                       cx, cy + 74f, 26, Pal.TextDim);
    }

    void DrawKillFeed(UiPainter p)
    {
        var feed = match.KillFeed;
        if (feed.Count == 0) return;

        const int Show = 4;
        float x = p.Size.X - 26f;
        float y = 96f;

        for (int i = Math.Max(0, feed.Count - Show); i < feed.Count; i++)
        {
            var e = feed[i];

            // Fade out over the last second of the entry's life rather than vanishing.
            float alpha = MathU.Clamp01((5.5f - e.Age) / 1.0f);

            string text = e.SelfInflicted
                ? $"{e.Victim} — {e.Weapon} — self"
                : $"{e.Killer} — {e.Weapon} — {e.Victim}";

            p.TextRight(text, x, y, 19, new Color(Pal.Text.R, Pal.Text.G, Pal.Text.B, alpha * 0.9f));
            y += 26f;
        }
    }

    /// <summary>
    /// Bottom edge of the scoreboard panel, in screen pixels.
    ///
    /// Exists because the spawn screen has to start below it. Both are centred at the top of the
    /// screen, the scoreboard is drawn last so it wins, and in Dominion it is tall enough — a row
    /// per command post — to run straight through the spawn cards and the ELIMINATED banner. The
    /// height is worked out from the same bands the panel is built from rather than guessed at, so
    /// a mode that adds a row cannot silently start overlapping again.
    /// </summary>
    float ScoreboardBottom()
    {
        const float HeaderH = 26f;
        const float RowH = 24f;
        const float PadBottom = 10f;
        const int ShownRows = 6;

        int hidden = Mathf.Max(0, match.Standings().Count - ShownRows);

        float clockH = match.TimeRemaining is null ? 0f : 30f;
        float zoneH = ScoreboardModeRows();
        float teamH = settings.Def.Teams ? RowH * 2f + 12f : 0f;

        return 14f + HeaderH + clockH + teamH
             + Mathf.Min(match.Standings().Count, ShownRows) * RowH
             + (hidden > 0 ? 20f : 0f) + zoneH + PadBottom;
    }

    /// <summary>The extra band each mode adds to the bottom of the scoreboard.</summary>
    float ScoreboardModeRows()
        => settings.Mode switch
        {
            GameMode.KingOfTheHill => 24f,
            GameMode.CaptureTheFlag => 44f,
            GameMode.Juggernaut => 42f,
            GameMode.Dominion => 26f + match.Posts.Count * 18f,
            GameMode.Portal => settings.IsPuzzle ? 26f + match.CheckpointCount * 18f : 22f,
            _ => 0f,
        };


    /// <summary>
    /// What you have to spend, top left, always.
    ///
    /// Battle points used to appear only on the reinforcement screen, which is the one moment you
    /// cannot act on them: by then you are already choosing, and what you wanted to know was
    /// whether to keep pushing for one more kill before you died. A currency you can only see at
    /// the till is a currency nobody plans around.
    ///
    /// Top left because that corner was the only empty one. Health and the gauges run up from the
    /// bottom left, the minimap owns the bottom right, and the scoreboard is centre top.
    /// </summary>
    void DrawBattlePoints(UiPainter p, Pawn pawn, Rect2 r)
    {
        float x = r.Position.X + 22f;
        float y = r.Position.Y + 20f;

        string points = pawn.BattlePoints.ToString();
        var size = p.Measure(points, 30);

        // Lit once the cheapest thing on the roster is affordable, and again in the faction's own
        // colour at hero money. Two thresholds rather than a bar, because the roster is a list of
        // prices rather than a track you fill up.
        var tint = pawn.BattlePoints >= Reinforcements.HeroCost ? pawn.Tint
                 : pawn.BattlePoints >= Reinforcements.CheapestUpgrade ? Pal.Ready
                 : Pal.TextDim;

        p.Rect(x - 8f, y - 4f, size.X + 92f, size.Y + 20f, new Color(0.04f, 0.05f, 0.07f, 0.55f));
        p.Rect(x - 8f, y - 4f, 3f, size.Y + 20f, tint);

        p.Text(points, x + 4f, y, 30, tint);
        p.Text("BP", x + size.X + 12f, y + 10f, 15, Pal.TextDim);

        // Only while it means something. A permanent "SPAWN TO USE" caption is nagging; a caption
        // that appears the moment you can afford something is information.
        if (pawn.BattlePoints >= Reinforcements.CheapestUpgrade)
            p.Text(pawn.BattlePoints >= Reinforcements.HeroCost ? "HERO READY" : "UPGRADE READY",
                   x + 4f, y + size.Y + 6f, 13, tint * new Color(1f, 1f, 1f, 0.85f));
    }

    // ---- minimap ----

    /// <summary>
    /// A north-up plan of the arena in the corner of each player's slice.
    ///
    /// North-up rather than rotating with the view. A map that turns under you is easier to read
    /// for the two seconds you are looking at it and useless for the thing a map is actually for,
    /// which is building a picture of a place you keep coming back to: the Reliquary is the same
    /// shape every round, and it can only become familiar if it is drawn the same way up every
    /// round. The heading wedge says which way you are pointing, which is the part that changes.
    ///
    /// What it shows, and the reasoning, because a minimap is an information decision before it is
    /// a drawing one:
    ///
    /// - **Weapon and gear crates.** The point of the thing. A crate you have never found is a
    ///   part of the game you do not know exists, and the portal gun in particular was something
    ///   players had heard of rather than used. Crates are static, public and already announced by
    ///   a coloured pillar in the world, so putting them on the map gives away nothing that
    ///   walking past would not.
    /// - **Vehicles**, for the same reason: a parked hull is a fixture, not a secret.
    /// - **Your own side**, always. Knowing where your team is, is what a team mode is made of.
    /// - **Enemies only while revealed** — the existing <see cref="Pawn.RevealedFor"/> flag that a
    ///   scan special or Prometheus' reign sets. This is the line the whole design turns on. A map
    ///   that paints every enemy permanently deletes flanking, ambush and map knowledge in one
    ///   stroke, and it would make the reveal abilities worthless by giving their effect away for
    ///   free. Revealed enemies appear here because being revealed is exactly what that means.
    /// </summary>
    /// <summary>How much of the world the minimap shows, as a radius in metres.</summary>
    const float MinimapRange = 55f;

    void DrawMinimap(UiPainter p, View v, Pawn self, Rect2 r)
    {
        // Skip it on a slice too small to read one. Four-way splitscreen on a 1080p window gives
        // each player a 960x540 quarter, which still clears this comfortably.
        if (r.Size.X < 420f || r.Size.Y < 320f) return;

        float size = Mathf.Clamp(r.Size.X * 0.20f, 120f, 180f);
        float half = size * 0.5f;

        // Bottom right. The health, gauges and tips column runs up the bottom *left* of every
        // slice, so this is the one corner with nothing already in it.
        float cx = r.Position.X + r.Size.X - half - 22f;
        float cy = r.Position.Y + r.Size.Y - half - 22f;

        p.Panel(cx - half, cy - half, size, size, new Color(0.05f, 0.06f, 0.08f, 0.62f),
                self.Tint * new Color(1f, 1f, 1f, 0.45f));

        // Heading-up and centred on the player, like every satnav ever made, and square because a
        // circular mask is not something the painter can cut.
        //
        // The earlier version was north-up and drew the whole arena. That is the better map for
        // learning a place and the worse one for being in it: at a fixed north the thing you
        // actually want — is the next turn left or right — is a mental rotation you have to do
        // yourself, every time, while somebody is shooting at you. Turning the world instead means
        // left on the map is left in your hands.
        //
        // What it costs is the overview, which is why the range is bounded rather than the whole
        // floor squeezed into 180 pixels where nothing is legible anyway.
        float scale = half / MinimapRange;

        // Screen up is the direction the player is facing. Yaw runs from +X toward +Z, so the
        // forward vector is (cos, sin) in world XZ, and the right vector is its perpendicular.
        float c = MathF.Cos(v.Yaw), sn = MathF.Sin(v.Yaw);

        Vector2? Plot(Vector3 at)
        {
            float dx = at.X - self.GlobalPosition.X;
            float dz = at.Z - self.GlobalPosition.Z;

            // Into the player's frame: along their heading, and across it.
            float ahead = dx * c + dz * sn;
            float across = -dx * sn + dz * c;

            // Ahead is up the screen, so it subtracts from Y.
            var at2 = new Vector2(cx + across * scale, cy - ahead * scale);

            bool inside = MathF.Abs(across) <= MinimapRange && MathF.Abs(ahead) <= MinimapRange;
            return inside ? at2 : null;
        }

        void Dot(Vector3 world, float d, Color col)
        {
            if (Plot(world) is not { } at) return;
            p.Rect(at.X - d * 0.5f, at.Y - d * 0.5f, d, d, col);
        }

        foreach (var (at, _, pickTint) in match.AvailablePickups())
            Dot(at, 5f, pickTint);

        foreach (var rig in match.VehicleList)
            if (rig.Alive)
                Dot(rig.GlobalPosition, 7f, rig.Def.Tint);

        foreach (var other in match.Pawns)
        {
            if (other == self || !other.Alive) continue;

            bool friend = settings.Def.Teams && Match.SameTeam(self, other);
            if (!friend && other.RevealedFor <= 0f) continue;

            Dot(other.GlobalPosition, 6f, other.Tint);
        }

        // You, dead centre and always pointing up, because the map turns and you do not. Three
        // stepped pips make the nose; the painter draws rectangles and text and nothing else.
        for (int i = 1; i <= 3; i++)
        {
            float d = 4f - i * 0.5f;
            p.Rect(cx - d * 0.5f, cy - i * 4f - d * 0.5f, d, d,
                   self.Tint * new Color(1f, 1f, 1f, 0.85f));
        }

        p.Rect(cx - 4f, cy - 4f, 8f, 8f, Colors.White);
        p.Rect(cx - 2.5f, cy - 2.5f, 5f, 5f, self.Tint);
    }

    void DrawScoreboard(UiPainter p)
    {
        var standings = match.Standings();

        // Only the top of the table, plus your own row if you have fallen off it.
        //
        // A twelve-fighter free-for-all is twelve rows of three hundred pixels at the top of a
        // splitscreen quarter, which is most of somebody's view. The results screen shows everyone;
        // mid-match you need to know who is winning and where you are, and nothing else.
        const int ShownRows = 6;
        int hidden = Mathf.Max(0, standings.Count - ShownRows);

        // Built from named band heights rather than accumulated offsets. The clock was originally
        // added to the panel height and to the row origin by different amounts, which pushed the
        // last row out through the bottom border.
        const float HeaderH = 26f;
        const float RowH = 24f;
        const float PadBottom = 10f;

        float clockH = match.TimeRemaining is null ? 0f : 30f;
        float zoneH = ScoreboardModeRows();
        float teamH = settings.Def.Teams ? RowH * 2f + 12f : 0f;

        float w = 310f;
        float x = p.Size.X * 0.5f - w * 0.5f;
        float y = 14f;
        float h = HeaderH + clockH + teamH
                + Mathf.Min(standings.Count, ShownRows) * RowH
                + (hidden > 0 ? 20f : 0f) + zoneH + PadBottom;

        p.Panel(x, y, w, h, new Color(0.09f, 0.10f, 0.13f, 0.85f), Pal.PanelHi);
        // Names the arena actually in play, which matters now that Random is the default.
        string header = settings.Mode == GameMode.Elimination
            ? $"Round {match.Round}  ·  {match.Arena.Name}  ·  first to {settings.ScoreLimit}"
            : $"{settings.Def.Name}  ·  {match.Arena.Name}  ·  to {settings.ScoreLimit}";

        p.TextCentered(header, x + w * 0.5f, y + 5f, 15, Pal.TextDim);

        if (match.TimeRemaining is { } left)
        {
            // Turns amber inside the last thirty seconds, which is when it starts mattering.
            p.TextCentered(MatchSettings.Clock(left), x + w * 0.5f, y + HeaderH - 2f, 26,
                           left <= 30f ? Pal.Warn : Pal.Text);
        }

        float ry = y + HeaderH + clockH;

        // A team mode is won on a combined score, so that is what the scoreboard leads with —
        // individual frags are detail underneath it.
        if (settings.Def.Teams)
        {
            for (int team = 0; team < 2; team++)
            {
                p.Text(Pal.TeamName(team), x + 12f, ry, 19, Pal.Teams[team]);

                // Dominion counts the opposite way from everything else on this panel: the big
                // number is what a side has left, not what it has earned, and it goes down. It is
                // shown in the same place all the same, because it is still the number that says
                // who is winning — it just says it backwards.
                if (settings.Mode == GameMode.Dominion)
                {
                    float pool = match.Tickets[team];
                    int posts = match.PostsHeld(team);

                    p.TextRight($"{posts} posts", x + w - 76f, ry + 2f, 15, Pal.TextDim);
                    p.TextRight($"{pool:0}", x + w - 12f, ry, 19,
                                pool <= settings.ScoreLimit * 0.2f ? Pal.Danger : Pal.Text);
                }
                else
                {
                    p.TextRight(match.TeamScore(team).ToString(), x + w - 12f, ry, 19, Pal.Text);
                }

                ry += RowH;
            }

            p.Rect(x + 12f, ry - 4f, w - 24f, 1f, Pal.PanelHi);
            ry += 8f;
        }

        for (int i = 0; i < standings.Count && i < ShownRows; i++)
        {
            var pawn = standings[i];
            p.Text(pawn.Name2, x + 12f, ry, 17, pawn.Tint);
            p.Text(pawn.Class.Name, x + 96f, ry, 15, Pal.TextDim);
            p.TextRight(match.ScoreOf(pawn).ToString(), x + w - 12f, ry, 17, Pal.Text);
            ry += RowH;
        }

        if (hidden > 0)
        {
            p.TextCentered($"+{hidden} more", x + w * 0.5f, ry, 14, Pal.TextDim);
            ry += 20f;
        }

        if (settings.Mode == GameMode.Juggernaut)
        {
            if (match.Juggernaut is { } king)
            {
                p.TextCentered($"{king.Name2} IS {king.Crown!.Name}", x + w * 0.5f, ry + 2f, 16,
                               king.Faction.Tint);
                ry += 20f;
                p.TextCentered(king.Crown.Epithet, x + w * 0.5f, ry, 13, Pal.TextDim);
            }
            else
            {
                p.TextCentered("FIRST BLOOD TAKES THE CROWN", x + w * 0.5f, ry + 2f, 16, Pal.Warn);
            }

            return;
        }

        if (settings.Mode == GameMode.CaptureTheFlag)
        {
            // Where both flags are, at all times. The mode is unplayable without it: you cannot
            // decide whether to attack or defend without knowing whether your own flag is home,
            // and that is not something the arena tells you from across the map.
            foreach (var flag in match.Flags)
            {
                (string state, Color tone) = flag.Carrier is { } who
                    ? ($"TAKEN by {who.Name2}", Pal.Warn)
                    : flag.Dropped
                        ? ($"DROPPED · {Match.FlagReturnTime - flag.Loose:0}s", Pal.Warn)
                        : ("AT BASE", Pal.Ready);

                p.Text($"{Pal.TeamName(flag.Team)} flag", x + 12f, ry, 15, Pal.Teams[flag.Team]);
                p.TextRight(state, x + w - 12f, ry, 15, tone);
                ry += 20f;
            }

            return;
        }

        if (settings.Mode == GameMode.Portal)
        {
            if (!settings.IsPuzzle)
            {
                // The elimination half. Worth saying out loud, because a mode where your gun does
                // no damage reads as broken until somebody tells you what it is for.
                p.TextCentered("NOBODY CAN SHOOT ANYBODY — drop them into the void",
                               x + w * 0.5f, ry + 2f, 15, Pal.Warn);
                return;
            }

            // The chambers, as a checklist. A puzzle with no visible progress is a puzzle nobody
            // can tell they are winning.
            p.TextCentered($"{match.CheckpointsReached} of {match.CheckpointCount} CHECKPOINTS",
                           x + w * 0.5f, ry + 2f, 16,
                           match.CheckpointsReached == match.CheckpointCount ? Pal.Ready : Pal.Text);
            ry += 22f;

            for (int i = 0; i < match.CheckpointCount; i++)
            {
                bool done = match.CheckpointDone(i);

                p.Text(done ? "REACHED" : $"checkpoint {i + 1}",
                       x + 12f, ry, 14, done ? Pal.Ready : Pal.TextDim);

                // The distance to the nearest unclaimed one, from this screen's own player, so the
                // panel says where to go rather than only what is left.
                if (!done && match.Pawns.Count > 0)
                {
                    float best = float.MaxValue;
                    foreach (var pawn in match.Pawns)
                        if (pawn.Alive)
                            best = MathF.Min(best,
                                             pawn.GlobalPosition.DistanceTo(match.Arena.Checkpoints[i]));

                    if (best < float.MaxValue)
                        p.TextRight($"{best:0}m", x + w - 12f, ry, 14, Pal.TextDim);
                }

                ry += 18f;
            }

            return;
        }

        if (settings.Mode == GameMode.Dominion)
        {
            // One row per post, in map order, because the shape of the row *is* the state of the
            // match — a run of your colour at one end and theirs at the other is a front line, and
            // an alternating row is a mess you are losing somewhere you have not looked.
            int lead = match.PostsHeld(0) - match.PostsHeld(1);
            (string note, Color tone) = lead == 0
                ? ("EVEN — nobody is bleeding", Pal.TextDim)
                : ($"{Pal.TeamName(lead > 0 ? 0 : 1)} +{Mathf.Abs(lead)} — "
                   + $"{Pal.TeamName(lead > 0 ? 1 : 0)} bleeding {Mathf.Abs(lead) * Match.BleedPerPost:0.0}/s",
                   Pal.Teams[lead > 0 ? 0 : 1]);

            p.TextCentered(note, x + w * 0.5f, ry + 2f, 15, tone);
            ry += 22f;

            foreach (var post in match.Posts)
            {
                Color tint = Match.PostTint(post.Owner);

                p.Text(post.Name, x + 12f, ry, 15, tint);

                string state = post.Contested ? "CONTESTED"
                             : post.Contender >= 0 ? $"{Pal.TeamName(post.Contender)} {post.Progress * 100f:0}%"
                             : post.Owner < 0 ? "neutral"
                             : Pal.TeamName(post.Owner);

                p.TextRight(state, x + w - 12f, ry, 14,
                            post.Contested ? Pal.Warn
                            : post.Contender >= 0 ? Pal.Teams[post.Contender]
                            : tint);
                ry += 18f;
            }

            return;
        }

        if (settings.Mode != GameMode.KingOfTheHill) return;

        // Who is scoring right now is the whole read of the mode, so it goes on the scoreboard
        // rather than being something you have to infer from the numbers ticking.
        (string label, Color colour) = match.ZoneContested
            ? ("ZONE CONTESTED", Pal.Warn)
            : match.ZoneHolder is { } holder
                ? ($"{holder.Name2} HOLDS THE ZONE", holder.Tint)
                : ("ZONE OPEN", Pal.TextDim);

        p.TextCentered(label, x + w * 0.5f, ry + 2f, 16, colour);
    }
}
