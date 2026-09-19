#!/usr/bin/env python3
"""
stereo_replay.py - replay a frame map through native stereo's duplication rule.

    "C:\\Program Files\\LibreOffice\\program\\python.exe" tools/stereo_replay.py [capture_dir]
        [--rule 181|165] [--seed-div R96,R178] [--frame N]

Run with PYTHONDONTWRITEBYTECODE=1 to keep tools/__pycache__ out of the tree.

WHY THIS EXISTS
  TEST 165's live log said 93-97% of the frame ran twice; this replay of that
  run's own capture said 57.7%. The difference was the shadow atlas - a
  four-slice array cleared one slice at a time - stuck DIVERGED under a
  per-resource state. Nothing in the live counters could show that. So before a
  rule change ships, it is replayed here against a real frame.

WHAT IT MIRRORS (stereo_dup.cpp, TEST 181)
  - camera uploads: a 1760/128/224-byte buffer is the MAIN VIEW for the rest of
    the frame when a world->clip or world->view site verifies against the
    published eye; any other upload of it clears that mark
  - an op is view-dependent when a DECLARED constant slot holds the main view,
    a DECLARED texture slot views a diverged subresource, it draws into or
    depth-tests a diverged subresource, or (compute) a declared UAV is diverged
  - state per subresource (mip x slice): div / sync / stale; clears reset the
    cleared view's subresources; copies and GenerateMips carry state as in C++
  - NGX-EVALUATE (rule 181): the eye-1 evaluation - the output is DIVERGED when
    the colour input is, otherwise out of date
  - [ours] ops are ignored

--rule 165 replays the previous build instead: one state bit per resource, a
clear only resets a surface with one subresource, and DLSS is invisible.
--seed-div marks resources DIVERGED before the first op, the way a taint from
earlier in a mission would; rule 181 must shake it off within a frame.
"""
import argparse
import math
import os
import re
import sys
from collections import Counter, defaultdict

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import framemap_analyze as FA  # noqa: E402

LAYOUT_FWD = {1760: (112, 240, 304), 128: (0,), 224: (64,)}
NEAR_M = 2.0     # rule 182: a camera this close to a published eye is the player's
STAGES_DRAW = ("VS", "HS", "DS", "GS", "PS")
USAGE_DEFAULT = 0


# ---------------------------------------------------------------------------
# parsing
# ---------------------------------------------------------------------------

def parse_views(path):
    views = {}
    with open(path, encoding="utf-8", errors="replace") as f:
        for line in f:
            m = re.match(r'V(\d+) (SRV|RTV|DSV|UAV) R(\d+) fmt=(\d+) dim=(\d+)(.*)', line.strip())
            if not m:
                continue
            rest = m.group(6).split("(buffer:")[0]
            kv = {k: int(v, 0) for k, v in
                  re.findall(r'\b(a|b|c|d|mip|first|size|flags)=(0x[0-9A-Fa-f]+|\d+)', rest)}
            kv.update(kind=m.group(2), rid=int(m.group(3)), fmt=int(m.group(4)),
                      dim=int(m.group(5)))
            views[int(m.group(1))] = kv
    return views


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
            m = FA.OP_RE.match(line)
            if not m:
                continue
            args, _, binds = m.group(6).partition("||")
            op = {"seq": int(m.group(1)), "frame": int(m.group(2)), "ours": bool(m.group(4)),
                  "kind": m.group(5), "args": args.strip(), "sh": {}, "rt": {}, "ds": None,
                  "uav": {}, "srv": defaultdict(dict), "cb": defaultdict(dict), "dss": None}
            for seg in (s.strip() for s in binds.split(" | ")):
                if seg.startswith("OM"):
                    for tok in seg.split()[1:]:
                        k, _, v = tok.partition("=")
                        mm = re.match(r'V(\d+):R(\d+)', v)
                        if not mm:
                            continue
                        pair = (int(mm.group(1)), int(mm.group(2)))
                        if k.startswith("rt"):
                            op["rt"][int(k[2:])] = pair
                        elif k == "ds":
                            op["ds"] = pair
                elif seg.startswith("DSS"):
                    mm = re.search(r'en(\d) wr(\d) fn(\d+)(?: st(\d))?', seg)
                    if mm:
                        op["dss"] = (int(mm.group(1)), int(mm.group(2)), int(mm.group(4) or 0))
                elif seg.startswith("cb."):
                    stage = seg[3:5]
                    for tok in seg.split()[1:]:
                        mm = re.match(r'b(\d+)=R(\d+)', tok)
                        if mm:
                            op["cb"][stage][int(mm.group(1))] = int(mm.group(2))
                elif seg.startswith("srv."):
                    stage = seg[4:6]
                    for tok in seg.split()[1:]:
                        mm = re.match(r't(\d+)=V(\d+):R(\d+)', tok)
                        if mm:
                            op["srv"][stage][int(mm.group(1))] = (int(mm.group(2)), int(mm.group(3)))
                elif seg.startswith("uav.CS"):
                    for tok in seg.split()[1:]:
                        mm = re.match(r'u(\d+)=V(\d+):R(\d+)', tok)
                        if mm:
                            op["uav"][int(mm.group(1))] = (int(mm.group(2)), int(mm.group(3)))
                else:
                    for tok in seg.split():
                        mm = re.match(r'([VHDGPC]S)=S(\d+)', tok)
                        if mm:
                            op["sh"][mm.group(1)] = int(mm.group(2))
            ops.append(op)
    return ops, cams


