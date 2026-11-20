namespace HHT.Bridge.Core.Audio;

public sealed record Beep(double StartSeconds, double DurationSeconds, bool IsLong);

public sealed record BeepAnalysis(IReadOnlyList<Beep> Beeps, IReadOnlyList<int> Groups, string Pattern, IReadOnlyList<BeepMeaning> Candidates)
{
    public bool Heard => Beeps.Count > 0;
    public string Describe() => !Heard ? "No beeps detected." :
        $"Heard {Beeps.Count} beep(s): {Pattern}" + (Groups.Count > 1 ? $" (groups {string.Join("-", Groups)})" : "") +
        (Candidates.Count > 0 ? ". Likely: " + string.Join("; ", Candidates.Take(3).Select(c => $"{c.Vendor}: {c.Meaning}")) : ".");
}

public sealed record BeepMeaning(string Vendor, string Pattern, string Meaning);

/// <summary>
/// Finds POST beeps in a recording (laptop/USB microphone held near the case — PC speakers are not on HDMI)
/// and matches them against common BIOS beep-code tables. Works on 16-bit mono PCM.
/// </summary>
public static class BeepCodeAnalyzer
{
    public static BeepAnalysis Analyze(ReadOnlySpan<short> samples, int sampleRate, string? vendorHint = null)
    {
        var beeps = DetectBeeps(samples, sampleRate);
        if (beeps.Count == 0) return new BeepAnalysis(beeps, Array.Empty<int>(), "", Array.Empty<BeepMeaning>());

        // Long vs short: split at the geometric mean when durations differ clearly, else by absolute length.
        double min = beeps.Min(b => b.DurationSeconds), max = beeps.Max(b => b.DurationSeconds);
        double split = max / min >= 1.8 ? Math.Sqrt(min * max) : 0.55;
        beeps = beeps.Select(b => b with { IsLong = b.DurationSeconds >= split }).ToList();

        // Groups: a pause clearly longer than the usual gap separates groups (Phoenix 1-3-1 style).
        var gaps = beeps.Zip(beeps.Skip(1), (a, b) => b.StartSeconds - (a.StartSeconds + a.DurationSeconds)).ToList();
        var groups = new List<int> { 1 };
        if (gaps.Count > 0)
        {
            double median = gaps.OrderBy(g => g).ElementAt(gaps.Count / 2);
            double minGap = gaps.Min();
            double groupGap = Math.Max(minGap * 2.2, 0.45);
            if (gaps.Max() < groupGap) groupGap = double.MaxValue; // evenly spaced: one group
            foreach (var g in gaps)
            {
                if (g >= groupGap) groups.Add(1); else groups[^1]++;
            }
            _ = median;
        }

        // Codes usually repeat: keep only the first repetition of a repeating group sequence.
        var unit = FirstRepeatingUnit(groups);
        int beepsInUnit = unit.Sum();
        var unitBeeps = beeps.Take(beepsInUnit).ToList();
        int longs = unitBeeps.Count(b => b.IsLong), shorts = unitBeeps.Count - longs;
        string pattern = unit.Count > 1
            ? string.Join("-", unit)
            : longs > 0 && shorts > 0 ? $"{longs} long, {shorts} short" : longs > 0 ? $"{longs} long" : $"{shorts} short";

        var candidates = BeepCodeTable.Match(pattern, vendorHint);
        return new BeepAnalysis(beeps, unit, pattern, candidates);
    }

    private static List<int> FirstRepeatingUnit(List<int> groups)
    {
        for (int len = 1; len <= groups.Count / 2; len++)
        {
            bool repeats = true;
            for (int i = len; i < groups.Count && repeats; i++)
                if (groups[i] != groups[i % len] && i < groups.Count - 1) repeats = false; // last group may be cut off
            if (repeats && groups.Count >= len * 2) return groups.Take(len).ToList();
        }
        return groups;
    }

    /// <summary>Energy envelope (10 ms frames) with an adaptive threshold and hysteresis.</summary>
    public static List<Beep> DetectBeeps(ReadOnlySpan<short> samples, int sampleRate)
    {
        int frame = Math.Max(1, sampleRate / 100);
        int frames = samples.Length / frame;
        if (frames < 5) return new List<Beep>();
        var rms = new double[frames];
        for (int f = 0; f < frames; f++)
        {
            double sum = 0;
            for (int i = f * frame; i < (f + 1) * frame; i++) sum += (double)samples[i] * samples[i];
            rms[f] = Math.Sqrt(sum / frame);
        }
        var sorted = rms.OrderBy(x => x).ToArray();
        double floor = sorted[(int)(frames * 0.20)];
        double peak = sorted[Math.Min(frames - 1, (int)(frames * 0.99))];
        if (peak < 300 || peak < floor * 3) return new List<Beep>(); // nothing clearly above the noise
        double on = floor + (peak - floor) * 0.35, off = floor + (peak - floor) * 0.20;

        var beeps = new List<Beep>();
        int? startF = null;
        int lastOn = -100;
        for (int f = 0; f < frames; f++)
        {
            bool loud = startF == null ? rms[f] >= on : rms[f] >= off;
            if (loud)
            {
                if (startF == null)
                {
                    // merge with the previous beep if the dip was shorter than 40 ms (tone wobble)
                    if (beeps.Count > 0 && f - lastOn <= 4) { var prev = beeps[^1]; beeps.RemoveAt(beeps.Count - 1); startF = (int)Math.Round(prev.StartSeconds * 100); }
                    else startF = f;
                }
                lastOn = f;
            }
            else if (startF != null)
            {
                Close(f);
            }
        }
        if (startF != null) Close(frames);
        return beeps.Where(b => b.DurationSeconds >= 0.06).ToList();

        void Close(int endF)
        {
            beeps.Add(new Beep(startF!.Value / 100.0, (lastOn + 1 - startF.Value) / 100.0, false));
            startF = null;
        }
    }
}

