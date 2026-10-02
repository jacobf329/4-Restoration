#!/usr/bin/env python3
"""Generate the set dressing: OpenAI for the reference, Meshy for the mesh.

Both halves in one tool because a prop is only finished when it is a .glb, and
splitting it across two scripts means a half-made roster nobody can tell the
state of. Run it as often as you like: an asset that already exists is skipped,
so a run that dies part way through costs nothing to repeat.

    python3 tools/props.py                     # everything missing
    python3 tools/props.py --only ice_         # just the ice
    python3 tools/props.py --only wreck_hull --force
    python3 tools/props.py --images            # references only, no Meshy

Reads assets/props-manifest.json and writes, per prop:

    assets/props/<key>_ref.png     the reference image
    assets/props/<key>.glb         the mesh
    assets/props/<key>_look.png    a render of it, so somebody looks

The last one is not optional. Measuring a model is not looking at it, and every
time this project has skipped the render it has shipped something wrong - see
tools/look.py.

Credentials come from the environment API credentials configured at
claude.ai/code, which the proxy attaches per request. Nothing here sees a key.
"""
import argparse, base64, json, os, subprocess, sys, time
import urllib.error, urllib.request

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
MANIFEST = os.path.join(ROOT, "assets", "props-manifest.json")
OUT = os.path.join(ROOT, "assets", "props")

IMAGES = "https://api.openai.com/v1/images/generations"
MESHY = "https://api.meshy.ai/openapi/v1/image-to-3d"

# Newer than the character pipeline runs, and deliberately: the mesh is only ever
# as good as the silhouette it is lifted from, so the reference is the cheapest
# place in the whole chain to buy quality.
MODEL = os.environ.get("OPENAI_IMAGE_MODEL", "gpt-image-2")

# Measured, not assumed: gpt-image-2 answers a transparent request with a 400
# ("Transparent background is not supported for this model"), gpt-image-1 with a
# 200. A whole batch of ten died on that before anyone looked. Transparency is
# not a nicety here - it is what keeps Meshy from turning the backdrop into
# geometry - so a model that cannot do it is the wrong model, and this falls
# back to one that can rather than quietly shipping props on slabs.
ALPHA_FALLBACK = "gpt-image-1"

# A prop is scenery seen at a distance by up to four viewports at once. The
# characters get 2k for twelve-up; scenery gets less, because there is going to
# be a great deal more of it than there are people.
DEFAULT_TRIS = 1500

# The relay in front of OpenAI gives up at about thirty seconds. Asking for less
# is how a request comes back inside the window rather than as a 502.
FALLBACK = {"high": "medium", "medium": "low"}

# And props ask for little to begin with. Measured: gpt-image-2 at medium times
# the relay out and falls back anyway, so starting at medium buys nothing but a
# wasted thirty seconds per prop. What image-to-3D actually needs is a clean
# silhouette under flat light, and low gives that - the detail it drops is
# surface detail Meshy is going to re-derive from its own texturing pass.
QUALITY = "low"


def props():
    with open(MANIFEST) as f:
        return json.load(f)["props"]


# ---------------------------------------------------------------- the reference

def reference(prop, quality=QUALITY, model=MODEL):
    """One OpenAI image, transparent, ready for image-to-3D."""
    key = prop["key"]
    dest = os.path.join(OUT, key + "_ref.png")

    body = json.dumps({
        "model": model,
        "prompt": prop["prompt"],
        "size": prop.get("size", "1024x1024"),
        "quality": quality,
        "n": 1,

        # Transparent so Meshy is handed a silhouette and not a backdrop it will
        # cheerfully turn into geometry. This is the single setting that decides
        # whether a prop comes back as an object or as an object on a slab.
        "background": "transparent",
        "output_format": "png",
    }).encode()

    req = urllib.request.Request(IMAGES, data=body,
                                 headers={"Content-Type": "application/json"})

    print(f"  image ({model}, {quality})...")
    try:
        with urllib.request.urlopen(req, timeout=600) as r:
            payload = json.loads(r.read())
    except urllib.error.HTTPError as e:
        detail = e.read().decode(errors="replace")[:400]
        print(f"  ! HTTP {e.code} {detail.strip()}")

        if e.code == 502 and quality in FALLBACK:
            print(f"  relay timed out; retrying at {FALLBACK[quality]}")
            return reference(prop, FALLBACK[quality], model)

        if e.code == 400 and "background" in detail and model != ALPHA_FALLBACK:
            print(f"  {model} will not do alpha; retrying on {ALPHA_FALLBACK}")
            return reference(prop, quality, ALPHA_FALLBACK)
        return None

    data = payload["data"][0]
    if "b64_json" not in data:
        print(f"  ! unexpected response shape: {sorted(data)}")
        return None

    with open(dest, "wb") as f:
        f.write(base64.b64decode(data["b64_json"]))

    print(f"  {os.path.basename(dest)} ({os.path.getsize(dest) // 1024} KB)")
    return dest


# ---------------------------------------------------------------- the mesh

