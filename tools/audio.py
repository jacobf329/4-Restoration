#!/usr/bin/env python3
"""Generate the game's audio from assets/audio-manifest.json via ElevenLabs.

Two endpoints, two shapes of output:

    sound effects   /v1/sound-generation, asked for raw PCM at 22050 Hz, which is
                    exactly the rate Sfx.cs already runs at. The WAV header is written
                    here, so no ffmpeg is needed anywhere in the pipeline.
    music           /v1/music, MP3 at 44.1k. Godot reads MP3 natively, and ElevenLabs
                    does not offer OGG, so MP3 is what the delivery spec says now.

Resumable by design: a file that already exists is skipped, so a run that dies part
way through costs nothing to repeat, and regenerating one cue you dislike is

    python3 tools/audio.py --only w_railgun --force

Credentials come from the environment API credential configured at claude.ai/code,
which the proxy attaches per request. Nothing here ever sees the key.
"""
import argparse, json, os, struct, sys, time, urllib.error, urllib.request
import numpy as np

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
MANIFEST = os.path.join(ROOT, "assets", "audio-manifest.json")
SFX_DIR = os.path.join(ROOT, "assets", "sfx")
MUSIC_DIR = os.path.join(ROOT, "assets", "music")

SFX_URL = "https://api.elevenlabs.io/v1/sound-generation?output_format=pcm_22050"
MUSIC_URL = "https://api.elevenlabs.io/v1/music?output_format=mp3_44100_128"

SFX_RATE = 22050


def post(url, payload, timeout):
    req = urllib.request.Request(
        url, data=json.dumps(payload).encode(), method="POST",
        headers={"Content-Type": "application/json"})

    # Only used when running outside the managed environment; normally unset.
    key = os.environ.get("ELEVENLABS_API_KEY")
    if key:
        req.add_header("xi-api-key", key)

    with urllib.request.urlopen(req, timeout=timeout) as r:
        return r.read()


# Sounds that must begin at the instant they are triggered. A swing may build into its
# peak and a portal may swell open, but a gunshot, a footstep, an impact or a UI click
# that starts a third of a second in reads as input lag, not as a sound.
TRANSIENT = ("w_", "f_", "i_", "ui_", "m_blade_hit", "m_punch", "x_", "st_hurt")


def polish(pcm, loop, transient=False):
    """Trim, normalise and de-click a generated effect.

    Three faults come back from the generator often enough to fix here rather than by
    hand, and all three were visible the first time the batch was inspected with
    tools/listen.py:

      dead air at the head   a UI click that starts three quarters of a second in is
                             not a click. The model pads, and the pad has to go.
      clipping               two of the first six came back at full scale. Normalising
                             down to -1 dBFS is the only way the mix has any headroom.
      overlong tails         the minimum billable duration is about a second, so a 0.4s
                             impact arrives with half a second of room tone after it.

    A loop is trimmed at neither end - cutting a sustained bed at a threshold puts a
    step in exactly the place the join has to be seamless.
    """
    a = np.frombuffer(pcm, dtype="<i2").astype(np.float64) / 32768.0
    if len(a) == 0:
        return pcm

    peak = np.abs(a).max()
    if peak < 1e-4:
        return pcm

    if not loop:
        # A transient is trimmed to a fifth of its own peak, which cannot eat the attack
        # because the attack IS the peak. Everything else keeps its quiet approach.
        loud = np.abs(a) > peak * (0.20 if transient else 0.02)
        if loud.any():
            first, last = np.argmax(loud), len(a) - np.argmax(loud[::-1])
            # A little room either side: cutting hard on the threshold clips the attack
            # transient off the front, which is the part that carries the impact.
            a = a[max(0, first - int(SFX_RATE * 0.005)):
                  min(len(a), last + int(SFX_RATE * 0.05))]

    a *= 0.89 / max(np.abs(a).max(), 1e-9)      # -1 dBFS

    # 1 ms ramps so a trimmed edge is not itself a click.
    r = max(1, int(SFX_RATE * 0.001))
    if len(a) > 2 * r:
        a[:r] *= np.linspace(0.0, 1.0, r)
        a[-r:] *= np.linspace(1.0, 0.0, r)

    return np.clip(a * 32767.0, -32768, 32767).astype("<i2").tobytes()


