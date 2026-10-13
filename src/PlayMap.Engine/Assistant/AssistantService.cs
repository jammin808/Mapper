using System.Globalization;
using System.Text.Json;
using PlayMap.Assistant;
using PlayMap.Core.Wire;
using PlayMap.Devices.Model;
using PlayMap.Engine.Devices;

namespace PlayMap.Engine.Assistant;

/// <summary>How the assistant service makes its parts (tests hand in a provider of their own).</summary>
public sealed record AssistantServiceOptions
{
    /// <summary>Makes the model provider from the settings and the key (null: <see cref="AssistantProviders.Create"/>).</summary>
    public Func<AssistantSettings, string?, IAssistantProvider?>? Providers { get; init; }

    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>How long a proposal waits for a person before it expires (null: the queue's default, 15 min).</summary>
    public TimeSpan? ProposalLifetime { get; init; }
}

/// <summary>One conversation as STATE and the desk see it: whose, how its last request ended, its words (secrets
/// masked), what it queued.</summary>
public sealed record ConversationView(string Id, string Client, bool Busy, string Outcome, string Answer, string Status, IReadOnlyList<string> Queued, long AtUnixMs);

/// <summary>
/// The node's assistant inside the engine: one <see cref="ProposalQueue"/> for the node (every conversation, MCP's
/// high-risk lines and a bus approval desk share it) and one <see cref="AssistantSession"/> per client (a desk, a
/// wire peer — by address and name). The model's key comes from the credential store ("assistant") and goes to
/// the provider alone: it is never logged, journalled, shown or put in a reply, and every word that leaves the
/// service passes a redactor that knows the node's secrets. Built before the engine; the queue exists from the
/// start (its lines reach the engine once attached), so the host can hand it to its approvals before then.
/// </summary>
public sealed class AssistantService : IAsyncDisposable
{
    private readonly ICredentialStore _credentials;
    private readonly AssistantServiceOptions _options;
    private readonly DeferredEndpoint _endpoint = new();
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Conversation> _conversations = new(StringComparer.Ordinal);
    private readonly List<IAssistantProvider> _providers = [];
    private IAssistantProvider? _provider;
    private long _providerKeyVersion = -1;
    private EngineCore? _engine;
    private StagewatchProposalSink? _desk;
    private long _mcpBatches;

    public AssistantService(ICredentialStore credentials, AssistantSettings settings, AssistantServiceOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(settings);
        _credentials = credentials;
        _options = options ?? new AssistantServiceOptions();
        Settings = settings.Normalised();
        Queue = new ProposalQueue(_endpoint, null, _options.Time, _options.ProposalLifetime);
        Queue.Changed += _ => _engine?.FactsChanged();
        Queue.Logged += line => Journal(line, true);
        Approvals = new QueueApprovals(this);
    }

    public AssistantSettings Settings { get; }

    /// <summary>Where the model's key is kept (under <see cref="NodeCredentialStore.AssistantKey"/>).</summary>
    public ICredentialStore Credentials => _credentials;

    /// <summary>The node's approval list (one per node).</summary>
    public ProposalQueue Queue { get; }

    /// <summary>The queue as Net's approvals take it (<c>HttpHostOptions.Approvals = new SwitchApprovalPolicy(() =>
    /// aiMayAct, assistant.Approvals)</c>): an MCP assistant's high-risk line waits on this node's list.</summary>
    public PlayMap.Net.IProposalSink Approvals { get; }

    /// <summary>The bus approval desk proposals go to, while one is used.</summary>
    public StagewatchProposalSink? ApprovalDesk => _desk;

    public IReadOnlyList<ConversationView> Conversations
    {
        get
        {
            lock (_gate)
                return _conversations.Values.Select(c => c.View()).OrderBy(v => v.Id, StringComparer.Ordinal).ToList();
        }
    }

    /// <summary>Attaches to an engine: the queue's lines run through it, the journal hears what the assistant did,
    /// STATE moves with the proposals. Disposing the result detaches.</summary>
    public IAsyncDisposable Attach(EngineCore engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        lock (_gate)
        {
            if (_engine is not null)
                throw new InvalidOperationException("the assistant is attached to an engine already");
            _engine = engine;
        }
        _endpoint.Bind(engine);
        return new Detach(this);
    }

    /// <summary>Sends this node's proposals to a Stagewatch approval desk and pulls its decisions (Show Bus §11);
    /// null stops using one (proposals stay on this node's list).</summary>
    public async Task UseApprovalDeskAsync(StagewatchSinkOptions? options)
    {
        StagewatchProposalSink? old;
        StagewatchProposalSink? next = null;
        if (options is not null)
            next = new StagewatchProposalSink(options, Queue, _endpoint, line => Journal(line, true));
        lock (_gate)
        {
            old = _desk;
            _desk = next;
        }
        Queue.Sink = next;
        next?.Start();
        if (old is not null)
            await old.DisposeAsync().ConfigureAwait(false);
        _engine?.FactsChanged();
    }

