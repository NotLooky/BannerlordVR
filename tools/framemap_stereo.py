#!/usr/bin/env python3
"""
framemap_stereo.py - derive the SECOND EYE'S CONSTANTS from the engine's own data.

    python tools/framemap_stereo.py [capture_dir] [--dump R78,R43]

WHY THIS EXISTS

Under AFR the engine renders eye 0 and eye 1 on consecutive frames, building every
constant itself. A four-frame map (L R L R) therefore contains the answer to the only
question native stereo has to get right:

    given eye 0's constant buffer, what would the engine have put in eye 1's?

PAIRING IS THE HARD PART. Per-object buffers are uploaded once per draw, and the two
frames do not contain exactly the same draws, so the k-th upload of one frame is not
the k-th of the other. The two op streams are therefore ALIGNED first (difflib over op
signatures), and only uploads that land in a matched block are compared. Pairing by
index silently compares different objects and makes every rule look wrong - which is
exactly what the first version of this script reported.

Then, for every 16-byte-aligned 4x4, it asks which rule reproduces the engine's frame-1
bytes from its frame-0 bytes, for a measured eye offset d:

    unchanged, pre-T(-d), pre-T(+d), post-T(+d), post-T(-d), row-major or transposed

Lanes that change but match no rule are UNEXPLAINED - those are what would come out
wrong in the second eye.
"""
import difflib
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
        blobs.append({"seq": seq, "frame": frame, "res": rid, "off": off, "n": n,
                      "f": struct.unpack_from("<%df" % nf, data, i) if nf else ()})
        i += n
    return blobs


OP_RE = re.compile(r'^#(\d+) f(\d+) th(\d+)( \[ours\])? (\S+)(.*)$')


def parse_ops(path):
    """Per frame: ordered (seq, signature) and the published camera."""
    frames, cams = defaultdict(list), {}
    frame = 0
    with open(path, encoding="utf-8", errors="replace") as f:
        for line in f:
            if line.startswith("=== FRAME"):
                frame = int(re.match(r'=== FRAME (\d+)', line).group(1))
                continue
            if line.startswith("CAMERA published"):
                m = re.search(r'eye=(-?\d+).*eyeCamera=\[([^\]]*)\]', line)
                if m:
                    v = [float(x) for x in m.group(2).split()]
                    if len(v) == 16:
                        cams[frame] = {"eye": int(m.group(1)), "pos": (v[12], v[13], v[14])}
                continue
            m = OP_RE.match(line)
            if not m:
                continue
            seq, kind, rest = int(m.group(1)), m.group(5), m.group(6)
            if kind == "CB-UPLOAD":
                rm = re.match(r' R(\d+) bytes=(\d+)', rest)
                sig = "CB|%s|%s" % (rm.group(1), rm.group(2)) if rm else "CB|?"
            else:
                sh = " ".join(re.findall(r'([VHDGPC]S)=S\d+', rest))
                shid = " ".join(re.findall(r'[VHDGPC]S=(S\d+)', rest))
                rt = re.search(r'rt0=V\d+:R(\d+)', rest)
                sig = "%s|%s|%s|%s" % (kind, sh, shid, rt.group(1) if rt else "-")
            frames[frame].append((seq, sig))
    return frames, cams


def parse_resources(path):
    res = {}
    with open(path, encoding="utf-8", errors="replace") as f:
        for line in f:
            m = re.match(r'R(\d+) (\S+)(.*)', line.strip())
            if m:
                res[int(m.group(1))] = line.strip()
    return res


def align(ops_a, ops_b):
    """seq in frame a -> seq in frame b, for ops that genuinely correspond."""
    sa = [s for _, s in ops_a]
    sb = [s for _, s in ops_b]
    sm = difflib.SequenceMatcher(a=sa, b=sb, autojunk=False)
    out = {}
    for i, j, n in sm.get_matching_blocks():
        for k in range(n):
            out[ops_a[i + k][0]] = ops_b[j + k][0]
    return out


# ---------------------------------------------------------------------------
# 4x4 helpers - row-vector convention: p' = p * M, translation in row 3
# ---------------------------------------------------------------------------

def rows(f, i):
    return [list(f[i + 4 * r:i + 4 * r + 4]) for r in range(4)]


def transpose(m):
    return [[m[c][r] for c in range(4)] for r in range(4)]


def pre_translate(m, d):
    out = [list(r) for r in m]
    for c in range(4):
        out[3][c] = m[3][c] + d[0] * m[0][c] + d[1] * m[1][c] + d[2] * m[2][c]
    return out


def post_translate(m, d):
    out = [list(r) for r in m]
    for r in range(4):
        for c in range(3):
            out[r][c] = m[r][c] + m[r][3] * d[c]
    return out


def maxdiff(a, b):
    return max(abs(a[r][c] - b[r][c]) for r in range(4) for c in range(4))


def finite(m):
    return all(math.isfinite(x) for r in m for x in r)


def scale_of(m):
    return max(abs(x) for r in m for x in r)


def neg(d):
    return (-d[0], -d[1], -d[2])


RULES = [
    ("unchanged",  lambda m, d: m),
    ("pre-T(-d)",  lambda m, d: pre_translate(m, neg(d))),
    ("pre-T(+d)",  lambda m, d: pre_translate(m, d)),
    ("post-T(+d)", lambda m, d: post_translate(m, d)),
    ("post-T(-d)", lambda m, d: post_translate(m, neg(d))),
]


def classify_matrix(m0, m1, d):
    if not (finite(m0) and finite(m1)):
        return None
    s = max(scale_of(m0), 1e-6)
    if s > 1e8:
        return None
    change = maxdiff(m0, m1)
    best, bestres = None, None
    for name, fn in RULES:
        for st in (False, True):
            a = transpose(m0) if st else m0
            b = transpose(m1) if st else m1
            r = maxdiff(fn(a, d), b)
            if bestres is None or r < bestres:
                bestres, best = r, name + (" T" if st else "")
    return {"rule": best, "res": bestres, "change": change, "scale": s}


