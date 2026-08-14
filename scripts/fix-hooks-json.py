# -*- coding: utf-8 -*-
"""修复 ~/.codex/hooks.json：用与插件 BuildJson 相同的结构重新生成，
确保 JSON 语法正确（引号转义），路径指向游戏插件目录。"""
import json
import os

plugin_dir = r"D:\SBeam\steamapps\common\Chill with You Lo-Fi Story\BepInEx\plugins\ChillAI"
events = ["SessionStart", "UserPromptSubmit", "PostToolUse",
          "PermissionRequest", "Stop", "SessionEnd"]
cmd_tpl = 'powershell -NoProfile -ExecutionPolicy Bypass -File "{0}" -EventName '
cmd = cmd_tpl.format(plugin_dir + "\\codex-hook.ps1")

root = {
    "hooks": {
        evt: [{"hooks": [{"type": "command", "command": cmd + evt, "timeout": 3}]}]
        for evt in events
    }
}

path = os.path.expanduser("~/.codex/hooks.json")
with open(path, "w", encoding="utf-8") as f:
    json.dump(root, f, indent=2, ensure_ascii=False)

# 自检
with open(path, encoding="utf-8") as f:
    data = json.load(f)
sample = data["hooks"]["SessionStart"][0]["hooks"][0]["command"]
print("OK: hooks.json 已重写且 JSON 有效")
print("示例命令:", sample)
