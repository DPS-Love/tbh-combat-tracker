#!/usr/bin/env python3
"""
在 GameAssembly.dll 的机器码里找"谁调用了这个函数"。

用途：判断某段代码是**存在**还是**真的被执行**。
比如游戏里打包了 Anti-Cheat Toolkit，但 ACTk 的检测器不调 StartDetection 就是死代码——
类定义摆在那儿不代表它跑。dump.cs 只有签名没有方法体，所以只能从二进制层面看调用点。

做法：扫描 .text 里所有 `E8 rel32`(call) / `E9 rel32`(jmp)，算出目标地址，
再和 dump.cs 里每个方法的入口地址对表。

用法:
    python tools/xref-scan.py --dump build/dump/dump.cs --targets InjectionDetector SpeedHackDetector
    python tools/xref-scan.py --targets "InjectionDetector.yum"
    python tools/xref-scan.py --va 0x18073CC70
"""
import argparse
import collections
import re
import struct
import sys

DEFAULT_DLL = r"D:\Steam\steamapps\common\TaskbarHero\GameAssembly.dll"
DEFAULT_DUMP = "build/dump/dump.cs"

DECL = re.compile(
    r'^\s*(?:\[[^\]]*\]\s*)*'
    r'(?:public|internal|private|protected)?[\w\s]*?'
    r'\b(?:class|struct|interface|enum)\s+([A-Za-z_][\w`]*)(?!\.)'
)
RVA_LINE = re.compile(r'//\s*RVA:\s*(0x[0-9A-Fa-f]+|-1).*?VA:\s*(0x[0-9A-Fa-f]+|-1)')


# ---------------------------------------------------------------- PE parsing

class PE:
    def __init__(self, path):
        self.data = open(path, 'rb').read()
        e_lfanew = struct.unpack_from('<I', self.data, 0x3C)[0]
        if self.data[e_lfanew:e_lfanew + 4] != b'PE\0\0':
            raise ValueError('不是有效的 PE 文件')

        coff = e_lfanew + 4
        n_sections, = struct.unpack_from('<H', self.data, coff + 2)
        opt_size, = struct.unpack_from('<H', self.data, coff + 16)
        opt = coff + 20

        magic, = struct.unpack_from('<H', self.data, opt)
        if magic != 0x20B:
            raise ValueError('只支持 PE32+ (x64)')
        self.image_base, = struct.unpack_from('<Q', self.data, opt + 24)

        self.sections = []
        sec = opt + opt_size
        for i in range(n_sections):
            off = sec + i * 40
            name = self.data[off:off + 8].rstrip(b'\0').decode('ascii', 'replace')
            vsize, vaddr, rawsize, rawptr = struct.unpack_from('<IIII', self.data, off + 8)
            chars, = struct.unpack_from('<I', self.data, off + 36)
            self.sections.append(dict(name=name, vaddr=vaddr, vsize=vsize,
                                      rawptr=rawptr, rawsize=rawsize, exec=bool(chars & 0x20000000)))

    def code_sections(self):
        return [s for s in self.sections if s['exec'] and s['rawsize'] > 0]


# ---------------------------------------------------------------- dump.cs

def parse_dump(path):
    """返回 [(va, type_name, signature)]，按 va 排序。"""
    out = []
    cur_type = '?'
    pending = None
    with open(path, encoding='utf-8', errors='replace') as f:
        for line in f:
            line = line.rstrip('\n')
            m = DECL.match(line)
            if m:
                cur_type = m.group(1)
                pending = None
                continue
            m = RVA_LINE.search(line)
            if m:
                pending = m.group(2)
                continue
            if pending is not None:
                s = line.strip()
                if s:
                    if pending != '-1':
                        out.append((int(pending, 16), cur_type, s.replace(' { }', '')))
                    pending = None
    out.sort(key=lambda t: t[0])
    return out


# ---------------------------------------------------------------- scan

def scan_calls(pe):
    """扫出所有 call/jmp rel32 的 (site_va, target_va)。"""
    sites = []
    for sec in pe.code_sections():
        blob = pe.data[sec['rawptr']:sec['rawptr'] + sec['rawsize']]
        base = pe.image_base + sec['vaddr']
        limit = len(blob) - 5
        find = blob.find
        unpack = struct.unpack_from
        for opcode in (0xE8, 0xE9):
            b = bytes([opcode])
            i = find(b)
            while i != -1:
                if i > limit:
                    break
                rel, = unpack('<i', blob, i + 1)
                target = base + i + 5 + rel
                sites.append((base + i, target))
                i = find(b, i + 1)
    return sites


