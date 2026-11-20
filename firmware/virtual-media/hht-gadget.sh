#!/bin/bash
# HHT Bridge Box - create the USB gadget (configfs) that the customer's PC sees:
# one removable mass-storage LUN (CD-ROM or disk), initially empty. Run as root at boot.
set -euo pipefail

G=/sys/kernel/config/usb_gadget/hhtvm
modprobe libcomposite

if [ -d "$G" ]; then
    echo "gadget already configured"; exit 0
fi

mkdir -p "$G"
cd "$G"
echo 0x1d6b > idVendor        # Linux Foundation (replace with HHT VID/PID for production)
echo 0x0104 > idProduct       # Multifunction Composite Gadget
echo 0x0100 > bcdDevice
echo 0x0200 > bcdUSB

mkdir -p strings/0x409
echo "HHT$(cat /proc/sys/kernel/random/uuid | tr -d - | cut -c1-12)" > strings/0x409/serialnumber
echo "Hammer Hill Technologies" > strings/0x409/manufacturer
echo "HHT Bridge Virtual Media" > strings/0x409/product

mkdir -p configs/c.1/strings/0x409
echo "Virtual media" > configs/c.1/strings/0x409/configuration
echo 250 > configs/c.1/MaxPower

mkdir -p functions/mass_storage.usb0
echo 1 > functions/mass_storage.usb0/stall
echo 1 > functions/mass_storage.usb0/lun.0/removable
echo 1 > functions/mass_storage.usb0/lun.0/cdrom
echo 1 > functions/mass_storage.usb0/lun.0/ro
echo "" > functions/mass_storage.usb0/lun.0/file
ln -s functions/mass_storage.usb0 configs/c.1/

UDC=$(ls /sys/class/udc | head -n1)
if [ -z "$UDC" ]; then
    echo "No USB device controller found. Add dtoverlay=dwc2,dr_mode=peripheral to config.txt and reboot." >&2
    exit 1
fi
echo "$UDC" > UDC
echo "gadget bound to $UDC"
