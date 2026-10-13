#!/usr/bin/env python3
"""
Generate the environment's PBR material library.

Why generate rather than photograph: every one of these has to tile seamlessly, at a texel
density the game picks per material, under a triplanar projection that will show a seam from
across the map if there is one. Procedural noise built on a wrapping lattice is seamless by
construction; a photograph has to be made seamless by hand and then stays that way only until
somebody crops it.

Writes three maps per material into assets/surfaces, named the way Surfaces.Load looks for them:

    <name>_base_color.png          what it looks like
    <name>_normal.png              tangent-space normals, +Y up
    <name>_metallic_roughness.png  glTF packing - G is roughness, B is metallic

Run:  python3 tools/surfaces.py            (all of them)
      python3 tools/surfaces.py brick      (just one)
"""

import os, sys, zlib, struct
import numpy as np

SIZE = 512
OUT = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
                   "assets", "surfaces")

rng = np.random.default_rng(20260909)


# ---------------------------------------------------------------- png

def write_png(path, arr):
    """arr: HxWx3 float 0..1 -> 8-bit RGB PNG."""
    h, w, _ = arr.shape
    data = (np.clip(arr, 0.0, 1.0) * 255.0 + 0.5).astype(np.uint8)
    raw = b"".join(b"\x00" + data[y].tobytes() for y in range(h))

    def chunk(tag, payload):
        body = tag + payload
        return struct.pack(">I", len(payload)) + body + struct.pack(">I", zlib.crc32(body) & 0xffffffff)

    with open(path, "wb") as f:
        f.write(b"\x89PNG\r\n\x1a\n")
        f.write(chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 2, 0, 0, 0)))
        f.write(chunk(b"IDAT", zlib.compress(raw, 9)))
        f.write(chunk(b"IEND", b""))


# ---------------------------------------------------------------- noise

def value_noise(freq, seed):
    """
    Tileable value noise: a random lattice of freq x freq, wrapped, smoothly interpolated.

    Wrapping the lattice index with % freq is the whole trick - the right edge samples the same
    lattice points as the left, so the result is periodic and therefore seamless.
    """
    r = np.random.default_rng(seed)
    lat = r.random((freq, freq))

    t = np.linspace(0.0, freq, SIZE, endpoint=False)
    i0 = np.floor(t).astype(int) % freq
    i1 = (i0 + 1) % freq
    f = t - np.floor(t)
    f = f * f * (3.0 - 2.0 * f)                       # smoothstep

    a = lat[np.ix_(i0, i0)] * (1 - f)[None, :] + lat[np.ix_(i0, i1)] * f[None, :]
    b = lat[np.ix_(i1, i0)] * (1 - f)[None, :] + lat[np.ix_(i1, i1)] * f[None, :]
    return a * (1 - f)[:, None] + b * f[:, None]


def fbm(octaves=5, freq=4, seed=0, gain=0.5):
    out = np.zeros((SIZE, SIZE))
    amp, total = 1.0, 0.0
    for o in range(octaves):
        out += amp * value_noise(freq * 2 ** o, seed + o * 977)
        total += amp
        amp *= gain
    return out / total


def norm(a):
    lo, hi = a.min(), a.max()
    return (a - lo) / (hi - lo) if hi > lo else a * 0.0


# ---------------------------------------------------------------- maps

def normal_map(height, strength=2.0):
    """
    Tangent-space normal from a height field, by central difference with wrapping.

    np.roll is what keeps this seamless: the gradient at the edge is computed against the
    opposite edge, exactly as the tiling will present it.
    """
    dx = (np.roll(height, -1, axis=1) - np.roll(height, 1, axis=1)) * strength
    dy = (np.roll(height, -1, axis=0) - np.roll(height, 1, axis=0)) * strength

    n = np.stack([-dx, dy, np.ones_like(height)], axis=-1)
    n /= np.linalg.norm(n, axis=-1, keepdims=True)
    return n * 0.5 + 0.5


def mr_map(roughness, metallic=0.0):
    """glTF packing: R free, G roughness, B metallic."""
    z = np.zeros((SIZE, SIZE))
    m = np.full((SIZE, SIZE), metallic) if np.isscalar(metallic) else metallic
    return np.stack([z, roughness, m], axis=-1)


