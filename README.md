# HHT Bridge Console: Bridge Box (Technician Hardware Edition) v0.4

**Hammer Hill Technologies · HHT USB Bridge product family**

Plug a customer's PC (even a dead, unbootable or wiped one) into your technician laptop. You can see its screen from the first POST frame, get into BIOS/UEFI and boot menus, and use your keyboard and mouse on it. **The target needs no software, network or drivers.** v0.3 adds reports, recording, automatic BIOS entry, screen reading, macros, bench alerts, remote viewing, a multi-bench dashboard, and support for the Bridge Box power/USB controller and virtual-media module.

Hardware: **HDMI→USB UVC capture dongle** + **CH9329 cable** (≈ $30, see [`docs/PARTS_LIST.md`](docs/PARTS_LIST.md)). Optional add-ons in [`docs/HARDWARE_V1.md`](docs/HARDWARE_V1.md).

---

## Status: what is verified and what is not

| Part | State |
|---|---|
| **Core library** (`HHT.Bridge.Core`): everything below that isn't Windows UI | **Built and tested: 52/52 tests pass** (.NET 10, Linux). They include end-to-end tests over **real HTTPS + WebSockets** (access key, lockout, consent, single controller, revoke, customer sign-off), **UDP discovery**, a **pinned bench proxy**, the **real virtual-media agent** and **real cloud relay** (both Python, run during the tests), a **fake RMM implementing your real v1 ticket API** (incl. duplicate-note handling and the offline queue), the **AI backend contract**, **license signing/forgery**, **beep-code detection on synthetic audio**, **playbooks**, **boot tests**, **BIOS compare** and **time tracking**. |
| **Controller firmware logic** (`firmware/rp2040-controller`, v1.1) | **Unit-tested on a PC** (g++): presses, 6 s hold, no overlap, USB switch gap, LED hold, **thermistor maths and fan RPM**, garbage/overflow, millis wrap. The RP2040 sketch itself is not compiled or flashed here. |
| **Cloud relay** (`server/cloud-relay`) | **Tested end-to-end** (multi-shop scoping, keys, image round trip, rate limit, JPEG check). Not yet deployed to the droplet. |
| **Virtual-media agent** (`firmware/virtual-media`) | **Tested end-to-end in dry-run mode**, i.e. everything except writing to the Pi's USB gadget (configfs). Not run on a Pi. |
| **Web viewer / dashboard JavaScript** | Syntax-checked with Node. The server side is integration-tested. Not yet exercised in a real browser. |
| **WPF console** (`HHT.Bridge.TechConsole`) | **Not compiled on Windows yet.** It **type-checks with zero errors** against API stubs (`tools/typecheck/run.sh`), and all 81 XAML event handlers and 137 named controls resolve. Expect small fixes where a WPF/OpenCvSharp signature differs from the stubs. |
| **Hardware** (BIOS visibility, controller board, Pi module, one-box PCB) | **Untested.** Follow `docs/TEST_PLAN.md` and `docs/HARDWARE_V1.md`. |
| Installer / code signing | Not started. Builds are **unsigned development builds**. |

## Features

| # | Feature | Where | Needs |
|---|---|---|---|
| – | Live video from POST onward, keyboard/mouse control, special keys, type text, target LEDs, screenshots, audit log | Connect / Keys tabs | v0 kit |
| 1 | **Service reports:** HTML report with timeline, findings, notes and embedded screenshots. Print to PDF for the customer or ticket. Typed text is never included. | Tools → Create report | v0 kit |
| 2 | **Session recording:** MP4 (falls back to AVI), real-time paced, to `Videos\HHT Bridge` | Tools → Start recording | v0 kit |
| 3 | **Automatic BIOS / boot-menu entry:** 15 brand profiles. Taps the right keys until the firmware screen is detected by OCR (60 s max). | Keys tab | v0 kit |
| 4 | **Screen reading (OCR):** built-in Windows OCR. Pulls out service tag/serial, model, BIOS version, CPU, RAM and boot device, and flags no bootable device, SMART failure, BitLocker recovery, stop codes, CMOS/fan/thermal errors. | Tools → Read screen | v0 kit |
| 5 | **Macro library:** 22 built-in macros (Device Manager, admin CMD, SFC, DISM, CHKDSK, reboot to firmware/WinRE, Safe Mode on/off, OOBE, BitLocker status…), plus your own in `macros.json`. Supports *wait for text on screen*. | Macros tab | v0 kit |
| 6 | **Bench alerts:** screen idle N min, screen changed after being still, video lost, or text appears. Sent to your phone via **ntfy** or any webhook, plus a window flash and sound. | Alerts tab | v0 kit |
| 7 | **Remote viewing:** HTTPS web viewer for any browser or tablet. Access key, lockout after 10 bad keys, view-only by default. **Control only after you tick "Allow"**, one controller at a time, "Take back control" button. | Share tab | LAN |
| 8 | **Multi-bench dashboard:** benches find each other on the LAN (UDP beacons) and show live thumbnails on one page. Several consoles can run on one PC with `--profile bench2`. | Share → Open dashboard | LAN |
| 9 | **Virtual USB drive:** upload an ISO to the Pi module, mount it as a CD-ROM (or .img as a USB disk), and boot the customer PC from it | Hardware tab | v0.6 Pi module |
| 10 | **Power / reset / force-off** + power and disk LED sensing | Hardware tab | v0.5 controller |
| 11 | **Switchable USB port** (laptop ⇄ customer PC) | Hardware tab | v0.5 controller |
| 12 | **One-box design:** PCB specification, isolation, ESD, power budget, compliance path | `docs/HARDWARE_V1.md` | PCB project |

