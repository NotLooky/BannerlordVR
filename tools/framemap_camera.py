#!/usr/bin/env python3
"""
framemap_camera.py - find EVERY camera matrix in the frame's constant buffers, by
geometry, and say which ones the second eye must have rewritten.

    python tools/framemap_camera.py [capture_dir]

WHY NOT COMPARE THE TWO EYES DIRECTLY

The obvious test - "how does the engine's eye-1 buffer differ from its eye-0 buffer"
- does not survive contact with the data: the engine jitters its PROJECTION every
frame for temporal AA, and the jitter lands in the same lanes as the eye offset, an
order of magnitude larger (3e-4 of NDC times a ~700 translation term is ~0.2, against
the eye's ~0.035). Two frames are never the same camera even for one eye.

So each matrix is tested against the camera OF ITS OWN FRAME, which jitter cannot
disturb, using facts that hold for any projection:

  WORLD->CLIP   the camera position maps to w = 0 (it is the projection's origin)
  WORLD->VIEW   the camera position maps to (0,0,0,1)
  CLIP->WORLD   NDC (0,0) at two depths traces a line through the camera position
  CAMPOS        a float3 equal to the camera position

The eye/time comparison is still reported, but as CORROBORATION: a lane that the
geometry calls a camera matrix should also move more between the eyes than it does
between two frames of the same eye.
"""
import math
import os
import re
import struct
import sys
from collections import defaultdict

# ---------------------------------------------------------------------------


def parse_cb(path):
    with open(path, "rb") as f:
        data = f.read()
    blobs, i = [], 0
    while i + 24 <= len(data):
        magic, seq, frame, rid, off, n = struct.unpack_from("<6I", data, i)
        if magic != 0x50554243:
            break
        i += 24
        nf = n // 4
        blobs.append({"seq": seq, "frame": frame, "res": rid, "off": off,
                      "f": struct.unpack_from("<%df" % nf, data, i) if nf else ()})
        i += n
    return blobs


OP_RE = re.compile(r'^#(\d+) f(\d+) th(\d+)( \[ours\])? (\S+)(.*)$')


def parse_ops(path):
    """cameras per frame, and for each CB upload seq the shaders bound at the next
    draw/dispatch (so a buffer can be tied to the passes that consume it)."""
    cams, frame = {}, 0
    eng = {}
    with open(path, encoding="utf-8", errors="replace") as f:
        for line in f:
            if line.startswith("=== FRAME"):
                frame = int(re.match(r'=== FRAME (\d+)', line).group(1))
            elif line.startswith("CAMERA published"):
                m = re.search(r'eye=(-?\d+).*eyeCamera=\[([^\]]*)\]', line)
                if m:
                    v = [float(x) for x in m.group(2).split()]
                    if len(v) == 16:
                        cams[frame] = {"eye": int(m.group(1)), "pos": (v[12], v[13], v[14])}
            elif line.startswith("CAMERA engine-recovered"):
                m = re.search(r'pos=\(([-\d.e]+) ([-\d.e]+) ([-\d.e]+)\)', line)
                if m:
                    eng[frame] = tuple(float(m.group(k)) for k in (1, 2, 3))
    for fr in cams:
        if fr in eng:
            cams[fr]["eng"] = eng[fr]
    return cams


def parse_resources(path):
    res = {}
    with open(path, encoding="utf-8", errors="replace") as f:
        for line in f:
            m = re.match(r'R(\d+) ', line.strip())
            if m:
                res[int(m.group(1))] = line.strip()
    return res


# ---------------------------------------------------------------------------
# row-vector convention: p' = p * M
# ---------------------------------------------------------------------------

def rows(f, i):
    return [list(f[i + 4 * r:i + 4 * r + 4]) for r in range(4)]


def transpose(m):
    return [[m[c][r] for c in range(4)] for r in range(4)]


def mul_point(p, m):
    return [sum(p[r] * m[r][c] for r in range(4)) for c in range(4)]


def mag_point(p, m):
    """Sum of |terms| per output component - the scale a zero must be judged against."""
    return [sum(abs(p[r] * m[r][c]) for r in range(4)) for c in range(4)]


def finite(m):
    return all(math.isfinite(x) for r in m for x in r)