def tint(height, base, spread, mottle):
    """Colour from a height field plus a low-frequency stain, so it is not flat."""
    base = np.array(base, dtype=float)
    spread = np.array(spread, dtype=float)
    shade = (height[..., None] - 0.5) * 2.0 * spread[None, None, :]
    return base[None, None, :] + shade + (mottle[..., None] - 0.5) * 0.08


# ---------------------------------------------------------------- materials

def bricks():
    """Running bond: every other course offset by half a brick, mortar recessed."""
    rows, cols = 16, 8
    y = np.arange(SIZE) / SIZE * rows
    x = np.arange(SIZE) / SIZE * cols

    course = np.floor(y).astype(int)
    offset = (course % 2) * 0.5
    bx = np.floor(x[None, :] + offset[:, None]).astype(int)

    fy = y - np.floor(y)
    fx = (x[None, :] + offset[:, None]) % 1.0

    mortar = 0.055
    edge = np.minimum(np.minimum(fx, 1 - fx), np.minimum(fy, 1 - fy)[:, None])
    face = np.clip((edge - mortar) / 0.05, 0.0, 1.0)

    ident = (course[:, None] * 131 + bx * 17) % 97
    per = (ident / 97.0)

    grain = fbm(4, 16, 11) * 0.35
    height = face * (0.72 + per * 0.18) + grain * face + (1 - face) * 0.12

    col = tint(height, [0.44, 0.20, 0.15], [0.16, 0.09, 0.07], fbm(3, 3, 21))
    mortar_col = np.array([0.62, 0.60, 0.56])
    col = col * face[..., None] + mortar_col[None, None, :] * (1 - face)[..., None]

    rough = 0.78 + (1 - face) * 0.14 + grain * 0.1
    return col, height, rough


def roof_tile():
    """
    Barrel tiles: half-round, overlapping up the slope, with a shadow under every course.

    The first attempt at this came out reading as more brick, and the reason is worth keeping:
    the profile was too shallow and the courses met flush. What makes a roof legible from the
    ground is not the shape of one tile, it is the row of hard shadows where each course laps
    over the one below. So the overlap is exaggerated and the shadow is drawn explicitly.
    """
    rows, cols = 9, 7
    y = np.arange(SIZE) / SIZE * rows
    x = np.arange(SIZE) / SIZE * cols

    course = np.floor(y).astype(int)
    offset = (course % 2) * 0.5
    fy = (y - np.floor(y))[:, None]
    fx = (x[None, :] + offset[:, None]) % 1.0

    # Half-round across the tile. This is the shape, and it wants to be obvious.
    barrel = np.sin(np.clip(fx, 0.0, 1.0) * np.pi) ** 0.45

    # Up the slope: proud at the lower edge where it laps the course below, tapering away.
    lap = np.clip(1.0 - fy * 0.75, 0.0, 1.0) ** 1.4

    # The shadow line. A band at the top of each course, which is where the tile above sits on it.
    shade = np.clip(1.0 - fy / 0.16, 0.0, 1.0)

    height = barrel * (0.35 + lap * 0.65) - shade * 0.55

    ident = ((course[:, None] * 71 + np.floor(x[None, :] + offset[:, None]).astype(int) * 29) % 89) / 89.0
    weather = fbm(4, 8, 34)

    col = tint(norm(height) + ident * 0.22, [0.42, 0.21, 0.16], [0.15, 0.09, 0.07], fbm(3, 4, 33))
    col *= (1.0 - shade * 0.45)[..., None]
    col *= (0.88 + weather * 0.24)[..., None]

    rough = 0.66 + (1 - barrel) * 0.22 + weather * 0.1
    return col, norm(height), rough


def plaster():
    h = fbm(6, 4, 41) * 0.55 + fbm(4, 24, 42) * 0.45
    col = tint(h, [0.80, 0.77, 0.70], [0.05, 0.05, 0.05], fbm(3, 3, 43))
    return col, h, 0.88 + h * 0.08


