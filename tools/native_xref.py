#!/usr/bin/env python3
"""
native_xref.py - cross-reference TaleWorlds.Native.dll (the rgl engine) without a
disassembler library: dumpbin's text disassembly plus the PE's own tables.

    python tools/native_xref.py build                 # dumpbin + index (once per game build)
    python tools/native_xref.py str <regex>           # strings, and the functions using them
    python tools/native_xref.py func <va>             # strings/calls/callers of the function at va
    python tools/native_xref.py dis <va> [maxlines]   # annotated disassembly of that function
    python tools/native_xref.py callers <va>          # direct calls/jumps/data pointers to va
    python tools/native_xref.py refs <va>             # every reference to va
    python tools/native_xref.py vtable <substr>       # RTTI class -> vtable slots
    python tools/native_xref.py grep <regex>          # regex over the disassembly, by function
    python tools/native_xref.py selftest              # the two anchors below must resolve

Run with LibreOffice's python (no real Python is installed):
    "C:\\Program Files\\LibreOffice\\program\\python.exe" tools/native_xref.py ...

Work files live in %BVR_RE_DIR% (default %TEMP%\\bvr_native_re): native.asm (~130 MB)
and native.idx.pickle. Addresses are absolute VAs at the DLL's preferred base
0x180000000, exactly as dumpbin prints them, so any claim can be re-checked with
    dumpbin /disasm:nobytes /range:0x<start>,0x<end> TaleWorlds.Native.dll

WHAT IS INDEXED

  strings    ASCII and UTF-16 runs in .rdata/.data. A reference into the middle of a
             string (the linker merges tails) resolves to the string containing it.
  functions  .pdata RUNTIME_FUNCTION chunks, chained unwind info followed back to
             the owning function, so a cold split-off chunk reports its real parent.
             Leaf functions without unwind info have no entry and print as '?'.
  imports    IAT slots -> dll!name, so 'call qword ptr [IAT]' reads as a name.
  rtti       TypeDescriptor -> CompleteObjectLocator -> vtable, one vtable per base
             offset. Virtual functions get a Class::vfN label.
  refs       every image address in an instruction operand, plus every aligned
             qword in .rdata/.data that points at code or at a string (vtables,
             option-name tables, dispatch tables).

The selftest anchors are facts established outside this tool: the engine_module.ini
parser must reference the option names shipped in Modules/Native/engine_module.ini,
and the NGX wrapper must reference "Jitter.Offset.X".
"""
import bisect
import glob
import os
import pickle
import re
import struct
import subprocess
import sys
from array import array

DEFAULT_DLL = (r"E:\Steam\steamapps\common\Mount & Blade II Bannerlord"
               r"\bin\Win64_Shipping_Client\TaleWorlds.Native.dll")
INDEX_VERSION = 3


def workdir():
    d = os.environ.get("BVR_RE_DIR") or os.path.join(os.environ.get("TEMP", "."), "bvr_native_re")
    os.makedirs(d, exist_ok=True)
    return d


# ----------------------------------------------------------------------------- PE

class PE:
    def __init__(self, path):
        self.path = path
        with open(path, "rb") as f:
            self.data = f.read()
        d = self.data
        pe = struct.unpack_from("<I", d, 0x3C)[0]
        if d[pe:pe + 4] != b"PE\0\0":
            raise SystemExit("not a PE file: " + path)
        coff = pe + 4
        nsec = struct.unpack_from("<H", d, coff + 2)[0]
        optsz = struct.unpack_from("<H", d, coff + 16)[0]
        opt = coff + 20
        if struct.unpack_from("<H", d, opt)[0] != 0x20B:
            raise SystemExit("not PE32+")
        self.base = struct.unpack_from("<Q", d, opt + 24)[0]
        self.dirs = [struct.unpack_from("<II", d, opt + 112 + 8 * i) for i in range(16)]
        self.sections = []
        sh = opt + optsz
        for _ in range(nsec):
            name = d[sh:sh + 8].rstrip(b"\0").decode("ascii", "replace")
            vsize, va, rawsz, rawptr = struct.unpack_from("<IIII", d, sh + 8)
            self.sections.append((name, self.base + va, vsize, rawptr, rawsz))
            sh += 40
        self.lo = self.base
        self.hi = max(s[1] + max(s[2], s[4]) for s in self.sections)

    def section(self, name):
        for s in self.sections:
            if s[0] == name:
                return s
        return None

    def section_blob(self, name):
        _, va, vsz, rp, rs = self.section(name)
        return va, self.data[rp:rp + min(vsz, rs)]

    def off(self, va):
        for _, sva, vsz, rp, rs in self.sections:
            if sva <= va < sva + max(vsz, rs):
                o = va - sva
                return rp + o if o < rs else None
        return None

    def read(self, va, n):
        o = self.off(va)
        return None if o is None else self.data[o:o + n]

    def u64(self, va):
        b = self.read(va, 8)
        return struct.unpack("<Q", b)[0] if b and len(b) == 8 else None

    def cstr(self, va, limit=512):
        b = self.read(va, limit) or b""
        return b.split(b"\0", 1)[0].decode("latin-1")