def wav(pcm, rate=SFX_RATE, channels=1, bits=16):
    """Wrap raw little-endian PCM in a canonical 44-byte WAV header."""
    block = channels * bits // 8
    return (b"RIFF" + struct.pack("<I", 36 + len(pcm)) + b"WAVEfmt "
            + struct.pack("<IHHIIHH", 16, 1, channels, rate, rate * block, block, bits)
            + b"data" + struct.pack("<I", len(pcm)) + pcm)


def attempt(fn, what, tries=3):
    for i in range(tries):
        try:
            return fn()
        except urllib.error.HTTPError as e:
            body = e.read()[:300].decode("utf-8", "replace")
            # A rejected request will be rejected again; only retry server-side faults.
            if e.code < 500 and e.code != 429:
                print(f"  ! {what}: HTTP {e.code} {body}")
                return None
            print(f"  . {what}: HTTP {e.code}, retrying")
        except Exception as e:
            print(f"  . {what}: {type(e).__name__}, retrying")
        time.sleep(2 * (i + 1))
    print(f"  ! {what}: gave up")
    return None


def make_sfx(item, force):
    made = 0
    for n in range(1, item["variants"] + 1):
        # A single-variant effect keeps a bare name; variants are numbered from 01.
        stem = item["key"] if item["variants"] == 1 else f"{item['key']}_{n:02d}"
        out = os.path.join(SFX_DIR, stem + ".wav")
        if os.path.exists(out) and not force:
            continue

        pcm = attempt(lambda: post(SFX_URL, {
            "text": item["prompt"],
            "duration_seconds": float(item["duration_s"]),
            # Higher than the default: these are described precisely and the descriptions
            # are the point. Left lower, the model invents its own idea of the sound.
            "prompt_influence": 0.65,
        }, 120), stem)

        if pcm is None:
            continue

        pcm = polish(pcm, item.get("loop", False),
                     transient=item["key"].startswith(TRANSIENT))

        with open(out, "wb") as f:
            f.write(wav(pcm))
        print(f"  {stem}.wav  {len(pcm) / 2 / SFX_RATE:.2f}s  {len(pcm) // 1024} KB")
        made += 1
    return made


def make_music(item, force):
    out = os.path.join(MUSIC_DIR, item["key"] + ".mp3")
    if os.path.exists(out) and not force:
        return 0

    # Loops are asked for explicitly in the prompt rather than trimmed afterwards: the
    # model will not hand back a sample-accurate loop either way, and a cue written to
    # end where it began joins far better than one faded at the seam.
    prompt = item["prompt"]
    if item.get("loop"):
        prompt += " Seamless loop, no fade in and no fade out, ending where it began."
    else:
        prompt += " No fade in."

    data = attempt(lambda: post(MUSIC_URL, {
        "prompt": prompt,
        "music_length_ms": int(item["length_ms"]),
    }, 420), item["key"])

    if data is None:
        return 0

    with open(out, "wb") as f:
        f.write(data)
    print(f"  {item['key']}.mp3  {item['length_ms'] / 1000:.0f}s  {len(data) // 1024} KB")
    return 1


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--only", default="", help="generate keys starting with this prefix")
    ap.add_argument("--kind", choices=["sfx", "music", "ambience", "all"], default="all")
    ap.add_argument("--force", action="store_true", help="regenerate files that exist")
    ap.add_argument("--limit", type=int, default=0, help="stop after this many files")
    ap.add_argument("--list", action="store_true", help="print what would be made")
    a = ap.parse_args()

    man = json.load(open(MANIFEST))
    os.makedirs(SFX_DIR, exist_ok=True)
    os.makedirs(MUSIC_DIR, exist_ok=True)

    groups = []
    if a.kind in ("music", "all"):
        groups.append(("music", man["music"], make_music))
    if a.kind in ("ambience", "all"):
        groups.append(("ambience", man["ambience"], make_music))
    if a.kind in ("sfx", "all"):
        groups.append(("sfx", man["sfx"], make_sfx))

    total = 0
    for name, items, fn in groups:
        want = [i for i in items if i["key"].startswith(a.only)]
        if not want:
            continue
        print(f"\n== {name} ({len(want)}) ==")

        for item in want:
            if a.list:
                print(f"  {item['key']}")
                continue
            total += fn(item, a.force)
            if a.limit and total >= a.limit:
                print(f"\nstopped at --limit {a.limit}")
                return

    print(f"\n{total} file(s) written")


if __name__ == "__main__":
    main()