def concrete():
    h = fbm(6, 5, 51)
    # Pinholes, which is the detail that stops concrete reading as grey plaster.
    holes = (fbm(2, 64, 52) > 0.74).astype(float)
    h = h * 0.85 - holes * 0.25

    # Faint horizontal form lines from the shuttering.
    lines = (np.sin(np.arange(SIZE) / SIZE * np.pi * 2 * 4) ** 8)[:, None]
    h = h - lines * 0.08

    col = tint(norm(h), [0.52, 0.52, 0.50], [0.10, 0.10, 0.10], fbm(3, 2, 53))
    return col, norm(h), 0.86 + norm(h) * 0.10


def tarmac():
    """
    Asphalt: aggregate you can see but mostly cannot feel.

    The relief and the colour deliberately disagree here. A first version drove the normal map
    straight off the aggregate mask and the result was a wall of per-pixel noise, which is the
    classic way to make a road shimmer from across a map - every step the camera takes resamples
    a different set of chips. Roads are visually busy and physically nearly flat, so the chips
    stay in the albedo and the height field is the slow undulation of the surface itself.
    """
    agg = (fbm(2, 96, 62) > 0.62).astype(float)
    fine = fbm(3, 48, 64)

    # Height: the road's own waviness, plus a fraction of the coarse grain. No chips.
    h = norm(fbm(4, 12, 61) * 0.75 + fbm(3, 26, 65) * 0.25)

    col = tint(h, [0.17, 0.17, 0.18], [0.05, 0.05, 0.05], fbm(3, 3, 63))
    col += agg[..., None] * 0.10           # chips, lighter than the binder
    col += (fine[..., None] - 0.5) * 0.04

    rough = 0.88 - agg * 0.10 + (h - 0.5) * 0.06
    return col, h, rough


def foliage():
    """Clumped leaves rather than green noise: a few scales of blob, thresholded."""
    big = fbm(3, 6, 71)
    leaf = fbm(3, 28, 72)
    h = big * 0.45 + leaf * 0.55
    mask = np.clip((h - 0.42) / 0.25, 0.0, 1.0)

    dark = np.array([0.11, 0.20, 0.09])
    light = np.array([0.34, 0.52, 0.20])
    col = dark[None, None, :] + (light - dark)[None, None, :] * mask[..., None]
    col += (fbm(3, 12, 73)[..., None] - 0.5) * 0.06
    return col, h, 0.74 + (1 - mask) * 0.16


def timber():
    """Grain running along one axis, with knots where a low-frequency field peaks."""
    y = np.arange(SIZE)[:, None] / SIZE
    wobble = fbm(4, 6, 81) * 0.10
    rings = np.sin((y + wobble) * np.pi * 2 * 26) * 0.5 + 0.5
    grain = rings ** 2 * 0.5 + fbm(4, 40, 82) * 0.5

    knots = fbm(2, 5, 83)
    knot_mask = np.clip((knots - 0.70) / 0.10, 0.0, 1.0)
    h = grain * (1 - knot_mask) + knot_mask * 0.15

    col = tint(h, [0.46, 0.31, 0.18], [0.13, 0.10, 0.06], fbm(3, 3, 84))
    col *= (1 - knot_mask * 0.45)[..., None]

    # Board joints, so it reads as planks rather than one enormous piece of wood.
    seam = ((np.arange(SIZE) / SIZE * 4) % 1.0)
    joint = np.clip(np.minimum(seam, 1 - seam) / 0.012, 0.0, 1.0)[None, :]
    h = h * joint
    col *= (0.55 + 0.45 * joint)[..., None]

    return col, h, 0.66 + (1 - h) * 0.18