def dump_pair(tag, f0, f1, base):
    print("\n  --- %s (first matched upload, %d floats) ---" % (tag, len(f0)))
    for i in range(0, min(len(f0), 48), 4):
        a = " ".join("%12.5f" % x for x in f0[i:i + 4])
        b = " ".join("%12.5f" % x for x in f1[i:i + 4])
        dd = " ".join("%9.5f" % (f1[i + k] - f0[i + k]) for k in range(min(4, len(f0) - i)))
        print("   +%-5d eye0[%s]  eye1[%s]  d[%s]" % (base + 4 * i, a, b, dd))


# ---------------------------------------------------------------------------


def main():
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    dumps = []
    for a in sys.argv[1:]:
        if a.startswith("--dump"):
            dumps = [int(x.strip().lstrip("R")) for x in a.split("=", 1)[1].split(",")]

    cap = args[0] if args else None
    if cap is None:
        root = os.path.join(os.path.expanduser("~"), "Documents",
                            "Mount and Blade II Bannerlord", "Logs", "BannerlordVR.FrameMap")
        subs = [os.path.join(root, x) for x in os.listdir(root)]
        cap = max([s for s in subs if os.path.isdir(s)], key=os.path.getmtime)

    blobs = parse_cb(os.path.join(cap, "cb.bin"))
    ops, cams = parse_ops(os.path.join(cap, "ops.txt"))
    res = parse_resources(os.path.join(cap, "resources.txt"))

    frames = sorted(cams)
    print("capture:", cap)
    for fr in frames:
        print("  frame %d eye %d pos %s ops %d" %
              (fr, cams[fr]["eye"], cams[fr]["pos"], len(ops[fr])))

    pairs = [(a, b) for a in frames for b in frames
             if b == a + 1 and cams[a]["eye"] != cams[b]["eye"]]
    if not pairs:
        print("no consecutive frames of different eyes")
        return 1
    a, b = pairs[0]
    d = tuple(cams[b]["pos"][k] - cams[a]["pos"][k] for k in range(3))
    print("\neye pair: frame %d (eye %d) -> frame %d (eye %d)" %
          (a, cams[a]["eye"], b, cams[b]["eye"]))
    print("measured eye offset d = (%.6f, %.6f, %.6f)  |d| = %.4f m" %
          (d[0], d[1], d[2], math.sqrt(sum(x * x for x in d))))

    m = align(ops[a], ops[b])
    print("op alignment: %d of %d frame-%d ops matched to frame-%d ops" %
          (len(m), len(ops[a]), a, b))

    by_seq = {}
    for bl in blobs:
        by_seq[(bl["frame"], bl["seq"])] = bl

    matched = defaultdict(list)
    for bl in blobs:
        if bl["frame"] != a:
            continue
        tgt = m.get(bl["seq"])
        if tgt is None:
            continue
        other = by_seq.get((b, tgt))
        if other is not None and other["res"] == bl["res"] and len(other["f"]) == len(bl["f"]):
            matched[bl["res"]].append((bl, other))

    print("\n" + "=" * 78)
    print("PER-BUFFER RULES  (what turns eye 0's constants into eye 1's)")
    print("=" * 78)

    unexplained = 0
    for rid in sorted(matched, key=lambda r: -len(matched[r])):
        pairlist = matched[rid]
        stats = defaultdict(lambda: defaultdict(list))
        vec = defaultdict(lambda: defaultdict(int))
        changed = False

        for bl0, bl1 in pairlist:
            f0, f1, base = bl0["f"], bl1["f"], bl0["off"]
            for i in range(0, len(f0) - 15, 4):
                c = classify_matrix(rows(f0, i), rows(f1, i), d)
                if c is None or c["change"] <= 1e-7 * max(c["scale"], 1.0):
                    continue
                changed = True
                tol = 0.02 * c["change"] + 1e-5 * c["scale"]
                stats[base + 4 * i]["UNEXPLAINED" if c["res"] > tol else c["rule"]].append(c["res"])
            for i in range(0, len(f0) - 2):
                dv = [f1[i + j] - f0[i + j] for j in range(3)]
                if max(abs(x) for x in dv) < 1e-7:
                    continue
                for sgn, nm in ((1.0, "pos +d"), (-1.0, "pos -d")):
                    if all(abs(dv[j] - sgn * d[j]) < 0.002 for j in range(3)):
                        vec[base + 4 * i][nm] += 1

        if not changed and not vec:
            continue

        print("\n%s   matched uploads %d" % (res.get(rid, "R%d" % rid), len(pairlist)))
        for off in sorted(stats):
            per = stats[off]
            parts = ["%s x%d (res %.2e)" % (r, len(v), max(v))
                     for r, v in sorted(per.items(), key=lambda kv: -len(kv[1]))]
            if "UNEXPLAINED" in per:
                unexplained += len(per["UNEXPLAINED"])
            print("    +%-5d %s%s" % (off, "  ".join(parts[:3]),
                                      "  <<< UNEXPLAINED" if "UNEXPLAINED" in per else ""))
        for off in sorted(vec):
            print("    +%-5d VECTOR %s" % (off, dict(vec[off])))

        if rid in dumps and pairlist:
            dump_pair(res.get(rid, "R%d" % rid), pairlist[0][0]["f"], pairlist[0][1]["f"],
                      pairlist[0][0]["off"])

    print("\nUNEXPLAINED matrix instances in total: %d" % unexplained)
    return 0


if __name__ == "__main__":
    sys.exit(main())
