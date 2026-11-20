# HHT Bridge cloud relay (multi-shop dashboard)

View-only dashboard of every bench in every shop, hosted on the droplet next to the RMM.

1. Copy this folder to the droplet: `scp -r cloud-relay root@204.48.16.17:/root/`
2. `ssh root@204.48.16.17` then `bash /root/cloud-relay/install.sh "Hammer Hill - Danville"`
3. Add the printed `handle_path /bridge/* { reverse_proxy 127.0.0.1:8450 }` block to the `rmm.hammerhillcomputers.com` site in `/etc/caddy/Caddyfile` (above the final `handle {`), then `systemctl reload caddy`.
4. In each bench console: **Share tab → Cloud dashboard**. Relay address `https://rmm.hammerhillcomputers.com/bridge`, paste the shop **publish key**, tick **Publish this bench**.
5. Open `https://rmm.hammerhillcomputers.com/bridge/` and enter the **owner view key** (all shops) or a shop's **view key**.

To add a shop, add an entry to `/etc/hht-bridge-relay/config.json` with new random keys (`head -c 32 /dev/urandom | base64`), then `systemctl restart hht-bridge-relay`.

Checks: `curl https://rmm.hammerhillcomputers.com/bridge/health` should return `{"ok": true}`. Logs: `journalctl -u hht-bridge-relay`.
