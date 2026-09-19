#!/usr/bin/env python3
"""
framemap_audit.py - check that every per_framef upload in a frame map describes ONE camera.

    python tools/framemap_audit.py [capture_dir]

Run with LibreOffice's python (no real Python is installed):
    "C:\\Program Files\\LibreOffice\\program\\python.exe" tools/framemap_audit.py

WHY IT EXISTS (TEST 166)

The shader disassembly in a capture carries full HLSL reflection, so per_framef's
lanes have NAMES - g_view_proj, g_view_proj_inverse, g_viewproj_unjittered, g_view,
g_inverse_view - and the relations between them are known exactly. A frame drawn
from one camera satisfies all of them in every upload; a frame split between two
viewpoints (vp_patch substituting some uploads and not others, or with a D that
changes mid-frame) breaks them. The dynamic-shadow pass reconstructs world position
through g_view_proj_inverse from depth rasterised through g_view_proj, so a broken
pair is a shadow that slides.

Lane offsets are read from the capture's own reflection, not hard-coded, so a game
update that moves them cannot make this report the wrong lane.

CHECKS, per main-view per_framef upload (a light-view upload - a cascade caster
pass, orthographic g_view_proj - is counted and skipped):
  INV   inverse(g_view_proj) * g_view_proj_inverse == I
  UNJ   g_viewproj_unjittered == g_view_proj, up to a sub-pixel jitter
  VIEW  inverse(g_view) * g_inverse_view == I
  SAME  every main-view upload of the frame carries the same g_view_proj

NOTE: frame_map records the ORIGINAL bytes. Under vp_patch = 1 that is what the
engine wrote, not what the GPU drew, so this proves the engine consistent and says
nothing about the patch. Under the late latch (vp_patch = 0) the original bytes ARE
the drawn bytes, and a clean report is the proof that the frame has one viewpoint.
"""
import glob
import os
import re
import struct
import sys

UPLOAD_RE = re.compile(r"^#(\d+) f(\d+) .*CB-UPLOAD (R\d+) bytes=(\d+) off=(\d+) via=\S+ blob=(\d+)")
# Every member, not only the matrices: the buffer's size is the end of its LAST
# member, and sizing it from the matrices alone put per_framef at 992 bytes, which
# matched no upload and reported a clean frame having checked nothing.
LANE_RE = re.compile(r"^//\s+\w+ (\w+)(?:\[\d+\])?;\s*// Offset:\s+(\d+) Size:\s+(\d+)")

TOL_INV = 2e-3     # max |element| deviation of a product from identity
TOL_UNJ = 5e-3     # relative: jitter moves the projection by a fraction of a pixel


def default_capture():
    base = os.path.join(os.path.expanduser("~"), "Documents", "Mount and Blade II Bannerlord",
                        "Logs", "BannerlordVR.FrameMap")
    dirs = sorted(d for d in glob.glob(os.path.join(base, "*")) if os.path.isdir(d))
    return dirs[-1] if dirs else None


def per_framef_lanes(cap):
    """name -> (offset, size) for per_framef, from the first shader that declares it."""
    for path in sorted(glob.glob(os.path.join(cap, "shaders", "*.asm"))):
        with open(path, encoding="utf-8", errors="replace") as f:
            text = f.read()
        i = text.find("// cbuffer per_framef")
        if i < 0:
            continue
        lanes = {}
        for line in text[i:].splitlines()[1:]:
            if line.startswith("// }"):
                break
            m = LANE_RE.match(line)
            if m:
                lanes[m.group(1)] = (int(m.group(2)), int(m.group(3)))
        if lanes:
            return lanes, os.path.basename(path)
    return None, None