def _plates(cols, rows, groove, seed):
    """
    A grid of rectangular plates with recessed seams, and a value per plate.

    The per-plate value is the whole reason this exists. A seam grid alone is graph paper:
    every cell is identical, so a fifty-metre wall is a hundred copies of one square and the
    eye reads the repeat rather than the surface. Giving each plate its own brightness breaks
    the lattice without adding a single feature, which is how real plating reads - you see
    panels of slightly different metal, not a drawn grid.

    Returns (face, ident) where face is 1 on a plate and 0 in a seam, and ident is a stable
    0..1 number per plate.
    """
    x = np.arange(SIZE) / SIZE * cols
    y = np.arange(SIZE) / SIZE * rows

    cx = np.floor(x).astype(int)[None, :]
    cy = np.floor(y).astype(int)[:, None]

    fx = (x % 1.0)[None, :]
    fy = (y % 1.0)[:, None]

    edge = np.minimum(np.minimum(fx, 1 - fx), np.minimum(fy, 1 - fy))
    face = np.clip((edge - groove) / 0.012, 0.0, 1.0)

    r = np.random.default_rng(seed)
    table = r.random((rows, cols))
    ident = table[np.clip(cy, 0, rows - 1), np.clip(cx, 0, cols - 1)]

    return face, ident


def hull():
    """
    Station hull plating: the walls, the ceilings, the bulkheads.

    This replaces a procedural grid that was the single worst thing in the game to look at. The
    old one drew one 1.3-metre square repeated across every surface at the same brightness, and
    on a fifty-metre bulkhead that is several hundred identical cells - which does not read as
    metal, it reads as graph paper, and that is exactly the word the complaint used.

    Three things fix it and all three are about breaking the lattice rather than about detail:

      - Two scales of plate, a large one and a smaller one inside it, so there is no single
        period for the eye to lock onto.
      - A brightness per plate, which is what real plating has and a drawn grid never does.
      - Panel channels: short recessed runs that cross plates and ignore the grid entirely.
        This is the thing the films are actually made of - the surface is covered in greebling
        that has no relationship to any underlying tiling - and it is what stops a wall reading
        as a texture at all.
    """
    big, bident = _plates(2, 2, 0.016, 2051)
    small, sident = _plates(6, 6, 0.013, 2052)

    # Height: the large seams cut deepest, the small ones are a scribe line.
    h = 0.80 - (1 - big) * 0.34 - (1 - small) * 0.11

    # Per-plate value. Small and deliberately so - this is a few percent of albedo, far below
    # the level anybody notices as a colour, and entirely enough to stop the grid.
    plate = (bident - 0.5) * 0.13 + (sident - 0.5) * 0.075

    # Greebling: recessed rectangular inlays and straight scribe lines, laid over the plating
    # without regard for it.
    #
    # Axis-aligned and rectangular, which took two goes to get right. The first version drew
    # these from ridged noise, and ridged noise makes WANDERING lines - the result was a wall
    # covered in meandering worms that read as cracked paint or as camouflage, not as a
    # machine. Everything on a hull was cut by a machine from a drawing, so everything on it
    # is straight and meets at right angles, and that is the entire difference between this
    # looking like a spacecraft and looking like a rock.
    cells = 14
    gx = np.arange(SIZE) / SIZE * cells
    gy = np.arange(SIZE) / SIZE * cells

    r = np.random.default_rng(2061)
    pick = (r.random((cells, cells)) < 0.17).astype(float)
    inset = 0.06 + r.random((cells, cells)) * 0.13

    ix = np.floor(gx).astype(int)[None, :]
    iy = np.floor(gy).astype(int)[:, None]

    fx = (gx % 1.0)[None, :]
    fy = (gy % 1.0)[:, None]

    margin = inset[iy, ix]
    inner = np.minimum(np.minimum(fx, 1 - fx), np.minimum(fy, 1 - fy))
    inlay = np.clip((inner - margin) / 0.03, 0.0, 1.0) * pick[iy, ix]

    # Scribe lines: a handful of full-length cuts, at cell boundaries so they line up with the
    # inlays rather than crossing them at random.
    lines = np.zeros(SIZE)
    for c in r.choice(cells, 4, replace=False):
        d = np.abs(((np.arange(SIZE) / SIZE * cells) - c + cells / 2) % cells - cells / 2)
        lines = np.maximum(lines, np.clip(1.0 - d / 0.10, 0.0, 1.0))

    scribe = np.maximum(lines[None, :] * np.ones((SIZE, 1)), lines[:, None] * np.ones((1, SIZE)))

    chan = np.maximum(inlay, scribe)
    h = h - inlay * 0.22 - scribe * 0.26

    # Vents: a band of close parallel slots, only where a low-frequency mask allows it, so they
    # appear in patches rather than striping the entire station.
    slots = (np.sin(np.arange(SIZE) / SIZE * np.pi * 2 * 48) ** 12)[:, None] * np.ones((1, SIZE))
    where = np.clip((fbm(2, 4, 2071) - 0.70) / 0.08, 0.0, 1.0)
    h = h - slots * where * 0.30

    grain = fbm(5, 18, 2081)
    h = h + (grain - 0.5) * 0.06

    col = tint(norm(h), [0.60, 0.61, 0.64], [0.10, 0.10, 0.11], fbm(3, 3, 2091))
    col = col * (1.0 + plate)[..., None]
    col = col * (1.0 - chan * 0.22)[..., None]
    col = col * (1.0 - slots * where * 0.30)[..., None]

    # Seams are dirtier than the faces they divide, which is where grime collects on anything
    # built out of bolted panels.
    rough = 0.52 + (1 - small) * 0.20 + (1 - big) * 0.14 + chan * 0.12 + grain * 0.10
    return col, norm(h), np.clip(rough, 0.0, 1.0)


