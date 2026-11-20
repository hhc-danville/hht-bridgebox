using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using HHT.Bridge.Core.Automation;
using HHT.Bridge.Core.Reports;
using HHT.Bridge.Core.Text;
using HHT.Bridge.TechConsole.Capture;

namespace HHT.Bridge.TechConsole;

// Items 1–4: BIOS entry, screen reading (OCR) with findings, session recording, service reports.
public partial class MainWindow
{
    private readonly OcrService _ocr = new();
    private readonly List<Finding> _findings = new();
    private readonly List<ReportScreenshot> _screenshots = new();
    private readonly List<string> _recordings = new();
    private DispatcherTimer? _autoReadTimer;
    private CancellationTokenSource? _biosCts;
    private string? _lastOcrText;
    private DateTime _lastOcrAt;

    private string ReportDir => Path.Combine(AppDataDir, "reports");
    private string RecordingDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "HHT Bridge");

    private void InitFeatures()
    {
        foreach (var p in BiosProfiles.All) BiosBrandCombo.Items.Add(p.Brand);
        BiosBrandCombo.SelectedItem = BiosProfiles.Find(_settings.BiosBrand)?.Brand ?? BiosProfiles.All[0].Brand;
        AutoReadCheck.IsChecked = _settings.AutoReadScreen;
        OcrStatusText.Text = _ocr.Available
            ? $"Windows OCR ready ({_ocr.LanguageTag})."
            : "Windows OCR is not available (install an OCR language in Settings → Time & language → Language).";
        ReadScreenButton.IsEnabled = _ocr.Available;
        AutoReadCheck.IsEnabled = _ocr.Available;
        _autoReadTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _autoReadTimer.Tick += async (_, _) =>
        {
            if (HHT.Bridge.Core.Licensing.LicenseVerifier.Allows(_edition, HHT.Bridge.Core.Licensing.Feature.Ocr)) await ReadScreenAsync(quiet: true);
        };
        if (_settings.AutoReadScreen && _ocr.Available) _autoReadTimer.Start();
    }

    private void ShutdownFeatures()
    {
        _autoReadTimer?.Stop();
        _biosCts?.Cancel();
        StopRecordingIfActive();
    }

    // ------------------------------------------------------------------ BIOS entry (item 3)

    private BiosProfile CurrentBiosProfile =>
        BiosProfiles.Find(BiosBrandCombo.SelectedItem as string ?? "") ?? BiosProfiles.All[0];

    private void BiosBrand_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        var p = CurrentBiosProfile;
        BiosKeysText.Text = p.SetupKeys.Length == 0
            ? p.Notes
            : $"Setup: {string.Join(" / ", p.SetupKeys)}   Boot menu: {string.Join(" / ", p.BootMenuKeys)}" + (p.Notes.Length > 0 ? $"\n{p.Notes}" : "");
        if (IsLoaded) { _settings.BiosBrand = p.Brand; SaveSettings(); }
    }

    private async void BiosSetup_Click(object sender, RoutedEventArgs e) => await RunBiosEntryAsync(BiosTarget.Setup);
    private async void BootMenu_Click(object sender, RoutedEventArgs e) => await RunBiosEntryAsync(BiosTarget.BootMenu);
    private void BiosStop_Click(object sender, RoutedEventArgs e) => _biosCts?.Cancel();

    private async Task RunBiosEntryAsync(BiosTarget target)
    {
        if (!Require(HHT.Bridge.Core.Licensing.Feature.BiosEntry) || !EnsureController() || _biosCts != null) return;
        var profile = CurrentBiosProfile;
        _biosCts = new CancellationTokenSource();
        BiosSetupButton.IsEnabled = BootMenuButton.IsEnabled = false;
        BiosStopButton.IsEnabled = true;
        BiosStatusText.Text = $"Tapping {string.Join("/", target == BiosTarget.Setup ? profile.SetupKeys : profile.BootMenuKeys)} — power on or restart the customer PC now.";
        _log.Audit($"BIOS entry started: {profile.Brand}, {(target == BiosTarget.Setup ? "setup" : "boot menu")}.");

        Func<CancellationToken, Task<bool>>? detect = null;
        if (_ocr.Available && _capture.IsRunning)
            detect = async _ =>
            {
                var text = await OcrCurrentFrameAsync();
                return text != null && BiosTextParser.LooksLikeFirmwareScreen(text);
            };
        try
        {
            var seq = new BiosEntrySequencer();
            var result = await seq.RunAsync(profile, target, r => _controller?.SendKeyboard(r), detect, _biosCts.Token);
            BiosStatusText.Text = $"{result.Message} ({result.KeyPresses} key presses in {result.Elapsed.TotalSeconds:0} s)";
            _log.Audit($"BIOS entry finished: {result.Message}");
            if (result.Detected && _ocr.Available) await ReadScreenAsync(quiet: true);
        }
        finally
        {
            _biosCts.Dispose();
            _biosCts = null;
            BiosSetupButton.IsEnabled = BootMenuButton.IsEnabled = true;
            BiosStopButton.IsEnabled = false;
        }
    }

    // ------------------------------------------------------------------ OCR (item 4)

    private async Task<string?> OcrCurrentFrameAsync()
    {
        if (!_ocr.Available || !_capture.IsRunning) return null;
        var frame = _capture.CopyLatest();
        if (frame == null) return null;
        var (bgr, w, h) = frame.Value;
        var text = await _ocr.ReadAsync(bgr, w, h);
        if (text != null)
        {
            _lastOcrText = text;
            _lastOcrAt = DateTime.Now;
            _monitor.OnText(text, DateTime.UtcNow);
        }
        return text;
    }

    private async void ReadScreen_Click(object sender, RoutedEventArgs e)
    {
        if (Require(HHT.Bridge.Core.Licensing.Feature.Ocr)) await ReadScreenAsync(quiet: false);
    }

    private void AutoRead_Click(object sender, RoutedEventArgs e)
    {
        if (AutoReadCheck.IsChecked == true && !Require(HHT.Bridge.Core.Licensing.Feature.Ocr)) { AutoReadCheck.IsChecked = false; return; }
        _settings.AutoReadScreen = AutoReadCheck.IsChecked == true;
        if (_settings.AutoReadScreen) _autoReadTimer?.Start(); else _autoReadTimer?.Stop();
        SaveSettings();
    }

    private async Task ReadScreenAsync(bool quiet)
    {
        if (!_capture.IsRunning) { if (!quiet) _log.Warn("Start the video before reading the screen."); return; }
        var text = await OcrCurrentFrameAsync();
        if (text == null) return;
        var found = BiosTextParser.Parse(text);
        int added = 0;
        foreach (var f in found)
        {
            if (_findings.Any(x => x.Kind == f.Kind && x.Value.Equals(f.Value, StringComparison.OrdinalIgnoreCase))) continue;
            _findings.Add(f);
            FindingsList.Items.Insert(0, $"{(f.IsProblem ? "⚠ " : "")}{f.Label}: {f.Value}");
            added++;
            if (f.IsProblem) _log.Warn($"Screen shows: {f.Value}");
            else _log.Audit($"Read from screen: {f.Label} = {f.Value}");
        }
        OcrStatusText.Text = $"Read {text.Split('\n').Length} lines at {DateTime.Now:HH:mm:ss}; {added} new finding(s), {_findings.Count} total.";
    }

    private void CopyFindings_Click(object sender, RoutedEventArgs e)
    {
        if (_findings.Count == 0) return;
        try { Clipboard.SetText(string.Join(Environment.NewLine, _findings.Select(f => $"{f.Label}: {f.Value}"))); }
        catch (Exception ex) { _log.Warn($"Clipboard: {ex.Message}"); }
    }

    // ------------------------------------------------------------------ recording (item 2)

    private void Record_Click(object sender, RoutedEventArgs e)
    {
        if (_capture.IsRecording) { StopRecordingIfActive(); return; }
        if (!Require(HHT.Bridge.Core.Licensing.Feature.Recording)) return;
        if (!_capture.IsRunning) { _log.Warn("Start the video before recording."); return; }
        Directory.CreateDirectory(RecordingDir);
        var name = $"session-{DateTime.Now:yyyyMMdd-HHmmss}" + (_launch.Ticket != null ? $"-ticket-{_launch.Ticket}" : "");
        _capture.StartRecording(Path.Combine(RecordingDir, name));
        RecordButton.Content = "Stop recording";
        RecPill.Visibility = Visibility.Visible;
        _log.Audit("Recording started.");
    }

    private void StopRecordingIfActive()
    {
        if (!_capture.IsRecording) return;
        var path = _capture.StopRecording();
        RecordButton.Content = "Start recording";
        RecPill.Visibility = Visibility.Collapsed;
        if (path != null) { _recordings.Add(path); _log.Audit($"Recording saved: {path}"); }
        else _log.Warn("Recording stopped before any frame was written.");
    }

    // ------------------------------------------------------------------ report (item 1)

    private void Report_Click(object sender, RoutedEventArgs e)
    {
        if (!Require(HHT.Bridge.Core.Licensing.Feature.Reports)) return;
        try
        {
            Directory.CreateDirectory(ReportDir);
            var report = BuildReport(customerCopy: ReportToCustomerCheck.IsChecked == true);
            var path = Path.Combine(ReportDir, $"report-{DateTime.Now:yyyyMMdd-HHmmss}" + (_launch.Ticket != null ? $"-ticket-{_launch.Ticket}" : "") + ".html");
            File.WriteAllText(path, SessionReportWriter.ToHtml(report));
            _log.Audit($"Report created: {path}");
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _log.Error($"Report failed: {ex.Message}");
        }
    }

    private void OpenReports_Click(object sender, RoutedEventArgs e) => OpenFolder(ReportDir);
}