    /// <summary>A redactor that knows every secret the node holds now (its pairing token, link keys, the credential
    /// store's secrets) besides the shapes of keys.</summary>
    public SecretRedactor Redactor() => new(KnownSecrets());

    private List<string> KnownSecrets()
    {
        var secrets = new List<string>();
        if (_credentials is NodeCredentialStore store)
            secrets.AddRange(store.Secrets());
        else if (_credentials.Find(NodeCredentialStore.AssistantKey) is { } key)
            secrets.Add(key.Secret);
        if (_engine?.Services.Settings is { } s)
            secrets.AddRange([s.Token, s.PatternsToken, s.StagewatchKey]);
        return secrets;
    }

    /// <summary>One request from a client: its conversation (made on first use) runs the loop and answers. Not sent
    /// — with the reason — when the assistant is off, or the client's last request is still running.</summary>
    public async Task<(Conversation? Conversation, AssistantTurnResult Result)> AskAsync(SessionInfo client, string text, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (_engine is null)
            return (null, NotSent("the assistant is not attached to a node"));
        if (!Settings.Enabled || Settings.Provider == AssistantProviderKind.None)
            return (null, NotSent("the assistant is off on this node — the desk's Explain works without it"));
        var conversation = ConversationFor(client);
        if (conversation is null)
            return (null, NotSent("no model could be reached with these settings"));
        var result = await conversation.Session.AskAsync(text, ct).ConfigureAwait(false);
        conversation.Record(result, _options.Time.GetUtcNow().ToUnixTimeMilliseconds(), Redactor());
        _engine?.FactsChanged();
        return (conversation, result);
    }

    private static AssistantTurnResult NotSent(string why) => new(TurnOutcome.NotSent, why, [], default, 0, why);

    private Conversation? ConversationFor(SessionInfo client)
    {
        var key = client.RemoteAddress + "|" + client.ClientName;
        lock (_gate)
        {
            var provider = Provider();
            if (provider is null)
                return null;
            if (_conversations.TryGetValue(key, out var c) && ReferenceEquals(c.Provider, provider))
                return c;
            var session = new AssistantSession(provider, _endpoint, Queue, Settings, new AssistantSessionOptions
            {
                ClientName = "Assistant for " + (client.ClientName.Length > 0 ? client.ClientName : client.Transport),
                Time = _options.Time,
                Secrets = KnownSecrets(),
            });
            session.Logged += line => Journal(line, true);
            c = new Conversation(session, provider, client.ClientName.Length > 0 ? client.ClientName : client.RemoteAddress);
            _conversations[key] = c;
            return c;
        }
    }

    /// <summary>The provider for the current settings and key: made again when the key changes (conversations made
    /// with the old one start over).</summary>
    private IAssistantProvider? Provider()
    {
        var version = _credentials is NodeCredentialStore store ? store.Version : 0;
        if (_provider is not null && version == _providerKeyVersion)
            return _provider;
        var key = _credentials.Find(NodeCredentialStore.AssistantKey)?.Secret;
        var provider = (_options.Providers ?? ((s, k) => AssistantProviders.Create(s, k)))(Settings, string.IsNullOrEmpty(key) ? null : key);
        if (provider is not null)
            _providers.Add(provider);
        _provider = provider;
        _providerKeyVersion = version;
        return provider;
    }

    /// <summary>Proposals waiting for a person on this node's list (STATE.ai.proposals).</summary>
    public int Waiting => Queue.Pending.Count;

    /// <summary>One proposal as the wire shows it.</summary>
    public static void WriteProposal(Utf8JsonWriter w, Proposal p, SecretRedactor redactor)
    {
        ArgumentNullException.ThrowIfNull(w);
        ArgumentNullException.ThrowIfNull(p);
        ArgumentNullException.ThrowIfNull(redactor);
        w.WriteStartObject();
        w.WriteString("id", p.Id);
        w.WriteString("line", redactor.Redact(p.Line));
        w.WriteString("verb", p.Verb);
        w.WriteString("risk", p.RiskWord);
        w.WriteString("reason", redactor.Redact(p.Reason));
        w.WriteString("from", p.From);
        w.WriteString("state", p.State.ToString().ToLowerInvariant());
        w.WriteBoolean("preTicked", p.PreTicked);
        w.WriteNumber("created", p.CreatedUnixMs);
        if (p.DecidedBy.Length > 0)
            w.WriteString("decidedBy", p.DecidedBy);
        if (p.ResultOk is { } ok)
            w.WriteBoolean("ok", ok);
        if (p.ResultWords.Length > 0)
            w.WriteString("result", redactor.Redact(p.ResultWords));
        if (p.RemoteDesk.Length > 0)
            w.WriteString("desk", p.RemoteDesk);
        w.WriteString("summary", redactor.Redact(p.Summary));
        w.WriteEndObject();
    }

