using System.Diagnostics;
using System.IO;
using System.Media;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using HHT.Bridge.Core.Automation;
using HHT.Bridge.Core.Ch9329;
using HHT.Bridge.Core.Monitoring;

namespace HHT.Bridge.TechConsole;

// Items 5–6: macro library, bench alerts.
public partial class MainWindow
{
    private readonly ScreenActivityMonitor _monitor = new();
    private readonly AlertNotifier _notifier = new();
    private DispatcherTimer? _alertTick;
    private List<Macro> _macros = new();
    private CancellationTokenSource? _macroCts;

    private string MacrosPath => Path.Combine(AppDataDir, "macros.json");

    private sealed class MacroItem
    {
        public required Macro Macro { get; init; }
        public override string ToString() => $"{Macro.Category}  ·  {Macro.Name}";
    }

    private void InitAutomation()
    {
        LoadMacros();

        AlertsEnabledCheck.IsChecked = _settings.AlertsEnabled;
        AlertIdleCheck.IsChecked = _settings.AlertIdle;
        IdleMinutesBox.Text = _settings.IdleMinutes.ToString("0.#");
        AlertChangedCheck.IsChecked = _settings.AlertChanged;
        AlertLostCheck.IsChecked = _settings.AlertVideoLost;
        AlertTextBox.Text = _settings.AlertText;
        NotifyUrlBox.Text = _settings.NotifyUrl ?? "";
        ApplyAlertRules();

        _capture.ChangeMeasured += diff => _monitor.OnFrame(diff, DateTime.UtcNow);
        _monitor.Alert += a => Dispatcher.InvokeAsync(() => OnAlert(a));
        _alertTick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _alertTick.Tick += (_, _) => { if (_capture.IsRunning) _monitor.Tick(DateTime.UtcNow); };
        _alertTick.Start();
    }

    private void ShutdownAutomation()
    {
        _alertTick?.Stop();
        _macroCts?.Cancel();
    }

    // ------------------------------------------------------------------ macros (item 5)

    private void LoadMacros()
    {
        MacroLibrary.SaveExample(MacrosPath);
        var user = MacroLibrary.LoadUser(MacrosPath, out var error);
        if (error != null) _log.Warn($"macros.json not loaded: {error}");
        _macros = MacroLibrary.BuiltIn.Concat(user).ToList();
        MacroList.Items.Clear();
        foreach (var m in _macros) MacroList.Items.Add(new MacroItem { Macro = m });
        MacroStatusText.Text = $"{MacroLibrary.BuiltIn.Count} built-in, {user.Count} of yours.";
    }

