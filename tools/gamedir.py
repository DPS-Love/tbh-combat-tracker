r"""
找游戏目录。和 Directory.Build.props、tools\find-game.ps1 同一套规则，按顺序：
  1. 脚本自己的命令行参数（各脚本自己处理）
  2. 环境变量 TBH_GAME_DIR
  3. Steam 默认库：注册表里的 SteamPath\steamapps\common\TaskbarHero
"""
import os
import sys


def find_game_dir(flag):
    """按 2、3 找游戏目录；找不到就退出，提示用 flag（脚本自己的参数）或环境变量指定。"""
    d = os.environ.get('TBH_GAME_DIR')
    if d:
        return d
    try:
        import winreg
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, r'Software\Valve\Steam') as key:
            steam = winreg.QueryValueEx(key, 'SteamPath')[0]
    except (ImportError, OSError):
        steam = None
    if steam:
        # SteamPath 是 d:/steam 这种正斜杠写法，normpath 顺手规范掉
        d = os.path.normpath(os.path.join(steam, 'steamapps', 'common', 'TaskbarHero'))
        if os.path.isfile(os.path.join(d, 'TaskBarHero.exe')):
            return d
    sys.exit(f'找不到游戏目录。游戏不在 Steam 默认库时，设置环境变量 TBH_GAME_DIR，或用 {flag} 指定。')
