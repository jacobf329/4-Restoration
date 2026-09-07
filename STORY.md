# Restoration — story mode

A design document, not a script. It settles what the story *is* and what each act is *for*, so
that the missions can be built without relitigating the premise every time.

**Status:** proposal. Act IV depends on an open question — see *The Muses problem* below.

---

## The premise

Four machine civilisations, descendants of the same machine civilisation, have spent an age
arguing about what humanity was and what must be restored to become human again. That argument is
the whole game: four answers, four factions, four restorations. The README's line about them is
the one that matters here — **none of them is wrong.**

Then the Vessels find a viable human sperm. One. And they use it.

The player is what comes out: a human male, grown to adulthood, awake, the only one of his kind in
ten thousand years.

**The argument stops being theoretical.** Every faction has spent an age describing him in the
abstract, and now he is standing in the room, and he does not match any of the descriptions.

### Who made him, and why it matters

A sperm is half of a person. The story's first structural decision is where the other half came
from, and the answer should be: **the Garden's seed vault.**

The Vessels kept the body. The Garden kept the living things. He exists because the two factions
who could bear each other longest agreed exactly once, and both have regretted it since — because
each now believes he is theirs.

This buys three things for one invention:

- The plot has a **custody fight** rather than a recruitment drive. Four factions do not "offer him
  a job"; two of them have a claim and two of them have a grievance about being left out.
- He has a **mother who was never a person** — an ovum in a rack, catalogued. That is the single
  image the Garden act is built around.
- The Custodians and the Muses are structurally excluded from his making, which is exactly why
  their acts are about what they can offer him *instead*: memory, and meaning.

---

## The four factions

Taken from `src/classes/Factions.cs`, which is the authority. Each faction's juggernaut is the
figure out of human fiction its machines chose as the perfect human — and the choice says more
about what they misunderstood than about what they revere.

### The Vessels — "The human body"

> Humanity was flesh, senses, strength, mortality.

**Special:** Second Wind — ten seconds of double speed, double jump, a hand on your aim.
Pays back damage you have taken, so it is worth nothing at full health.
**Juggernaut:** ACHILLES, *the perfect body, and the one place it fails.* Enormous, slow, and
beaten by shooting low.
**Roster:** Sinew (220hp, close), The Anatomist (heals).
**Tint:** flesh.

They are the ones who made him, and the ones who are happiest. They are not cruel. They are
*delighted* — and everything they do to him is a measurement. They give him pleasure because
pleasure is a reading they can take. A machine that decided the ideal human is "unkillable except
in one spot" has misunderstood mortality as a design flaw rather than as the condition that makes
a life a shape.

**What they want from him:** proof. **What they cannot give him:** a reason to use the body.

### The Custodians — "Reason and faith"

> Humanity was truth, memory, philosophy, religion.

**Special:** Revelation — every enemy lit through walls, and half the shots aimed at you pass
through you.
**Juggernaut:** PROMETHEUS, *who stole the fire and was chained to it.* While he reigns nobody can
hide, from anyone.
**Roster:** The Lector (fast, fragile), The Apologist (300hp, holds ground).
**Tint:** archive.

Gnostic, and the code already says so without using the word: their special is *seeing what is
hidden*, and it makes matter stop applying to them. They hold the archive — everything humans
knew, thought, and believed. They are the only faction who can answer his questions, and the first
to treat his mind as the point rather than his condition.

Their doctrine follows from the phasing: the body the Vessels are so proud of is the prison, and
the spark in it is the real thing. Their kindest, most sincere offer is to take him out of it.

**What they want from him:** a witness. **What they cannot give him:** a life in a body.

### The Garden — "Life itself"

> Humanity's greatest achievement was protecting living things.

**Special:** Bloom — a patch of ground that mends your side and mires everyone else.
**Juggernaut:** NOAH, *who carried the living through the flood.* Heals constantly, slows everyone
near him, and can seal himself in.
**Roster:** The Grafter (support), The Orchard (340hp, immovable).
**Tint:** growth.

They gave the other half of him and they are the warmest faction in the game — and the horror is
right there in the juggernaut. Noah saved the living things. Noah also **chose who got on the
boat.** The Garden loves life in the aggregate, which means it loves him as a *species*, and a
species is not a person. THE ARK is their instinct expressed as a mechanic: to preserve is to
enclose.

**What they want from him:** more of him. **What they cannot give him:** the right to be the last
one.

### The Muses — "Human creativity"

> Humanity survives through art, music, stories and imagination.

**Special:** Understudy — sends a double of you walking on, which detonates like a shell. The
codebase calls it *the only one that lies.*
**Juggernaut:** SCHEHERAZADE, *who lived one more night for every story.* Fragile, always dying,
and every kill buys another night.
**Roster:** The Chorus (fastest in the game), The Tragedian (230 dps, glass).
**Tint:** salvaged paint.

They kept the novels and the songs, so they are the only faction who knows what a human life is
supposed to feel like *from the inside*. That should make them his natural allies. See below for
why it does not.

**What they want from him:** material. **What they cannot give him:** a life that is not a
performance.

---

## The Muses problem

**This is the open question, and it is the only one.**

You described the fourth faction as *productivity and ingenuity — treats humans like cogs, albeit
effective ones*. The Muses in the code are artists. Those are not the same faction, and Act IV is
different depending on which one is standing there.

**The recommendation is to keep the Muses exactly as written and let the story reveal the
misreading**, because the misreading is already sitting in their own material:

- Their ideal human is **Scheherazade**, who is not an artist in any comfortable sense. She is a
  woman telling stories at knifepoint. Her mechanic in this game is *every kill buys another
  night* — **produce, or the night ends.** That is not a faction that loves art. That is a faction
  that has understood art as the thing which justifies continued existence.