    private void MacroList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (MacroList.SelectedItem is MacroItem mi)
            MacroDescText.Text = mi.Macro.Description + (mi.Macro.Confirm ? "  (asks before running)" : "");
    }

    private async void MacroList_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => await RunSelectedMacroAsync();
    private async void MacroRun_Click(object sender, RoutedEventArgs e) => await RunSelectedMacroAsync();
    private void MacroStop_Click(object sender, RoutedEventArgs e) => _macroCts?.Cancel();
    private void MacroReload_Click(object sender, RoutedEventArgs e) => LoadMacros();

    private void MacroEdit_Click(object sender, RoutedEventArgs e)
    {
        MacroLibrary.SaveExample(MacrosPath);
        Process.Start(new ProcessStartInfo("notepad.exe", $"\"{MacrosPath}\"") { UseShellExecute = true });
    }

    private async Task RunSelectedMacroAsync()
    {
        if (MacroList.SelectedItem is MacroItem mi) await RunMacroAsync(mi.Macro);
    }

    private async Task RunMacroAsync(Macro m)
    {
        if (!Require(HHT.Bridge.Core.Licensing.Feature.Macros) || _macroCts != null || !EnsureController()) return;
        if (m.Confirm && MessageBox.Show(this, $"Run \"{m.Name}\" on the customer PC?\n\n{m.Description}", "Confirm macro",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        _macroCts = new CancellationTokenSource();
        MacroRunButton.IsEnabled = false;
        MacroStopButton.IsEnabled = true;
        MacroStatusText.Text = $"Running {m.Name}...";
        _log.Audit($"Macro started: {m.Name}"); // the name, never the typed content
        try
        {
            var runner = new MacroRunner { KeyDelayMs = Math.Clamp(_settings.TypingDelayMs, 5, 200) };
            var result = await runner.RunAsync(m, new MacroTarget(this), _macroCts.Token, msg => Dispatcher.InvokeAsync(() => MacroStatusText.Text = msg));
            MacroStatusText.Text = $"{m.Name}: {result.Message}";
            _log.Audit($"Macro {m.Name}: {result.Message}");
        }
        finally
        {
            _macroCts.Dispose();
            _macroCts = null;
            MacroRunButton.IsEnabled = true;
            MacroStopButton.IsEnabled = false;
        }
    }

    /// <summary>Lets macros drive the CH9329 and wait for on-screen text via OCR.</summary>
    private sealed class MacroTarget : IMacroTarget
    {
        private readonly MainWindow _w;
        public MacroTarget(MainWindow w) => _w = w;

        public void SendKeyboard(KeyboardReport report) => _w._controller?.SendKeyboard(report);

        public async Task<bool> WaitForTextAsync(Regex regex, TimeSpan timeout, CancellationToken ct)
        {
            if (!_w._ocr.Available) return false;
            var end = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < end)
            {
                ct.ThrowIfCancellationRequested();
                var text = await _w.Dispatcher.InvokeAsync(() => _w.OcrCurrentFrameAsync()).Task.Unwrap();
                if (text != null && regex.IsMatch(text)) return true;
                await Task.Delay(1500, ct);
            }
            return false;
        }
    }

    // ------------------------------------------------------------------ alerts (item 6)

    private void AlertSettings_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        if (AlertsEnabledCheck.IsChecked == true && !_settings.AlertsEnabled && !Require(HHT.Bridge.Core.Licensing.Feature.Alerts))
        {
            AlertsEnabledCheck.IsChecked = false;
            return;
        }
        _settings.AlertsEnabled = AlertsEnabledCheck.IsChecked == true;
        _settings.AlertIdle = AlertIdleCheck.IsChecked == true;
        if (double.TryParse(IdleMinutesBox.Text, out var mins) && mins > 0) _settings.IdleMinutes = mins;
        _settings.AlertChanged = AlertChangedCheck.IsChecked == true;
        _settings.AlertVideoLost = AlertLostCheck.IsChecked == true;
        _settings.AlertText = AlertTextBox.Text.Trim();
        _settings.NotifyUrl = string.IsNullOrWhiteSpace(NotifyUrlBox.Text) ? null : NotifyUrlBox.Text.Trim();
        ApplyAlertRules();
        SaveSettings();
    }

    private void ApplyAlertRules()
    {
        _notifier.Url = _settings.NotifyUrl;
        bool allowed = HHT.Bridge.Core.Licensing.LicenseVerifier.Allows(_edition, HHT.Bridge.Core.Licensing.Feature.Alerts);
        _monitor.SetRules(_settings.AlertsEnabled && allowed ? _settings.BuildAlertRules() : Array.Empty<AlertRule>());
    }

    private async void OnAlert(AlertEvent a)
    {
        var bench = string.IsNullOrWhiteSpace(_settings.BenchName) ? Environment.MachineName : _settings.BenchName;
        var title = $"{bench}" + (_launch.Ticket != null ? $" (ticket {_launch.Ticket})" : "");
        AlertList.Items.Insert(0, $"{a.Time.ToLocalTime():HH:mm:ss}  {a.Message}");
        AlertBannerText.Text = a.Message;
        AlertBanner.Visibility = Visibility.Visible;
        _log.Audit($"Alert: {a.Message}");
        SystemSounds.Exclamation.Play();
        FlashTaskbar();
        if (_notifier.IsConfigured)
        {
            var (ok, detail) = await _notifier.SendAsync(title, a.Message, urgent: a.Rule.Kind == AlertKind.VideoLost);
            if (!ok) _log.Warn($"Phone notification failed: {detail}");
        }
        var hide = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        hide.Tick += (_, _) => { AlertBanner.Visibility = Visibility.Collapsed; hide.Stop(); };
        hide.Start();
    }

    private async void TestNotify_Click(object sender, RoutedEventArgs e)
    {
        AlertSettings_Changed(sender, e);
        if (!_notifier.IsConfigured) { MessageBox.Show(this, "Enter an https:// ntfy topic or webhook URL first.", "Alerts"); return; }
        var (ok, detail) = await _notifier.SendAsync($"{_settings.BenchName} test", "HHT Bridge Console test notification.");
        MessageBox.Show(this, ok ? "Sent. Check your phone." : $"Failed: {detail}", "Alerts", MessageBoxButton.OK,
            ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashInfo { public uint cbSize; public IntPtr hwnd; public uint dwFlags; public uint uCount; public uint dwTimeout; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlashWindowEx(ref FlashInfo info);

    private void FlashTaskbar()
    {
        if (IsActive) return;
        var fi = new FlashInfo
        {
            cbSize = (uint)Marshal.SizeOf<FlashInfo>(),
            hwnd = new WindowInteropHelper(this).Handle,
            dwFlags = 3 | 12, // FLASHW_ALL | FLASHW_TIMERNOFG
            uCount = uint.MaxValue,
        };
        FlashWindowEx(ref fi);
    }
}
