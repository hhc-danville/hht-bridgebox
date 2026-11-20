# HHT Bridge Console v0 — Manual Test Plan

Record results in the table at the bottom. Include the laptop model, Windows build, capture dongle and cable you used. **Don't mark a test as passing until you've seen it work.**

## Setup
1. Laptop: Windows 10 22H2 or Windows 11, .NET 10 Desktop Runtime installed.
2. Plug in the capture dongle and the CH9329 cable's **laptop end**.
3. Device Manager: dongle under *Cameras* or *Imaging devices*, cable under *Ports* as CH340.
4. Windows Settings → Privacy & security → Camera → **"Let desktop apps access your camera" = On**.
5. Target PC: HDMI → dongle, CH9329 cable's **target end** → any target USB port.

## Tests

| ID | Test | Steps | Expected |
|----|------|-------|----------|
| V1 | Video device listed | Launch app | Capture dongle appears in Video combo (pre-selected if its name looks like a capture device). |
| V2 | Live video | Start video with target powered on in Windows | Target desktop visible. Status bar shows ~30 fps (MS2109) at 1920x1080. |
| V3 | **BIOS / POST visible** | Reboot target | POST logo and BIOS output visible, without any software on the target. |
| V4 | Mode fallback | Choose 1920x1080@60 on an MS2109 | Log warns the device used a different mode; video still works. |
| V5 | Device busy | Open Windows Camera app on the dongle first, then Start video | Clear error message, no crash. |
| V6 | Unplug video | Unplug dongle while running | Log shows "No frames…" warning, no crash. Replug, Refresh, Start works. |
| C1 | Auto-detect | Click Auto-detect | Finds COMx at 9600 (or 115200). Chip version shown. |
| C2 | Target enumerated | Target powered on | Control pill green. "Target sees the keyboard/mouse". |
| C3 | Target off | Power target off | Within ~5 s pill turns amber: "target not enumerated". |
| C4 | LED sync | Toggle Caps Lock on target | CAPS LED in the header follows the target. |
| K1 | Basic typing | Click video, type in Notepad on target | Correct text. Holding a key auto-repeats on target. |
| K2 | **BIOS keys** | Reboot target, spam Del/F2 via keyboard while captured | BIOS setup opens. Arrow keys and Enter navigate it. |
| K3 | Boot menu | F12/F11/F8 button during POST | One-time boot menu opens. |
| K4 | Win / Alt+Tab | While captured press Win, Alt+Tab | They act on the TARGET, not the laptop. |
| K5 | Ctrl+Alt+Del | Button, and Right Ctrl + Del | Target shows the security screen. Laptop is unaffected. |
| K6 | Release | Tap Right Ctrl | Banner disappears. Keys go to laptop again. No stuck keys on target. |
| K7 | Focus loss | While holding Shift on captured target, Alt-tab away on laptop via mouse click elsewhere | Capture releases, target receives all-keys-up (type on target: no stuck Shift). |
| K8 | Type text | Paste "Hello World! 123 @#$" into Type text → Type it, target in Notepad | Exact text appears. Log says "Typed N characters" without the text. |
| M1 | Absolute mouse | Absolute mode, move over video in Windows target | Target pointer follows exactly. Click, double-click, right-click and drag work. |
| M2 | Letterbox | Resize window so black bars appear, click in bars | Nothing sent. |
| M3 | Wheel | Scroll in a browser on target | Scrolls. |
| M4 | Relative in BIOS | BIOS with mouse support (e.g. ASUS/MSI UEFI), try Absolute, then Relative | Note which works for each board. Record the results. |
| T1 | Screenshot | Right Ctrl + S, and the button | PNG in Pictures\HHT Bridge, matches screen. |
| T2 | Full screen | Right Ctrl + F twice | Enters and exits full screen. Sidebar restored. |
| T3 | Session log | Open Session logs | Log has connect/disconnect, special keys, screenshots and typing counts. **No typed content.** |
| R1 | Cable unplug | Unplug CH9329 laptop end while captured | Error logged, capture released, app stays usable. Replug + Connect works. |
| R2 | Restart | Close and reopen app | Last video device and port reopen automatically. |

## Results

| ID | Pass/Fail | Laptop / Windows build | Dongle | Cable | Notes |
|----|-----------|------------------------|--------|-------|-------|
|    |           |                        |        |       |       |

## v0.3 feature tests

