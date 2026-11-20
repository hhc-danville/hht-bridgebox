using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using HHT.Bridge.Core.Audio;
using HHT.Bridge.Core.Automation;
using HHT.Bridge.Core.Ch9329;
using HHT.Bridge.Core.Hardware;
using HHT.Bridge.Core.Integrations;
using HHT.Bridge.Core.Licensing;
using HHT.Bridge.Core.Remote;
using HHT.Bridge.Core.Reports;
using HHT.Bridge.Core.Text;
using HHT.Bridge.TechConsole.Capture;
using HHT.Bridge.TechConsole.Security;

namespace HHT.Bridge.TechConsole;

// v0.4: RMM ticket sync, time tracking, playbooks, AI assistant, agent hand-off, BIOS before/after,
// beep codes, customer sign-off, boot tests, sensors, cloud dashboard, licensing.
public partial class MainWindow
{
    private Edition _edition = Edition.Free;
    private RmmClient? _rmm;
    private RmmTicket? _ticket;
    private RmmOfflineQueue? _queue;
    private TimeTracker _time = new(DateTime.Now);
    private PlaybookSession? _playbook;
    private AiAnalysis? _aiAnalysis;
    private readonly List<SettingChange> _biosChanges = new();
    private readonly List<string> _testResults = new();
    private AudioRecorder? _audio;
    private System.Windows.Threading.DispatcherTimer? _audioTimer;
    private SignOff? _signOff;
    private bool _signSubscribed;
    private CancellationTokenSource? _testCts;
    private CloudPublisher? _cloud;
    private readonly string _sessionId = Guid.NewGuid().ToString("N");

    private const string NoStatusChange = "(leave status unchanged)";
    private string QueuePath => Path.Combine(AppDataDir, "rmm-queue.json");
    private string ComparePath => Path.Combine(AppDataDir, "compare", (_launch.Ticket ?? _ticket?.TicketNumber ?? "no-ticket") + ".json");
    private string TimesheetPath => Path.Combine(AppDataDir, "timesheet.csv");

    private void InitV04()
    {
        // license
        LicenseBox.Text = _settings.LicenseKey ?? "";
        ApplyLicense(_settings.LicenseKey, quiet: true);

        // time
        _time = new TimeTracker(_log.Started) { IncrementMinutes = Math.Clamp(_settings.BillingIncrementMinutes, 1, 60) };
        _log.EntryWritten += _ => _time.Mark(DateTime.Now);

        // ticket / RMM
        RmmUrlBox.Text = _settings.RmmUrl;
        RmmKeyBox.Password = Dpapi.UnprotectString(_settings.RmmKeyProtected) ?? "";
        TicketBox.Text = _launch.Ticket ?? "";
        FinalStatusCombo.Items.Add(NoStatusChange);
        foreach (var s in RmmClient.Statuses) FinalStatusCombo.Items.Add(s);
        FinalStatusCombo.SelectedIndex = 0;
        ReportToCustomerCheck.IsChecked = _settings.ReportToCustomer;
        _queue = new RmmOfflineQueue(QueuePath);
        UpdateQueueText();

        // guide / AI
        foreach (var p in PlaybookLibrary.BuiltIn) PlaybookCombo.Items.Add(p.Name);
        PlaybookCombo.SelectedIndex = 0;
        AiUrlBox.Text = _settings.AiUrl;
        AiKeyBox.Password = Dpapi.UnprotectString(_settings.AiKeyProtected) ?? "";

        // hand-off
        AgentUrlBox.Text = _settings.AgentUrl ?? "";
        AgentArgsBox.Text = _settings.AgentArgs;

        // tools
        foreach (var page in new[] { "Main", "Boot", "Storage", "Security", "Advanced", "Power" }) ComparePageCombo.Items.Add(page);
        ComparePageCombo.SelectedIndex = 0;
        try { foreach (var d in AudioRecorder.Devices()) AudioDeviceCombo.Items.Add(d); } catch (Exception) { }
        if (AudioDeviceCombo.Items.Count > 0) AudioDeviceCombo.SelectedIndex = 0;

        // hardware tests
        foreach (var p in BootTestPlans.BuiltIn) TestPlanCombo.Items.Add(p.Name);
        TestPlanCombo.SelectedIndex = 0;

        // cloud
        CloudUrlBox.Text = _settings.CloudUrl;
        CloudKeyBox.Password = Dpapi.UnprotectString(_settings.CloudKeyProtected) ?? "";
        if (_settings.CloudEnabled && LicenseVerifier.Allows(_edition, Feature.CloudRelay)) { CloudCheck.IsChecked = true; StartCloud(); }
        ApplyAlertRules(); // re-apply now that the edition is known
        UpdateTimeText();
    }

