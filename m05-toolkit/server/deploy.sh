#!/usr/bin/env bash
# M0.5 服务端一键部署（配套手册 §3）。幂等：重复执行会重建容器但不删数据卷。
set -euo pipefail
cd "$(dirname "$0")"

[ -f .env ] || { echo "缺 .env：先 cp .env.example .env 并填写"; exit 1; }
set -a; . ./.env; set +a

ROOT="${DATA_ROOT:-/opt/lan-sync/m05}"
SECRET_DIR="$ROOT/secrets"
TOKEN_FILE="$SECRET_DIR/relay-token"

echo "==> 0/6 目录与权限"
mkdir -p "$ROOT/disco" "$ROOT/relay" "$SECRET_DIR"
chmod 700 "$SECRET_DIR"

echo "==> 1/6 生成 relay token（已存在则保留）"
if [ ! -s "$TOKEN_FILE" ]; then
  openssl rand -hex 32 > "$TOKEN_FILE"
  chmod 600 "$TOKEN_FILE"
  echo "    TOKEN_SET=yes（内容不打印）"
else
  echo "    已存在，跳过（TOKEN_SET=yes）"
fi

echo "==> 2/6 构建镜像（从同一 tag，避免与客户端版本漂移）"
if [ ! -d "$ROOT/src/.git" ]; then
  git clone --depth 1 --branch "$SYNCTHING_TAG" https://github.com/syncthing/syncthing.git "$ROOT/src"
fi
( cd "$ROOT/src" && git fetch --depth 1 origin tag "$SYNCTHING_TAG" 2>/dev/null || true
  echo "    commit: $(git rev-parse HEAD)" )
docker build -f "$ROOT/src/Dockerfile.stdiscosrv" -t "lansync/stdiscosrv:$SYNCTHING_TAG" "$ROOT/src"
docker build -f "$ROOT/src/Dockerfile.strelaysrv" -t "lansync/strelaysrv:$SYNCTHING_TAG" "$ROOT/src"

echo "==> 3/6 记录镜像 ID（写进版本锁定表）"
docker image inspect "lansync/stdiscosrv:$SYNCTHING_TAG" --format 'stdiscosrv image: {{.Id}}'
docker image inspect "lansync/strelaysrv:$SYNCTHING_TAG" --format 'strelaysrv image: {{.Id}}'

echo "==> 4/6 启动"
docker compose up -d

echo "==> 5/6 取 discovery 的 Device ID（客户端要固定它）"
sleep 3
docker logs m05-disco 2>&1 | grep -o 'deviceId=[A-Z0-9-]*' | tail -1 || true

echo "==> 6/6 取 relay 的 Device ID（手册 §3.3：默认日志不打印，需要 -debug）"
bash ./get-relay-id.sh

cat <<'EOT'

下一步（手册 §3.4）：
  1) 安全组：8443/TCP 与 22067/TCP 只放行家庭/公司出口 IP；22070 不要对公网开放。
  2) 客户端 relay URI（自行拼装）：
     relay://<RELAY_HOST>:22067/?id=<RELAY_DEVICE_ID>&token=<RELAY_TOKEN>
  3) 采样：python3 relay-status-watch.py --csv status.csv --interval 10
EOT
