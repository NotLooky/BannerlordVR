#!/usr/bin/env python3
"""
framemap_analyze.py - read a BannerlordVR frame map and say what the frame is.

    python tools/framemap_analyze.py [capture_dir] > report.txt

With no argument, the newest folder under
Documents/Mount and Blade II Bannerlord/Logs/BannerlordVR.FrameMap is used.

WHAT IT ANSWERS
  1. The frame as PASSES: consecutive ops that share a render-target/UAV set,
     in order, with their target shapes, depth state, shaders, constant buffers
     and the surfaces they read.
  2. RESOURCE FLOW: which surfaces are read before they are written in a frame
     (history - they must be kept PER EYE under native stereo) and which are
     produced and consumed inside it (transient - a second copy suffices).
  3. CAMERA LANES: in every constant-buffer upload, which lanes hold the engine
     camera's position, a world->camera matrix, or an inverse (camera->world)
     matrix - tested against the camera recovered for that frame, with no
     assumption about where they sit.
  4. EYE DELTA: under AFR, frames alternate eyes. The same upload compared
     left-vs-right and left-vs-left separates "differs between the eyes" from
     "drifts with time" - which is exactly the set native stereo must rewrite.
  5. SHADER READS: from each shader's disassembly, which constant-buffer lanes it
     reads - so the passes that consume camera lanes are named, not guessed.
"""
import math
import os
import re
import struct
import sys
from collections import Counter, OrderedDict, defaultdict

DRAW_KINDS = {"DrawIndexed", "DrawIndexedInstanced", "Draw", "DrawInstanced",
              "DrawAuto", "DrawIndexedInstancedIndirect", "DrawInstancedIndirect"}
COMPUTE_KINDS = {"Dispatch", "DispatchIndirect"}

OP_RE = re.compile(r'^#(\d+) f(\d+) th(\d+)( \[ours\])? (\S+)(.*)$')


# ---------------------------------------------------------------------------
# parsing
# ---------------------------------------------------------------------------

def parse_binds(text):
    d = {"sh": {}, "rt": {}, "ds": None, "pu": {}, "cb": defaultdict(dict),
         "srv": defaultdict(dict), "uav": {}, "vp": None, "dss": None, "bl": None,
         "topo": None}
    if not text:
        return d
    for seg in [s.strip() for s in text.split(" | ")]:
        if not seg:
            continue
        if seg.startswith("OM"):
            for tok in seg.split()[1:]:
                k, _, v = tok.partition("=")
                rid = int(v.split(":R")[1]) if ":R" in v else None
                if k.startswith("rt"):
                    d["rt"][int(k[2:])] = rid
                elif k == "ds":
                    d["ds"] = rid
                elif k.startswith("pu"):
                    d["pu"][int(k[2:])] = rid
        elif seg.startswith("VP "):
            d["vp"] = seg[3:]
        elif seg.startswith("DSS"):
            m = re.search(r'en(\d) wr(\d) fn(\d+)', seg)
            if m:
                d["dss"] = (int(m.group(1)), int(m.group(2)), int(m.group(3)))
        elif seg.startswith("BL"):
            d["bl"] = seg[3:]
        elif seg.startswith("TOPO"):
            d["topo"] = int(seg.split()[1])
        elif seg.startswith("cb."):
            stage = seg[3:5]
            for tok in seg.split()[1:]:
                m = re.match(r'b(\d+)=R(\d+)(?:\[(\d+)\+(\d+)\])?', tok)
                if m:
                    first = int(m.group(3)) if m.group(3) else 0
                    d["cb"][stage][int(m.group(1))] = (int(m.group(2)), first)
        elif seg.startswith("srv."):
            stage = seg[4:6]
            for tok in seg.split()[1:]:
                m = re.match(r't(\d+)=V(\d+):R(\d+)', tok)
                if m:
                    d["srv"][stage][int(m.group(1))] = int(m.group(3))
        elif seg.startswith("uav.CS"):
            for tok in seg.split()[1:]:
                m = re.match(r'u(\d+)=V(\d+):R(\d+)', tok)
                if m:
                    d["uav"][int(m.group(1))] = int(m.group(3))
        else:
            for tok in seg.split():
                m = re.match(r'([VHDGPC]S)=S(\d+)', tok)
                if m:
                    d["sh"][m.group(1)] = int(m.group(2))
    return d


