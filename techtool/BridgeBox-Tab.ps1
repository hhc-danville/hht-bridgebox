    # ===================== TAB: BRIDGE BOX (HDMI capture + CH9329 KVM) =====================
    # Drop-in block for HHC-TechTool (PowerShell 5.1 / WinForms / PS2EXE).
    # Needs, defined earlier in the script:
    #     $tabBridge = New-TabPanel          (or however the current version creates a tab panel)
    #     $AppDir                            (folder that holds HHC-TechTool.exe)
    # The console itself is bundled as:  <AppDir>\bridge\HHTBridgeConsole.exe
    # All variables use the $bb / $script:BB prefix and functions use BB- to avoid name collisions
    # (PowerShell variable names are case-insensitive).
    # NOTE: keep single-quoted strings free of apostrophes.

    $bbDir     = Join-Path $AppDir 'bridge'
    $bbExe     = Join-Path $bbDir 'HHTBridgeConsole.exe'
    $bbLogDir  = Join-Path $env:LOCALAPPDATA 'HHT\BridgeConsole\logs'
    $bbShotDir = Join-Path ([Environment]::GetFolderPath('MyPictures')) 'HHT Bridge'
    $bbBlue    = [System.Drawing.Color]::FromArgb(47,125,246)
    $bbGreen   = [System.Drawing.Color]::FromArgb(52,199,114)
    $bbAmber   = [System.Drawing.Color]::FromArgb(226,163,58)
    $bbGrey    = [System.Drawing.Color]::FromArgb(150,150,150)
    $script:BBFound  = $null
    $script:BBLoaded = $false
    $script:BBPorts  = @()

    $bbHdr = New-Object System.Windows.Forms.Label
    $bbHdr.Text = 'Bridge Box - see and control a customer PC from POST/BIOS onward (HDMI capture + CH9329 cable).'
    $bbHdr.SetBounds(15,14,600,18); $tabBridge.Controls.Add($bbHdr)

    # ---- hardware status ----
    $bbCapDot = New-Object System.Windows.Forms.Label
    $bbCapDot.SetBounds(15,48,12,12); $bbCapDot.BackColor = $bbGrey; $tabBridge.Controls.Add($bbCapDot)
    $bbCapTxt = New-Object System.Windows.Forms.Label
    $bbCapTxt.Text = 'HDMI capture: not checked'; $bbCapTxt.SetBounds(34,45,540,18); $tabBridge.Controls.Add($bbCapTxt)

    $bbCtlDot = New-Object System.Windows.Forms.Label
    $bbCtlDot.SetBounds(15,74,12,12); $bbCtlDot.BackColor = $bbGrey; $tabBridge.Controls.Add($bbCtlDot)
    $bbCtlTxt = New-Object System.Windows.Forms.Label
    $bbCtlTxt.Text = 'Keyboard/mouse cable: not checked'; $bbCtlTxt.SetBounds(34,71,540,18); $tabBridge.Controls.Add($bbCtlTxt)

    $bbTgtDot = New-Object System.Windows.Forms.Label
    $bbTgtDot.SetBounds(15,100,12,12); $bbTgtDot.BackColor = $bbGrey; $tabBridge.Controls.Add($bbTgtDot)
    $bbTgtTxt = New-Object System.Windows.Forms.Label
    $bbTgtTxt.Text = 'Customer PC: not checked (run Test Cable)'; $bbTgtTxt.SetBounds(34,97,540,18); $tabBridge.Controls.Add($bbTgtTxt)

    $bbPortL = New-Object System.Windows.Forms.Label
    $bbPortL.Text = 'Cable port:'; $bbPortL.SetBounds(15,132,75,18); $tabBridge.Controls.Add($bbPortL)
    $bbPort = New-Object System.Windows.Forms.ComboBox
    $bbPort.SetBounds(90,129,250,24); $bbPort.DropDownStyle = 'DropDownList'; $tabBridge.Controls.Add($bbPort)
    $bbBaudL = New-Object System.Windows.Forms.Label
    $bbBaudL.Text = 'Baud:'; $bbBaudL.SetBounds(350,132,40,18); $tabBridge.Controls.Add($bbBaudL)
    $bbBaud = New-Object System.Windows.Forms.ComboBox
    $bbBaud.SetBounds(390,129,80,24); $bbBaud.DropDownStyle = 'DropDownList'; $tabBridge.Controls.Add($bbBaud)
    foreach ($b in @(9600,115200,57600,19200,38400)) { $bbBaud.Items.Add($b) | Out-Null }
    $bbBaud.SelectedIndex = 0

    $bbRefresh = New-Object System.Windows.Forms.Button
    $bbRefresh.Text = 'Refresh Hardware'; $bbRefresh.SetBounds(15,164,140,30); $tabBridge.Controls.Add($bbRefresh)
    $bbTest = New-Object System.Windows.Forms.Button
    $bbTest.Text = 'Test Cable'; $bbTest.SetBounds(165,164,110,30); $tabBridge.Controls.Add($bbTest)
    $bbHelp = New-Object System.Windows.Forms.Button
    $bbHelp.Text = 'How to Connect'; $bbHelp.SetBounds(285,164,120,30); $tabBridge.Controls.Add($bbHelp)

    # ---- session ----
    $bbTicketL = New-Object System.Windows.Forms.Label
    $bbTicketL.Text = 'Ticket #:'; $bbTicketL.SetBounds(15,212,75,18); $tabBridge.Controls.Add($bbTicketL)
    $bbTicket = New-Object System.Windows.Forms.TextBox
    $bbTicket.SetBounds(90,209,120,24); $tabBridge.Controls.Add($bbTicket)
    $bbTechL = New-Object System.Windows.Forms.Label
    $bbTechL.Text = 'Technician:'; $bbTechL.SetBounds(225,212,70,18); $tabBridge.Controls.Add($bbTechL)
    $bbTech = New-Object System.Windows.Forms.TextBox
    $bbTech.SetBounds(298,209,172,24); $bbTech.Text = $env:USERNAME; $tabBridge.Controls.Add($bbTech)

    $bbGo = New-Object System.Windows.Forms.Button
    $bbGo.Text = 'Open Bridge Console'; $bbGo.SetBounds(15,244,220,38)
    $bbGo.BackColor = $bbBlue; $bbGo.ForeColor = [System.Drawing.Color]::White
    $bbGo.Font = New-Object System.Drawing.Font('Segoe UI',9,[System.Drawing.FontStyle]::Bold)
    $tabBridge.Controls.Add($bbGo)
    $bbLogsBtn = New-Object System.Windows.Forms.Button
    $bbLogsBtn.Text = 'Session Logs'; $bbLogsBtn.SetBounds(245,244,110,38); $tabBridge.Controls.Add($bbLogsBtn)
    $bbShotsBtn = New-Object System.Windows.Forms.Button
    $bbShotsBtn.Text = 'Screenshots'; $bbShotsBtn.SetBounds(365,244,105,38); $tabBridge.Controls.Add($bbShotsBtn)

    $bbSessL = New-Object System.Windows.Forms.Label
    $bbSessL.Text = 'Recent sessions (double-click to open):'; $bbSessL.SetBounds(15,294,300,18); $tabBridge.Controls.Add($bbSessL)
    $bbSessions = New-Object System.Windows.Forms.ListBox
    $bbSessions.SetBounds(15,314,545,96); $bbSessions.Anchor = 'Top,Left,Right'; $tabBridge.Controls.Add($bbSessions)

    $bbLog = New-Object System.Windows.Forms.TextBox
    $bbLog.SetBounds(15,420,545,140); $bbLog.Multiline = $true; $bbLog.ReadOnly = $true; $bbLog.ScrollBars = 'Vertical'
    $bbLog.Font = New-Object System.Drawing.Font('Consolas',8.5); $bbLog.Anchor = 'Top,Bottom,Left,Right'
    $tabBridge.Controls.Add($bbLog)
    function BB-Log($m) { $bbLog.AppendText("[$(Get-Date -Format 'HH:mm:ss')] $m`r`n"); [System.Windows.Forms.Application]::DoEvents() }

    # ---- hardware detection ----
    function BB-GetCaptureDevices {
        $names = @()
        try {
            $names = @(Get-CimInstance Win32_PnPEntity -ErrorAction Stop | Where-Object {
                ($_.PNPClass -eq 'Camera' -or $_.PNPClass -eq 'Image') -and
                ($_.Name -match 'USB Video|Capture|HDMI|MS2109|MS2130|Openterface|Cam Link')
            } | Select-Object -ExpandProperty Name -Unique)
        } catch {}
        return $names
    }

    function BB-GetPorts {
        $out = @()
        try {
            Get-CimInstance Win32_PnPEntity -Filter "Name LIKE '%(COM%'" -ErrorAction Stop | ForEach-Object {
                if ($_.Name -match '\((COM\d+)\)') {
                    $out += [pscustomobject]@{ Port = $matches[1]; Name = $_.Name; Kvm = [bool]($_.Name -match 'CH340|CH341|CP210|USB-SERIAL') }
                }
            }
        } catch {}
        if ($out.Count -eq 0) {
            foreach ($p in [System.IO.Ports.SerialPort]::GetPortNames()) { $out += [pscustomobject]@{ Port = $p; Name = $p; Kvm = $false } }
        }
        return @($out | Sort-Object @{ Expression = { -not $_.Kvm } }, Port)
    }

    # Sends CMD_GET_INFO (57 AB 00 01 00 03) and parses the 0x81 reply. Returns $null if no CH9329 answered.
    function BB-Probe([string]$port, [int]$baud) {
        $sp = New-Object System.IO.Ports.SerialPort $port, $baud, ([System.IO.Ports.Parity]::None), 8, ([System.IO.Ports.StopBits]::One)
        $sp.ReadTimeout = 200; $sp.WriteTimeout = 500
        try {
            $sp.Open(); $sp.DiscardInBuffer()
            [byte[]]$cmd = 0x57,0xAB,0x00,0x01,0x00,0x03
            $sp.Write($cmd, 0, $cmd.Length)
            $buf = New-Object System.Collections.Generic.List[byte]
            $sw = [System.Diagnostics.Stopwatch]::StartNew()
            while ($sw.ElapsedMilliseconds -lt 450) {
                $n = $sp.BytesToRead
                if ($n -gt 0) {
                    $tmp = New-Object byte[] $n
                    $r = $sp.Read($tmp, 0, $n)
                    for ($k = 0; $k -lt $r; $k++) { $buf.Add($tmp[$k]) }
                } else { Start-Sleep -Milliseconds 15 }
                for ($i = 0; $i -le $buf.Count - 14; $i++) {
                    if ($buf[$i] -eq 0x57 -and $buf[$i+1] -eq 0xAB -and $buf[$i+3] -eq 0x81 -and $buf[$i+4] -eq 8) {
                        $sum = 0
                        for ($j = $i; $j -lt $i + 13; $j++) { $sum += $buf[$j] }
                        if (($sum -band 0xFF) -eq $buf[$i+13]) {
                            $ver = [int]$buf[$i+5]; $leds = [int]$buf[$i+7]
                            $verTxt = if ($ver -ge 0x30) { 'V{0}.{1}' -f ([math]::Floor(($ver - 0x30) / 10) + 1), (($ver - 0x30) % 10) } else { '0x{0:X2}' -f $ver }
                            return [pscustomobject]@{
                                Port = $port; Baud = $baud; Version = $verTxt
                                TargetUsb = ($buf[$i+6] -eq 1)
                                Num = (($leds -band 1) -ne 0); Caps = (($leds -band 2) -ne 0); Scroll = (($leds -band 4) -ne 0)
                            }
                        }
                    }
                }
            }
            return $null
        } finally {
            try { if ($sp.IsOpen) { $sp.Close() } } catch {}
            $sp.Dispose()
        }
    }

    function BB-LoadSessions {
        $bbSessions.Items.Clear()
        if (Test-Path $bbLogDir) {
            Get-ChildItem $bbLogDir -Filter 'session-*.log' -ErrorAction SilentlyContinue |
                Sort-Object LastWriteTime -Descending | Select-Object -First 20 | ForEach-Object {
                    $bbSessions.Items.Add($_.Name) | Out-Null
                }
        }
        if ($bbSessions.Items.Count -eq 0) { $bbSessions.Items.Add('(no sessions yet)') | Out-Null }
    }

    function BB-Refresh {
        BB-Log 'Checking hardware...'
        $caps = @(BB-GetCaptureDevices)
        if ($caps.Count -gt 0) {
            $bbCapDot.BackColor = $bbGreen; $bbCapTxt.Text = 'HDMI capture: ' + ($caps -join ', ')
            BB-Log ('Capture device(s): ' + ($caps -join ', '))
        } else {
            $bbCapDot.BackColor = $bbAmber; $bbCapTxt.Text = 'HDMI capture: not found - plug in the HDMI-to-USB dongle'
            BB-Log 'No HDMI capture dongle found (looked for USB Video / Capture / HDMI camera devices).'
        }

        $script:BBPorts = @(BB-GetPorts)
        $bbPort.Items.Clear()
        foreach ($p in $script:BBPorts) { $bbPort.Items.Add($p.Name) | Out-Null }
        if ($bbPort.Items.Count -gt 0) {
            $bbPort.SelectedIndex = 0
            $kvm = @($script:BBPorts | Where-Object { $_.Kvm })
            if ($kvm.Count -gt 0) {
                $bbCtlDot.BackColor = $bbAmber; $bbCtlTxt.Text = "Keyboard/mouse cable: $($kvm[0].Port) found - click Test Cable"
            } else {
                $bbCtlDot.BackColor = $bbAmber; $bbCtlTxt.Text = 'Keyboard/mouse cable: no CH340 port seen - check Device Manager > Ports'
            }
            BB-Log ('Serial ports: ' + (($script:BBPorts | ForEach-Object { $_.Name }) -join '; '))
        } else {
            $bbCtlDot.BackColor = $bbGrey; $bbCtlTxt.Text = 'Keyboard/mouse cable: no COM ports - plug in the CH9329 cable (laptop end)'
            BB-Log 'No COM ports found.'
        }
        $script:BBFound = $null
        $bbTgtDot.BackColor = $bbGrey; $bbTgtTxt.Text = 'Customer PC: not checked (run Test Cable)'
        BB-LoadSessions
    }

    function BB-SelectedPort {
        $i = $bbPort.SelectedIndex
        if ($i -lt 0 -or $i -ge $script:BBPorts.Count) { return $null }
        return $script:BBPorts[$i]
    }

    $bbRefresh.Add_Click({ BB-Refresh })

    $bbTest.Add_Click({
        if ($script:BBPorts.Count -eq 0) { BB-Refresh }
        if ($script:BBPorts.Count -eq 0) { return }
        $sel = BB-SelectedPort
        $ports = @()
        if ($sel) { $ports += $sel.Port }
        foreach ($p in $script:BBPorts) { if ($ports -notcontains $p.Port) { $ports += $p.Port } }
        $bauds = @([int]$bbBaud.SelectedItem)
        foreach ($b in @(9600,115200,57600,19200,38400)) { if ($bauds -notcontains $b) { $bauds += $b } }

        $bbTest.Enabled = $false
        $hit = $null
        try {
            :portLoop foreach ($p in $ports) {
                foreach ($b in $bauds) {
                    try {
                        $r = BB-Probe $p $b
                        if ($r) { $hit = $r; break portLoop }
                        BB-Log "  $p @ $b : no reply"
                    } catch {
                        BB-Log "  $p : cannot open ($($_.Exception.Message)) - close the Bridge Console if it is running"
                        break
                    }
                }
            }
        } finally { $bbTest.Enabled = $true }

        if (-not $hit) {
            $bbCtlDot.BackColor = $bbAmber; $bbCtlTxt.Text = 'Keyboard/mouse cable: no CH9329 answered on any port'
            BB-Log 'No CH9329 found. Check the cable listing says CH9329 and that the CH340 driver is installed.'
            return
        }
        $script:BBFound = $hit
        for ($i = 0; $i -lt $script:BBPorts.Count; $i++) { if ($script:BBPorts[$i].Port -eq $hit.Port) { $bbPort.SelectedIndex = $i } }
        $bbBaud.SelectedItem = $hit.Baud
        $bbCtlDot.BackColor = $bbGreen
        $bbCtlTxt.Text = "Keyboard/mouse cable: CH9329 $($hit.Version) on $($hit.Port) @ $($hit.Baud)"
        $leds = @(); if ($hit.Num) { $leds += 'NUM' }; if ($hit.Caps) { $leds += 'CAPS' }; if ($hit.Scroll) { $leds += 'SCROLL' }
        if ($hit.TargetUsb) {
            $bbTgtDot.BackColor = $bbGreen
            $bbTgtTxt.Text = 'Customer PC: sees the keyboard/mouse' + $(if ($leds.Count) { ' (lights: ' + ($leds -join ' ') + ')' } else { '' })
        } else {
            $bbTgtDot.BackColor = $bbAmber
            $bbTgtTxt.Text = 'Customer PC: has NOT detected the keyboard/mouse - check the target-end USB plug, or the PC is off'
        }
        BB-Log "CH9329 $($hit.Version) on $($hit.Port) @ $($hit.Baud). Target USB: $(if ($hit.TargetUsb) {'enumerated'} else {'not enumerated'})."
    })

    $bbGo.Add_Click({
        if (-not (Test-Path $bbExe)) {
            BB-Log "Console not found at: $bbExe"
            [System.Windows.Forms.MessageBox]::Show(
                "Bridge Console not found in:`n$bbDir`n`nOn the build PC run  .\build.ps1 -Publish  in the HHT.BridgeBox folder, then copy the publish\bridge folder next to HHC-TechTool.exe.",
                'Console not bundled','OK','Warning') | Out-Null
            return
        }
        $a = @()
        $t = $bbTicket.Text.Trim()
        if ($t) { $a += @('--ticket', $t) }
        $tech = $bbTech.Text.Trim()
        if ($tech) { $a += @('--tech', ('"' + $tech.Replace('"','') + '"')) }
        if ($script:BBFound) {
            $a += @('--port', $script:BBFound.Port, '--baud', [string]$script:BBFound.Baud)
        } else {
            $sel = BB-SelectedPort
            if ($sel -and $sel.Kvm) { $a += @('--port', $sel.Port, '--baud', [string]$bbBaud.SelectedItem) }
        }
        BB-Log ('Launching Bridge Console ' + ($a -join ' '))
        try {
            if ($a.Count) { Start-Process -FilePath $bbExe -ArgumentList $a -WorkingDirectory $bbDir }
            else { Start-Process -FilePath $bbExe -WorkingDirectory $bbDir }
        } catch {
            BB-Log "Launch failed: $($_.Exception.Message)"
            [System.Windows.Forms.MessageBox]::Show("Could not start the Bridge Console:`n$($_.Exception.Message)",'Error','OK','Error') | Out-Null
        }
    })

    $bbLogsBtn.Add_Click({
        if (-not (Test-Path $bbLogDir)) { New-Item -ItemType Directory -Path $bbLogDir -Force | Out-Null }
        Start-Process explorer.exe -ArgumentList ('"' + $bbLogDir + '"')
    })
    $bbShotsBtn.Add_Click({
        if (-not (Test-Path $bbShotDir)) { New-Item -ItemType Directory -Path $bbShotDir -Force | Out-Null }
        Start-Process explorer.exe -ArgumentList ('"' + $bbShotDir + '"')
    })
    $bbSessions.Add_DoubleClick({
        $f = [string]$bbSessions.SelectedItem
        if (-not $f -or $f.StartsWith('(')) { return }
        $path = Join-Path $bbLogDir $f
        if (Test-Path $path) { Start-Process notepad.exe -ArgumentList ('"' + $path + '"') }
    })

    $bbHelp.Add_Click({
        $msg = "WHAT YOU NEED`n" +
               " 1. HDMI-to-USB capture dongle (UVC, e.g. MS2109)`n" +
               " 2. CH340-to-CH9329 KVM cable (listing must say CH9329)`n" +
               " 3. HDMI cable (DisplayPort/VGA PCs need an adapter)`n`n" +
               "HOW TO CONNECT`n" +
               " - Customer PC HDMI out  ->  HDMI cable  ->  capture dongle  ->  this laptop`n" +
               " - Cable end that shows as CH340 COM port  ->  this laptop`n" +
               " - Other cable end  ->  any USB port on the customer PC`n`n" +
               "THEN`n" +
               " Refresh Hardware, Test Cable, Open Bridge Console.`n" +
               " In the console: Start video, click the picture to take control, tap Right Ctrl to release.`n`n" +
               "Never use a plain USB-A to USB-A cable - it joins two computers power rails."
        [System.Windows.Forms.MessageBox]::Show($msg,'Bridge Box - How to Connect','OK','Information') | Out-Null
    })

    # Detect hardware the first time the tab is shown (keeps TechTool startup fast).
    $tabBridge.Add_VisibleChanged({
        if ($tabBridge.Visible -and -not $script:BBLoaded) { $script:BBLoaded = $true; BB-Refresh }
    })
