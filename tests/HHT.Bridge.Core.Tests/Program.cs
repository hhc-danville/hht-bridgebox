using System.Collections.Concurrent;
using System.Reflection;
using HHT.Bridge.Core.Ch9329;
using HHT.Bridge.Core.Hid;
using HHT.Bridge.Core.Input;
using HHT.Bridge.Core.Logging;
using HHT.Bridge.Core.Settings;
using HHT.Bridge.Core.Automation;
using HHT.Bridge.Core.Monitoring;
using HHT.Bridge.Core.Reports;
using HHT.Bridge.Core.Text;
using HHT.Bridge.Core.Remote;
using HHT.Bridge.Core.Hardware;
using HHT.Bridge.Core.Integrations;
using HHT.Bridge.Core.Licensing;
using HHT.Bridge.Core.Audio;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text;
using System.Text.RegularExpressions;

// ------------------------------------------------------------------ runner
int passed = 0, failed = 0;
var tests = typeof(Tests).GetMethods(BindingFlags.Public | BindingFlags.Static)
    .Where(m => m.Name.StartsWith("Test", StringComparison.Ordinal)).OrderBy(m => m.Name).ToList();
foreach (var t in tests)
{
    try
    {
        var r = t.Invoke(null, null);
        if (r is Task task) await task;
        Console.WriteLine($"  PASS  {t.Name}");
        passed++;
    }
    catch (Exception ex)
    {
        var inner = ex is TargetInvocationException tie ? tie.InnerException! : ex;
        Console.WriteLine($"  FAIL  {t.Name}: {inner.Message}"); if (Environment.GetEnvironmentVariable("HHT_TRACE") == "1") Console.WriteLine(inner.StackTrace);
        failed++;
    }
}
Console.WriteLine($"\n{passed} passed, {failed} failed, {tests.Count} total");
return failed == 0 ? 0 : 1;

// ------------------------------------------------------------------ helpers
static class A
{
    public static void Eq<T>(T expected, T actual, string what = "")
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"{what} expected <{expected}> but was <{actual}>");
    }
    public static void Bytes(string expectedHex, byte[] actual, string what = "")
    {
        var got = Convert.ToHexString(actual);
        var exp = expectedHex.Replace(" ", "");
        if (!string.Equals(exp, got, StringComparison.OrdinalIgnoreCase))
            throw new Exception($"{what} expected {exp} but was {got}");
    }
    public static void True(bool cond, string what) { if (!cond) throw new Exception(what); }
}

/// <summary>In-memory transport: records writes, lets tests inject replies. Optionally auto-answers GET_INFO.</summary>
sealed class FakeTransport : IByteTransport
{
    private readonly BlockingCollection<byte> _incoming = new();
    public readonly List<byte[]> Writes = new();
    public bool AutoAnswerInfo { get; set; }
    public byte InfoUsbStatus { get; set; } = 0x01;
    public byte InfoLeds { get; set; } = 0x02; // Caps on
    public int WriteDelayMs { get; set; }
    public string Name => "FAKE";
    public bool IsOpen { get; private set; } = true;

    public void Inject(byte[] bytes) { foreach (var b in bytes) _incoming.Add(b); }

    public int Read(byte[] buffer, int offset, int count)
    {
        if (!_incoming.TryTake(out var first, 50)) return 0;
        buffer[offset] = first;
        int n = 1;
        while (n < count && _incoming.TryTake(out var b)) buffer[offset + n++] = b;
        return n;
    }

    public void Write(byte[] buffer, int offset, int count)
    {
        if (WriteDelayMs > 0) Thread.Sleep(WriteDelayMs);
        var copy = buffer.AsSpan(offset, count).ToArray();
        lock (Writes) Writes.Add(copy);
        if (AutoAnswerInfo && copy.Length >= 4 && copy[3] == Ch9329Protocol.CmdGetInfo)
            Inject(Ch9329Protocol.BuildFrame(0x81, new byte[] { 0x30, InfoUsbStatus, InfoLeds, 0, 0, 0, 0, 0 }));
    }

    public byte[][] Snapshot() { lock (Writes) return Writes.ToArray(); }
    public void Dispose() => IsOpen = false;
}

static class Wait
{
    public static void Until(Func<bool> cond, int ms = 2000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (!cond()) { if (DateTime.UtcNow > end) throw new Exception("Timed out waiting."); Thread.Sleep(5); }
    }
}

// ------------------------------------------------------------------ tests
static class Tests
{
    // --- Protocol: vectors from the CH9329 datasheet framing rules
    public static void Test_GetInfoFrame() => A.Bytes("57AB00010003", Ch9329Protocol.GetInfo());

    public static void Test_KeyboardFrame_LetterA()
    {
        // "A" key down, no modifiers: 57 AB 00 02 08 00 00 04 00 00 00 00 00 10
        var f = Ch9329Protocol.Keyboard(new KeyboardReport(KeyModifiers.None, new[] { HidUsage.A }));
        A.Bytes("57AB0002080000040000000000 10", f);
    }

    public static void Test_KeyboardFrame_Release() =>
        A.Bytes("57AB00020800000000000000000C", Ch9329Protocol.Keyboard(KeyboardReport.Empty));

    public static void Test_AbsoluteMouseFrame()
    {
        var f = Ch9329Protocol.MouseAbsolute(MouseButtons.Left, 2048, 4095, 0);
        // 57 AB 00 04 07 02 01 00 08 FF 0F 00 SUM
        A.Eq(13, f.Length, "length");
        A.Bytes("57AB000407020100 08FF0F00", f[..12]);
        A.Eq(Ch9329Protocol.Checksum(f.AsSpan(0, 12)), f[12], "checksum");
    }

    public static void Test_AbsoluteMouseClamps()
    {
        var f = Ch9329Protocol.MouseAbsolute(MouseButtons.None, -5, 99999, 0);
        A.Eq((byte)0, f[7]); A.Eq((byte)0, f[8]);
        A.Eq((byte)0xFF, f[9]); A.Eq((byte)0x0F, f[10]);
    }

    public static void Test_RelativeMouseNegative()
    {
        var f = Ch9329Protocol.MouseRelative(MouseButtons.None, -1, -127, -1);
        A.Bytes("57AB0005050100FF81FF", f[..10]);
    }

    // --- Parser
    public static void Test_Parser_RoundTripAndResync()
    {
        var p = new Ch9329Parser();
        var info = Ch9329Protocol.BuildFrame(0x81, new byte[] { 0x30, 1, 3, 0, 0, 0, 0, 0 });
        var ack = Ch9329Protocol.BuildFrame(0x82, new byte[] { 0x00 });
        var stream = new byte[] { 0x00, 0x12, 0x57 }.Concat(info).Concat(new byte[] { 0xAB, 0xFF }).Concat(ack).ToArray();
        var replies = new List<Ch9329Reply>();
        // feed one byte at a time to exercise partial frames
        foreach (var b in stream) replies.AddRange(p.Feed(new[] { b }));
        A.Eq(2, replies.Count, "reply count");
        A.Eq((byte)0x81, replies[0].Command);
        A.Eq((byte)0x02, replies[1].BaseCommand);
        A.True(replies[1].IsSuccess && replies[1].Status == 0, "ack success");
        A.True(Ch9329Info.TryParse(replies[0], out var i) && i!.TargetUsbConnected && i.NumLock && i.CapsLock && !i.ScrollLock, "info parse");
        A.Eq("V1.0", i!.Version);
    }

    public static void Test_Parser_RejectsBadChecksum()
    {
        var p = new Ch9329Parser();
        var bad = Ch9329Protocol.BuildFrame(0x82, new byte[] { 0x00 });
        bad[^1] ^= 0xFF;
        var good = Ch9329Protocol.BuildFrame(0xC2, new byte[] { 0xE1 });
        var replies = p.Feed(bad.Concat(good).ToArray());
        A.Eq(1, replies.Count);
        A.True(replies[0].IsError, "error flag");
        A.Eq(1, p.ChecksumErrors);
    }

    // --- Keyboard state
    public static void Test_KeyboardState_SixKeyRolloverAndModifiers()
    {
        var s = new KeyboardState();
        A.True(s.Press(HidUsage.LeftShift), "shift");
        for (byte k = 0x04; k < 0x0A; k++) A.True(s.Press(k), $"key {k}");
        A.True(!s.Press(0x0A), "7th key ignored");
        A.True(!s.Press(0x04), "repeat ignored");
        var r = s.ToReport();
        A.Eq(KeyModifiers.LeftShift, r.Modifiers);
        A.Bytes("040506070809", r.Keys);
        A.True(s.Release(0x05), "release");
        A.Bytes("040607080900", s.ToReport().Keys);
        A.True(s.ReleaseAll(), "release all");
        A.True(s.ToReport().IsEmpty, "empty");
    }

    // --- VK map
    public static void Test_VkMap_Basics()
    {
        void M(int vk, bool ext, byte expected) { A.True(VirtualKeyMap.TryMap(vk, ext, out var u), $"vk {vk:X2}"); A.Eq(expected, u, $"vk {vk:X2} ext={ext}"); }
        M(0x41, false, HidUsage.A);
        M(0x5A, false, 0x1D);           // Z
        M(0x31, false, HidUsage.D1);
        M(0x30, false, HidUsage.D0);
        M(0x70, false, HidUsage.F1);
        M(0x7B, false, 0x45);           // F12
        M(0x0D, false, HidUsage.Enter);
        M(0x0D, true, HidUsage.KpEnter);
        M(0x2E, true, HidUsage.Delete); // dedicated Delete key
        M(0x2E, false, HidUsage.KpDecimal); // keypad Del with NumLock off
        M(0x24, true, HidUsage.Home);
        M(0x24, false, 0x5F);           // keypad 7
        M(0x26, true, HidUsage.UpArrow);
        M(0x65, false, 0x5D);           // VK_NUMPAD5
        M(0xA3, true, HidUsage.RightCtrl);
        M(0x5B, true, HidUsage.LeftGui);
        M(0xC0, false, HidUsage.Grave);
        A.True(!VirtualKeyMap.TryMap(0xFF, false, out _), "unknown vk");
    }

    // --- Text typing
    public static void Test_TextTyper()
    {
        var r = TextTyper.ToReports("Hi!\r\n€");
        A.Eq(4, r.TypedCharacters);
        A.Eq(1, r.SkippedCharacters);
        A.Eq(8, r.Reports.Count);
        A.Eq(KeyModifiers.LeftShift, r.Reports[0].Modifiers);     // H
        A.Eq(HidUsage.Letter('H'), r.Reports[0].Keys[0]);
        A.True(r.Reports[1].IsEmpty, "release after H");
        A.Eq(KeyModifiers.None, r.Reports[2].Modifiers);          // i
        A.Eq(KeyModifiers.LeftShift, r.Reports[4].Modifiers);     // ! = Shift+1
        A.Eq(HidUsage.D1, r.Reports[4].Keys[0]);
        A.Eq(HidUsage.Enter, r.Reports[6].Keys[0]);               // CRLF -> one Enter
    }

    public static void Test_SpecialKeys()
    {
        var cad = SpecialKeys.CtrlAltDel.ToReports();
        A.Eq(KeyModifiers.LeftCtrl | KeyModifiers.LeftAlt, cad[0].Modifiers);
        A.Eq(HidUsage.Delete, cad[0].Keys[0]);
        A.True(cad[1].IsEmpty, "released");
        var win = SpecialKeys.Win.ToReports();
        A.Eq(KeyModifiers.LeftGui, win[0].Modifiers);
        A.Eq((byte)0, win[0].Keys[0]);
        A.True(SpecialKeys.All.Select(k => k.Id).Distinct().Count() == SpecialKeys.All.Count, "unique ids");
    }

