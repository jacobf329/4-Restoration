#!/usr/bin/env python3
"""Render a .glb to a PNG so it can actually be looked at.

Point splats are unreadable - a car and a motorcycle both come out as two
blobs.  This is a real z-buffer rasteriser with flat shading, orthographic,
which is enough to read a silhouette and the form under it.

    python3 tools/look.py assets/characters/john_smith.glb -o out.png

Renders four azimuths (front, three-quarter, side, back) side by side.
Node transforms are applied, so a model that arrives lying down looks like
it is lying down rather than being silently stood up.
"""
import json, math, os, struct, sys, zlib
import numpy as np

# ---------------------------------------------------------------- glTF

COMPONENT = {5120: 'i1', 5121: 'u1', 5122: 'i2', 5123: 'u2', 5125: 'u4', 5126: 'f4'}
COUNT = {'SCALAR': 1, 'VEC2': 2, 'VEC3': 3, 'VEC4': 4, 'MAT4': 16}


def read_glb(path):
    with open(path, 'rb') as f:
        data = f.read()
    if data[:4] != b'glTF':
        raise SystemExit('%s is not a binary glTF' % path)
    n = struct.unpack_from('<I', data, 12)[0]
    js = json.loads(data[20:20 + n].decode('utf-8'))
    off = 20 + n
    blen = struct.unpack_from('<I', data, off)[0]
    return js, data[off + 8:off + 8 + blen]


def accessor(js, bin_, index):
    a = js['accessors'][index]
    dt = np.dtype(COMPONENT[a['componentType']]).newbyteorder('<')
    n = COUNT[a['type']]
    bv = js['bufferViews'][a['bufferView']]
    base = bv.get('byteOffset', 0) + a.get('byteOffset', 0)
    stride = bv.get('byteStride') or dt.itemsize * n
    if stride == dt.itemsize * n:
        out = np.frombuffer(bin_, dtype=dt, count=a['count'] * n, offset=base)
        return out.reshape(a['count'], n)
    raw = np.frombuffer(bin_, dtype=np.uint8, count=a['count'] * stride, offset=base)
    raw = raw.reshape(a['count'], stride)[:, :dt.itemsize * n]
    return np.ascontiguousarray(raw).view(dt).reshape(a['count'], n)


def node_matrix(node):
    if 'matrix' in node:                      # glTF matrices are column-major
        return np.array(node['matrix'], dtype=np.float64).reshape(4, 4).T
    m = np.eye(4)
    if 'rotation' in node:
        m[:3, :3] = quat_matrix(node['rotation'])
    if 'scale' in node:
        m[:3, :3] = m[:3, :3] @ np.diag(node['scale'])
    if 'translation' in node:
        m[:3, 3] = node['translation']
    return m


def quat_matrix(q):
    x, y, z, w = q
    return np.array([
        [1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)],
        [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
        [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)]])


def sample(js, bin_, anim, t):
    """Node index -> {path: value}, the animation evaluated at time t.

    Step and cubic splines are read as linear.  This renders a pose to look
    at, not to ship, and every clip in this project is densely baked anyway.
    """
    out = {}
    for ch in anim['channels']:
        sm = anim['samplers'][ch['sampler']]
        times = accessor(js, bin_, sm['input']).reshape(-1).astype(np.float64)
        vals = accessor(js, bin_, sm['output']).astype(np.float64)
        if sm.get('interpolation') == 'CUBICSPLINE':
            vals = vals[1::3]
        i = int(np.clip(np.searchsorted(times, t) - 1, 0, len(times) - 2))
        span = max(times[i + 1] - times[i], 1e-9)
        u = float(np.clip((t - times[i]) / span, 0, 1))
        a, b = vals[i], vals[i + 1]
        if ch['target']['path'] == 'rotation':
            if a @ b < 0:                     # shortest arc
                b = -b
            v = a + (b - a) * u
            v = v / max(np.linalg.norm(v), 1e-9)
        else:
            v = a + (b - a) * u
        out.setdefault(ch['target']['node'], {})[ch['target']['path']] = v
    return out


