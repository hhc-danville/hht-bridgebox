# HHT USB Bridge — Feasibility Summary & Roadmap

## Two product lines

| | **Technician Hardware Edition** (this repo, v0) | **Software Edition** (later) |
|---|---|---|
| Connection | Direct USB + HDMI cables, no network | LAN / internet |
| Target needs software? | **No**. Works on dead, unbootable or wiped PCs | Yes, the agent must be installed and running |
| BIOS / UEFI / POST / boot menus | **Yes** | **No**. Not possible in software |
| Windows install, recovery, Linux, servers | Yes | Only once the OS is up |
| Kernel drivers needed | **None** (UVC + CH340 serial are standard Windows drivers) | Only for USB-over-IP |
| Main risk | Hardware compatibility (dongles, BIOS mouse support) | Security, NAT traversal, driver signing |

## What is software-only vs hardware-dependent

| Capability | Software only? | Notes |
|---|---|---|
| View/control a running Windows PC over LAN | ✅ | Desktop Duplication / Windows Graphics Capture + SendInput |
| File transfer, clipboard, KVM-style mouse sharing | ✅ | Needs an agent on both PCs |
| View BIOS/UEFI, POST, boot menu, failed boot | ❌ hardware | Needs HDMI capture (this repo) |
| Type into BIOS / pick boot device | ❌ hardware | Needs a USB HID emulator (CH9329) |
| Control a PC with no agent installed | ❌ hardware | Same |
| Remote power/reset | ❌ hardware | Future: ATX header relay in Bridge Box v1 |
| Make a remote USB device appear local | ⚠️ software + signed kernel driver | USB-over-IP virtual bus driver (USB/IP-style). Highest cost and risk: do last, starting with one device class |

## Roadmap (proposed reorder of the master plan)

1. **HW-v0 (now):** Bridge Console + off-the-shelf parts. Bench-test on real customer machines (`docs/TEST_PLAN.md`).
2. **v0.3 (built, untested on hardware):** reports, recording, auto BIOS entry, OCR findings, macros, bench alerts, remote viewing and dashboard, controller and virtual-media support. Next: signed MSIX installer.
3. **Integration:** push session logs/screenshots to the RMM ticket and TechDiag Pro.
4. **SW Phase 1–3:** discovery, pairing, LAN remote desktop, files (original master-prompt phases).
5. **HW-v1:** single-box PCB (capture + HID + hub + ATX control) built on the same console software.
6. **SW Phase 4:** USB-over-IP, one class first (e.g. mass storage or serial), with a signed driver.

## Hard limits (tell customers honestly)

- Software alone can never show a powered-off, POSTing or non-booting PC.
- Ctrl+Alt+Del and Win+L on the technician laptop can't be intercepted. Use the console button or Right Ctrl + Del.
- Some BIOS setups only accept a relative (boot-protocol) mouse, and a few accept none. The keyboard always works.
- Typing text assumes the target uses a US keyboard layout.
- HDCP-protected sources (some laptops' video playback) may show black. BIOS/OS screens are not HDCP-protected.