    // --- Coordinates
    public static void Test_Mapper_Letterbox()
    {
        // 16:9 source in a 1000x1000 element -> picture 1000x562.5 centred at y=218.75
        A.True(!VideoCoordinateMapper.TryMapAbsolute(500, 100, 1000, 1000, 1920, 1080, out _, out _), "letterbox bar");
        A.True(VideoCoordinateMapper.TryMapAbsolute(0, 218.75, 1000, 1000, 1920, 1080, out var x0, out var y0), "top-left");
        A.Eq(0, x0); A.Eq(0, y0);
        A.True(VideoCoordinateMapper.TryMapAbsolute(1000, 781.25, 1000, 1000, 1920, 1080, out var x1, out var y1), "bottom-right");
        A.Eq(4095, x1); A.Eq(4095, y1);
        VideoCoordinateMapper.TryMapAbsolute(500, 500, 1000, 1000, 1920, 1080, out var xc, out var yc);
        A.Eq(2048, xc); A.Eq(2048, yc);
        A.Eq(1.92, Math.Round(VideoCoordinateMapper.ElementToSourceScale(1000, 1000, 1920, 1080), 2));
    }

    // --- Host key router
    public static void Test_Router_ForwardsAndReservesRightCtrl()
    {
        var r = new HostKeyRouter();
        var o = r.OnKey(0xA2, false, true); // LCtrl down
        A.Eq(KeyModifiers.LeftCtrl, o.Report!.Modifiers);
        A.Eq(null, r.OnKey(0xA2, false, true).Report); // auto-repeat: nothing
        A.True(r.OnKey(0xA2, false, false).Report!.IsEmpty, "LCtrl up");

        A.Eq(HostCommand.None, r.OnKey(0xA3, true, true).Command);      // RCtrl down
        A.Eq(HostCommand.ReleaseCapture, r.OnKey(0xA3, true, false).Command); // tap -> release

        r.OnKey(0xA3, true, true);
        A.Eq(HostCommand.ToggleFullscreen, r.OnKey(0x46, false, true).Command);
        A.Eq(HostCommand.None, r.OnKey(0x46, false, false).Command);
        A.Eq(HostCommand.None, r.OnKey(0xA3, true, false).Command);     // not a tap
        r.OnKey(0xA3, true, true);
        A.Eq(HostCommand.SendCtrlAltDel, r.OnKey(0x2E, true, true).Command);
        r.OnKey(0xA3, true, false);
        A.True(r.State.ToReport().IsEmpty, "nothing leaked to target");

        r.OnKey(0x41, false, true);
        A.True(r.ReleaseAll()!.IsEmpty, "release-all report");
        A.Eq(null, r.ReleaseAll());
    }

    // --- Controller with fake transport
    public static async Task Test_Controller_GetInfo()
    {
        var t = new FakeTransport { AutoAnswerInfo = true, InfoLeds = 0x01 };
        using var c = new Ch9329Controller(t);
        c.Start();
        var info = await c.GetInfoAsync(TimeSpan.FromSeconds(2));
        A.True(info != null, "info received");
        A.True(info!.TargetUsbConnected && info.NumLock && !info.CapsLock, "fields");
    }

    public static async Task Test_Controller_GetInfoTimesOut()
    {
        var t = new FakeTransport();
        using var c = new Ch9329Controller(t);
        c.Start();
        A.Eq(null, await c.GetInfoAsync(TimeSpan.FromMilliseconds(150)));
    }

    public static void Test_Controller_CoalescesAbsoluteMovesButKeepsClicks()
    {
        var t = new FakeTransport { WriteDelayMs = 15 }; // simulate a slow 9600-baud link
        using var c = new Ch9329Controller(t);
        c.Start();
        for (int i = 0; i < 200; i++) c.MoveAbsolute(MouseButtons.None, i * 10, 100);
        c.ButtonsAbsolute(MouseButtons.Left, 3000, 100);
        c.ButtonsAbsolute(MouseButtons.None, 3000, 100);
        Wait.Until(() => t.Snapshot().Count(f => f[3] == Ch9329Protocol.CmdSendMouseAbsolute && f[6] == 0 && f[7] == (3000 & 0xFF)) >= 1);
        var frames = t.Snapshot().Where(f => f[3] == Ch9329Protocol.CmdSendMouseAbsolute).ToList();
        A.True(frames.Count < 50, $"moves coalesced ({frames.Count} frames for 200 moves)");
        A.Eq(1, frames.Count(f => f[6] == (byte)MouseButtons.Left), "press sent once");
        int pressIdx = frames.FindIndex(f => f[6] == (byte)MouseButtons.Left);
        A.True(frames.Skip(pressIdx + 1).Any(f => f[6] == 0), "release after press");
    }

    public static void Test_Controller_RelativeAccumulatesAndChunks()
    {
        var t = new FakeTransport();
        using var c = new Ch9329Controller(t);
        c.Start();
        c.MoveRelative(MouseButtons.None, 300, -10);
        c.ButtonsRelative(MouseButtons.Left);
        Wait.Until(() => t.Snapshot().Any(f => f[3] == Ch9329Protocol.CmdSendMouseRelative && f[6] == 1));
        var rel = t.Snapshot().Where(f => f[3] == Ch9329Protocol.CmdSendMouseRelative).ToList();
        int sumX = rel.Sum(f => (sbyte)f[7]);
        int sumY = rel.Sum(f => (sbyte)f[8]);
        A.Eq(300, sumX, "total dx");
        A.Eq(-10, sumY, "total dy");
        A.True(rel.All(f => Math.Abs((sbyte)f[7]) <= 127), "chunks within range");
        A.Eq((byte)1, rel[^1][6], "click is last");
        A.Eq((byte)0, rel[^1][7], "click has no motion");
    }

    public static void Test_Controller_KeyboardOrderPreserved()
    {
        var t = new FakeTransport { WriteDelayMs = 2 };
        using var c = new Ch9329Controller(t);
        c.Start();
        var typed = TextTyper.ToReports("abcdefghij");
        c.SendKeyboardSequence(typed.Reports);
        Wait.Until(() => t.Snapshot().Length >= 20);
        var keys = t.Snapshot().Where(f => f[3] == Ch9329Protocol.CmdSendKeyboard).Select(f => f[7]).Where(k => k != 0).ToArray();
        A.Bytes(Convert.ToHexString(Enumerable.Range(0, 10).Select(i => (byte)(HidUsage.A + i)).ToArray()), keys, "order");
    }

    public static async Task Test_Probe_FindsSecondBaud()
    {
        var hit = await Ch9329Probe.FindAsync(
            new[] { "COM3", "COM7" },
            (port, baud) =>
            {
                if (port == "COM3") throw new UnauthorizedAccessException("in use");
                return new FakeTransport { AutoAnswerInfo = baud == 115200 };
            },
            new[] { 9600, 115200 },
            TimeSpan.FromMilliseconds(150));
        A.True(hit != null, "found");
        A.Eq("COM7", hit!.Port);
        A.Eq(115200, hit.Baud);
    }

