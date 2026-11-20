namespace HHT.Bridge.Core.Ch9329;

/// <summary>A validated reply frame received from the CH9329.</summary>
public sealed record Ch9329Reply(byte Address, byte Command, byte[] Data)
{
    /// <summary>The original command this reply answers (flag bits stripped).</summary>
    public byte BaseCommand => (byte)(Command & 0x3F);
    public bool IsError => (Command & Ch9329Protocol.ReplyErrorFlag) == Ch9329Protocol.ReplyErrorFlag;
    public bool IsSuccess => !IsError && (Command & Ch9329Protocol.ReplySuccessFlag) != 0;

    /// <summary>Status byte for single-byte replies (0x00 = success).</summary>
    public byte? Status => Data.Length == 1 ? Data[0] : null;
}

/// <summary>
/// Incremental, resynchronising parser for CH9329 reply frames.
/// Feed it any chunk of bytes read from the serial port; complete, checksum-valid
/// frames are returned. Garbage and corrupt frames are skipped.
/// </summary>
public sealed class Ch9329Parser
{
    private readonly List<byte> _buffer = new(128);

    public int ChecksumErrors { get; private set; }
    public int DiscardedBytes { get; private set; }

    public IReadOnlyList<Ch9329Reply> Feed(ReadOnlySpan<byte> chunk)
    {
        foreach (var b in chunk) _buffer.Add(b);
        var replies = new List<Ch9329Reply>();

        while (true)
        {
            int start = FindHeader();
            if (start < 0)
            {
                // Keep a trailing 0x57 in case the 0xAB arrives in the next chunk.
                int keep = _buffer.Count > 0 && _buffer[^1] == Ch9329Protocol.Head1 ? 1 : 0;
                DiscardedBytes += _buffer.Count - keep;
                _buffer.RemoveRange(0, _buffer.Count - keep);
                break;
            }
            if (start > 0)
            {
                DiscardedBytes += start;
                _buffer.RemoveRange(0, start);
            }

            if (_buffer.Count < 5) break; // need HEAD, ADDR, CMD, LEN
            int len = _buffer[4];
            int total = 5 + len + 1;
            if (len > 64)
            {
                // Not a legal frame; drop this header and resync.
                DiscardedBytes += 2;
                _buffer.RemoveRange(0, 2);
                continue;
            }
            if (_buffer.Count < total) break;

            byte expected = 0;
            for (int i = 0; i < total - 1; i++) expected = unchecked((byte)(expected + _buffer[i]));
            if (expected != _buffer[total - 1])
            {
                ChecksumErrors++;
                DiscardedBytes += 2;
                _buffer.RemoveRange(0, 2);
                continue;
            }

            var data = _buffer.GetRange(5, len).ToArray();
            replies.Add(new Ch9329Reply(_buffer[2], _buffer[3], data));
            _buffer.RemoveRange(0, total);
        }

        return replies;
    }

    private int FindHeader()
    {
        for (int i = 0; i + 1 < _buffer.Count; i++)
            if (_buffer[i] == Ch9329Protocol.Head1 && _buffer[i + 1] == Ch9329Protocol.Head2) return i;
        return -1;
    }
}
