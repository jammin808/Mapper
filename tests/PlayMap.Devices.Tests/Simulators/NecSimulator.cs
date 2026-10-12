using PlayMap.Devices.Drivers.Nec;

namespace PlayMap.Devices.Tests.Simulators;

/// <summary>
/// An NEC projector's binary control as the driver understands it: frames of command bytes, id, model code,
/// length, data and checksum; a bad checksum is dropped; known commands are acknowledged (first byte + 0x20), an
/// unknown one refused (first byte + 0xA0, error 00 00); the running status carries power in its third data byte.
/// </summary>
public sealed class NecSimulator : FakeTcpServer
{
    private readonly Lock _gate = new();

    public bool On { get; set; }

    public bool PictureMuted { get; set; }

    public int BadChecksums { get; private set; }

    protected override async Task ServeAsync(Stream stream, CancellationToken ct)
    {
        while (true)
        {
            var head = await ReadExactlyAsync(stream, 5, ct);
            if (head is null)
                return;
            var rest = await ReadExactlyAsync(stream, head[4] + 1, ct);
            if (rest is null)
                return;
            byte[] frame = [.. head, .. rest];
            if (NecProtocol.Checksum(frame.AsSpan(0, frame.Length - 1)) != frame[^1])
            {
                lock (_gate)
                    BadChecksums++;
                continue;
            }
            Received.Enqueue(Convert.ToHexString(frame));
            await WriteAsync(stream, Answer(frame), ct);
        }
    }

    private byte[] Answer(byte[] f)
    {
        lock (_gate)
        {
            byte[] Ack(params byte[] data) => Reply((byte)(f[0] + 0x20), f[1], data);
            switch ((f[0], f[1]))
            {
                case (0x02, 0x00):
                    On = true;
                    return Ack();
                case (0x02, 0x01):
                    On = false;
                    return Ack();
                case (0x02, 0x10) or (0x02, 0x11):
                    if (!On)
                        return Reply((byte)(f[0] + 0xA0), f[1], 0x02, 0x0D);
                    PictureMuted = f[1] == 0x10;
                    return Ack();
                case (0x01, 0x98):
                    return Ack();
                case (0x00, 0x85):
                    var data = new byte[16];
                    data[2] = (byte)(On ? 0x01 : 0x00);
                    return Ack(data);
                default:
                    return Reply((byte)(f[0] + 0xA0), f[1], 0x00, 0x00);
            }
        }
    }

    private static byte[] Reply(byte c0, byte c1, params byte[] data)
    {
        var bytes = new byte[5 + data.Length + 1];
        bytes[0] = c0;
        bytes[1] = c1;
        bytes[2] = 0x01;
        bytes[4] = (byte)data.Length;
        data.CopyTo(bytes, 5);
        bytes[^1] = NecProtocol.Checksum(bytes.AsSpan(0, bytes.Length - 1));
        return bytes;
    }
}
