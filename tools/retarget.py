#!/usr/bin/env python3
"""Copy a walk/run cycle from one character onto another, offline.

Every body in this project is rigged to the same Meshy humanoid: 24 joints,
identical names, identical hierarchy.  That means a clip authored for one
character is a clip for all of them, and a new character needs a rig but not
new animation - which is the expensive half.

Rotations transfer verbatim; the target keeps its own bone translations, so
its proportions survive.  Hips translation is the one exception, carried
across scaled by the hip-height ratio so the bob and drift match the body.

    python3 tools/retarget.py --target john_rigged.glb \\
        --source assets/characters/sinew_walk.glb --out assets/characters/john_smith_walk.glb
"""
import argparse, json, struct, sys
import numpy as np

sys.path.insert(0, __file__.rsplit('/', 1)[0])
from look import read_glb, accessor

HIPS = 'Hips'


def pad4(b):
    return b + b'\x00' * (-len(b) % 4)


class Builder:
    """Appends accessors to a GLB's single binary buffer."""

    def __init__(self, js, bin_):
        self.js = js
        self.bin = bytearray(pad4(bin_))
        js.setdefault('bufferViews', [])
        js.setdefault('accessors', [])

    def add(self, array, kind):
        data = np.ascontiguousarray(array, dtype='<f4').tobytes()
        off = len(self.bin)
        self.bin += pad4(data)
        self.js['bufferViews'].append(
            {'buffer': 0, 'byteOffset': off, 'byteLength': len(data)})
        acc = {'bufferView': len(self.js['bufferViews']) - 1,
               'componentType': 5126, 'count': len(array), 'type': kind}
        if kind == 'SCALAR':                  # required on animation inputs
            acc['min'] = [float(np.min(array))]
            acc['max'] = [float(np.max(array))]
        self.js['accessors'].append(acc)
        return len(self.js['accessors']) - 1


def joints_by_name(js):
    nodes = js.get('nodes', [])
    if not js.get('skins'):
        raise SystemExit('the target has no skin - rig it before retargeting')
    return {nodes[j].get('name'): j for j in js['skins'][0]['joints']}


def rest_hips_height(js):
    nodes = js['nodes']
    for j in js['skins'][0]['joints']:
        if nodes[j].get('name') == HIPS:
            return float(nodes[j].get('translation', [0, 1, 0])[1])
    return 1.0


def retarget(target_path, source_path, out_path, clip_name=None):
    tjs, tbin = read_glb(target_path)
    sjs, sbin = read_glb(source_path)

    if not sjs.get('animations'):
        raise SystemExit('%s carries no animation' % source_path)
    anim = sjs['animations'][0] if clip_name is None else next(
        a for a in sjs['animations'] if a.get('name') == clip_name)

    tj = joints_by_name(tjs)
    snodes = sjs['nodes']
    scale = rest_hips_height(tjs) / max(rest_hips_height(sjs), 1e-9)

    b = Builder(tjs, tbin)
    channels, samplers = [], []
    matched, skipped = [], set()

    for ch in anim['channels']:
        path = ch['target']['path']
        name = snodes[ch['target']['node']].get('name')
        if name not in tj:
            skipped.add(name)
            continue
        # Translation would import the source body's bone lengths and squash
        # the target into its proportions.  Only the root carries any, scaled.
        if path == 'scale' or (path == 'translation' and name != HIPS):
            continue

        sm = anim['samplers'][ch['sampler']]
        times = accessor(sjs, sbin, sm['input']).reshape(-1)
        vals = accessor(sjs, sbin, sm['output'])
        if sm.get('interpolation') == 'CUBICSPLINE':
            vals = vals[1::3]
        if path == 'translation':
            vals = vals * scale

        samplers.append({'input': b.add(times, 'SCALAR'),
                         'output': b.add(vals, 'VEC4' if path == 'rotation' else 'VEC3'),
                         'interpolation': 'LINEAR'})
        channels.append({'sampler': len(samplers) - 1,
                         'target': {'node': tj[name], 'path': path}})
        matched.append(name)

    tjs['animations'] = [{'name': anim.get('name', 'clip'),
                          'channels': channels, 'samplers': samplers}]
    tjs['buffers'] = [{'byteLength': len(b.bin)}]

    # The JSON chunk pads with spaces, not nulls - a null lands inside the
    # document and every reader rejects it as trailing garbage.
    js_chunk = json.dumps(tjs, separators=(',', ':')).encode()
    js_chunk += b' ' * (-len(js_chunk) % 4)
    bin_chunk = bytes(b.bin)
    total = 12 + 8 + len(js_chunk) + 8 + len(bin_chunk)
    with open(out_path, 'wb') as f:
        f.write(b'glTF' + struct.pack('<II', 2, total))
        f.write(struct.pack('<I', len(js_chunk)) + b'JSON' + js_chunk)
        f.write(struct.pack('<I', len(bin_chunk)) + b'BIN\x00' + bin_chunk)

    print('%s + %s -> %s' % (target_path.split('/')[-1], source_path.split('/')[-1], out_path))
    print('  %d joints matched, hips scaled x%.3f, %d channels'
          % (len(set(matched)), scale, len(channels)))
    if skipped:
        print('  ! not on the target rig: %s' % ', '.join(sorted(skipped)))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--target', required=True, help='rigged .glb to receive the clip')
    ap.add_argument('--source', required=True, help='.glb carrying the animation')
    ap.add_argument('--out', required=True)
    ap.add_argument('--clip', default=None)
    a = ap.parse_args()
    retarget(a.target, a.source, a.out, a.clip)


if __name__ == '__main__':
    main()