    private async Task InitV04LoadedAsync()
    {
        if (_launch.Ticket != null && RmmKeyBox.Password.Length > 0) await LookupTicketAsync(quiet: true);
    }

    private void ShutdownV04()
    {
        _testCts?.Cancel();
        _cloud?.Dispose();
        _audio?.Dispose();
        _rmm?.Dispose();
        try
        {
            var summary = _time.Summarize(DateTime.Now);
            if (summary.Active > TimeSpan.Zero)
                TimeTracker.AppendTimesheet(TimesheetPath, _log.Started, _ticket?.TicketNumber ?? _launch.Ticket, _log.Technician ?? Environment.UserName, summary);
        }
        catch (Exception) { /* timesheet is best-effort */ }
    }

    /// <summary>Called from the 1.5 s status timer.</summary>
    private void UpdateTimeText() => TimeText.Text = _time.Summarize(DateTime.Now).ToString();

    // ------------------------------------------------------------------ licensing (item 14)

    private bool Require(Feature feature)
    {
        if (LicenseVerifier.Allows(_edition, feature)) return true;
        MessageBox.Show(this, $"{feature} needs the {LicenseVerifier.RequiredFor(feature)} edition. Enter a license key on the License tab.",
            "HHT Bridge Console", MessageBoxButton.OK, MessageBoxImage.Information);
        return false;
    }

    private void ApplyLicense(string? key, bool quiet)
    {
        _edition = LicenseVerifier.EffectiveEdition(key, DateTime.UtcNow, out var status);
        LicenseStatusText.Text = status;
        SubtitleText.Text = $"Hammer Hill Technologies · Bridge Box · {_edition} edition";
        if (!quiet) _log.Audit($"License: {status}");
    }