def scan_strings(pe):
    out = {}
    for name in (".rdata", ".data"):
        if not pe.section(name):
            continue
        sva, blob = pe.section_blob(name)
        for m in re.finditer(rb"[\x20-\x7e\t\r\n]{4,}(?=\x00)", blob):
            out[sva + m.start()] = m.group().decode("latin-1")
        for m in re.finditer(rb"(?:[\x20-\x7e]\x00){4,}(?=\x00\x00)", blob):
            if m.start() % 2 == 0:
                out.setdefault(sva + m.start(), 'L"' + m.group().decode("utf-16-le") + '"')
    return out


def scan_functions(pe):
    rva, size = pe.dirs[3]
    tbl = pe.read(pe.base + rva, size)
    chunks = []
    for i in range(0, size - size % 12, 12):
        b, e, u = struct.unpack_from("<III", tbl, i)
        if not b:
            continue
        begin = b
        for _ in range(64):                    # follow UNW_FLAG_CHAININFO to the owner
            hdr = pe.read(pe.base + (u & ~1), 4)
            if not hdr or not (hdr[0] >> 3) & 4:
                break
            p = pe.base + (u & ~1) + 4 + 2 * ((hdr[2] + 1) & ~1)
            begin, _, u = struct.unpack("<III", pe.read(p, 12))
        chunks.append((pe.base + b, pe.base + e, pe.base + begin))
    chunks.sort()
    return chunks


def scan_imports(pe):
    rva, _ = pe.dirs[1]
    out = {}
    p = pe.base + rva
    while True:
        oft, _, _, name, ft = struct.unpack("<IIIII", pe.read(p, 20))
        if not name:
            break
        dll = pe.cstr(pe.base + name).lower().replace(".dll", "")
        thunk = oft or ft
        i = 0
        while True:
            v = pe.u64(pe.base + thunk + 8 * i)
            if not v:
                break
            fn = "#%d" % (v & 0xFFFF) if v >> 63 else pe.cstr(pe.base + (v & 0x7FFFFFFF) + 2)
            out[pe.base + ft + 8 * i] = dll + "!" + fn
            i += 1
        p += 20
    return out


def demangle_type(s):
    body = s[4:] if s[:4] in (".?AV", ".?AU") else s
    if body.endswith("@@"):
        body = body[:-2]
    if "?" in body or "$" in body:
        return s
    return "::".join(reversed(body.split("@")))


def scan_rtti(pe, strings):
    tds = {va - 16: s for va, s in strings.items() if s.startswith(".?A")}
    sva, blob = pe.section_blob(".rdata")
    cols = {}
    i = blob.find(b"\x01\x00\x00\x00")
    while 0 <= i <= len(blob) - 24:
        if i % 4 == 0:
            _, off, _, tdr, _, selfr = struct.unpack_from("<IIIIII", blob, i)
            if selfr == sva - pe.base + i and pe.base + tdr in tds:
                cols[sva + i] = (pe.base + tdr, off)
        i = blob.find(b"\x01\x00\x00\x00", i + 1)
    _, tva, tsz, _, _ = pe.section(".text")
    vtables = []
    for i in range(0, len(blob) - 8, 8):
        q = struct.unpack_from("<Q", blob, i)[0]
        if q not in cols:
            continue
        funcs = []
        j = i + 8
        while j + 8 <= len(blob):
            f = struct.unpack_from("<Q", blob, j)[0]
            if not (tva <= f < tva + tsz):
                break
            funcs.append(f)
            j += 8
        td, off = cols[q]
        vtables.append((demangle_type(tds[td]), off, sva + i + 8, funcs))
    return vtables


# ----------------------------------------------------------------------------- asm

LINE = re.compile(rb"^  ([0-9A-F]{16}): (\S+)(?:\s+(.*?))?\r?$")
ADDR = re.compile(rb"([0-9A-F]{16})")
SPARSE = 1024