def deck():
    """
    Station decking: everything you stand on.

    Darker and busier than the walls, because a floor in this game is seen at a glancing angle
    from one metre up and at close to normal from five - it has to hold up at both. The tread
    is the detail that does that: too fine and it dissolves to speckle at range, too coarse and
    it reads as tiling, so it sits on a plate grid that is coarser than the tread by about four
    times and the two periods cover for each other.
    """
    face, ident = _plates(4, 4, 0.014, 3051)

    # Raised tread: a diagonal crosshatch, which is what plate floors are and reads as grip
    # rather than as pattern.
    u = np.arange(SIZE) / SIZE
    a = np.sin((u[None, :] + u[:, None]) * np.pi * 2 * 22)
    b = np.sin((u[None, :] - u[:, None]) * np.pi * 2 * 22)
    tread = np.clip(np.maximum(a, b), 0.0, 1.0) ** 3

    # Worn through where traffic goes, which is also where the tread stops being tread.
    wear = np.clip((fbm(4, 6, 3061) - 0.42) / 0.30, 0.0, 1.0)

    h = 0.46 + tread * 0.26 * (1 - wear * 0.7) - (1 - face) * 0.36
    grain = fbm(5, 22, 3071)
    h = h + (grain - 0.5) * 0.05

    col = tint(norm(h), [0.30, 0.31, 0.34], [0.09, 0.09, 0.10], fbm(3, 3, 3081))
    col = col * (1.0 + (ident - 0.5) * 0.11)[..., None]
    col = col * (1.0 - wear * 0.10)[..., None]

    rough = 0.60 + (1 - face) * 0.18 - tread * 0.10 + wear * 0.14 + grain * 0.08
    return col, norm(h), np.clip(rough, 0.0, 1.0)


# ---------------------------------------------------------------- the world grade
#
# Every material passes through this on the way out, and it is the whole reason they look like
# one place rather than seven downloads.
#
# The first set was generated independently, each material picking its own palette, its own value
# range and its own saturation. Individually each was defensible; together they had no continuity
# at all - a near-black road beside a bright orange brick beside a saturated green hedge, none of
# them agreeing about how bright the sun is or how dirty the world is.
#
# So the materials now describe only their own STRUCTURE - the courses, the grain, the aggregate -
# and this pass decides what the world looks like. It is the same idea as grading a film: shoot
# the scenes however you must, then put every frame through one look.

def snow():
    """Trodden snow: soft dunes, a wind crust, and the odd boot-broken patch.

    The hardest thing to get right is that snow is nearly white and almost featureless, so all
    the readability has to come from the relief - a flat white plane is invisible to play on.
    Two scales of drift plus a fine crust give it enough shading to judge distance across.
    """
    drift = fbm(4, 3, 71)                       # long dunes the wind pushed
    crust = fbm(6, 14, 72) * 0.35               # the frozen skin on top
    h = norm(drift * 0.8 + crust)

    # Broken patches where it has been walked through, darker and rougher than the crust.
    broken = (fbm(3, 9, 73) > 0.72).astype(float)
    h = norm(h - broken * 0.18)

    # Barely tinted, and very slightly blue in the hollows, which is what sells it as cold.
    col = tint(h, [0.97, 0.98, 1.00], [0.035, 0.028, 0.018], fbm(3, 5, 74))
    return col, h, 0.62 + (1.0 - h) * 0.22