### v0.4 additions

| Feature | Where | Edition |
|---|---|---|
| **RMM ticket sync** (your real `/api/v1` contract): look up a ticket (customer, device, issue), flag a serial mismatch, **post the whole session** (work log + report + status) in one idempotent call, attach screenshots, **offline queue** when the RMM is unreachable | Ticket tab | Shop |
| **Billable bench time:** counts active work, ignores idle gaps > 5 min, rounds up to 15-min increments, writes `timesheet.csv`, posts with the session | Ticket tab | Pro |
| **Guided repair playbooks:** no bootable device, BitLocker recovery, Automatic Repair loop, Safe Mode, CMOS/BIOS reset. Steps run macros/keys and branch on what's on screen | Guide tab | Pro |
| **AI assistant:** sends the screen text, findings and ticket issue to **your existing droplet AI backend** (`/ai/analyze`, no server changes). Suggestions only, never executes | Guide tab | Shop |
| **Hands-off hardware tests:** mount a test ISO (Memtest86+, MemTest86, custom), restart the PC, open the boot menu, pick the virtual drive by reading the menu, watch for PASS/FAIL, screenshot, phone alert | Hardware tab | Shop |
| **Windows install assist:** experimental macros for Setup screens and OOBE (wait-for-screen steps) | Macros tab | Pro |
| **BIOS settings before/after:** OCR each page at check-in and pickup and list the changed settings in the report | Tools tab | Pro |
| **Screenshot annotation:** red pen and highlighter | Tools tab | Free |
| **Customer sign-off on a tablet:** 6-digit single-use code, name + signature + agreement, embedded in the report. The customer never gets the shop key | Tools tab | Pro |
| **Agent hand-off:** one click opens elevated PowerShell on the customer PC and installs your RMM/TechDiag agent from an https URL (injection-safe) | Macros tab | Pro |
| **Beep codes:** listen through a mic, detect the beep pattern, look up AMI/Award/Phoenix/Dell/IBM meanings, save the WAV | Tools tab | Shop |
| **Sensor clip:** 2 temperature probes + fan RPM (controller firmware 1.1) | Hardware tab | Shop |
| **Cloud dashboard:** benches publish snapshots to a relay on your droplet. Owner sees every shop, managers see their own. View only | Share tab + `server/cloud-relay` | Shop |
| **Licensing:** Free / Pro / Shop, offline-verified ECDSA keys, issuer tool for Hammer Hill | License tab + `tools/HHT.License.Issuer` | — |
| **Wi-Fi Bridge Box:** design (Pi 5 + same core) | `docs/HARDWARE_V1.md` | — |

## Build & run (Windows 10 2004+ / 11)

```powershell
winget install Microsoft.DotNet.SDK.10
cd HHT.BridgeBox
.\build.ps1            # restore + build + 52 core tests
.\build.ps1 -Run       # build, test, launch
.\build.ps1 -Publish   # self-contained folder publish\bridge (copy next to HHC-TechTool.exe)
.\tools\firewall.ps1   # (admin, once per bench) allow Share on Private networks: TCP 8443 + UDP 47811
```

Command line: `HHTBridgeConsole.exe [--ticket 10423] [--tech Steve] [--port COM5 --baud 9600] [--profile bench2]`

Checks you can run on Linux/macOS: `dotnet run --project tests/HHT.Bridge.Core.Tests` and `bash tools/typecheck/run.sh`. The firmware test command is in `.github/workflows/build.yml`. Push to GitHub and the workflow runs the real Windows build plus all of these checks.

### First run
1. Windows Settings → Privacy & security → **Camera → Let desktop apps access your camera: On**.
2. Device Manager → Ports should show **USB-SERIAL CH340 (COMx)**.
3. **Start video**, **Auto-detect**, then click the video to take control. Tap **Right Ctrl** to release.
4. For phone alerts, install the ntfy app, subscribe to a hard-to-guess topic, and paste `https://ntfy.sh/<topic>` (or your own ntfy server) into the Alerts tab.

## Licensing

The console runs as **Free** with no key. Paste a key on the **License** tab to unlock Pro or Shop. Keys are signed with Hammer Hill's private ECDSA key. The console holds only the public key, so keys can't be forged or edited (tested).

```
dotnet run --project tools/HHT.License.Issuer -- issue --key hht-license-private.pem --to "Customer Shop" --edition Pro --seats 3 --expires 2027-12-31
```
The private key and Hammer Hill's own Shop key are delivered **separately** (not in this project). Keep the private key offline. If it leaks, anyone can mint keys: generate a new pair with `keygen`, put the new public key in `LicenseVerifier.PublicKeyPem` and re-issue keys. Seat counts aren't enforced yet (that needs an online activation check).