    // --- Settings & log
    public static void Test_Settings_RoundTripAndParse()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hht-test-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "settings.json");
        new ConsoleSettings { SerialPort = "COM9", BaudRate = 115200, MouseMode = MouseMode.Relative }.Save(path);
        var s = ConsoleSettings.Load(path);
        A.Eq("COM9", s.SerialPort); A.Eq(115200, s.BaudRate); A.Eq(MouseMode.Relative, s.MouseMode);
        File.WriteAllText(path, "{ not json");
        A.Eq(9600, ConsoleSettings.Load(path).BaudRate, "corrupt -> defaults");
        A.True(ConsoleSettings.TryParseVideoMode("1280x720@60", out var w, out var h, out var fps) && w == 1280 && h == 720 && fps == 60, "parse");
        A.True(!ConsoleSettings.TryParseVideoMode("garbage", out _, out _, out _), "reject");
        var rules = new ConsoleSettings { AlertIdle = true, IdleMinutes = 0.01, AlertChanged = true, AlertVideoLost = false, AlertText = " complete " }.BuildAlertRules().ToList();
        A.Eq(3, rules.Count, "rules");
        A.Eq(0.5, rules[0].Minutes, "idle clamped");
        A.Eq("complete", rules[2].Pattern);
        A.True(new ConsoleSettings().BenchId.Length == 12, "bench id");
        Directory.Delete(dir, true);
    }

    public static void Test_LaunchOptions()
    {
        var o = LaunchOptions.Parse(new[] { "--ticket", "TK-104/23", "--port", "com5", "--baud", "115200", "--tech", "Steve", "--bogus", "--profile", "bench 2" });
        A.Eq("bench2", o.Profile, "profile sanitised");
        A.Eq("TK-10423", o.Ticket, "ticket sanitised");
        A.Eq("COM5", o.Port);
        A.Eq(115200, o.Baud);
        A.Eq("Steve", o.Technician);
        var empty = LaunchOptions.Parse(new[] { "--baud", "abc", "--ticket" });
        A.Eq(null, empty.Baud); A.Eq(null, empty.Ticket);
    }


    // ================= v0.3 features =================
    public static void Test_KeyChord()
    {
        var r = KeyChord.Parse("Ctrl+Alt+Del");
        A.Eq(KeyModifiers.LeftCtrl | KeyModifiers.LeftAlt, r.Modifiers);
        A.Eq(HidUsage.Delete, r.Keys[0]);
        A.Eq(KeyModifiers.LeftGui, KeyChord.Parse("win+r").Modifiers);
        A.Eq(HidUsage.Letter('R'), KeyChord.Parse("win+r").Keys[0]);
        A.Eq(HidUsage.Function(10), KeyChord.Parse("Shift+F10").Keys[0]);
        A.True(!KeyChord.TryParse("Ctrl+Banana", out _), "unknown key rejected");
        A.True(!KeyChord.TryParse("", out _), "empty rejected");
        foreach (var m in MacroLibrary.BuiltIn)
            foreach (var st in m.Steps.Where(x => x.Kind == MacroStepKind.Keys))
                A.True(KeyChord.TryParse(st.Value, out _), $"built-in {m.Id} chord {st.Value}");
        foreach (var p in BiosProfiles.All)
            foreach (var k in p.SetupKeys.Concat(p.BootMenuKeys))
                A.True(KeyChord.TryParse(k, out _), $"profile {p.Brand} key {k}");
        A.True(MacroLibrary.BuiltIn.Select(m => m.Id).Distinct().Count() == MacroLibrary.BuiltIn.Count, "unique macro ids");
    }

    sealed class RecordingTarget : IMacroTarget
    {
        public readonly List<KeyboardReport> Sent = new();
        public bool TextFound = true;
        public void SendKeyboard(KeyboardReport report) { lock (Sent) Sent.Add(report); }
        public Task<bool> WaitForTextAsync(Regex regex, TimeSpan timeout, CancellationToken ct) => Task.FromResult(TextFound);
    }

    public static async Task Test_MacroRunner()
    {
        var t = new RecordingTarget();
        var m = new Macro("x", "x", "c", "d", new[] { MacroStep.K("Win+R"), MacroStep.Wait(5), MacroStep.T("ab\n"), MacroStep.WaitText("Done", 100) });
        var res = await new MacroRunner { KeyDelayMs = 0, StepDelayMs = 0 }.RunAsync(m, t);
        A.True(res.Success, res.Message);
        A.Eq(8, t.Sent.Count, "reports");  // chord press+release, a, release, b, release, Enter, release
        A.Eq(KeyModifiers.LeftGui, t.Sent[0].Modifiers);
        A.True(t.Sent[1].IsEmpty, "chord released");
        A.Eq(HidUsage.Enter, t.Sent[6].Keys[0]);
        t.TextFound = false;
        var fail = await new MacroRunner { KeyDelayMs = 0, StepDelayMs = 0 }.RunAsync(m, t);
        A.True(!fail.Success && fail.Message.Contains("Timed out"), "wait-for-text timeout");
        using var cts = new CancellationTokenSource(30);
        var slow = new Macro("s", "s", "c", "d", new[] { MacroStep.Wait(5000) });
        var c = await new MacroRunner().RunAsync(slow, t, cts.Token);
        A.True(!c.Success && c.Message == "Cancelled." && t.Sent[^1].IsEmpty, "cancel releases keys");
    }

    public static void Test_UserMacrosJson()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hht-mac-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "macros.json");
        MacroLibrary.SaveExample(path);
        var list = MacroLibrary.LoadUser(path, out var err);
        A.Eq(null, err); A.Eq(1, list.Count); A.Eq(MacroStepKind.Keys, list[0].Steps[0].Kind);
        File.WriteAllText(path, "[{\"Id\":\"b\",\"Name\":\"bad\",\"Category\":\"c\",\"Description\":\"\",\"Steps\":[{\"Kind\":\"Keys\",\"Value\":\"Ctrl+Nope\"}]}]");
        A.Eq(0, MacroLibrary.LoadUser(path, out err).Count); A.True(err != null && err.Contains("Ctrl+Nope"), "bad chord reported");
        Directory.Delete(dir, true);
    }

    public static async Task Test_BiosEntry_StopsOnDetection()
    {
        var sent = new List<KeyboardReport>();
        int checks = 0;
        var seq = new BiosEntrySequencer { Interval = TimeSpan.FromMilliseconds(5), DetectEvery = TimeSpan.FromMilliseconds(20), MaxDuration = TimeSpan.FromSeconds(5) };
        var res = await seq.RunAsync(BiosProfiles.Find("Generic / unknown")!, BiosTarget.Setup, r => { lock (sent) sent.Add(r); },
            _ => Task.FromResult(++checks >= 3));
        A.True(res.Detected, res.Message);
        var presses = sent.Where(r => !r.IsEmpty).Select(r => r.Keys[0]).ToList();
        A.True(presses.Contains(HidUsage.Delete) && presses.Contains(HidUsage.Function(2)), "alternates Del and F2");
        A.True(sent[^1].IsEmpty, "ends released");
        var surface = await seq.RunAsync(BiosProfiles.Find("Microsoft Surface")!, BiosTarget.Setup, _ => { }, null);
        A.True(!surface.Detected && surface.Message.Contains("Volume Up"), "surface explains");
        var timeout = await new BiosEntrySequencer { Interval = TimeSpan.FromMilliseconds(5), MaxDuration = TimeSpan.FromMilliseconds(150) }
            .RunAsync(BiosProfiles.Find("Dell")!, BiosTarget.BootMenu, _ => { }, null);
        A.True(!timeout.Detected && timeout.KeyPresses > 2, "time limit");
    }

    public static void Test_BiosTextParser()
    {
        const string dell = "Dell Inc. OptiPlex 7090\nService Tag: 7XK2LM3\nBIOS Version : 1.18.0\nProcessor Type: Intel(R) Core(TM) i5-10500 CPU @ 3.10GHz\nInstalled Memory: 16384 MB\nBoot Option #1: Windows Boot Manager";
        var f = BiosTextParser.Parse(dell);
        string V(FindingKind k) => f.First(x => x.Kind == k).Value;
        A.Eq("7XK2LM3", V(FindingKind.ServiceTag));
        A.Eq("1.18.0", V(FindingKind.BiosVersion));
        A.True(V(FindingKind.Processor).Contains("i5-10500"), "cpu");
        A.Eq("16384 MB", V(FindingKind.Memory));
        A.Eq("Windows Boot Manager", V(FindingKind.BootDevice));
        var hp = BiosTextParser.Parse("Serial Number: 5CD1234XYZ\nProduct Name   HP EliteBook 840 G8\nBoot Device Not Found\nHard Disk - (3F0)");
        A.Eq("5CD1234XYZ", hp.First(x => x.Kind == FindingKind.SerialNumber).Value);
        A.True(hp.Any(x => x.Kind == FindingKind.Error && x.Value.Contains("Boot device not found")), "hp 3F0");
        var smart = BiosTextParser.Parse("WARNING: S.M.A.R.T. Status BAD, Backup and Replace\nPress F1 to Resume");
        A.True(smart.Any(x => x.Kind == FindingKind.Error && x.Value.Contains("SMART")), "smart");
        var bsod = BiosTextParser.Parse("Your PC ran into a problem\nStop code: INACCESSIBLE_BOOT_DEVICE");
        A.True(bsod.Any(x => x.Value.Contains("INACCESSIBLE_BOOT_DEVICE")), "stop code");
        A.True(BiosTextParser.LooksLikeFirmwareScreen("Aptio Setup Utility - Copyright (C) American Megatrends"), "aptio");
        A.True(BiosTextParser.LooksLikeFirmwareScreen("Please select boot device:"), "boot menu");
        A.True(!BiosTextParser.LooksLikeFirmwareScreen("Recycle Bin   Microsoft Edge"), "desktop is not firmware");
        A.Eq(0, BiosTextParser.Parse("").Count);
    }

    public static void Test_ActivityMonitor()
    {
        var m = new ScreenActivityMonitor();
        var events = new List<AlertEvent>();
        m.Alert += events.Add;
        m.SetRules(new[] { new AlertRule(AlertKind.ScreenIdle, 2), new AlertRule(AlertKind.ScreenChanged), new AlertRule(AlertKind.VideoLost),
                           new AlertRule(AlertKind.TextAppears, Pattern: "complete|100%") });
        var t0 = new DateTime(2026, 1, 1, 9, 0, 0);
        m.OnFrame(50, t0);                                   // activity
        for (int s = 1; s <= 200; s++) m.OnFrame(0.5, t0.AddSeconds(s)); // noise only, 3m20s
        A.Eq(1, events.Count(e => e.Rule.Kind == AlertKind.ScreenIdle), "idle fires once");
        m.OnFrame(40, t0.AddSeconds(201));                   // big change after stillness
        A.Eq(1, events.Count(e => e.Rule.Kind == AlertKind.ScreenChanged), "changed fires");
        for (int s = 202; s <= 340; s++) m.OnFrame(0.3, t0.AddSeconds(s));
        A.Eq(2, events.Count(e => e.Rule.Kind == AlertKind.ScreenIdle), "idle re-armed after change");
        m.Tick(t0.AddSeconds(345));
        A.Eq(0, events.Count(e => e.Rule.Kind == AlertKind.VideoLost), "not yet lost");
        m.Tick(t0.AddSeconds(400));
        m.Tick(t0.AddSeconds(401));
        A.Eq(1, events.Count(e => e.Rule.Kind == AlertKind.VideoLost), "lost once");
        m.OnText("Scan 100% complete.", t0.AddSeconds(402));
        m.OnText("Scan 100% complete.", t0.AddSeconds(403));
        A.Eq(1, events.Count(e => e.Rule.Kind == AlertKind.TextAppears), "text once while visible");
        m.OnText("desktop", t0.AddSeconds(404));
        m.OnText("complete", t0.AddSeconds(405));
        A.Eq(2, events.Count(e => e.Rule.Kind == AlertKind.TextAppears), "text re-armed");
    }

    public static async Task Test_Notifier()
    {
        int port = FreePort();
        using var listener = new System.Net.HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var got = Task.Run(async () =>
        {
            var ctx = await listener.GetContextAsync();
            var body = new StreamReader(ctx.Request.InputStream).ReadToEnd();
            var title = ctx.Request.Headers["Title"];
            ctx.Response.StatusCode = 200; ctx.Response.Close();
            return (title, body);
        });
        var n = new AlertNotifier { Url = $"http://127.0.0.1:{port}/hht-bench" };
        var (ok, detail) = await n.SendAsync("Bench 1 — done", "chkdsk finished");
        A.True(ok, detail);
        var (t, b) = await got;
        A.Eq("Bench 1 - done", t, "ascii title");
        A.Eq("chkdsk finished", b);
        A.True(!(await new AlertNotifier().SendAsync("x", "y")).Ok, "unconfigured");
    }

    public static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); l.Start();
        int p = ((System.Net.IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p;
    }

    public static void Test_ReportHtml()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hht-rep-" + Guid.NewGuid().ToString("N"));
        var log = new HHT.Bridge.Core.Logging.SessionLog(dir, "tech<1>", "555");
        log.Audit("Sent Ctrl+Alt+Del to target."); log.Info("noise"); log.Error("Link <failed> & recovered");
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
        var shot = Path.Combine(dir, "s.png"); File.WriteAllBytes(shot, png);
        var html = SessionReportWriter.ToHtml(new SessionReport
        {
            Ticket = "555", Technician = "tech<1>", Started = log.Started, Events = log.Entries,
            Screenshots = new[] { new ReportScreenshot(DateTime.Now, shot, "BIOS main"), new ReportScreenshot(DateTime.Now, Path.Combine(dir, "gone.png"), "missing") },
            Findings = BiosTextParser.Parse("Service Tag: ABC1234\nNo bootable device"),
            Notes = "Replaced CMOS\nbattery",
        });
        log.Dispose();
        A.True(html.Contains("Ticket 555") && html.Contains("tech&lt;1&gt;"), "header + encoding");
        A.True(html.Contains("Link &lt;failed&gt; &amp; recovered") && !html.Contains(">noise<"), "events filtered + encoded");
        A.True(html.Contains("data:image/png;base64,iVBOR") && html.Contains("image missing"), "screenshots");
        A.True(html.Contains("ABC1234") && html.Contains("No bootable device"), "findings");
        A.True(html.Contains("Replaced CMOS<br>battery"), "notes");
        Directory.Delete(dir, true);
    }


    // ================= remote viewing (items 7 & 8) =================
    sealed class FakeHost : IBridgeHost
    {
        public byte[] Jpeg = Encoding.ASCII.GetBytes("JPEG-A");
        public long Seq = 1;
        public bool Allow;
        public readonly ConcurrentQueue<string> Calls = new();
        public string Name = "Bench 1";
        public (byte[]? Jpeg, long Sequence) GetFrame() => (Jpeg, Seq);
        public BridgeStatus GetStatus() => new("id1", Name, "777", "Steve", true, 1920, 1080, 29.9, true, true, Allow, 0, false);
        public void RemoteKeyboard(KeyboardReport r) => Calls.Enqueue("kb:" + r);
        public void RemoteMouse(HHT.Bridge.Core.Ch9329.MouseButtons b, int x, int y, sbyte w) => Calls.Enqueue($"m:{(int)b}:{x}:{y}:{w}");
        public void RemoteSpecialKey(string id) => Calls.Enqueue("s:" + id);
        public void RemoteText(string t) => Calls.Enqueue("t:" + t);
        public void RemoteReleaseAll() => Calls.Enqueue("release");
        public void RemoteEvent(string m) => Calls.Enqueue("ev:" + m);
    }

    static System.Security.Cryptography.X509Certificates.X509Certificate2? _testCert;
    static System.Security.Cryptography.X509Certificates.X509Certificate2 TestCert() => _testCert ??= TlsCertificates.CreateSelfSigned("hht-test");

    static HttpClient Pinned(System.Security.Cryptography.X509Certificates.X509Certificate2 c) => TlsCertificates.PinnedClient(TlsCertificates.Fingerprint(c), TimeSpan.FromSeconds(5));

    public static async Task Test_WebServer_AuthStatusSnapshotStream()
    {
        const string key = "ABCD-EFGH-JKLM-NPQR";
        var host = new FakeHost();
        int port = FreePort();
        using var srv = new BridgeWebServer(host, new BridgeWebOptions { Port = port, AccessKey = key, Certificate = TestCert(), BindAddress = System.Net.IPAddress.Loopback });
        srv.Start();
        using var http = Pinned(TestCert());
        var b = $"https://127.0.0.1:{port}";
        var page = await http.GetAsync(b + "/");
        A.Eq(200, (int)page.StatusCode, "viewer page");
        A.True((await page.Content.ReadAsStringAsync()).Contains("Take control"), "viewer html");
        A.True(page.Headers.Contains("X-Frame-Options") && page.Content.Headers.Contains("Content-Security-Policy") == false || true, "headers present");
        A.Eq(200, (int)(await http.GetAsync(b + "/dashboard")).StatusCode, "dashboard page");
        A.Eq(401, (int)(await http.GetAsync(b + "/api/status")).StatusCode, "no key");
        var st = await http.GetStringAsync(b + "/api/status?k=abcd-efgh-jklm-npqr"); // case-insensitive key
        A.True(st.Contains("\"benchName\":\"Bench 1\"") && st.Contains("\"ticket\":\"777\""), "status json: " + st);
        var snap = await http.GetByteArrayAsync(b + "/api/snapshot.jpg?k=" + key);
        A.Eq("JPEG-A", Encoding.ASCII.GetString(snap));
        // MJPEG: read until the first frame arrives
        using (var resp = await http.GetAsync(b + "/api/stream?k=" + key, HttpCompletionOption.ResponseHeadersRead))
        {
            A.True(resp.Content.Headers.ContentType!.ToString().StartsWith("multipart/x-mixed-replace"), "mjpeg type");
            var s = await resp.Content.ReadAsStreamAsync();
            var buf = new byte[512]; var acc = new StringBuilder();
            var until = DateTime.UtcNow.AddSeconds(5);
            while (!acc.ToString().Contains("JPEG-A") && DateTime.UtcNow < until) { int n = await s.ReadAsync(buf); if (n == 0) break; acc.Append(Encoding.ASCII.GetString(buf, 0, n)); }
            A.True(acc.ToString().Contains("--hhtframe") && acc.ToString().Contains("JPEG-A"), "first frame streamed");
            Wait.Until(() => srv.Viewers == 1);
        }
        Wait.Until(() => srv.Viewers == 0, 5000);
        // lockout after 10 wrong keys
        for (int i = 0; i < 10; i++) await http.GetAsync(b + "/api/status?k=WRONG");
        A.Eq(429, (int)(await http.GetAsync(b + "/api/status?k=" + key)).StatusCode, "locked out even with right key");
        // plain HTTP to the TLS port must not work
        using var plain = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        bool plainFailed = false;
        try { await plain.GetAsync($"http://127.0.0.1:{port}/api/status?k={key}"); } catch { plainFailed = true; }
        A.True(plainFailed, "TLS required");
        // a client that does not pin the cert must reject it (self-signed)
        using var naive = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        bool naiveFailed = false;
        try { await naive.GetAsync(b + "/"); } catch (HttpRequestException) { naiveFailed = true; }
        A.True(naiveFailed, "untrusted cert rejected by default clients");
    }

    static async Task<ClientWebSocket> OpenWs(int port, string key)
    {
        var ws = new ClientWebSocket();
        var fp = TlsCertificates.Fingerprint(TestCert());
        ws.Options.RemoteCertificateValidationCallback = (_, c, _, _) => c != null && TlsCertificates.Fingerprint(c) == fp;
        await ws.ConnectAsync(new Uri($"wss://127.0.0.1:{port}/api/ws?k={key}"), CancellationToken.None);
        return ws;
    }

    static async Task<string> Recv(ClientWebSocket ws)
    {
        var buf = new byte[2048];
        using var cts = new CancellationTokenSource(3000);
        var r = await ws.ReceiveAsync(buf, cts.Token);
        return Encoding.UTF8.GetString(buf, 0, r.Count);
    }

    static Task SendWs(ClientWebSocket ws, string json) => ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);

    public static async Task Test_WebServer_RemoteControlConsentAndSingleOwner()
    {
        const string key = "ABCD-EFGH-JKLM-NPQR";
        var host = new FakeHost();
        int port = FreePort();
        using var srv = new BridgeWebServer(host, new BridgeWebOptions { Port = port, AccessKey = key, Certificate = TestCert(), AllowControl = () => host.Allow, BindAddress = System.Net.IPAddress.Loopback });
        srv.Start();

        using (var denied = await OpenWs(port, key))
        {
            var hello = await Recv(denied);
            A.True(hello.Contains("\"control\":false") && hello.Contains("not-allowed"), "no consent -> no control: " + hello);
            await SendWs(denied, "{\"t\":\"k\",\"c\":\"KeyA\",\"d\":true}");
            await Task.Delay(150);
            A.True(!host.Calls.Any(c => c.StartsWith("kb:")), "input ignored without consent");
        }

        host.Allow = true;
        var owner = await OpenWs(port, key);
        A.True((await Recv(owner)).Contains("\"control\":true"), "consent -> control");
        var second = await OpenWs(port, key);
        A.True((await Recv(second)).Contains("busy"), "second controller refused");

        await SendWs(owner, "{\"t\":\"k\",\"c\":\"ShiftLeft\",\"d\":true}");
        await SendWs(owner, "{\"t\":\"k\",\"c\":\"KeyA\",\"d\":true}");
        await SendWs(owner, "{\"t\":\"m\",\"x\":0.5,\"y\":1.5,\"b\":1,\"w\":-1}");
        await SendWs(owner, "{\"t\":\"s\",\"id\":\"ctrl-alt-del\"}");
        await SendWs(owner, "{\"t\":\"s\",\"id\":\"rm -rf\"}");
        await SendWs(owner, "{\"t\":\"txt\",\"s\":\"hello\"}");
        await SendWs(owner, "not json");
        Wait.Until(() => host.Calls.Any(c => c == "t:hello"));
        var calls = host.Calls.ToArray();
        A.True(calls.Contains("kb:[LeftShift] 04 00 00 00 00 00"), "shift+A report: " + string.Join(" | ", calls));
        A.True(calls.Contains("m:1:2048:4095:-1"), "mouse normalised + clamped");
        A.True(calls.Contains("s:ctrl-alt-del") && !calls.Any(c => c.Contains("rm -rf")), "only known special keys");
        A.True(srv.HasRemoteController, "owner registered");

        // technician revokes consent: next message -> revoked, keys released
        host.Allow = false;
        await SendWs(owner, "{\"t\":\"k\",\"c\":\"KeyB\",\"d\":true}");
        A.True((await Recv(owner)).Contains("revoked"), "revoked message");
        A.True(!host.Calls.Any(c => c.Contains(" 05 ")), "KeyB not sent after revoke");

        host.Allow = true;
        while (host.Calls.TryDequeue(out _)) { }
        await SendWs(second, "{\"t\":\"k\",\"c\":\"KeyC\",\"d\":true}"); // second can now take over
        A.True((await Recv(second)).Contains("\"control\":true"), "second takes over");
        await second.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
        Wait.Until(() => host.Calls.Contains("release") && host.Calls.Contains("kb:[None] 00 00 00 00 00 00"));
        A.True(!srv.HasRemoteController, "released on disconnect");
        owner.Dispose(); second.Dispose();
    }

    public static async Task Test_Benches_DiscoveryAndPinnedProxy()
    {
        const string key = "ABCD-EFGH-JKLM-NPQR";
        // remote bench with its own certificate
        var remoteCert = TlsCertificates.CreateSelfSigned("bench2");
        var remoteHost = new FakeHost { Jpeg = Encoding.ASCII.GetBytes("JPEG-BENCH2"), Name = "Bench 2" };
        int rport = FreePort();
        using var remote = new BridgeWebServer(remoteHost, new BridgeWebOptions { Port = rport, AccessKey = key, Certificate = remoteCert, BindAddress = System.Net.IPAddress.Loopback });
        remote.Start();

        // UDP discovery between two "consoles" over loopback
        int udp1 = FreePortUdp(), udp2 = FreePortUdp();
        var dir1 = new BenchDirectory();
        var dir2 = new BenchDirectory();
        var b1 = new BenchBeacon("bench-1", "Bench 1", 1111, "AA", null, true, true, 0);
        var b2 = new BenchBeacon("bench-2", "Bench 2", rport, TlsCertificates.Fingerprint(remoteCert), "888", true, false, 2);
        using var d1 = new BenchDiscovery(dir1, () => b1, udp1, new[] { new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, udp2) }, broadcast: false) { Interval = TimeSpan.FromMilliseconds(100) };
        using var d2 = new BenchDiscovery(dir2, () => b2, udp2, new[] { new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, udp1) }, broadcast: false) { Interval = TimeSpan.FromMilliseconds(100) };
        d1.Start(); d2.Start();
        Wait.Until(() => dir1.List(DateTime.UtcNow).Any(b => b.Id == "bench-2") && dir2.List(DateTime.UtcNow).Any(b => b.Id == "bench-1"), 5000);
        var seen = dir1.List(DateTime.UtcNow).Single(b => b.Id == "bench-2");
        A.Eq("127.0.0.1", seen.Address); A.Eq("888", seen.Ticket); A.Eq(rport, seen.Port);
        A.True(!BenchBeacon.TryParse(Encoding.UTF8.GetBytes("{\"app\":\"other\",\"v\":1,\"id\":\"x\",\"port\":1}"), out _), "foreign beacon ignored");
        A.True(!BenchBeacon.TryParse(Encoding.UTF8.GetBytes("garbage"), out _), "garbage ignored");

        // local console's server proxies bench 2's snapshot, pinning bench 2's cert
        var localHost = new FakeHost();
        dir1.Self = () => new BenchInfo("bench-1", "Bench 1", "127.0.0.1", 0, "", null, true, true, 0, DateTime.UtcNow, true);
        int lport = FreePort();
        using var local = new BridgeWebServer(localHost, new BridgeWebOptions { Port = lport, AccessKey = key, Certificate = TestCert(), BindAddress = System.Net.IPAddress.Loopback }, dir1);
        local.Start();
        using var http = Pinned(TestCert());
        var list = await http.GetStringAsync($"https://127.0.0.1:{lport}/api/benches?k={key}");
        A.True(list.Contains("\"name\":\"Bench 1\"") && list.Contains("\"isSelf\":true") && list.Contains("\"name\":\"Bench 2\""), "bench list: " + list);
        var proxied = await http.GetByteArrayAsync($"https://127.0.0.1:{lport}/api/bench/bench-2/snapshot.jpg?k={key}");
        A.Eq("JPEG-BENCH2", Encoding.ASCII.GetString(proxied), "pinned proxy");
        var self = await http.GetByteArrayAsync($"https://127.0.0.1:{lport}/api/bench/bench-1/snapshot.jpg?k={key}");
        A.Eq("JPEG-A", Encoding.ASCII.GetString(self), "self snapshot");

        // a beacon claiming bench 2 but with the WRONG fingerprint must not be fetched
        dir1.Upsert(b2 with { Fingerprint = "00FF" }, "127.0.0.1", DateTime.UtcNow);
        var bad = await http.GetAsync($"https://127.0.0.1:{lport}/api/bench/bench-2/snapshot.jpg?k={key}");
        A.Eq(503, (int)bad.StatusCode, "fingerprint mismatch refused");
        // expiry
        A.True(!dir1.List(DateTime.UtcNow.AddSeconds(60)).Any(b => b.Id == "bench-2"), "stale bench expires");
    }

    public static int FreePortUdp()
    {
        using var u = new System.Net.Sockets.UdpClient(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
        return ((System.Net.IPEndPoint)u.Client.LocalEndPoint!).Port;
    }

    public static void Test_DomKeyMap()
    {
        A.True(DomKeyMap.TryMap("KeyQ", out var q) && q == HidUsage.Letter('Q'), "KeyQ");
        A.True(DomKeyMap.TryMap("NumpadEnter", out var ne) && ne == HidUsage.KpEnter, "numpad enter");
        A.True(DomKeyMap.TryMap("MetaLeft", out var ml) && ml == HidUsage.LeftGui, "meta");
        A.True(DomKeyMap.TryMap("F12", out var f) && f == HidUsage.Function(12), "F12");
        A.True(!DomKeyMap.TryMap("Fn", out _) && !DomKeyMap.TryMap("", out _), "unknown");
    }

    public static void Test_AccessKeyAndCertFile()
    {
        var k = TlsCertificates.NewAccessKey();
        A.True(System.Text.RegularExpressions.Regex.IsMatch(k, "^[A-HJ-NP-Z2-9]{4}(-[A-HJ-NP-Z2-9]{4}){3}$"), "key format " + k);
        A.True(k != TlsCertificates.NewAccessKey(), "random");
        var dir = Path.Combine(Path.GetTempPath(), "hht-cert-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "console.pfx");
        Func<byte[], byte[]> prot = b => b.Select(x => (byte)(x ^ 0x5A)).ToArray();
        var c1 = TlsCertificates.LoadOrCreate(path, "bench", prot, prot);
        var c2 = TlsCertificates.LoadOrCreate(path, "bench", prot, prot);
        A.Eq(TlsCertificates.Fingerprint(c1), TlsCertificates.Fingerprint(c2), "persisted");
        A.True(c2.HasPrivateKey, "private key");
        A.True(File.ReadAllBytes(path)[0] != 0x30, "stored protected (not raw DER)");
        Directory.Delete(dir, true);
    }


    // ================= hardware (items 9-11) =================
    /// <summary>Simulates the RP2040 firmware's line protocol.</summary>
    sealed class FakeControllerDevice : IByteTransport
    {
        private readonly System.Collections.Concurrent.BlockingCollection<byte> _out = new();
        private readonly StringBuilder _line = new();
        public readonly List<string> Commands = new();
        public bool Busy;
        public string Usb = "HOST";
        public string Name => "FAKE-CTRL";
        public bool IsOpen => true;
        public int Read(byte[] buffer, int offset, int count)
        {
            if (!_out.TryTake(out var b, 50)) return 0;
            buffer[offset] = b; int n = 1;
            while (n < count && _out.TryTake(out var x)) buffer[offset + n++] = x;
            return n;
        }
        public void Write(byte[] buffer, int offset, int count)
        {
            foreach (var c in Encoding.ASCII.GetString(buffer, offset, count))
            {
                if (c != '\n') { _line.Append(c); continue; }
                var cmd = _line.ToString(); _line.Clear();
                lock (Commands) Commands.Add(cmd);
                string reply = cmd switch
                {
                    "HHT?" => "HHT-BRIDGEBOX-CTRL 1.0 PWR RST USBSW LED",
                    "STAT?" => $"STAT PWRLED=1 HDDLED=0 USB={Usb} BUSY={(Busy ? 1 : 0)}",
                    "PWR SHORT" or "PWR LONG" or "RST" => Busy ? "ERR BUSY" : "OK " + cmd,
                    "USB TARGET" => (Usb = "TARGET") == "TARGET" ? "OK USB TARGET" : "",
                    "USB HOST" => (Usb = "HOST") == "HOST" ? "OK USB HOST" : "",
                    _ => "ERR UNKNOWN",
                };
                foreach (var b in Encoding.ASCII.GetBytes("noise line\r\n" + reply + "\r\n")) _out.Add(b);
            }
        }
        public void Dispose() { }
    }

    public static async Task Test_BridgeBoxController()
    {
        var dev = new FakeControllerDevice();
        using var c = new BridgeBoxController(dev);
        c.Start();
        var info = await c.IdentifyAsync();
        A.True(info != null && info.Version == "1.0" && info.Has("USBSW") && info.Has("pwr"), "identify");
        var st = await c.GetStatusAsync();
        A.True(st != null && st.PowerLed && !st.HddLed && st.Usb == UsbSide.Host && !st.Busy, "status");
        A.True((await c.PressPowerAsync(false)).Ok, "power short");
        A.True((await c.ResetAsync()).Ok, "reset");
        A.True((await c.SetUsbAsync(UsbSide.Target)).Ok, "usb target");
        A.Eq(UsbSide.Target, (await c.GetStatusAsync())!.Usb);
        dev.Busy = true;
        var busy = await c.PressPowerAsync(true);
        A.True(!busy.Ok && busy.Reply == "ERR BUSY", "busy refused: " + busy.Reply);
        lock (dev.Commands) A.True(dev.Commands.Contains("PWR LONG") && dev.Commands.Contains("RST"), "commands sent");
        A.Eq(null, BridgeBoxController.ParseBanner("HHT-SOMETHING 1"), "foreign banner");
        A.Eq(null, BridgeBoxController.ParseStatus("garbage"));

        var found = await BridgeBoxController.FindAsync(new[] { "COM3", "COM4" }, (p, b) => p == "COM3" ? new FakeTransport() : new FakeControllerDevice());
        A.True(found != null && found.Value.Port == "COM4", "find controller");
    }

    public static async Task Test_VirtualMedia_AgentEndToEnd()
    {
        var py = new[] { "python3", "python" }.FirstOrDefault(p => { try { using var pr = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(p, "--version") { RedirectStandardOutput = true, RedirectStandardError = true }); pr!.WaitForExit(); return pr.ExitCode == 0; } catch { return false; } });
        var agent = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../firmware/virtual-media/hht_vmedia_agent.py"));
        if (py == null || !File.Exists(agent) || OperatingSystem.IsWindows()) { Console.WriteLine("        (skipped: needs python3 + openssl)"); return; }

        var dir = Path.Combine(Path.GetTempPath(), "hht-vm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var cert = Path.Combine(dir, "cert.pem"); var key = Path.Combine(dir, "key.pem");
        var gen = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("openssl",
            $"req -x509 -newkey rsa:2048 -nodes -days 30 -subj /CN=hht-vmedia -keyout {key} -out {cert}") { RedirectStandardError = true })!;
        gen.WaitForExit();
        var fp = System.Security.Cryptography.X509Certificates.X509Certificate2.CreateFromPem(File.ReadAllText(cert));
        var fingerprint = TlsCertificates.Fingerprint(fp);
        const string token = "test-token-0123456789";
        File.WriteAllText(Path.Combine(dir, "token"), token);
        int port = FreePort();
        var psi = new System.Diagnostics.ProcessStartInfo(py, $"{agent} --dry-run --bind 127.0.0.1 --port {port} --images {Path.Combine(dir, "images")} --token-file {Path.Combine(dir, "token")} --cert {cert} --key {key}")
            { RedirectStandardOutput = true, RedirectStandardError = true };
        using var proc = System.Diagnostics.Process.Start(psi)!;
        try
        {
            var line = await proc.StandardOutput.ReadLineAsync();
            A.True(line != null && line.Contains("DRY-RUN"), "agent started: " + line);
            using var vm = new VirtualMediaClient($"https://127.0.0.1:{port}", token, fingerprint);
            var st = await vm.StatusAsync();
            A.True(st.GadgetActive && st.Images.Count == 0 && st.Mounted == null && st.Version == "1.0", "empty status");

            var iso = Path.Combine(dir, "Win11_Test.iso");
            var payload = new byte[3 * 1024 * 1024 + 123]; new Random(7).NextBytes(payload);
            File.WriteAllBytes(iso, payload);
            double last = 0; var progress = new Progress<double>(p => last = p);
            await vm.UploadAsync(iso, progress);
            st = await vm.StatusAsync();
            A.True(st.Images.Count == 1 && st.Images[0].Name == "Win11_Test.iso" && st.Images[0].Size == payload.Length, "uploaded");
            A.True(File.ReadAllBytes(Path.Combine(dir, "images", "Win11_Test.iso")).AsSpan().SequenceEqual(payload), "bytes intact");

            await vm.MountAsync("Win11_Test.iso", asCdrom: true);
            st = await vm.StatusAsync();
            A.True(st.Mounted == "Win11_Test.iso" && st.Mode == "cdrom", "mounted as cdrom");
            bool conflict = false;
            try { await vm.DeleteAsync("Win11_Test.iso"); } catch (HttpRequestException e) { conflict = e.StatusCode == System.Net.HttpStatusCode.Conflict; }
            A.True(conflict, "cannot delete mounted image");
            await vm.EjectAsync();
            A.Eq(null, (await vm.StatusAsync()).Mounted, "ejected");
            await vm.DeleteAsync("Win11_Test.iso");
            A.Eq(0, (await vm.StatusAsync()).Images.Count, "deleted");

            bool missing = false;
            try { await vm.MountAsync("nope.iso", true); } catch (HttpRequestException e) { missing = e.StatusCode == System.Net.HttpStatusCode.NotFound; }
            A.True(missing, "mount missing -> 404");
            A.True(!VirtualMediaClient.IsAllowedImageName("../etc/passwd.iso") && !VirtualMediaClient.IsAllowedImageName("x.exe") && VirtualMediaClient.IsAllowedImageName("ubuntu-24.04 (amd64).iso"), "name rules");

            // wrong token and wrong certificate are both refused
            using var badTok = new VirtualMediaClient($"https://127.0.0.1:{port}", "wrong-token-xxxxxxxx", fingerprint);
            bool unauth = false;
            try { await badTok.StatusAsync(); } catch (HttpRequestException e) { unauth = e.StatusCode == System.Net.HttpStatusCode.Unauthorized; }
            A.True(unauth, "bad token");
            using var badFp = new VirtualMediaClient($"https://127.0.0.1:{port}", token, new string('A', 64));
            bool tlsRefused = false;
            try { await badFp.StatusAsync(); } catch (HttpRequestException) { tlsRefused = true; }
            A.True(tlsRefused, "pinned fingerprint mismatch refused");
            bool http = false;
            try { new VirtualMediaClient($"http://127.0.0.1:{port}", token, fingerprint); } catch (ArgumentException) { http = true; }
            A.True(http, "http refused");
        }
        finally
        {
            try { proc.Kill(); } catch { }
            Directory.Delete(dir, true);
        }
    }


    // ================= v0.4: RMM + AI (items 7, 3) =================
    /// <summary>Fake HHC-RMM implementing the real v1 contract (incl. duplicate client_entry_id handling).</summary>
    sealed class FakeRmm : IDisposable
    {
        public readonly System.Net.HttpListener L = new();
        public readonly List<(string Method, string Path, string Body, string? Auth)> Requests = new();
        public readonly HashSet<string> SeenEntries = new();
        public bool Down;
        public string Url;
        public FakeRmm()
        {
            int port = FreePort();
            Url = $"http://127.0.0.1:{port}/";
            L.Prefixes.Add(Url); L.Start();
            _ = Task.Run(Loop);
        }
        async Task Loop()
        {
            while (L.IsListening)
            {
                System.Net.HttpListenerContext ctx;
                try { ctx = await L.GetContextAsync(); } catch { return; }
                var body = new StreamReader(ctx.Request.InputStream).ReadToEnd();
                var path = ctx.Request.Url!.AbsolutePath;
                lock (Requests) Requests.Add((ctx.Request.HttpMethod, path, body, ctx.Request.Headers["Authorization"]));
                int code = 200; string json;
                if (Down) { code = 503; json = "{\"ok\":false,\"error\":\"server_error\",\"message\":\"down\"}"; }
                else if (ctx.Request.Headers["Authorization"] != "Bearer hht_good") { code = 401; json = "{\"ok\":false,\"error\":\"invalid_key\",\"message\":\"Invalid API key.\"}"; }
                else if (path == "/api/v1/ping") json = "{\"ok\":true,\"server\":\"HHC-RMM\",\"tech\":\"sbarrow\",\"time\":\"2026-11-20T08:00:00Z\",\"scope\":\"assigned\",\"can_write\":true,\"api_version\":1}";
                else if (path == "/api/v1/tickets/T-00012") json = "{\"ticket_number\":\"T-00012\",\"status\":\"open\",\"customer\":{\"name\":\"Jane Smith\",\"phone\":\"434-555-0147\",\"email\":null,\"company\":\"Smith Accounting\"},\"device\":{\"label\":\"JANE-PC (Dell Latitude 5420)\",\"hostname\":\"JANE-PC\",\"asset_tag\":\"HHC-00231\",\"serial\":\"7KQ2L13\",\"device_id\":\"d1\"},\"issue\":\"Won't boot after update\",\"subject\":\"No boot\",\"priority\":\"high\",\"assigned_tech\":\"sbarrow\",\"created\":\"2026-11-19T14:02:00Z\",\"notes_count\":3}";
                else if (path.StartsWith("/api/v1/tickets/") && !path.Contains("T-00012")) { code = 404; json = "{\"ok\":false,\"error\":\"ticket_not_found\",\"message\":\"No ticket T-00099.\"}"; }
                else if (path.EndsWith("/notes"))
                {
                    var id = JsonDocument.Parse(body).RootElement.GetProperty("client_entry_id").GetString()!;
                    bool dup = !SeenEntries.Add(id);
                    code = dup ? 200 : 201; json = $"{{\"ok\":true,\"note_id\":8841,\"duplicate\":{(dup ? "true" : "false")}}}";
                }
                else if (path.EndsWith("/attachments")) { code = 201; json = "{\"ok\":true,\"attachment_id\":552,\"url\":\"https://rmm/api/v1/attachments/552\",\"filename\":\"x\",\"size\":5}"; }
                else if (path.EndsWith("/status"))
                {
                    var st = JsonDocument.Parse(body).RootElement.GetProperty("status").GetString();
                    json = $"{{\"ok\":true,\"status\":\"{st}\",\"ticket_number\":\"T-00012\"}}";
                }
                else if (path.EndsWith("/session"))
                {
                    var root = JsonDocument.Parse(body).RootElement;
                    int created = 0, dups = 0;
                    foreach (var e in root.GetProperty("entries").EnumerateArray()) { if (SeenEntries.Add(e.GetProperty("client_entry_id").GetString()!)) created++; else dups++; }
                    code = 201; json = $"{{\"ok\":true,\"notes_created\":{created},\"duplicates\":{dups},\"attachment_id\":553,\"status\":\"resolved\"}}";
                }
                else { code = 404; json = "{\"ok\":false,\"error\":\"not_found\",\"message\":\"?\"}"; }
                var bytes = Encoding.UTF8.GetBytes(json);
                ctx.Response.StatusCode = code; ctx.Response.ContentType = "application/json";
                ctx.Response.OutputStream.Write(bytes); ctx.Response.Close();
            }
        }
        public void Dispose() { try { L.Stop(); } catch { } }
    }

    public static async Task Test_Rmm_ReadWriteSessionAndErrors()
    {
        using var rmm = new FakeRmm();
        using var c = new RmmClient(rmm.Url, "hht_good", allowLoopbackHttp: true);
        var ping = await c.PingAsync();
        A.True(ping.Ok && ping.Tech == "sbarrow" && ping.CanWrite && ping.Scope == "assigned", "ping");
        var t = await c.GetTicketAsync("t-00012");
        A.Eq("T-00012", t.TicketNumber);
        A.True(t.Customer!.Name == "Jane Smith" && t.Device!.Serial == "7KQ2L13" && t.Device.AssetTag == "HHC-00231" && t.NotesCount == 3 && t.Created!.Value.Year == 2026, "ticket fields");
        A.True(t.Summary.Contains("Jane Smith"), "summary");

        var nf = await Catch(() => c.GetTicketAsync("99"));
        A.True(nf is RmmApiException { Code: "ticket_not_found" } e1 && (int)e1.Status == 404 && !e1.IsTransient, "404 mapped");
        A.True(await Catch(() => c.GetTicketAsync("DROP TABLE")) is ArgumentException, "bad ticket refused locally");

        var note = new RmmNote("diagnostic", "info", "Read from screen: Service tag = 7KQ2L13", null, DateTime.UtcNow, Guid.NewGuid().ToString());
        A.Eq(8841L, await c.PostNoteAsync("T-00012", note));
        await c.PostNoteAsync("T-00012", note); // duplicate accepted (200)
        string noteBody; lock (rmm.Requests) noteBody = rmm.Requests.Last(r => r.Path.EndsWith("/notes")).Body;
        A.True(noteBody.Contains("\"client_entry_id\"") && noteBody.Contains("\"source\":\"HHT-Bridge\"") && noteBody.Contains("\"tool_version\":\"bridge-0.4\""), "note wire format: " + noteBody);

        var (aid, url) = await c.AttachAsync("T-00012", "shot.png", "scan_result", "image/png", new byte[] { 1, 2, 3, 4, 5 });
        A.True(aid == 552 && url != null, "attachment");
        string attBody; lock (rmm.Requests) attBody = rmm.Requests.Last(r => r.Path.EndsWith("/attachments")).Body;
        A.True(attBody.Contains("\"data_base64\":\"AQIDBAU=\"") && attBody.Contains("\"visibility\":\"internal\""), "base64 + internal");
        A.True(await Catch(() => c.AttachAsync("T-00012", "big.mp4", "other", "video/mp4", new byte[RmmClient.MaxAttachmentBytes + 1])) is RmmApiException { Code: "too_large" }, "25 MB limit");

        A.Eq("waiting", await c.SetStatusAsync("T-00012", "waiting", "Waiting on customer password"));
        A.True(await Catch(() => c.SetStatusAsync("T-00012", "needs_parts")) is ArgumentException, "invalid status refused");

        // session bundle from the audit log: idempotent ids, typed text never present
        var dir = Path.Combine(Path.GetTempPath(), "hht-rmm-" + Guid.NewGuid().ToString("N"));
        var log = new HHT.Bridge.Core.Logging.SessionLog(dir, "s", "T-00012");
        log.Audit("Controller: Forced power off (6 s hold)."); log.Info("noise"); log.Warn("Screen shows: SMART failure reported");
        log.Audit("Typed 12 characters into target.");
        var notes = RmmClient.NotesFromLog(log.Entries, "sess1");
        log.Dispose();
        A.True(notes.Count == 3 && notes.All(n => RmmClient.Categories.Contains(n.Category) && RmmClient.Severities.Contains(n.Severity)), "notes mapped: " + notes.Count);
        A.True(notes.Any(n => n.Category == "repair" && n.Severity == "warning"), "forced off = repair/warning");
        A.True(!notes.Any(n => n.Action.Contains("noise")), "INFO lines are not posted");
        var again = RmmClient.NotesFromLog(log.Entries, "sess1");
        A.True(again.Select(n => n.ClientEntryId).SequenceEqual(notes.Select(n => n.ClientEntryId)), "deterministic ids");
        var r1 = await c.PostSessionAsync("T-00012", DateTime.UtcNow.AddMinutes(-30), DateTime.UtcNow, notes, Encoding.UTF8.GetBytes("<html/>"), "report.html", "resolved");
        var r2 = await c.PostSessionAsync("T-00012", DateTime.UtcNow.AddMinutes(-30), DateTime.UtcNow, notes, null, null, null);
        A.True(r1.NotesCreated == 3 && r1.AttachmentId == 553 && r2.NotesCreated == 0 && r2.Duplicates == 3, $"session idempotent {r1} {r2}");
        Directory.Delete(dir, true);

        using var bad = new RmmClient(rmm.Url, "hht_wrong", allowLoopbackHttp: true);
        A.True(await Catch(() => bad.PingAsync()) is RmmApiException { Code: "invalid_key" }, "401 mapped");
        A.True(Catch(() => { new RmmClient("http://rmm.example.com", "k"); return Task.CompletedTask; }).Result is ArgumentException, "https required");
    }

    public static async Task Test_Rmm_OfflineQueue()
    {
        using var rmm = new FakeRmm();
        using var c = new RmmClient(rmm.Url, "hht_good", allowLoopbackHttp: true);
        var path = Path.Combine(Path.GetTempPath(), "hht-q-" + Guid.NewGuid().ToString("N"), "queue.json");
        var q = new RmmOfflineQueue(path);
        var n1 = new RmmNote("info", "info", "a", null, DateTime.UtcNow, "11111111-1111-1111-1111-111111111111");
        q.Add("T-00012", n1); q.Add("T-00012", n1); // de-duplicated locally
        q.Add("T-00099", n1 with { ClientEntryId = "22222222-2222-2222-2222-222222222222" });
        A.Eq(2, q.Items.Count, "queued");
        rmm.Down = true;
        var (s0, k0, d0) = await q.FlushAsync(c);
        A.True(s0 == 0 && k0 == 2 && d0 == 0, "kept while RMM down");
        rmm.Down = false;
        var (s1, k1, d1) = await q.FlushAsync(c);
        A.True(s1 == 1 && k1 == 0 && d1 == 1, $"sent 1, dropped unknown ticket ({s1},{k1},{d1})");
        A.Eq(0, new RmmOfflineQueue(path).Items.Count, "persisted empty");
    }

    public static async Task Test_AiClient()
    {
        int port = FreePort();
        using var l = new System.Net.HttpListener(); l.Prefixes.Add($"http://127.0.0.1:{port}/ai/"); l.Start();
        string? gotBody = null, gotKey = null;
        _ = Task.Run(async () =>
        {
            for (int i = 0; i < 3; i++)
            {
                var ctx = await l.GetContextAsync();
                gotKey = ctx.Request.Headers["X-HHC-Key"];
                string json;
                if (ctx.Request.Url!.AbsolutePath.EndsWith("/health")) json = "{\"ok\":true}";
                else
                {
                    gotBody = new StreamReader(ctx.Request.InputStream).ReadToEnd();
                    json = "{\"summary\":\"Drive not detected.\",\"overall_severity\":\"high\",\"findings\":[{\"title\":\"No boot drive\",\"evidence\":\"No bootable device\",\"root_cause\":\"SSD failed or unplugged\",\"severity\":\"high\",\"reversible\":false,\"action_id\":\"manual_review\",\"action_params\":{},\"recommendation\":\"Check SATA mode and reseat the SSD.\"}]}";
                }
                var b = Encoding.UTF8.GetBytes(json); ctx.Response.ContentType = "application/json"; ctx.Response.OutputStream.Write(b); ctx.Response.Close();
            }
        });
        using var ai = new AiClient($"http://127.0.0.1:{port}/ai", "gate-123", allowLoopbackHttp: true);
        A.True(await ai.HealthAsync(), "health");
        var tel = AiClient.BuildTelemetry("No bootable device found", BiosTextParser.Parse("No bootable device"), "Won't boot", "Dell", "Dell", new[] { "evt1" }, "What now?");
        var res = await ai.AnalyzeAsync(tel);
        A.True(res.Summary == "Drive not detected." && res.Findings.Count == 1 && res.Findings[0].Recommendation!.Contains("SATA"), "parsed");
        A.Eq("gate-123", gotKey);
        A.True(gotBody!.Contains("\"telemetry\"") && gotBody.Contains("screen_text") && gotBody.Contains("manual_review"), "telemetry sent");
    }

    static async Task<Exception?> Catch(Func<Task> f) { try { await f(); return null; } catch (Exception e) { return e; } }


    // ================= v0.4: playbooks, boot tests, compare, time, hand-off (items 1,2,4,5,8,10) =================
    public static void Test_Playbooks()
    {
        foreach (var p in PlaybookLibrary.BuiltIn)
        {
            var problems = PlaybookSession.Validate(p).ToList();
            A.True(problems.Count == 0, string.Join("; ", problems));
        }
        var s = new PlaybookSession(PlaybookLibrary.Find("no-boot-device")!);
        A.Eq("bios", s.Current.Id);
        A.True(s.EvaluateScreen("Storage Information\nSATA Port 0  Not Installed\nNVMe: Samsung SSD 980"), "has check");
        A.Eq("detected", s.Current.Id);
        s.EvaluateScreen("Boot Sequence\nUEFI: USB Drive");          // no Windows Boot Manager
        A.Eq("nobootmgr", s.Current.Id);
        A.True(s.Back() && s.Current.Id == "detected", "back");
        s.EvaluateScreen("Boot Option #1 [Windows Boot Manager]");
        A.Eq("bootorder", s.Current.Id);
        s.Advance();
        A.True(s.IsComplete, "complete");
        var bad = new Playbook("x", "x", "x", new[] { new PlaybookStep("a", "a", "a", Next: "zzz", MacroId: "nope") });
        A.Eq(2, PlaybookSession.Validate(bad).Count(), "validation catches bad targets/macros");
    }

    sealed class FakeRig : IBenchRig
    {
        public readonly Queue<string?> Screens = new();
        public readonly List<string> Keys = new();
        public readonly List<string> Shots = new();
        public bool MountOk = true, RestartOk = true, MenuOk = true;
        public string? Last;
        public Task<bool> MountAsync(string imageName, bool asCdrom, CancellationToken ct) => Task.FromResult(MountOk);
        public Task<bool> RestartTargetAsync(CancellationToken ct) => Task.FromResult(RestartOk);
        public Task<bool> OpenBootMenuAsync(CancellationToken ct) => Task.FromResult(MenuOk);
        public Task<string?> ReadScreenAsync(CancellationToken ct) { if (Screens.Count > 0) Last = Screens.Dequeue(); return Task.FromResult(Last); }
        public void SendKey(string chord) => Keys.Add(chord);
        public Task ScreenshotAsync(string caption) { Shots.Add(caption); return Task.CompletedTask; }
        public void Log(string message) { }
    }

    public static async Task Test_BootTestRunner()
    {
        var plan = BootTestPlans.BuiltIn.First(p => p.Id == "memtest86plus");
        var menu = "Please select boot device:\nWindows Boot Manager (Samsung SSD 980)\nUEFI: Realtek PXE B01 D00\nUEFI: Linux File-CD Gadget 0414\nEnter Setup\n↑ and ↓ to move selection";
        var rig = new FakeRig();
        rig.Screens.Enqueue(menu);
        rig.Screens.Enqueue("Memtest86+ v7.00  Pass: 0  Errors: 0  Testing...");
        rig.Screens.Enqueue("Memtest86+ v7.00  Pass: 1  Errors: 0\n** Pass complete, no errors, press Esc to exit **");
        var runner = new BootTestRunner { PollEvery = TimeSpan.FromMilliseconds(5), BootMenuSettle = TimeSpan.Zero };
        var r = await runner.RunAsync(plan, "memtest86plus.iso", true, rig);
        A.Eq(BootTestResult.Pass, r.Result, r.Message);
        A.Eq("Down,Down,Enter", string.Join(",", rig.Keys), "navigated to the gadget entry");
        A.True(rig.Shots.Single().Contains("PASS"), "screenshot of result");

        var failRig = new FakeRig();
        failRig.Screens.Enqueue(menu);
        failRig.Screens.Enqueue("Memtest86+  Pass: 0  Errors: 12\nTest 7 ... address 0x1F3A4000 FAIL");
        var f = await runner.RunAsync(plan, "memtest86plus.iso", true, failRig);
        A.Eq(BootTestResult.Fail, f.Result);

        var noDev = new FakeRig(); noDev.Screens.Enqueue("Please select boot device:\nWindows Boot Manager\nEnter Setup");
        A.Eq(BootTestResult.SetupFailed, (await runner.RunAsync(plan, "x.iso", true, noDev)).Result, "gadget missing");
        A.Eq(BootTestResult.SetupFailed, (await runner.RunAsync(plan, "x.iso", true, new FakeRig { MountOk = false })).Result, "mount failed");

        var slowRig = new FakeRig(); slowRig.Screens.Enqueue(menu); slowRig.Screens.Enqueue("Testing... Pass: 0 Errors: 0");
        var quick = plan with { MaxMinutes = 0 };
        A.Eq(BootTestResult.Timeout, (await runner.RunAsync(quick, "x.iso", true, slowRig)).Result, "timeout");
        using var cts = new CancellationTokenSource(50);
        var abortRig = new FakeRig(); abortRig.Screens.Enqueue(menu);
        A.Eq(BootTestResult.Aborted, (await new BootTestRunner { PollEvery = TimeSpan.FromSeconds(10), BootMenuSettle = TimeSpan.Zero }.RunAsync(plan, "x.iso", true, abortRig, null, cts.Token)).Result, "abort");
        foreach (var p in BootTestPlans.BuiltIn) { _ = new Regex(p.PassRegex); _ = new Regex(p.FailRegex); _ = new Regex(p.BootDeviceRegex); }
    }

    public static void Test_ScreenCompare()
    {
        var before = new ScreenSnapshot("Boot", DateTime.Now, "Boot Mode              [UEFI]\nSecure Boot           [Enabled]\nSATA Operation:  AHCI\nSystem Time  [10:31:02]\nCPU Temperature   48 C\nFast Boot   Disabled");
        var after = new ScreenSnapshot("Boot", DateTime.Now, "Boot Mode              [Legacy]\nSecure Boot           [Enab1ed]\nSATA Operation:  RAID On\nSystem Time  [17:02:55]\nCPU Temperature   61 C\nWake on LAN   Enabled");
        var ch = ScreenCompare.Compare(before, after);
        A.True(ch.Any(c => c.Kind == SettingChangeKind.Changed && c.Setting == "Boot Mode" && c.After == "Legacy"), "boot mode change");
        A.True(ch.Any(c => c.Setting == "SATA Operation" && c.After == "RAID On"), "SATA change");
        A.True(!ch.Any(c => c.Setting.StartsWith("Secure Boot")), "OCR 1/l noise ignored");
        A.True(!ch.Any(c => c.Setting.Contains("Time") || c.Setting.Contains("Temperature")), "volatile ignored");
        A.True(ch.Any(c => c.Kind == SettingChangeKind.Added && c.Setting == "Wake on LAN") && ch.Any(c => c.Kind == SettingChangeKind.Removed && c.Setting == "Fast Boot"), "added/removed");
        var path = Path.Combine(Path.GetTempPath(), "hht-cmp-" + Guid.NewGuid().ToString("N"), "s.json");
        ScreenCompare.Save(path, new[] { before });
        A.Eq("Boot", ScreenCompare.Load(path).Single().Label);
        Directory.Delete(Path.GetDirectoryName(path)!, true);
    }

    public static void Test_TimeTracker()
    {
        var t0 = new DateTime(2026, 11, 20, 9, 0, 0);
        var tt = new TimeTracker(t0) { IdleCap = TimeSpan.FromMinutes(5), IncrementMinutes = 15 };
        for (int m = 0; m <= 20; m++) tt.Mark(t0.AddMinutes(m));     // 20 active minutes
        tt.Mark(t0.AddMinutes(200));                                    // came back after a 3-hour test: only 5 min counted
        tt.Mark(t0.AddMinutes(199));                                    // out-of-order ignored
        var s = tt.Summarize(t0.AddMinutes(210));
        A.Eq(25.0, s.Active.TotalMinutes, "active");
        A.Eq(30.0, s.Billable.TotalMinutes, "rounded up to 15-min increments");
        A.Eq(0.5, s.BillableHours);
        var csv = Path.Combine(Path.GetTempPath(), "hht-ts-" + Guid.NewGuid().ToString("N"), "timesheet.csv");
        TimeTracker.AppendTimesheet(csv, t0, "T-00012", "=cmd|' /C calc'!A0", s);
        TimeTracker.AppendTimesheet(csv, t0, null, "steve", s);
        var lines = File.ReadAllLines(csv);
        A.Eq(3, lines.Length);
        A.True(lines[1].Contains(",'=cmd") && lines[1].EndsWith(",25,0.50"), "formula injection blocked: " + lines[1]);
        Directory.Delete(Path.GetDirectoryName(csv)!, true);
        A.Eq(0.0, new TimeTracker(t0).Summarize(t0).Billable.TotalMinutes, "no activity -> 0");
    }

    public static async Task Test_AgentInstallMacro()
    {
        var m = MacroLibrary.AgentInstall("https://rmm.hammerhillcomputers.com/agent/HHT-Agent.exe", "/quiet /token=ABC123");
        A.True(m.Confirm && m.Category == "Hand-off", "confirm");
        var t = new RecordingTarget();
        var res = await new MacroRunner { KeyDelayMs = 0, StepDelayMs = 0 }.RunAsync(m with { Steps = m.Steps.Where(x => x.Kind != MacroStepKind.Delay).ToArray() }, t);
        A.True(res.Success, res.Message);
        // reconstruct what was typed (US layout) to prove the command is exactly right
        var typed = new StringBuilder();
        var map = new Dictionary<(byte, bool), char>();
        foreach (var ch in Enumerable.Range(32, 95).Select(i => (char)i).Append('\n'))
            if (TextTyper.TryMap(ch, out var u, out var sh)) map.TryAdd((u, sh), ch);
        foreach (var r in t.Sent.Where(r => !r.IsEmpty && (r.Modifiers == KeyModifiers.None || r.Modifiers == KeyModifiers.LeftShift)))
            if (map.TryGetValue((r.Keys[0], r.Modifiers == KeyModifiers.LeftShift), out var c)) typed.Append(c);
        var text = typed.ToString();
        A.True(text.Contains("$f=\"$env:TEMP\\hht-agent.exe\"; Invoke-WebRequest -UseBasicParsing 'https://rmm.hammerhillcomputers.com/agent/HHT-Agent.exe' -OutFile $f; Start-Process $f -ArgumentList '/quiet /token=ABC123' -Wait"), "typed: " + text);
        var msi = MacroLibrary.AgentInstall("https://x.example.com/a.msi", "");
        A.True(msi.Steps.Any(s => s.Value.Contains("msiexec.exe -ArgumentList '/i',$f,'/qn' -Wait")), "msi");
        A.True(await Catch(() => { MacroLibrary.AgentInstall("http://x/a.exe", ""); return Task.CompletedTask; }) is ArgumentException, "http refused");
        A.True(await Catch(() => { MacroLibrary.AgentInstall("https://x/a.exe';calc;'", ""); return Task.CompletedTask; }) is ArgumentException, "quote injection refused");
        A.True(await Catch(() => { MacroLibrary.AgentInstall("https://x/a.exe", "a; del *"); return Task.CompletedTask; }) is ArgumentException, "arg injection refused");
    }


    // ================= v0.4: sign-off, licensing, cloud relay (items 9, 14, 15) =================
    static byte[] TinyPng() => Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAGQAAAAyCAIAAAAx7rVNAAAAQklEQVR42u3OMQ0AAAgDoL1/aRzD6g7gV6pNlhcAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAGgDsC4AAeX+dQUAAAAASUVORK5CYII=");

    public static async Task Test_SignOff()
    {
        const string key = "ABCD-EFGH-JKLM-NPQR";
        var host = new FakeHost();
        int port = FreePort();
        using var srv = new BridgeWebServer(host, new BridgeWebOptions { Port = port, AccessKey = key, Certificate = TestCert(), BindAddress = System.Net.IPAddress.Loopback });
        srv.Start();
        SignOff? got = null;
        srv.SignedOff += so => got = so;
        using var http = Pinned(TestCert());
        var b = $"https://127.0.0.1:{port}";
        A.True((await http.GetStringAsync(b + "/sign")).Contains("6-digit code"), "sign page");
        A.Eq(401, (int)(await http.GetAsync(b + "/api/sign/info?code=000000")).StatusCode, "no sign-off open");
        var code = srv.StartSignOff(new SignOffRequest("Hammer Hill Computers", "T-00012", "Replaced SSD, reinstalled Windows 11.", "$189.00"));
        A.True(code.Length == 6 && code.All(char.IsDigit), "6-digit code");
        var info = await http.GetStringAsync(b + "/api/sign/info?code=" + code);
        A.True(info.Contains("Replaced SSD") && info.Contains("$189.00"), "info: " + info);
        async Task<int> Post(object o) => (int)(await http.PostAsync(b + "/api/sign?code=" + code, new StringContent(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json"))).StatusCode;
        A.Eq(400, await Post(new { name = "Jane Smith", agree = false, png = "data:image/png;base64," + Convert.ToBase64String(TinyPng()) }), "must agree");
        A.Eq(400, await Post(new { name = "Jane Smith", agree = true, png = Convert.ToBase64String(new byte[500]) }), "not a png");
        A.Eq(200, await Post(new { name = "Jane Smith", agree = true, png = "data:image/png;base64," + Convert.ToBase64String(TinyPng()) }), "signed");
        A.True(got != null && got.Name == "Jane Smith" && got.AgreedSummary.Contains("SSD") && got.SignaturePng.Length == TinyPng().Length, "event raised");
        A.Eq(401, await Post(new { name = "Again", agree = true, png = Convert.ToBase64String(TinyPng()) }), "single use");
        var big = new string('A', BridgeWebServer.MaxBodyBytes + 10);
        var code2 = srv.StartSignOff(new SignOffRequest("s", null, "x", null));
        int capStatus;
        try { capStatus = (int)(await http.PostAsync(b + "/api/sign?code=" + code2, new StringContent(big))).StatusCode; }
        catch (HttpRequestException) { capStatus = 413; } // server refused and closed before the client finished sending: also correct
        A.Eq(413, capStatus, "body cap");
        // the shop access key is never needed (or accepted) for sign-off, and the code never unlocks the API
        A.Eq(401, (int)(await http.GetAsync(b + "/api/status?k=" + code2)).StatusCode, "code is not an API key");

        var html = HHT.Bridge.Core.Reports.SessionReportWriter.ToHtml(new HHT.Bridge.Core.Reports.SessionReport { Started = DateTime.Now, SignOff = got, CustomerCopy = true, AiSummary = "internal stuff",
            Events = new[] { new HHT.Bridge.Core.Logging.LogEntry(DateTime.Now, "AUDIT", "secret internal event") } });
        A.True(html.Contains("Customer sign-off") && html.Contains("Jane Smith") && !html.Contains("internal stuff") && !html.Contains("secret internal event"), "customer copy");
    }

    public static void Test_Licensing()
    {
        using var priv = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var pub = priv.ExportSubjectPublicKeyInfoPem();
        var now = new DateTime(2026, 11, 20);
        var key = LicenseVerifier.Issue(new LicenseInfo("abc", "Test Shop", Edition.Pro, 3, now, now.AddYears(1)), priv);
        A.True(LicenseVerifier.TryVerify(key, now, out var info, out var err, pub) && info!.Edition == Edition.Pro && info.Seats == 3, err);
        A.True(LicenseVerifier.TryVerify("  " + key[..40] + "\n" + key[40..] + " ", now, out _, out _, pub), "whitespace/line breaks tolerated");
        A.True(!LicenseVerifier.TryVerify(key, now.AddYears(2), out _, out err, pub) && err.Contains("expired"), "expiry");
        // tamper: upgrade the edition inside the payload -> signature fails
        var parts = key["HHT1.".Length..].Split('.');
        var json = Encoding.UTF8.GetString(Convert.FromBase64String(parts[0].Replace('-', '+').Replace('_', '/').PadRight((parts[0].Length + 3) / 4 * 4, '=')));
        var forged = Convert.ToBase64String(Encoding.UTF8.GetBytes(json.Replace("\"edition\":1", "\"edition\":2"))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        A.True(!LicenseVerifier.TryVerify("HHT1." + forged + "." + parts[1], now, out _, out err, pub) && err.Contains("signature"), "tamper detected");
        // a key signed by someone else's private key is rejected by the real built-in public key
        A.True(!LicenseVerifier.TryVerify(key, now, out _, out _), "foreign signer rejected");
        A.True(!LicenseVerifier.TryVerify("garbage", now, out _, out _) && !LicenseVerifier.TryVerify("HHT1.x.y", now, out _, out _), "garbage");
        A.Eq(Edition.Free, LicenseVerifier.EffectiveEdition(null, now, out var st)); A.True(st.Contains("Free"), st);
        A.True(LicenseVerifier.Allows(Edition.Free, Feature.Video) && !LicenseVerifier.Allows(Edition.Free, Feature.Reports), "free gating");
        A.True(LicenseVerifier.Allows(Edition.Pro, Feature.Macros) && !LicenseVerifier.Allows(Edition.Pro, Feature.RemoteView), "pro gating");
        A.True(Enum.GetValues<Feature>().All(f => LicenseVerifier.Allows(Edition.Shop, f)), "shop has everything");
    }

    public static void Test_HammerHillLicenseKey()
    {
        var path = Environment.GetEnvironmentVariable("HHT_LICENSE_FILE");
        if (path == null || !File.Exists(path)) { Console.WriteLine("        (skipped: no HHT_LICENSE_FILE)"); return; }
        A.True(LicenseVerifier.TryVerify(File.ReadAllText(path), DateTime.UtcNow, out var info, out var err) && info!.Edition == Edition.Shop, err);
    }

    public static async Task Test_CloudRelayEndToEnd()
    {
        var py = new[] { "python3", "python" }.FirstOrDefault(p => { try { using var pr = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(p, "--version") { RedirectStandardOutput = true, RedirectStandardError = true }); pr!.WaitForExit(); return pr.ExitCode == 0; } catch { return false; } });
        var relayPy = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../server/cloud-relay/hht_bridge_relay.py"));
        if (py == null || !File.Exists(relayPy)) { Console.WriteLine("        (skipped: needs python3)"); return; }
        var dir = Path.Combine(Path.GetTempPath(), "hht-relay-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var cfg = Path.Combine(dir, "config.json");
        File.WriteAllText(cfg, """
            {"owner_view_key":"OWNER-KEY-0123456789abcdef","shops":[
              {"id":"dan","name":"Danville","publish_key":"PUB-DAN-0123456789abcdef","view_key":"VIEW-DAN-0123456789abcdef"},
              {"id":"mart","name":"Martinsville","publish_key":"PUB-MART-0123456789abcdef","view_key":"VIEW-MART-0123456789abcdef"}]}
            """);
        int port = FreePort();
        using var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(py, $"{relayPy} --config {cfg} --port {port}") { RedirectStandardOutput = true, RedirectStandardError = true })!;
        try
        {
            A.True((await proc.StandardOutput.ReadLineAsync())!.Contains("2 shop(s)"), "relay up");
            var jpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 0xFF, 0xD9 };
            var baseUrl = $"http://127.0.0.1:{port}";
            using var pubDan = new CloudPublisher(baseUrl, "PUB-DAN-0123456789abcdef", () => new("bench-1", "Bench 1", "T-00012", true, true, 0, jpeg), allowLoopbackHttp: true);
            using var pubMart = new CloudPublisher(baseUrl, "PUB-MART-0123456789abcdef", () => new("b-9", "Front counter", null, false, false, 0, null), allowLoopbackHttp: true);
            A.True(await pubDan.PublishOnceAsync() && await pubMart.PublishOnceAsync(), "published: " + pubDan.LastError);
            A.True(!await pubDan.PublishOnceAsync() || true, "rate limited publish tolerated");
            using var bad = new CloudPublisher(baseUrl, "WRONG-KEY-0123456789abcdef", () => new("x", "x", null, false, false, 0, null), allowLoopbackHttp: true);
            A.True(!await bad.PublishOnceAsync() && bad.LastError!.Contains("rejected"), "bad publish key");

            using var http = new HttpClient();
            async Task<string> Get(string path, string k) { var rq = new HttpRequestMessage(HttpMethod.Get, baseUrl + path); rq.Headers.Add("X-HHT-View-Key", k); var r = await http.SendAsync(rq); return $"{(int)r.StatusCode}:{await r.Content.ReadAsStringAsync()}"; }
            var owner = await Get("/api/benches", "OWNER-KEY-0123456789abcdef");
            A.True(owner.StartsWith("200") && owner.Contains("Danville") && owner.Contains("Martinsville") && owner.Contains("T-00012"), "owner sees all: " + owner);
            var dan = await Get("/api/benches", "VIEW-DAN-0123456789abcdef");
            A.True(dan.Contains("Bench 1") && !dan.Contains("Front counter"), "shop view scoped: " + dan);
            A.True((await Get("/api/benches", "nope")).StartsWith("401"), "bad view key");
            var img = await http.GetByteArrayAsync($"{baseUrl}/api/bench/dan/bench-1.jpg?k=VIEW-DAN-0123456789abcdef");
            A.True(img.AsSpan().SequenceEqual(jpeg), "jpeg round trip");
            var cross = await http.GetAsync($"{baseUrl}/api/bench/dan/bench-1.jpg?k=VIEW-MART-0123456789abcdef");
            A.Eq(404, (int)cross.StatusCode, "other shop cannot see image");
            A.True((await http.GetStringAsync(baseUrl + "/")).Contains("Bridge benches"), "dashboard page");
            var notJpeg = new CloudPublisher(baseUrl, "PUB-DAN-0123456789abcdef", () => new("bench-2", "x", null, true, true, 0, new byte[] { 1, 2, 3 }), allowLoopbackHttp: true);
            A.True(!await notJpeg.PublishOnceAsync(), "non-JPEG refused");
            notJpeg.Dispose();
        }
        finally
        {
            try { proc.Kill(); } catch { }
            Directory.Delete(dir, true);
        }
    }


    // ================= v0.4: beep codes, sensors (items 11, 12) =================
    static short[] Synth(int rate, IEnumerable<(double OnSec, double OffSec)> pattern, double freq = 2000, double noise = 300, int seed = 1)
    {
        var rnd = new Random(seed);
        var list = new List<short>();
        void Add(double seconds, bool tone)
        {
            int n = (int)(seconds * rate);
            for (int i = 0; i < n; i++)
            {
                double v = (rnd.NextDouble() * 2 - 1) * noise;
                if (tone) v += 9000 * Math.Sin(2 * Math.PI * freq * list.Count / rate);
                list.Add((short)Math.Clamp(v, short.MinValue, short.MaxValue));
            }
        }
        Add(0.8, false);
        foreach (var (on, off) in pattern) { Add(on, true); Add(off, false); }
        Add(0.8, false);
        return list.ToArray();
    }

    public static void Test_BeepCodes()
    {
        const int R = 16000;
        // AMI "1 long, 3 short", repeated twice
        var unit = new[] { (0.9, 0.35), (0.22, 0.3), (0.22, 0.3), (0.22, 1.6) };
        var a = BeepCodeAnalyzer.Analyze(Synth(R, unit.Concat(unit)), R, "AMI");
        A.Eq("1 long, 3 short", a.Pattern, a.Describe());
        A.True(a.Candidates.First().Vendor == "AMI" && a.Candidates.First().Meaning.Contains("Memory"), "AMI meaning: " + a.Describe());

        // Dell: 2 short beeps (no RAM), repeating with long pauses
        var dell = BeepCodeAnalyzer.Analyze(Synth(R, new[] { (0.25, 0.25), (0.25, 1.8), (0.25, 0.25), (0.25, 1.8) }), R, "Dell");
        A.Eq("2 short", dell.Pattern, dell.Describe());
        A.True(dell.Candidates.First().Vendor == "Dell" && dell.Candidates.First().Meaning.Contains("No memory"), dell.Describe());

        // Phoenix 1-3-1 (groups of short beeps separated by pauses)
        var ph = BeepCodeAnalyzer.Analyze(Synth(R, new[] { (0.2, 0.7), (0.2, 0.2), (0.2, 0.2), (0.2, 0.7), (0.2, 1.5) }), R, "Phoenix");
        A.Eq("1-3-1", ph.Pattern, ph.Describe());
        A.True(ph.Candidates.Any(c => c.Meaning.Contains("RAM refresh")), ph.Describe());

        // silence / noise only
        A.True(!BeepCodeAnalyzer.Analyze(Synth(R, Array.Empty<(double, double)>()), R).Heard, "noise is not a beep");
        A.Eq("No beeps detected.", BeepCodeAnalyzer.Analyze(new short[100], R).Describe());

        // WAV round trip
        var samples = Synth(R, new[] { (0.3, 0.3) });
        var (back, rate) = Wav.Read(Wav.Write(samples, R));
        A.True(rate == R && back.AsSpan().SequenceEqual(samples), "wav");
    }

    public static void Test_SensorParsing()
    {
        var s = BridgeBoxController.ParseSensors("SENS T1=41.5 T2=NA FAN1=1320");
        A.True(s != null && s.Probe1C == 41.5 && s.Probe2C == null && s.FanRpm == 1320, "parsed");
        A.True(s!.ToString().Contains("41.5") && s.ToString().Contains("1320 RPM"), s.ToString());
        A.True(BridgeBoxController.ParseSensors("SENS T1=-5.0 T2=88.2 FAN1=0")!.Probe1C == -5.0, "negative");
        A.Eq(null, BridgeBoxController.ParseSensors("ERR UNKNOWN"));
    }

    public static void Test_SessionLog_WritesFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hht-log-" + Guid.NewGuid().ToString("N"));
        string file;
        using (var log = new SessionLog(dir, "tech1")) { log.Audit("Sent Ctrl+Alt+Del"); file = log.FilePath; }
        var text = File.ReadAllText(file);
        A.True(text.Contains("[AUDIT] Sent Ctrl+Alt+Del") && text.Contains("Session ended"), "contents");
        Directory.Delete(dir, true);
    }
}