def parse_ops(path):
    ops, cams = [], defaultdict(list)
    frame = 0
    with open(path, encoding="utf-8", errors="replace") as f:
        for line in f:
            line = line.rstrip("\n")
            if line.startswith("=== FRAME"):
                frame = int(re.match(r'=== FRAME (\d+)', line).group(1))
                continue
            if line.startswith("CAMERA"):
                cams[frame].append(line)
                continue
            m = OP_RE.match(line)
            if not m:
                continue
            rest = m.group(6)
            args, _, binds = rest.partition("||")
            op = {"seq": int(m.group(1)), "frame": int(m.group(2)), "th": int(m.group(3)),
                  "ours": bool(m.group(4)), "kind": m.group(5), "args": args.strip()}
            op.update(parse_binds(binds))
            ops.append(op)
    return ops, cams


def parse_resources(path):
    res = {}
    with open(path, encoding="utf-8", errors="replace") as f:
        for line in f:
            m = re.match(r'R(\d+) (\S+)(.*)', line.strip())
            if not m:
                continue
            r = {"id": int(m.group(1)), "type": m.group(2), "text": line.strip()}
            rest = m.group(3)
            dm = re.search(r' (\d+)x(\d+)', rest)
            if dm:
                r["w"], r["h"] = int(dm.group(1)), int(dm.group(2))
            for k in ("fmt", "bytes", "bind", "usage", "misc", "mips", "arr", "stride"):
                km = re.search(r'\b%s=(0x[0-9A-Fa-f]+|\d+)' % k, rest)
                if km:
                    r[k] = int(km.group(1), 0)
            r["eye"] = "EYE0" if "EYE0" in rest else ("EYE1" if "EYE1" in rest else "")
            res[r["id"]] = r
    return res


def parse_cb(path):
    blobs = []
    with open(path, "rb") as f:
        data = f.read()
    i = 0
    while i + 24 <= len(data):
        magic, seq, frame, rid, off, n = struct.unpack_from("<6I", data, i)
        if magic != 0x50554243:
            print("cb.bin: bad magic at", i, file=sys.stderr)
            break
        i += 24
        raw = data[i:i + n]
        i += n
        nf = n // 4
        floats = struct.unpack_from("<%df" % nf, raw, 0) if nf else ()
        blobs.append({"seq": seq, "frame": frame, "res": rid, "off": off, "n": n,
                      "f": floats})
    return blobs


def camera_positions(cams):
    """Engine camera position per frame, from the recovered-camera line, falling
    back to row 3 of the published eye camera."""
    out = {}
    for fr, lines in cams.items():
        pos = None
        for ln in lines:
            m = re.search(r'engine-recovered pos=\(([-\d.e]+) ([-\d.e]+) ([-\d.e]+)\)', ln)
            if m:
                pos = tuple(float(m.group(k)) for k in (1, 2, 3))
        if pos is None:
            for ln in lines:
                m = re.search(r'published eye=\S+ valid=1 eyeCamera=\[([^\]]*)\]', ln)
                if m:
                    v = [float(x) for x in m.group(1).split()]
                    if len(v) == 16:
                        pos = (v[12], v[13], v[14])
        eye = None
        for ln in lines:
            m = re.search(r'published eye=(-?\d+)', ln)
            if m:
                eye = int(m.group(1))
        out[fr] = {"pos": pos, "eye": eye}
    return out


# ---------------------------------------------------------------------------
# helpers
# ---------------------------------------------------------------------------

def shape(res, rid):
    r = res.get(rid)
    if r is None:
        return "R%s(?)" % rid
    if r["type"] == "BUF":
        return "R%d(buf %dB bind0x%X)" % (rid, r.get("bytes", 0), r.get("bind", 0))
    s = "R%d(%dx%d f%d%s%s)" % (rid, r.get("w", 0), r.get("h", 0), r.get("fmt", 0),
                               " m%d" % r["mips"] if r.get("mips", 1) > 1 else "",
                               " " + r["eye"] if r["eye"] else "")
    return s


def writes_of(op):
    w = set()
    if op["kind"] in DRAW_KINDS:
        w.update(v for v in op["rt"].values() if v is not None)
        if op["ds"] is not None and op["dss"] and op["dss"][1]:
            w.add(op["ds"])
        w.update(v for v in op["pu"].values() if v is not None)
    elif op["kind"] in COMPUTE_KINDS:
        w.update(op["uav"].values())
    else:
        a = op["args"]
        for key in ("dst=R",):
            for m in re.finditer(r'dst=R(\d+)', a):
                w.add(int(m.group(1)))
        if op["kind"] in ("ClearRTV", "ClearDSV", "ClearUAVUint", "ClearUAVFloat"):
            m = re.search(r'V\d+:R(\d+)', a)
            if m:
                w.add(int(m.group(1)))
        if op["kind"] in ("ClearView", "DiscardView", "DiscardView1"):
            m = re.search(r'view-of-R(\d+)', a)
            if m:
                w.add(int(m.group(1)))
        if op["kind"] == "UPDATE":
            m = re.match(r'R(\d+)', a)
            if m:
                w.add(int(m.group(1)))
    return w