| ID | Test | Steps | Expected |
|----|------|-------|----------|
| F1 | Report | Take 2 screenshots, read the screen on a BIOS page, add notes, Tools → Create report | Browser opens a report with session info, findings, activity, both screenshots embedded. **No typed text.** Print to PDF works. |
| F2 | Recording | Start recording, use the target 1 min, stop | MP4 (or AVI) in Videos\HHT Bridge plays at real-time speed. |
| F3 | Auto BIOS entry | Keys → pick brand → Enter BIOS setup, then power on the target | Setup opens. Status says "Firmware screen detected" (or stop manually). Repeat with Open boot menu. Record the brands tested. |
| F4 | OCR findings | On a Dell/HP/Lenovo BIOS main page: Read screen now | Service tag/serial, model and BIOS version are listed. Unplug the boot drive, reboot, read again: "No bootable device" is flagged. |
| F5 | Macros | Windows target: run Device Manager, Admin Command Prompt, SFC | Each opens correctly. UAC is accepted with Alt+Y. The log shows macro names only. Confirm-type macros ask first. |
| F6 | Custom macro | Edit my macros → change the example → Reload | It appears and runs. A bad key name shows a warning instead of crashing. |
| F7 | Alerts | Enable alerts, idle 1 min, ntfy URL, Send test. Leave the target on a static screen | Test arrives on the phone. After ~1 min an idle alert arrives once. Moving the mouse re-arms it. Unplugging HDMI gives "Video signal lost" within ~30 s. |
| F8 | Share, view only | Share on. From a tablet, open the https URL and enter the key | Live video. Take control is refused until Allow is ticked. 10 wrong keys lock that tablet out for 5 min. |
| F9 | Share, control | Tick Allow. Tablet: Take control, type and click | Target responds. The console shows the orange "remote technician" banner. Take back control releases it immediately. |
| F10 | Dashboard | Two benches (or two consoles with `--profile`) sharing with the same key | Both appear with thumbnails. Clicking one opens its viewer. |
| H1 | Controller | With the v0.5 board: Find controller, Power, Reset, Force off, USB switch | As in HARDWARE_V1.md. The power LED dot follows the target. |
| H2 | Virtual media | With the v0.6 Pi: Connect, upload an ISO, Mount as CD-ROM, boot the target from it | The installer boots. Eject and Delete work. A wrong fingerprint or token is refused. |

## v0.4 feature tests

| ID | Test | Expected |
|----|------|----------|
| T1 | Ticket tab: enter your RMM key → Save & test, then Look up T-000xx | Shows your tech name and scope, then the customer, device and issue. A wrong key shows "Invalid API key". |
| T2 | Post session with status "in_progress" (customer copy off) | The ticket gets internal notes labelled HHT-Bridge plus the HTML report attachment. Posting again creates no duplicates. |
| T3 | Unplug the network, Post session, reconnect, then Send queued notes | Notes are queued and the report saved locally. They're sent after reconnecting. |
| T4 | Bench time | Grows while you work. A 20-min idle break adds only 5 min. A line appears in timesheet.csv on close. |
| G1 | Guide → "No bootable device" on a PC with the SSD unplugged | Check screen in BIOS takes the "Drive not detected" branch. |
| G2 | Guide → Ask about this screen on a BSOD / boot error | A summary plus suggestions appear. Nothing is typed on the target. |
| B1 | Hardware test: Memtest86+ ISO uploaded, Run test (with controller) | The PC restarts, the boot menu opens, the HHT device is selected, Memtest runs. PASS or FAIL is reported with a screenshot and phone alert. Record the boards tested. |
| B2 | BIOS compare: Save BEFORE on Boot page, change Boot mode, Compare | The change is listed and appears in the report. |
| S1 | Sign-off: Share on, Start sign-off, open /sign on a tablet, enter code, sign | The console shows "Signed by…". The report contains the signature. The code doesn't work a second time. |
| A1 | Beep codes: remove the RAM, Listen, power on | The pattern is detected (e.g. Dell "2 short" = no memory). The WAV is saved. |
| C1 | Cloud: deploy the relay, publish from two benches, open /bridge/ with the owner key | Both benches are visible. A shop view key shows only that shop. |
| L1 | License: no key, try Reports | Says it needs Pro. Paste the Hammer Hill Shop key and everything unlocks. A tampered key is rejected. |
| H3 | Agent hand-off on a Windows target with your agent URL | Elevated PowerShell downloads and runs the installer. UAC is accepted. |