def find_dumpbin():
    if os.environ.get("DUMPBIN"):
        return os.environ["DUMPBIN"]
    pats = [r"C:\Program Files*\Microsoft Visual Studio\*\*\VC\Tools\MSVC\*\bin\Hostx64\x64\dumpbin.exe"]
    for p in pats:
        hits = sorted(glob.glob(p))
        if hits:
            return hits[-1]
    raise SystemExit("dumpbin.exe not found; set DUMPBIN")


def parse_asm(path, pe):
    lo, hi = pe.lo, pe.hi
    refs = {}
    kinds = {}
    sparse_va = array("Q")
    sparse_off = array("Q")
    n = 0
    off = 0
    with open(path, "rb") as f:
        for raw in f:
            m = LINE.match(raw)
            if m:
                va = int(m.group(1), 16)
                if n % SPARSE == 0:
                    sparse_va.append(va)
                    sparse_off.append(off)
                n += 1
                ops = m.group(3)
                if ops:
                    for a in ADDR.findall(ops):
                        t = int(a, 16)
                        if lo <= t < hi:
                            refs.setdefault(t, []).append(va)
                            mn = m.group(2)
                            if mn == b"call" and not ops.startswith(b"qword"):
                                kinds[va] = "c"
                            elif mn == b"jmp" and not ops.startswith(b"qword"):
                                kinds[va] = "j"
            off += len(raw)
    return refs, kinds, sparse_va, sparse_off, n


def scan_data_pointers(pe, strings, refs, kinds):
    """Aligned qwords in .rdata/.data that point at code or at a string start."""
    _, tva, tsz, _, _ = pe.section(".text")
    for name in (".rdata", ".data"):
        sva, blob = pe.section_blob(name)
        for i in range(0, len(blob) - 7, 8):
            q = struct.unpack_from("<Q", blob, i)[0]
            if (tva <= q < tva + tsz) or q in strings:
                refs.setdefault(q, []).append(sva + i)
                kinds[sva + i] = "d"


# ----------------------------------------------------------------------------- index

class Index:
    def __init__(self, d):
        self.__dict__.update(d)
        self.str_keys = sorted(self.strings)
        self.chunk_starts = [c[0] for c in self.chunks]
        self.func_chunks = {}
        for c in self.chunks:
            self.func_chunks.setdefault(c[2], []).append((c[0], c[1]))
        self.vlabels = {}
        for cls, off, vt, funcs in self.vtables:
            for k, fva in enumerate(funcs):
                tag = "%s::vf%d" % (cls, k) + ("@%X" % off if off else "")
                self.vlabels.setdefault(fva, []).append(tag)
        self._asm = None

    # --- lookups
    def string_at(self, va):
        if va in self.strings:
            return self.strings[va]
        i = bisect.bisect_right(self.str_keys, va) - 1
        if i >= 0:
            s0 = self.str_keys[i]
            s = self.strings[s0]
            if s0 < va < s0 + len(s) and not s.startswith('L"'):
                return s[va - s0:]
        return None

    def func_of(self, va):
        i = bisect.bisect_right(self.chunk_starts, va) - 1
        if i >= 0 and self.chunks[i][0] <= va < self.chunks[i][1]:
            return self.chunks[i][2]
        return None

    def label(self, fva):
        if fva is None:
            return "?"
        tags = self.vlabels.get(fva)
        if tags:
            extra = "" if len(tags) == 1 else " (+%d)" % (len(tags) - 1)
            return "sub_%X [%s%s]" % (fva, tags[0], extra)
        if fva in self.imports:
            return self.imports[fva]
        return "sub_%X" % fva

    def where(self, va):
        k = self.kinds.get(va)
        if k == "d":
            return "data@%X" % va
        f = self.func_of(va)
        return "%s+%X" % (self.label(f), va - f) if f is not None else "?@%X" % va

    # --- disassembly
    def asm_lines(self, start, end):
        if self._asm is None:
            self._asm = open(self.asm_path, "rb")
        i = max(0, bisect.bisect_right(self.sparse_va, start) - 1)
        self._asm.seek(self.sparse_off[i])
        for raw in self._asm:
            m = LINE.match(raw)
            if not m:
                continue
            va = int(m.group(1), 16)
            if va < start:
                continue
            if va >= end:
                return
            yield va, m.group(2).decode(), (m.group(3) or b"").decode("latin-1")

    def func_lines(self, fva):
        chunks = self.func_chunks.get(fva)
        if chunks:
            for s, e in sorted(chunks):
                for x in self.asm_lines(s, e):
                    yield x
            return
        # Leaf function (no unwind info): run to the first ret / tail jmp / padding,
        # never on into whatever happens to follow it.
        for va, mn, ops in self.asm_lines(fva, fva + 0x400):
            if mn == "int" and ops.strip() == "3":
                return
            yield va, mn, ops
            if mn == "ret" or (mn == "jmp" and not ops.startswith("qword")):
                return

    def annotate(self, ops):
        notes = []
        for a in ADDR.findall(ops.encode()):
            t = int(a, 16)
            s = self.string_at(t)
            if s is not None:
                notes.append(repr(s[:90]))
            elif t in self.imports:
                notes.append(self.imports[t])
            elif t in self.func_chunks or t in self.vlabels:
                notes.append(self.label(t))
            else:
                for cls, off, vt, funcs in self.vtables:
                    if t == vt:
                        notes.append("vtable " + cls + ("@%X" % off if off else ""))
                        break
        return notes


