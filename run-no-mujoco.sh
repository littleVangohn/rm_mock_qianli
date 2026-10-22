#!/usr/bin/env bash
# 启动已打包的 RM26 无 MuJoCo 版 Windows 播放器。
set -eu

EXE_WIN='D:\Pacific_doorlock\release\RM26-Simulator-Win64-LAN\RM26-Simulator\RM26-Simulator.exe'
DIR_WIN='D:\Pacific_doorlock\release\RM26-Simulator-Win64-LAN\RM26-Simulator'

if [ -x /mnt/c/Windows/System32/WindowsPowerShell/v1.0/powershell.exe ]; then
  PWSH='/mnt/c/Windows/System32/WindowsPowerShell/v1.0/powershell.exe'
else
  # Git Bash / MSYS: powershell.exe is already on PATH.
  PWSH='powershell.exe'
fi

"$PWSH" -NoProfile -Command \
  "Start-Process -FilePath '$EXE_WIN' -WorkingDirectory '$DIR_WIN' -ArgumentList '-screen-fullscreen','1'"
echo "已启动 RM26 模拟器（全屏；Host → 选车 → Spawn）。"
