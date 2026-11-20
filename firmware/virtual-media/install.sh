#!/bin/bash
# HHT Bridge Box virtual media module - installer for Raspberry Pi OS (Bookworm, 64-bit Lite).
# Supported boards: Pi 4 / Pi 5 (USB-C port = gadget port), Pi Zero 2 W (USB data port), CM4/CM5.
# Run:  sudo bash install.sh
set -euo pipefail
[ "$(id -u)" = 0 ] || { echo "run with sudo"; exit 1; }

HERE=$(cd "$(dirname "$0")" && pwd)
CFG=/boot/firmware/config.txt; [ -f "$CFG" ] || CFG=/boot/config.txt

echo "== Enabling USB device (gadget) mode =="
grep -q '^dtoverlay=dwc2' "$CFG" || echo 'dtoverlay=dwc2,dr_mode=peripheral' >> "$CFG"
grep -q '^libcomposite' /etc/modules || echo libcomposite >> /etc/modules

echo "== Installing files =="
install -d -m 755 /opt/hht-vmedia /etc/hht-vmedia /var/lib/hht-vmedia/images
install -m 755 "$HERE/hht_vmedia_agent.py" /opt/hht-vmedia/
install -m 755 "$HERE/hht-gadget.sh" /opt/hht-vmedia/

if [ ! -f /etc/hht-vmedia/token ]; then
    head -c 24 /dev/urandom | base64 | tr -dc 'A-Za-z0-9' | cut -c1-24 > /etc/hht-vmedia/token
    chmod 600 /etc/hht-vmedia/token
fi
if [ ! -f /etc/hht-vmedia/cert.pem ]; then
    openssl req -x509 -newkey rsa:2048 -nodes -days 1825 \
        -subj "/CN=hht-vmedia/O=Hammer Hill Technologies" \
        -keyout /etc/hht-vmedia/key.pem -out /etc/hht-vmedia/cert.pem 2>/dev/null
    chmod 600 /etc/hht-vmedia/key.pem
fi

cat > /etc/systemd/system/hht-gadget.service <<'EOF'
[Unit]
Description=HHT Bridge virtual media USB gadget
After=sys-kernel-config.mount
[Service]
Type=oneshot
RemainAfterExit=yes
ExecStart=/opt/hht-vmedia/hht-gadget.sh
[Install]
WantedBy=multi-user.target
EOF

cat > /etc/systemd/system/hht-vmedia.service <<'EOF'
[Unit]
Description=HHT Bridge virtual media agent
After=network-online.target hht-gadget.service
Wants=network-online.target
Requires=hht-gadget.service
[Service]
ExecStart=/usr/bin/python3 /opt/hht-vmedia/hht_vmedia_agent.py
Restart=on-failure
[Install]
WantedBy=multi-user.target
EOF

systemctl daemon-reload
systemctl enable hht-gadget.service hht-vmedia.service

FP=$(openssl x509 -in /etc/hht-vmedia/cert.pem -noout -fingerprint -sha256 | cut -d= -f2 | tr -d :)
IP=$(hostname -I | awk '{print $1}')
echo
echo "================ enter these in the Bridge Console (Hardware tab) ================"
echo " URL:          https://${IP:-<pi-address>}:8444"
echo " Token:        $(cat /etc/hht-vmedia/token)"
echo " Fingerprint:  $FP"
echo "=================================================================================="
echo "Reboot now to activate gadget mode:  sudo reboot"