def parse_decls(shdir):
    decl = {}
    if not os.path.isdir(shdir):
        return decl
    for fn in os.listdir(shdir):
        m = re.match(r'S(\d+)_', fn)
        if not m:
            continue
        d = {"cb": set(), "t": set(), "u": set()}
        with open(os.path.join(shdir, fn), encoding="utf-8", errors="replace") as f:
            for ln in f:
                ln = ln.strip()
                mm = re.match(r'dcl_constantbuffer CB(\d+)', ln)
                if mm:
                    d["cb"].add(int(mm.group(1)))
                mm = re.match(r'dcl_resource\w*\b.*?\bt(\d+)', ln)
                if mm:
                    d["t"].add(int(mm.group(1)))
                mm = re.match(r'dcl_uav\w*\b.*?\bu(\d+)', ln)
                if mm:
                    d["u"].add(int(mm.group(1)))
        decl[int(m.group(1))] = d
    return decl


def eye_positions(cams):
    out = {}
    for fr, lines in cams.items():
        for ln in lines:
            m = re.search(r'published eye=\S+ valid=1 eyeCamera=\[([^\]]*)\]', ln)
            if m:
                v = [float(x) for x in m.group(1).split()]
                out[fr] = (v[12], v[13], v[14])
    return out


def is_forward(f, i, c):
    m = f[i:i + 16]
    if len(m) < 16 or any(not math.isfinite(x) for x in m):
        return False
    p = (c[0], c[1], c[2], 1.0)
    v = [sum(p[r] * m[r * 4 + k] for r in range(4)) for k in range(4)]
    g = [sum(abs(p[r] * m[r * 4 + k]) for r in range(4)) for k in range(4)]
    t = [3e-5 * g[k] + 1e-4 for k in range(4)]
    if abs(v[3]) < t[3] and abs(v[0]) < t[0] and abs(v[1]) < t[1] and g[3] > 1e-3:
        return True
    return (abs(v[0]) < t[0] and abs(v[1]) < t[1] and abs(v[2]) < t[2] and
            abs(v[3] - 1.0) < 1e-3 and (g[0] + g[1] + g[2]) > 1e-3)


# ---------------------------------------------------------------------------
# the rule
# ---------------------------------------------------------------------------

