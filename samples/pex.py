# usage: pex.py xref <string>... | fn <va>... | callers <va> [max] | q <va>[:count]...   (stdlib only)
# Reads Client/Game.exe relative to the current directory (run it from a LocalExile checkout), or the path in GAME_EXE.
import sys, struct, re, bisect, mmap, os
EXE = os.environ.get("GAME_EXE", "Client/Game.exe")
f = open(EXE, 'rb'); d = mmap.mmap(f.fileno(), 0, access=mmap.ACCESS_READ)
pe = struct.unpack_from('<I', d, 0x3c)[0]
nsec = struct.unpack_from('<H', d, pe+6)[0]; optsz = struct.unpack_from('<H', d, pe+20)[0]
base = struct.unpack_from('<Q', d, pe+24+24)[0]
secs = []
for i in range(nsec):
    o = pe+24+optsz+40*i
    name = d[o:o+8].rstrip(b'\0').decode(); vs, va, rs, ro = struct.unpack_from('<IIII', d, o+8)
    secs.append((name, va, vs, ro, rs))
def off2va(off):
    for n, va, vs, ro, rs in secs:
        if ro <= off < ro+rs: return base+va+off-ro
def va2off(v):
    r = v-base
    for n, va, vs, ro, rs in secs:
        if va <= r < va+max(vs, rs): return ro+r-va
text = [s for s in secs if s[0] == '.text'][0]
pdata = [s for s in secs if s[0] == '.pdata'][0]
funcs = [struct.unpack_from('<II', d, pdata[3]+12*i) for i in range(pdata[4]//12)]
funcs = sorted(x for x in funcs if x[0]); starts = [x[0] for x in funcs]
def fn(v):
    r = v-base; i = bisect.bisect_right(starts, r)-1
    return (base+funcs[i][0], base+funcs[i][1]) if i >= 0 and r < funcs[i][1] else None
def xrefs(target):
    tb = d[text[3]:text[3]+text[4]]; out = []
    want = target-base
    for m in re.finditer(rb'[\x48\x4c][\x8d\x8b][\x05\x0d\x15\x1d\x25\x2d\x35\x3d]', tb):
        i = m.start(); disp = struct.unpack_from('<i', tb, i+3)[0]
        if text[1]+i+7+disp == want: out.append(base+text[1]+i)
    return out
def callers(target):
    tb = d[text[3]:text[3]+text[4]]; out = []; want = target-base
    for m in re.finditer(rb'[\xe8\xe9]', tb):
        i = m.start()
        if i+5 > len(tb): break
        if text[1]+i+5+struct.unpack_from('<i', tb, i+1)[0] == want: out.append(base+text[1]+i)
    return out
cmd = sys.argv[1]
if cmd == 'xref':
    for s in sys.argv[2:]:
        for enc in (s.encode()+b'\0', s.encode('utf-16le')+b'\0\0'):
            for m in re.finditer(re.escape(enc), d):
                v = off2va(m.start())
                if v is None: continue
                for x in xrefs(v):
                    print(f"{s!r} @0x{v:x} xref 0x{x:x} fn {tuple(hex(a) for a in fn(x)) if fn(x) else None}")
elif cmd == 'fn':
    for a in sys.argv[2:]: print(a, tuple(hex(x) for x in fn(int(a, 16))) if fn(int(a,16)) else None)
elif cmd == 'callers':
    for a in sys.argv[2:]:
        cs = callers(int(a, 16)); print(a, len(cs), 'callers')
        for c in cs[:int(sys.argv[-1]) if sys.argv[-1].isdigit() else 40]: print(f"  0x{c:x} fn {hex(fn(c)[0]) if fn(c) else None}")
if cmd == 'q':
    for a in sys.argv[2:]:
        v = int(a.split(':')[0], 16); n = int(a.split(':')[1]) if ':' in a else 6
        o = va2off(v); print(a, ' '.join(hex(struct.unpack_from('<Q', d, o+8*i)[0]) for i in range(n)))