/// <summary>Common beep-code meanings. Boards vary: confirm with the motherboard/system manual.</summary>
public static class BeepCodeTable
{
    public static readonly IReadOnlyList<BeepMeaning> All = new[]
    {
        new BeepMeaning("AMI", "1 short", "Memory refresh failure (older boards) — on many boards one short beep simply means POST passed."),
        new BeepMeaning("AMI", "2 short", "Memory parity error: reseat/replace RAM."),
        new BeepMeaning("AMI", "3 short", "Base 64K memory failure: reseat/replace RAM."),
        new BeepMeaning("AMI", "4 short", "System timer failure (motherboard)."),
        new BeepMeaning("AMI", "5 short", "Processor error."),
        new BeepMeaning("AMI", "6 short", "Keyboard controller / Gate A20 error."),
        new BeepMeaning("AMI", "8 short", "Display memory error: reseat/replace the graphics card."),
        new BeepMeaning("AMI", "1 long, 3 short", "Memory test failed (conventional/extended)."),
        new BeepMeaning("AMI", "1 long, 8 short", "Display test failed / no graphics card."),
        new BeepMeaning("Award", "1 long, 2 short", "Video adapter error: reseat the GPU or check the monitor cable."),
        new BeepMeaning("Award", "1 long, 3 short", "Video adapter or keyboard controller error."),
        new BeepMeaning("Award", "1 long", "Memory problem (repeating long beeps): reseat/replace RAM."),
        new BeepMeaning("Dell", "1 short", "BIOS ROM checksum in progress or failure."),
        new BeepMeaning("Dell", "2 short", "No memory (RAM) detected."),
        new BeepMeaning("Dell", "3 short", "Chipset error / real-time clock failure (motherboard)."),
        new BeepMeaning("Dell", "4 short", "Memory (RAM) failure."),
        new BeepMeaning("Dell", "5 short", "CMOS battery failure: replace the coin cell."),
        new BeepMeaning("Dell", "6 short", "Video card/chip failure."),
        new BeepMeaning("Dell", "7 short", "Processor (CPU) failure."),
        new BeepMeaning("Dell", "8 short", "LCD/display failure."),
        new BeepMeaning("Phoenix", "1-1-3", "CMOS read/write failure."),
        new BeepMeaning("Phoenix", "1-1-4", "BIOS ROM checksum error."),
        new BeepMeaning("Phoenix", "1-2-1", "Programmable interval timer failure."),
        new BeepMeaning("Phoenix", "1-3-1", "RAM refresh verification failure: reseat/replace RAM."),
        new BeepMeaning("Phoenix", "1-3-3", "First 64K RAM chip or data line failure."),
        new BeepMeaning("Phoenix", "1-4-2", "Parity failure in first 64K RAM."),
        new BeepMeaning("Phoenix", "3-3-4", "Display memory test failure."),
        new BeepMeaning("Phoenix", "4-2-1", "Timer tick failure."),
        new BeepMeaning("Phoenix", "4-2-3", "Gate A20 failure."),
        new BeepMeaning("IBM/Lenovo", "1 long, 2 short", "Display adapter error."),
        new BeepMeaning("IBM/Lenovo", "1 long, 3 short", "Enhanced graphics adapter / video error."),
        new BeepMeaning("IBM/Lenovo", "3 long", "Keyboard card / system board error."),
    };

    public static IReadOnlyList<BeepMeaning> Match(string pattern, string? vendorHint)
    {
        var hits = All.Where(m => m.Pattern.Equals(pattern, StringComparison.OrdinalIgnoreCase)).ToList();
        if (!string.IsNullOrWhiteSpace(vendorHint))
            hits = hits.OrderByDescending(h => vendorHint.Contains(h.Vendor.Split('/')[0], StringComparison.OrdinalIgnoreCase)).ToList();
        return hits;
    }
}

/// <summary>Minimal 16-bit PCM WAV reader/writer (for saved beep recordings and tests).</summary>
public static class Wav
{
    public static byte[] Write(ReadOnlySpan<short> samples, int sampleRate)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        int dataLen = samples.Length * 2;
        w.Write("RIFF"u8.ToArray()); w.Write(36 + dataLen); w.Write("WAVE"u8.ToArray());
        w.Write("fmt "u8.ToArray()); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(sampleRate); w.Write(sampleRate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8.ToArray()); w.Write(dataLen);
        foreach (var s in samples) w.Write(s);
        w.Flush();
        return ms.ToArray();
    }

    public static (short[] Samples, int SampleRate) Read(byte[] wav)
    {
        if (wav.Length < 44 || wav[0] != 'R' || wav[8] != 'W') throw new InvalidDataException("Not a WAV file.");
        int channels = BitConverter.ToInt16(wav, 22), rate = BitConverter.ToInt32(wav, 24), bits = BitConverter.ToInt16(wav, 34);
        if (bits != 16) throw new InvalidDataException("Only 16-bit WAV is supported.");
        int pos = 12;
        while (pos + 8 <= wav.Length)
        {
            int len = BitConverter.ToInt32(wav, pos + 4);
            if (wav[pos] == 'd' && wav[pos + 1] == 'a' && wav[pos + 2] == 't' && wav[pos + 3] == 'a')
            {
                int n = Math.Min(len, wav.Length - pos - 8) / 2 / channels;
                var s = new short[n];
                for (int i = 0; i < n; i++) s[i] = BitConverter.ToInt16(wav, pos + 8 + i * 2 * channels); // first channel
                return (s, rate);
            }
            pos += 8 + len + (len & 1);
        }
        throw new InvalidDataException("WAV has no data chunk.");
    }
}