def load_blobs(cap):
    with open(os.path.join(cap, "cb.bin"), "rb") as f:
        data = f.read()
    blobs, i = [], 0
    while i + 24 <= len(data):
        magic, seq, frame, rid, off, n = struct.unpack_from("<6I", data, i)
        if magic != 0x50554243:
            print("cb.bin: bad magic at", i, file=sys.stderr)
            break
        i += 24
        blobs.append(struct.unpack_from("<%df" % (n // 4), data, i))
        i += n
    return blobs


def mat(f, off):
    return list(f[off // 4:off // 4 + 16])


def mul(a, b):
    return [sum(a[r * 4 + k] * b[k * 4 + c] for k in range(4)) for r in range(4) for c in range(4)]


def inv(m):
    a = [m[r * 4:(r + 1) * 4] + [1.0 if r == c else 0.0 for c in range(4)] for r in range(4)]
    for c in range(4):
        p = max(range(c, 4), key=lambda r: abs(a[r][c]))
        if abs(a[p][c]) < 1e-30:
            return None
        a[c], a[p] = a[p], a[c]
        piv = a[c][c]
        a[c] = [x / piv for x in a[c]]
        for r in range(4):
            if r != c:
                k = a[r][c]
                a[r] = [x - k * y for x, y in zip(a[r], a[c])]
    return [a[r][4 + c] for r in range(4) for c in range(4)]


def identity_dev(m):
    return max(abs(m[r * 4 + c] - (1.0 if r == c else 0.0)) for r in range(4) for c in range(4))


def rel_diff(a, b):
    scale = max(1e-12, max(abs(x) for x in a))
    return max(abs(x - y) for x, y in zip(a, b)) / scale


def orthographic(m):
    row = abs(m[3]) < 1e-6 and abs(m[7]) < 1e-6 and abs(m[11]) < 1e-6 and abs(m[15] - 1) < 1e-4
    col = abs(m[12]) < 1e-6 and abs(m[13]) < 1e-6 and abs(m[14]) < 1e-6 and abs(m[15] - 1) < 1e-4
    return row or col


def product_dev(a, b):
    """How far a and b are from being each other's inverse: a*b == I."""
    return identity_dev(mul(a, b))


def apex(vp):
    """The eye point of a perspective world->clip matrix (row-vector): the point
    it sends to w = 0 on the axis, i.e. clip (0,0,1,0) taken back to world."""
    iv = inv(vp)
    if not iv:
        return None
    p = [sum((0.0, 0.0, 1.0, 0.0)[k] * iv[k * 4 + c] for k in range(4)) for c in range(4)]
    if abs(p[3]) < 1e-12:
        return None
    return [p[0] / p[3], p[1] / p[3], p[2] / p[3]]


def main():
    cap = sys.argv[1] if len(sys.argv) > 1 else default_capture()
    if not cap or not os.path.isdir(cap):
        print("no capture folder found", file=sys.stderr)
        return 1

    lanes, source = per_framef_lanes(cap)
    if not lanes:
        print("no shader in %s declares cbuffer per_framef" % cap, file=sys.stderr)
        return 1
    need = ["g_camera_position", "g_view_proj", "g_view_proj_inverse", "g_viewproj_unjittered",
            "g_view", "g_inverse_view"]
    missing = [n for n in need if n not in lanes]
    if missing:
        print("per_framef lacks %s - layout changed; update this tool" % missing, file=sys.stderr)
        return 1
    size = max(o + s for o, s in lanes.values())
    size = (size + 15) // 16 * 16
    off = {n: lanes[n][0] for n in need}

    print("framemap_audit: %s" % cap)
    print("per_framef layout from %s (%d bytes): %s" % (
        source, size, ", ".join("%s@%d" % (n, off[n]) for n in need)))

    blobs = load_blobs(cap)
    uploads = []
    with open(os.path.join(cap, "ops.txt"), encoding="utf-8", errors="replace") as f:
        for line in f:
            m = UPLOAD_RE.match(line)
            if m and int(m.group(4)) == size and int(m.group(5)) == 0:
                uploads.append((int(m.group(1)), int(m.group(2)), int(m.group(6))))

    frames = sorted({fr for _, fr, _ in uploads})
    if not uploads:
        print("\nRESULT: NOTHING CHECKED - no %d-byte upload in ops.txt. Not a clean "
              "result; the size or the capture is wrong." % size)
        return 1
    total_bad = 0
    checked = 0
    for fr in frames:
        light = 0
        other = 0
        rows = []
        scene_cam = None     # the frame's camera position, from its first main view
        scene_vp = None      # ...and that upload's (jittered) g_view_proj
        for opn, frame, bi in uploads:
            if frame != fr:
                continue
            b = blobs[bi]
            vp = mat(b, off["g_view_proj"])
            if orthographic(vp):
                light += 1
                continue
            # The main view is the one whose eye point IS this upload's own
            # g_camera_position, and that position is the scene camera's. A view
            # at the world origin (a 90-degree cube face or UI pass was found at
            # the end of the frame) matches the first test and not the second.
            eye = apex(vp)
            cam = b[off["g_camera_position"] // 4:off["g_camera_position"] // 4 + 3]
            if eye is None or max(abs(eye[k] - cam[k]) for k in range(3)) > 1.0:
                other += 1
                continue
            if scene_cam is None:
                if max(abs(c) for c in cam) < 1.0:
                    other += 1
                    continue
                scene_cam, scene_vp = list(cam), vp
            elif max(abs(cam[k] - scene_cam[k]) for k in range(3)) > 1.0:
                other += 1
                continue
            # 688 must invert SOME account of this camera: the late post passes
            # upload the unjittered matrix into g_view_proj but keep the jittered
            # inverse, which is one camera, not two.
            inverse = mat(b, off["g_view_proj_inverse"])
            d_inv = min(product_dev(vp, inverse),
                        product_dev(mat(b, off["g_viewproj_unjittered"]), inverse),
                        product_dev(scene_vp, inverse))
            d_unj = rel_diff(vp, mat(b, off["g_viewproj_unjittered"]))
            d_view = product_dev(mat(b, off["g_view"]), mat(b, off["g_inverse_view"]))
            rows.append((opn, bi, vp, d_inv, d_unj, d_view))

        ref = rows[0][2] if rows else None
        bad = 0
        print("\nframe %d: %d main-view upload(s); skipped %d light view(s), %d other view(s)" % (
            fr, len(rows), light, other))
        print("   op       blob   INV        UNJ        VIEW       SAME")
        for opn, bi, vp, d_inv, d_unj, d_view in rows:
            d_same = rel_diff(ref, vp)
            flags = []
            if d_inv > TOL_INV:
                flags.append("INV")
            if d_unj > TOL_UNJ:
                flags.append("UNJ")
            if d_view > TOL_INV:
                flags.append("VIEW")
            # Jitter tolerance, not bit-identity: the engine uploads the
            # unjittered matrix into g_view_proj for its late post passes.
            if d_same > TOL_UNJ:
                flags.append("SAME")
            bad += bool(flags)
            print("   #%06d  %5d  %.2e   %.2e   %.2e   %.2e   %s" % (
                opn, bi, d_inv, d_unj, d_view, d_same, " ".join(flags) if flags else "ok"))
        total_bad += bad
        checked += len(rows)
        print("   -> %s" % ("ONE camera in every main-view upload" if bad == 0 else
                           "%d upload(s) break the one-camera invariants" % bad))

    if checked == 0:
        print("\nRESULT: NOTHING CHECKED - every upload was a light view. Not a clean result.")
        return 1
    print("\nRESULT: %s (%d main-view upload(s) checked)" % (
        "clean - every frame has one viewpoint" if total_bad == 0 else
        "%d violating upload(s) across %d frame(s)" % (total_bad, len(frames)), checked))
    return 0 if total_bad == 0 else 2


if __name__ == "__main__":
    sys.exit(main())
