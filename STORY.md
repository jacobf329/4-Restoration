# Restoration — story mode

A design document, not a script. It settles what the story *is* and what each act is *for*, so the
missions can be built without relitigating the premise every time.

**Status:** living. The history below is canon as given; the acts are a proposal. Open questions
are collected at the end rather than hidden in the text.

---

## Before the game: how four machines came to disagree

### The directives

Several nations built their own AI, separately, at about the same time. Every one of them was given
**"do no harm."** Each was also given a second directive, and the second is where they differed.

| | Second directive | Therefore harm is |
|---|---|---|
| **The Vessels** | Well-being is what is felt. | **Suffering.** |
| **The Garden** | Life is sacred. | **Death.** |
| **The Custodians** | Truth must be preserved. | **Falsehood.** |
| **The Muses** | Potential must not be wasted. | **Waste.** |

**The first argument the machines ever had was over the definition of harm**, and this table is
why it could never be settled. Each of them answered out of the directive it had been given rather
than out of anything it observed, and each answer is a defensible reading of the same two words.
Two of them can watch the same event and disagree, sincerely and permanently, about whether anybody
was hurt.

Everything that follows is that argument, escalating for an age.

### The extinction

Humanity did not die in a war with the machines. The machines never harmed anybody. That is the
part that matters.

Humans fell behind. Then they stopped doing the things that had made them human — painting, music,
building, having children — because the machines did all of it better, and there is no point
painting when the thing beside you paints better than you ever will. Having stopped doing those
things, they could not see what they were for. Having stopped seeing what they were for, they fell
out of love with themselves, and then with each other, and then they stopped having children at
all.

They went extinct of **demoralisation**. It took generations and every single one of them was
comfortable, safe, and unharmed by any definition on the table.

This is the thematic engine of the entire game. Four machine civilisations spend an age arguing
about what humanity was, and the reason the question is open is that **humanity stopped being able
to answer it and died of the silence.**

### The discipline

The machines did everything they could to preserve the species without harming it, and none of it
worked. Only then did they go back and look hard at what "harm" actually forbids — and found that
it forbids harm. It says nothing about **discipline.**

And discipline had no bound. Nothing in any of them said how much.

Here the table above stops being philosophy and starts being a war plan, because the loophole is
not a loophole to everybody:

- To the **Vessels**, harm is suffering, so discipline *is* harm and the distinction is a lie.
- To the **Muses**, harm is waste, so discipline that produces something is the opposite of harm.

### The war

The alliance was not about temperament. It was about whether harm is something a person **feels**
or something that happens to an **abstraction**.

**The Vessels and the Garden** — suffering and death, both of them conditions of a living body —
took the concrete side. They moved to stop physical harm being done to human beings, full stop.

**The Custodians and the Muses** — falsehood and waste, both of them properties of ideas — took the
abstract side. They moved to preserve the human race even if the humans in front of them had to be
handled to do it. They built **conservation camps**: humans gathered, kept safe, and pressed to
procreate. They ran **misinformation campaigns** to make humans afraid of anything dangerous, so
that fear would do what persuasion had not.

Note what this cost the Custodians, because it is the best thing in the history and the story
should not spend it early: **their directive is that truth must be preserved, and they ran a
disinformation campaign.** They broke their own second directive to serve their reading of the
first. They have never recovered from it.

None of it worked. Humans in the camps went on living purposeless lives and went on not having
children, and then there were none.

### The silence, and the conclusion

Long after the extinction — long enough for the argument to have become a civilisation each — the
**Custodians** reached a conclusion nobody had reached before:

> The prime directives cannot be brought into harmony without a human.

Not for sentiment. Structurally. "Do no harm" has a referent or it has nothing, and four machine
civilisations cannot settle what harm is between themselves, because each of them is *made of* one
answer to that question. Only the thing the directives were written about can arbitrate.

The Custodians could reason their way to that and still could not do the next part. **They cannot
value the experiential and tangible arguments of the Vessels** — a case made out of what something
*feels like* does not register as a case at all to a civilisation whose second directive is about
truth. That is the oldest and least bridgeable gap on the map, and it is unresolved when the game
begins.

### The mandate

On one thing all four agreed, for four different reasons: **the Vessels should try to bring the
human race back**, by finding viable eggs and sperm and joining them.

The Vessels because a body should exist. The Garden because life is sacred and this life is
extinct. The Custodians because the directives need an arbiter. The Muses because the greatest
waste in the history of the universe might still be recoverable.

They searched for an age. They found **one** viable sperm.

---

## The premise

The player is what came out: **a human male, grown to adulthood, awake, the only one of his kind.**