def ice():
    """Glare ice: fracture planes, trapped air, and almost no roughness.

    The opposite problem to snow - this one wants to be smooth enough to catch the light, so the
    relief is all long cracks rather than grain, and the roughness sits far below everything else
    in the set so it reads as polished against the snow beside it.
    """
    # Fracture lines: thin, long and branching. A ridged field at high frequency, taken right at
    # its crest - a lower frequency or a looser threshold gives broad blotches, which read as
    # camouflage rather than as cracks. That was the first attempt and it looked like it.
    veins = 1.0 - np.abs(fbm(4, 16, 81) * 2.0 - 1.0)
    cracks = np.clip((veins - 0.93) / 0.07, 0.0, 1.0)

    bubbles = (fbm(4, 26, 82) > 0.86).astype(float) * 0.3
    h = norm(fbm(3, 5, 83) * 0.35 - cracks * 0.9 + bubbles * 0.15)

    col = tint(h, [0.80, 0.88, 0.94], [0.06, 0.05, 0.03], fbm(4, 3, 84))
    return col, h, 0.18 + norm(h) * 0.16

def glass():
    """Glazing: panes in a painted lattice, seen from inside a damp building.

    Two materials in one tile, which is what a glasshouse wall is - a bar every so often and
    nothing at all in between. The bars carry every bit of the relief and most of the roughness;
    the panes are flat and smooth, and what little they have is condensation running down them and
    the dirt that collects where they meet the frame.

    Deliberately not transparent. Nothing in this renderer is, and a glasshouse read at arena scale
    is a pale lattice with a bright sheen between the bars - which is what this is - rather than a
    hole you can see through. Making it transparent would also make it cover you, which is the
    opposite of what a wall in a shooter is for.
    """
    # Tall and narrow, the way glazing is actually cut. Square panes read as wall tiling - that
    # was the first attempt and it looked like a bathroom.
    cols, rows = 4, 3
    x = np.arange(SIZE) / SIZE * cols
    y = np.arange(SIZE) / SIZE * rows

    fx = (x % 1.0)[None, :]
    fy = (y % 1.0)[:, None]

    # The bar: a margin around every pane, feathered over a few texels so the edge catches the
    # light rather than aliasing into a dotted line at distance. Narrower across than up, because
    # the glazing bar is one section turned two ways and the panes are not square.
    edge = np.minimum(np.minimum(fx, 1 - fx) * cols, np.minimum(fy, 1 - fy) * rows)
    pane = np.clip((edge - 0.22) / 0.10, 0.0, 1.0)

    # Condensation, only on the glass. Stretched vertically by sampling a field that is fine
    # across and coarse down, which is what makes it read as running rather than as blotches.
    runs = norm(value_noise(48, 91)[:, :] * 0.6 + fbm(3, 6, 93) * 0.4)
    runs = np.clip((runs - 0.46) / 0.30, 0.0, 1.0)
    runs = np.minimum(runs, np.roll(runs, 3, axis=0)) * pane

    # And the dirt where the glass meets the frame, which is the only thing saying it is old.
    grime_edge = np.clip(1.0 - (edge - 0.22) / 0.55, 0.0, 1.0) ** 2 * pane

    height = pane * (0.30 + runs * 0.10) + (1 - pane) * 0.94

    frame = np.array([0.87, 0.88, 0.85])                 # white-painted iron, gone grey
    lit = tint(0.55 + runs * 0.25, [0.70, 0.80, 0.79], [0.07, 0.05, 0.04], fbm(3, 4, 92))

    col = lit * pane[..., None] + frame[None, None, :] * (1 - pane)[..., None]
    col *= (1.0 - grime_edge * 0.26)[..., None]

    # The one place in the set where roughness carries the material: painted metal is matt and
    # glass is not, and the difference between the two is the whole read.
    rough = 0.88 - pane * 0.74 + runs * 0.22 + grime_edge * 0.25
    return col, height, np.clip(rough, 0.0, 1.0)


