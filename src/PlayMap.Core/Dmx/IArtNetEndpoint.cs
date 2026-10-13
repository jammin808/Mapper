namespace PlayMap.Core.Dmx;

/// <summary>A received Art-Net packet: <paramref name="packet"/> and <paramref name="from"/> are valid only during the call (the
/// receive buffer and address are reused); copy what you keep.</summary>
public delegate void ArtNetReceived(ReadOnlySpan<byte> packet, System.Net.SocketAddress from);

/// <summary>The node's one socket on UDP 6454 (Art-Net 4), shared by control input (ArtDmx, ArtSync, ArtTimeCode, ArtPoll) and LED
/// output (ArtDmx, ArtSync, ArtPoll). Opened when any consumer needs it (DMX input, an ARTNET timecode source, LED output) and bound
/// to the configured address. Subscribers demux by OpCode. Send never blocks and, once warm, never allocates: it uses the .NET
/// `SendTo(ReadOnlySpan&lt;byte&gt;, SocketFlags, SocketAddress)` overload with one cached SocketAddress per destination, and
/// receive uses `ReceiveFrom(Span&lt;byte&gt;, SocketFlags, SocketAddress)` into one reused buffer.</summary>
public interface IArtNetEndpoint : IAsyncDisposable
{
    event ArtNetReceived? Received;
    /// <summary>A destination's cached address (made once per destination, off the send path).</summary>
    System.Net.SocketAddress AddressOf(System.Net.IPEndPoint to);
    bool Send(ReadOnlySpan<byte> packet, System.Net.SocketAddress to);
    /// <summary>The interface it is bound to (null = all).</summary>
    System.Net.IPAddress? Interface { get; }
    /// <summary>Why it is not bound as asked ("UDP 6454 is in use by another program — sending from an ephemeral port"), or null.</summary>
    string? Problem { get; }
}
