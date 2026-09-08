#!/usr/bin/env python3
"""
从 Il2CppDumper 产出的 dump.cs 里按类型名抠出完整定义。

dump.cs 有 140 万行、53 MB，直接用编辑器打开会卡死，也没法 grep 出完整的类块。
这个脚本做花括号配对，把整个类/结构体/接口/枚举连同上方的 `// Namespace:` 注释一起打出来。

用法：
    python tools/extract-types.py <dump.cs> Unit Hero Monster DamageInfo
    python tools/extract-types.py <dump.cs> --grep Damage        # 按子串找类型名
"""
import argparse
import re
import sys

# 末尾的 (?!\.) 是为了排掉 dump.cs 里的嵌套类行（`class Hero.pa : ...`），
# 否则查 Hero 会连它的一堆编译器生成迭代器类一起命中。
DECL = re.compile(
    r'^\s*(?:\[[^\]]*\]\s*)*'
    r'(?:public|internal|private|protected)?[\w\s]*?'
    r'\b(class|struct|interface|enum)\s+([A-Za-z_][\w`]*)(?!\.)'
)


def load(path):
    with open(path, encoding='utf-8', errors='replace') as f:
        return f.read().split('\n')


def namespace_above(lines, i):
    for k in range(max(0, i - 8), i):
        if lines[k].startswith('// Namespace:'):
            return lines[k]
    return '// Namespace: ?'


def blocks(lines, want):
    """yield (line_no, namespace, text)；want 是一个 name -> bool 的判定函数。"""
    i, n = 0, len(lines)
    while i < n:
        m = DECL.match(lines[i])
        if m and want(m.group(2)):
            j = i
            while j < n and '{' not in lines[j]:
                j += 1
            depth, end = 0, j
            for k in range(j, n):
                depth += lines[k].count('{') - lines[k].count('}')
                if depth <= 0:
                    end = k
                    break
            yield i + 1, namespace_above(lines, i), '\n'.join(lines[i:end + 1])
            i = end + 1
            continue
        i += 1


# RVA 注释写在方法签名的上一行，形如
#   // RVA: 0xCD4550 Offset: 0xCD2F50 VA: 0x180CD4550 Slot: 8
RVA_LINE = re.compile(r'//\s*RVA:\s*(0x[0-9A-Fa-f]+|-1)')
SIG_LINE = re.compile(r'^\s+(?:\[[^\]]*\]\s*)*(?:public|private|protected|internal)[^;{]*\([^;]*\)\s*\{')


def methods(text):
    """yield (rva, 签名)。RVA 相同说明 IL2CPP 把方法体合并了——那种地址不能安全 hook，
    见 docs/symbols.md 第 7 节。"""
    rva = '?'
    for line in text.splitlines():
        m = RVA_LINE.search(line)
        if m:
            rva = m.group(1)
            continue
        if SIG_LINE.match(line):
            yield rva, line.strip().rstrip('{ ').strip()
            rva = '?'


def main():
    # Windows 控制台默认 GBK，中文提示会变乱码
    for stream in (sys.stdout, sys.stderr):
        try:
            stream.reconfigure(encoding='utf-8')
        except Exception:
            pass

    ap = argparse.ArgumentParser()
    ap.add_argument('dump')
    ap.add_argument('--grep', help='按子串匹配类型名，而不是精确匹配')
    ap.add_argument('--names-only', action='store_true', help='只列出匹配到的类型名和行号')
    ap.add_argument('--methods', action='store_true',
                    help='只列方法，每行 "RVA 签名"。RVA 相同 = 同一段机器码，'
                         '游戏更新后靠签名重新定位 hook 点时最好用')
    # 类型名走 parse_known_args 收尾，这样 --names-only 放在类型名前后都能用
    args, extra = ap.parse_known_args()

    names = [e for e in extra if not e.startswith('-')]
    unknown = [e for e in extra if e.startswith('-')]
    if unknown:
        ap.error(f'未知选项: {" ".join(unknown)}')
    if not names and not args.grep:
        ap.error('至少给一个类型名，或者用 --grep')

    lines = load(args.dump)
    if args.grep:
        needle = args.grep.lower()
        want = lambda name: needle in name.lower()
    else:
        targets = set(names)
        want = lambda name: name in targets

    found = 0
    for line_no, ns, text in blocks(lines, want):
        found += 1
        if args.names_only:
            print(f'{line_no}: {ns[14:]:35} {text.split(chr(10))[0]}')
        elif args.methods:
            print(f'// dump.cs:{line_no}  {text.split(chr(10))[0]}')
            for rva, sig in methods(text):
                print(f'  {rva:<12} {sig}')
            print()
        else:
            print(f'// dump.cs:{line_no}')
            print(ns)
            print(text)
            print('\n' + '=' * 72 + '\n')

    if found == 0:
        print('没有匹配到任何类型。混淆名可能已随游戏更新变化，'
              '试试 --grep 或者按 docs/symbols.md 里的"识别特征"重新定位。', file=sys.stderr)
        return 1
    return 0


if __name__ == '__main__':
    sys.exit(main())