    private void Journal(string line, bool ok)
    {
        var engine = _engine;
        engine?.Journal.Add("assistant", Redactor().Redact(line), ok: ok);
    }

    public async ValueTask DisposeAsync()
    {
        await UseApprovalDeskAsync(null).ConfigureAwait(false);
        lock (_gate)
        {
            _engine = null;
            _conversations.Clear();
            foreach (var p in _providers)
                p.Dispose();
            _providers.Clear();
            _provider = null;
        }
        _endpoint.Bind(null);
    }

    /// <summary>One client's conversation and how its last request ended.</summary>
    public sealed class Conversation
    {
        private readonly Lock _gate = new();
        private ConversationView _last;

        internal Conversation(AssistantSession session, IAssistantProvider provider, string client)
        {
            Session = session;
            Provider = provider;
            _last = new ConversationView(session.Id, client, false, "none", "", "", [], 0);
        }

        public AssistantSession Session { get; }

        internal IAssistantProvider Provider { get; }

        internal void Record(AssistantTurnResult result, long atUnixMs, SecretRedactor redactor)
        {
            lock (_gate)
                _last = _last with
                {
                    Outcome = OutcomeWord(result.Outcome),
                    Answer = redactor.Redact(result.Text),
                    Status = redactor.Redact(result.Status),
                    Queued = result.Queued,
                    AtUnixMs = atUnixMs,
                };
        }

        internal ConversationView View()
        {
            lock (_gate)
                return _last with { Busy = Session.Busy };
        }
    }

    public static string OutcomeWord(TurnOutcome outcome) => outcome switch
    {
        TurnOutcome.MaxTokens => "max_tokens",
        TurnOutcome.BudgetSpent => "budget_spent",
        TurnOutcome.InvalidCalls => "invalid_calls",
        TurnOutcome.ContextFull => "context_full",
        TurnOutcome.NotSent => "not_sent",
        _ => outcome.ToString().ToLowerInvariant(),
    };

    /// <summary>MCP's high-risk lines onto the node's list: the line passes the assistant's gate first (so an unknown
    /// verb, a connection word or a key in a line is never queued) and waits for a person.</summary>
    private sealed class QueueApprovals(AssistantService service) : PlayMap.Net.IProposalSink
    {
        public async ValueTask<string?> ProposeAsync(string line, string reason, VerbRisk risk, CancellationToken ct)
        {
            var check = CommandGate.Check(line, service._engine?.Store.Project);
            if (!check.Ok || check.Spec is null)
                return null;
            var batch = "mcp/" + Interlocked.Increment(ref service._mcpBatches).ToString(CultureInfo.InvariantCulture);
            var proposal = await service.Queue.EnqueueAsync(
                new ProposalDraft(check.Line, check.Spec, string.IsNullOrWhiteSpace(reason) ? "(no reason given)" : reason.Trim(), "an assistant over MCP", "mcp", batch, 0), ct)
                .ConfigureAwait(false);
            return proposal.Id;
        }
    }

    private sealed class Detach(AssistantService service) : IAsyncDisposable
    {
        private int _done;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
            {
                lock (service._gate)
                    service._engine = null;
                service._endpoint.Bind(null);
            }
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>The engine as the queue and the conversations reach it, bound once the engine exists: before that a
/// line is refused with the reason, and STATE and the context are empty objects.</summary>
internal sealed class DeferredEndpoint : IWireEndpoint
{
    private volatile EngineCore? _engine;
    private Action<long>? _stateChanged;
    private Action<long>? _projectChanged;

    public void Bind(EngineCore? engine)
    {
        var old = _engine;
        if (old is not null)
        {
            old.StateChanged -= RaiseState;
            old.ProjectChanged -= RaiseProject;
        }
        _engine = engine;
        if (engine is not null)
        {
            engine.StateChanged += RaiseState;
            engine.ProjectChanged += RaiseProject;
        }
    }

    public ValueTask<CommandReply> HandleAsync(WireLine line, SessionInfo session, CancellationToken ct) =>
        _engine is { } e ? e.HandleAsync(line, session, ct) : ValueTask.FromResult(CommandReply.Fail("the assistant is not attached to a node"));

    public (string Json, long Revision) State() => _engine?.State() ?? ("{}", 0);

    public string Context() => _engine?.Context() ?? "{}";

    public (string Json, long Revision) ProjectJson() => _engine?.ProjectJson() ?? ("{}", 0);

    public event Action<long>? StateChanged
    {
        add => _stateChanged += value;
        remove => _stateChanged -= value;
    }

    public event Action<long>? ProjectChanged
    {
        add => _projectChanged += value;
        remove => _projectChanged -= value;
    }

    private void RaiseState(long revision) => _stateChanged?.Invoke(revision);

    private void RaiseProject(long revision) => _projectChanged?.Invoke(revision);
}