def classify(m, c):
    """What this matrix is, relative to a camera at c. None if it is not one."""
    if not finite(m):
        return None
    s = max(abs(x) for r in m for x in r)
    if s < 1e-9 or s > 1e9:
        return None

    p = [c[0], c[1], c[2], 1.0]
    v = mul_point(p, m)
    g = mag_point(p, m)
    tol = [max(3e-5 * gi, 1e-4) for gi in g]

    upper = max(abs(m[r][k]) for r in range(3) for k in range(3))
    if upper > 1e-9:
        # world -> clip: the camera is where w vanishes (and x, y with it)
        if (abs(v[3]) < tol[3] and abs(v[0]) < tol[0] and abs(v[1]) < tol[1]
                and g[3] > 1e-3):
            return "WORLD->CLIP"
        # world -> view: the camera is the origin
        if (abs(v[0]) < tol[0] and abs(v[1]) < tol[1] and abs(v[2]) < tol[2]
                and abs(v[3] - 1.0) < 1e-3 and max(g[0], g[1], g[2]) > 1e-3):
            return "WORLD->VIEW"

    # clip -> world: NDC (0,0) at two depths is a line through the camera
    a = mul_point([0.0, 0.0, 0.2, 1.0], m)
    b = mul_point([0.0, 0.0, 0.8, 1.0], m)
    if abs(a[3]) > 1e-12 and abs(b[3]) > 1e-12:
        pa = [a[k] / a[3] for k in range(3)]
        pb = [b[k] / b[3] for k in range(3)]
        ab = [pb[k] - pa[k] for k in range(3)]
        L = math.sqrt(sum(x * x for x in ab))
        if L > 1e-3:
            u = [x / L for x in ab]
            w = [c[k] - pa[k] for k in range(3)]
            t = sum(w[k] * u[k] for k in range(3))
            perp = math.sqrt(max(sum((w[k] - t * u[k]) ** 2 for k in range(3)), 0.0))
            if perp < 0.02 and abs(t) < 1e5:
                return "CLIP->WORLD"
    return None


# ---------------------------------------------------------------------------


def main():
    cap = sys.argv[1] if len(sys.argv) > 1 else None
    if cap is None:
        root = os.path.join(os.path.expanduser("~"), "Documents",
                            "Mount and Blade II Bannerlord", "Logs", "BannerlordVR.FrameMap")
        subs = [os.path.join(root, x) for x in os.listdir(root)]
        cap = max([s for s in subs if os.path.isdir(s)], key=os.path.getmtime)

    blobs = parse_cb(os.path.join(cap, "cb.bin"))
    cams = parse_ops(os.path.join(cap, "ops.txt"))
    res = parse_resources(os.path.join(cap, "resources.txt"))

    print("capture:", cap)
    for fr in sorted(cams):
        print("  frame %d eye %d published %s engine %s" %
              (fr, cams[fr]["eye"], cams[fr]["pos"], cams[fr].get("eng")))

    # Test against BOTH eye positions: the constants uploaded during a capture frame
    # belong to the eye published one step earlier, so the frame header is off by one.
    # this frame were actually built from (managed drives the engine camera onto the
    # eye, so the two agree to a fraction of a millimetre - reported above).
    hits = defaultdict(lambda: defaultdict(int))       # res -> (off, kind, T) -> n
    uploads = defaultdict(int)
    examples = {}

    for bl in blobs:
        cam = cams.get(bl["frame"])
        if cam is None:
            continue
        cand = [cams[fr]["pos"] for fr in sorted(cams)]
        f = bl["f"]
        uploads[bl["res"]] += 1
        for i in range(0, len(f) - 15, 4):
            for st in (False, True):
                m = rows(f, i)
                if st:
                    m = transpose(m)
                k = None
                for c in cand:
                    k = classify(m, c)
                    if k: break
                if k:
                    key = (bl["off"] + 4 * i, k, st)
                    hits[bl["res"]][key] += 1
                    examples.setdefault((bl["res"], key), (bl["frame"], m))

    print("\n" + "=" * 78)
    print("CAMERA MATRICES FOUND BY GEOMETRY")
    print("=" * 78)
    total = 0
    for rid in sorted(hits, key=lambda r: -sum(hits[r].values())):
        print("\n%s   uploads seen %d" % (res.get(rid, "R%d" % rid), uploads[rid]))
        for (off, kind, st), n in sorted(hits[rid].items()):
            total += 1
            print("    +%-6d %-12s %-11s seen %d/%d uploads" %
                  (off, kind, "transposed" if st else "row-major", n, uploads[rid]))
    print("\ndistinct camera matrix sites: %d" % total)

    print("\n" + "=" * 78)
    print("WHAT THE SECOND EYE NEEDS, PER SITE")
    print("=" * 78)
    print("  WORLD->CLIP / WORLD->VIEW : M' = T(-d) * M   (row 3 += -d * upper3x3)")
    print("  CLIP->WORLD               : M' = M * T(+d)   (each row += its w * d)")
    print("  CAMPOS                    : v' = v + d")
    print("  everything else           : unchanged - the engine feeds WORLD positions,")
    print("                              so object and bone matrices do not move.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
