# HHT Bridge Box: hardware roadmap (items 9–12)

**Status: design plus firmware.** The software for these features is written and tested here. The controller logic passes host unit tests, and the virtual-media agent was tested end-to-end in dry-run mode. **None of the hardware below has been built or bench-tested yet.** Each stage lists what to build and how to verify it before relying on it.

| Stage | What it adds | Parts | Effort |
|---|---|---|---|
| **v0** (today) | Video + keyboard/mouse | MS2109 dongle + CH9329 cable | Off the shelf |
| **v0.5 "Controller"** | Item 10 power/reset control and item 11 switchable USB port | Raspberry Pi Pico + 2× PC817 opto modules + USB 2.0 mux breakout | Hand-wired, ~$20 |
| **v0.6 "Virtual Media"** | Item 9: boot the customer PC from an ISO on your laptop | Raspberry Pi 4 or 5 (or Zero 2 W) running `firmware/virtual-media` | Off the shelf + installer, ~$45–80 |
| **v1 "One box"** | Item 12: everything above in one enclosure on one USB-C cable | Custom PCB (spec below) | PCB design + fab + certification |

---

## v0.5 Controller (items 10 and 11)

Firmware: `firmware/rp2040-controller/` (`HHT_BridgeController.ino` + `BridgeController.cpp`). Console: **Hardware** tab → *Power & USB controller* → **Find controller**.

### Wiring (Raspberry Pi Pico)

| Pico pin | Goes to | Notes |
|---|---|---|
| GP2 | PC817 #1 LED anode via 330 Ω, cathode → GND | Opto #1 transistor (collector/emitter) wired **in parallel** with the case power button on the motherboard `PWR_SW` header pins. Use a header Y-splitter so the case button still works. |
| GP3 | PC817 #2 LED via 330 Ω | Opto #2 transistor in parallel with `RESET_SW`. |
| GP6 | TS3USB221 `S` (select) | LOW = shared port → technician laptop, HIGH = → customer PC. |
| GP7 | TS3USB221 `/OE` | HIGH disconnects both sides (used for a clean 250 ms unplug when switching). |
| GP10 | PC817 #3 output (collector, pull-up enabled) | Opto #3 LED across the motherboard `PWR_LED` header with a 1 kΩ series resistor. Reads "power on". |
| GP11 | PC817 #4 output | Same, across `HDD_LED`. |
| GND | Opto output emitters, mux GND | **Never** connect Pico GND to the target's front-panel pins. The optos are the isolation barrier. |

The shared USB port's **VBUS comes from the laptop side only**, through a current-limited load switch (TPS2051B or similar, 500 mA). Only the D+/D− data lines go through the mux. That way the two computers' 5 V rails are never joined.

### Bench verification (do this before connecting a customer PC)
1. Flash the Pico. In the Arduino Serial Monitor (115200), `HHT?` should answer `HHT-BRIDGEBOX-CTRL 1.0 ...`.
2. With a multimeter in continuity mode across opto #1's output, `PWR SHORT` should beep for ~0.4 s and `PWR LONG` for ~6 s.
3. On a spare PC: `PWR SHORT` powers it on, `PWR LONG` forces it off, `RST` resets it. PWRLED in `STAT?` should follow the PC.
4. Put a flash drive in the shared port. `USB TARGET` makes it appear on the target, `USB HOST` brings it back to the laptop. Check that each side sees a clean removal.
5. Unplug the Pico mid-press. The watchdog/brown-out must leave the button **released**.

---

## v0.6 Virtual Media (item 9)

Agent and installer: `firmware/virtual-media/`. Console: **Hardware** tab → *Virtual media*.

```
 Laptop ──Ethernet / shop LAN──► Raspberry Pi 4/5 ──USB-C (gadget mode)──► customer PC USB port
            (HTTPS 8444)            hht-vmedia agent                         sees "HHT Bridge Virtual Media"
                                                                             CD-ROM or USB disk
```