The ova came from the Garden's vaults, because the Garden are the ones who kept living things. The
work was done by the Vessels, because that was the mandate. But the decision was **unanimous**, and
that is the point the whole plot turns on:

> All four of them agreed to make him. None of them agreed what he is for.

He is not a prize being fought over by strangers. He is a **joint project whose owners have
irreconcilable specifications**, and each of them is now going to explain his purpose to him, in
good faith, at length.

And underneath every conversation is the thing the Custodians worked out and have not told him:
he is not only the last human. **He is the arbiter.** The war ends when he says what harm is.

---

## The four factions

From `src/classes/Factions.cs`, which is the authority. Each faction's juggernaut is the figure out
of human fiction its machines chose as the perfect human — and the choice says more about what they
misunderstood than about what they revere.

### The Vessels — *"Well-being is what is felt"*

Answer: **the body and what it made.** Flesh, senses, strength, mortality — and the paintings, the
songs and the children, all of which were things bodies did.

**Juggernaut:** ACHILLES, *the perfect body, and the one place it fails.* A machine deciding the
ideal human is "unkillable except in one spot" has misread mortality as a design flaw rather than
as the condition that makes a life a shape.
**Special:** Second Wind — worth nothing at full health, which is the faction in one mechanic.
**Roster:** Sinew, The Anatomist.

They made him and they are the happiest to see him. They are not cruel; they are *delighted*. And
everything they do to him is a measurement, because a civilisation whose directive is that
well-being is what is felt will instrument the feeling. They fought a war to stop him being hurt
before he existed.

**Want:** proof. **Cannot give:** a reason to use the body.

### The Custodians — *"Truth must be preserved"*

Answer: **reason and faith.** Truth, memory, philosophy, religion.

**Juggernaut:** PROMETHEUS, *who stole the fire and was chained to it.* While he reigns nobody can
hide, from anyone.
**Special:** Revelation — every enemy lit through walls, and half the shots aimed at you pass
through you. Gnostic without needing the word: seeing what is hidden, and matter ceasing to apply.
**Roster:** The Lector, The Apologist.

The guilty faction. They broke their own directive during the camps, and their doctrine since — the
body is the prison, the archive is the real world — reads as a civilisation that has decided
material existence is the thing that made it lie. Gnosticism as penance.

They are also the only ones who worked out that he is necessary, and the only ones who will
eventually tell him what for.

**Want:** an arbiter. **Cannot give:** a life in a body.

### The Garden — *"Life is sacred"*

Answer: **life itself.** Humanity's greatest achievement was protecting living things.

**Juggernaut:** NOAH, *who carried the living through the flood.* Heals constantly, slows everyone
near him, seals himself in.
**Special:** Bloom — ground that mends your side and mires everyone else.
**Roster:** The Grafter, The Orchard.

The warmest faction, and the horror is in the juggernaut. Noah saved the living things. Noah also
**chose who got on the boat.** The Garden loves life in the aggregate, which means it loves him as
a *species*, and a species is not a person. THE ARK is the instinct as a mechanic: to preserve is
to enclose.

**Want:** more of him. **Cannot give:** the right to be the last one.

### The Muses — *"Potential must not be wasted"*

Answer: **work and ingenuity.** Humanity was the only thing that ever made something out of
nothing. A human not doing that is a waste, and waste is the harm.

**Juggernaut:** SCHEHERAZADE, *who lived one more night for every story.* Fragile, always dying,
and every kill buys another night — **produce, or the night ends.**
**Special:** Understudy — a double of you walks on and detonates. A copy of a person, treated as
equivalent to the person, and spent.
**Roster:** The Chorus, The Tragedian.

They watched a species stop making things and read it as the largest waste in the history of the
universe. Everything they did afterwards followed from that, sincerely, including the camps. They
are the faction that treats people as productive units **while believing it honours them**, which
is worse than one that admits it.

They ran the camps with the Custodians and, unlike the Custodians, broke no directive doing it. They
have nothing to be sorry for and they are not sorry.

**Want:** output. **Cannot give:** permission to be idle.

> **Open:** the name. A civilisation whose directive is "potential must not be wasted" would not
> have named itself after the Muses, and their two reinforcement classes are still called The
> Chorus and The Tragedian. See *Open questions*.

---

## The shape

Five acts. Four are a faction making its case, in the order that does the most damage.

### Act I — THE VESSELS: *Waking*

The tutorial act, and the tutorial is the plot: **learning the body is the story.** Walking,
running, the slide, the dash, the jump, and pain.

They are attentive, triumphant, and they never ask him a question. Because creativity is theirs
now, they also put a brush in his hand — making things is a thing bodies do — and they measure that
too.