WORLD_TINT = np.array([1.00, 0.975, 0.94])   # a slightly warm sun, on everything

# Contrast is pulled toward this rather than normalised per material, which matters: normalising
# each one would flatten the differences BETWEEN them and make tarmac as bright as plaster.
# Compressing around a shared midpoint keeps the road the darkest thing and the render the
# lightest, while stopping either from reaching an extreme nothing else in the world reaches.
WORLD_MID = 0.40
WORLD_CONTRAST = 0.62

SATURATION = 0.55        # nothing in a real place is as saturated as a texture generator wants
GRIME = 0.22             # how strongly the shared dirt field shows through

LUMA = np.array([0.299, 0.587, 0.114])


def world_grime():
    """
    One dirt field, shared by every material.

    The same seed for all of them on purpose. Two materials meeting at a corner with unrelated
    weathering read as two objects from different worlds set next to each other; the same field
    across both reads as one building that has been rained on.
    """
    return fbm(5, 6, 90101) * 0.6 + fbm(4, 20, 90102) * 0.4


def grade(col, grime, ceiling=0.86, grime_scale=1.0, contrast=None, warm=1.0, level=1.0):
    """Desaturate, compress into the shared value range, warm it, then dirty it.

    `ceiling` and `grime_scale` exist for snow, and only barely. The shared range is what keeps
    seven materials looking like one world, and raising the roof for a material is a thing to do
    once with a reason rather than whenever something looks dull.

    The reason is that snow is the brightest surface there is - it is what "white" is measured
    against - and put through the shared settings it came out at 0.72 and read as poured concrete.
    A snow map made of concrete is not a snow map.

    Raising the ceiling alone did not fix it, which is worth writing down: the ceiling was never
    what bound. Compressing toward WORLD_MID at 0.62 takes an input of 0.97 down to 0.75 all by
    itself, and the clamp at 0.86 never even engages. So snow also relaxes the compression. And it
    turns down WORLD_TINT, because the shared warm sun on a near-white surface is the difference
    between snow and sand - at full strength it came out beige.

    `level` is the correction to that correction, and it is a separate knob because it does a
    different thing. Ceiling and contrast decide how bright a material may be relative to the
    others; level decides how much of the renderer's dynamic range it is allowed to spend. Snow
    came out at 0.92 mean with a range of nine hundredths, which is not a bright material, it is a
    clipped one: the scene's ambient runs at 1.55 and ACES rolls everything above white into the
    same white, so a snowfield rendered flat and featureless to the horizon. Scaling it down keeps
    every bit of the variation and gives the tonemapper somewhere to put it.
    """
    lum = col @ LUMA
    col = lum[..., None] + (col - lum[..., None]) * SATURATION

    lum = np.clip(col @ LUMA, 1e-4, None)
    k = WORLD_CONTRAST if contrast is None else contrast
    want = np.clip(WORLD_MID + (lum - WORLD_MID) * k, 0.04, ceiling)
    col = col * (want / lum)[..., None]

    col = col * (1.0 + (WORLD_TINT - 1.0) * warm)[None, None, :]
    col = col * (1.0 - GRIME * grime_scale * (1.0 - grime)[..., None])
    return col * level


# Materials that sit above the shared ceiling, and how much of the shared dirt they take.
# Everything not named here uses the defaults, which is the whole set except these two.
GRADE = {
    "snow": dict(ceiling=0.985, grime_scale=0.30, contrast=0.95, warm=0.25, level=0.85),
    "glass": dict(ceiling=0.93, grime_scale=0.60, contrast=0.85, warm=0.55),
    "ice":  dict(ceiling=0.94,  grime_scale=0.55, contrast=0.80, warm=0.35, level=0.94),

    # The station is not the village, and the shared grade is the village's. Left on the
    # defaults the hull came out beige - WORLD_TINT is a warm sun on a world lit by one - and a
    # beige Death Star is a corridor in a hotel. So both station materials take almost none of
    # the warmth, and they relax the compression toward WORLD_MID so the deck is allowed to
    # stay darker than the wall above it. That contrast between floor and bulkhead is most of
    # what makes an interior readable; flattened to one value, a corner stops being a corner.
    "hull": dict(ceiling=0.82, grime_scale=0.75, contrast=0.80, warm=0.30),
    "deck": dict(ceiling=0.74, grime_scale=0.70, contrast=0.80, warm=0.30),
}