1. Flash Raspberry Pi OS Lite 64-bit and enable SSH. Copy the `virtual-media` folder over and run `sudo bash install.sh`, then `sudo reboot`.
2. The installer prints the **URL, token and certificate fingerprint**. Enter them in the console.
3. **Upload ISO** (stored on the Pi's SD card or SSD), then **Mount as CD-ROM**. Power on the target, open its boot menu (the console's boot-menu button does this), and pick the HHT device.
4. Use **Mount as USB disk** for `.img` images, e.g. a Ventoy or memtest image written as a raw disk.

**Board notes:**
- **Pi 4/5:** the USB-C power port is also the gadget port. Feed it from the target's USB-C, or use a USB-C power/data splitter so the Pi gets enough power. A Pi 5 can't run from a laptop USB-A port.
- **Pi Zero 2 W:** use the micro-USB *data* port. It's USB 2.0 high-speed (~30 MB/s), so a 6 GB Windows ISO installs fine, just slower.
- **Unsigned VID/PID:** the gadget uses the Linux Foundation test IDs (1d6b:0104). Replace them with HHT's own USB-IF VID/PID before selling.

**Verify:** mount an Ubuntu ISO, then boot a test PC from it (legacy and UEFI). Mount Windows 11 media and start Setup. Eject during Setup, confirm Setup reports the missing media, then remount.

---

## v1 "One box" (item 12): PCB specification

Goal: one enclosure, **one USB-C cable to the laptop**, plus HDMI-in, a USB-A to the target, a front-panel header cable and a switchable USB-A port.

### Block diagram
```
 Laptop USB-C (USB 3.2 Gen1)
   └─ USB3 hub VL817 (or GL3523)
        ├─ MS2130   HDMI → UVC capture (USB3, 1080p60)   ◄── HDMI IN (+ optional HDMI loop-out via IT66121/splitter)
        ├─ CH343P   USB-UART  ──UART──► CH9329 ──USB──► TARGET USB-A  (CH9329 powered from TARGET VBUS only)
        ├─ RP2040   controller (USB CDC) ──► 4× PC817 opto ──► front-panel header cable (PWR/RST/PWRLED/HDDLED)
        └─ TS3USB221 data mux ◄── RP2040 GP6/GP7 ──► SHARED USB-A (VBUS via TPS2051B from laptop side)
                            └──────────────────────────► TARGET-side upstream (2nd USB-A/C to target)
 Optional "Pro" variant: replace CH9329 with a Pi CM5 module providing HID + virtual media gadget on the target port.
```

### Key requirements
- **Isolation:** no copper connection between laptop GND/VBUS and target VBUS except through the CH9329's target-side USB. Front-panel signals go only through optocouplers (≥ 3.75 kV PC817 class). Add a ferrite plus 1 MΩ/1 nF between the two grounds to control ESD and EMI. Get this checked by an EE.
- **ESD:** USBLC6-2SC6 (or TPD4E05U06) on every external USB data pair; HDMI ESD array (TPD12S016 class) on HDMI-in.
- **Power budget:** USB-C 5 V / 900 mA (USB 3) from the laptop. MS2130 ~1.6 W, hub ~0.6 W, RP2040 0.1 W, shared port 2.5 W (limited). Add a USB-C PD sink (or a barrel jack) if the shared port must power bus-hungry drives.
- **Firmware IDs:** the CH9329 VID/PID/strings can be customised with WCH's configuration tool (show "HHT Bridge Keyboard/Mouse"). The RP2040 uses HHT's own VID/PID.
- **Console compatibility:** no software changes are needed. The console finds the UVC device, the CH9329 COM port and the controller COM port separately, exactly as in v0 + v0.5.

### Path to a product
1. **EE schematic review**, especially isolation, ESD and USB3 layout (90 Ω differential pairs, length matching).
2. KiCad schematic + 4-layer PCB, then a prototype run of 5–10 boards (JLCPCB/PCBWay with assembly).
3. Bench validation using the v0.5/v0.6 checklists above, plus the console's TEST_PLAN.md, on ≥ 20 different PCs and laptops (desktop, laptop and DP-only machines).
4. **Compliance:** FCC Part 15 Subpart B (unintentional radiator, SDoC) for the US; CE (EMC + RoHS) and UKCA if selling in Europe/UK. A USB-IF VID ($6,000 one-time) or a sub-licensed PID.
5. Enclosure (3D-printed for pilot, then injection-moulded), labelling, and a warranty/RMA process.

