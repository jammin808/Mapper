using System.Net;
using System.Net.Sockets;
using System.Text;
using PlayMap.Devices.Drivers.PjLink;

namespace PlayMap.Devices.Tests.Simulators;

/// <summary>
/// A PJLink projector per the JBMIA grammar: greets "PJLINK 0" or "PJLINK 1 seed", checks the MD5 digest in front
/// of the first command ("PJLINK ERRA" and hang up when it is wrong), answers "%1POWR=OK", values and ERR1–ERR4.
/// Class 1 answers class 2 commands with ERR1. Power warms up through state 3 when <see cref="WarmUpSteps"/> &gt; 0.
/// </summary>
public sealed class PjLinkSimulator : FakeTcpServer
{
    private readonly Lock _gate = new();

    public PjLinkSimulator(string password = "", int pjClass = 2, string seed = "498e4a67")
    {
        Password = password;
        Class = pjClass;
        Seed = seed;
    }

    public string Password { get; }

    public int Class { get; }

    public string Seed { get; }

    public string Power { get; set; } = "0";

    public string Avmt { get; set; } = "30";

    public string Input { get; set; } = "31";

    public string Erst { get; set; } = "000000";

    public string Lamp { get; set; } = "1234 1";

    public string Freeze { get; set; } = "0";

    public string Ires { get; set; } = "1920x1080";

    /// <summary>Answer every POWR set with ERR3 (warming, cooling, busy).</summary>
    public bool PowerBusy { get; set; }

    /// <summary>Power reads "3" (warming) this many times after POWR 1 before it reads "1".</summary>
    public int WarmUpSteps { get; set; }

    /// <summary>The digests the simulator was sent (to prove one rode in front of the first command only).</summary>
    public List<string> Digests { get; } = [];

    private int _warming;

    protected override async Task ServeAsync(Stream stream, CancellationToken ct)
    {
        var protectedMode = Password.Length > 0;
        await WriteAsync(stream, protectedMode ? $"PJLINK 1 {Seed}\r" : "PJLINK 0\r", ct);
        var first = true;
        while (true)
        {
            var line = await ReadUntilAsync(stream, (byte)'\r', ct);
            if (line is null)
                return;
            if (first && protectedMode)
            {
                var expected = PjLinkProtocol.Digest(Seed, Password);
                if (line.Length < 32 || !line[..32].Equals(expected, StringComparison.Ordinal))
                {
                    await WriteAsync(stream, "PJLINK ERRA\r", ct);
                    return;
                }
                lock (_gate)
                    Digests.Add(line[..32]);
                line = line[32..];
            }
            first = false;
            Received.Enqueue(line);
            await WriteAsync(stream, Answer(line) + "\r", ct);
        }
    }

    private string Answer(string line)
    {
        if (line.Length < 6 || line[0] != '%')
            return "%1XXXX=ERR1";
        var cls = line[1];
        var cmd = line[2..6];
        var arg = line.Length > 7 ? line[7..] : "";
        var head = "%" + cls + cmd + "=";
        if (cls == '2' && Class < 2)
            return head + "ERR1";
        lock (_gate)
        {
            switch (cmd)
            {
                case "POWR":
                    if (arg == "?")
                    {
                        if (Power == "3" && _warming > 0 && --_warming == 0)
                            Power = "1";
                        return head + Power;
                    }
                    if (PowerBusy)
                        return head + "ERR3";
                    if (arg == "1")
                    {
                        _warming = WarmUpSteps;
                        Power = WarmUpSteps > 0 ? "3" : "1";
                        return head + "OK";
                    }
                    if (arg == "0")
                    {
                        Power = "0";
                        return head + "OK";
                    }
                    return head + "ERR2";
                case "AVMT":
                    if (arg == "?")
                        return Power == "1" ? head + Avmt : head + "ERR3";
                    if (arg is "10" or "11" or "20" or "21" or "30" or "31")
                    {
                        Avmt = arg;
                        return head + "OK";
                    }
                    return head + "ERR2";
                case "INPT":
                    if (arg == "?")
                        return head + Input;
                    if (arg is "11" or "31" or "32" or "3A" or "51")
                    {
                        Input = arg;
                        return head + "OK";
                    }
                    return head + "ERR2";
                case "ERST":
                    return head + Erst;
                case "LAMP":
                    return head + Lamp;
                case "NAME":
                    return head + "Stage Left";
                case "INF1":
                    return head + "PANASONIC";
                case "INF2":
                    return head + "PT-RZ120";
                case "INFO":
                    return head + "sim";
                case "CLSS":
                    return head + Class.ToString(System.Globalization.CultureInfo.InvariantCulture);
                case "SNUM":
                    return head + "SH1234567";
                case "SVER":
                    return head + "1.02";
                case "IRES":
                    return head + Ires;
                case "FREZ":
                    if (arg == "?")
                        return head + Freeze;
                    if (arg is "0" or "1")
                    {
                        Freeze = arg;
                        return head + "OK";
                    }
                    return head + "ERR2";
                default:
                    return head + "ERR1";
            }
        }
    }
}

/// <summary>The UDP side of a class 2 projector: answers "%2SRCH" with "%2ACKN=mac" to the searcher, and sends
/// notifications ("%2LKUP=mac") to a controller on demand.</summary>
public sealed class PjLinkUdpSimulator : IDisposable
{
    private readonly UdpClient _udp = new(new IPEndPoint(IPAddress.Loopback, 0));
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    public PjLinkUdpSimulator(string mac)
    {
        Mac = mac;
        _loop = Task.Run(LoopAsync, CancellationToken.None);
    }

    public string Mac { get; }

    public IPEndPoint EndPoint => (IPEndPoint)_udp.Client.LocalEndPoint!;

    public Task SendAsync(string datagram, IPEndPoint to) =>
        _udp.SendAsync(Encoding.ASCII.GetBytes(datagram), to, _stop.Token).AsTask();

    private async Task LoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            UdpReceiveResult r;
            try
            {
                r = await _udp.ReceiveAsync(_stop.Token);
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }
            if (Encoding.ASCII.GetString(r.Buffer).StartsWith("%2SRCH", StringComparison.Ordinal))
                await _udp.SendAsync(Encoding.ASCII.GetBytes($"%2ACKN={Mac}\r"), r.RemoteEndPoint, _stop.Token);
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _udp.Dispose();
        _loop.GetAwaiter().GetResult();
        _stop.Dispose();
    }
}
