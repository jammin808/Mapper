using System.Diagnostics;
using PlayMap.Core.Media;

namespace PlayMap.Media.Video;

/// <summary>What every network sender shares: one worker thread per send ("send &lt;name&gt;") and a latest-wins box of one frame.
/// <see cref="Offer"/> never blocks: a frame still waiting when a newer one comes is released at once and counted dropped. The worker
/// hands each frame to <see cref="Transmit"/>; a protocol that keeps reading the frame's memory after the call returns (NDI's
/// asynchronous send) says so and the frame is released at the protocol's next synchronising call, otherwise at once. Every
/// 250 ms, frames or not, the worker asks the protocol for its connections and tally (<see cref="Refresh"/>). The render thread only
/// offers; it never waits on the network.</summary>
public abstract class SendSink : ISendSink
{
    private const int RefreshMs = 250;
    private const int EncodeRing = 64;
    private const int HoldMs = 200;

    private readonly Thread _thread;
    private readonly AutoResetEvent _wake = new(false);
    private readonly double[] _encodeMs = new double[EncodeRing];
    private readonly double[] _encodeSorted = new double[EncodeRing];
    private readonly Lock _statsGate = new();
    private SendFrame? _pending;
    private SendFrame? _held;
    private long _heldSince;
    private long _sent;
    private long _dropped;
    private long _bytes;
    private long _windowBytes;
    private long _windowStart = Stopwatch.GetTimestamp();
    private double _mbps;
    private int _encodes;
    private volatile bool _stopping;
    private int _disposed;

    protected SendSink(SendSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        Spec = spec;
        _thread = new Thread(Loop) { IsBackground = true, Name = "send " + spec.Name };
    }

    /// <summary>What the sender was last asked to send.</summary>
    public SendSpec Spec { get; private set; }

    public abstract string FullName { get; }

    public int Connections { get; protected set; }

    public (bool Program, bool Preview) Tally { get; protected set; }

    public string Words { get; protected set; } = "";

    public SendStats Stats
    {
        get
        {
            lock (_statsGate)
            {
                var n = Math.Min(_encodes, EncodeRing);
                double p95 = 0;
                if (n > 0)
                {
                    Array.Copy(_encodeMs, _encodeSorted, n);
                    Array.Sort(_encodeSorted, 0, n);
                    p95 = Math.Round(_encodeSorted[Math.Min(n - 1, (int)Math.Ceiling(n * 0.95) - 1)], 2);
                }
                return new SendStats(Volatile.Read(ref _sent), Volatile.Read(ref _dropped), p95, _mbps, Volatile.Read(ref _bytes));
            }
        }
    }

    /// <summary>Starts the worker; the derived constructor calls it once the sender exists.</summary>
    protected void Start() => _thread.Start();

    public void Offer(SendFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (_stopping)
        {
            frame.Release();
            Interlocked.Increment(ref _dropped);
            return;
        }
        if (Interlocked.Exchange(ref _pending, frame) is { } older)
        {
            older.Release();
            Interlocked.Increment(ref _dropped);
        }
        _wake.Set();
    }

    public void Update(SendSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        Spec = spec;
        OnUpdate(spec);
    }

    /// <summary>Worker thread: sends one packed frame. True when the protocol keeps reading its memory after returning (it is released
    /// at the next <see cref="Transmit"/>, <see cref="Flush"/> or close), false when it is done with it.</summary>
    protected abstract bool Transmit(SendFrame frame);

    /// <summary>Worker thread: the protocol's last frame is no longer read (a synchronising call with no frame) before the held frame
    /// is released on close.</summary>
    protected virtual void Flush()
    {
        // A protocol that never keeps a frame past its send has nothing to flush.
    }

    /// <summary>Worker thread, every 250 ms: connections and tally from the protocol.</summary>
    protected abstract void Refresh();

    /// <summary>Applies a changed spec in place.</summary>
    protected virtual void OnUpdate(SendSpec spec)
    {
        // A protocol with nothing to change in place keeps the default.
    }

    /// <summary>Worker thread, last: closes the sender.</summary>
    protected abstract void Close();

    private void Loop()
    {
        var nextRefresh = Stopwatch.GetTimestamp();
        while (!_stopping)
        {
            _wake.WaitOne(RefreshMs);
            if (Interlocked.Exchange(ref _pending, null) is { } frame && !_stopping)
                Send(frame);
            var now = Stopwatch.GetTimestamp();
            // A frame the protocol keeps reading is let go when no newer frame came for a while (the protocol's synchronising call
            // with no frame first), so a quiet send never holds the producer's memory for long.
            if (_held is { } idle && now - _heldSince > Stopwatch.Frequency * HoldMs / 1000)
            {
                FlushQuietly();
                _held = null;
                idle.Release();
            }
            if (now >= nextRefresh)
            {
                nextRefresh = now + Stopwatch.Frequency * RefreshMs / 1000;
                RefreshQuietly();
                RollWindow(now);
            }
        }
        if (Interlocked.Exchange(ref _pending, null) is { } left)
        {
            left.Release();
            Interlocked.Increment(ref _dropped);
        }
        if (_held is { } held)
        {
            FlushQuietly();
            _held = null;
            held.Release();
        }
        Close();
    }

    private void Send(SendFrame frame)
    {
        var t0 = Stopwatch.GetTimestamp();
        bool keeps;
        try
        {
            keeps = Transmit(frame);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException or System.Net.Sockets.SocketException or ObjectDisposedException)
        {
            Words = ex.Message;
            frame.Release();
            Interlocked.Increment(ref _dropped);
            return;
        }
        var ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        // The previous frame the protocol was still reading is free now: this call was its synchronising event.
        if (_held is { } previous)
        {
            _held = null;
            previous.Release();
        }
        if (keeps)
        {
            _held = frame;
            _heldSince = Stopwatch.GetTimestamp();
        }
        else
            frame.Release();
        Interlocked.Increment(ref _sent);
        Interlocked.Add(ref _bytes, frame.Bytes);
        Interlocked.Add(ref _windowBytes, frame.Bytes);
        lock (_statsGate)
        {
            _encodeMs[_encodes % EncodeRing] = ms;
            _encodes++;
        }
    }

    private void RollWindow(long now)
    {
        var seconds = (now - _windowStart) / (double)Stopwatch.Frequency;
        if (seconds < 1)
            return;
        var bytes = Interlocked.Exchange(ref _windowBytes, 0);
        lock (_statsGate)
            _mbps = Math.Round(bytes * 8 / seconds / 1e6, 1);
        _windowStart = now;
    }

    private void RefreshQuietly()
    {
        try
        {
            Refresh();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Net.Sockets.SocketException or ObjectDisposedException)
        {
            Words = ex.Message;
        }
    }

    private void FlushQuietly()
    {
        try
        {
            Flush();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Net.Sockets.SocketException or ObjectDisposedException)
        {
            Words = ex.Message;
        }
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!disposing || Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _stopping = true;
        _wake.Set();
        if (_thread.ThreadState != System.Threading.ThreadState.Unstarted)
            _thread.Join();
        else
            Close();
        _wake.Dispose();
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }
}