class Replay:
    def __init__(self, res, views, decl, rule):
        self.res, self.views, self.decl, self.rule = res, views, decl, rule
        self.state = {}          # rid -> [div, sync, stale]
        self.main = {}           # cb rid -> frame it holds the main view for
        self.light = {}          # cb rid -> frame it holds a light view carrying our position
        self.last_main = {}      # frame -> camera position of that frame's main view
        self.stats = Counter()
        self.readbacks = Counter()

    # -- subresources -------------------------------------------------------
    def info(self, rid):
        r = self.res.get(rid, {})
        if r.get("type") != "TEX2D":
            return 1, 1, True
        mips, arr = max(1, r.get("mips", 1)), max(1, r.get("arr", 1))
        return mips, arr, (self.rule == 165 or mips * arr > 64)

    def all_bits(self, rid):
        mips, arr, col = self.info(rid)
        return 1 if col else (1 << (mips * arr)) - 1

    def range_bits(self, rid, mip0, nmip, s0, ns):
        mips, arr, col = self.info(rid)
        if col:
            return 1
        if mip0 >= mips:
            mip0 = 0
        if nmip == 0 or nmip > mips - mip0:
            nmip = mips - mip0
        if s0 >= arr:
            s0 = 0
        if ns == 0 or ns > arr - s0:
            ns = arr - s0
        m = 0
        for s in range(s0, s0 + ns):
            for k in range(mip0, mip0 + nmip):
                m |= 1 << (k + s * mips)
        return m

    def sub_bit(self, rid, sub):
        mips, arr, col = self.info(rid)
        return 1 if col else (1 << sub if sub < 64 else 0)

    def mask(self, vid, rid):
        v = self.views.get(vid)
        allb = self.all_bits(rid)
        if v is None or self.info(rid)[2]:
            return allb
        k, d, g = v["kind"], v["dim"], (lambda key: v.get(key, 0))
        m = allb
        if k == "RTV":
            if d in (2, 4, 8):
                m = self.range_bits(rid, g("mip"), 1, 0, 1)
            elif d in (3, 5):
                m = self.range_bits(rid, g("mip"), 1, g("first"), g("size"))
            elif d == 6:
                m = self.range_bits(rid, 0, 1, 0, 1)
            elif d == 7:
                m = self.range_bits(rid, 0, 1, g("mip"), g("first"))
        elif k == "DSV":
            if d in (1, 3):
                m = self.range_bits(rid, g("mip"), 1, 0, 1)
            elif d in (2, 4):
                m = self.range_bits(rid, g("mip"), 1, g("first"), g("size"))
            elif d == 5:
                m = self.range_bits(rid, 0, 1, 0, 1)
            elif d == 6:
                m = self.range_bits(rid, 0, 1, g("mip"), g("first"))
        elif k == "UAV":
            if d in (2, 4, 8):
                m = self.range_bits(rid, g("a"), 1, 0, 1)
            elif d in (3, 5):
                m = self.range_bits(rid, g("a"), 1, g("b"), g("c"))
        else:
            if d in (2, 4, 8):
                m = self.range_bits(rid, g("a"), g("b"), 0, 1)
            elif d in (3, 5):
                m = self.range_bits(rid, g("a"), g("b"), g("c"), g("d"))
            elif d == 6:
                m = self.range_bits(rid, 0, 1, 0, 1)
            elif d == 7:
                m = self.range_bits(rid, 0, 1, g("a"), g("b"))
            elif d == 9:
                m = self.range_bits(rid, g("a"), g("b"), 0, 6)
            elif d == 10:
                m = self.range_bits(rid, g("a"), g("b"), g("c"), g("d") * 6)
        return m or allb

    def st(self, rid):
        return self.state.setdefault(rid, [0, 0, 0])

    def div(self, rid):
        s = self.state.get(rid)
        return s[0] if s else 0

    def twinnable(self, rid):
        return self.res.get(rid, {}).get("usage", 0) == USAGE_DEFAULT

    def mark_shared_write(self, rid, m):
        s = self.state.get(rid)
        if not s:
            return
        moved = (s[0] | s[1]) & m
        s[2] |= moved
        s[0] &= ~moved
        s[1] &= ~moved

    def prepare_and_mark_div(self, rid, m):
        s = self.st(rid)
        need = m & ~(s[0] | s[1])
        synced = 1 if need else 0
        s[0] |= m
        s[1] &= ~m
        s[2] &= ~m
        return synced

    # -- operations ---------------------------------------------------------
    def cb_upload(self, blob, eye, eyes_pub=None):
        n = blob["n"]
        if n not in LAYOUT_FWD or blob["off"] != 0 or eye is None:
            return
        fr = blob["frame"]
        if self.rule < 182:
            if any(is_forward(blob["f"], off // 4, eye) for off in LAYOUT_FWD[n]):
                self.main[blob["res"]] = fr
            else:
                self.main.pop(blob["res"], None)
            return

        # rule 182: the upload's OWN camera, gated by distance to the published eyes.
        f = blob["f"]
        pubs = [p for p in (eyes_pub or [eye]) if p is not None]
        self.light.pop(blob["res"], None)
        if n == 1760:
            own = (f[0], f[1], f[2])
            near = any(math.dist(own, p) <= NEAR_M for p in pubs)
            fwd = any(is_forward(f, off // 4, own) for off in LAYOUT_FWD[n])
            if fwd and near:
                self.main[blob["res"]] = fr
                self.last_main[fr] = own
                self.stats["1760 main"] += 1
                return
            self.main.pop(blob["res"], None)
            if near:
                self.light[blob["res"]] = fr
                self.stats["1760 light"] += 1
            else:
                self.stats["1760 other camera"] += 1
            return
        cands = [self.last_main.get(fr), self.last_main.get(fr - 1)] + pubs
        for c in cands:
            if c is not None and any(is_forward(f, off // 4, c) for off in LAYOUT_FWD[n]):
                self.main[blob["res"]] = fr
                self.stats["%d main" % n] += 1
                return
        self.main.pop(blob["res"], None)
        self.stats["%d not main" % n] += 1

    def clear(self, rid, m, whole):
        s = self.state.get(rid)
        if not s:
            return
        if not (s[0] | s[1] | s[2]) & m:
            return
        if not whole:
            return      # the twin is cleared too, but nothing of the old contents is known gone
        if self.rule == 165:
            mips = self.res.get(rid, {}).get("mips", 1)
            arr = self.res.get(rid, {}).get("arr", 1)
            if not (mips == 1 and arr == 1):
                return
        s[1] |= m
        s[0] &= ~m
        s[2] &= ~m

    def copy_resource(self, dst, src):
        ss = self.state.get(src)
        if ss and ss[0]:
            if not self.twinnable(dst):
                self.readbacks["copy into a staging/dynamic resource"] += 1
                return
            allb = self.all_bits(src)
            self.state[dst] = [ss[0] & allb, allb & ~ss[0], 0]
            return
        ds = self.state.get(dst)
        if ds and (ds[0] | ds[1]):
            ds[2] |= ds[0] | ds[1]
            ds[0] = ds[1] = 0

    def copy_region(self, dst, dsub, src, ssub):
        ss, ds = self.state.get(src), self.state.get(dst)
        sbit, dbit = self.sub_bit(src, ssub), self.sub_bit(dst, dsub)
        if ss and ss[0] & sbit:
            if not self.twinnable(dst):
                self.readbacks["region copy into a staging/dynamic resource"] += 1
                return
            d = self.st(dst)
            d[0] |= dbit
            d[1] &= ~dbit
            d[2] &= ~dbit

    def generate_mips(self, vid, rid):
        s = self.state.get(rid)
        if not s:
            return
        mips, arr, col = self.info(rid)
        if col:
            return
        m = self.mask(vid, rid)
        lower_all = new_div = new_sync = 0
        rest = m
        while rest:
            idx = (rest & -rest).bit_length() - 1
            slice_ = idx // mips
            slice_bits = sum(1 << (k + slice_ * mips) for k in range(mips))
            in_view = m & slice_bits
            rest &= ~slice_bits
            base = 1 << idx
            lower = in_view & ~base
            lower_all |= lower
            if s[0] & base:
                new_div |= lower
            elif s[1] & base:
                new_sync |= lower
        tracked_lower = (s[0] | s[1] | s[2]) & lower_all
        s[0] = (s[0] & ~lower_all) | new_div
        s[1] = (s[1] & ~lower_all) | new_sync
        s[2] = (s[2] & ~lower_all) | (tracked_lower & ~(new_div | new_sync))

    def ngx(self, op):
        if self.rule == 165:
            return "invisible to rule 165"
        out = re.search(r'Output=R(-?\d+)', op["args"])
        col = re.search(r'Color=R(-?\d+)', op["args"])
        if not out:
            return "no output"
        out, col = int(out.group(1)), int(col.group(1)) if col else -1
        if col >= 0 and self.div(col):
            self.state[out] = [self.all_bits(out), 0, 0]
            return "eye-1 evaluation: R%d DIVERGED" % out
        s = self.state.get(out)
        if s and (s[0] | s[1]):
            s[2] |= s[0] | s[1]
            s[0] = s[1] = 0
        return "colour R%d not per-eye: R%d left shared" % (col, out)

    def draw_or_dispatch(self, op):
        compute = op["kind"] in FA.COMPUTE_KINDS
        stages = ("CS",) if compute else STAGES_DRAW
        vd, refuse, why = False, False, None
        main_cb, light_cb = False, False
        cs_uav = None
        reads = []
        for sg in stages:
            sid = op["sh"].get(sg)
            if sid is None:
                continue
            dc = self.decl.get(sid)
            known = dc is not None
            if sg == "CS":
                cs_uav = dc["u"] if known else None
            elif sg == "PS" and known and dc["u"]:
                refuse = True
            if known and any(u >= 8 for u in dc["u"]):
                refuse = True
            for slot, rid in op["cb"].get(sg, {}).items():
                if known and slot not in dc["cb"]:
                    continue
                if self.main.get(rid) == op["frame"]:
                    vd = True
                    main_cb = True
                    why = why or "camera constants %s b%d" % (sg, slot)
                elif self.light.get(rid) == op["frame"]:
                    light_cb = True
            for slot, (vid, rid) in op["srv"].get(sg, {}).items():
                if known and slot not in dc["t"]:
                    continue
                m = self.mask(vid, rid)
                if self.div(rid) & m:
                    vd = True
                    reads.append((rid, m))
                    why = why or "per-eye texture %s t%d R%d" % (sg, slot, rid)
        outs = []
        ds_write = False
        if compute:
            for slot, (vid, rid) in op["uav"].items():
                if cs_uav is not None and slot not in cs_uav:
                    continue
                m = self.mask(vid, rid)
                outs.append((rid, m))
                if self.div(rid) & m:
                    vd = True
                    why = why or "reads a per-eye UAV u%d" % slot
        else:
            for slot, (vid, rid) in op["rt"].items():
                m = self.mask(vid, rid)
                outs.append((rid, m))
                if self.div(rid) & m:
                    vd = True
                    why = why or "draws into a per-eye target"
            if op["ds"] is not None:
                vid, rid = op["ds"]
                en, wr, stn = op["dss"] or (1, 1, 0)
                read, write = bool(en or stn), bool(en and wr)
                m = self.mask(vid, rid)
                if (read or write) and self.div(rid) & m:
                    vd = True
                    why = why or "tests a per-eye depth"
                if write:
                    outs.append((rid, m))
                    ds_write = True
        # rule 182: a light-space pass (its per-view constants are a light view carrying
        # the player's position) is shared by definition - shadows are one for both eyes -
        # whatever per-eye texture it samples.
        if self.rule >= 182 and vd and light_cb and not main_cb:
            self.stats["light-space op kept shared"] += 1
            vd = False
        if not vd:
            for rid, m in outs:
                self.mark_shared_write(rid, m)
            return False, None, 0
        if refuse or any(not self.twinnable(rid) for rid, _ in outs):
            for rid, m in outs:
                self.mark_shared_write(rid, m)
            return False, "refused", 0
        synced = 0
        if self.rule >= 182:
            for rid, m in reads:
                s = self.st(rid)
                need = m & ~(s[0] | s[1])
                if need:
                    self.stats["read re-sync (mixed subresource states)"] += 1
                    s[1] |= need
                    s[2] &= ~need
        for rid, m in outs:
            synced += self.prepare_and_mark_div(rid, m)
        return True, why, synced


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("capture", nargs="?")
    ap.add_argument("--rule", type=int, default=182, choices=(165, 181, 182))
    ap.add_argument("--seed-div", default="")
    ap.add_argument("--frame", type=int, default=-1, help="frame whose passes to list (default: last)")
    a = ap.parse_args()

    cap = a.capture or FA.newest_capture()
    ops, cams = parse_ops(os.path.join(cap, "ops.txt"))
    res = FA.parse_resources(os.path.join(cap, "resources.txt"))
    views = parse_views(os.path.join(cap, "views.txt"))
    blobs = FA.parse_cb(os.path.join(cap, "cb.bin"))
    decl = parse_decls(os.path.join(cap, "shaders"))
    eyes = eye_positions(cams)

    rp = Replay(res, views, decl, a.rule)
    for tok in filter(None, (t.strip() for t in a.seed_div.split(","))):
        rid = int(tok.lstrip("Rr"))
        rp.state[rid] = [rp.all_bits(rid), 0, 0]

    events = [(o["seq"], 1, o) for o in ops] + [(b["seq"], 0, b) for b in blobs]
    events.sort(key=lambda e: (e[0], e[1]))

    frames = sorted({o["frame"] for o in ops})
    show = a.frame if a.frame >= 0 else frames[-1]
    per = defaultdict(Counter)
    passes = []
    cur = None
    notes = []
    eye0 = [rid for rid, r in res.items() if r.get("eye") == "EYE0"]

    # The NGX-EVALUATE op is written BEFORE the real call, and NGX's own DiscardView /
    # ClearView of the output are issued INSIDE it - so they appear right after the op in
    # ops.txt but happen before its result exists. The eye-1 evaluation (which runs after
    # the real call returns) is therefore applied once those are past.
    NGX_INTERNAL = ("DiscardView", "DiscardView1", "ClearView", "DiscardResource")
    pending_ngx = None

    others = {}
    for fr, lines in cams.items():
        for ln in lines:
            m = re.search(r'other eye=\S+ valid=1 eyeCamera=\[([^\]]*)\]', ln)
            if m:
                v = [float(x) for x in m.group(1).split()]
                others[fr] = (v[12], v[13], v[14])

    for _seq, typ, e in events:
        if typ == 0:
            rp.cb_upload(e, eyes.get(e["frame"]), [eyes.get(e["frame"]), others.get(e["frame"])])
            continue
        o = e
        if o["ours"]:
            continue
        k, fr, args = o["kind"], o["frame"], o["args"]
        if pending_ngx is not None and k not in NGX_INTERNAL:
            notes.append((pending_ngx["frame"], "DLSS #%d: %s" % (pending_ngx["seq"], rp.ngx(pending_ngx))))
            pending_ngx = None
        if k in FA.DRAW_KINDS or k in FA.COMPUTE_KINDS:
            dup, why, synced = rp.draw_or_dispatch(o)
            per[fr]["ops"] += 1
            if dup:
                per[fr]["vd"] += 1
                per[fr]["syncs"] += synced
            elif why == "refused":
                per[fr]["refused"] += 1
            if o["ds"] and o["ds"][1] in (96, 178):
                per[fr]["cascade ops"] += 1
                per[fr]["cascade ops duplicated"] += int(dup)
            if any(rid in eye0 for _, rid in o["rt"].values()):
                notes.append((fr, "final composite #%d into the eye texture: %s" % (
                    o["seq"], "duplicated (%s)" % why if dup else "NOT duplicated")))
            if k in FA.COMPUTE_KINDS:
                key = ("C", tuple(sorted(rid for _, rid in o["uav"].values())))
            else:
                key = ("G", tuple(sorted(rid for _, rid in o["rt"].values())),
                       o["ds"][1] if o["ds"] else None)
            if cur is None or cur[0] != key or cur[1] != fr:
                cur = [key, fr, 0, 0, Counter(), o["seq"]]
                passes.append(cur)
            cur[2] += 1
            if dup:
                cur[3] += 1
                cur[4][why] += 1
        elif k in ("ClearRTV", "ClearDSV", "ClearUAVFloat", "ClearUAVUint"):
            mm = re.search(r'V(\d+):R(\d+)', args)
            if not mm:
                continue
            vid, rid = int(mm.group(1)), int(mm.group(2))
            whole = True
            if k == "ClearDSV":
                fl = re.search(r'flags=(0x[0-9A-Fa-f]+|\d+)', args)
                flags = int(fl.group(1), 0) if fl else 3
                has_stencil = views.get(vid, {}).get("fmt") in (20, 45)
                whole = bool(flags & 1) and (not has_stencil or bool(flags & 2))
            rp.clear(rid, rp.mask(vid, rid), whole)
        elif k == "ClearView":
            mm = re.search(r'view-of-R(\d+) rects=(\d+)', args)
            if mm:
                rid = int(mm.group(1))
                rp.clear(rid, rp.all_bits(rid), int(mm.group(2)) == 0)
        elif k == "CopyResource":
            mm = re.search(r'dst=R(\d+) src=R(\d+)', args)
            if mm:
                rp.copy_resource(int(mm.group(1)), int(mm.group(2)))
        elif k.startswith("CopySubresourceRegion"):
            mm = re.search(r'dst=R(\d+)/(\d+) .*?src=R(\d+)/(\d+)', args)
            if mm:
                rp.copy_region(int(mm.group(1)), int(mm.group(2)), int(mm.group(3)), int(mm.group(4)))
        elif k == "GenerateMips":
            mm = re.search(r'V(\d+):R(\d+)', args)
            if mm:
                rp.generate_mips(int(mm.group(1)), int(mm.group(2)))
        elif k == "NGX-EVALUATE":
            pending_ngx = o

    print("stereo_replay: %s  rule %d%s" % (cap, a.rule,
          ("  seeded DIVERGED: " + a.seed_div) if a.seed_div else ""))
    for fr in frames:
        c = per[fr]
        print("  frame %d: %d draw/dispatch, %d view-dependent (%.1f%%), %d refused, %d twin "
              "re-sync(s); shadow atlas ops %d, duplicated %d" % (
                  fr, c["ops"], c["vd"], 100.0 * c["vd"] / max(c["ops"], 1), c["refused"],
                  c["syncs"], c["cascade ops"], c["cascade ops duplicated"]))
        for nfr, text in notes:
            if nfr == fr:
                print("      " + text)
    if rp.readbacks:
        print("  readbacks of per-eye data left with eye 0's value: %s" % dict(rp.readbacks))
    if rp.stats:
        print("  classification: %s" % dict(rp.stats))

    print("\n  PASSES OF FRAME %d (ops, duplicated, first reason):" % show)
    for key, fr, n, vd, why, s0 in passes:
        if fr != show:
            continue
        if key[0] == "G":
            tgt = ",".join(FA.shape(res, r) for r in key[1]) or "-"
            ds = FA.shape(res, key[2]) if key[2] is not None else "-"
            label = "%s ds=%s" % (tgt, ds)
        else:
            label = "UAV " + ",".join(FA.shape(res, r) for r in key[1])
        print("    #%06d %4d %4d  %-78s %s" % (s0, n, vd, label[:78],
                                               why.most_common(1)[0][0] if why else ""))
    return 0


if __name__ == "__main__":
    sys.exit(main())
