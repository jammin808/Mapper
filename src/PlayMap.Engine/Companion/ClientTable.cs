using System.Collections.Immutable;
using System.Text.Json;
using PlayMap.Core.Wire;

namespace PlayMap.Engine.Companion;

/// <summary>One controller on the line wire as STATE.clients shows it: its session, what it said in HELLO, how it came (transport and
/// channel), whether it paired, and what it told the node in PRESENCE.</summary>
public sealed record ClientView(
    string Session,
    string Name,
    string Kind,
    string Transport,
    string Channel,
    string Address,
    bool Paired,
    long SinceUnixMs)
{
    public string? Module { get; init; }

    public string? Where { get; init; }

    public bool Recording { get; init; }

    public int Thumbs { get; init; }
}

/// <summary>The controllers the node knows now: added on HELLO, changed by pairing and PRESENCE, removed when the session ends. Any
/// thread; readers take the immutable list.</summary>
public sealed class ClientTable
{
    /// <summary>PRESENCE is a short note, not a channel: at most this many bytes.</summary>
    public const int MaxPresenceBytes = 2048;

    /// <summary>A HELLO name is said before pairing, so STATE and the context keep at most this many characters of it: a client that
    /// never paired cannot grow what every other client is sent.</summary>
    public const int MaxNameChars = 64;

    private static readonly ImmutableHashSet<string> PresenceKeys =
        ImmutableHashSet.Create(StringComparer.Ordinal, "kind", "module", "live", "page", "recording", "thumbs", "where");

    private readonly Lock _gate = new();
    private ImmutableSortedDictionary<string, ClientView> _clients = ImmutableSortedDictionary.Create<string, ClientView>(StringComparer.Ordinal);

    /// <summary>Raised after any change (STATE moves).</summary>
    public event Action? Changed;

    public IReadOnlyList<ClientView> All => [.. _clients.Values];

    public ClientView? Find(string session) => _clients.GetValueOrDefault(session);

    public static string KindOf(SessionInfo session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return session.Transport switch
        {
            "companion" => "companion",
            "desk" => "desk",
            "stagewatch" => "stagewatch",
            "patterns-device" => "patterns",
            _ => "wire",
        };
    }

    public void Hello(SessionInfo session, string channel, long nowUnixMs)
    {
        ArgumentNullException.ThrowIfNull(session);
        lock (_gate)
        {
            var since = _clients.TryGetValue(session.SessionId, out var old) ? old.SinceUnixMs : nowUnixMs;
            var name = Clip(session.ClientName);
            var view = new ClientView(session.SessionId, name, KindOf(session), session.Transport, channel, session.RemoteAddress,
                session.Paired, since)
            {
                Module = old?.Module,
                Where = old?.Where,
                Recording = old?.Recording ?? false,
                Thumbs = old?.Thumbs ?? 0,
            };
            if (view == old)
                return;
            _clients = _clients.SetItem(session.SessionId, view);
        }
        Changed?.Invoke();
    }

    private static string Clip(string name)
    {
        if (name.Length <= MaxNameChars)
            return name;
        var n = char.IsHighSurrogate(name[MaxNameChars - 1]) ? MaxNameChars - 1 : MaxNameChars;
        return string.Concat(name.AsSpan(0, n), "…");
    }

    public void Gone(string session)
    {
        lock (_gate)
        {
            if (!_clients.ContainsKey(session))
                return;
            _clients = _clients.Remove(session);
        }
        Changed?.Invoke();
    }

    /// <summary>Takes a PRESENCE note: null when taken, else why not (too long, not one JSON object, an unknown key, a value of the
    /// wrong shape, or a session that never said HELLO).</summary>
    public string? Presence(string session, string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaxPresenceBytes)
            return $"PRESENCE is a short note of at most {MaxPresenceBytes} bytes";
        string? module = null, where = null, live = null, page = null;
        bool? recording = null;
        int? thumbs = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return "PRESENCE takes one JSON object: {\"kind\",\"module\",\"live\",\"page\",\"recording\",\"thumbs\",\"where\"}";
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (!PresenceKeys.Contains(p.Name))
                    return $"PRESENCE knows no key '{p.Name}' — kind, module, live, page, recording, thumbs, where";
                switch (p.Name)
                {
                    case "recording":
                        if (p.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                            return "PRESENCE recording is true or false";
                        recording = p.Value.GetBoolean();
                        break;
                    case "thumbs":
                        if (p.Value.ValueKind != JsonValueKind.Number || !p.Value.TryGetInt32(out var n) || n < 0)
                            return "PRESENCE thumbs is a count";
                        thumbs = n;
                        break;
                    default:
                        if (p.Value.ValueKind != JsonValueKind.String)
                            return $"PRESENCE {p.Name} is text";
                        var text = p.Value.GetString()!;
                        if (p.Name == "module")
                            module = text;
                        else if (p.Name == "where")
                            where = text;
                        else if (p.Name == "live")
                            live = text;
                        else if (p.Name == "page")
                            page = text;
                        break;
                }
            }
        }
        catch (JsonException)
        {
            return "PRESENCE takes one JSON object";
        }
        lock (_gate)
        {
            if (!_clients.TryGetValue(session, out var old))
                return "PRESENCE follows HELLO: this session has not said who it is";
            where ??= live is null ? old.Where : page is null ? "Live · " + live : $"Live · {live} {page}";
            _clients = _clients.SetItem(session, old with
            {
                Module = module ?? old.Module,
                Where = where,
                Recording = recording ?? old.Recording,
                Thumbs = thumbs ?? old.Thumbs,
            });
        }
        Changed?.Invoke();
        return null;
    }

    /// <summary>STATE.clients: one object per controller, in session order.</summary>
    public void Write(Utf8JsonWriter w)
    {
        ArgumentNullException.ThrowIfNull(w);
        w.WriteStartArray("clients");
        foreach (var c in _clients.Values)
        {
            w.WriteStartObject();
            w.WriteString("session", c.Session);
            w.WriteString("name", c.Name);
            w.WriteString("kind", c.Kind);
            w.WriteString("transport", c.Transport);
            w.WriteString("channel", c.Channel);
            w.WriteString("address", c.Address);
            w.WriteBoolean("paired", c.Paired);
            if (c.Module is null)
                w.WriteNull("module");
            else
                w.WriteString("module", c.Module);
            w.WriteNumber("since", c.SinceUnixMs);
            w.WriteBoolean("recording", c.Recording);
            if (c.Where is null)
                w.WriteNull("where");
            else
                w.WriteString("where", c.Where);
            w.WriteNumber("thumbs", c.Thumbs);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }
}