CACHE = 'build/xref-cache.pkl'


def load_or_scan(pe, dll_path, use_cache=True):
    """扫描很慢（2.6M 个调用点），按 dll 的大小+mtime 做缓存。"""
    import os
    import pickle

    st = os.stat(dll_path)
    key = (dll_path, st.st_size, int(st.st_mtime))

    if use_cache and os.path.exists(CACHE):
        try:
            with open(CACHE, 'rb') as f:
                cached_key, sites = pickle.load(f)
            if cached_key == key:
                print(f'  复用缓存 {CACHE}（{len(sites)} 个调用点）')
                return sites
        except Exception:
            pass

    print('扫描 call/jmp rel32 …')
    sites = scan_calls(pe)
    if use_cache:
        try:
            os.makedirs(os.path.dirname(CACHE), exist_ok=True)
            with open(CACHE, 'wb') as f:
                pickle.dump((key, sites), f, protocol=4)
        except Exception as e:
            print(f'  (缓存写入失败，忽略: {e})')
    return sites


def same_family(a, b):
    """`Foo` 和它的编译器生成内部类（dump.cs 里被截成 `Fo`/`Foo` 前缀）算同一家。"""
    if a == b:
        return True
    lo, hi = (a, b) if len(a) <= len(b) else (b, a)
    return len(lo) >= 3 and hi.startswith(lo)


def report_external_callers(methods, sites, type_name, max_callers):
    """列出所有从**外部**打进 type_name 的调用点。"""
    own = {va: sig for va, tn, sig in methods if tn == type_name}
    print(f'══ {type_name} —— 外部调用者 ══')
    if not own:
        print('   !! dump 里没有这个类型\n')
        return

    print(f'   该类共 {len(own)} 个有地址的方法')

    external = collections.Counter()
    detail = collections.defaultdict(collections.Counter)
    for site, target in sites:
        if target not in own:
            continue
        enc = enclosing(methods, site)
        caller_type = enc[1] if enc else '?'
        if same_family(caller_type, type_name):
            continue  # 类内部自调用不算
        external[caller_type] += 1
        detail[caller_type][own[target]] += 1

    if not external:
        print('   ★ 零外部调用 —— 这个类从没被外面碰过，是死代码\n')
        return

    print(f'   外部调用点 {sum(external.values())} 处，来自 {len(external)} 个类型:')
    for caller, n in external.most_common(max_callers):
        print(f'      ← {n}x  {caller}')
        for sig, c in detail[caller].most_common(3):
            print(f'           → {sig}')
    print()


def report_callees(methods, sites, type_name, max_items):
    """列出 type_name 的方法体里调用了哪些外部方法。"""
    print(f'══ {type_name} —— 它调用了谁 ══')
    own = [(va, sig) for va, tn, sig in methods if tn == type_name]
    if not own:
        print('   !! dump 里没有这个类型\n')
        return

    own_vas = {va for va, _ in own}
    by_va = collections.defaultdict(list)
    for va, tn, sig in methods:
        by_va[va].append((tn, sig))

    # 方法体的范围 = [入口, 下一个方法入口)
    entries = [m[0] for m in methods]
    ranges = []
    for va, sig in own:
        i = enclosing_index(entries, va)
        end = entries[i + 1] if i + 1 < len(entries) else va + 0x400
        ranges.append((va, end, sig))

    out = collections.Counter()
    for site, target in sites:
        if target in own_vas:
            continue  # 类内自调用跳过
        for lo, hi, sig in ranges:
            if lo <= site < hi:
                for tn2, sig2 in by_va.get(target, []):
                    if tn2 != type_name:
                        out[f'{tn2}.{sig2}'] += 1
                break

    if not out:
        print('   （没解析到外部调用）\n')
        return
    for name, n in out.most_common(max_items):
        print(f'   → {name}')
    print()


def enclosing_index(entries, va):
    lo, hi = 0, len(entries)
    while lo < hi:
        mid = (lo + hi) // 2
        if entries[mid] <= va:
            lo = mid + 1
        else:
            hi = mid
    return lo - 1