    private void ApplyLicense_Click(object sender, RoutedEventArgs e)
    {
        var key = LicenseBox.Text.Trim();
        if (key.Length > 0 && !LicenseVerifier.TryVerify(key, DateTime.UtcNow, out _, out var err))
        {
            MessageBox.Show(this, err, "License", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _settings.LicenseKey = key.Length > 0 ? key : null;
        SaveSettings();
        ApplyLicense(_settings.LicenseKey, quiet: false);
    }

    // ------------------------------------------------------------------ RMM ticket sync (item 7) + time (item 10)

    private RmmClient? EnsureRmm()
    {
        if (_rmm != null) return _rmm;
        try { _rmm = new RmmClient(RmmUrlBox.Text.Trim(), RmmKeyBox.Password); }
        catch (ArgumentException ex) { RmmStatusText.Text = ex.Message; return null; }
        return _rmm;
    }

    private async void RmmTest_Click(object sender, RoutedEventArgs e)
    {
        if (!Require(Feature.RmmSync)) return;
        _rmm?.Dispose();
        _rmm = null;
        _settings.RmmUrl = RmmUrlBox.Text.Trim();
        _settings.RmmKeyProtected = Dpapi.ProtectString(RmmKeyBox.Password);
        SaveSettings();
        var c = EnsureRmm();
        if (c == null) return;
        try
        {
            var ping = await c.PingAsync();
            RmmStatusText.Text = $"Connected to {ping.Server} as {ping.Tech} (scope: {ping.Scope}{(ping.CanWrite ? "" : ", READ ONLY")}).";
            await FlushQueueAsync();
        }
        catch (RmmApiException ex) { RmmStatusText.Text = ex.Message; }
    }

    private async void TicketLookup_Click(object sender, RoutedEventArgs e) => await LookupTicketAsync(quiet: false);

    private async Task LookupTicketAsync(bool quiet)
    {
        if (!LicenseVerifier.Allows(_edition, Feature.RmmSync)) { if (!quiet) Require(Feature.RmmSync); return; }
        var c = EnsureRmm();
        if (c == null) return;
        if (!RmmClient.TryNormalizeTicket(TicketBox.Text, out var number)) { TicketInfoText.Text = "Enter a ticket number like T-00012."; return; }
        try
        {
            var t = await c.GetTicketAsync(number);
            _ticket = t;
            TicketInfoText.Text = $"{t.TicketNumber} · {t.Status}{(t.Priority != null ? $" · {t.Priority}" : "")}\n" +
                                  $"Customer: {t.Customer?.Name ?? "—"}{(t.Customer?.Phone is { } ph ? $" · {ph}" : "")}\n" +
                                  $"Device: {t.Device?.Label ?? "—"}{(t.Device?.Serial is { } sn ? $" · S/N {sn}" : "")}\n" +
                                  $"Issue: {t.Issue ?? t.Subject ?? "—"}";
            Title = $"HHT Bridge Console — {t.TicketNumber}" + (t.Customer?.Name is { } n ? $" — {n}" : "");
            if (string.IsNullOrWhiteSpace(SignSummaryBox.Text)) SignSummaryBox.Text = t.Subject ?? "";
            _log.Audit($"Ticket {t.TicketNumber} loaded ({t.Customer?.Name}).");
            // Compare serials: the screen's service tag vs the RMM asset record.
            var screenSerial = _findings.FirstOrDefault(f => f.Kind is FindingKind.ServiceTag or FindingKind.SerialNumber)?.Value;
            if (screenSerial != null && t.Device?.Serial is { Length: > 0 } rmmSerial && !rmmSerial.Equals(screenSerial, StringComparison.OrdinalIgnoreCase))
                _log.Warn($"Serial on screen ({screenSerial}) does not match the ticket's device ({rmmSerial}). Is this the right PC?");
        }
        catch (RmmApiException ex)
        {
            TicketInfoText.Text = ex.Message;
            if (!quiet) _log.Warn($"Ticket lookup failed: {ex.Message}");
        }
    }

    private void ReportToCustomer_Click(object sender, RoutedEventArgs e)
    {
        _settings.ReportToCustomer = ReportToCustomerCheck.IsChecked == true;
        SaveSettings();
    }

    private string? ChosenFinalStatus => FinalStatusCombo.SelectedItem as string is { } s && s != NoStatusChange ? s : null;

    private async void PostSession_Click(object sender, RoutedEventArgs e)
    {
        if (!Require(Feature.RmmSync)) return;
        var c = EnsureRmm();
        if (c == null || !RmmClient.TryNormalizeTicket(TicketBox.Text, out var number)) { RmmStatusText.Text = "Load a ticket first."; return; }
        var status = ChosenFinalStatus;
        if (status == "resolved" && MessageBox.Show(this, $"Post this session to {number} and mark it RESOLVED?", "Post session",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        PostSessionButton.IsEnabled = false;
        var notes = RmmClient.NotesFromLog(_log.Entries, _sessionId).ToList();
        var summary = _time.Summarize(DateTime.Now);
        notes.Add(new RmmNote("info", "info", $"Bench time: {summary}", null, DateTime.Now, $"{_sessionId}-time-{(int)summary.Active.TotalMinutes}"));
        bool customer = ReportToCustomerCheck.IsChecked == true;
        var html = Encoding.UTF8.GetBytes(SessionReportWriter.ToHtml(BuildReport(customerCopy: customer)));
        try
        {
            var r = await c.PostSessionAsync(number, _log.Started, DateTime.Now, notes, html, $"bridge-report-{DateTime.Now:yyyyMMdd-HHmm}.html", status, customer);
            RmmStatusText.Text = $"Posted: {r.NotesCreated} new notes ({r.Duplicates} already there), report attached" + (r.Status != null ? $", status {r.Status}." : ".");
            _log.Audit($"Session posted to {number}: {r.NotesCreated} notes, report #{r.AttachmentId}{(r.Status != null ? $", status {r.Status}" : "")}.");
        }
        catch (RmmApiException ex) when (ex.IsTransient)
        {
            foreach (var n in notes) _queue!.Add(number, n);
            Directory.CreateDirectory(ReportDir);
            var path = Path.Combine(ReportDir, $"unsent-report-{number}-{DateTime.Now:yyyyMMdd-HHmmss}.html");
            File.WriteAllBytes(path, html);
            RmmStatusText.Text = $"RMM unreachable ({ex.Message}). {notes.Count} notes queued; report saved to {path}. Use 'Send queued notes' later.";
            _log.Warn("Session not posted (RMM unreachable); notes queued.");
        }
        catch (RmmApiException ex)
        {
            RmmStatusText.Text = ex.Message;
            _log.Warn($"Session post rejected: {ex.Message}");
        }
        finally
        {
            PostSessionButton.IsEnabled = true;
            UpdateQueueText();
        }
    }

    private async void AttachShots_Click(object sender, RoutedEventArgs e)
    {
        if (!Require(Feature.RmmSync)) return;
        var c = EnsureRmm();
        if (c == null || !RmmClient.TryNormalizeTicket(TicketBox.Text, out var number) || _screenshots.Count == 0) { RmmStatusText.Text = "Need a ticket and at least one screenshot."; return; }
        int sent = 0;
        foreach (var s in _screenshots.ToList())
        {
            try
            {
                await c.AttachAsync(number, Path.GetFileName(s.Path), "other", "image/png", await File.ReadAllBytesAsync(s.Path));
                sent++;
            }
            catch (Exception ex) { _log.Warn($"Attach {Path.GetFileName(s.Path)} failed: {ex.Message}"); }
        }
        RmmStatusText.Text = $"Attached {sent} of {_screenshots.Count} screenshots to {number}.";
        _log.Audit($"Attached {sent} screenshot(s) to {number}.");
    }

    private async void RetryQueue_Click(object sender, RoutedEventArgs e) => await FlushQueueAsync();

    private async Task FlushQueueAsync()
    {
        var c = EnsureRmm();
        if (c == null || _queue == null || _queue.Items.Count == 0) { UpdateQueueText(); return; }
        var (sent, kept, dropped) = await _queue.FlushAsync(c, m => _log.Warn(m));
        RmmStatusText.Text = $"Queued notes: {sent} sent, {kept} still waiting, {dropped} rejected.";
        UpdateQueueText();
    }

    private void UpdateQueueText()
    {
        int n = _queue?.Items.Count ?? 0;
        RetryQueueButton.Content = n > 0 ? $"Send queued notes ({n})" : "Send queued notes";
    }

    /// <summary>One report builder for the local report, the ticket attachment and the customer copy.</summary>
    private SessionReport BuildReport(bool customerCopy) => new()
    {
        Ticket = _ticket?.TicketNumber ?? _launch.Ticket,
        Technician = _log.Technician ?? Environment.UserName,
        Started = _log.Started,
        Ended = DateTime.Now,
        VideoDevice = _capture.IsRunning ? VideoDeviceCombo.SelectedItem as string : null,
        ControlDevice = _controller != null ? $"CH9329 {_controller.LastInfo?.Version} on {_controller.PortName}" : null,
        Notes = NotesBox.Text,
        RecordingPath = _recordings.Count > 0 ? string.Join("; ", _recordings) : _capture.RecordingPath,
        Events = _log.Entries,
        Screenshots = _screenshots.ToArray(),
        Findings = _findings.ToArray(),
        Time = _time.Summarize(DateTime.Now),
        BiosChanges = _biosChanges.ToArray(),
        TestResults = _testResults.ToArray(),
        AiSummary = _aiAnalysis?.Summary,
        AiRecommendations = _aiAnalysis?.Findings.Select(f => $"{f.Title}: {f.Recommendation}").ToArray() ?? Array.Empty<string>(),
        SignOff = _signOff,
        CustomerCopy = customerCopy,
    };

    // ------------------------------------------------------------------ playbooks (item 2)

    private void PlaybookStart_Click(object sender, RoutedEventArgs e)
    {
        if (!Require(Feature.Playbooks)) return;
        var pb = PlaybookLibrary.BuiltIn[Math.Max(0, PlaybookCombo.SelectedIndex)];
        _playbook = new PlaybookSession(pb);
        PlaybookButtons.IsEnabled = true;
        _log.Audit($"Playbook started: {pb.Name}");
        ShowPlaybookStep();
    }

    private void ShowPlaybookStep()
    {
        if (_playbook == null) return;
        if (_playbook.IsComplete)
        {
            PlaybookStepTitle.Text = "Playbook complete";
            PlaybookStepText.Text = $"{_playbook.Playbook.Name}: done.";
            PlaybookButtons.IsEnabled = false;
            _log.Audit($"Playbook finished: {_playbook.Playbook.Name} (steps: {string.Join(" → ", _playbook.History)})");
            return;
        }
        var s = _playbook.Current;
        PlaybookStepTitle.Text = s.Title;
        PlaybookStepText.Text = s.Instruction;
        PlaybookDoButton.IsEnabled = s.MacroId != null || s.KeyChord != null;
        PlaybookDoButton.Content = s.MacroId != null ? "Run macro" : s.KeyChord != null ? $"Press {s.KeyChord}" : "Do it";
        PlaybookCheckButton.IsEnabled = s.ScreenRegex != null && _ocr.Available;
    }

    private async void PlaybookDo_Click(object sender, RoutedEventArgs e)
    {
        var s = _playbook?.Current;
        if (s == null || !EnsureController()) return;
        if (s.MacroId != null && MacroLibrary.BuiltIn.FirstOrDefault(m => m.Id == s.MacroId) is { } macro) await RunMacroAsync(macro);
        else if (s.KeyChord != null)
        {
            _controller!.SendKeyboardSequence(new[] { KeyChord.Parse(s.KeyChord), KeyboardReport.Empty });
            _log.Audit($"Playbook key: {s.KeyChord}");
        }
    }

    private async void PlaybookCheck_Click(object sender, RoutedEventArgs e)
    {
        if (_playbook == null) return;
        var text = await OcrCurrentFrameAsync();
        if (text == null) { PlaybookStepText.Text += "\n(Could not read the screen — start the video.)"; return; }
        _playbook.EvaluateScreen(text);
        ShowPlaybookStep();
    }

    private void PlaybookNext_Click(object sender, RoutedEventArgs e) { _playbook?.Advance(); ShowPlaybookStep(); }
    private void PlaybookBack_Click(object sender, RoutedEventArgs e) { _playbook?.Back(); PlaybookButtons.IsEnabled = _playbook != null; ShowPlaybookStep(); }

    // ------------------------------------------------------------------ AI assistant (item 3)

    private async void AiTest_Click(object sender, RoutedEventArgs e)
    {
        _settings.AiUrl = AiUrlBox.Text.Trim();
        _settings.AiKeyProtected = Dpapi.ProtectString(AiKeyBox.Password);
        SaveSettings();
        try
        {
            using var ai = new AiClient(_settings.AiUrl, AiKeyBox.Password);
            AiSummaryText.Text = await ai.HealthAsync() ? "AI backend online." : "AI backend not reachable (check URL and gate key).";
        }
        catch (ArgumentException ex) { AiSummaryText.Text = ex.Message; }
    }

    private async void AskAi_Click(object sender, RoutedEventArgs e)
    {
        if (!Require(Feature.Ai)) return;
        AskAiButton.IsEnabled = false;
        AiSummaryText.Text = "Reading the screen and asking the AI...";
        AiFindingsList.Items.Clear();
        try
        {
            var screen = await OcrCurrentFrameAsync() ?? _lastOcrText;
            var telemetry = AiClient.BuildTelemetry(screen, _findings, _ticket?.Issue, _ticket?.Device?.Label, CurrentBiosProfile.Brand,
                _log.Entries.Where(x => x.Level is "AUDIT" or "WARN").Select(x => x.Message), AiQuestionBox.Text.Trim());
            using var ai = new AiClient(AiUrlBox.Text.Trim(), AiKeyBox.Password);
            var result = await ai.AnalyzeAsync(telemetry);
            _aiAnalysis = result;
            AiSummaryText.Text = $"[{result.OverallSeverity}] {result.Summary}";
            foreach (var f in result.Findings) AiFindingsList.Items.Add($"{f.Severity?.ToUpperInvariant()} · {f.Title} — {f.Recommendation}");
            _log.Audit($"AI assistant consulted ({result.Findings.Count} suggestion(s)).");
        }
        catch (Exception ex) when (ex is HttpRequestException or ArgumentException or InvalidDataException or TaskCanceledException or System.Text.Json.JsonException)
        {
            AiSummaryText.Text = $"AI assistant unavailable: {ex.Message}";
        }
        finally { AskAiButton.IsEnabled = true; }
    }

    // ------------------------------------------------------------------ agent hand-off (item 8)

    private async void AgentInstall_Click(object sender, RoutedEventArgs e)
    {
        if (!Require(Feature.Macros)) return;
        Macro macro;
        try { macro = MacroLibrary.AgentInstall(AgentUrlBox.Text.Trim(), AgentArgsBox.Text.Trim(), "agent"); }
        catch (ArgumentException ex) { MessageBox.Show(this, ex.Message, "Hand-off"); return; }
        _settings.AgentUrl = AgentUrlBox.Text.Trim();
        _settings.AgentArgs = AgentArgsBox.Text.Trim();
        SaveSettings();
        await RunMacroAsync(macro);
    }

    // ------------------------------------------------------------------ BIOS before/after (item 5)

    private string ComparePage => string.IsNullOrWhiteSpace(ComparePageCombo.Text) ? "Main" : ComparePageCombo.Text.Trim();

    private async void SaveBefore_Click(object sender, RoutedEventArgs e)
    {
        if (!Require(Feature.BiosCompare)) return;
        var text = await OcrCurrentFrameAsync();
        if (text == null) { CompareList.Items.Clear(); CompareList.Items.Add("Start the video (and OCR) first."); return; }
        var snaps = ScreenCompare.Load(ComparePath).Where(s => s.Label != ComparePage).ToList();
        snaps.Add(new ScreenSnapshot(ComparePage, DateTime.Now, text));
        ScreenCompare.Save(ComparePath, snaps);
        int settings = ScreenCompare.ExtractSettings(text).Count;
        CompareList.Items.Clear();
        CompareList.Items.Add($"Saved BEFORE for '{ComparePage}' ({settings} settings read).");
        TakeScreenshot($"BIOS {ComparePage} — before");
        _log.Audit($"BIOS '{ComparePage}' saved as BEFORE ({settings} settings).");
    }

    private async void CompareAfter_Click(object sender, RoutedEventArgs e)
    {
        if (!Require(Feature.BiosCompare)) return;
        var before = ScreenCompare.Load(ComparePath).FirstOrDefault(s => s.Label == ComparePage);
        CompareList.Items.Clear();
        if (before == null) { CompareList.Items.Add($"No BEFORE saved for '{ComparePage}' on this ticket."); return; }
        var text = await OcrCurrentFrameAsync();
        if (text == null) { CompareList.Items.Add("Start the video (and OCR) first."); return; }
        var changes = ScreenCompare.Compare(before, new ScreenSnapshot(ComparePage, DateTime.Now, text));
        _biosChanges.RemoveAll(c => c.Setting.StartsWith(ComparePage + ": ", StringComparison.Ordinal));
        foreach (var c in changes) _biosChanges.Add(c with { Setting = $"{ComparePage}: {c.Setting}" });
        if (changes.Count == 0) CompareList.Items.Add("No setting changes detected.");
        foreach (var c in changes) CompareList.Items.Add(c.ToString());
        TakeScreenshot($"BIOS {ComparePage} — after");
        _log.Audit($"BIOS '{ComparePage}' compared: {changes.Count} change(s).");
    }

    // ------------------------------------------------------------------ beep codes (item 11)

    private void Listen_Click(object sender, RoutedEventArgs e)
    {
        if (_audio?.IsRecording == true) { FinishListening(); return; }
        if (!Require(Feature.AudioBeep)) return;
        if (AudioDeviceCombo.SelectedIndex < 0) { BeepResultText.Text = "No microphone found."; return; }
        try
        {
            _audio = new AudioRecorder();
            _audio.Start(AudioDeviceCombo.SelectedIndex);
        }
        catch (Exception ex) { BeepResultText.Text = ex.Message; return; }
        ListenButton.Content = "Stop listening";
        BeepResultText.Text = "Listening... power on the PC now (stops after 30 s).";
        _audioTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _audioTimer.Tick += (_, _) => FinishListening();
        _audioTimer.Start();
    }

    private void FinishListening()
    {
        _audioTimer?.Stop();
        if (_audio == null) return;
        var samples = _audio.Stop();
        _audio.Dispose();
        _audio = null;
        ListenButton.Content = "Listen for beep codes";
        var result = BeepCodeAnalyzer.Analyze(samples, AudioRecorder.SampleRate, CurrentBiosProfile.Brand);
        BeepResultText.Text = result.Describe();
        try
        {
            Directory.CreateDirectory(RecordingDir);
            var wav = Path.Combine(RecordingDir, $"beeps-{DateTime.Now:yyyyMMdd-HHmmss}.wav");
            File.WriteAllBytes(wav, Wav.Write(samples, AudioRecorder.SampleRate));
            _recordings.Add(wav);
        }
        catch (Exception) { }
        if (result.Heard) _testResults.Add($"Beep codes: {result.Describe()}");
        _log.Audit($"Beep listen: {result.Describe()}");
    }

    // ------------------------------------------------------------------ customer sign-off (item 9)

    private void StartSignOff_Click(object sender, RoutedEventArgs e)
    {
        if (!Require(Feature.SignOff)) return;
        if (_server == null) { MessageBox.Show(this, "Turn on 'Share this bench' (Share tab) first — the tablet signs through it.", "Sign-off"); return; }
        if (string.IsNullOrWhiteSpace(SignSummaryBox.Text)) { MessageBox.Show(this, "Type a short summary of the work for the customer.", "Sign-off"); return; }
        if (!_signSubscribed)
        {
            _server.SignedOff += so => Dispatcher.InvokeAsync(() =>
            {
                _signOff = so;
                SignStatusText.Text = $"Signed by {so.Name} at {so.Time:HH:mm}. It will appear in the report.";
            });
            _signSubscribed = true;
        }
        var code = _server.StartSignOff(new SignOffRequest("Hammer Hill Computers", _ticket?.TicketNumber ?? _launch.Ticket,
            SignSummaryBox.Text.Trim(), string.IsNullOrWhiteSpace(SignTotalBox.Text) ? null : SignTotalBox.Text.Trim()));
        var url = ShareUrlList.Items.OfType<string>().FirstOrDefault(u => !u.EndsWith("dashboard"))?.TrimEnd('/') + "/sign";
        SignStatusText.Text = $"On the customer tablet open:\n{url}\nCode: {code}   (valid 30 minutes, single use)";
        _log.Audit("Customer sign-off started.");
    }

    // ------------------------------------------------------------------ annotate (item 6)

    private void Annotate_Click(object sender, RoutedEventArgs e)
    {
        var last = _screenshots.LastOrDefault();
        if (last == null || !File.Exists(last.Path)) { MessageBox.Show(this, "Take a screenshot first.", "Annotate"); return; }
        var w = new AnnotateWindow(last.Path) { Owner = this };
        if (w.ShowDialog() == true && w.SavedPath != null)
        {
            _screenshots.Add(new ReportScreenshot(DateTime.Now, w.SavedPath, last.Caption + " (annotated)"));
            _log.Audit($"Annotated screenshot saved: {w.SavedPath}");
        }
    }

    // ------------------------------------------------------------------ boot tests (item 1) + sensors (item 12)

    private void TestPlan_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        var p = BootTestPlans.BuiltIn[Math.Max(0, TestPlanCombo.SelectedIndex)];
        TestImageBox.Text = p.ImageName;
        TestCdCheck.IsChecked = p.ImageName.EndsWith(".iso", StringComparison.OrdinalIgnoreCase) || p.ImageName.Length == 0;
        TestStatusText.Text = p.Notes;
    }

    private async void RunTest_Click(object sender, RoutedEventArgs e)
    {
        if (!Require(Feature.BootTests) || _testCts != null) return;
        if (!_ocr.Available || !_capture.IsRunning) { TestStatusText.Text = "Needs video and Windows OCR."; return; }
        bool watchOnly = TestAlreadyBootedCheck.IsChecked == true;
        if (!watchOnly && (_vm == null || !EnsureController())) { TestStatusText.Text = "Connect the virtual-media module (and keyboard control) first, or tick 'Already booted'."; return; }
        var plan = BootTestPlans.BuiltIn[Math.Max(0, TestPlanCombo.SelectedIndex)];
        var image = TestImageBox.Text.Trim();
        _testCts = new CancellationTokenSource();
        RunTestButton.IsEnabled = false;
        StopTestButton.IsEnabled = true;
        var progress = new Progress<string>(m => TestStatusText.Text = m);
        var runner = new BootTestRunner();
        var rig = new ConsoleRig(this);
        try
        {
            var outcome = watchOnly
                ? await runner.WatchAsync(plan, rig, new Regex(plan.PassRegex, RegexOptions.IgnoreCase), new Regex(plan.FailRegex, RegexOptions.IgnoreCase), DateTime.UtcNow, progress, _testCts.Token)
                : await runner.RunAsync(plan, image, TestCdCheck.IsChecked == true, rig, progress, _testCts.Token);
            var line = $"{plan.Name}: {outcome.Result.ToString().ToUpperInvariant()} — {outcome.Message} ({outcome.Duration.TotalMinutes:0} min)";
            TestStatusText.Text = line;
            _testResults.Add(line);
            _log.Audit($"Hardware test: {line}");
            if ((outcome.Result is BootTestResult.Pass or BootTestResult.Fail or BootTestResult.Timeout) && _notifier.IsConfigured)
                await _notifier.SendAsync($"{_settings.BenchName}: {plan.Name} {outcome.Result}", line, urgent: outcome.Result != BootTestResult.Pass);
        }
        finally
        {
            _testCts.Dispose();
            _testCts = null;
            RunTestButton.IsEnabled = true;
            StopTestButton.IsEnabled = false;
        }
    }

    private void StopTest_Click(object sender, RoutedEventArgs e) => _testCts?.Cancel();

    /// <summary>Connects the boot-test runner to this bench's virtual media, controller, keyboard and OCR.</summary>
    private sealed class ConsoleRig : IBenchRig
    {
        private readonly MainWindow _w;
        public ConsoleRig(MainWindow w) => _w = w;

        public async Task<bool> MountAsync(string imageName, bool asCdrom, CancellationToken ct)
        {
            if (_w._vm == null) return false;
            try { await _w._vm.MountAsync(imageName, asCdrom, ct); return true; }
            catch (Exception ex) { Log($"Mount failed: {ex.Message}"); return false; }
        }

        public async Task<bool> RestartTargetAsync(CancellationToken ct)
        {
            if (_w._box != null)
            {
                var st = await _w._box.GetStatusAsync();
                if (st?.PowerLed == true)
                {
                    await _w._box.PressPowerAsync(longPress: true);
                    await Task.Delay(TimeSpan.FromSeconds(9), ct);
                }
                return (await _w._box.PressPowerAsync(longPress: false)).Ok;
            }
            return MessageBox.Show(_w, "Restart (or power on) the customer PC now, then press OK immediately.", "Hardware test",
                MessageBoxButton.OKCancel, MessageBoxImage.Information) == MessageBoxResult.OK;
        }

        public async Task<bool> OpenBootMenuAsync(CancellationToken ct)
        {
            var seq = new BiosEntrySequencer { MaxDuration = TimeSpan.FromSeconds(75) };
            var r = await seq.RunAsync(_w.CurrentBiosProfile, BiosTarget.BootMenu, rep => _w._controller?.SendKeyboard(rep),
                async _ => (await _w.OcrCurrentFrameAsync()) is { } t && BiosTextParser.LooksLikeFirmwareScreen(t), ct);
            return r.Detected;
        }

        public Task<string?> ReadScreenAsync(CancellationToken ct) => _w.OcrCurrentFrameAsync();

        public void SendKey(string chord)
        {
            _w._controller?.SendKeyboard(KeyChord.Parse(chord));
            _w._controller?.SendKeyboard(KeyboardReport.Empty);
        }

        public Task ScreenshotAsync(string caption) { _w.TakeScreenshot(caption); return Task.CompletedTask; }
        public void Log(string message) => _w._log.Audit(message);
    }

    /// <summary>Called from the status timer when the controller is connected.</summary>
    private async Task PollSensorsAsync()
    {
        if (_box?.Info?.Has("SENS") != true) return;
        var s = await _box.GetSensorsAsync();
        if (s != null) SensorText.Text = s.ToString();
    }

    // ------------------------------------------------------------------ cloud dashboard (item 15)

    private void Cloud_Click(object sender, RoutedEventArgs e)
    {
        if (CloudCheck.IsChecked == true)
        {
            if (!Require(Feature.CloudRelay)) { CloudCheck.IsChecked = false; return; }
            _settings.CloudUrl = CloudUrlBox.Text.Trim();
            _settings.CloudKeyProtected = Dpapi.ProtectString(CloudKeyBox.Password);
            StartCloud();
        }
        else
        {
            _cloud?.Dispose();
            _cloud = null;
            CloudStatusText.Text = "Not publishing.";
            _log.Audit("Cloud publishing stopped.");
        }
        _settings.CloudEnabled = _cloud != null;
        SaveSettings();
    }

    private void StartCloud()
    {
        _cloud?.Dispose();
        try
        {
            _cloud = new CloudPublisher(CloudUrlBox.Text.Trim(), CloudKeyBox.Password, () =>
                new CloudPublisher.CloudSnapshot(_settings.BenchId, _settings.BenchName, _ticket?.TicketNumber ?? _launch.Ticket,
                    _capture.IsRunning, _targetUsbCached, _server?.Viewers ?? 0, _capture.GetJpeg(55).Jpeg));
            _cloud.StatusChanged += err => Dispatcher.InvokeAsync(() =>
                CloudStatusText.Text = err ?? $"Publishing to {CloudUrlBox.Text.Trim()} (view only).");
            _cloud.Start();
            CloudStatusText.Text = "Starting...";
            _log.Audit("Cloud publishing started (view only).");
        }
        catch (ArgumentException ex)
        {
            _cloud = null;
            CloudCheck.IsChecked = false;
            CloudStatusText.Text = ex.Message;
        }
    }
}
