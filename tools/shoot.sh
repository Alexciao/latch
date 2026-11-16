#!/usr/bin/env bash
# Screenshots a page of the Vite dev server (mock backend) with headless Edge.
# Usage: tools/shoot.sh <out.png> <path?query> [width] [height] [dark]
out="$1"; path="$2"; w="${3:-960}"; h="${4:-640}"; scheme="${5:-light}"
edge="/c/Program Files (x86)/Microsoft/Edge/Application/msedge.exe"
"$edge" --headless=new --disable-gpu --hide-scrollbars --force-device-scale-factor=1 \
  --blink-settings=preferredColorScheme=$([ "$scheme" = dark ] && echo 0 || echo 1) \
  --virtual-time-budget=3000 --window-size="$w,$h" --screenshot="$(cygpath -w "$out")" "http://localhost:5173$path" 2>/dev/null
