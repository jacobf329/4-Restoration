#!/usr/bin/env python3
"""Turn a generated T-pose image into a game-ready .glb via Meshy image-to-3D.

The roster is authored as images first (tools/portraits.py, OpenAI) and only
then lifted into 3D, because Meshy's own text-to-3D of a *character* is
markedly worse than its image-to-3D of a good reference.

    python3 tools/image23d.py john_smith
    python3 tools/image23d.py john_smith --tris 2000

Reads assets/characters/<name>_tpose.png, writes assets/characters/<name>.glb,
then renders it so the result gets looked at instead of just measured.

Needs a Meshy credential: MESHY_API_KEY, or the environment API credentials
configured at claude.ai/code, which the proxy attaches per request.
"""
import argparse, base64, json, os, subprocess, sys, time, urllib.request

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CHARACTERS = os.path.join(ROOT, 'assets', 'characters')
API = 'https://api.meshy.ai/openapi/v1/image-to-3d'

# Characters run twelve-up in four splitscreen viewports, so the per-body
# budget is tight.  2k reads cleanly at that size once the normal map is on;
# the faceting only shows in a close-up.
DEFAULT_TRIS = 2000


def call(url, payload=None, method='GET'):
    body = json.dumps(payload).encode() if payload is not None else None
    req = urllib.request.Request(url, data=body, method=method,
                                 headers={'Content-Type': 'application/json'})
    key = os.environ.get('MESHY_API_KEY')
    if key:
        req.add_header('Authorization', 'Bearer ' + key)
    with urllib.request.urlopen(req, timeout=120) as r:
        return json.loads(r.read())


def generate(name, tris):
    src = os.path.join(CHARACTERS, name + '_tpose.png')
    if not os.path.exists(src):
        raise SystemExit('no T-pose at %s - generate it first with tools/portraits.py' % src)

    with open(src, 'rb') as f:
        uri = 'data:image/png;base64,' + base64.b64encode(f.read()).decode()

    task = call(API, {
        'image_url': uri,
        'ai_model': 'meshy-5',
        'topology': 'triangle',
        'target_polycount': tris,
        'should_remesh': True,
        'should_texture': True,
        'enable_pbr': True,
    }, 'POST')['result']
    print('%s: task %s' % (name, task))

    last = None
    while True:
        s = call('%s/%s' % (API, task))
        note = '%s %s%%' % (s['status'], s.get('progress', 0))
        if note != last:
            print('  ' + note)
            last = note
        if s['status'] == 'SUCCEEDED':
            break
        if s['status'] in ('FAILED', 'CANCELED'):
            raise SystemExit('  %s: %s' % (s['status'], s.get('task_error')))
        time.sleep(10)

    url = s['model_urls']['glb']
    out = os.path.join(CHARACTERS, name + '.glb')
    with urllib.request.urlopen(url, timeout=300) as r, open(out, 'wb') as f:
        f.write(r.read())
    print('  wrote %s (%d KB)' % (out, os.path.getsize(out) // 1024))
    print('  rig it from this same task id: %s' % task)
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('name', help='character key, e.g. john_smith')
    ap.add_argument('--tris', type=int, default=DEFAULT_TRIS)
    a = ap.parse_args()

    out = generate(a.name, a.tris)
    subprocess.run([sys.executable, os.path.join(ROOT, 'tools', 'look.py'), out,
                    '-o', os.path.join(ROOT, a.name + '_look.png')])


if __name__ == '__main__':
    main()
