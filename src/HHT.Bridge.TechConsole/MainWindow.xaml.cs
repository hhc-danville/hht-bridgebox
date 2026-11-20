using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using HHT.Bridge.Core.Ch9329;
using HHT.Bridge.Core.Hid;
using HHT.Bridge.Core.Input;
using HHT.Bridge.Core.Logging;
using HHT.Bridge.Core.Settings;
using HHT.Bridge.TechConsole.Capture;
using HHT.Bridge.TechConsole.Input;
using HHT.Bridge.TechConsole.Serial;
using HidMouseButtons = HHT.Bridge.Core.Ch9329.MouseButtons;

namespace HHT.Bridge.TechConsole;

public partial class MainWindow : Window
{
    /// <summary>Per-profile data folder (--profile lets several benches run on one PC).</summary>
    private static readonly string AppDataDir = App.Launch.Profile == null
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HHT", "BridgeConsole")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HHT", "BridgeConsole", "profiles", App.Launch.Profile);
    private static readonly string SettingsPath = Path.Combine(AppDataDir, "settings.json");
    private static readonly string LogDir = Path.Combine(AppDataDir, "logs");

    private static readonly string[] VideoModes =
        { "1920x1080@30", "1920x1080@60", "1280x720@60", "1280x720@30", "1024x768@30", "800x600@30", "640x480@30" };

    private readonly ConsoleSettings _settings;
    private readonly LaunchOptions _launch = App.Launch;
    private readonly SessionLog _log;
    private readonly CaptureService _capture = new();
    private readonly HostKeyRouter _router = new();
    private readonly LowLevelKeyboardHook _hook;
    private readonly DispatcherTimer _statusTimer;

    private Ch9329Controller? _controller;
    private int _infoMisses;
    private bool _pollInFlight;
    private WriteableBitmap? _bitmap;
    private int _frameW, _frameH;
    private bool _captured;
    private bool _fullscreen;
    private bool _typing;
    private bool _warnedNoController;
    private HidMouseButtons _buttons;
    private int _lastAbsX = 2048, _lastAbsY = 2048;
    private Point? _lastRelPoint;
    private WindowState _restoreState;
    private WindowStyle _restoreStyle;

    public MainWindow()
    {
        InitializeComponent();
        Directory.CreateDirectory(AppDataDir);
        _settings = ConsoleSettings.Load(SettingsPath);
        _log = new SessionLog(LogDir, _launch.Technician, _launch.Ticket);
        if (_launch.Ticket != null) Title += $" — Ticket {_launch.Ticket}";
        if (_launch.Profile != null) Title += $" — {_launch.Profile}";
        _log.EntryWritten += line => Dispatcher.BeginInvoke(() => LastLogText.Text = line);
        _hook = new LowLevelKeyboardHook(OnHookKey);

        _capture.FrameReady += OnFrame;
        _capture.Problem += msg => Dispatcher.BeginInvoke(() => _log.Warn(msg));

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        _statusTimer.Tick += async (_, _) => await PollStatusAsync();
        _statusTimer.Start();

        BuildSpecialKeyButtons();
        foreach (var m in VideoModes) VideoModeCombo.Items.Add(m);
        VideoModeCombo.SelectedItem = VideoModes.Contains(_settings.VideoMode) ? _settings.VideoMode : VideoModes[0];
        foreach (var b in Ch9329Probe.CommonBaudRates) BaudCombo.Items.Add(b);
        BaudCombo.SelectedItem = Ch9329Probe.CommonBaudRates.Contains(_settings.BaudRate) ? _settings.BaudRate : 9600;
        AbsoluteRadio.IsChecked = _settings.MouseMode == MouseMode.Absolute;
        RelativeRadio.IsChecked = _settings.MouseMode == MouseMode.Relative;
        HideCursorCheck.IsChecked = _settings.HideLocalCursorWhenCaptured;

        InitFeatures();
        InitAutomation();
        InitHardware();
        InitV04();

        Loaded += async (_, _) => await OnLoadedAsync();
        Deactivated += (_, _) => EndCapture("window lost focus");
        Closing += (_, _) => Shutdown();
    }

    // =====================================================================
    // Startup / shutdown
    // =====================================================================

    private async Task OnLoadedAsync()
    {
        RefreshDeviceLists();
        InitShare();
        await InitV04LoadedAsync();

        // Plug-and-go: reopen the last-used devices if they are present.
        if (_settings.VideoDeviceName != null && VideoDeviceCombo.SelectedItem as string == _settings.VideoDeviceName)
            StartVideo();

        // A port passed on the command line (from the TechTool tab) wins over the saved one.
        if (_launch.Port != null)
        {
            var match = PortCombo.Items.OfType<SerialPortEntry>().FirstOrDefault(e => e.Port == _launch.Port);
            if (match == null) { _log.Warn($"Requested port {_launch.Port} is not present."); return; }
            PortCombo.SelectedItem = match;
            int baud = _launch.Baud ?? (BaudCombo.SelectedItem is int sb ? sb : 9600);
            if (!BaudCombo.Items.Contains(baud)) BaudCombo.Items.Add(baud);
            BaudCombo.SelectedItem = baud;
            await ConnectControlAsync(match.Port, baud, quiet: false);
        }
        else if (_settings.SerialPort != null && PortCombo.SelectedItem is SerialPortEntry p && p.Port == _settings.SerialPort)
            await ConnectControlAsync(p.Port, (int)BaudCombo.SelectedItem!, quiet: true);
    }

    private void Shutdown()
    {
        EndCapture("closing");
        _statusTimer.Stop();
        ShutdownFeatures();
        ShutdownAutomation();
        ShutdownShare();
        ShutdownHardware();
        ShutdownV04();
        SaveSettings();
        DisconnectControl();
        _capture.Dispose();
        _hook.Dispose();
        _log.Dispose();
    }

    private void SaveSettings()
    {
        _settings.VideoDeviceName = VideoDeviceCombo.SelectedItem as string;
        _settings.VideoMode = VideoModeCombo.SelectedItem as string ?? VideoModes[0];
        _settings.SerialPort = (PortCombo.SelectedItem as SerialPortEntry)?.Port;
        _settings.BaudRate = BaudCombo.SelectedItem is int b ? b : 9600;
        _settings.MouseMode = RelativeRadio.IsChecked == true ? MouseMode.Relative : MouseMode.Absolute;
        _settings.HideLocalCursorWhenCaptured = HideCursorCheck.IsChecked == true;
        try { _settings.Save(SettingsPath); } catch (Exception ex) { _log.Warn($"Could not save settings: {ex.Message}"); }
    }

    private void RefreshDeviceLists()
    {
        var videoNames = DirectShowDevices.GetVideoInputNames();
        VideoDeviceCombo.Items.Clear();
        foreach (var n in videoNames) VideoDeviceCombo.Items.Add(n);
        VideoDeviceCombo.SelectedItem =
            videoNames.FirstOrDefault(n => n == _settings.VideoDeviceName)
            ?? videoNames.FirstOrDefault(DirectShowDevices.LooksLikeHdmiCapture)
            ?? videoNames.FirstOrDefault();

        var ports = SerialPortCatalog.List();
        PortCombo.Items.Clear();
        foreach (var p in ports) PortCombo.Items.Add(p);
        PortCombo.SelectedItem =
            ports.FirstOrDefault(p => p.Port == _settings.SerialPort)
            ?? ports.FirstOrDefault(p => p.LooksLikeKvmCable)
            ?? ports.FirstOrDefault();

        _log.Info($"Found {videoNames.Count} video device(s), {ports.Count} serial port(s).");
    }

    private void RefreshDevices_Click(object sender, RoutedEventArgs e) => RefreshDeviceLists();

    // =====================================================================
    // Video
    // =====================================================================

    private void VideoStart_Click(object sender, RoutedEventArgs e)
    {
        if (_capture.IsRunning) StopVideo();
        else StartVideo();
    }

    private void StartVideo()
    {
        int index = VideoDeviceCombo.SelectedIndex;
        if (index < 0) { _log.Warn("No video device selected."); return; }
        var mode = VideoModeCombo.SelectedItem as string ?? VideoModes[0];
        ConsoleSettings.TryParseVideoMode(mode, out var w, out var h, out var fps);

        if (!_capture.Start(index, w, h, fps, out var error))
        {
            _log.Error($"Video start failed on \"{VideoDeviceCombo.SelectedItem}\": {error}");
            MessageBox.Show(this, error, "Video", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _log.Info($"Video started: \"{VideoDeviceCombo.SelectedItem}\" requested {mode}, device reports {_capture.Width}x{_capture.Height}@{_capture.RequestedFps:0}.");
        if (_capture.Width != w || _capture.Height != h)
            _log.Warn($"Capture device did not accept {w}x{h}; using {_capture.Width}x{_capture.Height}.");
        VideoStartButton.Content = "Stop video";
        SaveSettings();
        UpdateIndicators();
    }

    private void StopVideo()
    {
        StopRecordingIfActive();
        _capture.Stop();
        _log.Info("Video stopped.");
        VideoStartButton.Content = "Start video";
        NoVideoPanel.Visibility = Visibility.Visible;
        UpdateIndicators();
    }

    /// <summary>Capture thread → UI thread. Only one frame is ever in flight.</summary>
    private void OnFrame(VideoFrame f)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            try
            {
                if (_bitmap == null || _bitmap.PixelWidth != f.Width || _bitmap.PixelHeight != f.Height)
                {
                    _bitmap = new WriteableBitmap(f.Width, f.Height, 96, 96, PixelFormats.Bgr24, null);
                    VideoImage.Source = _bitmap;
                    _frameW = f.Width;
                    _frameH = f.Height;
                }
                _bitmap.WritePixels(new Int32Rect(0, 0, f.Width, f.Height), f.Buffer, f.Stride, 0);
                if (NoVideoPanel.Visibility == Visibility.Visible) NoVideoPanel.Visibility = Visibility.Collapsed;
            }
            finally
            {
                _capture.FrameConsumed();
            }
        });
    }

    // =====================================================================
    // CH9329 control link
    // =====================================================================

    private async void ControlConnect_Click(object sender, RoutedEventArgs e)
    {
        if (_controller != null) { DisconnectControl(); return; }
        if (PortCombo.SelectedItem is not SerialPortEntry p) { _log.Warn("No serial port selected."); return; }
        await ConnectControlAsync(p.Port, BaudCombo.SelectedItem is int b ? b : 9600, quiet: false);
    }

    private async Task<bool> ConnectControlAsync(string port, int baud, bool quiet)
    {
        DisconnectControl();
        Ch9329Controller? c = null;
        try
        {
            c = new Ch9329Controller(new SerialTransport(port, baud));
            c.Faulted += ex => Dispatcher.BeginInvoke(() =>
            {
                _log.Error($"Control link failed on {port}: {ex.Message} (cable unplugged?)");
                DisconnectControl();
            });
            c.Start();
            var info = await c.GetInfoAsync(TimeSpan.FromMilliseconds(700));
            if (info == null)
            {
                c.Dispose();
                var msg = $"{port} opened at {baud} baud but no CH9329 answered. Try Auto-detect, or a different baud rate.";
                _log.Warn(msg);
                if (!quiet) MessageBox.Show(this, msg, "Keyboard & mouse", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            _controller = c;
            _infoMisses = 0;
            _log.Audit($"Control connected: CH9329 {info.Version} on {port} @ {baud}. Target USB {(info.TargetUsbConnected ? "enumerated" : "NOT enumerated")}.");
            ApplyInfo(info);
            ControlConnectButton.Content = "Disconnect";
            SaveSettings();
            return true;
        }
        catch (Exception ex)
        {
            c?.Dispose();
            var msg = $"Could not open {port}: {ex.Message}";
            _log.Error(msg);
            if (!quiet) MessageBox.Show(this, msg, "Keyboard & mouse", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        finally
        {
            UpdateIndicators();
        }
    }

    private void DisconnectControl()
    {
        if (_controller == null) return;
        EndCapture("control disconnected");
        try
        {
            _controller.ReleaseEverything(RelativeRadio.IsChecked != true, _lastAbsX, _lastAbsY);
            Thread.Sleep(60); // let the release frames go out
        }
        catch { /* link already gone */ }
        _controller.Dispose();
        _controller = null;
        _log.Audit("Control disconnected.");
        ControlConnectButton.Content = "Connect";
        ChipInfoText.Text = "Not connected.";
        ApplyInfo(null);
        UpdateIndicators();
    }

    private async void AutoDetect_Click(object sender, RoutedEventArgs e)
    {
        DisconnectControl();
        AutoDetectButton.IsEnabled = false;
        try
        {
            var ports = SerialPortCatalog.List();
            if (ports.Count == 0) { _log.Warn("No serial ports found. Is the CH9329 cable plugged into this laptop?"); return; }
            var ordered = ports.OrderByDescending(p => p.LooksLikeKvmCable).Select(p => p.Port).ToList();
            _log.Info($"Auto-detect: probing {string.Join(", ", ordered)} ...");
            var hit = await Task.Run(() => Ch9329Probe.FindAsync(ordered, (port, baud) => new SerialTransport(port, baud),
                log: m => Dispatcher.BeginInvoke(() => _log.Info(m))));
            if (hit == null)
            {
                _log.Warn("Auto-detect: no CH9329 found on any port.");
                MessageBox.Show(this, "No CH9329 answered on any COM port or common baud rate.\n\n" +
                                      "Check the cable is plugged into this laptop and that the CH340 driver is installed (Device Manager → Ports).",
                    "Auto-detect", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            PortCombo.SelectedItem = PortCombo.Items.OfType<SerialPortEntry>().FirstOrDefault(p => p.Port == hit.Port);
            BaudCombo.SelectedItem = hit.Baud;
            await ConnectControlAsync(hit.Port, hit.Baud, quiet: false);
        }
        finally
        {
            AutoDetectButton.IsEnabled = true;
        }
    }

    private async Task PollStatusAsync()
    {
        FpsText.Text = _capture.IsRunning
            ? $"{_frameW}x{_frameH} · {_capture.MeasuredFps:0.0} fps" + (_controller != null ? $" · {_controller.FramesSent} HID frames" : "")
              + (_capture.IsRecording ? " · recording" : "")
            : "";
        UpdateTimeText();
        if (_box != null) { await PollControllerAsync(); await PollSensorsAsync(); }
        if (_controller == null || _pollInFlight || _typing) return;
        _pollInFlight = true;
        try
        {
            var info = await _controller.GetInfoAsync(TimeSpan.FromMilliseconds(600));
            if (_controller == null) return;
            if (info == null)
            {
                if (++_infoMisses == 3) _log.Warn("CH9329 is not answering status requests.");
            }
            else
            {
                if (_infoMisses >= 3) _log.Info("CH9329 answering again.");
                _infoMisses = 0;
                ApplyInfo(info);
            }
            UpdateIndicators();
        }
        finally { _pollInFlight = false; }
    }

    private bool _lastTargetUsb;

    private void ApplyInfo(Ch9329Info? info)
    {
        var on = (Brush)FindResource("HhtGreen");
        var off = new SolidColorBrush(Color.FromRgb(0x55, 0x5A, 0x63));
        NumLed.Foreground = info?.NumLock == true ? on : off;
        CapsLed.Foreground = info?.CapsLock == true ? on : off;
        ScrollLed.Foreground = info?.ScrollLock == true ? on : off;
        _targetUsbCached = info?.TargetUsbConnected == true;
        if (info == null) return;
        ChipInfoText.Text = $"CH9329 {info.Version} on {_controller?.PortName}. " +
                            (info.TargetUsbConnected
                                ? "Target sees the keyboard/mouse."
                                : "Target has NOT enumerated the keyboard/mouse — check the USB cable to the target, or the target is powered off.");
        if (info.TargetUsbConnected != _lastTargetUsb)
        {
            _log.Info(info.TargetUsbConnected ? "Target enumerated the HID device." : "Target no longer sees the HID device.");
            _lastTargetUsb = info.TargetUsbConnected;
        }
    }

    private void UpdateIndicators()
    {
        var green = (Brush)FindResource("HhtGreen");
        var amber = (Brush)FindResource("HhtAmber");
        var grey = new SolidColorBrush(Color.FromRgb(0x55, 0x5A, 0x63));

        VideoDot.Fill = _capture.IsRunning ? green : grey;
        VideoPillText.Text = _capture.IsRunning ? $"Video: {_capture.Width}x{_capture.Height}" : "Video: off";

        if (_controller == null)
        {
            ControlDot.Fill = grey;
            ControlPillText.Text = "Control: not connected";
        }
        else if (_infoMisses >= 3 || _controller.LastInfo?.TargetUsbConnected != true)
        {
            ControlDot.Fill = amber;
            ControlPillText.Text = _infoMisses >= 3 ? "Control: not responding" : "Control: target not enumerated";
        }
        else
        {
            ControlDot.Fill = green;
            ControlPillText.Text = $"Control: {_controller.PortName}";
        }
    }

    private bool EnsureController()
    {
        if (_controller != null) return true;
        if (!_warnedNoController)
        {
            _log.Warn("Keyboard/mouse control is not connected — connect the CH9329 cable first.");
            _warnedNoController = true;
        }
        return false;
    }

    // =====================================================================
    // Input capture (explicit, technician-initiated)
    // =====================================================================

    private void BeginCapture()
    {
        if (_captured) return;
        if (!EnsureController()) return;
        try { _hook.Install(); }
        catch (Exception ex) { _log.Error($"Keyboard hook failed: {ex.Message}"); return; }
        _captured = true;
        _lastRelPoint = null;
        VideoHost.Focus();
        CaptureBanner.Visibility = Visibility.Visible;
        if (HideCursorCheck.IsChecked == true) VideoHost.Cursor = Cursors.None;
        _log.Info("Input capture started.");
    }

    private void EndCapture(string reason)
    {
        if (!_captured) return;
        _captured = false;
        _hook.Uninstall();
        var release = _router.ReleaseAll();
        if (_controller != null)
        {
            if (release != null) _controller.SendKeyboard(release);
            if (_buttons != HidMouseButtons.None) SendButtons(HidMouseButtons.None, 0);
        }
        _buttons = HidMouseButtons.None;
        if (VideoHost.IsMouseCaptured) VideoHost.ReleaseMouseCapture();
        VideoHost.Cursor = null;
        CaptureBanner.Visibility = Visibility.Collapsed;
        _log.Info($"Input capture released ({reason}).");
    }

    /// <summary>Runs on the UI thread from the low-level hook. Must return quickly.</summary>
    private bool OnHookKey(int vk, int scan, bool extended, bool down)
    {
        if (!_captured || !IsActive) return false;
        if (down) _time.Mark(DateTime.Now);
        var outcome = _router.OnKey(vk, extended, down);
        if (outcome.Report != null) _controller?.SendKeyboard(outcome.Report);
        if (outcome.Command != HostCommand.None)
        {
            var cmd = outcome.Command;
            Dispatcher.BeginInvoke(() => HandleHostCommand(cmd));
        }
        return true; // while captured, nothing reaches the local PC
    }

    private void HandleHostCommand(HostCommand cmd)
    {
        switch (cmd)
        {
            case HostCommand.ReleaseCapture: EndCapture("Right Ctrl"); break;
            case HostCommand.ToggleFullscreen: ToggleFullscreen(); break;
            case HostCommand.Screenshot: TakeScreenshot(); break;
            case HostCommand.SendCtrlAltDel: SendSpecial(SpecialKeys.CtrlAltDel); break;
        }
    }

    // ---------------------------------------------------------------- mouse

    private bool AbsoluteMode => RelativeRadio.IsChecked != true;

    private static HidMouseButtons ButtonsFrom(MouseEventArgs e)
    {
        var b = HidMouseButtons.None;
        if (e.LeftButton == MouseButtonState.Pressed) b |= HidMouseButtons.Left;
        if (e.RightButton == MouseButtonState.Pressed) b |= HidMouseButtons.Right;
        if (e.MiddleButton == MouseButtonState.Pressed) b |= HidMouseButtons.Middle;
        return b;
    }

    private (int W, int H) SourceSize() =>
        _frameW > 0 ? (_frameW, _frameH) : ((int)Math.Max(1, VideoHost.ActualWidth), (int)Math.Max(1, VideoHost.ActualHeight));

    private bool MapAbsolute(Point p)
    {
        var (sw, sh) = SourceSize();
        if (!VideoCoordinateMapper.TryMapAbsolute(p.X, p.Y, VideoHost.ActualWidth, VideoHost.ActualHeight, sw, sh, out var x, out var y))
            return false;
        _lastAbsX = x;
        _lastAbsY = y;
        return true;
    }

    private void SendButtons(HidMouseButtons buttons, sbyte wheel)
    {
        if (_controller == null) return;
        if (AbsoluteMode) _controller.ButtonsAbsolute(buttons, _lastAbsX, _lastAbsY, wheel);
        else _controller.ButtonsRelative(buttons, wheel);
    }

    private void VideoHost_MouseDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (!_captured)
        {
            // The first click only takes control; it is not sent to the target.
            if (e.ChangedButton == MouseButton.Left) BeginCapture();
            return;
        }
        var p = e.GetPosition(VideoHost);
        _time.Mark(DateTime.Now);
        if (AbsoluteMode && !MapAbsolute(p)) return; // click in the letterbox bars
        _buttons = ButtonsFrom(e);
        SendButtons(_buttons, 0);
        VideoHost.CaptureMouse(); // keep receiving the release even if the pointer leaves
    }

    private void VideoHost_MouseUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (!_captured) return;
        if (AbsoluteMode) MapAbsolute(e.GetPosition(VideoHost));
        _buttons = ButtonsFrom(e);
        SendButtons(_buttons, 0);
        if (_buttons == HidMouseButtons.None && VideoHost.IsMouseCaptured) VideoHost.ReleaseMouseCapture();
    }

    private void VideoHost_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_captured || _controller == null) return;
        var p = e.GetPosition(VideoHost);
        _buttons = ButtonsFrom(e);
        if (AbsoluteMode)
        {
            if (MapAbsolute(p)) _controller.MoveAbsolute(_buttons, _lastAbsX, _lastAbsY);
            return;
        }
        if (_lastRelPoint is { } last)
        {
            var (sw, sh) = SourceSize();
            double scale = VideoCoordinateMapper.ElementToSourceScale(VideoHost.ActualWidth, VideoHost.ActualHeight, sw, sh);
            int dx = (int)Math.Round((p.X - last.X) * scale);
            int dy = (int)Math.Round((p.Y - last.Y) * scale);
            if (dx != 0 || dy != 0) _controller.MoveRelative(_buttons, dx, dy);
        }
        _lastRelPoint = p;
    }

    private void VideoHost_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        if (!_captured) return;
        int notches = Math.Clamp(e.Delta / 120, -127, 127);
        if (notches == 0) notches = Math.Sign(e.Delta);
        SendButtons(_buttons, (sbyte)notches);
    }

    private void VideoHost_MouseLeave(object sender, MouseEventArgs e) => _lastRelPoint = null;

    private void MouseMode_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        _lastRelPoint = null;
        _log.Info($"Mouse mode: {(AbsoluteMode ? "absolute" : "relative")}.");
        SaveSettings();
    }

    private void HideCursor_Click(object sender, RoutedEventArgs e)
    {
        if (_captured) VideoHost.Cursor = HideCursorCheck.IsChecked == true ? Cursors.None : null;
        SaveSettings();
    }

    // =====================================================================
    // Special keys & typing
    // =====================================================================

    private void BuildSpecialKeyButtons()
    {
        foreach (var key in SpecialKeys.All)
        {
            var b = new Button
            {
                Content = key.Label,
                Tag = key,
                ToolTip = string.IsNullOrEmpty(key.Hint) ? key.Label : key.Hint,
                Margin = new Thickness(0, 0, 6, 6),
                Padding = new Thickness(8, 4, 8, 4),
                MinWidth = 52,
                FontSize = 12,
                Focusable = false,
            };
            b.Click += (_, _) => SendSpecial(key);
            SpecialKeysPanel.Children.Add(b);
        }
    }

    private void SendSpecial(SpecialKey key)
    {
        if (!EnsureController()) return;
        _controller!.SendKeyboardSequence(key.ToReports());
        _log.Audit($"Sent {key.Label} to target.");
    }

    private async void TypeText_Click(object sender, RoutedEventArgs e) => await TypeAsync(TypeTextBox.Text);

    private async void TypeClipboard_Click(object sender, RoutedEventArgs e)
    {
        string text;
        try { text = Clipboard.ContainsText() ? Clipboard.GetText() : ""; }
        catch (Exception) { text = ""; }
        if (text.Length == 0) { _log.Warn("Clipboard has no text."); return; }
        await TypeAsync(text);
    }

    private async Task TypeAsync(string text)
    {
        if (string.IsNullOrEmpty(text) || _typing || !EnsureController()) return;
        var result = TextTyper.ToReports(text);
        if (result.TypedCharacters > 500 &&
            MessageBox.Show(this, $"Type {result.TypedCharacters} characters into the target?", "Type text",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        _typing = true;
        TypeTextButton.IsEnabled = TypeClipboardButton.IsEnabled = false;
        try
        {
            int delay = Math.Clamp(_settings.TypingDelayMs, 0, 200);
            foreach (var r in result.Reports)
            {
                var c = _controller;
                if (c == null) break; // link dropped mid-typing
                c.SendKeyboard(r);
                if (delay > 0) await Task.Delay(delay);
            }
            // Content intentionally not logged (may be a password).
            _log.Audit($"Typed {result.TypedCharacters} characters into target" +
                       (result.SkippedCharacters > 0 ? $" ({result.SkippedCharacters} unsupported characters skipped)." : "."));
        }
        finally
        {
            _typing = false;
            TypeTextButton.IsEnabled = TypeClipboardButton.IsEnabled = true;
        }
    }

    // =====================================================================
    // Tools
    // =====================================================================

    private string ScreenshotDir
    {
        get
        {
            var root = _settings.ScreenshotFolder
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "HHT Bridge");
            return _launch.Ticket == null ? root : Path.Combine(root, $"Ticket-{_launch.Ticket}");
        }
    }

    private void Screenshot_Click(object sender, RoutedEventArgs e) => TakeScreenshot();

    private void TakeScreenshot(string? caption = null)
    {
        if (_bitmap == null) { _log.Warn("No video frame to capture."); return; }
        try
        {
            Directory.CreateDirectory(ScreenshotDir);
            var path = Path.Combine(ScreenshotDir, $"target-{DateTime.Now:yyyyMMdd-HHmmss-fff}.png");
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(_bitmap.Clone()));
            using (var fs = File.Create(path)) encoder.Save(fs);
            _screenshots.Add(new HHT.Bridge.Core.Reports.ReportScreenshot(DateTime.Now, path, caption ??
                (_findings.Count > 0 && _lastOcrAt > DateTime.Now.AddSeconds(-10) ? "Screen with findings" : $"Screenshot {_screenshots.Count + 1}")));
            _log.Audit($"Screenshot saved: {path}");
        }
        catch (Exception ex) { _log.Error($"Screenshot failed: {ex.Message}"); }
    }

    private void Fullscreen_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();

    private void ToggleFullscreen()
    {
        _fullscreen = !_fullscreen;
        if (_fullscreen)
        {
            _restoreState = WindowState;
            _restoreStyle = WindowStyle;
            Sidebar.Visibility = HeaderBar.Visibility = StatusBar.Visibility = Visibility.Collapsed;
            SidebarColumn.Width = new GridLength(0);
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Normal; // toggle so Maximized covers the taskbar
            WindowState = WindowState.Maximized;
        }
        else
        {
            Sidebar.Visibility = HeaderBar.Visibility = StatusBar.Visibility = Visibility.Visible;
            SidebarColumn.Width = new GridLength(360);
            WindowStyle = _restoreStyle;
            WindowState = _restoreState;
        }
    }

    private void OpenScreenshots_Click(object sender, RoutedEventArgs e) => OpenFolder(ScreenshotDir);
    private void OpenLogs_Click(object sender, RoutedEventArgs e) => OpenFolder(LogDir);

    private void OpenFolder(string dir)
    {
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
    }
}
