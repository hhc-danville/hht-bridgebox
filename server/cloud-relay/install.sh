#!/bin/bash
# Install the HHT Bridge cloud relay on the droplet (Ubuntu 24.04). Run as root:  bash install.sh "Hammer Hill - Danville"
# Adds a systemd service on 127.0.0.1:8450. You then add ONE block to Caddy (printed at the end).
set -euo pipefail
[ "$(id -u)" = 0 ] || { echo "run as root"; exit 1; }
SHOP_NAME="${1:-Hammer Hill Computers}"
HERE=$(cd "$(dirname "$0")" && pwd)

install -d -m 755 /opt/hht-bridge-relay
install -d -m 700 /etc/hht-bridge-relay
install -m 755 "$HERE/hht_bridge_relay.py" /opt/hht-bridge-relay/
id hhtrelay >/dev/null 2>&1 || useradd --system --no-create-home --shell /usr/sbin/nologin hhtrelay

rnd() { head -c 32 /dev/urandom | base64 | tr -dc 'A-Za-z0-9' | cut -c1-32; }
if [ ! -f /etc/hht-bridge-relay/config.json ]; then
    PUB=$(rnd); VIEW=$(rnd); OWNER=$(rnd)
    cat > /etc/hht-bridge-relay/config.json <<EOF
{
  "owner_view_key": "$OWNER",
  "shops": [
    { "id": "shop1", "name": "$SHOP_NAME", "publish_key": "$PUB", "view_key": "$VIEW" }
  ]
}
EOF
    echo "Created config with new keys."
fi
chown -R hhtrelay /etc/hht-bridge-relay
chmod 600 /etc/hht-bridge-relay/config.json

cat > /etc/systemd/system/hht-bridge-relay.service <<'EOF'
[Unit]
Description=HHT Bridge cloud relay (multi-shop bench dashboard)
After=network.target
[Service]
User=hhtrelay
ExecStart=/usr/bin/python3 /opt/hht-bridge-relay/hht_bridge_relay.py --config /etc/hht-bridge-relay/config.json
Restart=always
NoNewPrivileges=yes
ProtectSystem=strict
ProtectHome=yes
PrivateTmp=yes
[Install]
WantedBy=multi-user.target
EOF
systemctl daemon-reload
systemctl enable --now hht-bridge-relay
sleep 1
curl -fsS http://127.0.0.1:8450/health && echo

echo
echo "=== 1) Add this inside your rmm.hammerhillcomputers.com block in /etc/caddy/Caddyfile, ABOVE the final 'handle {', then: systemctl reload caddy"
echo "    handle_path /bridge/* {"
echo "        reverse_proxy 127.0.0.1:8450"
echo "    }"
echo "=== 2) Keys (also in /etc/hht-bridge-relay/config.json):"
python3 - <<'EOF'
import json
c = json.load(open("/etc/hht-bridge-relay/config.json"))
print("  Owner view key (all shops):", c["owner_view_key"])
for s in c["shops"]:
    print("  %s: publish key (bench consoles) = %s   view key (managers) = %s" % (s["name"], s["publish_key"], s["view_key"]))
EOF
echo "=== 3) Dashboard: https://rmm.hammerhillcomputers.com/bridge/   Relay URL for consoles: https://rmm.hammerhillcomputers.com/bridge"
echo "To add another shop, add an entry to config.json and: systemctl restart hht-bridge-relay"
