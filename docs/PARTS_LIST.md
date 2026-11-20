# HHT Bridge Box v0 — Parts List

The v0 hardware is made entirely from off-the-shelf parts. You don't need to solder, build a custom PCB or install drivers on the target computer.

```
 Technician laptop                                     Customer / target PC
 ┌──────────────────────┐                              ┌───────────────────┐
 │ HHT Bridge Console   │◄── USB ── HDMI capture ◄─HDMI┤ HDMI / DP / VGA*  │
 │                      │                              │                   │
 │                      ├── USB ── CH340 ═ CH9329 ── USB ► USB-A port     │
 └──────────────────────┘     (serial)    (HID out)    └───────────────────┘
```
\* DisplayPort or VGA targets need an active DP→HDMI or VGA→HDMI adapter.

## Required

| # | Part | What to search for | Approx. cost | Notes |
|---|------|--------------------|-------------|-------|
| 1 | **HDMI → USB capture dongle (UVC)** | "HDMI USB capture card MS2109" or "MS2130 USB 3.0 capture" | $10–30 | **Must be UVC (it shows up as a webcam).** MS2109 = USB 2.0, 1080p30 MJPEG, fine for BIOS work. MS2130 = USB 3.0, smoother 1080p60. Buy one with an HDMI *loop-out* if you want the customer's monitor to keep working too. |
| 2 | **CH9329 KVM cable** (USB-A ⇄ USB-A with the CH9329 chip in the middle) | "CH9329 cable USB" | $5–15 | **The listing must say CH9329.** A plain USB-A-to-USB-A cable is NOT the same thing and can damage both computers (it joins two USB hosts together). The laptop end is a CH340 USB-serial, so it shows up as a COM port. |
| 3 | HDMI cable, short | — | $5 | Target HDMI out → capture dongle. |

**Total: about $20–50.**

## Recommended

| Part | Why |
|------|-----|
| Small powered USB hub on the laptop side | Laptops with only 1–2 ports. |
| Active DisplayPort→HDMI adapter | For office PCs that have only DisplayPort. |
| VGA→HDMI adapter (powered, with audio not needed) | For old PCs and servers. |
| USB-C → USB-A adapter | For modern laptops. |
| Short labelled pouch / case | Keeps the kit together as a "crash cart" kit. |

## Alternative: build your own CH9329 cable (lab use only)

A CH9329 module ($2–8) wired to a CP2102 or CH340 USB-UART board: TX→RX, RX→TX, GND→GND.
**Do not connect the 5 V pins of both sides together**. That would back-feed power between the laptop and the target. Power the CH9329 from the target side only. Use this only for prototyping, not as a sellable product.

## Before you buy, check

- [ ] The capture dongle lists "UVC" or "driver-free" and supports MJPG 1920×1080.
- [ ] The cable listing says **CH9329** (some say "KVM cable", "USB HID emulator cable").
- [ ] On first plug-in, Windows shows the cable under **Device Manager → Ports (COM & LPT)** as "USB-SERIAL CH340 (COMx)". If it doesn't, install the WCH CH340 driver from wch-ic.com.

## Path to a sellable HHT Bridge Box (future phase)

v1 merges both functions into one enclosure with one USB-C connection to the laptop:
MS2109/MS2130 capture chip + CH9329 (or an RP2040 running HID firmware for features like virtual USB storage) + a USB hub chip + optional ATX power/reset header. Openterface Mini-KVM is an open (OSHWA-certified) reference design for this architecture. If you reuse any of its schematics or code, check its license first.