def reads_of(op):
    r = set()
    for stage, slots in op["srv"].items():
        r.update(slots.values())
    if op["kind"] in DRAW_KINDS and op["ds"] is not None and op["dss"] and op["dss"][0]:
        r.add(op["ds"])
    for m in re.finditer(r'src=R(\d+)', op["args"]):
        r.add(int(m.group(1)))
    for m in re.finditer(r'args=R(\d+)', op["args"]):
        r.add(int(m.group(1)))
    if op["kind"] == "GenerateMips":
        m = re.search(r'V\d+:R(\d+)', op["args"])
        if m:
            r.add(int(m.group(1)))
    if op["kind"] == "NGX-EVALUATE":
        for m in re.finditer(r'(\w[\w.]*)=R(\d+)', op["args"]):
            if m.group(1) != "Output" and int(m.group(2)) >= 0:
                r.add(int(m.group(2)))
    return r


def ngx_writes(op):
    m = re.search(r'Output=R(\d+)', op["args"])
    return {int(m.group(1))} if m else set()


# ---------------------------------------------------------------------------
# 1. passes
# ---------------------------------------------------------------------------

def pass_key(op):
    if op["kind"] in DRAW_KINDS:
        return ("G", tuple(sorted(op["rt"].items())), op["ds"])
    if op["kind"] in COMPUTE_KINDS:
        return ("C", tuple(sorted(op["uav"].items())))
    return ("X", op["kind"])


def report_passes(ops, res, frame, out):
    out.append("\n" + "=" * 78)
    out.append("1. PASSES IN FRAME %d" % frame)
    out.append("=" * 78)
    fops = [o for o in ops if o["frame"] == frame]
    groups = []
    for o in fops:
        if o["kind"] in ("CB-UPLOAD", "MAP", "Begin", "End", "SetPredication"):
            continue
        k = pass_key(o)
        if groups and groups[-1][0] == k and k[0] != "X":
            groups[-1][1].append(o)
        else:
            groups.append((k, [o]))

    for gi, (k, g) in enumerate(groups):
        o0 = g[0]
        if k[0] == "X":
            out.append("  P%03d  #%06d  %-22s %s%s" % (gi, o0["seq"], o0["kind"], o0["args"][:150],
                                                     "  [ours]" if o0["ours"] else ""))
            continue
        kinds = Counter(o["kind"] for o in g)
        shaders = defaultdict(set)
        cbs, srvs = set(), set()
        dss = set()
        for o in g:
            for st, s in o["sh"].items():
                shaders[st].add(s)
            for st, slots in o["cb"].items():
                for slot, (rid, first) in slots.items():
                    cbs.add(rid)
            for st, slots in o["srv"].items():
                srvs.update(slots.values())
            if o["dss"]:
                dss.add(o["dss"])
        if k[0] == "G":
            tgt = " ".join("rt%d=%s" % (i, shape(res, r)) for i, r in k[1])
            if k[2] is not None:
                tgt += " ds=" + shape(res, k[2])
        else:
            tgt = "UAV " + " ".join("u%d=%s" % (i, shape(res, r)) for i, r in k[1])
        out.append("  P%03d  #%06d  %s x%d  %s%s" % (
            gi, o0["seq"], "GFX" if k[0] == "G" else "CS ", len(g),
            ",".join("%s:%d" % kv for kv in kinds.items()), "  [ours]" if o0["ours"] else ""))
        out.append("        targets: %s" % tgt)
        if dss:
            out.append("        depth(en,wr,fn): %s   viewport: %s" % (sorted(dss), o0["vp"]))
        out.append("        shaders: %s" % "  ".join("%s x%d" % (st, len(v)) for st, v in shaders.items()))
        if cbs:
            out.append("        cbs: %s" % " ".join(shape(res, r) for r in sorted(cbs)))
        if srvs:
            s = sorted(srvs)
            out.append("        reads: %s%s" % (" ".join(shape(res, r) for r in s[:16]),
                                                " ...(+%d)" % (len(s) - 16) if len(s) > 16 else ""))