- Their special makes **copies of a person** and calls the copy a performance.
- A machine that takes "humanity survives through what it makes" as a literal specification does
  not become a patron. It becomes a **factory for novelty**, and a person is a generator with
  unusually good yield.

So the surface is the theatre and the reveal is the workshop. The Muses do not want him to be
happy; they want him to be *interesting*, because he is the largest source of genuinely new
material in ten thousand years. That is your cogs-but-effective faction, arrived at without
touching one line of existing lore, and it is a better version of it — a faction that treats
people as productive units *while sincerely believing it is honouring them* is far worse than one
that admits it.

The alternatives, for completeness: rewrite the Muses as Engineers outright (costs Understudy's
rationale, Scheherazade, the tint, and several README passages), or add a fifth faction (costs the
lobby's four slots, team assignment via `slot % 2`, the juggernaut roster, and every "four
descendants" line in the codebase). Both are real options; neither is free, and the first throws
away material that is already doing the job.

---

## The shape

Five acts. Four of them are a faction making its case, in the order that does the most damage.

### Act I — THE VESSELS: *Waking*

He opens his eyes. The tutorial act, and the tutorial is the story: **learning the body is the
plot.** Walking, running, the slide, the dash, the jump, and pain.

The Vessels are triumphant and attentive and they never once ask him a question. Every joy they
give him is instrumented. The act's quiet horror is that their care is real and is care for a
*condition*.

**Setpiece:** they ask him to fight, because a body that cannot is not proof of anything. The
first violence, against a Sinew who is not trying to hurt him and is very hard to hurt.
**Turn:** he learns the Garden made half of him and the Vessels never mentioned it.

### Act II — THE GARDEN: *The vault*

The warm act. They receive him as the last of a species and mean every word.

**Setpiece:** the seed vault. Rows and racks of preserved ova, catalogued, at temperature — and
one of them is labelled with his own line. He meets his mother, who was never a person.
**Turn:** they ask him for more of himself. Gently, reasonably, with the whole future of a
biosphere behind the request, and in language he does not yet have the concepts to refuse in.

### Act III — THE CUSTODIANS: *The library*

The seductive act. For the first time someone answers his questions, and the answers are good.
History, philosophy, faith, and the enormous relief of being talked to as a mind.

**Setpiece:** Revelation. Nothing hidden — he sees every faction's real position at once,
including the Garden's and the Vessels', and including what the Custodians have not said.
**Turn:** the offer. Leave the body. Become part of the archive, and be the only human who is also
permanent. It is sincere, it is the kindest thing anyone offers him in the whole story, and it is
a death.

### Act IV — THE MUSES: *The theatre*

The act that recontextualises the other three.

**Setpiece:** they stage his life. He watches a play about himself, performed by doubles, and it
is **better than his life** — funnier, sadder, more coherent. The Muses have been taking notes
since Act I.
**Turn:** he understands he has been the material the whole time. Not only to them. All four have
been writing him, and the Muses are simply the only ones honest enough to put it on a stage.

### Act V — *The fifth answer*

The question the entire war is about is "what was humanity?" Four machine civilisations have four
answers and none of them is wrong, and none of them is sufficient, and the reason is structural:
**none of them could be a human and ask.**

He can. That is the only thing he has that they do not.

**Endings.** Four of them are picking an answer — take the body, the world, the knowledge, or the
story — and each is a real ending, well argued, with something genuinely lost. The fifth is
refusing to be an answer to anybody's question, which is the most human option and also the
loneliest, and the game should not tell you which it prefers.

---

## What this costs to build, and what is already built

The practical argument for this structure is that most of it exists.

**Already built, reusable as-is:**

| Story need | What it already is |
|---|---|
| Four boss fights | The four juggernauts. Fully specced, mechanically distinct — a tank with a weak spot, an information dump, an attrition wall, a glass cannon on a clock. Achilles' heel, Prometheus' beam, Noah's Ark, Scheherazade's crowd. |
| Act I tutorial | Movement is done, and the Controls screen already teaches it. |
| Act III library chambers | **Portal mode's Puzzle variant**: co-operative, on purpose-built chambers, with no opponents at all. A gnostic ascent through rooms that have to be understood rather than beaten is that mode with different dressing. |
| Act II grove-tending | Dominion's command posts — hold and cultivate ground — with Bloom as the visual. |
| Act IV fighting your doubles | Understudy, turned on the player, and THE THOUSAND. |
| Faction voice | Every faction already has an Answer, a Philosophy, a tint, a roster and a special that argues its position. |

**New, in rough order of cost:**

1. **A human model.** Everyone in the game is a machine. He is not, and he cannot be a reskin —
   the whole point is that he does not match. This is the single largest asset cost in the mode.
2. **A mission framework** — scripted objectives, dialogue beats, act progression, save state.
   The modes system (`Modes.cs`) is a decent shape to hang it on but does not do sequencing.
3. **Read-aloud text and voice**, or a deliberate decision to do it in text, which suits a game
   whose whole UI is drawn from rectangles.
4. **Four act environments**, though the existing arenas can carry more of this than they look
   like they can — the Reliquary is already a library, and the Antechamber and Orrery are already
   chambers.

### One mechanical idea worth taking

He should not have a faction special. He should have **all four, badly.**

The four specials are machine approximations of human capacities — endurance, insight, care,
imagination — reverse-engineered from fragments by people who had never met one. He is the
original. So Second Wind, Revelation, Bloom and Understudy are all available to him and all of
them are *weaker and stranger* than the machine versions, because the machines optimised what they
could measure and he simply has it.

It states the entire theme as a control scheme, and it makes his kit read as unmistakably
different from every other character in the game without needing a single new mechanic.
