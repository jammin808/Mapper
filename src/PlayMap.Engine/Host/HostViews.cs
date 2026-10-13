namespace PlayMap.Engine;

/// <summary>A part of the node that is not doing its job, in words an operator acts on: a transport whose port was
/// refused, a render host that failed, a project that would not open. STATE lists them under "faults" and the
/// context shows each as a red item, so a node that stayed up on its other doors says what it is missing.</summary>
public sealed record NodeFault(string Part, string Words);

/// <summary>The faults the host knows of now (read on any thread; an empty list when all is well).</summary>
public interface INodeFaults
{
    IReadOnlyList<NodeFault> Faults();
}

/// <summary>One media pull between nodes as STATE shows it: the library item and its file by hash, who it comes from,
/// how far it is, and its state ("queued", "pulling", "failed: …").</summary>
public sealed record MediaTransferView(string Hash, string MediaId, string Name, string From, long BytesDone, long Size, string State)
{
    /// <summary>The fraction pulled, 0 to 1.</summary>
    public double Progress => Size > 0 ? Math.Clamp(BytesDone / (double)Size, 0, 1) : 0;

    /// <summary>Still on its way (not finished, not failed).</summary>
    public bool Moving => State is "queued" or "pulling";
}

/// <summary>The media pulls the network edge runs (MEDIA FETCH), for STATE's media.transfers and MEDIA LIST.</summary>
public interface IMediaTransfers
{
    IReadOnlyList<MediaTransferView> Transfers();
}

/// <summary>The node's pairing token, kept where the node keeps its settings: PAIRING NEW makes a new one, writes it
/// and hands it to every transport at once (a peer paired with the old one pairs again). The token is never answered
/// on a line — the words say where to read it.</summary>
public interface INodePairing
{
    string NewToken();
}

/// <summary>The assistant's door as STATE.ai shows it: whether MCP is served, and how many assistants and screens are on
/// the web port now (MCP sessions heard in the last minute and open WebSockets).</summary>
public interface IAiDoor
{
    bool Mcp { get; }

    int Clients { get; }
}

/// <summary>The assistant's proposals waiting for a person (STATE.ai.proposals): the assistant side supplies it as
/// <see cref="EngineServices.Proposals"/>; without one, none are waiting.</summary>
public interface IProposalCount
{
    int Waiting { get; }
}

/// <summary>The host's views and controls beside the engine's positional services. Null on an engine built without a
/// host (tests, a bare core).</summary>
public sealed partial record EngineServices
{
    public INodeFaults? Faults { get; init; }

    public IMediaTransfers? Transfers { get; init; }

    public INodePairing? Pairing { get; init; }

    public IAiDoor? Ai { get; init; }

    /// <summary>The assistant side's count of proposals waiting (set in its extension's Configure).</summary>
    public IProposalCount? Proposals { get; init; }
}