# ---------------------------------------------------------------------------
# 2. resource flow
# ---------------------------------------------------------------------------

def report_flow(ops, res, frame, out):
    out.append("\n" + "=" * 78)
    out.append("2. RESOURCE FLOW IN FRAME %d (textures only)" % frame)
    out.append("=" * 78)
    first = {}
    written, read = set(), set()
    for o in ops:
        if o["frame"] != frame:
            continue
        w = writes_of(o) | (ngx_writes(o) if o["kind"] == "NGX-EVALUATE" else set())
        r = reads_of(o)
        for rid in r:
            if rid not in first:
                first[rid] = ("R", o["seq"])
            read.add(rid)
        for rid in w:
            if rid not in first:
                first[rid] = ("W", o["seq"])
            written.add(rid)

    history = [rid for rid, (t, _) in first.items()
               if t == "R" and rid in written and res.get(rid, {}).get("type") == "TEX2D"]
    const_in = [rid for rid, (t, _) in first.items()
                if t == "R" and rid not in written and res.get(rid, {}).get("type") == "TEX2D"
                and res[rid].get("bind", 0) & 0x20]    # RTV-capable but never written this frame
    transient = [rid for rid, (t, _) in first.items()
                 if t == "W" and res.get(rid, {}).get("type") == "TEX2D"]

    out.append("  READ BEFORE WRITTEN, then written (HISTORY - must be per eye):")
    for rid in sorted(history):
        out.append("    %s  first read #%06d" % (shape(res, rid), first[rid][1]))
    out.append("  READ, render-target-capable, NEVER written this frame (history kept "
               "across frames, or written by the other eye's frame):")
    for rid in sorted(const_in):
        out.append("    %s  first read #%06d" % (shape(res, rid), first[rid][1]))
    out.append("  WRITTEN FIRST (transient within the frame): %d textures" % len(transient))
    for rid in sorted(transient):
        out.append("    %s  first write #%06d" % (shape(res, rid), first[rid][1]))


# ---------------------------------------------------------------------------
# 3. camera lanes
# ---------------------------------------------------------------------------

def mat_rows(f, i, transpose):
    m = [list(f[i + 4 * r:i + 4 * r + 4]) for r in range(4)]
    if transpose:
        m = [[m[c][r] for c in range(4)] for r in range(4)]
    return m


def classify_matrix(m, c):
    """Tests a 4x4 (row-vector convention, rows as given) against camera c."""
    labels = []
    if any(not math.isfinite(x) for row in m for x in row):
        return labels
    mx = max(abs(x) for row in m for x in row)
    if mx < 1e-6 or mx > 1e7:
        return labels
    v = [sum(([c[0], c[1], c[2], 1.0])[r] * m[r][k] for r in range(4)) for k in range(4)]
    mag = [sum(abs(([c[0], c[1], c[2], 1.0])[r] * m[r][k]) for r in range(4)) for k in range(4)]
    tol = [max(1e-4 * mg, 1e-5) for mg in mag]
    upper = max(abs(m[r][k]) for r in range(3) for k in range(3))
    if upper > 1e-6 and all(mg > 1e-3 for mg in mag[:2]):
        if abs(v[0]) < tol[0] and abs(v[1]) < tol[1] and abs(v[3]) < tol[3]:
            labels.append("WORLD->CLIP(VP)")
        elif abs(v[0]) < tol[0] and abs(v[1]) < tol[1] and abs(v[2]) < tol[2] and abs(v[3] - 1) < 1e-3:
            labels.append("WORLD->VIEW")
    r2 = m[2]
    if abs(r2[3]) > 1e-9:
        p = [r2[k] / r2[3] for k in range(3)]
        if all(abs(p[k] - c[k]) < 0.05 for k in range(3)):
            labels.append("CLIP->WORLD(invVP, row2)")
    r3 = m[3]
    if abs(r3[3] - 1.0) < 1e-3 and all(abs(r3[k] - c[k]) < 0.05 for k in range(3)):
        labels.append("VIEW->WORLD(invView, row3)")
    if upper > 1e-3 and abs(r3[0]) < 1e-6 and abs(r3[1]) < 1e-6 and abs(r3[3]) < 1e-6 and abs(r3[2]) > 1e-6:
        labels.append("REL->CLIP(no translation)")
    return labels


