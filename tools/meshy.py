#!/usr/bin/env python3
"""
Generate one model from assets/meshy-*.json through the Meshy text-to-3D API.

    MESHY_API_KEY=... python3 tools/meshy.py needler

Writes assets/weapons/<name>.glb (or assets/characters/, by which file the entry
came from) and checks it against the budget the README sets for imported models.

WHY THIS EXISTS
---------------
The models in this project were generated through a Meshy skill in an earlier
session. That skill is not available in every session, and the sandbox Claude
Code runs in blocks api.meshy.ai outright, so there is no way to do it from
inside one. This is the same job as a script you can run anywhere with a key.

A WARNING ABOUT THE ENDPOINTS BELOW
-----------------------------------
They were written from memory and could NOT be checked against the live docs -
docs.meshy.ai is blocked from the environment this was written in. The request
and response shapes are the v2 text-to-3D flow as of writing. If Meshy has moved
on, the constants at the top are the only thing that should need changing, and
every failure prints the raw response so you can see what it actually wanted.
Treat the first run as a test, not as a batch.
"""

import json
import os
import pathlib
import sys
import time
import urllib.error
import urllib.request

API = os.environ.get("MESHY_API", "https://api.meshy.ai/v2/text-to-3d")
KEY = os.environ.get("MESHY_API_KEY", "")

ROOT = pathlib.Path(__file__).resolve().parent.parent

# From the README's imported-model budget. Checked after download rather than
# trusted, because the brief that came with the first batch warned against
# importing Meshy output blindly and it was right to.
MAX_TRIANGLES = 7000
MAX_MATERIALS = 2


def auth_headers():
    """
    The Authorization header, or nothing when something else is supplying it.

    Two ways this script gets authenticated, and it must not do both. Run locally you
    hold the key and it goes in the header here. Run inside a Claude Code cloud session
    with Meshy added as an environment API credential, the agent proxy attaches the header
    after the request leaves the VM and the key is never visible in here at all - so
    sending our own would be a second Authorization on the same request.

    No key and no proxy is a 401 from Meshy, which is a clearer error than anything this
    script could invent by guessing which case it is in.
    """
    return {"Authorization": f"Bearer {KEY}"} if KEY else {}


def post(path, body):
    req = urllib.request.Request(
        path,
        data=json.dumps(body).encode(),
        headers={**auth_headers(), "Content-Type": "application/json"},
        method="POST",
    )
    return call(req)


def get(path):
    req = urllib.request.Request(path, headers=auth_headers())
    return call(req)


def call(req):
    try:
        with urllib.request.urlopen(req, timeout=60) as r:
            return json.loads(r.read())
    except urllib.error.HTTPError as e:
        body = e.read().decode(errors="replace")
        sys.exit(f"\nMeshy said {e.code}:\n{body}\n\n"
                 f"If this is a 404 the API has moved since this script was written - "
                 f"set MESHY_API to the current endpoint.")
    except urllib.error.URLError as e:
        # Caught separately because it is the failure you will actually hit first, and a
        # bare traceback about a socket says nothing about what to do.
        sys.exit(f"\nCould not reach {API}: {e.reason}\n\n"
                 f"In a Claude Code cloud session this means the host is not reachable from "
                 f"the environment. Adding Meshy as an environment API credential at "
                 f"claude.ai/code fixes it - listing a host there grants access to it "
                 f"whatever the network access level is. Otherwise run this from a machine "
                 f"with open outbound network.")


def wait(task_id, label):
    """Poll one task to completion, printing progress so a five-minute wait is visible."""
    last = -1
    while True:
        task = get(f"{API}/{task_id}")
        status = task.get("status", "?")
        pct = task.get("progress", 0)

        if pct != last:
            print(f"  {label}: {status} {pct}%", flush=True)
            last = pct

        if status == "SUCCEEDED":
            return task
        if status in ("FAILED", "CANCELED", "EXPIRED"):
            sys.exit(f"  {label} {status}: {json.dumps(task.get('task_error', {}))}")

        time.sleep(5)


def entry_for(name):
    """Find the prompt, and the folder its kind of asset belongs in."""
    for stem, folder in (("weapons", "weapons"), ("characters", "characters")):
        path = ROOT / "assets" / f"meshy-{stem}.json"
        if not path.exists():
            continue

        for item in json.loads(path.read_text()):
            if item.get("name") == name:
                return item, ROOT / "assets" / folder

    sys.exit(f"No entry named {name!r} in assets/meshy-*.json. "
             f"Add one there first - the prompt is the input to this, not an argument.")


def check_budget(glb):
    """
    Parse the glTF header out of the .glb and count what is in it.

    Reading the file rather than trusting the exporter, which is how the first
    batch was checked and how the 2048px textures were caught.
    """
    data = glb.read_bytes()
    if data[:4] != b"glTF":
        print("  ! not a glTF container - skipping budget check")
        return

    length = int.from_bytes(data[12:16], "little")
    doc = json.loads(data[20:20 + length])

    tris = 0
    for mesh in doc.get("meshes", []):
        for prim in mesh.get("primitives", []):
            idx = prim.get("indices")
            if idx is not None:
                tris += doc["accessors"][idx]["count"] // 3

    materials = len(doc.get("materials", []))
    images = doc.get("images", [])

    print(f"  triangles {tris} (budget {MAX_TRIANGLES}), "
          f"materials {materials} (budget {MAX_MATERIALS}), images {len(images)}")

    if tris > MAX_TRIANGLES:
        print("  ! over the triangle budget - regenerate with a lower target_polycount")
    if materials > MAX_MATERIALS:
        print("  ! more materials than the loader expects")


def main():
    if not KEY:
        print("No MESHY_API_KEY set - assuming an environment API credential supplies it.\n"
              "  If this 401s, either export the key or add one at claude.ai/code.\n")

    if len(sys.argv) != 2:
        sys.exit(f"usage: MESHY_API_KEY=... python3 {sys.argv[0]} <name>")

    name = sys.argv[1]
    item, folder = entry_for(name)

    print(f"{name}: {item['prompt'][:80]}...")

    # Preview first, then refine. Two calls rather than one because refine takes
    # the preview's id - asking for a textured model in one shot is not the flow.
    preview = post(API, {
        "mode": "preview",
        "prompt": item["prompt"],
        "art_style": "realistic",
        "target_polycount": item.get("target_polycount", 9000),
        "should_remesh": True,
    })

    preview_id = preview.get("result") or preview.get("id")
    if not preview_id:
        sys.exit(f"No task id in the response:\n{json.dumps(preview, indent=2)}")

    wait(preview_id, "preview")

    refine = post(API, {
        "mode": "refine",
        "preview_task_id": preview_id,
        "texture_prompt": item.get("texture_prompt", ""),
    })

    refine_id = refine.get("result") or refine.get("id")
    task = wait(refine_id or preview_id, "refine")

    url = (task.get("model_urls") or {}).get("glb")
    if not url:
        sys.exit(f"No glb url in the finished task:\n{json.dumps(task, indent=2)}")

    folder.mkdir(parents=True, exist_ok=True)
    out = folder / f"{name}.glb"

    with urllib.request.urlopen(url, timeout=300) as r:
        out.write_bytes(r.read())

    print(f"  wrote {out.relative_to(ROOT)} ({out.stat().st_size // 1024} KB)")
    check_budget(out)

    print("\nDrop it in and relaunch - the loader picks it up with no code change, "
          "and the box silhouette stops being used the moment the file exists.")


if __name__ == "__main__":
    main()
