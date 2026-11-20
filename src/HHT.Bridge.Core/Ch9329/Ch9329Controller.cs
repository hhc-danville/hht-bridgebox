namespace HHT.Bridge.Core.Ch9329;

/// <summary>
/// Owns a transport to one CH9329 and serialises everything sent to it.
///
/// Ordering rules:
///   • Keyboard reports and mouse button/wheel changes are sent strictly in order.
///   • Absolute mouse <i>moves</i> are coalesced: only the newest pending position is sent,
///     so a slow 9600-baud link never builds up lag.
///   • Relative mouse moves are accumulated and sent in ±127 chunks, so no motion is lost.
/// A background reader parses replies and completes <see cref="GetInfoAsync"/>.
/// </summary>
public sealed class Ch9329Controller : IDisposable
{
    private readonly IByteTransport _transport;
    private readonly object _gate = new();
    private readonly Queue<byte[]> _ordered = new();
    private readonly SemaphoreSlim _signal = new(0, int.MaxValue);
    private readonly CancellationTokenSource _cts = new();
    private readonly Ch9329Parser _parser = new();
    private Thread? _writer, _reader;

    private byte[]? _pendingAbsMove;
    private int _relDx, _relDy;
    private MouseButtons _relButtons;

    private TaskCompletionSource<Ch9329Info>? _infoWaiter;

    public Ch9329Controller(IByteTransport transport) => _transport = transport;

    public string PortName => _transport.Name;
    public long FramesSent { get; private set; }
    public long ErrorReplies { get; private set; }
    public Ch9329Info? LastInfo { get; private set; }

    /// <summary>Raised on the reader thread for every valid reply.</summary>
    public event Action<Ch9329Reply>? ReplyReceived;
    /// <summary>Raised on a background thread when the transport fails.</summary>
    public event Action<Exception>? Faulted;

    public void Start()
    {
        if (_writer != null) throw new InvalidOperationException("Already started.");
        _writer = new Thread(WriterLoop) { IsBackground = true, Name = "CH9329 writer" };
        _reader = new Thread(ReaderLoop) { IsBackground = true, Name = "CH9329 reader" };
        _writer.Start();
        _reader.Start();
    }

    // ---------------------------------------------------------------- sending

    public void SendKeyboard(KeyboardReport report) => EnqueueOrdered(Ch9329Protocol.Keyboard(report));

    private void EnqueueOrdered(byte[] frame)
    {
        lock (_gate)
        {
            _ordered.Enqueue(frame);
            _signal.Release();
        }
    }

    public void SendKeyboardSequence(IEnumerable<KeyboardReport> reports)
    {
        lock (_gate)
        {
            int n = 0;
            foreach (var r in reports) { _ordered.Enqueue(Ch9329Protocol.Keyboard(r)); n++; }
            if (n > 0) _signal.Release(n);
        }
    }

    /// <summary>Absolute pointer position (0..4095). Pure moves are coalesced.</summary>
    public void MoveAbsolute(MouseButtons buttons, int x, int y)
    {
        var frame = Ch9329Protocol.MouseAbsolute(buttons, x, y, 0);
        lock (_gate)
        {
            bool wasEmpty = _pendingAbsMove == null;
            _pendingAbsMove = frame;
            if (wasEmpty) _signal.Release();
        }
    }

    /// <summary>Button change or wheel at an absolute position. Never coalesced; supersedes any pending move.</summary>
    public void ButtonsAbsolute(MouseButtons buttons, int x, int y, sbyte wheel = 0)
    {
        var frame = Ch9329Protocol.MouseAbsolute(buttons, x, y, wheel);
        lock (_gate)
        {
            _pendingAbsMove = null; // this frame carries the same-or-newer position
            _ordered.Enqueue(frame);
            _signal.Release();
        }
    }

    /// <summary>Relative motion; accumulated and chunked so nothing is lost.</summary>
    public void MoveRelative(MouseButtons buttons, int dx, int dy)
    {
        lock (_gate)
        {
            bool wasIdle = _relDx == 0 && _relDy == 0;
            _relDx += dx;
            _relDy += dy;
            _relButtons = buttons;
            if (wasIdle && (_relDx != 0 || _relDy != 0)) _signal.Release();
        }
    }

