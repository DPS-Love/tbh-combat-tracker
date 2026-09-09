#!/usr/bin/env python3
"""
检查每个 Harmony 补丁方法都被 try/catch 包住了。构建时自动跑，不合格直接编译失败。

## 为什么值得专门加一道检查

补丁方法是我们和游戏代码之间的**边界**。从 Prefix 里漏出去的异常会让 Harmony
**跳过原方法**——一个只读的统计 Mod 因此可以把游戏功能整个玩坏。

这不是假想。游戏 1.2.2 更新后 `pl.bdvr` 字段改名，`HealFunnel_Pre` 里一行**调试日志**
读它时抛了 `MissingMethodException`。那个方法当时没有 try/catch，异常漏进 Prefix，
于是挂在生命恢复总入口 `pp.hbs` 上的补丁让原方法一次都没执行——游戏里所有回血失效，
玩家看到的现象是"牧师的治愈不回血"。一次会话里刷了 234 次。

游戏每次更新都会改混淆名，这类 `MissingMethodException` 迟早还会出现。
所以正确的目标不是"不出异常"，而是**出了异常也只影响统计，绝不影响游戏**。

## 判据

补丁方法体的第一条语句必须是 `try`（允许前面先给 `__state` 赋一个默认值——
Harmony 要求 out 参数在所有路径上都赋值）。要求"第一条就是 try"而不是"body 里有 catch"，
是因为后者会放过"只包了一半"的写法，而这次出事的恰恰就是没被包住的那半边。

用法:
    python tools/check-guards.py                 # 检查，有问题退出码 1
    python tools/check-guards.py --list          # 列出所有补丁方法及其状态
"""
import argparse
import glob
import os
import re
import sys

SRC = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'src')

# 行注释和块注释——判断"第一条语句"之前要先去掉
LINE_COMMENT = re.compile(r'//[^\n]*')
BLOCK_COMMENT = re.compile(r'/\*.*?\*/', re.S)


def strip_comments(text):
    return LINE_COMMENT.sub('', BLOCK_COMMENT.sub('', text))


def body_of(text, start):
    """从 start 之后的第一个 '{' 起做花括号配对，返回方法体（不含最外层括号）。"""
    i = text.find('{', start)
    if i < 0:
        return None
    depth = 0
    for k in range(i, len(text)):
        depth += (text[k] == '{') - (text[k] == '}')
        if depth == 0:
            return text[i + 1:k]
    return None


def check_file(path):
    """返回 [(方法名, 是否合格, 说明)]。"""
    text = open(path, encoding='utf-8').read()

    # 挂补丁时都是 prefix: nameof(X) / postfix: / finalizer:，只认这三个位置，
    # 免得把普通的 nameof 用法也当成补丁方法
    hooked = sorted(set(re.findall(r'(?:prefix|postfix|finalizer)\s*:\s*nameof\((\w+)\)', text)))

    out = []
    for name in hooked:
        m = re.search(r'\n[ \t]*private static [^\n(]*\b' + re.escape(name) + r'\s*\(', text)
        if not m:
            out.append((name, False, '找不到方法定义'))
            continue

        # 表达式体（=> ...）没法包 try/catch
        tail = text[m.end():]
        close = tail.find(')')
        after = tail[close + 1:close + 40]
        if '=>' in after.split(';')[0]:
            out.append((name, False, '表达式体，无法防护——改成块体并包 try/catch'))
            continue

        body = body_of(text, m.end())
        if body is None:
            out.append((name, False, '解析不出方法体'))
            continue

        stripped = strip_comments(body).strip()

        # 允许 out 参数先赋默认值：Harmony 要求所有路径都给 out 赋值
        while True:
            m2 = re.match(r'__\w+\s*=\s*[^;]*;', stripped)
            if not m2:
                break
            stripped = stripped[m2.end():].strip()

        if stripped.startswith('try'):
            out.append((name, True, ''))
        else:
            first = stripped.split('\n')[0][:60]
            out.append((name, False, f'第一条语句不是 try，而是: {first}'))

    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--list', action='store_true', help='把合格的也列出来')
    a = ap.parse_args()

    for stream in (sys.stdout, sys.stderr):
        try:
            stream.reconfigure(encoding='utf-8')
        except AttributeError:
            pass

    files = sorted(glob.glob(os.path.join(SRC, '**', '*.cs'), recursive=True))
    total, bad = 0, []
    for f in files:
        for name, ok, why in check_file(f):
            total += 1
            rel = os.path.relpath(f, os.path.join(SRC, '..'))
            if a.list:
                print(f'{"OK" if ok else "!!"}  {rel}  {name}' + (f'  —— {why}' if why else ''))
            if not ok:
                bad.append((rel, name, why))

    if not total:
        print('没找到任何补丁方法——检查脚本的匹配规则是不是过时了。', file=sys.stderr)
        return 1

    if bad:
        print(f'\n{len(bad)}/{total} 个补丁方法没有防护：', file=sys.stderr)
        for rel, name, why in bad:
            print(f'  {rel}: {name} —— {why}', file=sys.stderr)
        print('\n补丁方法是和游戏代码的边界，异常漏出去会让 Harmony 跳过原方法，'
              '把游戏功能玩坏。把整个方法体包进 try/catch，catch 里调 LogOnceInternal。',
              file=sys.stderr)
        return 1

    print(f'{total} 个补丁方法，全部有异常防护。')
    return 0


if __name__ == '__main__':
    sys.exit(main())
