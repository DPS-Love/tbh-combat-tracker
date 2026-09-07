#!/usr/bin/env python3
"""
离线解出游戏的本地化字符串表，输出 key -> 各语言译文。

背景：Unity Localization 的键**不在** global-metadata 里——界面上的文本大多由预制体上的
`LocalizeStringEvent` 挂键，键存在 Addressables 资产里。所以靠翻 dump.cs 或扫描
metadata 字面量都找不到；只能把 bundle 解出来看。

用它回答过的问题：
  * 伤害类型（Melee / Projectile / AOE / Summon）有没有译文？——没有独立词条，
    但属性面板的 `StatName_IncreaseMeleeDamage` = "增加近战伤害" 里含这个词。
  * 元素属性（Fire / Cold / …）的键是什么？——就是裸枚举名，直接能查到。

用法：
    python tools/dump-localization.py                      # 中英对照，全部键
    python tools/dump-localization.py --langs zh-hans ko-kr ru-ru
    python tools/dump-localization.py --grep Melee|Stat_   # 只看匹配的键
"""
import argparse
import glob
import os
import re
import struct
import sys

GAME = r'D:\Steam\steamapps\common\TaskbarHero'
BUNDLES = os.path.join(GAME, r'TaskbarHero_Data\StreamingAssets\aa\StandaloneWindows64')


# ---------------------------------------------------------------- UnityFS

def lz4_block(src, out_size):
    """LZ4 块格式解压。只有几十行，不值得为它拉一个依赖进来。"""
    dst = bytearray()
    i, n = 0, len(src)
    while i < n and len(dst) < out_size:
        tok = src[i]; i += 1
        ll = tok >> 4
        if ll == 15:
            while True:
                b = src[i]; i += 1; ll += b
                if b != 255: break
        dst += src[i:i + ll]; i += ll
        if i + 2 > n: break
        off = src[i] | (src[i + 1] << 8); i += 2
        ml = tok & 0xF
        if ml == 15:
            while True:
                b = src[i]; i += 1; ml += b
                if b != 255: break
        ml += 4
        s = len(dst) - off
        for k in range(ml):
            dst.append(dst[s + k])
    return bytes(dst)


class _R:
    def __init__(s, d): s.d, s.p = d, 0
    def u16(s): v = struct.unpack_from('>H', s.d, s.p)[0]; s.p += 2; return v
    def u32(s): v = struct.unpack_from('>I', s.d, s.p)[0]; s.p += 4; return v
    def i64(s): v = struct.unpack_from('>q', s.d, s.p)[0]; s.p += 8; return v

    def cstr(s):
        e = s.d.index(b'\0', s.p)
        v = s.d[s.p:e].decode('utf-8', 'replace')
        s.p = e + 1
        return v


def unpack(path):
    """解出 bundle 里的原始数据块。不解析 SerializedFile——我们只要里面的字符串。"""
    d = open(path, 'rb').read()
    r = _R(d)
    assert r.cstr() == 'UnityFS', path
    r.u32(); r.cstr(); r.cstr()            # version / player / revision
    r.i64()                                 # 整包大小
    ci, ui, flags = r.u32(), r.u32(), r.u32()

    r.p += (-r.p) & 15                      # version >= 7：blocksInfo 前对齐
    if flags & 0x80:                        # blocksInfoAtEnd
        blob = d[len(d) - ci:]
    else:
        blob = d[r.p:r.p + ci]; r.p += ci
    info = blob if (flags & 0x3F) == 0 else lz4_block(blob, ui)

    b = _R(info); b.p += 16                 # hash
    blocks = [(b.u32(), b.u32(), b.u16()) for _ in range(b.u32())]

    if flags & 0x200:                       # BlockInfoNeedPaddingAtStart
        r.p += (-r.p) & 15

    out = bytearray()
    for un, cn, f in blocks:
        chunk = d[r.p:r.p + cn]; r.p += cn
        out += chunk if (f & 0x3F) == 0 else lz4_block(chunk, un)
    return bytes(out)


def sstrings(data):
    """Unity 序列化的字符串是 u32 长度 + UTF-8 + 4 字节对齐。
    紧挨在前面的 8 字节通常就是条目 id，两张表靠它对齐。"""
    out = []
    i, n = 0, len(data)
    while i + 4 <= n:
        ln = struct.unpack_from('<I', data, i)[0]
        if 1 <= ln <= 4000 and i + 4 + ln <= n:
            try:
                s = data[i + 4:i + 4 + ln].decode('utf-8')
            except UnicodeDecodeError:
                i += 1
                continue
            if all(c == '\n' or c.isprintable() for c in s):
                idv = struct.unpack_from('<q', data, i - 8)[0] if i >= 8 else -1
                out.append((idv, s))
                i += 4 + ln
                i += (-i) & 3
                continue
        i += 1
    return out


# ---------------------------------------------------------------- 主流程

def load_keys():
    hits = glob.glob(os.path.join(BUNDLES, 'localization-assets-shared*.bundle'))
    if not hits:
        sys.exit(f'找不到 shared bundle，确认游戏路径：{BUNDLES}')
    keys = {}
    for idv, s in sstrings(unpack(hits[0])):
        if re.fullmatch(r'[A-Za-z][A-Za-z0-9_]{2,60}', s):
            keys[idv] = s
    return keys


def load_lang(lang):
    hits = [p for p in glob.glob(os.path.join(BUNDLES, 'localization-string-tables-*.bundle'))
            if f'({lang})' in os.path.basename(p).lower()]
    if not hits:
        return None
    return {idv: s for idv, s in sstrings(unpack(hits[0]))}


def main():
    # Windows 控制台默认 GBK，打泰语/俄语会直接抛 UnicodeEncodeError
    try:
        sys.stdout.reconfigure(encoding='utf-8', errors='replace')
    except AttributeError:
        pass

    ap = argparse.ArgumentParser()
    ap.add_argument('--langs', nargs='+', default=['zh-hans', 'en-us'])
    ap.add_argument('--grep', help='只输出键名匹配这个正则的条目')
    a = ap.parse_args()

    keys = load_keys()
    langs = {}
    for l in a.langs:
        v = load_lang(l)
        if v is None:
            print(f'!! 没有语言 {l}', file=sys.stderr)
        else:
            langs[l] = v

    pat = re.compile(a.grep, re.I) if a.grep else None
    shown = 0
    for idv, k in sorted(keys.items(), key=lambda kv: kv[1]):
        if pat and not pat.search(k):
            continue
        line = [f'{k}']
        for l, v in langs.items():
            t = v.get(idv)
            if t is not None:
                line.append(f'    {l:8} {t}')
        if len(line) > 1:
            print('\n'.join(line))
            shown += 1
    print(f'\n共 {len(keys)} 个键，输出 {shown} 个。', file=sys.stderr)


if __name__ == '__main__':
    main()