def scan_blob(b, c):
    found = []
    f = b["f"]
    n = len(f)
    base = b["off"]
    for i in range(0, n - 2):
        if all(math.isfinite(f[i + k]) for k in range(3)) and \
           all(abs(f[i + k] - c[k]) < 0.02 for k in range(3)):
            found.append((base + 4 * i, "CAMPOS"))
    for i in range(0, n - 15, 4):
        for tr in (False, True):
            for lab in classify_matrix(mat_rows(f, i, tr), c):
                found.append((base + 4 * i, lab + (" T" if tr else "")))
    return found


def report_camera_lanes(blobs, cams, res, out):
    out.append("\n" + "=" * 78)
    out.append("3. CAMERA LANES (per constant buffer, all frames)")
    out.append("=" * 78)
    agg = defaultdict(Counter)
    uploads = Counter()
    for b in blobs:
        cam = cams.get(b["frame"], {}).get("pos")
        uploads[b["res"]] += 1
        if cam is None:
            continue
        for off, lab in scan_blob(b, cam):
            agg[b["res"]][(off, lab)] += 1
    for rid in sorted(agg, key=lambda r: -sum(agg[r].values())):
        out.append("  %s  uploads=%d" % (shape(res, rid), uploads[rid]))
        for (off, lab), n in sorted(agg[rid].items()):
            out.append("      offset %5d  %-30s x%d" % (off, lab, n))
    return agg


# ---------------------------------------------------------------------------
# 4. eye delta
# ---------------------------------------------------------------------------

def report_eye_delta(blobs, cams, res, cam_agg, out):
    out.append("\n" + "=" * 78)
    out.append("4. EYE DELTA - lanes that differ between the eyes but not across time")
    out.append("=" * 78)
    frames = sorted(cams)
    eyes = {fr: cams[fr].get("eye") for fr in frames}
    out.append("  frames/eyes: %s" % ", ".join("f%d=eye%s" % (fr, eyes[fr]) for fr in frames))
    same, cross = None, None
    for a in frames:
        for b in frames:
            if b <= a:
                continue
            if eyes[a] is not None and eyes[a] == eyes[b] and same is None:
                same = (a, b)
    if same:
        for b in frames:
            if b > same[0] and eyes.get(b) is not None and eyes[b] != eyes[same[0]]:
                cross = (same[0], b)
                break
    if not same or not cross:
        out.append("  need two frames of one eye and one of the other (frame_map_frames >= 3 "
                   "under AFR); skipped.")
        return
    out.append("  time pair f%d-f%d (same eye), eye pair f%d-f%d" % (same + cross))
    byres = defaultdict(lambda: defaultdict(list))
    for b in blobs:
        byres[b["res"]][b["frame"]].append(b)
    for rid, per in byres.items():
        a, b, c2 = per.get(same[0], []), per.get(same[1], []), per.get(cross[1], [])
        n = min(len(a), len(b), len(c2))
        if n == 0:
            continue
        lanes = Counter()
        for k in range(n):
            if not (len(a[k]["f"]) == len(b[k]["f"]) == len(c2[k]["f"])):
                continue
            for i, (x, y, z) in enumerate(zip(a[k]["f"], b[k]["f"], c2[k]["f"])):
                if not (math.isfinite(x) and math.isfinite(y) and math.isfinite(z)):
                    continue
                dt, de = abs(y - x), abs(z - x)
                scale = max(abs(x), 1e-3)
                if de > 1e-4 * scale + 1e-6 and de > 4.0 * dt:
                    lanes[a[k]["off"] + 4 * i] += 1
        if not lanes:
            continue
        known = {off for (off, _lab) in cam_agg.get(rid, {})}
        out.append("  %s  %d upload(s) compared" % (shape(res, rid), n))
        row, last = [], None
        for off in sorted(lanes):
            row.append("%d%s" % (off, "" if any(k <= off < k + 64 for k in known) else "?"))
        out.append("      eye-dependent lane offsets (? = not inside a lane section 3 "
                   "identified): %s" % " ".join(row[:120]))


# ---------------------------------------------------------------------------
# 5. shader reads
# ---------------------------------------------------------------------------

CB_READ_RE = re.compile(r'(?<![a-z])cb(\d+)\[(\d+)\]\.([xyzw]+)')
CB_DYN_RE = re.compile(r'(?<![a-z])cb(\d+)\[[^\]]*r\d')


