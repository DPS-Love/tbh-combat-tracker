#!/usr/bin/env python3
"""
算出哪些方法可以安全地打 Harmony 补丁。

背景：IL2CPP 会把**方法体完全相同**的函数合并成同一段机器码。这不只发生在同一个类内部——
本游戏里 `0x6B1620`（一条 `ret 0`）被 **1872** 个方法共用，`0xCE8880`（一个有完整序言的
真函数）被 **457** 个方法共用。

后果：对这种共享地址挂 detour，等于劫持了全游戏所有共用它的方法。它们的 `this` 是各种
不相干的类型，进到 Harmony 生成的 wrapper 里一转型就 NullReferenceException，每帧刷屏，
游戏进不去。

判据不是"这段代码是否简单"（`0xCE8880` 就是个真函数），而是"这段机器码在全二进制里
是否只属于一个方法"。只有全局唯一的才能安全 hook。

用法：
    python tools/safe-hooks.py                       # 默认查 pj/ph/pf/Monster
    python tools/safe-hooks.py --types pj ph Monster
    python tools/safe-hooks.py --csharp              # 直接输出 C# 数组字面量
"""
import argparse
import collections
import re
import sys

DEFAULT_DUMP = 'build/dump/dump.cs'
DEFAULT_TYPES = ['pj', 'ph', 'pf', 'Monster']

DECL = re.compile(
    r'^\s*(?:public|internal|private|protected)?[\w\s]*?'
    r'\b(?:class|struct)\s+([A-Za-z_][\w]*)(?!\.)'
)
RVA = re.compile(r'//\s*RVA:\s*(0x[0-9A-Fa-f]+)')
NAME = re.compile(r'\b([A-Za-z_][\w]*)\s*\(')

# Unity 生命周期回调和构造函数，诊断价值低且容易高频触发
SKIP = {
    'Update', 'LateUpdate', 'FixedUpdate', 'OnGUI', 'OnEnable', 'OnDisable',
    'OnDestroy', 'Awake', 'Start', 'OnValidate', '.ctor', '.cctor',
    'ctor', 'cctor', 'Finalize', 'ToString', 'GetHashCode', 'Equals',
}
SKIP_PREFIX = ('get_', 'set_', 'add_', 'remove_')


def parse(path, wanted):
    lines = open(path, encoding='utf-8', errors='replace').read().split('\n')
    global_count = collections.Counter()
    per_type = collections.defaultdict(list)

    cur, pending = None, None
    for line in lines:
        m = DECL.match(line)
        if m:
            cur, pending = m.group(1), None
            continue
        r = RVA.search(line)
        if r:
            global_count[r.group(1)] += 1
            pending = r.group(1)
            continue
        if pending is not None:
            sig = line.strip()
            if sig:
                if cur in wanted:
                    per_type[cur].append((pending, sig.replace(' { }', '')))
                pending = None
    return global_count, per_type


def main():
    for stream in (sys.stdout, sys.stderr):
        try:
            stream.reconfigure(encoding='utf-8')
        except Exception:
            pass

    ap = argparse.ArgumentParser()
    ap.add_argument('--dump', default=DEFAULT_DUMP)
    ap.add_argument('--types', nargs='*', default=DEFAULT_TYPES)
    ap.add_argument('--csharp', action='store_true', help='输出 C# 数组字面量')
    ap.add_argument('--show-unsafe', action='store_true', help='把被排除的也列出来')
    args = ap.parse_args()

    wanted = set(args.types)
    global_count, per_type = parse(args.dump, wanted)

    safe, unsafe = [], []
    for cls in args.types:
        for rva, sig in per_type.get(cls, []):
            m = NAME.search(sig)
            name = m.group(1) if m else '?'
            if name in SKIP or name.startswith(SKIP_PREFIX):
                continue
            shared = global_count[rva]
            (safe if shared == 1 else unsafe).append((cls, name, rva, shared, sig))

    if args.csharp:
        print('        private static readonly HashSet<string> SafeToPatch = new HashSet<string>')
        print('        {')
        for cls in args.types:
            items = [f'"{c}.{n}"' for c, n, _, _, _ in safe if c == cls]
            for i in range(0, len(items), 6):
                print('            ' + ', '.join(items[i:i + 6]) + ',')
        print('        };')
        return 0

    print(f'{"类":<9}{"方法":<9}{"地址":<12}{"共用":>6}  签名')
    for cls, name, rva, shared, sig in safe:
        print(f'{cls:<9}{name:<9}{rva:<12}{shared:>6}  {sig[:60]}')

    if args.show_unsafe and unsafe:
        print(f'\n被排除（机器码被多个方法共用，挂了会劫持无关方法）：')
        for cls, name, rva, shared, sig in sorted(unsafe, key=lambda x: -x[3]):
            print(f'{cls:<9}{name:<9}{rva:<12}{shared:>6}  {sig[:60]}')

    print(f'\n安全 {len(safe)} 个，排除 {len(unsafe)} 个。')
    print('把结果贴进 src/TbhCombatTracker/Diagnostics.cs 的 SafeToPatch（用 --csharp 直接出格式）。')
    return 0


if __name__ == '__main__':
    sys.exit(main())
