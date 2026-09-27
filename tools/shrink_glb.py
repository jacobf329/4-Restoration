#!/usr/bin/env python3
"""Re-encode the textures inside a .glb, in place, at a sane size for scenery.

Meshy ships 2048px PBR maps. Eighteen props of those is two hundred megabytes in
a repository people have to clone, for surfaces read at twenty metres through
four splitscreen viewports. PropModels already downsizes them when it loads
them, which fixes the memory and does nothing at all for the disk.

Binary glTF surgery rather than a library, the same way tools/retarget.py and
tools/look.py do it: a .glb is a header, a JSON chunk and a BIN chunk, and every
bufferView is an offset into the BIN. Replacing an image means rebuilding the
whole BIN and remapping every offset in it - which is why this rewrites all of
them rather than trying to patch one in place.

    python3 tools/shrink_glb.py assets/props/*.glb
    python3 tools/shrink_glb.py --size 256 assets/props/snow_drift.glb
"""
import argparse, io, json, os, struct, sys
from PIL import Image

DEFAULT_SIZE = 512
QUALITY = 86


def read(path):
    with open(path, "rb") as f:
        blob = f.read()

    magic, version, _ = struct.unpack("<III", blob[:12])
    if magic != 0x46546C67:
        raise SystemExit(f"{path}: not a glb")

    at, js, bin_ = 12, None, b""
    while at < len(blob):
        length, kind = struct.unpack("<II", blob[at:at + 8])
        body = blob[at + 8:at + 8 + length]
        if kind == 0x4E4F534A:
            js = json.loads(body)
        elif kind == 0x004E4942:
            bin_ = body
        at += 8 + length + (-length % 4)

    if js is None:
        raise SystemExit(f"{path}: no JSON chunk")
    return js, bin_


def write(path, js, bin_):
    text = json.dumps(js, separators=(",", ":")).encode()
    text += b" " * (-len(text) % 4)
    body = bin_ + b"\0" * (-len(bin_) % 4)

    out = struct.pack("<III", 0x46546C67, 2, 12 + 8 + len(text) + 8 + len(body))
    out += struct.pack("<II", len(text), 0x4E4F534A) + text
    out += struct.pack("<II", len(body), 0x004E4942) + body

    with open(path, "wb") as f:
        f.write(out)


def shrink(path, size):
    js, bin_ = read(path)
    views = js.get("bufferViews", [])
    if not views:
        return 0, 0

    before = os.path.getsize(path)

    # Which bufferViews hold an image, and what each image should become.
    replaced = {}
    for image in js.get("images", []):
        view = image.get("bufferView")
        if view is None:
            continue

        v = views[view]
        raw = bin_[v.get("byteOffset", 0):v.get("byteOffset", 0) + v["byteLength"]]

        try:
            img = Image.open(io.BytesIO(raw))
            img.load()
        except Exception:
            continue

        if max(img.size) <= size:
            continue

        k = size / max(img.size)
        img = img.convert("RGB").resize(
            (max(1, int(img.width * k)), max(1, int(img.height * k))), Image.LANCZOS)

        buf = io.BytesIO()
        img.save(buf, "JPEG", quality=QUALITY, optimize=True)

        replaced[view] = buf.getvalue()
        image["mimeType"] = "image/jpeg"

    if not replaced:
        return before, before

    # Rebuild the whole BIN: every view is copied or replaced, in order, and every
    # offset is rewritten. Patching one in place would shift everything after it.
    packed = bytearray()
    for i, v in enumerate(views):
        start = v.get("byteOffset", 0)
        chunk = replaced.get(i, bin_[start:start + v["byteLength"]])

        packed += b"\0" * (-len(packed) % 4)
        v["byteOffset"] = len(packed)
        v["byteLength"] = len(chunk)
        packed += chunk

    js["buffers"] = [{"byteLength": len(packed)}]
    write(path, js, bytes(packed))

    return before, os.path.getsize(path)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("files", nargs="+")
    ap.add_argument("--size", type=int, default=DEFAULT_SIZE)
    a = ap.parse_args()

    was = now = 0
    for path in a.files:
        b, n = shrink(path, a.size)
        was += b
        now += n
        if b != n:
            print(f"  {os.path.basename(path):24s} {b // 1024:6d} -> {n // 1024:5d} KB")

    if was:
        print(f"\n{was // 1024 // 1024} MB -> {now // 1024 // 1024} MB "
              f"({100 - now * 100 // max(was, 1)}% saved)")


if __name__ == "__main__":
    main()