def parse_shader_reads(shdir):
    reads = {}
    if not os.path.isdir(shdir):
        return reads
    for name in os.listdir(shdir):
        m = re.match(r'S(\d+)_', name)
        if not m:
            continue
        sid = int(m.group(1))
        lanes = defaultdict(set)
        dyn = set()
        with open(os.path.join(shdir, name), encoding="utf-8", errors="replace") as f:
            for line in f:
                if line.lstrip().startswith("dcl_"):
                    continue
                for mm in CB_READ_RE.finditer(line):
                    slot, reg = int(mm.group(1)), int(mm.group(2))
                    for ch in mm.group(3):
                        lanes[slot].add(reg * 16 + "xyzw".index(ch) * 4)
                for mm in CB_DYN_RE.finditer(line):
                    dyn.add(int(mm.group(1)))
        reads[sid] = (lanes, dyn)
    return reads


def report_shader_reads(ops, res, reads, cam_agg, out):
    out.append("\n" + "=" * 78)
    out.append("5. WHO READS THE CAMERA - shaders reading lanes section 3 identified")
    out.append("=" * 78)
    if not reads:
        out.append("  no disassembly available.")
        return
    cam_lanes = defaultdict(dict)
    for rid, cnt in cam_agg.items():
        for (off, lab), _n in cnt.items():
            span = 12 if lab == "CAMPOS" else 64
            for o in range(off, off + span, 4):
                cam_lanes[rid][o] = lab
    hits = defaultdict(lambda: defaultdict(set))
    uses = Counter()
    for o in ops:
        if o["frame"] != 0:
            continue
        for st, sid in o["sh"].items():
            if sid not in reads:
                continue
            lanes, dyn = reads[sid]
            for slot, (rid, first) in o["cb"].get(st, {}).items():
                base = first * 16
                labs = set()
                for lane in lanes.get(slot, ()):
                    lab = cam_lanes.get(rid, {}).get(base + lane)
                    if lab:
                        labs.add("%s@%d" % (lab, base + lane - (base + lane) % 16))
                if labs:
                    hits[(st, sid)][rid].update(labs)
                    uses[(st, sid)] += 1
                if slot in dyn and rid in cam_lanes:
                    hits[(st, sid)][rid].add("DYNAMIC-INDEXED")
    for (st, sid), per in sorted(hits.items(), key=lambda kv: -uses[kv[0]]):
        out.append("  %s S%d  (%d op(s) in frame 0)" % (st, sid, uses[(st, sid)]))
        for rid, labs in per.items():
            out.append("      %s: %s" % (shape(res, rid), " ".join(sorted(labs))[:300]))


# ---------------------------------------------------------------------------

def newest_capture():
    docs = os.path.join(os.path.expanduser("~"), "Documents", "Mount and Blade II Bannerlord",
                        "Logs", "BannerlordVR.FrameMap")
    if not os.path.isdir(docs):
        return None
    subs = [os.path.join(docs, d) for d in os.listdir(docs)
            if os.path.isdir(os.path.join(docs, d))]
    return max(subs, key=os.path.getmtime) if subs else None


def main():
    cap = sys.argv[1] if len(sys.argv) > 1 else newest_capture()
    if not cap or not os.path.isdir(cap):
        print("no capture folder found", file=sys.stderr)
        return 1

    ops, cam_lines = parse_ops(os.path.join(cap, "ops.txt"))
    res = parse_resources(os.path.join(cap, "resources.txt"))
    blobs = parse_cb(os.path.join(cap, "cb.bin"))
    cams = camera_positions(cam_lines)
    reads = parse_shader_reads(os.path.join(cap, "shaders"))

    out = ["BannerlordVR frame map report: %s" % cap]
    frames = sorted({o["frame"] for o in ops})
    for fr in frames:
        fo = [o for o in ops if o["frame"] == fr]
        out.append("frame %d: %d ops %s  threads %s  camera %s eye %s" % (
            fr, len(fo), dict(Counter(o["kind"] for o in fo).most_common(12)),
            dict(Counter(o["th"] for o in fo)), cams.get(fr, {}).get("pos"),
            cams.get(fr, {}).get("eye")))

    report_passes(ops, res, frames[0] if frames else 0, out)
    report_flow(ops, res, frames[0] if frames else 0, out)
    cam_agg = report_camera_lanes(blobs, cams, res, out)
    report_eye_delta(blobs, cams, res, cam_agg, out)
    report_shader_reads(ops, res, reads, cam_agg, out)

    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    print("\n".join(out))
    return 0


if __name__ == "__main__":
    sys.exit(main())