def build(dll):
    wd = workdir()
    asm = os.path.join(wd, "native.asm")
    pe = PE(dll)
    if not os.path.exists(asm) or os.path.getmtime(asm) < os.path.getmtime(dll):
        print("dumpbin -> " + asm)
        subprocess.check_call([find_dumpbin(), "/nologo", "/disasm:nobytes", "/section:.text",
                               "/out:" + asm, dll], stdout=subprocess.DEVNULL)
    print("strings..."); strings = scan_strings(pe)
    print("functions..."); chunks = scan_functions(pe)
    print("imports..."); imports = scan_imports(pe)
    print("rtti..."); vtables = scan_rtti(pe, strings)
    print("asm (slow)..."); refs, kinds, sva, soff, n = parse_asm(asm, pe)
    print("data pointers..."); scan_data_pointers(pe, strings, refs, kinds)
    d = dict(version=INDEX_VERSION, dll=dll, dll_mtime=os.path.getmtime(dll), asm_path=asm,
             strings=strings, chunks=chunks, imports=imports, vtables=vtables, refs=refs,
             kinds=kinds, sparse_va=sva, sparse_off=soff)
    with open(os.path.join(wd, "native.idx.pickle"), "wb") as f:
        pickle.dump(d, f, protocol=pickle.HIGHEST_PROTOCOL)
    print("%d instructions, %d strings, %d chunks, %d imports, %d vtables, %d referenced targets"
          % (n, len(strings), len(chunks), len(imports), len(vtables), len(refs)))


def load():
    p = os.path.join(workdir(), "native.idx.pickle")
    if not os.path.exists(p):
        raise SystemExit("no index; run: native_xref.py build")
    with open(p, "rb") as f:
        d = pickle.load(f)
    if d.get("version") != INDEX_VERSION:
        raise SystemExit("index is from another version of this tool; rebuild")
    if os.path.exists(d["dll"]) and os.path.getmtime(d["dll"]) != d["dll_mtime"]:
        print("WARNING: the DLL changed since the index was built (game update?) - rebuild")
    return Index(d)


# ----------------------------------------------------------------------------- commands

def parse_va(s):
    v = int(s, 16)
    return v + 0x180000000 if v < 0x180000000 else v


def cmd_str(ix, pattern, limit=60):
    rx = re.compile(pattern)
    shown = 0
    for va in ix.str_keys:
        s = ix.strings[va]
        if not rx.search(s):
            continue
        users = ix.refs.get(va, [])
        print("%X  %r  (%d refs)" % (va, s[:120], len(users)))
        for u in users[:12]:
            print("    <- " + ix.where(u))
        shown += 1
        if shown >= limit:
            print("... (limit %d)" % limit)
            break


def cmd_func(ix, va):
    f = ix.func_of(va)
    if f is None:
        print("no .pdata function contains %X (leaf function?)" % va)
        f = va
    chunks = sorted(ix.func_chunks.get(f, []))
    print("function %s  chunks: %s" % (ix.label(f), ", ".join("%X-%X" % c for c in chunks)))
    for tag in ix.vlabels.get(f, [])[1:]:
        print("  also " + tag)
    strs, calls = [], []
    for iva, mn, ops in ix.func_lines(f):
        for a in ADDR.findall(ops.encode()):
            t = int(a, 16)
            s = ix.string_at(t)
            if s is not None:
                strs.append((iva, s))
            elif mn in ("call", "jmp") and (t in ix.imports or t in ix.func_chunks or t in ix.vlabels):
                calls.append((iva, ix.label(t) if t not in ix.imports else ix.imports[t]))
            elif mn == "call" and t in ix.imports:
                calls.append((iva, ix.imports[t]))
    print("strings:")
    for iva, s in strs:
        print("  %X  %r" % (iva, s[:110]))
    print("calls:")
    for iva, l in calls:
        print("  %X  %s" % (iva, l))
    print("callers / pointers to it:")
    for u in ix.refs.get(f, [])[:40]:
        print("  %s  (%s)" % (ix.where(u), {"c": "call", "j": "jmp", "d": "ptr"}.get(ix.kinds.get(u), "ref")))