Realistic timeline with a contract EE: 8–12 weeks to a validated pilot batch.

---

## Sensor clip (item 12) — add to the v0.5 controller

Firmware 1.1 adds `SENS?` → `SENS T1=41.5 T2=NA FAN1=1320`. The console shows it on the Hardware tab.

| Part | Wiring |
|---|---|
| 2× 10 kΩ NTC thermistor probes (B=3950, epoxy bead on a lead) | Probe between GP26 (or GP27) and GND, plus a 10 kΩ 1 % resistor from GP26 (GP27) to 3V3. Tape the bead to the heatsink base, VRM or SSD. |
| Fan tach (3/4-pin fan, yellow wire) | Tach → 1 kΩ → GP12, with a 10 kΩ pull-up to 3V3. **Use a 4-pin fan Y-splitter and take ONLY the tach wire; leave the fan's 12 V to the motherboard.** |

**Grounding:** a fan tach is referenced to the target's ground, and the Pico is referenced to the laptop. Run the tach through a PC817 opto (tach pulls the opto LED via 1 kΩ from the fan's own +12 V/5 V), or use a separate battery-powered clip. Never join the two grounds directly. The NTC probes only touch metal and are insulated, so they're safe.

**Verify:** at room temperature, `SENS?` should read ~20–25 °C. Pinch the probe and it should rise. A 1,200 RPM fan should read 1,150–1,250.

## Beep codes (item 11)

PC speaker beeps are **not** carried over HDMI. Use the laptop's built-in mic, or better, a $10 USB lavalier mic clipped inside the case near the speaker or buzzer. The console's **Tools → Listen for beep codes** records up to 60 s at 16 kHz, finds the beeps, works out the pattern (e.g. "1 long, 3 short" or "1-3-1"), and suggests meanings from AMI / Award / Phoenix / Dell / IBM-Lenovo tables. Pick the BIOS brand on the Keys tab so the right table ranks first. The audio is saved as a WAV and attached to the report.

HDMI audio from the capture dongle (MS2109 "USB Digital Audio") can also be recorded for checking the target's sound output.

## Wi-Fi Bridge Box (item 13) — design

Goal: a battery box that sits at the customer's PC, so the technician works from anywhere in the shop (or on site) without cables to the laptop.

```
 Customer PC ──HDMI──► USB capture ─┐
             ◄──USB── CH9329 ◄─UART─┤  Raspberry Pi 5 (or CM5)  ──Wi-Fi 5/6──► shop LAN ──► Bridge Console web viewer / dashboard
             ◄──USB-C gadget────────┤  + 5,000 mAh USB-C PD power bank
 front panel ◄──opto── RP2040 ◄─USB─┘
```

- **Software:** HHT.Bridge.Core already runs on Linux (all 52 tests pass there). Its HTTPS viewer, WebSocket control, dashboard beacons, macros, BIOS entry, alerts, reports and cloud publishing are cross-platform. The missing piece is a headless Linux host. It needs `v4l2` capture (via ffmpeg or `OpenCvSharp4.runtime.linux-arm64`), the same CH9329 serial transport (`/dev/ttyUSB0`), and Linux's own OCR (Tesseract) instead of Windows OCR. Estimated effort: 1–2 weeks.
- **Virtual media for free:** the Pi 5's USB-C port is the gadget port, so the v0.6 module merges into this box.
- **Power:** Pi 5 + capture + CH9329 draw about 6–8 W, so a 5,000 mAh PD bank lasts 2–3 hours. Alternatively, power it from the target's USB-C or a wall adapter at the bench.
- **Security:** the box joins the shop Wi-Fi as a normal client (no hotspot by default) and uses the same access key, consent and TLS model as the console. Remote control still needs the technician's "Allow" on the box's local page or a hardware button.
- **Verify before building:** Wi-Fi latency (aim < 60 ms keyboard round trip), MJPEG at 1280×720 15 fps over Wi-Fi 5, battery runtime, and heat in an enclosed case.
