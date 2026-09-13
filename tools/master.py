#!/usr/bin/env python3
"""Loudness-master the generated music so every cue sits at the same level.

Why this exists: the effects were normalised when they were generated and the music
was not, and measuring the result showed why nobody could hear the soundtrack. The
cues came back from the generator between -11 and -60 dBFS, and the engine then put
all of them on the same -14 dB bed under effects normalised to -1 dBFS peak. Half the
roster was below the noise the game itself makes.

Peak normalising is the wrong tool for a bed. A track with one loud hit and two quiet
minutes has no peak headroom and is still inaudible, so the level measured here is the
90th percentile of 400 ms window RMS - "how loud is this most of the time it is doing
something" - and the peak is handled afterwards by a limiter rather than by refusing
to apply the gain.

Idempotent: the target is an absolute level, so mastering an already-mastered file
computes a gain of roughly zero. Run it as often as you like.

    python3 tools/master.py                 # report only
    python3 tools/master.py --write         # master in place
"""
import argparse, glob, os, sys
import numpy as np
import miniaudio
import lameenc

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
MUSIC_DIR = os.path.join(ROOT, "assets", "music")

# Where a cue should sit. Beds, not songs - a commercial master is nearer -9, and the
# game has to put twelve fighters and a tank on top of this.
TARGET_DB = -15.0

# The limiter never lets a sample past this, leaving a decibel for the MP3 encoder,
# which is not sample-accurate and can overshoot what it was given.
CEILING = 0.89          # -1.0 dBFS

# A cue needing more than this is not quiet, it is empty, and the fix is to generate it
# again rather than to amplify sixty decibels of nothing.
MAX_GAIN_DB = 26.0

BLOCK = 1024


def db(v):
    return 20.0 * np.log10(max(float(v), 1e-9))


def decode(path):
    d = miniaudio.decode_file(path, output_format=miniaudio.SampleFormat.SIGNED16)
    a = np.frombuffer(bytes(d.samples), dtype=np.int16).astype(np.float64) / 32768.0
    ch = d.nchannels
    return a.reshape(-1, ch) if ch > 1 else a.reshape(-1, 1), d.sample_rate


def loudness(a, rate):
    """90th percentile of 400 ms window RMS, over the channel sum."""
    m = a.mean(axis=1)
    w = int(rate * 0.4)
    n = len(m) // w
    if n < 1:
        return float(np.sqrt((m * m).mean()))
    win = m[:n * w].reshape(n, w)
    return float(np.percentile(np.sqrt((win * win).mean(axis=1)), 90))


def limit(a):
    """Block limiter with an interpolated gain curve.

    A hard clip on a bed is audible as grit on every swell; a tanh fold is audible as
    the whole track going dull. Riding the gain down over a block and back up again is
    neither, because the reduction is smooth and the ear reads it as the mix breathing.
    """
    n = len(a)
    pad = (-n) % BLOCK
    m = np.abs(a).max(axis=1)
    if pad:
        m = np.concatenate([m, np.zeros(pad)])
    blocks = m.reshape(-1, BLOCK).max(axis=1)

    # Look one block either side so the gain is already down when the peak arrives.
    wide = np.maximum.reduce([
        blocks,
        np.roll(blocks, 1), np.roll(blocks, -1),
        np.roll(blocks, 2), np.roll(blocks, -2)])
    g = np.minimum(1.0, CEILING / np.maximum(wide, 1e-9))

    # One gain value per block edge, straight-line between them.
    edges = np.concatenate([g, g[-1:]])
    ramp = np.interp(np.arange(len(blocks) * BLOCK),
                     np.arange(len(edges)) * BLOCK, edges)[:n]
    return a * ramp[:, None]


def encode(a, rate, channels, kbps=128):
    enc = lameenc.Encoder()
    enc.set_bit_rate(kbps)
    enc.set_in_sample_rate(rate)
    enc.set_channels(channels)
    enc.set_quality(2)
    pcm = np.clip(a * 32767.0, -32768, 32767).astype("<i2").tobytes()
    return bytes(enc.encode(pcm)) + bytes(enc.flush())


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--write", action="store_true", help="master in place")
    ap.add_argument("--only", default="", help="substring of the file name")
    args = ap.parse_args()

    empty = []
    print(f"{'file':34s} {'was':>7s} {'gain':>7s} {'now':>7s} {'peak':>7s}")
    for path in sorted(glob.glob(os.path.join(MUSIC_DIR, "*.mp3"))):
        name = os.path.basename(path)
        if args.only and args.only not in name:
            continue

        a, rate = decode(path)
        was = loudness(a, rate)
        gain_db = TARGET_DB - db(was)

        if gain_db > MAX_GAIN_DB:
            empty.append((name, db(was)))
            print(f"{name:34s} {db(was):7.1f} {'EMPTY':>7s}")
            continue

        out = limit(a * (10.0 ** (gain_db / 20.0)))
        now, peak = loudness(out, rate), np.abs(out).max()
        print(f"{name:34s} {db(was):7.1f} {gain_db:+7.1f} {db(now):7.1f} {db(peak):7.1f}")

        if args.write:
            with open(path, "wb") as f:
                f.write(encode(out, rate, a.shape[1]))

    if empty:
        print("\nToo quiet to rescue - generate these again:")
        for name, level in empty:
            print(f"  {name}  ({level:.0f} dBFS)")
    return 1 if empty else 0


if __name__ == "__main__":
    sys.exit(main())
