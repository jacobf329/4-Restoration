#!/usr/bin/env python3
"""Look at a sound you cannot hear.

The counterpart to tools/look.py. A generated effect can be the right length, the
right format and completely wrong - silence, a clipped mess, or two seconds of room
tone with the event buried at the end - and every one of those passes a file-size
check. This prints the envelope, so the shape is visible: a gunshot is a spike with a
decay, a loop is flat across, and silence is obvious at a glance.

    python3 tools/listen.py assets/sfx/*.wav
"""
import struct, sys, wave
import numpy as np

BARS = " .:-=+*#%@"


def read(path):
    with wave.open(path, "rb") as w:
        n, ch, rate = w.getnframes(), w.getnchannels(), w.getframerate()
        raw = w.readframes(n)
    a = np.frombuffer(raw, dtype="<i2").astype(np.float64) / 32768.0
    if ch > 1:
        a = a.reshape(-1, ch).mean(axis=1)
    return a, rate


def envelope(a, cells=44):
    if len(a) < cells:
        return " " * cells
    # Peak per cell rather than mean: a transient is one sample wide and averaging
    # hides exactly the thing worth seeing.
    cut = np.array_split(np.abs(a), cells)
    peaks = np.array([c.max() if len(c) else 0.0 for c in cut])
    idx = np.clip((peaks * (len(BARS) - 1) * 1.6).astype(int), 0, len(BARS) - 1)
    return "".join(BARS[i] for i in idx)


def main(paths):
    print(f"{'file':<28}{'secs':>6}{'peak':>7}{'rms':>7}  {'envelope':<44} note")
    for p in paths:
        try:
            a, rate = read(p)
        except Exception as e:
            print(f"{p.split('/')[-1]:<28}  unreadable: {e}")
            continue

        peak = float(np.abs(a).max()) if len(a) else 0.0
        rms = float(np.sqrt((a ** 2).mean())) if len(a) else 0.0

        notes = []
        if peak < 0.02:
            notes.append("SILENT")
        elif peak > 0.999:
            notes.append("CLIPPED")
        if rms > 0 and peak / max(rms, 1e-9) < 3:
            notes.append("flat/sustained")
        # Dead air at the head is the common failure: the event should start at once.
        if len(a):
            lead = np.argmax(np.abs(a) > peak * 0.25) / rate if peak > 0 else 0
            if lead > 0.25:
                notes.append(f"starts {lead:.2f}s in")

        print(f"{p.split('/')[-1]:<28}{len(a)/rate:>6.2f}{peak:>7.2f}{rms:>7.3f}  "
              f"{envelope(a):<44} {', '.join(notes)}")


if __name__ == "__main__":
    main(sys.argv[1:])