def globals_for(js, posed):
    """World matrix per node, with the sampled pose overriding the rest pose."""
    nodes = js.get('nodes', [])
    world = [None] * len(nodes)

    def walk(i, parent):
        node = dict(nodes[i])
        if i in posed:
            node.pop('matrix', None)
            node.update({k: list(v) for k, v in posed[i].items()})
        world[i] = parent @ node_matrix(node)
        for c in nodes[i].get('children', []):
            walk(c, world[i])

    roots = js['scenes'][js.get('scene', 0)]['nodes'] if js.get('scenes') else range(len(nodes))
    for r in roots:
        walk(r, np.eye(4))
    for i in range(len(nodes)):               # nodes outside the scene graph
        if world[i] is None:
            world[i] = np.eye(4)
    return world


def triangles(path, clip=None, time=0.0):
    """World-space (n,3,3) triangle soup.

    With a clip name (or 0 for the first), the skin is deformed by that
    animation at `time`, so a rig can be checked in a pose rather than only
    in its bind stance - which is the only way to see a bad retarget.
    """
    js, bin_ = read_glb(path)
    anims = js.get('animations', [])
    posed = {}
    if clip is not None and anims:
        chosen = anims[0] if clip in (0, '', True) else next(
            (a for a in anims if a.get('name') == clip), anims[0])
        posed = sample(js, bin_, chosen, time)

    world = globals_for(js, posed)
    out = []
    uvs = []

    for i, node in enumerate(js.get('nodes', [])):
        if 'mesh' not in node:
            continue
        skin = js['skins'][node['skin']] if 'skin' in node else None
        if skin is not None:
            ibm = accessor(js, bin_, skin['inverseBindMatrices']).astype(np.float64)
            ibm = ibm.reshape(-1, 4, 4).transpose(0, 2, 1)      # column-major
            jm = np.stack([world[j] @ ibm[k] for k, j in enumerate(skin['joints'])])

        for prim in js['meshes'][node['mesh']]['primitives']:
            if prim.get('mode', 4) != 4:
                continue
            attrs = prim['attributes']
            pos = accessor(js, bin_, attrs['POSITION']).astype(np.float64)

            if skin is not None and 'JOINTS_0' in attrs and 'WEIGHTS_0' in attrs:
                jo = accessor(js, bin_, attrs['JOINTS_0']).astype(np.int64)
                wt = accessor(js, bin_, attrs['WEIGHTS_0']).astype(np.float64)
                if wt.dtype != np.float64 or wt.max() > 1.5:    # normalised integers
                    wt = wt / wt.max()
                wt = wt / np.maximum(wt.sum(1, keepdims=True), 1e-9)
                h = np.concatenate([pos, np.ones((len(pos), 1))], axis=1)
                acc = np.zeros((len(pos), 3))
                for k in range(jo.shape[1]):
                    m = jm[jo[:, k]]                            # (n,4,4)
                    acc += np.einsum('nij,nj->ni', m[:, :3, :], h) * wt[:, k:k + 1]
                pos = acc
            else:
                w = world[i]
                pos = pos @ w[:3, :3].T + w[:3, 3]

            if 'indices' in prim:
                idx = accessor(js, bin_, prim['indices']).reshape(-1).astype(np.int64)
            else:
                idx = np.arange(len(pos))
            idx = idx[:len(idx) // 3 * 3]
            out.append(pos[idx].reshape(-1, 3, 3))

            if 'TEXCOORD_0' in attrs:
                uv = accessor(js, bin_, attrs['TEXCOORD_0']).astype(np.float64)
                uvs.append(uv[idx].reshape(-1, 3, 2))
            else:
                uvs.append(np.zeros((len(idx) // 3, 3, 2)))

    if not out:
        raise SystemExit('no triangles in %s' % path)
    return np.concatenate(out), js, np.concatenate(uvs)


def base_colour(path):
    """The material's base colour map as an (h,w,3) array, or None."""
    try:
        from PIL import Image
    except ImportError:
        return None
    import io
    js, bin_ = read_glb(path)
    mats = js.get('materials') or []
    tex = (mats[0].get('pbrMetallicRoughness', {}).get('baseColorTexture')
           if mats else None)
    if tex is None:
        return None
    img = js['images'][js['textures'][tex['index']]['source']]
    if 'bufferView' not in img:
        return None
    bv = js['bufferViews'][img['bufferView']]
    o = bv.get('byteOffset', 0)
    raw = bin_[o:o + bv['byteLength']]
    return np.asarray(Image.open(io.BytesIO(raw)).convert('RGB'), dtype=np.float64) / 255.0


# ---------------------------------------------------------------- raster

def view(tris, azimuth, elevation, w, h, uv=None, tex=None):
    """Flat-shaded orthographic render, +Y up, framed to fit."""
    a, e = math.radians(azimuth), math.radians(elevation)
    # camera basis: right, up, forward(towards viewer)
    fwd = np.array([math.cos(e) * math.sin(a), math.sin(e), math.cos(e) * math.cos(a)])
    right = np.array([math.cos(a), 0.0, -math.sin(a)])
    up = np.cross(fwd, right)
    basis = np.stack([right, up, fwd])                       # (3,3)

    v = tris.reshape(-1, 3) @ basis.T
    lo, hi = v.min(0), v.max(0)
    ext = np.maximum(hi - lo, 1e-6)
    scale = min((w - 16) / ext[0], (h - 16) / ext[1])
    cx, cy = (lo[0] + hi[0]) / 2, (lo[1] + hi[1]) / 2

    p = v.reshape(-1, 3, 3).copy()
    p[:, :, 0] = (p[:, :, 0] - cx) * scale + w / 2
    p[:, :, 1] = h / 2 - (p[:, :, 1] - cy) * scale           # +Y up on screen

    # Flat normals from the un-flipped camera-space triangles.  Taking them
    # from the screen-space copy would mirror Y, which reverses the cross
    # product's handedness and points every normal away from the viewer.
    c = v.reshape(-1, 3, 3)
    n = np.cross(c[:, 1] - c[:, 0], c[:, 2] - c[:, 0])
    n /= np.maximum(np.linalg.norm(n, axis=1, keepdims=True), 1e-9)
    n = np.where(n[:, 2:3] < 0, -n, n)      # two-sided: winding is not trusted
    key = np.array([0.35, 0.55, 0.76]); key /= np.linalg.norm(key)
    lit = 0.16 + 0.60 * np.clip(n @ key, 0, 1) + 0.24 * np.clip(n[:, 2], 0, 1)

    colour = np.zeros((h, w), dtype=np.float64)
    depth = np.full((h, w), -1e30)
    albedo = np.zeros((h, w, 3), dtype=np.float64)
    textured = uv is not None and tex is not None

    # painter-safe z-buffer, scanline per triangle
    x0 = np.clip(np.floor(p[:, :, 0].min(1)).astype(int), 0, w - 1)
    x1 = np.clip(np.ceil(p[:, :, 0].max(1)).astype(int), 0, w - 1)
    y0 = np.clip(np.floor(p[:, :, 1].min(1)).astype(int), 0, h - 1)
    y1 = np.clip(np.ceil(p[:, :, 1].max(1)).astype(int), 0, h - 1)

    for t in range(len(p)):
        if x1[t] <= x0[t] or y1[t] <= y0[t]:
            continue
        ax, ay, az = p[t, 0]; bx, by, bz = p[t, 1]; cx2, cy2, cz = p[t, 2]
        area = (bx - ax) * (cy2 - ay) - (by - ay) * (cx2 - ax)
        if abs(area) < 1e-12:
            continue
        xs = np.arange(x0[t], x1[t] + 1) + 0.5
        ys = np.arange(y0[t], y1[t] + 1) + 0.5
        gx, gy = np.meshgrid(xs, ys)
        wc = ((bx - ax) * (gy - ay) - (by - ay) * (gx - ax)) / area   # area ABP -> C
        wa = ((cx2 - bx) * (gy - by) - (cy2 - by) * (gx - bx)) / area  # area BCP -> A
        wb = 1.0 - wa - wc
        inside = (wa >= 0) & (wb >= 0) & (wc >= 0)
        if not inside.any():
            continue
        z = az * wa + bz * wb + cz * wc
        sl = (slice(y0[t], y1[t] + 1), slice(x0[t], x1[t] + 1))
        win = inside & (z > depth[sl])
        depth[sl] = np.where(win, z, depth[sl])
        colour[sl] = np.where(win, lit[t], colour[sl])
        if textured:
            uu = (uv[t, 0] * wa[..., None] + uv[t, 1] * wb[..., None]
                  + uv[t, 2] * wc[..., None])
            th, tw = tex.shape[:2]
            # glTF puts UV (0,0) at the TOP-left, which is also image row 0,
            # so V is not flipped on the way in.
            px = np.clip((uu[..., 0] % 1.0) * (tw - 1), 0, tw - 1).astype(int)
            py = np.clip((uu[..., 1] % 1.0) * (th - 1), 0, th - 1).astype(int)
            albedo[sl] = np.where(win[..., None], tex[py, px], albedo[sl])

    img = np.full((h, w, 3), 22, dtype=np.uint8)
    hit = depth > -1e29
    if textured:
        # keep some of the flat shading so form still reads under the texture
        shaded = albedo * (0.55 + 0.65 * colour[..., None])
    else:
        shaded = np.repeat(colour[..., None], 3, axis=2)
    shaded = np.clip(shaded * 255, 0, 255).astype(np.uint8)
    for c in range(3):
        img[:, :, c] = np.where(hit, shaded[:, :, c], img[:, :, c])
    return img


def png(path, img):
    h, w, _ = img.shape
    raw = b''.join(b'\x00' + img[y].tobytes() for y in range(h))

    def chunk(tag, d):
        c = tag + d
        return struct.pack('>I', len(d)) + c + struct.pack('>I', zlib.crc32(c) & 0xffffffff)

    with open(path, 'wb') as f:
        f.write(b'\x89PNG\r\n\x1a\n'
                + chunk(b'IHDR', struct.pack('>IIBBBBB', w, h, 8, 2, 0, 0, 0))
                + chunk(b'IDAT', zlib.compress(raw, 6))
                + chunk(b'IEND', b''))


def main(argv):
    src = argv[1]
    out = 'look.png'
    size = 420
    angles = [0, 45, 90, 180]
    elev = 8.0
    clip = None
    time = 0.0
    tex = False
    i = 2
    while i < len(argv):
        if argv[i] == '-o':
            out = argv[i + 1]; i += 2
        elif argv[i] == '--size':
            size = int(argv[i + 1]); i += 2
        elif argv[i] == '--angles':
            angles = [float(a) for a in argv[i + 1].split(',')]; i += 2
        elif argv[i] == '--elev':
            elev = float(argv[i + 1]); i += 2
        elif argv[i] == '--clip':
            clip = argv[i + 1]; i += 2
        elif argv[i] == '--time':
            time = float(argv[i + 1]); i += 2
        elif argv[i] == '--tex':
            tex = True; i += 1
        else:
            raise SystemExit('unknown argument %s' % argv[i])

    tris, js, uv = triangles(src, clip, time)
    texture = base_colour(src) if tex else None
    lo = tris.reshape(-1, 3).min(0); hi = tris.reshape(-1, 3).max(0)
    print('%s: %d triangles, extent %.2f x %.2f x %.2f (XYZ)'
          % (os.path.basename(src), len(tris), *(hi - lo)))
    print('  meshes %d  skins %d  animations %d  images %d'
          % (len(js.get('meshes', [])), len(js.get('skins', [])),
             len(js.get('animations', [])), len(js.get('images', []))))

    h = int(size * 1.35)
    cells = [view(tris, a, elev, size, h, uv, texture) for a in angles]
    sheet = np.concatenate(
        [np.concatenate([c, np.full((h, 2, 3), 70, np.uint8)], axis=1) for c in cells], axis=1)
    png(out, sheet)
    print('  wrote %s - views left to right: %s (azimuth degrees, elev %g)'
          % (out, ', '.join('%g' % a for a in angles), elev))


if __name__ == '__main__':
    main(sys.argv)