## Security model

- **Nothing is sent to the customer PC until the technician clicks into the video**, starts a macro, or presses a button. Keys and buttons are released on focus loss, capture release and disconnect.
- **Typed text and keystrokes are never logged or put in reports.** Macros are logged by name only.
- **Sharing is off by default.** The server is HTTPS-only with a self-signed certificate (private key DPAPI-protected) and needs the shop access key (DPAPI-protected). There's a lockout after 10 wrong keys per address and security headers (CSP, no-referrer, no framing).
- **Remote control is off every session** until the technician ticks *Allow*. Only one remote controller at a time. Taking back control, or un-ticking *Allow*, cuts it off immediately and releases all keys.
- Benches trust each other's snapshots only by **pinned certificate fingerprint**. Beacons carry no secrets.
- The virtual-media module requires HTTPS with a pinned fingerprint plus a token. Uploads are name-checked (.iso/.img only, no paths).
- The power/reset controller drives the motherboard **only through opto-isolators**. Firmware starts with everything released, never overlaps presses, and is watchdog-protected.
- RMM, AI-backend, cloud and virtual-media secrets are stored **DPAPI-protected**. All of them must be HTTPS. The RMM key is per technician and scoped by the RMM.
- The AI assistant only **suggests**. The console never types or runs what the AI says.
- Customer sign-off uses a **6-digit, single-use, 30-minute code**. Wrong codes count toward the lockout, and the code can't open the API.
- The cloud relay is **view only** by design. Remote control stays on the shop LAN with the technician's consent.
- Agent hand-off only accepts plain `https://` URLs and simple arguments, so nothing unexpected can be typed (tested against quote/command injection).
- The console runs as a normal user (no admin).

## Project layout

```
HHT.BridgeBox/
├── src/HHT.Bridge.Core/            net10.0, no NuGet dependencies, cross-platform
│   ├── Ch9329/ Hid/ Input/         protocol, key maps, input routing
│   ├── Automation/                 key chords, macros + runner, BIOS profiles + entry sequencer
│   ├── Text/BiosTextParser.cs      OCR text → findings
│   ├── Monitoring/BenchAlerts.cs   activity monitor + ntfy/webhook notifier
│   ├── Reports/SessionReport.cs    HTML report writer
│   ├── Remote/                     HTTPS server, WebSocket control, viewer/dashboard pages, UDP discovery, TLS helpers
│   ├── Hardware/                   Bridge Box controller client, virtual-media client
│   └── Logging/ Settings/
├── src/HHT.Bridge.TechConsole/     WPF app (MainWindow split by feature: .Features/.Automation/.Share/.Hardware.cs)
├── tests/HHT.Bridge.Core.Tests/    52 tests (dependency-free runner)
├── server/cloud-relay/             droplet relay for the multi-shop dashboard (Python stdlib + installer)
├── tools/HHT.License.Issuer/       issues license keys (needs Hammer Hill's PRIVATE key, kept outside this repo)
├── firmware/rp2040-controller/     controller sketch + portable logic + host unit test
├── firmware/virtual-media/         Pi USB-gadget agent, gadget script, installer
├── techtool/                       Bridge Box tab for HHC-TechTool
├── tools/                          typecheck/, firewall.ps1
└── docs/                           PARTS_LIST, TEST_PLAN, FEASIBILITY, HARDWARE_V1
```

## Dependencies & licences

| Package | Licence | Used for |
|---|---|---|
| OpenCvSharp4 + runtime.win 4.10.0.20241108 | Apache-2.0 (OpenCV Apache-2.0) | Capture, JPEG, recording, change detection |
| System.IO.Ports, System.Management, System.Security.Cryptography.ProtectedData 10.0.0 | MIT | COM ports, port names, DPAPI |
| Windows.Media.Ocr (part of Windows) | — | OCR |
| Pi agent: Python 3 standard library; RP2040: Arduino-Pico core (LGPL-2.1, dynamically linked core) | — | Hardware modules |

## Known limitations

- The WPF console is not yet compiled on Windows (see Status). The first build may need small fixes.
- Ctrl+Alt+Del and Win+L on the laptop can't be intercepted. Use the button or Right Ctrl + Del. Right Ctrl is the host key.
- Typing text assumes a US layout on the target. Live keys are positional.
- OCR quality depends on the capture resolution and the BIOS font. It's good on 1080p capture and weaker on tiny or low-contrast text.
- The web viewer uses MJPEG (≈ 1–3 Mbit/s at 1280 px, 15 fps). It's fine on a LAN and not meant for the internet. Use the RMM or a VPN for off-site access.
- Browsers warn about the self-signed certificate once per bench. Check the fingerprint shown in the Share tab.
- Phone alerts go through ntfy/webhooks. Native Windows toast notifications aren't used (the app is unpackaged).
- Items 9–12 need hardware that hasn't been built yet. The one-box PCB needs an EE review, prototypes and FCC/CE compliance before sale.