def enclosing(methods, va):
    """二分查上界：调用点属于入口地址不大于它的那个方法。"""
    lo, hi = 0, len(methods)
    while lo < hi:
        mid = (lo + hi) // 2
        if methods[mid][0] <= va:
            lo = mid + 1
        else:
            hi = mid
    return methods[lo - 1] if lo else None


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--dll', default=DEFAULT_DLL)
    ap.add_argument('--dump', default=DEFAULT_DUMP)
    ap.add_argument('--targets', nargs='*', default=[],
                    help='类名，或 类名.方法名。列出这些方法被调用的次数和调用者')
    ap.add_argument('--callers-of', nargs='*', default=[],
                    help='类名。只列出**外部**调用者（排除该类自己的方法和它的状态机内部类）——'
                         '用来判断一段代码是不是从没被外面碰过')
    ap.add_argument('--callees-of', nargs='*', default=[],
                    help='类名。列出该类的方法体里调用了哪些外部方法——用来看一段代码触发后会做什么')
    ap.add_argument('--va', action='append', default=[], help='直接给目标地址，如 0x18073CC70')
    ap.add_argument('--max-callers', type=int, default=8)
    ap.add_argument('--no-cache', action='store_true')
    args = ap.parse_args()

    for stream in (sys.stdout, sys.stderr):
        try:
            stream.reconfigure(encoding='utf-8')
        except Exception:
            pass

    print(f'解析 {args.dump} …')
    methods = parse_dump(args.dump)
    print(f'  {len(methods)} 个有地址的方法')

    print(f'解析 {args.dll} …')
    pe = PE(args.dll)
    print(f'  ImageBase 0x{pe.image_base:X}，可执行节: ' +
          ', '.join(f"{s['name']}({s['rawsize'] // 1024}KB)" for s in pe.code_sections()))

    sites = load_or_scan(pe, args.dll, use_cache=not args.no_cache)
    targets = collections.Counter(t for _, t in sites)
    print(f'  {len(sites)} 个调用点，{len(targets)} 个不同目标')

    # 健全性检查：随机字节里的 E8 也会被当成 call，所以先看有多少目标恰好落在方法入口上。
    entries = {va for va, _, _ in methods}
    on_entry = sum(c for t, c in targets.items() if t in entries)
    print(f'  其中 {on_entry} 个 ({on_entry * 100.0 / max(len(sites), 1):.1f}%) 精确命中方法入口 —— '
          f'比例越高说明扫描越可信\n')

    for tn in args.callers_of:
        report_external_callers(methods, sites, tn, args.max_callers)

    for tn in args.callees_of:
        report_callees(methods, sites, tn, max(args.max_callers, 40))

    # 挑出要查的方法
    wanted = []
    for spec in args.targets:
        if '.' in spec:
            tn, mn = spec.split('.', 1)
            wanted += [m for m in methods if m[1] == tn and re.search(r'\b' + re.escape(mn) + r'\s*\(', m[2])]
        else:
            wanted += [m for m in methods if m[1] == spec]
    for v in args.va:
        va = int(v, 16)
        m = next((m for m in methods if m[0] == va), (va, '?', '?'))
        wanted.append(m)

    if not wanted:
        if not args.callers_of:
            print('没指定目标（--targets / --callers-of / --va），只做了全局统计。')
        return 0

    # IL2CPP 会把方法体完全相同的方法去重成同一个地址，所以一个 VA 可能对应好几个逻辑方法。
    # 这种地址上的调用计数无法单独归因——但"零调用"仍然可靠（共用只会让计数变多）。
    by_va = collections.defaultdict(list)
    for va, tn, sig in methods:
        by_va[va].append(f'{tn}.{sig}')

    called_any = False
    for va, tname, sig in wanted:
        n = targets.get(va, 0)
        shared = by_va.get(va, [])
        mark = '被调用' if n else '★ 无任何调用点（死代码）'
        print(f'{tname}.{sig}')
        print(f'   VA 0x{va:X}   调用点 {n} 处   {mark}')
        if len(shared) > 1:
            print(f'   ⚠ 该地址被 {len(shared)} 个方法共用（IL2CPP 方法体去重），'
                  f'调用数无法单独归因给本方法')
        if n:
            called_any = True
            callers = collections.Counter()
            for site, t in sites:
                if t == va:
                    enc = enclosing(methods, site)
                    callers[f'{enc[1]}.{enc[2]}' if enc else '?'] += 1
            for name, c in callers.most_common(args.max_callers):
                print(f'      ← {c}x  {name}')
        print()

    return 0 if called_any else 0


if __name__ == '__main__':
    sys.exit(main())
