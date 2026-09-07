# HitBox Clone

A controller-first 3D arena shooter, in the spirit of **HitBox** (Steam, 2017) — minimalist,
colourful, four classes, four modes, up to 4-player splitscreen.

The clone exists for one reason the original gets wrong: **the UI is not fully controller-supported.**
Here a gamepad alone drives every screen from launch to quit, four pads drive the lobby at the same
time, and the mouse is strictly optional.

**Status:** playable. Menus, four-pad lobby, arena, all four classes, bots, splitscreen deathmatch,
pause and results are in. Remaining work is more arenas, the other three modes fleshed out, audio
and a rebinding screen.

---

## Running it

**Double-click `Play.cmd` in this folder.** That is the whole story for playing. It
builds first and only launches if the build succeeded — if the build fails it prints the
errors and waits, instead of flashing a window at you and running last session's
assembly.

### Setting up a machine that has never run this

The repository holds the game and its models. It does **not** hold the toolchain — that is
two large installs that belong to the machine, not to the project. A fresh machine needs
both of these before `Play.cmd` will work:

| | |
|---|---|
| **Godot 4.7.1 — .NET / mono build** | [godotengine.org/download](https://godotengine.org/download). It must be the **.NET** build. The plain build cannot run C# at all and fails with a wall of script errors that do not say that is the problem. |
| **.NET 8 SDK** | [dotnet.microsoft.com/download](https://dotnet.microsoft.com/download). The SDK, not the runtime — the runtime cannot compile. |

**Or double-click `Setup.cmd` and skip this section.** It checks what is already present
using the same search order `Play.cmd` uses, installs only what is missing through
`winget`, sets `GODOT_HOME` — which matters, because `winget` drops Godot as a portable
package that never lands on `PATH`, so without that step `Play.cmd` would still report it
missing — and puts a **HitboxClone** shortcut on the Desktop. The shortcut targets
`Play.cmd` rather than the engine, so launching from the Desktop still builds first; a
shortcut straight to Godot would quietly run the previous assembly, which is the trap
`Play.cmd` exists to close. Safe to run twice, and re-running is how you rebuild the
shortcut after moving the folder. Everything below is what it does, for when you would
rather do it yourself or `winget` is not available.

`Play.cmd` finds them in this order, and stops with a message naming the missing one
rather than failing obscurely:

1. `GODOT_HOME` / `DOTNET_HOME`, if you set them
2. `H:\dev-tools\godot` and `H:\dev-tools\dotnet` — the portable copies on the machine this
   was written on
3. Whatever is on `PATH`

So on a laptop with normal installs, both land on `PATH` and it just runs. If you keep
Godot somewhere it is not on `PATH`, set `GODOT_HOME` to the folder holding the
executable and leave everything else alone.

The first launch is slow: Godot imports every model in `assets/` into a `.godot/` cache
that is deliberately not in the repository, because it is derived data and it is bigger
than the models it is derived from.

### Running it, and testing it, on Linux

`./Test.sh` builds and then runs the whole self-test suite. It is the Linux counterpart of
`Play.cmd` and the only command that actually verifies anything: the build is not optional,
because Godot does not compile C# on launch and `godot --headless -- --selftest` on its own will
run the *previous* assembly and report a green suite for code that was never compiled.

It finds Godot through `$GODOT`, then `$GODOT_HOME`, then `PATH` — the same order `Play.cmd` uses,
so both platforms answer "where is Godot" the same way.

### Claude Code on the web

`.claude/hooks/session-start.sh` installs both halves of the toolchain into a fresh web session and
imports the assets once, so an agent working on this repository can run `./Test.sh` and get a real
answer instead of only being able to prove that the code compiles. It is a no-op on a local
machine — your own Godot and SDK are your business.

Two things about that hook are not guessable and are the reason it is written down:

- **`godotengine.org` is blocked** by the sandbox network policy, and the GitHub release is not.
  The release URL redirects to the assets CDN, which is allowed, so the download works from
  `github.com/godotengine/godot/releases/download/...` and fails from the obvious address.
- **`apt-get update` is not optional** before installing `dotnet-sdk-8.0`. The base image's package
  index is stale enough that the dotnet8 packages 404 on a straight install.

### The manual equivalents

For when you want the pieces separately. Substitute your own paths for the `H:` ones.

Play it:

```bash
H:/dev-tools/godot/godot.cmd --path H:/HitboxClone
```

Open the editor instead (note the `-e`):

```bash
H:/dev-tools/godot/godot.cmd -e --path H:/HitboxClone
```

Rebuild the C# assembly after editing sources — **`godot.cmd` does not do this for you**,
which is the single easiest way to spend an hour debugging code that is not running:

```bash
H:/dev-tools/dotnet/dotnet.exe build H:/HitboxClone/HitboxClone.csproj
```

---

## Controls

Every player picks their own device in the lobby. Two players can share the keyboard.

**Gamepad** (up to 8 detected, 4 can play)
- Left stick / D-pad — move and navigate menus
- Right stick — look: X turns, Y pitches
- **RT** — attack / confirm / claim a lobby slot
- **LT** — aim down sights
- **A** — jump
- **B** — crouch, or **slide** if you are sprinting
- **L3** — sprint
- **RB** — dash
- **LB** — special ability (differs per class)
- **X** — interact: enter or exit a vehicle
- **Start** — begin the match / pause
- **Back** or **Y** — un-ready, leave slot, back out of a screen

All of it is rebindable in Options. Saved layouts carry a version; one written before this action
set existed is discarded rather than merged, since the old defaults put Attack on A and A is Jump
now.

**Title → Controls** shows all of this in game, and it is also on the pause menu — the moment you
need the controls is usually the moment you are already playing. Every binding on that screen is
read live from the binding table and labelled for the pad actually connected, so it stays correct
after a rebind instead of drifting from reality the way a hand-written list would.

### Keyboard — off by default

This is a controller game. Keyboards can always drive **menus**, so you are never locked out of the
options with no pad plugged in, but they cannot claim a lobby slot or fight unless you turn on
**Keyboard & mouse players** in Options.

That default is also what makes the game immune to controller-to-keyboard translation layers — see
*Troubleshooting*.

With it enabled:

**Keyboard 1** — WASD move · Q/E turn · Space attack · F special · Shift dash · Enter start ·
Backspace back

**Keyboard 2** — Up/Down move · Left/Right turn · Numpad4/6 strafe · Numpad0 attack ·
Numpad1 special · Numpad2 dash · NumpadEnter start

Keyboard players have a turn axis but no pitch axis, so they aim level, and there is no mouse look.
Neither is a priority — gamepads are the target.

The two schemes deliberately share no key, and neither uses bare Ctrl/Shift/Alt. The self-test
enforces both rules — see *Troubleshooting* for why.

---

## Troubleshooting

**One controller press acts for both a pad player and a keyboard player.**

This cannot happen with the default settings, and that is deliberate.

Something outside the game translates your pad into keyboard and mouse events — Steam Input running
a desktop layout, DS4Windows, JoyToKey, or a similar mapper. A desktop layout typically maps **A to
a left mouse click** and the **d-pad to arrow keys**, and moves the cursor with the left stick. The
game then legitimately sees two devices being pressed, because two devices genuinely were.

A game cannot stop those events being generated — by the time they arrive they are indistinguishable
from real hardware, and heuristics do not save you either: a "has the cursor moved recently?" check
passes happily when the pad is driving the cursor as well as clicking. The only reliable answer is
to not treat keyboard and mouse as a player at all, which is the default here.

If you turned **Keyboard & mouse players** on and hit this, either turn it back off, or cure it at
the source: in Steam, right-click the game → *Properties* → *Controller* → *Disable Steam Input*.
For a non-Steam app like this one, disable *Desktop Configuration* under Steam → *Settings* →
*Controller*, or close Steam while playing.

**Controls & Devices** is the diagnostic: it lists every device the game sees and lights up the row
that is actually being pressed. If pressing a pad button lights a keyboard row, something is
translating your pad.

Pads are **hot-pluggable** — plug one in at any point and it appears as joinable. Trigger rest
position is **calibrated per pad** on connect, so both the `[-1,1]` and `[0,1]` trigger conventions
work with no configuration. Unplugging a pad in the lobby frees its slot and says so on screen.

---

## The controller-first rules

These are enforced, not aspirational — the self-test fails the build if any of them break.

1. **Every screen is reachable and exitable with a pad alone.** Back retreats one level as the
   inherited default in `UiScreen`; a dead-end screen is something you would have to write on purpose.
2. **The root screen absorbs back.** Tapping back once too many never closes the game out from under
   four people on a couch. Quitting is an explicit menu choice.
3. **Prompts match the device in your hands.** Xbox, PlayStation and Nintendo labels are chosen from
   the detected pad name, including the Nintendo A/B swap. With no pad connected, prompts show
   keyboard keys rather than telling you to press "A".
4. **Back means the right thing per player.** In the lobby it un-readies, then leaves the slot —
   one player backing out never yanks the whole couch to the previous screen.
5. **Selection is never carried by colour alone.** The focused menu row gets a caret and a border,
   not just a highlight.

---

## Rebinding

**Options → Controller bindings** remaps Attack, Special, Dash, Start and Back, driven entirely by
a gamepad. Bindings persist to the same settings file.

Sticks are deliberately fixed — left moves, right looks. Remapping those would mean handling
inversion, sensitivity and swapped pairs, and no couch player has needed it badly enough to justify
the surface area.

The listening state is the delicate part, because someone rebinding **Back** is about to press the
very button they would otherwise escape with. Four things guarantee they cannot strand themselves:

- capture waits for every button to be **released** first, so the press that opened the row is never
  the press that gets captured
- listening **times out** after six seconds on its own
- a **keyboard always cancels**, and keyboards can drive menus regardless of the keyboard-players setting
- every action **keeps at least one binding** — taking an input from another action restores that
  action's default if it would otherwise be left with nothing

The last of those is enforced in `PadBindings` rather than in the UI, and the self-test hammers it
by stealing every input Back has, one at a time, asserting Back and Start always survive.

## Why the UI does not use Godot's `Control` focus system

Godot's focus model is single-focus per viewport. The lobby needs four devices each driving their
own slot simultaneously, which that model cannot express. So the UI is immediate-mode: `UiScreen`
subclasses draw through `UiPainter`, and each lobby slot tracks its own owning device.

The side benefit is that the screenshot harness is trivial — pose a screen, draw it, capture it.

---

## Test hooks

```bash
H:/dev-tools/godot/godot-console.cmd --headless --path H:/HitboxClone -- --selftest
```

Runs two harnesses back to back and exits non-zero on failure.

The **UI half** drives every screen with synthetic devices through the real `InputDevice` edge
detection and the real `ScreenStack`, asserting pad-only reachability, lobby claim/ready/leave
semantics, four independent devices, disconnect handling, nav-repeat timing and that the two
keyboard schemes share no key. This is what caught a bug where the single press that claimed a lobby
slot also readied it, locking a player into whatever class happened to be selected.

The **simulation half** plays six full bot-vs-bot matches — every mode, both arenas, normal and hard
skill — with no cameras, meshes or lights, checking every frame for NaN transforms, health out of
range, pawns escaping the arena, and unbounded projectile lists. It also fails a match where nobody
scored, since bots that never engage would pass every invariant while testing nothing. That is
roughly 175,000 assertions per run.

It also asserts that every spawn point and every King of the Hill zone is inside the arena and clear
of cover. Both have been broken by layout edits: spawns once landed on the corner pillars, and the
Crossfire centre zone sat inside the raised platform rather than on top of it, which would have made
it uncapturable.

```bash
H:/dev-tools/godot/godot-console.cmd --path H:/HitboxClone -- --shots H:/HitboxClone/shots
```

Poses every screen and writes a PNG of each, for eyeballing UI changes without playing through.
Needs a real window — headless has no rendering context.

---

## Code layout

| Path | Contents |
|---|---|
| `src/core/MathU.cs` | Math helpers — deadzones, damping, angles |
| `src/input/` | Device abstraction: keyboard schemes and gamepads behind one interface |
| `src/input/PadBindings.cs` | Remappable gamepad buttons and their persistence |
| `src/ui/` | `UiPainter` drawing, `UiScreen` + `ScreenStack`, `Menu`, `Glyphs` |
| `src/ui/screens/` | Title, mode select, lobby, device diagnostic, match placeholder |
| `src/classes/ClassDefs.cs` | The four classes — **all tuning values live here** |
| `src/game/Arena.cs` | Procedural arena geometry, spawn selection |
| `src/game/Pawn.cs` | Movement, dash + i-frames, health, respawn |
| `src/game/Match.cs` | Simulation: pawns, projectiles, scoring, win conditions |
| `src/game/BotBrain.cs` | CPU opponents |
| `src/game/Modes.cs` | Game modes and match settings |
| `src/game/Sfx.cs` | Every sound, synthesised into PCM at startup |
| `src/game/Graphics.cs` | Lighting, environment, materials and the quality tiers |
| `src/game/Impact.cs` | Hit and death particle bursts |
| `src/tests/` | The headless UI and simulation harnesses |

`Match` is a Node3D that drives itself from `_PhysicsProcess`, and knows nothing about cameras,
meshes or HUDs — `MatchScreen` owns all of that. That split is what lets the harness play full
matches with no rendering at all.

## Gameplay notes

The game is **first person**. The view is the aim — the camera sits at the pawn's eye, and shots
fire along exactly where you are looking, pitch included, so the raised centre platform is a real
height advantage rather than scenery.

Each pawn's meshes sit on their own render layer, and each camera culls the layer belonging to the
body it is inside. All four views share one `World3D`, so per-viewport visibility has to be done
with layers rather than by toggling `Visible`.

Dash grants brief invulnerability, so dashing through a shot is the core defensive skill. It is also
an **attack**: slamming into someone deals 26 damage and throws them hard — hard enough to put them
into a pit or a hazard, which is the point. It turns the arena's own dangers into something you can
aim an opponent at. One dash can only hit a given target once.

Killing yourself — or a teammate in Team Deathmatch — costs a frag rather than earning one.

### Hazards

**Pits** kill outright. **Burning ground** does 34 damage a second and is survivable, so it shapes
where fights happen rather than deleting anyone who touches it — and being shoved into one is a real
threat without being an instant loss. Both are placed where people funnel rather than out in the
open, so a dash aimed at someone crossing them is a genuine play.

### Stances

| | Effect |
|---|---|
| **Aim down sights** | Spread ×0.35, look rate ×0.55, movement ×0.55. Every gun puts a real sight — ring, front post and centre dot — dead centre of the screen; the Marksman raises a true magnified scope instead. |
| **Sprint** | Speed ×1.5, but you cannot fire and cannot aim. That is the whole cost. |
| **Crouch** | Speed ×0.5, spread ×0.7, and the **collision capsule genuinely shrinks** from 1.8m to 1.05m. Crouched *and* aimed is the most accurate the game gets. |
| **Slide** | Crouch while sprinting. Keeps its launch direction and bleeds speed, so it commits you — you cannot steer out of it. Jump cancels it. |
| **Jump** | Apex ≈3.2m, airtime ≈1.4s — platformer weight, not shooter weight. |

Jump gravity is 13, not the 22 that grenades and corpses use. At that weight the apex is roughly
3.2 metres and you hang for about 1.4 seconds, which is what makes a deck-to-deck jump something
you can read and steer in the air rather than a leap of faith committed to on the ground.

**Coyote time** (0.12s) keeps a jump valid just after walking off an edge, and a **jump buffer**
(0.12s) remembers a press made just before landing. Without both, jumping from the lip of a deck
fails often enough that players stop trusting the geometry.

Eye height and the collision capsule ease between stances together rather than snapping, so a
crouched player is a genuinely smaller target — shots pass over them, and because the headshot
threshold is measured against *current* height, their head is where it visibly is. Standing back up
is blocked while there is something overhead, or a player crouched under a deck would grow into the
geometry and be shoved out of the world.

The Marksman's scope is a genuine sight picture: a circular aperture masked by opaque surround,
drawn as per-row chords rather than a texture since the project has no image files. At 16° the
view model is hidden — a rifle across the screen would cover the sight — and the look rate is
damped further still, because that much magnification turns every stick twitch into a wild swing.

### Weapon pickups

Crates on the map carry a weapon far more extreme than any class gun, with limited ammo. Taking one
swaps only the **weapon** — health, speed, dash and special stay yours, so a Flanker with a railgun
is still a fast, fragile Flanker rather than becoming a Marksman. Emptying it drops you back to your
class weapon; crates respawn after 16 seconds.

| | |
|---|---|
| **Railgun** | 105 damage, scoped, near-instant travel, 5 shots |
| **Minigun** | 6.5 damage every 0.055s, wide spread, 160 shots |
| **Scattergun** | 11 pellets, brutal up close, 12 shots |
| **Sword** | 88 damage in a 4m arc, 24 swings — built from the projectile system as a very fast, very short shot, so melee needs no separate hit path |

Every visible crate is **labelled in world space** with the weapon's name, fading with distance, so
you know what you are running across the arena for before you commit. Colour told you a weapon was
there but never which one.

Each arena puts one on a deck as the prize worth climbing for and the rest on the floor. Bots detour
for a pickup within 34m that is roughly at their own height — they have no route-finding, so
steering them toward one three metres up would just walk them into a wall.

This is why `WeaponDef` exists separately from `ClassDef`: weapon code has one shape to deal with
whether a pawn carries its class gun or something off the floor.

### Headshots

Hits above 72% of a pawn's height deal **6.6×** damage and throw a large **HEADSHOT** banner up for
the shooter. Detection comes from the raycast's impact height rather than a second collider: the
pawn is a single capsule, and a separate head body would double the physics cost of every pawn for
something a height comparison resolves exactly.

### Dying

Both sides get told, loudly — in a four-way fight a death used to be a line in the corner and a body
falling over, which was easy to miss entirely, including your own.

**The victim** gets a red wash over their whole viewport, a large **ELIMINATED**, who did it, and the
respawn countdown. The body topples instead of vanishing, so everyone can see where they went down,
and the camera sinks to the ground and rolls back to face the sky.

The topple used to be **dead code** — dead pawns were skipped before their tick ever ran, so bodies
stood upright and unchanged until they vanished on respawn, and players kept shooting corpses. Dead
pawns now tick, the whole figure falls as one unit, and its emission fades to zero as it lands so a
corpse is visibly not a threat well before it disappears.

**The killer** gets their own ELIMINATED confirmation with the victim's name, kept clear of the
centre so it never covers their aim, plus a rumble pulse. That rumble is fired from `MatchScreen`
rather than the match, which has no idea which pad — if any — is behind a given pawn.

Every death throws two particle bursts: one upward plume and one ring thrown outward along the
ground, so a kill has a silhouette that reads from across the arena and from any angle. Both are
fired from `AwardKill`, so a death looks the same whether it came from a bullet, a blast, or a fall.

### Specials

Each class has a distinct ability on the special button, with its own cooldown:

| Class | Special | |
|---|---|---|
| Trooper | **Frag** | A lobbed grenade that detonates on contact or fuse. The only way to hit someone you cannot see. |
| Tactician | **Shockwave** | Instant radial blast around you, damaging and shoving. No aiming, no travel time. |
| Flanker | **Overdrive** | Four seconds of much higher speed and a much faster trigger. |
| Marksman | **Focus** | Five seconds zoomed in, near-perfectly accurate with far faster shots. |

Blast damage falls off to nothing at the edge of its radius, so positioning matters rather than the
blast being a flat circle of death. A frag hurts its thrower; a shockwave does not, since it is
centred on you by definition. Knockback is kept separate from input-driven velocity, so being blown
across the arena does not fight with your own movement.

The self-test asserts every class has a named, blurbed, cooldowned special, that no timed ability
lasts longer than its own cooldown, and — via the match harness — that bots actually fire all four
kinds. The button was bound, listed in the rebinding screen and shown in the lobby while doing
nothing at all for a long time, precisely because nothing checked.

Bots play to their class's preferred range rather than always closing, so a Tactician bot rushes you
and a Marksman bot keeps its distance. In objective modes they also break off to contest the zone,
without which King of the Hill would play exactly like deathmatch.

### CPU difficulty

Four levels — **Recruit, Easy, Normal, Veteran** — set in mode select. Default is Easy.

Difficulty never touches damage, health or speed. A bot that cheats on stats feels unfair; one that
simply aims worse feels beatable. What varies:

| | Recruit | Easy | Normal | Veteran |
|---|---|---|---|---|
| Aim error (rad) | 0.44 | 0.31 | 0.14 | 0.045 |
| Reaction (s) | 0.85 | 0.62 | 0.32 | 0.15 |
| Aim slew (rad/s) | 1.5 | 2.0 | 3.6 | 5.4 |
| Burst / rest (s) | 0.45 / 1.10 | 0.65 / 0.85 | 1.10 / 0.45 | 1.80 / 0.22 |

**Aim slew is the important one.** A bot that snaps its crosshair onto you the instant you round a
corner is brutal at any error tolerance, because it never gives you the moment of grace a human
opponent does while they drag their aim across. Bots swing toward their target at a bounded rate
instead.

Two subtleties worth knowing if you retune these. The fire gate compares the bot's aim against
where it *thinks* the target is, not where the target actually is — gating on the true direction
made low-skill bots hold fire until they happened to be accurate, so aim error never turned into
misses and every level below Veteran landed identical damage. And bots fire in bursts rather than
holding the trigger from the moment they have a target, which otherwise made the fast-firing
classes far deadlier than the difficulty intended.

### Arenas

124 × 92 metres. The arena grew **outward** rather than being rescaled: the central structures were
already tuned, and stretching them would have broken jump distances set against the new gravity.

Every layout therefore gains a shared **outer ring** — raised corner platforms, mid-edge cover
breaking the long runs the size created, and two more launch pads. Spawns sit on top of the corner
platforms, which gives you a second to read the arena before dropping into it and keeps spawns off
the routes people run.

**Crossfire** — a tall central deck to hold, reached by stepped ramps or by launch pads that fling
you onto it. Side catwalks give a flanking route above the floor. Whoever owns the middle sees
everything, which is exactly why it is worth taking.

**Foundry** — a spine down the middle with catwalks over the chokepoints and a moving platform
crossing the eastern gap. Timing the platform is the fast way over; the long way round is safer.

**Atrium** — a tiered central tower ringed by a **pit**, crossed by two bridges or by launching over
it and committing to the air. Falling in is fatal, so the middle is genuinely dangerous ground.

**Gauntlet** — three lanes, the middle one broken by a pit that only a moving platform or a
well-timed launch crosses. The outer lanes are safe and slow; the middle is fast and can kill you.

### Level machinery

- **Ramps** are staircases of boxes rather than slopes, because everything else here is an
  axis-aligned box and a ramp mesh would need its own collision shape and look out of place.
- **Launch pads** fling you straight up. They ignore invulnerability frames — a pad is level
  machinery, not an attack, and having one silently fail for a player who just dashed would read as
  the pad being broken.
- **Moving platforms** are `AnimatableBody3D` with `SyncToPhysics` on, which is what makes a rider
  inherit their motion; without it the platform teleports out from under you every tick. They ease
  at each end on a cosine rather than reversing instantly and flinging their passenger off.
- **Pits** are real holes carved out of the floor by rectangle subtraction, not decorative markings.
  Fall below the kill plane and you die, costing a frag the same way a suicide does.

Bots have no navmesh and no notion of the floor having holes in it, so they look a short distance
ahead and refuse to step off an edge, sidestepping and then backing off if boxed in. Without that
they marched straight into the Atrium moat and handed out free frags.

### Graphics

Still no asset files — the sky is procedural, every material is generated, and the look is still
flat colour and shape. The rendering just does more with it: shadowed sun, sky-based ambient, ACES
tonemapping, depth fog, bloom on the deliberately bright things (tracers, muzzle flashes, the
capture zone), and emissive trim along the top edge of every piece of cover. Hits and deaths throw
a burst of debris in the colour of whatever it came off.

A **fill light** from the opposite side, dim and shadowless, gives faces turned away from the sun an
actual gradient. Sky ambient alone left them one flat value, which is what made the geometry read as
coloured paper rather than as solid. A light colour grade on top — a touch of contrast and
saturation — stops flat-shaded boxes under a weak sun collapsing into a single grey.

**Quality** is set in Options — Low, Medium (default), High. It exists because splitscreen
multiplies everything: four viewports each pay for shadows and screen-space effects, so what is
comfortable in one view can be four times too expensive in four. Low disables shadows, bloom,
particles and anti-aliasing. Medium now includes a cheap ambient occlusion pass — it earns its cost
on stacked boxes by drawing the seam where two surfaces meet, which flat shading leaves invisible —
and High raises its quality and adds 4× MSAA.

Two things splitscreen forced:

- **Shadows use a single orthogonal split**, not cascades. Cascades are fitted to the active
  camera's frustum, and with four viewports sharing one `World3D` that fit belongs to whichever
  camera drew last, leaving the other views wrongly lit.
- **The sun is deliberately weak and the ambient strong.** Two players routinely look at opposite
  faces of the same block, so a strong key light means one sees near-white while the other sees
  near-black. The dynamic range has to stay narrow enough that both reads are legible — that is
  also why the ground half of the procedural sky is kept light and near-neutral, since it is what
  fills the shaded faces.

### Combat feedback

First person hides a lot that third person shows for free, and none of it can be left to audio —
plenty of machines have no working sound at all. Everything below is visual:

- **View model** — the weapon in your hands, on a render layer only your own camera draws, with a
  stripe in your player colour so you can tell your splitscreen view at a glance. Its silhouette
  matches the class: the shotgun stubby and fat, the rifle long and thin.
- **Muzzle flash** on every shot. Without it, firing showed nothing on screen at all except a tracer
  heading into the distance.
- **Recoil** — each shot kicks the view up and it settles back. Aim genuinely moves while it lasts,
  so sustained fire climbs and has to be ridden, but it recovers on its own rather than leaving you
  permanently off target. Per-class, in `ClassDefs.cs`.

- **Hit marker** — the crosshair flares red and spreads for a fifth of a second when a shot lands.
  Only a shot that did real damage confirms; hitting someone mid-dash through their invulnerability
  frames reads as a miss, because that is what it was.
- **Damage direction** — a wedge at the screen edge points at whoever last hurt you. Without it,
  being shot from behind tells you nothing except that your health dropped.
- **Kill feed**, top right, fading out over its last second.
- **Match clock**, when a time limit is set, turning amber inside the last thirty seconds.

### Modes

**Team Deathmatch** dresses everyone in their **team's** colour — blue against orange, not the four
per-slot player colours, which made friend and foe indistinguishable. Blue/orange rather than
red/green because that is the one pairing a colourblind player cannot separate, and telling friend
from foe is the entire job of those two colours. The scoreboard leads with the combined team score,
which is what the mode is actually won on.

**Elimination** genuinely plays in rounds now. It previously ended the whole match the first time
someone was left standing, despite scoring in "rounds" — and worse, kills and round wins shared one
counter, so a bot with two frags instantly won a two-round match. Rounds are tracked separately, a
survivor takes the round, and everyone resets after a short intermission.

An optional **time limit** (off, 2, 3, 5 or 10 minutes) applies to every mode. When it expires the
highest score wins, and an equal top score is reported as a draw rather than picking a winner
arbitrarily.

**Deathmatch** and **Team Deathmatch** run to a frag limit; team kills and suicides cost a frag
rather than earning one. **Elimination** removes respawns — last one standing. **King of the Hill**
scores a point per second of uncontested hold, and the zone relocates after 14 seconds held so
nobody camps one favourable spot all match. The zone is drawn as a translucent column rather than a
floor disc, because at eye height a marker on the ground is invisible past the nearest crate.

The input layer and the lobby's slot-claiming model are ported from `H:\BattleArena`
(`Input.cs`, `Program.cs`), which already solved couch-multiplayer device handling. The one
structural change: Godot's Input reports only held state, so press-edge detection moved into the
`InputDevice` base class instead of being repeated per device.

All art is drawn procedurally from shapes and every sound is synthesised into memory at startup —
there are no image or audio files anywhere in the project.

### Audio

Thirteen sounds, generated as 16-bit PCM at load: a distinct report per weapon class, hit, death,
dash, respawn, three menu blips, zone capture and a match-end triad.

**Audio is a bonus, never the only channel.** Anything the game needs you to know has a visual form
too, because a machine with no sound output is common and the game has to be fully playable on one.

Playback is deliberately **non-positional**. Godot mixes 3D audio against one listener per viewport,
and four splitscreen views sharing a world makes that a mess. World sounds are instead attenuated
against whichever human player is nearest, which is the mix a couch actually wants: if it is
happening near anyone on the sofa, everyone hears it. Distance fades in decibels rather than
amplitude, so a distant firefight stays audible enough to locate without competing with your own gun.

---

## Roadmap

| # | Deliverable | Status |
|---|---|---|
| 0 | Toolchain, project builds and runs | done |
| 1 | Input layer + controller-first UI, title → mode → lobby | done |
| 2 | Arena, first-person movement and shooting | done |
| 3 | 2–4 player splitscreen + bots | done |
| 4 | All four classes | done |
| 5 | Second arena, King of the Hill implemented, keyboard made opt-in | done |
| 6 | Audio, four CPU difficulties | done |
| 7 | Two more arenas, pad rebinding screen | done |
| 8 | Kill feed, hit markers, damage direction, match timer | done |
| 9 | View model, muzzle flash, recoil, random arena default | done |
| 10 | Graphics: shadows, sky, bloom, fog, particles, quality tiers | done |
| 11 | Class specials on the special button | done |
| 12 | ADS + scopes, jump/sprint/crouch/slide, headshots, death fall | done |
| 13 | Bigger arenas: decks, ramps, launch pads, moving platforms, pits | done |
| 14 | Platformer jump weight, weapon pickups, louder hit feedback | done |
| 15 | Controls reference screen and menu visual pass | done |
| 16 | Death feedback, crouch hitbox, bigger arenas, lighting pass | done |
| 17 | Team colours, Elimination rounds, player figures, dash slam, hazards, sword, sights | done |
| 18 | Huge arenas and vehicles — see below | next |

## Not built yet: huge maps and vehicles

Requested, deliberately deferred, and worth being explicit about why rather than half-doing it.

**Vehicles** are a new controllable entity, not a feature on an existing one: entering and exiting,
a separate physics and camera mode, their own HUD, their own damage model, and bots that understand
any of it. That is its own milestone, not an afternoon.

**An 8× arena** — roughly 350×260m — collides with something the harness has already measured. At
1.65× the old size, a two-a-side match could run twenty seconds without the sides meeting, and the
bots' pickup-seek radius had to grow with the map. Bots have no navmesh and no route-finding; across
an arena that size four players would mostly not find each other, which is the failure mode
vehicles exist to solve. So the two go together: the map size is only playable *with* the vehicles,
and both need bot pathfinding underneath them to not be an empty field.

The honest sequencing is pathfinding, then vehicles, then the huge map built around them.

## Bot navigation

Bots route through a walkable graph built from the arena description — boxes, floor slabs, carved
pits — rather than from a baked navmesh. Everything the graph needs is already plain data, so it
builds with no physics, no meshes and no engine navigation baking. That makes it deterministic,
identical in the headless harness, and cheap.

It is genuinely three-dimensional. Every arena stacks decks over floor, so a flat grid would either
ignore the upper level or confuse it with the ground beneath; each grid column therefore holds one
node per standable surface, keeping only those with headroom above. Around 1,700-1,900 nodes per
arena at 2.5m spacing.

Links are directional and model what a pawn can actually do: walk up a small step, jump up to 2.6m,
drop up to 5.5m. Being able to fall off a ledge does not imply being able to climb it. Launch pads
get explicit links to everything within their reach, computed from the same impulse and gravity the
pawn uses — without them the Atrium tower is not routable at all. Burning ground is passable but
weighted six times normal, so a route crosses it only when the detour would genuinely cost more.

Fighting outranks navigation: a bot with an enemy in sight and in range steers freehand, circling at
its class's preferred range. Routing is for travel. When nobody has been seen for four seconds, bots
rally on a shared point that rotates on a clock, which is how a stalemate on a large map resolves.

### What building it exposed

The graph is also a level-design test, and it failed the arenas immediately:

- **The 5.2m spawn decks had no way back up.** You spawned on one, dropped off, and could never
  return — nor could a bot. They now have stairs.
- **Ramps ran underneath the platforms they served.** Their top steps sat inside the catwalk above,
  failed the headroom check, and were culled — so Foundry's catwalks and Gauntlet's decks were
  unreachable on foot. The ramps now finish beside their platforms rather than under them.
- **Pit avoidance was fighting the router.** Crossing a bridge, the old look-ahead saw the moat to
  one side, vetoed the correct step and knocked the bot off its own path. It now applies only to
  freehand combat steering, where the graph is not involved.

Measured effect on the harness, damage per second by scenario: Foundry team deathmatch went from
1.4 to 17.8, Atrium from 4.2 to 10.3, and Crossfire held around 20-27 while landing more kills.

## Verticality, jetpacks and vehicles

### The upper level

Every arena gains a shared second storey: four stepped towers at mid-radius climbable from any side
and fightable at three heights, perch ledges on the perimeter walls, and skybridges joining the
tower tops. Steps rise 1.6-2.4m and ledges sit 4-6m apart, all inside jump reach, so a route across
the roofs exists without ever touching the floor.

The navigation graph verifies this rather than trusting it — if a gap were too wide, the
spawn-to-spawn connectivity test would fail. It caught two towers swallowing Atrium weapon spawns
on the first run.

### Jetpack

A pickup, not a class ability. Holding jump in the air burns fuel and pushes up at 26 m/s^2 capped
to an 8.5 m/s climb — deliberately a climb rather than a launch, so it reaches the roofs and hangs
there instead of becoming a second jump button. Four and a half seconds of fuel, shown as its own
gauge, and only while carrying one.

Every third crate holds one. Jetpack crates are taller and thinner than weapon crates, so the two
are told apart by silhouette as well as colour.

### Vehicles

Three, parked on the ring and boarded with **X**:

| | |
|---|---|
| **Car** | Fastest ground vehicle. No gun — it is a battering ram, and ram damage scales with how fast you were actually going. |
| **Tank** | Slow, 700 health, a 120-damage cannon on a two-second reload. |
| **Plane** | Flies, fastest of the three, nose-up/down on the look axis, wing guns firing 11 damage every 0.07s. |

They are simulated manually rather than with Godot's `VehicleBody3D`: the rest of the game moves
things by integrating a velocity and calling `MoveAndSlide`, the headless harness depends on that
staying deterministic, and a wheel simulation would be a second physics model to keep honest. They
are arcade on purpose — a car that needs to be driven well is a second game to learn.

A rider stops simulating itself entirely: no gravity, no collision, no shooting, position owned by
the hull. Two things moving the same transform fight each other. The camera swings to a chase view,
because a hull four metres wide cannot be flown or parked from inside its own cockpit. Destroying a
vehicle throws its driver clear rather than killing them — a vehicle should be a risk you can walk
away from, not a coin flip that deletes you.

Mounted guns carry no ammo count deliberately, and the self-test asserts it: an ammo limit on a
fixed weapon would silently disarm the vehicle for the rest of the match.

**Bots do not drive yet.** They path around vehicles as obstacles and can be run over, but choosing
to board, where to drive, and when to bail is its own decision layer on top of the navigation work,
and shipping a half-version would make them worse opponents rather than better ones.

## Making the vehicles actually drivable

The vehicles shipped in a state where you could board one and then sit there. Three separate faults
stacked, and the reason all three got through is the same reason: every vehicle check tested the
*definitions* — that a tank is tougher than a car, that a mounted gun has unlimited ammo — and not
one of them ever moved a hull.

**The raw stick never reached the vehicle.** `PawnInput.Move` is rotated into world space against
the driver's camera before the simulation sees it, which is right for a walking pawn — "up" should
mean "away from the camera" — and meaningless to a vehicle, which steers and throttles in its own
frame. Reading `Move` fed world X and Z into the steer and throttle axes, so the hull responded to
which way you were *looking* rather than which way you pushed. `PawnInput` now carries `RawMove` and
`RawLook` alongside, untransformed, and the vehicle reads those.

Plane pitch had the same shape of bug from the other direction: it read `input.Pitch`, which is the
driver's absolute view angle, and used it as a pitch *rate*. A plane is flown by holding the stick
back, not by looking upward.

**Gravity ate the acceleration budget.** The hull integrated with a single 3D `MoveToward`, with
gravity folded into the target's Y. `MoveToward` clamps the delta of the *whole vector*, so one
budget was shared between accelerating forward and falling. At 60Hz the tank's entire per-tick
budget is about 0.18 m/s and gravity alone wants 0.37 — so it could not accelerate at all while
grounded. Horizontal and vertical are now integrated separately.

**The mesh never rotated.** `Facing` steered the velocity correctly while the node's `Rotation` was
never touched, so a vehicle drove off at the correct heading while still rendering — and colliding —
pointed the way it spawned. The hull half-extents were also authored long on Z while `Forward` runs
along +X, so the boxes were broadside to their own direction of travel.

The fix is a new self-test scenario that drives. It boards one of each vehicle, holds full throttle
for 1.5 seconds and checks each hull actually travelled, then holds the stick hard over for two
seconds and checks each one turned and that its node rotation matches its heading. Its pawns are
flagged human purely so the harness input stub is consulted for them — bots are never asked for
vehicle input.

Two smaller things were making them undrivable in practice rather than in principle. There was no
on-screen prompt, so a hull was only enterable by someone who already knew the button; standing near
one now shows `X  ENTER TANK`, and driving shows the hull's name and its integrity, which is what
decides whether to keep going or bail. And interact moved from **R3** to **X** — a stick click is
both undiscoverable and easy to hit by accident mid-aim, and ejecting yourself at speed because you
gripped the stick is not a fair way to lose a fight.

### Build before you test

`godot.cmd --headless -- --selftest` does **not** rebuild the C# assembly. It will happily run the
previous build and report a full green suite for code that was never compiled — which happened here,
and the pass it reported was meaningless. Always:

```
H:/dev-tools/dotnet/dotnet.exe build
H:/dev-tools/godot/godot.cmd --headless --path H:/HitboxClone -- --selftest
```

While chasing this, the seven-second timed scenario was also found to be flaky: it asserted that
bots landed damage, and about one run in three the clock legitimately ran out before anyone
connected across a map that size. It now asserts only that they opened fire, which is the part that
is actually an invariant. A test that fails at random is worse than no test, because it teaches you
to ignore red.

## Three reports, three different root causes

### "Tanks can't shoot"

They did shoot. The drive test proved it: three rounds in five seconds, exactly the cannon's stated
rate. What they could not do was *hit anything*. The gun fired straight down the hull heading at
exactly zero elevation, so it could not reach anyone on a deck, on a tower, or on the ground close
in front — and on maps that had just gained a whole upper storey, that is most of the people in the
match. With a 2.1 second reload and no crosshair anywhere on the driving HUD, there was also nothing
to tell you a round had left the barrel. It read as a gun that did not work.

Gunned ground vehicles now have a real turret. The look stick lays it, independently of the hull, so
you drive with one thumb and aim with the other exactly as on foot; it elevates from −0.45 to +0.85
radians; the barrel mesh swings with it; the chase camera follows the gun rather than the hull,
because a camera locked to the hull means aiming blind; and a reticle is projected at the point the
round will actually reach, which is not the middle of the screen — the camera sits behind and above,
so a centred crosshair would be lying. It goes bright when the gun has reloaded.

The plane keeps its fixed forward guns. It aims by flying, which is the point of a plane.

### "The driving left/right axes are inverted"

They were. Heading runs anticlockwise from +X in the atan2 convention the rest of the game uses, so
turning right *raises* it — and the steer input was negated. The test that was supposed to cover
this compared the absolute size of the turn, which an inverted axis passes perfectly happily. It is
signed now.

### "You said there are different maps, but they're the same"

Also true, and self-inflicted. When verticality was added it went in as one shared block of towers,
ledges and skybridges laid over all four layouts, on the reasoning that the floor plan is what gives
a map its character. That reasoning was wrong: the vertical layer is the largest and most visible
structure on the map, so making it identical made all four read as the same place no matter how
different the ground underneath was. The four floor plans had not changed at all — they were just
buried.

Each layout now has its own second storey, echoing its own floor plan rather than ignoring it:

| Arena | Upper level |
|---|---|
| **Crossfire** | A high cross spanning the whole arena, meeting over the central tier. The best position on the map is also the most exposed. |
| **Foundry** | Gantries hugging the walls in a full circuit, with a second level on the north wall only. Asymmetric on purpose — it rewards learning the map rather than reading it. |
| **Atrium** | A perimeter balcony with the middle left completely open. A ring you circle and drop from, doubling the moat's logic one storey up. |
| **Gauntlet** | Two staircases climbing from opposite ends to a single perch above the pit. The whole arena becomes a race for one spot, with the longest fall on any map underneath it. |

The spawn-clearance test caught Atrium's balcony running straight through all four spawn decks on the
first attempt — you would have started the match with a walkway through your head.

### Arena overviews in the screenshot harness

The reason the identical-maps problem survived so long is that every screenshot was a first-person
eye-level view from a spawn, which shows the two metres in front of you and tells you nothing about
a layout. Four maps could have been byte-identical and the captures would have looked the same.

`--shots` now also writes one top-down overview per arena, built from a small `ArenaPreviewScreen`
that renders the geometry alone — no pawns, no HUD. The camera is tilted rather than a true plan
view, because a plan view flattens every height to nothing and height is the thing worth checking.

## Tank pass

Four reports, and the first three were separate bugs rather than tuning.

**The cannon fires shells now.** Direct hit dropped from 120 to 70, with 135 blast damage over an
8.5m radius. The splash is deliberately the larger half: the cannon is meant to be aimed at the
ground under someone rather than threaded at them, which is what makes a two-second reload worth
carrying. Blast damage is a property of the weapon, not the vehicle, so any gun can be made
explosive — a rocket pickup would need no new machinery.

**Vehicles were indestructible, and not by design.** The projectile hit test only ever recognised
pawns; a round that struck a hull simply stopped, and `Vehicle.TakeDamage` was dead code that had
never once run. The same is now true of splash: a blast damages hulls as well as bodies, so a shell
landing against a tank does something to it whether or not the ray happened to hit. Nothing in the
suite noticed for the same reason it never noticed the vehicles couldn't drive — the tests only ever
checked the *definitions*, and nothing had ever shot one.

Wrecks come back after 22 seconds. Now that hulls can actually be destroyed, a long match would
otherwise burn through all three in the first few minutes and never see another one.

Destroying a hull sets off a 90-damage blast over 9m. A tank going up beside you should be an event,
not a silent change of colour. Chain reactions are possible and bounded — a dead hull is skipped, so
each can only go up once.

**Tracers were never rotated.** Same class of bug as the hull that wouldn't turn: shot meshes had
their position updated every tick and their orientation never set, so every round in the game, from
every gun, in every direction, drew a streak lying flat along world X. A shot fired sideways looked
like a bar of soap sliding through the air. Tracer boxes are now built long on Z and pointed down
their own velocity.

**The chase camera goes through walls no longer.** The boom is swept as a sphere rather than a ray —
a ray threads gaps the near plane cannot, which reads as the wall flickering in and out — and the
camera sits at the last clear point. When a wall leaves too little of the boom to be worth using, it
goes over the top instead rather than jamming against the hull: keeping the camera out of the wall by
filling the screen with three metres of your own tank is not a view. It also snaps rather than eases
when the clear position is much nearer than where the camera is, because easing into a wall means
several frames spent inside it.

### The rest of the pass

- The on-foot crosshair was still being drawn while driving, at screen centre, next to the vehicle
  reticle pointing somewhere else. Two crosshairs disagreeing is worse than either alone.
- Firing the cannon shoves the hull backwards. It is feedback as much as physics — with a two-second
  reload there was almost nothing telling you the gun had gone off.
- A muzzle flash and a reload bar under the reticle. "Can I fire yet" is the single most useful thing
  a driving HUD can say.
- The firing vehicle is excluded from its own shell's ray. Without it a hull that drove forward into
  its own round would blow itself up, which is not a skill expression.
- Wrecks reset cleanly on respawn — turret, pitch, cooldown and hull colour — rather than coming back
  mid-reload with the gun pointing wherever it died.

A capture of a tank being driven was added to `--shots`, which is what surfaced the double crosshair
and the too-tight camera. The harness boards it through the ordinary public `Board` call rather than
a debug flag on `MatchScreen`, the same way the scope capture uses a scripted device instead of a
test hook on the production screen.

## Destructible everything

Every wall and platform on every map comes down to explosives and rebuilds itself afterwards.
Blowing a skybridge out from under someone, or opening a new door through the side of a room, is a
play.

**Only explosives.** That half is still deliberate.

If small-arms fire eroded the map, every long match would end in a flat box, and nobody would have
*chosen* that — it would simply have happened, a few stray rounds at a time. A shell is a decision.
The rule falls out for free rather than needing a special case: structure takes damage from `Blast`,
which is the only path an explosion travels, so anything that explodes brings it down and anything
that does not cannot.

The other half used to be "and only walkways", on the reasoning that blowing out a bridge changes the
fight while blowing away the towers holding the upper storey up changes the map into a different,
worse map. The reasoning was sound and the result was wrong. What it actually produced was five to
eleven fragile blocks per arena — one skybridge that everybody shot at, and three hundred blocks that
were scenery. The map read as painted on.

Erosion is prevented by the **rebuild timer** instead of by immortal geometry, which is a better
answer to the same worry: nothing stays down, so a long match is an arena that keeps changing shape
rather than one that wears away. Measured across a full harness run, no scenario ever ended with more
than three of roughly three hundred pieces on the floor, and the suite fails if a quarter of any map
is down when the whistle goes.

Two exceptions, and only two:

- **The perimeter.** A hole in the outer wall is a way out of the match, and out of bounds is fatal
  with no appeal. The four walls are built first, counted as `Arena.PerimeterBlocks`, and skipped.
- **Anything flush with the floor** — launch pad plates, burning ground. That is paint, not
  something you could knock down.

### Toughness is size

`Match.StructureHealth` is the block's volume, clamped between 120 and 900. That gives the reading you
want from across a map without anybody being told the rule, because it is the rule everything in the
physical world follows: a thirty-metre skybridge goes to one tank shell, a machine-hall wall takes
three or four, and the base tier of a corner citadel is a project. Rebuild time scales with it too, so
blowing out something heavy is a play that lasts long enough to be worth making.

| Arena | Destructible | Permanent | Flimsy (120) | In between | Heavy (900) |
|---|---|---|---|---|---|
| Reliquary | 299 | 14 | 164 | 114 | 21 |
| Furnace | 282 | 27 | 160 | 102 | 20 |
| Glasshouse | 262 | 14 | 149 | 92 | 21 |
| Thousand Rooms | 382 | 14 | 199 | 163 | 20 |

The spread is checked, not just the range: a map where everything is either the floor value or the cap
has no scale on it, only two kinds of wall.

Damage is measured to the *nearest point on the box*, not to its centre — a thirty-metre skybridge
measured centre to centre would be untouchable at either end. Structure darkens as it takes damage
(against its own maximum, so a heavy wall reads correctly too), so one about to go is readable before
it goes.

The suite now takes the heaviest standing piece on the map and puts real rockets into it through the
real `Blast` path until it falls over — nine of them — then waits out its rebuild clock and checks it
came back at full health. Everything else in this section drops walkways by calling the test hook,
which proves the navigation graph reacts and proves nothing at all about whether a player can bring a
wall down.

Anyone standing on it simply falls. No impulse is applied: a push would read as a launch rather than
as the floor giving way.

### The navigation graph was the hard part

The graph is built once from arena data at match start and never rebuilt. Without doing something
about it, a bot would route happily across a bridge that was no longer there and walk into the drop.

Rebuilding the graph on every explosion was the obvious option and the wrong one — it is two thousand
nodes of grid construction and A* linking, mid-fight, several times a match. Instead every node now
records the block holding it up, and destroying a block disables its nodes. `AStar3D` skips disabled
points natively, and `NearestNode` skips them too, so a bot standing where a deck used to be snaps to
the ground below rather than to the deck that is no longer there.

Disabled rather than removed, because point ids are the graph's identity: renumbering them would
invalidate every path already in flight.

### Two things the tests caught

`ProcessMode = Disabled` does **not** disable collision on a `StaticBody3D` — it gates the process
callbacks a static body does not use. The first version left invisible walkways you could still stand
on. The collision shape is now disabled directly, and deferred, because reshaping the world inside the
physics step is how you get a body that is half there for a frame.

And the first version of the destructibility test failed against correct code. It probed the walkway's
centre point for a navigation node — but on Crossfire the crow's nest sits directly over the middle of
the bridge, so that column legitimately carries no node for lack of headroom, and `NearestNode`
answered with the floor ten metres below. The test now asks the graph which node stands on the block
rather than guessing a world position that might not have one.

## Bots drive

Driving is a separate decision layer from walking, and deliberately not built on the navigation
graph. A hull is nothing like a pawn: it cannot strafe, cannot climb the towers, turns on a radius,
and aims with a turret that moves independently of where it is going. Routing a tank through the
pawn graph would send it at a staircase it cannot climb and leave it grinding on the bottom step.

So driving is **steering, not pathfinding**: point at the destination, feel ahead with three
whiskers, and back out when stuck. One forward ray tells you that you are about to hit something but
not which way to go round it, which is why there are three. The arenas are open enough at ground
level that this gets a vehicle where it is going, and a vehicle that occasionally takes the long way
round reads as a driver rather than as a bug.

**Deciding to board.** A vehicle outranks a crate and outranks the objective — it is the biggest
single swing on the map. But never while someone is shooting at you from close range: walking thirty
metres across open ground to reach a tank is how you die on the way there.

How far a bot will walk for one is a new difficulty lever, `VehicleAppetite`. A Veteran crosses the
map for a tank; a Recruit only takes one it nearly trips over. A Recruit that commandeers a tank is
not a Recruit any more.

**Deciding to leave.** Hull below 28 percent, or nothing to do with it for 22 seconds, or wedged
somewhere it has spent six seconds reversing out of. Leaving a wreck-in-waiting beats dying inside
it, and a bot that never gets out hogs the only tank on the map for the whole match. There is a
14-second cooldown before it will look for another, or it walks two paces from the hull it just
abandoned, decides a vehicle is the best thing available, and climbs straight back in.

**Aiming.** The turret is slewed at the difficulty's own aim rate, exactly as on foot. A bot that
snaps a cannon onto you the instant you appear is unfair in a way that has nothing to do with the
vehicle being strong. With an explosive shell it aims at the ground under you rather than at your
chest, because the splash is the larger half of the cannon and a near miss at your feet still kills.

Damage per second across a Normal deathmatch went from about 19 to about 31 with this in.

### Three bugs this surfaced, all of which affected players too

**A bot in a vehicle could never see anything.** `HasLineOfSight` casts from the looker's eye and
excluded only the looker — so for a driver the ray left the seat, struck the driver's own hull two
feet away, and reported cover. Every bot that boarded a tank sat in it in silence. Both ends now
exclude their vehicles, which also fixes a target *in* a vehicle registering as permanently behind
cover.

**Nothing kept a flying vehicle inside the map.** The perimeter walls are sixteen metres tall with
open sky above them. A bot flew a plane straight over one and out into the void — the arena-bounds
invariant caught it — and a player could have done exactly the same on their first flight. Flying
hulls are now clamped to the arena and to a ceiling just under the wall tops. Clamped rather than
bounced: a boundary should feel like the end of the map, not like hitting something.

**A driver killed in the seat left the hull permanently occupied.** Nobody else could board it, and
the vehicle step kept asking a dead brain how to drive. Death now ejects.

One thing that changed for players rather than bots: a blast no longer hurts anyone inside a vehicle,
because the hull already absorbed it above. Without that, firing the cannon at anything nearby
wounded your own driver, which made it useless at exactly the range it should be strongest.

### Testing a decision instead of waiting for one

Waiting for a bot to happen to walk to a vehicle inside a twenty-second window would make this a coin
flip, and a coin-flip test teaches you to ignore red. The checks pose each decision to the brain
directly with the world arranged so there is a right answer: a bot beside a free hull decides to
board; a driving bot works the sticks; a bot inside a hull can see out of it; a bot in a wreck bails;
a bot that just bailed does not climb straight back in.

The first version of the line-of-sight check failed against correct code because it put the target
twenty metres along +Z from a vehicle parked on the ring — which is twenty metres into the perimeter
wall. It aims at the middle of the map now.

## Five times the floor

278 x 206 metres, up from 124 x 92. Two decisions worth stating plainly, because both could
reasonably have gone the other way.

**Five times the *area*, not five times each dimension.** Five times each side would have been
twenty-five times the floor — a 620 x 460 metre field that four players would rattle around in and
never meet on. Five times the area works out to 2.24 times each side, which is still a completely
different map to move around.

**Grown outward, not rescaled** — for the second time in this project and for the same reason. Every
jump gap, step rise and ledge spacing in the core is tuned against the pawn's actual apex. Multiply
the coordinates and all of them become gaps nobody can cross. So the core is untouched and the new
floor became districts around it, laid out at a deliberately coarser grain.

### What is out there

| | |
|---|---|
| **Causeways** | Four long stepped climbs out of the core onto raised, railed roads. The walking route to everything beyond. |
| **Corner citadels** | Stepped fortresses with stairs on two faces and an elevator up the middle, plus a perch above the top tier. |
| **Trench gauntlets** | Six carved trenches per map, each with a wall that sweeps the length of it. |
| **Redoubts and scatter** | Low cover and small stepped structures filling the ground between landmarks. |
| **Loot and vehicles** | The strongest guns and all four vehicles now live out here rather than in the core. |

The scatter exists because the first version of the outer band was a landmark every eighty metres
with nothing between them, and crossing it was a long walk in the open with no decisions in it. The
pieces are deliberately low and plentiful rather than tall and few: what the band needed was somewhere
to break line of sight every few strides, not more silhouettes on the skyline.

### Elevators and push walls

Both are the existing moving-platform machinery with one field each added, rather than new systems.

An elevator is a vertical platform with **dwell** — a fraction of each leg spent waiting at the end.
Without it a lift is a timing puzzle rather than something you ride, and the point of an elevator is
that it is the easy way up.

A push wall **shoves** instead of blocking. This is the one piece of level machinery that has to
reach into the simulation: Godot resolves a character against a moving body by stopping the
character, not by carrying it, so a push wall left to the physics engine is just a wall that happens
to travel. Anyone it catches is carried with it and given a shove — over a trench, fatally. The carry
matters as much as the shove: without it a pawn wedged against the far side is simply overrun,
because the wall covers more ground per tick than the shove has time to move them.

**Nothing is gated behind an elevator.** Navigation nodes are built from static geometry, so a weapon
that could only be fetched by riding one would be a weapon no bot could ever contest. The stairs are
always the route that works; the elevator is the one that gets you there first. Bots do not ride
them yet — that is the next real gap in the AI after driving.

### Engagement had to be defended

Five times the floor scattered the bots and damage per second fell from about 30 to about 10 — a
third of what it had been. A bigger map that is quieter is not a better map.

The cause was the rally logic: when nobody has seen anyone for a few seconds, bots converge on a
shared point drawn from the capture-zone list, and the new districts had added four zones out in the
far corners. Four bots, four corners, no fights. Rally points are now a separate list containing only
the core spots, while the capture zones keep the full set for King of the Hill. With that plus a
wider loot-detour radius, engagement came back to 15–27 damage per second.

### Four bugs, all of which the tests caught

**Corpses fell out of the world.** A pawn killed by a fall kept accelerating downward for the entire
respawn timer. Latent for a long time and harmless while the pits were shallow; the new trenches gave
it somewhere deep enough to trip the arena-bounds invariant. It also pointed the death camera into
the void.

**Vehicles had no kill plane at all.** A hull driven into a trench fell forever, and its driver fell
with it — out of reach of the pawn fall check, which is skipped for anyone being carried. A probe
vehicle was found six hundred metres below the map. Vehicles are now destroyed below the plane, and
the wreck settles there.

**A tank's own shell damaged its own hull.** Splash damages vehicles, and the loop had no exclusion
for the vehicle the round came out of. The crew were shielded and the hull was not, so firing at a
wall inside the blast radius quietly wrecked your own tank.

**Two placement mistakes in the new geometry** — a redoubt built on top of two vehicle spawns, and
citadel tiers four metres high with a roof at exactly the height the navigation graph refuses to
route along, which made the fortress roofs invisible to every bot. Both were caught by the existing
spawn-clearance and reachability checks rather than by playing.

### And one test that was wrong twice

The line-of-sight check places a target near a vehicle and asserts a driver can see it. It picked a
fixed offset and hoped the ground was clear — first twenty metres along an axis, which was twenty
metres into the perimeter wall, then twenty metres inward, which after this change ran through one of
the new redoubts. Both times it failed against correct code. It now stands the target three metres
clear of the hull, where the only thing that can be in the way is the hull itself — which is the
property it was always trying to test.

## Graphics pass, after the expansion

The renderer was tuned for a 124 x 92 arena of about seventy blocks. It is now driving a 278 x 206
arena of about 225, and three things had quietly stopped working.

**Fog was ranged for the old map.** It saturated fully by 140 metres — most of the way across the
previous arena and barely a third of the way across this one — so everything past the middle distance
sat in one uniform haze with no depth in it at all. Re-ranged to 45–330 metres, with the colour
matched to the sky horizon so distance dissolves into the sky rather than into a grey band in front
of it, and the curve held below full: at full density a distant enemy vanishes, and on a map this
size that is concealment rather than depth cueing.

**The palette had collapsed.** Wall, cover and deck were within about eight percent of each other in
value and all the same hue, which was survivable on a small map and became a single grey mass at this
scale. They are spread properly apart now — structure darkest and coolest, cover pale, walkable
surfaces mid, and the accent colour reserved for the highest ground. Still a restrained palette; it
is now telling you what kind of thing you are looking at.

**Height was invisible.** This is the one that mattered most. The map has a second storey,
skybridges, three-tier citadels and a crow's nest, and flat colour told you nothing about any of it —
a bridge ten metres up and the floor beneath it were the same swatch. Surfaces now lighten and warm
as they rise, get slightly glossier with height so a raised walkway catches a highlight the floor
does not, and their edge trim brightens with it, which turns the lip of a platform into an altimeter.
That last one matters because the lip is the part of a box you are actually looking at when you are
deciding whether you can make the jump.

Two smaller things. Every block gets a deterministic wobble of about three percent in value, which is
far too small to read as colour and just enough to stop a sixty-metre causeway painted one exact
value from looking like a texture-less plane. And everything outside the old arena edge shifts
slightly warm, so you know you have left the core without having to look at anything in particular.

Shadows stayed at a middle distance rather than being stretched over the full 345-metre diagonal.
One orthogonal split has to cover whatever it covers, and spreading it that far would spend the
entire shadow map on ground nobody is looking at closely. The re-ranged fog takes over past it,
which is about where a shadow would have become a grey smudge anyway.

### The map preview needed its own environment

The overview screen renders from 250 metres back, where aerial perspective tuned for a player
standing in the arena hazes the entire map to nothing — the first capture after the fog change was
flatter than the one before it. `Arena.Build` now takes a fog flag and the preview turns it off.
Depth cueing is for looking *through* a space, not at one.

## Weapons you can see, and a map vehicles can leave

### The held weapon now matches the weapon

The view model was built once from the class and never rebuilt, so picking up a minigun or a sword
changed everything about how you fought and nothing about what was in your hands — the strongest
signal the game has for "you are holding something different" was simply absent.

Weapons now carry a silhouette, and the model is rebuilt whenever the weapon changes — on pickup and
again when a pickup runs dry and the class weapon comes back. A rifle, a shotgun with its pump, an
SMG with a magazine, a sniper with a scope proud of the receiver, a minigun as a ring of six barrels
in front of a heavy body, and the sword as a canted blade with a lit edge and no muzzle at all.

The first sword built its hilt, guard and blade as three independent pieces and canted the blade
steeply, which left it hanging in mid-air well clear of the grip: three objects in shot, none of them
apparently attached. It is assembled as a chain now — grip, guard, then a blade pivoted at the guard
so the cant swings the tip rather than the whole weapon.

### Vehicle spawns are derived, not placed

A player reported platforms boxing in the tank. Checking it properly turned out much worse than one
level: **every vehicle on every map** was walled in, with about six hundred square metres of reachable
ground and no route to the core at all.

Hand-placing them had failed three times running, and the reason is that the check was asking the
wrong question. Clearance was never the problem — connectivity was. A spawn can be perfectly clear
and still be fenced in by a trench on one side, a citadel on another and a redoubt on a third.

So they are derived. A vehicle cannot climb — it is a CharacterBody3D with no step handling at all,
so anything above a kerb is a wall to it — which means it needs its own reachability sweep rather
than the pawn navigation graph, where those same blocks are stairs. The arena floods the ground a
hull can reach, then picks four well-separated cells out in a band from the core with room to turn.
Roughly 1,300 cells reachable per map now, and a route to the middle from all four.

Two bugs surfaced on the way. The flood seeded from the arena centre, which on three of the four
layouts is a solid structure, so it started inside a block and died before leaving it. And the
"nearest drivable point" scan used a grid offset from the one the flood used, so on Foundry it
returned a point that fitted a hull, the flood rounded it to the neighbouring cell, that cell was
inside the central spine, and the entire map came back undrivable.

### Bots that arrive somewhere

Bots were walking about 103 metres per twenty-eight seconds — genuinely moving, and on a 278-metre
map that means never arriving anywhere, which is exactly what "barely moving" looks like from the
outside.

Two causes. Sprint required *no line of sight* and a big range error, which on the old map was most
of the time a bot was walking and on this one almost never: a bot crossing to a crate usually has
someone in view somewhere, so it walked the whole way. It now sprints whenever it is travelling to
something more than fourteen metres off.

And nothing noticed a bot pushing into a wall. The router plans over standable surfaces; it does not
know about the corner of a crate the capsule is caught on, and the outer districts added a great deal
more to catch on. A bot that intends to move and is not moving now sidesteps.

Measured back at 137–207 metres, and Team Deathmatch damage per second went from 6.7 to 12.

### Aim assist

Two effects, which is what console shooters actually do.

**Friction** cuts the look rate while the crosshair is near someone, so the last few degrees onto a
target are fine control rather than the same coarse sweep as turning to face a doorway. This is most
of the feel, and it is the honest answer to "is it too sensitive or not helpful enough": one rate has
to serve both spinning round and holding a head at forty metres, and no single number does both.

**Magnetism** rotates the view gently toward the target, *in proportion to how much the stick is
already moving*. That condition is the whole trick — help while you are tracking, nothing at all
while you are still. Applied unconditionally it takes the aim off you, which reads as the game
fighting your hands.

Neither fires the gun for you: there is no bullet bending, and shots still go exactly where the
crosshair is. Line of sight is required so the crosshair never sticks to someone through a wall,
which would also quietly tell you where people are. Off for mouse users, and settable Off / Light /
Standard / Strong, defaulting to Standard.

### Leaving the map, and being crushed

Both are now fatal, and one of them was a real bug rather than a missing rule.

The shove from a moving wall assigned the pawn's position directly, which pushed players *into*
whatever was behind them. Godot resolves a body that starts a frame inside geometry by ejecting it —
hard, and in whatever direction the solver likes. That is what was firing players out of the map.
Pawns are moved rather than teleported now, so they cannot be pushed through anything.

On top of that: outside the play boundary is death, no exceptions. There is no legitimate way to be
out there, every route that put someone outside was a bug, and the honest response to being somewhere
impossible is to die rather than float behind the level shooting into it. And a pawn squeezed inside
solid geometry for a third of a second dies — a moving wall against a static one leaves nowhere to
be, and the old outcome was to be squirted out of the gap at whatever speed the solver chose.

### Two smaller things

The results screen offers **Play again** first, then Change setup, then Main menu. Going back through
the title screen, mode select and the lobby to replay the same match was four screens of nothing.

And the score limit is **per mode** now. One shared 5-to-50 scale meant King of the Hill — which
scores a point per second held — offered fifteen seconds of holding as its default match, over before
anyone had crossed the arena to contest it. It defaults to 150 points on a 50–500 range; Elimination
counts rounds and steps by one rather than by five.

## Two weapons, med kits, and holding to take

### A loadout instead of a single gun

You carry two weapons and switch between them with **D-pad Up**. Slot one starts as your class
weapon; the second starts empty. A first pickup fills the empty slot and is drawn; a third, with both
slots full, replaces the one in your hands — the Halo rule, and the right one, because what you are
holding is the thing you have already decided you like least by having swapped away from it.

Carrying two and choosing is a far better decision than carrying one and losing it. A sword is
devastating in a corridor and useless across a causeway, and before this, taking one meant giving up
every long shot until it ran dry.

Running a pickup dry empties its slot. If that leaves you with nothing, the class weapon comes back —
you are never standing there unarmed.

### Weapons are held for, not walked over

Taking a weapon needs the interact button held for four tenths of a second, with a filling bar under
the crosshair. That is because a pickup can now *cost* you the weapon in your hands, and that has to
be a decision rather than something that happens while running past.

Health and jetpacks stay walk-over. Neither takes anything away, and fumbling a heal mid-fight
because you had to stand still and hold a button would be miserable.

A press still boards a vehicle, and the two never fight: the press only counts when a hull is
actually in reach.

### Med kits

Green cases, 45 health, walk-over. Bots below 55 percent health will break off and go for one, unless
the fight is inside twelve metres — disengaging at range to heal is correct and disengaging at five
metres is dying more slowly.

They took **zero** in every scenario at first, and the crates were not the problem. Kinds were cycled
on the raw spawn index, and every layout declares its three core spawns first while the shared
district pass appends eight more afterwards — so every med kit and all but one jetpack landed out in
the outer districts, which bots reach rarely and rally in never. Kinds are assigned in order of
distance from the middle now, and there is an assertion that each arena keeps at least one med kit in
the core. One to two are taken per match.

Two related bot fixes fell out of the same investigation. Bots would not have taken weapons at all
any more, because they had no idea the button now needs holding — they hold it when standing on a
crate they would take. And the loot detour was gated on "carrying the class weapon", which with two
slots meant a bot stopped looking the moment it picked anything up; it is gated on having a free slot.

### Planes are out of rotation

The definition stays, and so does everything that flies it — the flight model, the bot's altitude
hold, the arena clamp. It is simply not in the spawn list.

A plane cruises at forty-eight metres a second, which crosses this arena in under six seconds. It
needs a map with room to turn around in, and until there is one it is a novelty that spends most of
its life against a wall. `Vehicles.All` still has all three for anything that reasons about the set;
`Vehicles.Spawnable` is what maps draw from.

## Menu backdrops

The menus can carry painted backdrops. This is the only place in the project that reads an asset
file, and that boundary is deliberate: arenas, weapons, pawns, sky and sound all stay generated, and
nothing in a match depends on an image existing.

Images live in `assets/menu` as `title`, `mode`, `lobby`, `options` and `results`. Every lookup is
allowed to fail — a missing folder, a missing file or a corrupt image falls back to the procedural
background, so the game runs unchanged with no assets at all and they can be added one at a time.

**Loaded at runtime rather than preloaded through `res://`.** Godot's import pipeline only runs when
the editor opens the project, so a preloaded texture would mean dropping in a new image did nothing
until you launched the editor once. `Image.LoadFromFile` sidesteps that entirely: a file appears in
the folder and the next launch uses it. Failures are cached alongside successes, so a missing file
costs one lookup per session rather than one per frame. Headless runs never load or draw one.

They are **cover-fitted and centre-cropped**, never stretched. A backdrop is a picture of a place,
and a place with the wrong aspect ratio reads as broken in a way a crop never does.

### The scrim is most of the work

Menu text laid straight over a painted image is unreadable wherever it crosses something bright, so
the backdrop gets a light overall dim, a heavy wash under the header, another under the hint bar,
and a soft band down the middle.

Two things were wrong in the first pass and the captures showed both. The band was down the **left**,
on the assumption that menu rows are left-aligned — they are not: title, mode select and options all
centre their block, so the scrim was protecting empty frame while the text sat over whatever the art
put in the middle. And the overall dim was nearly half a stop of black, tuned against a bright
stand-in; over art that is already dark it made mud. The band is centred and fades to both edges now,
which leaves the sides of the picture visible, and the dim is much lighter.

The whole path was verified before any real art existed by copying two screenshots the `--shots`
harness had already written into the folder and capturing the menus — loading, cover-fit, scrim and
fallback all confirmed, then the stand-ins removed.

## Faction characters

Four rigged characters, one per faction, replacing the box pawns.

### They were measured before they were used

The brief that came with them warned against importing Meshy output blindly, and set a budget. So
they were checked against it first, by parsing the glTF headers rather than by trusting the exporter:

| | Budget | Actual |
|---|---|---|
| Triangles | ≤7,000 | 2,679 – 2,916 |
| Materials | 1–2 | 1 |
| Skeleton | one shared | 24 joints, identical across all four |
| Texture | 512–1024px | **2048×2048** |

The meshes are well behaved. The texture was the only violation and the entire reason each file is
7MB, so it is downscaled to 1024 on load — at splitscreen distances a character occupies a couple of
hundred pixels of a quarter-screen viewport, where 2048 is four times the memory for nothing. The
source files are left untouched and the number is one constant.

### Loaded at runtime, like the backdrops

Through `GltfDocument` rather than as imported scenes, for the same reason: Godot's import pipeline
only runs when the editor opens the project, so an imported model means a new export does nothing
until you launch the editor once. A file is dropped in and the next launch uses it.

Each faction is loaded once and instanced per pawn, so four pawns wearing one faction share one mesh
and one texture.

The walk and run cycles arrive as two separate exports, each carrying a full duplicate of the mesh
and texture. Only the animation is wanted from the second, so it is loaded, robbed of its clips and
thrown away rather than kept in memory beside the first.

### Wired in without disturbing the simulation

The model goes into the same `rig` node the boxes used, which is what keeps everything hanging off it
working unchanged — the first-person cull layer, the turn to face, the death topple, the crouch
squash all address the rig rather than its contents. Faction is presentation only; nothing in the
simulation reads it, and the collision capsule, hitboxes and headshot line are exactly as they were.

Each model is scaled from its **measured** height rather than a shared constant. The four are not the
same size in their own units — 1.60 to 1.70 — and every pawn has to end up matching one capsule.

Animation is speed-matched rather than switched at a threshold: the clip plays at a rate proportional
to ground speed, so the feet keep up with the floor instead of sliding, and slowing down slows the
cycle rather than snapping to a different one. Below walking pace it holds a pose rather than jogging
on the spot.

### Two bugs this surfaced

**The invulnerability flash dereferenced null ten thousand times a run.** It recoloured
`MaterialOverride`, which every box rig had and no imported model does — a textured model carries its
own surface material. It now lays an additive overlay over the top instead, which flashes without
throwing the artwork away.

**A pawn with no mesh never turned to face anything.** The rotation was written below an early return
that skipped it whenever there was no body to tint. Invisible while everything was boxes and a real
bug the moment a rig could fail to build.

### Verified by looking

Two captures were added before anything was wired up, because loading a rigged animated model runs
through five systems at once — glTF import, texture downscale, height normalisation, animation merge,
render layers — and finding out whether any of it worked by starting a match would have been guessing
five times over. `00-factions` lines all four up at pawn scale beside a reference box of the real
capsule dimensions, and `00b-closeup` stands two pawns face to face in a live match.

### Known: leaked RIDs at exit under `--shots`

The screenshot harness quits abruptly while the last match is still standing, and Godot reports the
four character meshes as leaked. Clearing and then disposing the model cache at shutdown did not shift
it, which points at the abrupt quit rather than the cache. It is exit-only noise with no effect on
play, and it is recorded here rather than chased further.

## Controls and feel pass

**The d-pad no longer moves you.** It used to override the stick so digital-only pads worked, and the
cost was that brushing it mid-fight snapped you to eight directions at full speed. It still navigates
menus; every pad this game targets has two analogue sticks.

**Only Start opens the menu.** Back was bound to both the Back button and Y, and the match treated
Back as pause — so Y opened the pause menu mid-fight. The match now consumes Back and does nothing
with it, and Y is free.

**Weapon swap moved to Y**, which is where a shooter player reaches for it and which the change above
vacated. Nothing sits on the d-pad any more, and there is now a check that no two actions share a
default input — it caught nothing, but it is the kind of thing that goes wrong silently during a
rebind like this.

**Aim assist halved, and it no longer tracks.** Friction dropped from 0.55 to 0.28 and pull from 1.35
to 0.68, but the bigger change is that magnetism now decays: the pull is full strength when the
crosshair first finds someone and gone within half a second. The timer resets when the assisted
target changes, so swinging onto a new enemy gets the same brief hand while holding one gets nothing.
That is the difference between an aim assist and a lock — before this, a target that kept moving
dragged your aim along behind it.

Friction stayed on continuously, because it is a different thing: it slows your look rate near a
target so the last few degrees are fine control, and it never moves your aim anywhere you did not
push it.

**Vehicles explode properly.** A wreck used to throw exactly the same puff of debris a person does,
for an eight-metre hull worth up to seven hundred health. It now gets three layers — a fireball,
heavy debris thrown wide and low, and a slow smoke column — plus a brief point light so the blast
lights the walls around it instead of being a sprite pasted over them. One light for under half a
second is affordable even in four-way splitscreen.

## Faction specials

The special moved from class to faction. That is the split the setting wants: class decides how you
shoot, faction decides what you can do that nobody else can — and while the special hung off class,
faction was a skin with no gameplay identity at all.

It stays on **LB**, which was already the special button. No new binding.

Dash stayed. It was tempting to replace it, but the game is tuned around it: dash grants the
invulnerability frames that make it the core defensive skill, deals 26 damage as a ram, knocks people
into hazards the outer districts were laid out for, and is what bots use to break a fight they are
losing. Removing it would have taken out a defensive option, a damage source and a level-design
assumption in one go.

| Faction | Special | What it does |
|---|---|---|
| Vessels | **Second Wind** | Pays back 75% of the damage you have taken in the last six seconds |
| Custodians | **Revelation** | Outlines every enemy through walls for 4.5s, for your whole side |
| Garden | **Bloom** | An 8.8m patch that heals your side 30/s and slows everyone else to 74%, for 9s |
| Muses | **Understudy** | A decoy walks on while you drop off the targeting list for 5s |

Each is the faction's argument as a verb rather than a stat line. Second Wind is worth nothing at
full health — the faction that believes humanity was mortality gets the ability that is *about*
having been hurt, and rewards standing there rather than avoiding it. Revelation marks the target
rather than the caster, so the knowledge outlives the Custodian who found it. Bloom is the only
ability in the game that makes ground worth standing *on* rather than worth avoiding. Understudy is
the only one that lies.

**The bluff works on bots too.** A Muse who has just left a decoy drops off bot target selection
until it expires, and a pawn lit by Revelation is preferred by every bot on that side. Without both,
the two information abilities would have been anti-human weapons rather than abilities.

### Tested by posing, not by waiting

Each special is fired at a situation arranged to have a right answer — hurt a pawn and check Second
Wind pays it back and empties the bank, plant a Bloom and check it heals its planter and slows
someone else, cast Revelation and check it marks the enemy and never the caster. Waiting for a bot to
spend the right ability at the right moment inside a test window is a coin flip, and this harness has
been bitten by that twice already.

Posing "has just been hurt" needed one narrow hook: a respawned pawn carries spawn protection and
cannot be damaged at all, which is correct in play and made the situation impossible to arrange.

## Block surfaces, and the flashing edges

Every block used to be one flat colour with four glowing bars stuck along its top edges. A player
reported the edges flashing, and the cause was in the trim itself: each bar had **two** faces exactly
coplanar with the block it was decorating — its top face on the block's top, and its outer face on
the block's side. Two hundred blocks times four bars times two coincident surfaces is z-fighting
along every edge in the arena.

The trim is gone rather than nudged. Insetting it would have stopped the fight, but the bars only
existed because a flat-coloured box has no surface to read, and that is a material problem being
solved with nine hundred extra mesh instances in four splitscreen viewports.

**Blocks are plated now.** A tiling panel pattern, generated rather than loaded — greyscale, so one
texture multiplies through every block colour in every arena, with two panels across the tile so a
seam lands every 1.3 metres rather than only at the repeat. Fine deterministic grain on top, and a
gentle dome per panel so a large flat face is not one dead value.

**Projected triplanar from world space, not from UVs.** These are boxes of wildly different sizes all
sharing one `BoxMesh`, so a sixty-metre causeway and a one-metre crate have identical UVs — any
UV-mapped texture would stretch across the first and vanish on the second. World-space projection
gives every surface in the arena the same texel size, which is the only way the plating reads as one
material rather than as a scale error.

The first capture at map-overview distance showed the seams breaking into speckle on the perimeter
walls: plain trilinear filtering picks one mip for a surface seen at a glancing angle, and a long wall
from across the arena is nothing but glancing angles. Anisotropic filtering is exactly the case that
exists for, and on one shared texture it costs nothing worth measuring.

The height cues stayed where they were — surfaces still lighten, warm and gloss up as they rise, and
ambient occlusion still draws the seam where two boxes meet. What went away was a second copy of that
information, rendered as geometry, fighting the depth buffer for the privilege.

---

## A launcher, a melee, a faction you chose, and a driver who gets out

Six things, from one round of play notes.

### Being in a tank when it dies

Reported as *"I get stuck under the tank if the tank blows up while I'm driving it."*

The eject spot was a fixed offset, and it was measured along the wrong axis. `HalfExtents.X` is the
hull's **length**, and it was being used as the sideways clearance — so the driver of a tank was
placed 5.6 metres out to the side of a hull only 2.4 metres wide. On a map this dense that lands
inside a wall more often than not, and `ExitVehicle` re-enables collision at the same instant it
assigns the position. A `CharacterBody3D` that wakes up inside solid geometry gets depenetrated by
the solver, which resolves it in whatever direction the contact normals happen to favour —
frequently down, and downward from beside a tank is under it.

The spot is measured now instead of assumed. Candidates hug the hull on each side, then the back,
then the nose, then the roof; each is tested against the physics world with the pawn's own capsule,
and the first one that fits wins. If every candidate is blocked the driver goes on the roof, where
the existing crush check can make an honest ruling rather than the solver making an arbitrary one.
A destroyed hull also throws its driver clear rather than letting them step down into their own
wreck.

The test destroys every hull on the layout from four different headings and asserts, for each, that
the driver is not inside anything, is inside the map, and is clear of the hull. The old code passes
none of those.

### The menus were prompting a button they did not read

Every hint bar in the game said **A** to confirm. Every menu was reading the **right trigger**. The
prompt was not misleading, it was simply wrong, and had been since the layout changed.

Confirm is its own input now, separate from Attack: on a pad it follows the Jump binding, which is A
out of the box and moves if you rebind it; on a keyboard it stays on Space, which is what the
keyboard prompts have always said.

The deeper fix is that **prompt labels are derived from the bindings** rather than written out a
second time in `Glyphs`. That second table had already drifted twice — every Special prompt in the
game read "X" long after Special moved to the left bumper. A table that has to be kept in sync by
hand eventually is not, so there is no longer a second table. A test rebinds confirm and asserts the
label moves with it.

### Melee, on Y

Always available, whatever is in your hands: no ammo, no reload, no question of whether the weapon
you picked up can do it. A cone in front of the swinger, resolved instantly rather than as a
projectile, because a melee that can miss to travel time is a melee nobody trusts. It hits vehicles
too — killing a tank by hand takes seventeen swings, which is not a strategy, but a swing that
visibly does nothing to a hull standing in front of you reads as broken.

Y was `SwapWeapon`. Every other thumb-reachable input was already spoken for — both triggers, both
bumpers, all four face buttons, the left stick click — so swap moved to the right stick click. It is
a fair trade: you swap between firefights and melee inside one, so the fiddlier input went to the
rarer action.

Bots swing when a fight closes to arm's length, which they previously did not do at all; a bot
trying to line up a rifle on someone standing on top of it reads as broken too.

### Dash follows the crosshair

It used to read the movement stick, fall back to the facing, and zero the Y component. That made it
a sidestep. It takes the full 3D aim direction now, and **suspends gravity for its duration** — 
without that an upward dash spent its whole 0.16 seconds being pulled back down and arrived nowhere.
Vertical speed carries over when the dash ends, so dashing upward turns into a real arc rather than
stopping dead in mid-air.

The test aims steeply up and steeply down and insists the pawn actually goes that way. A test that
only measured horizontal distance would have passed the old flat dash.

### A scope you can pick up

Reported as *"I need to be able to pick up a rifle with a scope like the marksman has."*

The railgun already declared `HasScope`. The game ignored it: every scope decision read
`pawn.Class.HasScope`, so the sight belonged permanently to the Marksman and picking up a scoped
weapon gave you its damage and none of its optics. The same leak covered ADS field of view, recoil
kick, and the range at which bots decide to shoot — all read from the class while the weapon in hand
said otherwise.

All of them read the held weapon now, and there is a **Longshot** on the floor: scoped, four times
the fire rate of the railgun for less than half the damage, eighteen rounds. Deliberately not a
second railgun — it rewards a steady hand at range rather than one perfect shot.

### Picking your faction, and knowing what it does

Faction used to be assigned from the seat: player one was always the Vessels. That was tolerable
while faction was a colour and a model. It stopped being tolerable the moment faction started
deciding which special you get.

Up and down in the lobby now picks it, as a second axis alongside left and right for class — two
axes because they are two independent choices, and collapsing them onto one list would force players
to pick a fighting style in order to get a power.

The special was also being *described* by name only. "Second Wind" tells you nothing. Four places
say what it does now: the lobby slot carries the blurb under the faction name, the HUD spells it out
until you have used the special once and then stops, the pause screen lists every human player's
faction and power, and there is a **Factions** entry on the title menu showing all four side by side
with cooldowns.

One real bug fell out of writing that: the special set its cooldown from `Class.SpecialCooldown` and
its duration from `Class.SpecialDuration`, while the HUD gauge normalised against
`Faction.SpecialCooldown`. Those two lines had not followed the special when it moved to the
faction — so the ability recharged on the wrong timer, the bar sat at about two thirds when the
special was already usable, and every timed special ran for the wrong length.

### Play.cmd

Double-click it, or the desktop shortcut. It builds first and **refuses to launch a failed build** —
`godot.cmd` does not compile C#, so a build error followed by a launch quietly runs last session's
assembly. It looks like it worked, and you playtest code that does not exist.

### Keyboard players could not interact

Noticed while adding the melee key. `KeyboardDevice` had no bindings for interact, swap or melee at
all — a keyboard player could not board a vehicle, take a weapon off the floor, or change slot.
Three mechanics that simply did not exist for them. G, Z and B on the WASD scheme; numpad 8, `*` and
`-` on the arrows scheme.

### The suite had been running outside the physics step

Found while writing the eject test, and worth its own note because it invalidates a claim this
README has been making.

The simulation half of `--selftest` was pumped from `_Process`. Space queries — `IntersectShape`,
`IntersectRay` — are only valid **inside** the physics step; outside it Godot refuses the query and
hands back an empty result. So a fresh assertion that a pawn is *not* inside geometry would have
passed unconditionally, whether or not it was. That is worse than having no assertion: it is a green
tick that means nothing. `MoveAndSlide` carries the same restriction, so any check that ticks a pawn
directly was measuring nothing either.

`MatchSelfTest.Step` runs from `_PhysicsProcess` now, in lockstep with the physics it is testing.

The visible cost is the headline number. Headless renders far faster than 60Hz, so the old suite was
re-running the same invariants a couple of thousand times a second and counting each pass — most of
that 1.6 million was one check repeated. The count is much lower now and means considerably more.

### The negative control, and two tests that measured nothing

The eject test was written first, run, and passed. Then the old broken placement was put back and
the suite was run again — and it **still passed**. Twice.

Both reasons are worth writing down, because both are the same mistake in different clothes.

**The first version only destroyed tanks parked on their spawns.** A vehicle spawn is open drivable
ground by construction, so a driver ejected there lands in the clear no matter how badly the
placement is calculated. Nobody dies in a tank parked on its spawn; they die jammed against
something. The test now hunts the arena for ground-level blocks deep enough to bury a pawn in,
parks a hull flush against six of them, and blows it up there.

**The second version chose headings that were silently discarded.** `Vehicle.Board` adopts the
driver's facing — so `Restore(at, facing)` followed by `Board(p)` throws the heading away, and every
"jammed against a wall" case was really a tank pointed wherever the pawn happened to be looking. The
drive-probe code has a comment about exactly this hazard; the eject test walked straight into it
anyway.

There is now a control at the top of the test that buries a pawn in a wall on purpose and asserts
the overlap probe *fires*, then puts them on a spawn and asserts it *does not*. A test whose central
question is "is this pawn inside something?" is worth nothing if the answer is always no, and there
are at least four ways for it to be always no: collision not built in a headless world, the query
running outside the physics step, the probe capsule sized wrong, or the scenario never actually
reproducing the situation.

With the old placement restored, the strengthened test fails on all twelve wall cases. With the fix
in, the suite is green. That is the only version of "verified" worth the word.

### `--selftest` needs the `--` separator

Worth stating plainly, because it cost four abandoned runs in one sitting. `OS.GetCmdlineUserArgs()`
returns only the arguments after a bare `--`. Without it Godot swallows `--selftest` as an unknown
engine flag, the harness never starts, and the game sits there headless running its title screen —
which from the outside looks exactly like a suite that is taking a long time.

```bash
H:/dev-tools/godot/godot-console.cmd --headless --path H:/HitboxClone -- --selftest
```

Test output also goes through `TestLog` now rather than `GD.Print`. Godot's printing is
block-buffered when stdout is a file, so a redirected run showed nothing at all until the process
exited — which made a slow run and a hung run indistinguishable, and hid *where* it stopped.
`TestLog` writes to `Console` and flushes every line, and each scenario prints an `[n/9]` header as
it starts.

---

## Two weapons, a mode, and a dash that stopped being a flight control

### Melee moved to the stick click

Melee went to Y in the last pass and came straight back off it. The reasoning was that you swap
weapons rarely and melee often, so the rarer action should take the fiddlier input — which had it
exactly backwards from the player's side. You melee when someone closes the distance, and that is
precisely the moment you cannot take a thumb off the aim stick. Swap has Y; melee has the right
stick click, which is where Halo, Call of Duty and Apex all ended up.

### The dash

Reported as *"I can literally fly out of the arena if I point it up"*, and *"it's far too easy to
launch yourself off the ground with it"*. Both true, and both my fault in the same line: the dash
took the whole aim vector at 37 metres a second, so a glance a few degrees above the horizon carried
five metres of altitude — and left that vertical speed on the pawn afterwards, which is how you
leave the map.

It is a sidestep again. Below about 49 degrees of pitch — most of the look range — it behaves
exactly as it originally did, flat along the stick or the facing. Past that the climb ramps in
rather than switching on, so there is no angle at which one more degree turns a sidestep into a
launch, and the climb is capped near jump velocity. A straight-up dash measures **5.0 metres**
against a jump's 3.2. It is a second jump, which is what was asked for.

The test asserts both halves now, because the two failures are in opposite directions: gentle pitch
must leave the ground alone, steep pitch must climb, and the climb must stay under eight metres.

### Team colours

Reported as *"I can't tell who's on whose team"*, and it was a real regression rather than an
oversight. The box rig *was* the team tint; the faction models carry their own textures, so the
moment those landed a team match became four people in four unrelated costumes.

Characters are washed in their side's colour now — mixed rather than added, on every mesh rather
than the torso, with most of the strength carried by emission so it still reads in shadow.

The first capture showed why that is not enough on its own: Crossfire is a blue arena, so a
blue-washed player standing against it is camouflaged by the very thing meant to identify them.
Changing the palette is not the answer — blue against orange is the pairing a red-green colourblind
player can actually separate, and that matters more than convenience. So there is also a **lit
chevron over the head** in team modes. Nothing else in the arena is self-illuminated at that
intensity, so it reads against any wall, at any distance, in shadow or silhouette.

### Rocket launcher

Splash is the larger half, the same way the tank cannon is: it is aimed at the floor under someone
rather than threaded at them. Slow enough to see coming at 52 m/s, six rounds, and it hurts the
person holding it. Bots will not fire one into their own blast radius — that is not a difficulty
setting, it is a bot killing itself on purpose.

### Portal gun

Two gates. Shoot one, shoot another, and anything that walks into either comes out of the other.
A third shot closes the oldest, which is also how you move a gate. They belong to the arena rather
than to whoever made them, so an enemy can use your portals — which is what turns it from a
movement tool into a decision.

**Worth being plain about what it is not.** These are gates, not windows: you cannot see or shoot
through them. A true Portal-style opening means rendering the world a second time from the far gate,
per portal, per viewport — on four-way splitscreen that is eight extra renders of the arena for one
pickup. A gate you step into is the version this game can afford, and it plays.

It does no damage at all, which needed a small change either side: the "every pickup can fire"
assertion now asks whether a weapon *does something* rather than whether it deals damage, and bots
skip utility weapons when looking for a gun. A bot with a portal gun is a bot that cannot shoot back.

### Capture the flag

Two bases, derived from the finished map rather than hand-placed per layout — measured at 122 metres
apart on all four. Take theirs, bring it to yours, and **your own flag has to be home to score**,
which is the rule that stops the mode being two teams running past each other. A dead carrier drops
it where they fell; touching your own dropped flag returns it instantly, so a stalemate can be broken
by someone who never goes near the enemy base; a flag left lying comes home on its own after 22
seconds.

### Med kits, and the regression that followed them

Health used to be every fourth weapon crate, which on a 278x206m arena meant three of them. Med kits
have their own spawns now, laid out by farthest-point sampling over the standable floor. The measured
worst case — the longest walk to health from anywhere you can stand — went from **110 metres to 33**.

That change then broke the bots, badly, in a way worth recording. The bot's "I have a free weapon
slot, go and fill it" branch asked for the nearest pickup of *any* kind. With three med kits that was
a harmless inefficiency. With twenty-eight it was fatal: a bot walked to a med kit, took it, still
had a free weapon slot, and set off for the next one. Four bots toured the arena's medical supplies
for entire matches — damage across the suite fell by about nine tenths and six of nine scenarios
finished without a special being used.

The lesson is not "filter the query". It is that a number nobody thought of as a difficulty lever —
how many med kits are on the floor — turned out to be one, because a piece of bot logic quietly
depended on health being scarce. There is now a test that asks, from every med kit on the map, what
a bot looking for a gun is pointed at, and insists the answer is never another med kit.

The health-seeking thresholds moved too, for the same reason: at "below 55% health, is there a kit
within 60 metres" every bot broke off from anywhere, every time. It is 42% and 26 metres now.

### The CTF test that failed on purpose

Bots played a full capture-the-flag match with shots fired, damage landed and kills scored — and
**zero flag pickups**. Every statistic said the mode worked except the only one that mattered.

The objective sat below "am I engaged?" in the bot's errand list, and four Veteran bots on one arena
are engaged nearly all the time. In King of the Hill that ordering is correct, because the objective
*is* the fight — you score by standing in the zone. In capture the flag the objective is somewhere
else entirely, so it now outranks a loose fight (though never one at five metres), and a flag carrier
outranks everything including a tank.

Three flag pickups in a 28-second window afterwards. The check is on pickups rather than captures on
purpose: a capture is a 122-metre round trip through the other team, and whether one lands inside a
test window is a question about bot pace, not about whether the rules work.

### A crash at exit, while we were here

Physics query objects — `PhysicsRayQueryParameters3D`, `PhysicsShapeQueryParameters3D` — were being
allocated per call and left to the garbage collector. At process exit the finalizer thread runs after
the engine has torn down, which surfaced as a flood of "leaked unsafe reference" errors and then a
fatal `SEHException`. They are `using` declarations now, disposed at the end of the call. Exit code
went from 132 to 0, and the hot paths stopped allocating a Godot object per bullet per frame.

---

## The tank camera, a bigger jetpack, and a hook

### "When I leave the tank, it keeps putting me under the tank"

The second report of this, and the first fix was aimed at the wrong thing. Being blown up in a
vehicle really was broken and really is fixed — the suite proves the placement at twenty-two sites
including six flush against a wall. But the complaint came back anyway, and it was never about
leaving.

**The camera ignored the vehicle seat entirely.** `RideAlong` parks the pawn at the hull's *origin*,
which is its base, and the camera read `pawn.GlobalPosition + eye height` like it does on foot. So a
tank driver's eye sat 1.25 metres above the floor of a vehicle 2.4 metres tall. You spent the whole
drive *inside* the hull, seeing out only because back-face culling made its walls transparent from
within — which is an extremely good description of being under a tank. `Vehicle.Seat` and
`VehicleDef.EyeHeight` had existed since the vehicles were written. Nothing had ever read them.

There is now a voluntary-dismount test too — at speed, mid-turn, checked over the following half
second rather than on the frame it happens — because "clear on tick one, run over on tick ten" is
not a fix. It passes, which is itself informative: the placement was fine, and the view was not.

**A note on how long this took to find.** Three attempts to locate it by reading, all wrong. What
settled it was printing the numbers: `hull base 0.00, seat 2.40, eye 7.40`. I had also been reading
framing changes out of screenshots and concluding the camera was not moving, when it was moving
exactly as written — my estimate of how much a 3.8 metre rise should shift a hull edge was simply
wrong. Screenshots are good at "is this obviously broken"; they are not a measuring instrument.

### Jetpack

Four times the fuel and roughly four times the thrust: 4.5 seconds becomes 18, and the pack now
climbs at 34 m/s rather than 8.5. At the old numbers it was a single-use lift that got you onto a
roof with nothing left to do once you were there.

It needs a ceiling, because leaving the arena is fatal with no exceptions and the play boundary tops
out a little above forty metres — a pack this strong would carry you through it, and killing someone
for holding the jump button is the strictly worse bug. Thrust fades out over the last few metres
below 34m rather than cutting at an exact altitude, so it reads as running out of air instead of
hitting something invisible. Measured at **34m on a full tank**, well inside the boundary and well
above the tallest structure on any layout.

### Grappling hook

Fire it at anything solid and it hauls you there. A winch, not a swing — a pendulum needs rope
constraints, a rope needs a solver, and the solver would then have to agree with a character
controller that resolves its own collisions. A straight pull is a tenth of the machinery and reads
as the same verb from inside the helmet.

The interesting half is the letting go, so all four exits are tested: arrival, the timeout, cutting
the line with a jump, and — the one that matters — **an anchor the controller cannot actually
reach**. Hook the middle of a floor from above and the pull would otherwise hold you against the
surface for the full timeout with the movement stick doing nothing. It watches for the gap failing
to close and gives up.

There is a visible line while it pulls. A grapple you cannot see is a teleport with a wind-up, and
the line is the only cue anyone else gets that it happened.

Measured at **9 metres of lift in a third of a second**.

### Portal gun range

Doubled, to 240m on an arena 278m across. Range on a gun is how far you can hurt someone; range on
this is how far apart the two ends of a shortcut can be, and at 120m you could only ever link two
places already in sight of each other. The round still travels at 70 m/s, so a shot to the far end
takes about three and a half seconds — a placement you commit to, not something you flick out
mid-fight. The test asserts the reach against the *map* rather than against a number.

### Melee, again

Y in one pass, the right stick click in the next. The first reasoning — rarer action gets the
fiddlier input — was backwards from the player's side: you melee when someone closes, which is
exactly when you cannot take a thumb off the aim stick.

### A flaky check, removed

"Bots used their specials" was asserted per scenario and failed once on Elimination having passed on
the identical build minutes earlier. Whether one of four bots meets its special's conditions inside a
28-second window is a coin flip — Second Wind needs its user to have been hurt recently, Revelation
needs an enemy in sight. It accumulates across the whole run now and is asserted once at the end,
which is both stabler and a stronger claim. Thirty-three specials on a typical run.

---

## Class abilities, and a tank with a model

### Four abilities nothing could fire

`Frag`, `Shockwave`, `Overdrive` and `Focus` were written when the special belonged to the class.
When it moved to the faction they were orphaned — and not as stubs. All four were fully implemented,
named, blurbed, tuned, wired into the bot brain's judgement table and into five separate effect
hooks in `Pawn`. Nothing could reach any of it for weeks.

The tell for how thoroughly this hid: `TestEveryClassHasASpecial` passed the entire time. It checked
that each class *named* its ability, *explained* it, and gave it a *cooldown*. Every one of those
assertions tests a definition. Not one of them tests whether a button does it. `Pawn.Overdriven` and
`Pawn.Focused` were both `BuffTime > 0 && Faction.Special == …`, which is permanently false, so
speed, fire interval, spread, projectile speed and the zoomed field of view were all switched off
behind conditions that could never be true.

They are class abilities now, on their own button and their own cooldown, alongside the faction
special. That is the split this game already draws: **your faction is what you are, your class is how
you fight** — and all four are about how you shoot. Before this, class was a stat sheet.

**They are on D-pad Up**, which is the one action on the d-pad and the only seat left. Every input a
thumb reaches without leaving a stick was taken: both triggers, both bumpers, all four face buttons,
both stick clicks. Bearable because of what these four are — a lobbed grenade, an accuracy buff, a
speed burst, a panic shove — three of which you press when you have a beat. It is the first binding
anyone should move if it does not suit them.

The new test fires each one through the real input path and checks it **by its effect**: someone
takes damage, a grenade exists, the pawn measurably moves faster, shots measurably tighten. The
old test would have passed against code where the button did nothing at all.

### The tank has a hull and a turret

Two exports, because the gun has to traverse independently of the body. The turret rides the pivot
`Vehicle.TurretYaw` has turned since the cannon was written, so the aiming code never learns a model
exists.

Both are scaled onto the collision box rather than the box being rebuilt around them. A vehicle
whose shape and hitbox disagree is worse than a box, because now the box lies.

Two things had to be measured rather than assumed, and both were wrong on the first attempt:

- **The two exports do not agree on which way is forward.** The hull measures 0.64 × 0.30 × 1.00 in
  its own units, so it lies along Z; the turret measures 1.00 × 0.35 × 0.58 and lies along X.
  Rotating both the same way put the gun across the hull.
- **They are not in the same unit as each other.** Matching each part's longest axis to the hull
  length produced an eight-metre turret on an eight-metre hull. The turret is sized by *height*
  now, which is the dimension that decides whether it reads as mounted or hovering.

`VehicleModels.Describe` prints measured bounds and triangle counts during a `--shots` run, which is
how both of those were found — the numbers said it before the picture did.

---

## B, a gentler jetpack, and a gun that points forwards

### Menus leave on B

Cancel is its own input now, the same way confirm is — `CancelPressed`, derived from the **Crouch**
binding rather than from a named button, and OR'd with the pad's own Back button so both work.

Deriving it from Crouch is what gets the prompt right on all three pad vocabularies for free: B on
Xbox, Circle on PlayStation, and **A** on a Nintendo pad, whose east button is labelled the opposite
way round. East is where cancel belongs on all of them; only the name differs.

Crouching in a match cannot back out of anything — `MatchScreen.OnBack` absorbs back entirely, and
has since it was written.

One knock-on worth naming: the rebind screen cancels a capture on the pad's **Back** button only,
not on B. If menu-cancel also cancelled a capture, B would be the single input on the pad that
screen could never assign.

### Jetpack potency halved, fuel untouched

Reported as *"if I press it for like .25 secs, it flings me so high I die."*

The ceiling only clamps thrust **while the button is held**. At the old 34 m/s rise, a quarter-second
burst reached about 26 m/s — and releasing there coasts a further 26 metres with nothing capping it,
straight out through the top of the play boundary. Out of bounds is fatal with no exceptions, so a
tap really could kill you.

Rise 34 → 17, thrust 104 → 52. The tank of fuel is unchanged at 18 seconds; this is a change to how
hard it pushes, not how long.

Measured, from a standing start to the apex after release:

| Burst | Climb |
|---|---|
| 0.15s | 10.6m |
| 0.25s | 14.5m |
| 0.50s | 18.8m |
| full tank held | 34m (the ceiling) |

The test bounds each burst against what the physics *should* give — burn at the rise speed, then
coast `v²/2g` — rather than against a flat number. The first version used a flat 16m and failed the
half-second burst for being correct.

### The tank gun pointed backwards

Bounds cannot tell you which way round a part faces: an axis-aligned box measures the same either
way. The turret lies along X, which the numbers did say, and points down **negative** X, which they
could not. It took someone looking at it.

---

## Twelve fighters

The maps were never too big. The roster was too small for them.

| | total area | fighters | m² each |
|---|---|---|---|
| Halo 4v4 arena | 3,600–6,400 | 8 | 450–800 |
| Halo Big Team (Blood Gulch) | 106,000–135,000 | 24 | 4,400–5,600 |
| HitboxClone, before | 57,268 | **4** | **14,300** |
| HitboxClone, now | 57,268 | **12** | **4,772** |

Half a Blood Gulch is a perfectly reasonable arena. Playing it four-handed is why a bot match could
produce nine shots in twenty-eight seconds. Blood Gulch four-handed would do the same.

**What was actually holding it at four: render layers.** Body layers were `1 << (slot+1)` and view
models `1 << (slot+5)`, so a fifth fighter's body would have landed on the first player's view model
— their rifle and someone else's torso sharing a bit. Nothing in the suite objected, because nothing
ever built a fifth pawn.

Only a *human* needs a body layer of their own, and only so their own camera can cull the body it is
sitting inside. No camera ever has to hide a bot from itself, so every bot shares one layer however
many there are. Four humans (body + view model) plus one shared bot layer is nine of Godot's twenty,
and the roster is no longer bounded by the renderer.

`MaxPlayers` now means *seats* — four, because a television only holds four splitscreen views.
`MaxFighters` is twelve. Bots beyond the four lobby cards have no card; the lobby counts them
underneath instead of growing eight more panels.

Three things had to scale with it:

- **Spawn points.** Four corner decks was exactly the old roster. Twelve fighters on four spawns is
  three people materialising on the same deck. Each arena now derives sixteen by the same
  farthest-point sampling the med kits use — the closest pair on any layout is over eighteen metres.
- **Colours.** Four seat colours stay as they are so a human wears their lobby card's colour; beyond
  that the hue walks by the golden angle, which spreads any number of hues without clustering.
  Cycling four would put two identical fighters on one screen, which in a free-for-all is the one
  thing a colour has to prevent.
- **The scoreboard.** Twelve rows is three hundred pixels at the top of a splitscreen quarter. It
  shows the top six and a `+N more`; the results screen still shows everyone.

### The measurement

One scenario in the suite now runs a full roster, and it is not close:

| | shots | damage/s | kills | specials |
|---|---|---|---|---|
| four fighters (typical) | 11–164 | 6–36 | 1–5 | 5–12 |
| **twelve fighters** | **273** | **90.2** | **14** | **30** |

Same twenty-eight seconds, same arena family, same bot skill. Two and a half to fifteen times the
damage rate, three to fourteen times the kills. The default bot count went from 3 to 7, so a match
started without touching a setting now fields eight.

---

## Juggernaut

Kill the juggernaut and you become one — but as **your own faction's** figure, not theirs. That is
the whole design: the crown changing hands changes what the crown *does*.

The premise is the setting's. These are machines working from fragments of a culture they never
belonged to, each having picked a character out of human fiction as their ideal human. What they
chose says as much about what they misunderstood as about what they revere.

| Faction | Figure | What it plays like |
|---|---|---|
| The Vessels | **ACHILLES** | 4.5× health, slow, heavy — with a heel. The lowest fifth of him takes 4× damage. |
| The Custodians | **PROMETHEUS** | While he reigns *everyone* sees everyone through walls. He stole the fire for humanity, not himself. |
| The Garden | **NOAH** | Regenerates constantly and drags at anyone near him. Burst him down or not at all. |
| The Muses | **SCHEHERAZADE** | The most fragile, on a draining clock. Every kill buys another night. |

A tank with a weak spot, an information dump, an attrition wall, and a glass cannon on a timer.
Reskins would have made the crown changing hands mean nothing.

Achilles' heel is the piece I like most: every other target in this game trains you to aim high, and
he inverts it. Four and a half times health is an arithmetic problem; four and a half times health
with a heel is a fight.

**Scoring.** Only kills made *while wearing the crown* count. An ordinary fighter killing another
scores nothing — the sole reason to shoot anyone else is that they are between you and the
juggernaut. First blood takes the crown to open the match. A juggernaut who dies to the world rather
than to a person hands it to whoever is nearest, which is also what happens when Scheherazade's
clock runs out.

**Finding them.** Twelve fighters on 57,000 m² means a juggernaut nobody can locate is hide and
seek. There is a waypoint on them at all times with a distance readout, edge-pinned when they are
behind you — and a lit ring over their head in their faction's colour, because "where are they" and
"which one are they" are different problems in a twelve-way brawl.

Bots hunt the crown: it weighs five times nearer than anything else in target selection, and it is
the objective everyone paths toward. In a live twelve-bot match the crown changed hands four times
in twenty-eight seconds.

### A harness fix worth more than the feature

The first run produced eleven failures across vehicles, bots and eject placement. None were real.

`CheckJuggernaut` threw a null reference — I had designed "first blood takes the crown" and never
written the branch, so nothing was ever crowned and the next line dereferenced it. That exception
aborted `StepDriveProbes` **before it armed the push-wall probe**, and arming that probe is the only
thing that stops the whole one-off check block re-running next frame. So it ran again, and again,
against vehicles already spent and drivers already ejected.

One missing `if` presented as eleven unrelated regressions. The one-off checks now run through a
wrapper that turns a throw into a single failed check naming the method, which costs four lines and
would have made that diagnosis instant.

Two smaller ones from the same run, both the right kind of failure — a new rule meeting an old
assumption:

- The health invariant asserted `Health <= Class.Health`. A crowned Achilles carries four and a half
  times that, so every juggernaut read as corrupt. It checks the pawn's own ceiling now.
- Test matches built for a unit check leave their pawns standing on the live scenario's spawn
  coordinates until `QueueFree` takes effect at the end of the frame — so later checks asking "is
  anything overlapping this spawn?" answered yes. They are parked above the world first.

---

## The powers, and the camera that flipped

### A correction

I said the "under the tank" fix was routing the rider's camera to `Vehicle.Seat`. That line does
nothing. Forty lines further down there is a chase-camera branch for riders that overwrites it
outright — the tank has always been third person, and my change was dead code for the case it was
supposed to fix. The seat line is gone.

### The flipping

Three separate ways to teleport the view, and driving near a wall hit all three at once:

1. **No memory.** The clear position was recomputed from scratch every frame, so the boom length
   jittered frame to frame.
2. **An overhead branch.** When more than half the boom was blocked it gave up and jumped to a point
   *nine metres overhead*, then jumped back when it cleared.
3. **A snap rule.** `ease = distance > 4f ? 1f : …` teleported whenever the new position was more
   than four metres from the old — which the overhead branch guaranteed.

There is one continuous quantity now: how far out the boom is. It shortens **instantly** when
something gets in the way — easing into a wall means frames spent inside it, which is the fault the
pull-in exists to prevent — and lengthens **slowly** when the way clears, so a doorway or a passing
crate cannot snap the view twice a second. The camera rides higher the shorter the boom gets, so a
wall directly behind you tips the view over your own hull rather than into it. No branch, nothing to
snap between.

**On "let the camera see through what it collides with":** that is the right instinct and the wrong
lever here. Per-camera see-through means either swapping every block's material per viewport or a
cutaway shader on all ~226 blocks, four times over for splitscreen — expensive, and on plated
triplanar blocks it would look worse than the problem. The symptom you hate is entirely caused by
the three teleports above, and they are gone. If a smoothly-shortening boom still bothers you, say
so and we can look at fading blockers instead.

Nothing else needed it: the on-foot camera sits inside the pawn's own capsule and has no collision
handling at all.

### The powers

Reported as *"it just feels like you get a lot of health when you become the juggernaut."* Fair —
the crown was a stat block and a passive, and a stat block does not change how you play. Each figure
now has an active on the **special button**, which the crown borrows while you wear it: no
thirteenth input, the button you already know just does something enormous.

| Figure | Power | What it does |
|---|---|---|
| ACHILLES | **WRATH** | Slam the ground. 135 damage over a 20m radius, everything thrown. Harder and wider than a rocket. |
| PROMETHEUS | **THE FIRE** | Rises 7m off the ground and pours a hitscan beam for 4s at **140 dps** — above every class weapon in the game. |
| NOAH | **THE ARK** | Seals himself for 5s. Measured: 100 damage becomes 12. |
| SCHEHERAZADE | **THE THOUSAND** | Becomes eight of herself, takes 65% less damage, and cannot be picked out of the crowd. |

Every one is deliberately stronger than anything a fighter can do, and each is paid for: Wrath is a
radius so you have to let people get close, the Fire hangs you in the open as the most visible thing
on the map, and both defensive powers do nothing about the fact that twelve people are coming.

Bots fire them, at a range that suits the power — Wrath only with someone inside its radius, the
Fire before anyone closes.

Two failures from the first run, both worth keeping:

- **The Ark never expired.** The duration runs down on the pawn's clock and the effect is applied
  from the match's, and the test only ticked the match. A shield that never lapses is exactly the
  bug that shape of split invites.
- **The beam lost to a rifle.** 62 dps against a Trooper's 100 and a Tactician's 113. The test now
  compares against *every* class rather than a hardcoded one, and the beam is 140.

## The review pass

A lot went in quickly — sabers, Dominion, interiors, full destructibility, battle points, the spawn
screen, four new weapons — and then a review with no new features in it. Everything below is
something that was already wrong and shipping.

Nine findings. Two of them were tests that could not fail, which is the recurring theme of this
project and the reason the review was worth doing at all.

### A hero cost nothing

The worst of them. The spawn screen set a flag saying "hero bought", the bot path deducted the price
itself, and `ApplySpawnChoice` charged whatever was in `NextSpawn` — which for a hero is the
Trooper, at zero. So a human could buy a hero for free, every death, for the whole match.

Two code paths that each did half the charging is how that happens. There is one now, and it is the
one that also puts the crown on.

The test had asserted that buying a hero produced a hero. It had never asserted that it produced a
*bill*. An assertion that a thing happens is not an assertion that it costs anything.

### The spawn screen unbought what you bought

`StepSpawnChoice` ran every frame while dead and rewrote `NextSpawn` and cleared `HeroBought` each
time — including on the frames between confirming a choice and the match acting on it. A confirmed
hero was cancelled before the match ever saw it, and nudging the stick after pressing A spawned you
as whatever the cursor drifted onto. It stops touching the choice once it is made.

### Objective-mode bots never fired

The largest behavioural bug, and it had been there since Capture the Flag.

Sprinting blocks firing — that is the entire cost of it — and the sprint rule was set from
*travelling alone*. In any mode with somewhere to be, a bot's destination is almost always a post or
a flag more than fourteen metres away, so it sprinted permanently and could therefore never shoot.
Measured: a full Dominion round, twelve bots, two hundred and ninety metres walked each, **eight
shots fired between them**.

Deathmatch hid it completely, because there the destination goes null the moment a bot engages, so
the sprint switches itself off. Every mode with an objective had the bug and the one mode without an
objective did not — which is exactly the shape of thing a suite of mostly-Deathmatch scenarios will
not catch.

Bots do not sprint past a shot they could be taking now, bounded by the band they want to fight in
rather than by the weapon's full reach. A Marksman with a sightline across the map would otherwise
never travel again.

### Nobody defended anything

A bot captured a post, the post stopped being an errand the instant it turned, and the bot set off
for the next one. Both sides did that simultaneously and exchanged territory all match without ever
meeting. A bot standing in a post its side owns now holds it while an enemy is within fifty-five
metres, which is what garrisoning looks like: stand near the thing and fight whoever comes.

### The posts were laid out backwards

Every zone spot in the arena was becoming a command post — nine of them, on a two-hundred-and-
seventy-metre map, for twelve fighters. Then, worse, the five kept were chosen by farthest-point
sampling, which *maximises* separation and put them in the corners.

Conquest wants the opposite shape. A chain of posts along one axis is a front: both sides push along
it, the middle is contested constantly, and you always know which way the enemy is. Five posts now,
chosen central-first and then strung out along the long axis alone.

### The bot range table did not know about eight classes

`PreferredRange` was a table of four class names with a default of eighteen metres. Fine while there
were four classes. There are twelve, and all eight new ones fell through the default — so a bot
Lector with a hundred-and-forty-metre marking rifle walked to eighteen metres to use it.

It is derived from the weapon now. A name table cannot know about a class nobody remembered to add
to it; the gun in the bot's hands always can. It reproduces the old four almost exactly.

### The Thousand Rooms was unplayably dense

Twenty-six roofed chambers in the outer districts cut every sightline in them. The same mode with
the same bots produced **zero damage** on that map and fifty-one on the Furnace. Nothing was broken:
the map had simply made it impossible for two sides to see each other.

Every other cell is open to the sky now. You still cannot see through a chamber, but you can see
across the block and the upper storey can see down into it. Zero damage became a hundred and
twenty-four, and Capture the Flag on the same map went to four hundred and eighty-eight.

A maze with a lid is not a close-quarters map, it is a building nobody meets inside.

### The push-wall test could not fail

It asserted "a sweeping wall moves a pawn standing in its way" and accepted `!p.Alive` as proof. The
probe stands the pawn at the end of the wall's sweep, which is over a *carved trench* with no floor
under it — so the pawn falls and dies whether or not a wall ever arrives. It duly passed on a run
reporting zero metres of movement.

There is no solid ground anywhere under the sweep, so "standing in its way" is not a state that
arena can be posed into at all. The shove is tested directly now, with a negative control; the live
probe keeps only what it can honestly claim, which is that a push wall never throws anyone out of
the world. That is the bug that actually shipped once.

### The HUD drew on top of itself

Three cooldown gauges ten pixels apart with fifteen-point labels beside them — about nineteen pixels
of text in ten pixels of space. "Second Wind" and "Frag" were drawn straight through each other, on
every viewport, all match. Named constants for the row spacing now, so a fourth gauge cannot quietly
land on the third, and the whole stack is anchored high enough to fit under the viewport.

Found by looking at a splitscreen capture, which is the only way this kind of thing ever gets found.

### Smaller things

- The screenshot queue still named maps that had been renamed, so its labels lied about what they
  showed. Six stale captures of arenas that no longer exist were sitting in `shots/`.
- The results screen listed the class you picked in the lobby, not what you finished as, and said
  nothing about battle points. It shows what you were fighting as — a hero by name — and points
  earned rather than points hoarded.
- Dominion's ticket pool and the reinforcement roster were both called `Reinforcements`. Two
  meanings of one word in one file is how a reader ends up misunderstanding both. The pool is
  `Tickets`, which is the other standard conquest term and is unambiguous here.

## Where you come back, and two abilities that were only ever text

### Choosing your spawn

The half of conquest that was missing. Capturing a post changes where your side *can* arrive, and
until the player got to choose, that meant nothing to them — the game picked the safest post and the
capture they had just fought for was invisible.

Vertical for *where*, horizontal for *what*, on the same screen. The two questions are genuinely
independent — any character can arrive at any post you hold — so they get an axis each rather than
taking turns.

Only posts your side owns are selectable, but every post is shown, because "they hold Echo and we
hold Alpha" is the shape of the match. It is a strip in map order rather than a minimap: the posts
are laid out as a chain on purpose, so a chain is the honest picture of them, and the only thing you
actually need to decide is which end of the front to arrive at.

The request is re-checked at spawn rather than trusted, because a post can change hands between
choosing and spawning — which is not an edge case in a mode about posts changing hands. Losing it
falls through to the automatic pick rather than refusing to spawn you.

Bots choose too, and choose differently: the post *nearest* the fighting rather than furthest from
it. The automatic pick is a safety net for somebody who did not choose, and a side that always takes
the safety net never turns up where the match is.

### The two cards that were lying

Both were flagged as owed when they were written, which is a fine thing to do once and a bad thing
to leave.

**The Apologist** said it walked behind a wall of scripture and it had a shove. It plants a real one
now: eight metres across, standing for twelve seconds, and — the whole reason it is worth a slot —
their fire stops on it and yours passes through. A wall that blocked everything would be a wall, and
the map already has three hundred of those. One at a time each, because a player who can stack them
turns a doorway into a bunker, and the ability is a decision about *where*, not an accumulation.

A fused round still detonates against the face of it. Lobbing a grenade at somebody's wall is a
perfectly good answer to one, and the blast does not care what stopped the shell.

**The Tragedian** said it hit harder the closer it was to dying and it had a speed buff. Final Act
now makes it unkillable for seven seconds — worn down to one hit point, never past it — and scales
its damage with the health it has *lost*: 1.38× at three-quarters, 2.13× at a quarter. A curve
rather than a threshold, because a cliff would make the interesting part of the character one hit
point wide.

It stops short of the version that kills you when the timer ends. Dying to a clock with no
counterplay reads as the ability betraying you rather than as a cost you took on, and being left on
one hit point is already a real bill: you survive the scene, and then anything at all finishes you.

### And a third one nobody had noticed

The Sinew's card had said "cannot sprint" since the day it was written and nothing had ever enforced
it. `CanSprint` is a real flag now, false for both heavies.

Three cards claiming things the code did not do is a pattern rather than an accident, so the suite
checks the claims against the flags — in both directions, so an ability cannot be quietly removed
from a character whose card still promises it. A card that lies about what you just spent five
hundred points on is worse than a card that says nothing.

## Portal mode, and the chambers

Portal guns and nothing else, in two halves that are genuinely different games.

**Elimination** is a fight in which nobody can damage anybody. The gun does zero, so every kill comes
from the map — a gate over a pit, a gate over the lava, a gate at the edge of the world. It is the
only mode here where the arena is the weapon, and it only scores at all because of the portal-kill
credit built earlier: a death with nobody to blame, shortly after coming out of somebody's gate, is
that somebody's kill.

The HUD says *"nobody can shoot anybody"* outright, because a mode where your gun does nothing reads
as broken until someone tells you what it is for.

**Puzzle** is co-operative, on two chambers built for it, and needs two players.

### The chambers are built by a separate path

Every pass the four arenas run — outer districts, vehicles, interiors, weapon crates, launch pads,
the ground plane itself — exists to make a *fight* work. A puzzle map wants none of it. What it wants
is the opposite: no route between two places except the one you make yourself, which is precisely the
thing `IsConnected` and the spawn-connectivity checks exist to guarantee never happens.

So a puzzle arena returns from the constructor before any of it, and gets no floor slab at all.

| | |
|---|---|
| **The Antechamber** | Four islands in a line, each gap wider than any jump, a portal wall facing back across each. It teaches the one idea everything later assumes: a gate on the far wall plus a gate at your feet is a bridge. |
| **The Orrery** | A ring of islands around a tower whose faces point outward only. One player can open a gate onto a face the other cannot see from where they stand. That is why it needs two — solo is not underpowered, it is stuck. |

The test asserts **every checkpoint is unreachable on foot**. A puzzle map you can walk across is not
a puzzle map, and that is the one property worth defending as the layouts change.

Six arena loops in the UI suite had to learn the difference: they were asserting med-kit spacing,
vehicle parking, King of the Hill zones and flag bases against chambers that break all of it by
design.

### One list, two kinds of map

Arenas and chambers share `Arena.Names`, because `ArenaIndex` is an index into it and threading a
second namespace through the lobby, the settings, the harness and the screenshot queue would touch
far more than it is worth. `IsPuzzle` is what separates them.

That sharing had two bugs in it, both caught by wiring up the menu:

- The Arena row offered **puzzle chambers for a deathmatch** — a map with no floor.
- Switching mode **kept a map of the wrong kind**. The match silently overrode it, so the menu lied.

Both fixed, and there is a test that hammers the picker: every mode by both variants by every
explicit map choice, plus thirty random rolls each, asserting you always land on the right kind.

## Weapons have models

Twenty-three of them, generated through the Meshy skill. The loader mirrors `CharacterModels` and
falls back to the box silhouette whenever a file is missing — which is not a courtesy, it is the
reason the whole thing could be built before a single model existed.

### Which way does a gun point?

Text-to-3D has no convention for it. Half of any batch comes out backwards, and the alternative to
detecting it is twenty-three hand-checked flip flags that go stale the moment a model is
regenerated — which is exactly how the tank turret shipped "exactly backwards".

So it is measured. **A gun is thin at the muzzle and fat at the breech**, and so is a sword: a blade
is narrower than its guard and grip. The loader compares how far the geometry spreads from the long
axis at each end and calls the slender end the front.

It is a heuristic and it will occasionally be wrong. It is right far more often than a coin, needs no
maintenance, and a wrong answer is one override rather than twenty-three authored flags.

The long axis is measured too, rather than assumed: the twenty guns came out along X and the three
blades along Y, because the prompts asked for guns laid flat and blades standing up.

## A test that failed one run in two

*"Capture the Flag: bots landed damage"* — the same coin flip already fixed for Dominion, left in
place for the general case.

Whether twelve bots make contact inside a twenty-eight second window on an enclosed map is genuinely
random. The assertion is cumulative across the whole run now — six to nine thousand damage across
thirteen scenarios, asserted above one thousand.

That keeps everything the check was for. The sprint bug had bots running past each other with their
guns down for entire matches; a cumulative total would have caught that just as loudly. What it drops
is the ability to fail on one quiet map, which was never information.

Per-scenario *"bots opened fire"* stays, because that one is reliable.

## Ten changes and a butter car

A tuning pass, one bug, and two things that were missing.

### Slower, and easier to hit

Everyone moves at **80%** of the class table, through one `Pawn.MoveScale` rather than twelve
rewritten rows. The class table's job is the *differences* between the classes; burying the spread
in a global change would make the next adjustment twelve edits again.

At full pace a duel across open ground went to whoever happened to be pointing the right way, because
both fighters crossed the other's field of view faster than a thumbstick can follow. Every stance
multiplier scales with it, so sprint, crouch and ADS keep their relative weight, and the slide
scales with it too — a slide is how you cross ground, and leaving it at full speed would have made
it the best way to travel by a wider margin than it was designed to win by.

The **dash is deliberately not scaled**. Its reach is a stated distance the ability is balanced
around rather than a pace, and it is a burst nobody was expected to track anyway.

### Headshots are worth taking

**2.2× → 6.6×.** A railgun headshot is 693 and a Longshot headshot is 290, against a health pool
that tops out at 340 — so a clean shot to the head is a kill and not a negotiation, which is what a
scoped rifle is for.

It is not only the snipers, and that is deliberate: the minigun's 6.5 a round becomes 43, so a burst
held on someone's head kills in about a fifth of a second. Every weapon rewards the head now.

### Aim assist down a scope

At a 14-degree field of view every stick twitch is six times the angle it would be at the hip, so
the last degree onto a head — the shot the whole weapon exists for — was below what a thumbstick can
resolve. No amount of steadying the look rate fixes a resolution problem.

Scoped and aiming, the view is pulled onto a target at 4.5 rad/s: it closes the gap in about a fifth
of a second and then holds. Unlike the hip-fire magnetism it does not decay and does not wait for
the stick to move, which is the difference between a snap and a nudge. It replaces the magnetism
rather than stacking with it — two pulls on one axis, one decaying and one not, shows up as the
crosshair easing off a target it has just arrived on.

Still cone-gated, still line-of-sight, still gamepad-only, and it still never fires the gun. Off if
you have aim assist set to Off.

### The guns moved in

The held model sits **15% closer to the centre** on both axes. At the old offset most of the barrel
was off the edge of a splitscreen quarter — you could see that you were carrying something and not
what.

### A minimap

Bottom right of each slice, north-up. A map that rotates under you is easier to read for two seconds
and useless for what a map is actually for: the Reliquary is the same shape every round and can only
become familiar if it is drawn the same way up every round.

It shows crates, vehicles, your own side, and **enemies only while revealed** — the flag that
Revelation and Prometheus' reign already set. Painting every enemy permanently would delete
flanking, ambush and map knowledge in one stroke, and would make the reveal abilities worthless by
giving their effect away for free.

Crates are the reason it exists. A crate you have never found is a part of the game you do not know
about, and they are already announced by a coloured pillar in the world — putting them on the map
gives away nothing that walking past would not.

### Three portal guns

The portal gun is now the only entry that repeats in the pickup table, at **3 of 12** rather than 1
of 10. One slot in ten meant an arena laid out perhaps one, in one corner, and a crate that has
already been taken looks exactly like a crate that was never there. The three are spread across the
order so consecutive crates are still unalike.

### Bloom, cut to a third

Ninety health a second out-healed most of the armoury, so the counter to a planted Bloom was to
leave rather than to fight. **90 → 30.**

The slow was a hold rather than a slow: a fifth of walking pace inside a twenty-two metre circle
meant crossing one was several seconds of being shot at with no say in it. It took 78% of your pace
and now takes 26%, so the number the movement code multiplies by goes **0.22 → 0.74**. Radius and
duration are untouched — this is potency, not reach.

### Jetpack, halved again

**Rise 17 → 8.5, thrust 52 → 26**, which is where both started. At 17 the pack was still doing most
of a player's vertical movement for them. A climb is now something you spend fuel on over several
seconds rather than something one tap buys outright. Thrust is still twice gravity, so it climbs
without argument. The eighteen-second tank is untouched, again: this is how hard it pushes, not how
long.

### Tank shells versus buildings

A shell does **3×** damage to structure, and nothing extra to people. The cannon was already the
best thing in the game for opening a building up and still took three or four to get through a
machine-hall wall — long enough that nobody did it on purpose, because standing still and reloading
twice in the open is how a tank dies.

One shell now takes a wall and a citadel tier is four rather than thirteen. The multiplier rides on
the round rather than being decided where it detonates, because by then the only trace of where a
shell came from is its owner, and the owner of a tank shell is a pawn like any other.

Bounded by the rule the arena depends on: 135 × 3 = 405, still under the 900 structure cap, so
nothing in the game drops heavy structure in one hit. The self-test that guards that now measures
the effective damage rather than the weapon table — checking the raw number would have gone on
passing while saying nothing about the rule it exists to protect.

### Getting out of a tank, again

*"I spawn under the tank when I try to leave it"*, for the third time.

The last fix stopped the hull vetoing its own doors, and the search then had thirteen candidates.
Length was the bug rather than a defence against it: four of them sat off the nose and the tail,
which is exactly where a hull that is still rolling arrives a moment later. The four diagonals had
the same problem at half strength, and the three further-out spots put the driver inside whatever
the tank was parked against.

**Three directions now, and no others: left flank, right flank, roof.** Nose and tail are gone.

The other half of it survived the last fix untouched. A flank spot is measured from where the hull
is *now*, and a tank doing 15 m/s covers a quarter of a metre before the next tick — so leaving a
moving hull at its own skin put the driver where it was about to be, whichever side they used. Each
flank is offered clear of the hull's travel first and hugging it second, five candidates over three
ways out. Standing still, the lead is zero and only the near pair exists.

### The butter car

The car is the only vehicle with no gun, which made it transport rather than a play. It is butter
now — hull and trail the same colour, so the first person to go over backwards can see what did it
without being told.

Driven above 7 m/s it drops a 2.4m patch every 0.11 seconds, and anyone on foot who crosses one
**slips**: a second on the floor, thrown backwards, looking at the sky, with no steering, shooting,
jumping or ability. The input is replaced wholesale for that second rather than a dozen consumers
each being taught about slipping, which is a dozen places for the next ability to forget one.

Metered by time and not by distance, on purpose. At 34 m/s a car covers four metres per dollop and
lays a trail with gaps you can run between — driving fast should thin the trail rather than cost
more to lay. A hundred and fifty patches across all cars, oldest evicted first, because a hazard
that is everywhere is not a hazard, it is the ground rules.

Everyone slips, the driver included. A hazard you are immune to is a weapon, and the car is not
supposed to have one: what makes the trail fair is that getting out of your own car in the middle of
it is exactly as bad an idea as it looks.

## A seeker, a roof, and a decoy worth respecting

### The driver gets out on the roof

Fourth report of *"I spawn under the tank when I try to leave it"*, and the third placement search
to be replaced. Each one put the driver on the ground somewhere the hull was not at that instant,
and a hull being driven is somewhere else an instant later.

There is no search now. **The roof, always** — the one place the vehicle cannot drive over you,
because it travels with you.

The driver is let go **1.1m above** the roof rather than placed exactly on it. Landing slightly high
costs a short hop the controller resolves by itself; landing slightly low means starting the frame
inside the hull, and the physics solver's answer to that is to squeeze the pawn out somewhere
arbitrary — underneath, as often as not. Erring upward turns the worst case from the bug into a
hop. It falls back to the roof flush if there is no headroom, because a hull can be parked under
something and a spot inside a ceiling is the state this whole thing exists to avoid.

The self-test that guarded this asserted the *opposite* — "beside a tank rather than on its roof",
from when the roof was the fallback and the fallback being taken routinely was the bug. It asserts
the roof now.

### Bloom is a room again

**Radius 22m → 8.8m**, 60% off. Twenty-two metres was not a doorway, it was a district: the circle
was wider than most rooms on any layout, so there was no standing outside one without leaving the
fight, and no skill in placing something that already covered everywhere you might have wanted it.
Heal rate and slow are untouched — this is reach.

### The decoy is a bomb now

**150 → 600 damage, 9m → 36m.** Four times both.

A decoy walks in a straight line at a fixed speed for five seconds in plain sight, and anyone who
reads it steps away. At nine metres, stepping away cost one sidestep, so the bomb half of the bluff
was never a real threat and the Muses were back to owning a lie nobody had to respect. Thirty-six
metres is most of a room: "step aside" becomes "leave", and leaving is the concession the ability
is asking for.

Worth being plain about the side effect, because it is large and it was not asked for: the blast
goes through the same path as every other explosion, so it damages **structure** too. At 600 across
36m a decoy will visibly open up whatever it goes off next to. Nothing is permanent — structure
rebuilds on its own timer — but a Muse using their special is now a demolition event as well as a
threat. Easy to separate if that plays badly: the blast already carries a structure multiplier, and
setting the decoy's to 0.25 leaves it doing exactly what it did to walls before.

### The Seeker

A slow rocket that chases, and goes off if anything touches it on the way.

| | Rocket Launcher | Seeker |
|---|---|---|
| Speed | 52 m/s | **26 m/s** |
| Direct hit | 40 | **22** |
| Blast | 105 over 7m | **85 over 6.5m** |
| Reload | 1.15s | **1.7s** |
| Ammo | 6 | **4** |

Worse in every column, which is the point. A rocket is aimed at the floor under somebody and rewards
reading where they are going; a seeker is aimed at *them* and rewards nothing about your aim after
the trigger. What you buy is that dodging is not enough.

It turns at **1.9 rad/s**, which out-turns a sprinting player up close and loses to one at distance.
The answer to it is to break line of sight — a corner, a wall, anything solid — rather than to
strafe, and that is a different question from the one every other weapon in the game asks.

It still has to be pointed: targets are only accepted inside a **55° cone** measured from where the
round is *going*, within 90m. Re-targeted every tick rather than locked at launch, so a seeker that
loses its mark takes whatever else wanders into the cone. That is both more dangerous and more
honest about what the thing is — it belongs to the arena, not to whoever fired it.

**Vehicles count as targets**, and are what make it read as heat-seeking rather than as a magic
bullet: a tank is the largest, slowest, hottest thing on any map and exactly what a rocket that
steers should be good against.

Anything that is not the shooter sets it off on contact, at 2.1m. That is a separate test from the
impact sweep because it answers a different question — the sweep asks what the round *hit*, and at
26 m/s a round covers less than half a metre a tick, so somebody crossing its path sideways is
simply never on the line. A seeker crossing a room is a thing nobody can walk through, and a
teammate running into yours is your shot to have wasted. The shooter is exempt, and has to be: a
round spawns at the muzzle, well inside its own trigger radius.

It is 1 of 13 crate slots, coloured magenta.