def cmd_dis(ix, va, maxlines=400):
    f = ix.func_of(va)
    if f is None:
        f = va
    print("; %s" % ix.label(f))
    for k, (iva, mn, ops) in enumerate(ix.func_lines(f)):
        if k >= maxlines:
            print("; ... truncated at %d lines" % maxlines)
            break
        notes = ix.annotate(ops)
        mark = ">" if iva == va else " "
        print("%s%X: %-8s %-44s%s" % (mark, iva, mn, ops, ("; " + " | ".join(notes)) if notes else ""))


def cmd_callers(ix, va, kinds=("c", "j", "d")):
    for u in ix.refs.get(va, []):
        k = ix.kinds.get(u)
        if k in kinds:
            print("%s  (%s)" % (ix.where(u), {"c": "call", "j": "jmp", "d": "ptr"}[k]))


def cmd_refs(ix, va):
    for u in ix.refs.get(va, []):
        print(ix.where(u))


def cmd_vtable(ix, sub):
    for cls, off, vt, funcs in ix.vtables:
        if sub.lower() not in cls.lower():
            continue
        print("%s  offset %X  vtable %X  (%d slots)" % (cls, off, vt, len(funcs)))
        for k, fva in enumerate(funcs):
            hint = ""
            for iva, mn, ops in ix.func_lines(fva):
                for a in ADDR.findall(ops.encode()):
                    s = ix.string_at(int(a, 16))
                    if s:
                        hint = repr(s[:70])
                        break
                if hint:
                    break
            print("  vf%-3d %X  %s" % (k, fva, hint))


def cmd_grep(ix, pattern, limit=200):
    rx = re.compile(pattern.encode())
    n = 0
    with open(ix.asm_path, "rb") as f:
        for raw in f:
            if not rx.search(raw):
                continue
            m = LINE.match(raw)
            if not m:
                continue
            va = int(m.group(1), 16)
            print("%-48s %s" % (ix.where(va), raw.decode("latin-1").strip()))
            n += 1
            if n >= limit:
                print("... (limit %d)" % limit)
                return


def cmd_selftest(ix):
    ok = True
    names = ["disable_async_render_jobs", "disable_async_predraw", "disable_camera_precision_offset",
             "use_depth_flipping", "enable_occluder_depth_prepass", "disable_dynamic_instancing"]
    votes = {}
    for va, s in ix.strings.items():
        if s in names:
            for u in ix.refs.get(va, []):
                f = ix.func_of(u) if ix.kinds.get(u) != "d" else ("data", u & ~0xFFF)
                votes.setdefault(f, set()).add(s)
    best = max(votes.items(), key=lambda kv: len(kv[1])) if votes else (None, set())
    print("ini parser: %s references %d/%d option names"
          % (ix.label(best[0]) if isinstance(best[0], int) else best[0], len(best[1]), len(names)))
    ok &= len(best[1]) >= 4
    hits = [ix.where(u) for va, s in ix.strings.items() if s == "Jitter.Offset.X" for u in ix.refs.get(va, [])]
    print("NGX jitter: %s" % (hits or "NOT FOUND"))
    ok &= bool(hits)
    print("SELFTEST " + ("PASS" if ok else "FAIL"))
    return ok


def main(argv):
    if len(argv) < 2:
        print(__doc__)
        return
    cmd = argv[1]
    if cmd == "build":
        build(argv[2] if len(argv) > 2 else DEFAULT_DLL)
        return
    ix = load()
    if cmd == "str":
        cmd_str(ix, argv[2])
    elif cmd == "func":
        cmd_func(ix, parse_va(argv[2]))
    elif cmd == "dis":
        cmd_dis(ix, parse_va(argv[2]), int(argv[3]) if len(argv) > 3 else 400)
    elif cmd == "callers":
        cmd_callers(ix, parse_va(argv[2]))
    elif cmd == "refs":
        cmd_refs(ix, parse_va(argv[2]))
    elif cmd == "vtable":
        cmd_vtable(ix, argv[2])
    elif cmd == "grep":
        cmd_grep(ix, argv[2])
    elif cmd == "selftest":
        sys.exit(0 if cmd_selftest(ix) else 1)
    else:
        print(__doc__)


if __name__ == "__main__":
    main(sys.argv)