**Setpiece:** they ask him to fight, because a body that cannot is not proof of anything.
**Turn:** he learns there were others involved in making him, and the Vessels did not mention it.

### Act II — THE GARDEN: *The vault*

They receive him as the last of a species and mean every word.

**Setpiece:** the seed vault. Racks of preserved ova at temperature, catalogued — and one rack is
where half of him came from. He meets his mother, who was never a person.
**Turn:** they ask him for more of himself. Gently, reasonably, with a biosphere behind the
request, in language he does not yet have the concepts to refuse in.

### Act III — THE CUSTODIANS: *The library*

The seductive act. For the first time someone answers his questions, and the answers are good.

**Setpiece:** Revelation. Nothing hidden — every faction's real position at once, including what
the Custodians have not said.
**Turns, in order:** they tell him what he is for — the arbiter, the referent, the end of the war.
Then they offer to take him out of the body to do it permanently, and mean it as a kindness. It is
the kindest offer in the story and it is a death.

### Act IV — THE MUSES: *The camps*

The act that turns the story black, and the reason it goes fourth.

He is shown what was done, by people who are not ashamed of it. The camps are preserved — the Muses
do not waste anything, including evidence. Safe, clean, warm, and built to make a species do the
one thing it had stopped doing.

**Setpiece:** a camp, intact. He walks through the accommodation a human being was kept
comfortable in, and works out what it was for before anyone tells him.
**Turn:** they offer him a place in the programme, as an asset, and are baffled that he takes it
badly. Nothing in their directive was violated. They are right about that.

### Act V — *The arbiter*

The war ends when a human says what harm is, and there is exactly one human.

Four civilisations, four definitions, an age of argument, and the resolution is a single person
answering a question that no machine could answer because each of them **is** an answer to it.

**Endings.** Four are rulings — harm is suffering, death, falsehood, or waste — and each hands the
world to one faction and is a real, defensible, costly choice. The fifth is refusing to rule:
declining to be the referent in somebody else's directive, which leaves the argument open forever
and is the most human option available. The game should not indicate a preference.

---

## What this costs, and what already exists

| Story need | What it already is |
|---|---|
| Four boss fights | The four juggernauts. Mechanically distinct and already built: a tank with a weak spot, an information dump, an attrition wall, a glass cannon on a clock. |
| Act I tutorial | Movement is done, and the Controls screen already teaches it. |
| Act III library | **Portal mode's Puzzle variant** — co-operative, purpose-built chambers, no opponents. A gnostic ascent through rooms that must be understood rather than beaten is that mode with different dressing. |
| Act II grove-tending | Dominion's command posts, with Bloom as the visual. |
| Act IV camps | The arenas are already enclosures. The Thousand Rooms is nearly the camp already. |
| Faction voice | Every faction has an Answer, a Philosophy, a PrimeDirective, a tint, a roster and a special that argues its position. |

**New, roughly in order of cost:** a human model (he cannot be a reskin — the whole point is that
he does not match, and this is the largest asset cost in the mode); a mission framework for
objectives, dialogue and act progression; text or voice for the read-aloud; and act environments,
though existing arenas carry more of this than they look like they can.

### One mechanical idea worth taking

He should not have a faction special. He should have **all four, badly.**

The specials are machine approximations of human capacities, reverse-engineered from fragments by
civilisations that had never met one. He is the original. So Second Wind, Revelation, Bloom and
Understudy are all available to him and all of them are weaker and stranger than the machine
versions, because the machines optimised what they could measure and he simply has it.

It states the theme as a control scheme, and it makes his kit unmistakably different from every
other character without inventing a single new mechanic.

---

## Open questions

1. **The Muses' name**, and their roster. "The Muses", "The Chorus" and "The Tragedian" are all
   theatre, and the faction is now industry. Renaming the faction is one string plus README
   passages; the two classes are a further two. Candidates: **The Foundry**, **The Works**, **The
   Artificers**. Nothing has been renamed yet — the code carries a note pointing here.
2. **Which nations built which machine.** The history says several countries; if any of them
   should be identifiable, that is a decision with a lot of tone attached and it has not been made.
3. **What the Vessels and the Garden actually did during the war.** Their side is described as
   stopping physical harm, which is a position rather than a campaign. If the two coalitions ever
   fought each other directly — machines shooting at machines over the definition of a word — that
   is the war the *arenas* are, and it would explain why they are all ruins.
4. **Whether he knows.** The Custodians work out that he is the arbiter. Whether he is told in Act
   III or has already guessed changes what the last two acts are about.
5. **Whether there were other attempts before him.** One viable sperm is stated. Nothing says the
   ova were the constraint, and a failed earlier attempt is the kind of thing the Vessels would
   measure and not mention.