# How metallic a material is. Everything in the village set is zero and should be: brick, render
# and tarmac are dielectrics, and a metallic brick is a black brick.
#
# The station is the exception and it is a small number on purpose. Metal without an environment
# to reflect goes BLACK, not shiny - there is no reflection probe in here - so this is enough to
# tip the specular response toward metal and not enough to lose the surface if the ambient drops.
METALLIC = {
    "hull": 0.28,
    "deck": 0.14,
}


# Roughness lives in one band too. A world where one surface is glassy and the next is chalk
# reads as a materials test; the eye picks up an inconsistent specular response long before it
# consciously notices a colour being wrong.
ROUGH_BAND = (0.58, 0.92)


def grade_rough(rough, grime):
    rough = ROUGH_BAND[0] + np.clip(rough, 0.0, 1.0) * (ROUGH_BAND[1] - ROUGH_BAND[0])
    return np.clip(rough + (grime - 0.5) * 0.10, 0.0, 1.0)


MATERIALS = {
    "brick": bricks,
    "roof_tile": roof_tile,
    "plaster": plaster,
    "concrete": concrete,
    "tarmac": tarmac,
    "foliage": foliage,
    "timber": timber,
    "snow": snow,
    "ice": ice,
    "glass": glass,
    "hull": hull,
    "deck": deck,
}

# How pronounced each material's relief is. Brick and tile are real geometry being faked and
# want a lot; plaster is nearly flat and looks absurd with the same setting.
# Relief, on one scale rather than seven unrelated ones. Read as multiples of each other: brick
# and tile are real geometry being faked and want the most, plaster is nearly flat, a road is
# flatter still.
STRENGTH = {
    "brick": 5.0, "roof_tile": 7.0, "plaster": 1.4, "concrete": 2.2,
    "tarmac": 1.0, "foliage": 3.5, "timber": 2.6,

    # Snow is all relief and no colour, so it gets more than it looks like it should. Ice is the
    # other way about: nearly flat, and what shape it has is cracks.
    "snow": 3.2, "ice": 1.6,

    # Glass is flat and its frame is not, so the relief is entirely in the lattice. High enough
    # that the bars read as standing proud of the panes at arena distance.
    "glass": 4.2,

    # Plating is shallow in reality - a seam is a few millimetres - but it is the ONLY thing
    # giving a flat bulkhead any shape at all, so it is pushed well past life size. Without
    # this the walls go back to being one flat value with a pattern drawn on them.
    "hull": 4.6, "deck": 3.4,
}


def main():
    os.makedirs(OUT, exist_ok=True)
    want = sys.argv[1:] or list(MATERIALS)

    for name in want:
        if name not in MATERIALS:
            print(f"  ? no material called {name}")
            continue

        col, height, rough = MATERIALS[name]()
        if np.isscalar(rough):
            rough = np.full((SIZE, SIZE), float(rough))

        grime = world_grime()
        col = grade(col, grime, **GRADE.get(name, {}))
        rough = grade_rough(rough, grime)

        write_png(f"{OUT}/{name}_base_color.png", col)
        write_png(f"{OUT}/{name}_normal.png", normal_map(norm(height), STRENGTH[name]))
        write_png(f"{OUT}/{name}_metallic_roughness.png", mr_map(rough, METALLIC.get(name, 0.0)))

        kb = sum(os.path.getsize(f"{OUT}/{name}_{s}.png")
                 for s in ("base_color", "normal", "metallic_roughness")) // 1024
        print(f"  {name:10s} albedo+normal+mr, {kb} KB total")


if __name__ == "__main__":
    main()
