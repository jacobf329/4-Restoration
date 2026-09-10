#!/usr/bin/env python3
"""
Generate character art through the OpenAI image API.

    python3 tools/portraits.py john_smith            both images for one character
    python3 tools/portraits.py john_smith tpose      just the one
    python3 tools/portraits.py --all                 everything in the prompt file

Prompts live in assets/openai-characters.json, one entry per image, so a reroll is
an edit to a text file rather than a change to this script.

Output:
    assets/characters/<name>_tpose.png      the reference Meshy turns into a model
    assets/portraits/<name>_portrait.png    the face the dialogue box draws

WHY BOTH ARE GENERATED FROM ONE FILE
------------------------------------
The two images have to be the same person, and the only thing holding them
together is that their prompts share a description written once. Keeping them
adjacent in one file is what makes that easy to check by eye.

WHY THE T-POSE IS SO PRESCRIPTIVE
---------------------------------
Everything downstream depends on it. Meshy builds the mesh from this image, and
the plan is to rig new characters by transferring skin weights from an already
rigged model - which works by matching vertices between two meshes standing in
the same pose. Arms not level, or a hip cocked, or the camera below chest
height, and the transfer degrades into per-character hand work. The pose
paragraph is not styling, it is a contract with the next tool along.

THE THIRTY SECOND CEILING
-------------------------
The agent proxy cuts a relay off at about thirty seconds and returns 502
"upstream request failed". That is not OpenAI: measured here, quality=high at
1024x1536 takes longer than that and fails every time, while the identical
request at 1024x1024 returned 200 in exactly thirty seconds, and medium at
1024x1536 returns in fifteen. So the setting that matters is not the one that
looks like it should be - the prompt length is irrelevant, and a 2MB response
comes back fine.

Each entry therefore carries a quality it can actually finish in, and a 502 is
retried once a step lower rather than reported as a failure. A T-pose is
reference for a mesh generator and wants shape rather than skin texture, so
medium costs it nothing; a portrait is a face somebody looks at, so it asks for
high and falls back only if it has to.

CREDENTIALS
-----------
No key is read or stored here. The environment's API credential is attached to
the request by the agent proxy, exactly as tools/meshy.py relies on. If this
401s, the key attached to the environment is not valid - see the message the
script prints, which quotes what OpenAI actually said.
"""

import base64
import json
import os
import pathlib
import sys
import urllib.error
import urllib.request

ROOT = pathlib.Path(__file__).resolve().parent.parent
PROMPTS = ROOT / "assets" / "openai-characters.json"
ENDPOINT = "https://api.openai.com/v1/images/generations"
MODEL = "gpt-image-1"

# Where each kind of image belongs, and what it is for.
FOLDERS = {
    "tpose": ROOT / "assets" / "characters",
    "portrait": ROOT / "assets" / "portraits",
}


def entries():
    if not PROMPTS.exists():
        sys.exit(f"no prompt file at {PROMPTS}")
    return json.loads(PROMPTS.read_text())


# What each kind asks for, and what it settles for when the relay runs out of
# patience. See THE THIRTY SECOND CEILING above.
DEFAULT_QUALITY = {"tpose": "medium", "portrait": "high"}
FALLBACK = {"high": "medium", "medium": "low"}


def generate(entry, quality=None):
    name, kind = entry["name"], entry["kind"]

    if kind not in FOLDERS:
        sys.exit(f"{name}: unknown kind {kind!r} - expected one of {sorted(FOLDERS)}")

    quality = quality or entry.get("quality") or DEFAULT_QUALITY.get(kind, "medium")

    body = json.dumps({
        "model": MODEL,
        "prompt": entry["prompt"],
        "size": entry.get("size", "1024x1024"),
        "quality": quality,
        "n": 1,

        # Transparent so the T-pose hands Meshy a clean silhouette with no background
        # to mistake for geometry, and so a portrait composites onto the dialogue
        # panel rather than sitting on a rectangle of its own.
        "background": "transparent",
        "output_format": "png",
    }).encode()

    req = urllib.request.Request(ENDPOINT, data=body,
                                 headers={"Content-Type": "application/json"})

    print(f"{name} {kind} ({quality}, {entry.get('size', '1024x1024')}): "
          f"{entry['prompt'][:52]}...")

    try:
        with urllib.request.urlopen(req, timeout=600) as r:
            payload = json.loads(r.read())
    except urllib.error.HTTPError as e:
        detail = e.read().decode(errors="replace")
        print(f"  HTTP {e.code}")
        print(f"  {detail.strip()[:600]}")

        if e.code == 401:
            print("\n  The request reached OpenAI and the key attached to it was refused.")
            print("  Nothing here can fix that: replace the OpenAI credential on the")
            print("  environment at claude.ai/code and run this again.")
            return False

        # 502 from the proxy means the relay was cut off, not that the request was
        # wrong. Ask for less and it comes back inside the window.
        if e.code == 502 and quality in FALLBACK:
            print(f"  relay timed out; retrying at {FALLBACK[quality]}")
            return generate(entry, FALLBACK[quality])

        return False

    # gpt-image-1 always returns base64 rather than a URL.
    data = payload["data"][0]
    if "b64_json" not in data:
        print(f"  unexpected response shape: {sorted(data)}")
        return False

    folder = FOLDERS[kind]
    folder.mkdir(parents=True, exist_ok=True)
    out = folder / f"{name}_{kind}.png"
    out.write_bytes(base64.b64decode(data["b64_json"]))

    print(f"  wrote {out.relative_to(ROOT)} ({out.stat().st_size // 1024} KB)")
    return True


def main():
    args = sys.argv[1:]
    if not args:
        sys.exit(__doc__)

    all_entries = entries()

    if args[0] == "--all":
        wanted = all_entries
    else:
        name = args[0]
        kinds = args[1:] or ["tpose", "portrait"]
        wanted = [e for e in all_entries if e["name"] == name and e["kind"] in kinds]

        if not wanted:
            known = sorted({e["name"] for e in all_entries})
            sys.exit(f"nothing in the prompt file for {name!r} - have: {', '.join(known)}")

    ok = sum(generate(e) for e in wanted)
    print(f"\n{ok} of {len(wanted)} generated")
    return 0 if ok == len(wanted) else 1


if __name__ == "__main__":
    sys.exit(main())