def call(url, payload=None, method="GET"):
    body = json.dumps(payload).encode() if payload is not None else None
    req = urllib.request.Request(url, data=body, method=method,
                                 headers={"Content-Type": "application/json"})

    # Only used outside the managed environment; normally unset.
    key = os.environ.get("MESHY_API_KEY")
    if key:
        req.add_header("Authorization", "Bearer " + key)

    with urllib.request.urlopen(req, timeout=180) as r:
        return json.loads(r.read())


def mesh(prop, tris):
    key = prop["key"]
    src = os.path.join(OUT, key + "_ref.png")
    dest = os.path.join(OUT, key + ".glb")

    with open(src, "rb") as f:
        uri = "data:image/png;base64," + base64.b64encode(f.read()).decode()

    task = call(MESHY, {
        "image_url": uri,
        "ai_model": "meshy-5",
        "topology": "triangle",
        "target_polycount": tris,
        "should_remesh": True,
        "should_texture": True,
        "enable_pbr": True,
    }, "POST")["result"]

    print(f"  meshy task {task}")

    last = None
    while True:
        s = call(f"{MESHY}/{task}")
        note = f"{s['status']} {s.get('progress', 0)}%"
        if note != last:
            print("    " + note)
            last = note
        if s["status"] == "SUCCEEDED":
            break
        if s["status"] in ("FAILED", "CANCELED"):
            print(f"  ! {s['status']}: {s.get('task_error')}")
            return None
        time.sleep(10)

    with urllib.request.urlopen(s["model_urls"]["glb"], timeout=600) as r:
        blob = r.read()
    with open(dest, "wb") as f:
        f.write(blob)

    print(f"  {os.path.basename(dest)} ({len(blob) // 1024} KB)")
    record(key, task)
    return dest


def record(key, task):
    """Keep the Meshy task ids. Rigging and re-texturing both take the id back."""
    path = os.path.join(ROOT, "assets", "meshy-props.json")
    jobs = {}
    if os.path.exists(path):
        with open(path) as f:
            jobs = json.load(f)

    jobs[key] = task
    with open(path, "w") as f:
        json.dump(jobs, f, indent=2, sort_keys=True)
        f.write("\n")


def measure(key):
    """Record what Meshy actually returned, in its own units.

    This exists because of a real mistake. The manifest's `extents` were authored by hand before
    any mesh existed, and the first drift came back nearly square in plan (1.89 x 1.79) against a
    box written 12m long - so fitted uniformly it would have filled a third of its own collider and
    the box would have been a lie about what you can hide behind.

    You cannot know a generated mesh's proportions in advance, so the arena reads them from here
    afterwards instead of guessing them beforehand. Only the RATIO matters: the arena picks how big
    it wants the prop and the box comes out at the mesh's own shape.
    """
    import look as looker

    tris, _, _ = looker.triangles(os.path.join(OUT, key + ".glb"))
    lo = tris.reshape(-1, 3).min(axis=0)
    hi = tris.reshape(-1, 3).max(axis=0)

    size = [round(float(v), 4) for v in (hi - lo)]

    with open(MANIFEST) as f:
        doc = json.load(f)

    for prop in doc["props"]:
        if prop["key"] == key:
            prop["measured"] = size

    with open(MANIFEST, "w") as f:
        json.dump(doc, f, indent=2)
        f.write("\n")

    print(f"  measured {size[0]:.2f} x {size[1]:.2f} x {size[2]:.2f}")
    return size


def look(path):
    subprocess.run([sys.executable, os.path.join(ROOT, "tools", "look.py"), path,
                    "-o", path[:-4] + "_look.png"], check=False)


# ---------------------------------------------------------------- driver

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--only", default="", help="substring of the prop key")
    ap.add_argument("--force", action="store_true", help="remake what exists")
    ap.add_argument("--images", action="store_true", help="references only")
    ap.add_argument("--tris", type=int, default=DEFAULT_TRIS)
    ap.add_argument("--measure", action="store_true",
                    help="re-measure meshes already on disk and stop")
    a = ap.parse_args()

    os.makedirs(OUT, exist_ok=True)
    sys.path.insert(0, os.path.join(ROOT, "tools"))
    made = failed = 0

    if a.measure:
        for prop in props():
            if a.only and a.only not in prop["key"]:
                continue
            if os.path.exists(os.path.join(OUT, prop["key"] + ".glb")):
                print(f"== {prop['key']} ==")
                measure(prop["key"])
        return 0

    for prop in props():
        key = prop["key"]
        if a.only and a.only not in key:
            continue

        ref = os.path.join(OUT, key + "_ref.png")
        glb = os.path.join(OUT, key + ".glb")

        if os.path.exists(glb) and not a.force and not a.images:
            continue

        print(f"\n== {key} ==")

        if a.force or not os.path.exists(ref):
            if reference(prop) is None:
                failed += 1
                continue

        if a.images:
            made += 1
            continue

        if a.force or not os.path.exists(glb):
            if mesh(prop, a.tris) is None:
                failed += 1
                continue

        measure(key)
        look(glb)
        made += 1

    print(f"\n{made} made, {failed} failed")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
