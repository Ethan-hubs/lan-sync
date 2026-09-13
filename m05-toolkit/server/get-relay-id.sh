#!/usr/bin/env bash
# 抓 relay 自身的 Device ID。
# 依据（手册 §3.3 / §0.7）：源码里 log.Println("ID:", id) 在 if debug 之内，
# 所以正式运行（不带 -debug）的日志里【没有】ID。这里用一次性容器带 -debug 抓。
set -euo pipefail
cd "$(dirname "$0")"
set -a; . ./.env 2>/dev/null || true; set +a

ROOT="${DATA_ROOT:-/opt/lan-sync/m05}"
TAG="${SYNCTHING_TAG:-v2.1.5}"

echo "==> 一次性容器（-debug）抓 ID，抓到即退出"
docker run --rm \
  -v "$ROOT/relay:/var/strelaysrv" \
  "lansync/strelaysrv:$TAG" \
  -pools="" -keys=/var/strelaysrv -listen=:22067 -status-srv="" -debug 2>&1 \
| grep -m1 -E 'ID: *[A-Z0-9-]{10,}' || {
    echo "!!! 没抓到 ID。回退方案：读证书算 Device ID，或临时给正式容器加 -debug 重启一次"; exit 1; }

echo
echo "把上面那串记为 <RELAY_DEVICE_ID>；客户端 URI 格式："
echo '  relay://<RELAY_HOST>:22067/?id=<RELAY_DEVICE_ID>&token=<RELAY_TOKEN>'
