# Adding the Bridge Box tab to HHC-TechTool

`BridgeBox-Tab.ps1` is a drop-in block in TechTool's own style: PowerShell 5.1, WinForms, `SetBounds` layout, PS2EXE-safe (ASCII only, no apostrophes in single-quoted strings, every name prefixed `$bb` / `BB-`). It follows the same pattern as the Firewalls tab: TechTool hosts the UI and status checks, and launches a bundled exe for the heavy lifting.

## What the tab does

- **Hardware check:** finds the HDMI capture dongle and the CH340 COM port (WMI). This runs the first time the tab opens, so TechTool still starts fast.
- **Test Cable:** talks directly to the CH9329 (sends GET_INFO). It reports the chip version, whether the **customer PC** has detected the keyboard/mouse, and the customer's Num/Caps/Scroll lights. It tries every port and baud rate.
- **Ticket # / Technician:** passed to the console, so the session log is named `session-...-ticket-10423.log` and screenshots go to `Pictures\HHT Bridge\Ticket-10423\`.
- **Open Bridge Console:** launches `bridge\HHTBridgeConsole.exe --ticket .. --tech .. --port COMx --baud N`, already connected.
- **Recent sessions:** double-click to open a log. Buttons open the Session Logs and Screenshots folders.
- **How to Connect:** a cabling cheat-sheet for techs.

## Steps (on the PC with the current v77+ script)

1. **Create the panel** next to the other `New-TabPanel` lines:
   ```powershell
   $tabBridge = New-TabPanel
   ```
2. **Paste** the whole of `BridgeBox-Tab.ps1` after another tab's block (for example after the Firewalls tab). It only needs `$tabBridge` and `$AppDir`.
3. **Add it to the navigation.** In v41 that was an `$allTabs` entry:
   ```powershell
   @{Name='Bridge Box'; Panel=$tabBridge; Color=<next index>; Offline=$false}
   ```
   plus a colour in `$script:TabColors` (suggest HHT blue `FromArgb(47,125,246)`). In the accordion layout, put it under the hardware/diagnostics category. Set `Offline=$false` because the console needs a normal Windows desktop, not WinPE.
4. **Bundle the console.** On a PC with the .NET 10 SDK: `.\build.ps1 -Publish` in the HHT.BridgeBox folder. Copy `publish\bridge\` next to `HHC-TechTool.exe` and include it in the package or update flow, the same way the `sonicwall` folder is.
5. **Optional: ticket prefill.** Wherever TechTool loads the current RMM ticket, add `$bbTicket.Text = <ticket id>`.
6. Bump the version, rebuild with `HHC-TechTool_Setup.cmd`, test that it opens, then publish (latest.json + hash).

## Verified so far

- Parses with the PowerShell parser on its own and when spliced into the v41 script (PowerShell 7.4 parser; also written for 5.1 syntax).
- `BB-Probe` tested over a real serial link against a simulated CH9329. It correctly parsed version V1.1, target enumerated, Num + Caps on, even with junk bytes before the reply.
- Not yet run inside the compiled TechTool exe or against the real cable.