    /// <summary>Relative button/wheel change. Pending motion is flushed first so the click lands where expected.</summary>
    public void ButtonsRelative(MouseButtons buttons, sbyte wheel = 0)
    {
        lock (_gate)
        {
            int released = 0;
            while (_relDx != 0 || _relDy != 0) { _ordered.Enqueue(TakeRelativeChunk_NoLock()); released++; }
            _relButtons = buttons;
            _ordered.Enqueue(Ch9329Protocol.MouseRelative(buttons, 0, 0, wheel));
            _signal.Release(released + 1);
        }
    }

    /// <summary>Queue a CMD_GET_INFO and await the reply.</summary>
    public async Task<Ch9329Info?> GetInfoAsync(TimeSpan timeout)
    {
        TaskCompletionSource<Ch9329Info> tcs;
        lock (_gate)
        {
            _infoWaiter ??= new TaskCompletionSource<Ch9329Info>(TaskCreationOptions.RunContinuationsAsynchronously);
            tcs = _infoWaiter;
            _ordered.Enqueue(Ch9329Protocol.GetInfo());
            _signal.Release();
        }
        var done = await Task.WhenAny(tcs.Task, Task.Delay(timeout)).ConfigureAwait(false);
        if (done == tcs.Task) return await tcs.Task.ConfigureAwait(false);
        lock (_gate) { if (_infoWaiter == tcs) _infoWaiter = null; }
        return null;
    }

    /// <summary>Releases every key and mouse button on the target.</summary>
    public void ReleaseEverything(bool absoluteMode, int lastX = 0, int lastY = 0)
    {
        lock (_gate)
        {
            _pendingAbsMove = null;
            _relDx = _relDy = 0;
            _ordered.Enqueue(Ch9329Protocol.Keyboard(KeyboardReport.Empty));
            _ordered.Enqueue(absoluteMode
                ? Ch9329Protocol.MouseAbsolute(MouseButtons.None, lastX, lastY, 0)
                : Ch9329Protocol.MouseRelative(MouseButtons.None, 0, 0, 0));
            _signal.Release(2);
        }
    }

    // ---------------------------------------------------------------- threads

    private byte[] TakeRelativeChunk_NoLock()
    {
        int cx = Math.Clamp(_relDx, -127, 127);
        int cy = Math.Clamp(_relDy, -127, 127);
        _relDx -= cx;
        _relDy -= cy;
        return Ch9329Protocol.MouseRelative(_relButtons, (sbyte)cx, (sbyte)cy, 0);
    }

    private byte[]? TakeNext()
    {
        lock (_gate)
        {
            if (_ordered.Count > 0) return _ordered.Dequeue();
            if (_pendingAbsMove != null) { var f = _pendingAbsMove; _pendingAbsMove = null; return f; }
            if (_relDx != 0 || _relDy != 0) return TakeRelativeChunk_NoLock();
            return null;
        }
    }

    private void WriterLoop()
    {
        var ct = _cts.Token;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                _signal.Wait(ct);
                // Drain everything available; extra semaphore counts just cause harmless empty passes.
                while (TakeNext() is { } frame)
                {
                    _transport.Write(frame, 0, frame.Length);
                    FramesSent++;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Faulted?.Invoke(ex); }
    }

    private void ReaderLoop()
    {
        var ct = _cts.Token;
        var buf = new byte[256];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int n = _transport.Read(buf, 0, buf.Length);
                if (n <= 0) continue;
                foreach (var reply in _parser.Feed(buf.AsSpan(0, n))) HandleReply(reply);
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested) { Faulted?.Invoke(ex); }
        catch { /* shutting down */ }
    }

    private void HandleReply(Ch9329Reply reply)
    {
        if (reply.IsError) ErrorReplies++;
        if (Ch9329Info.TryParse(reply, out var info) && info != null)
        {
            LastInfo = info;
            TaskCompletionSource<Ch9329Info>? waiter;
            lock (_gate) { waiter = _infoWaiter; _infoWaiter = null; }
            waiter?.TrySetResult(info);
        }
        ReplyReceived?.Invoke(reply);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _writer?.Join(500);
        _reader?.Join(500);
        _transport.Dispose();
        _cts.Dispose();
    }
}
