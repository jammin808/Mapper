using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PlayMap.Core.Bus;

namespace PlayMap.Core.Tests.Bus;

/// <summary>
/// The PeerLink kit's vectors: every row of this tree's copy of the master's ```peerlink block (docs/SHOWBUS.md, 13.20) run against
/// PlayMap's own pure core, src/PlayMap.Core/Bus/PeerLink.cs. The crypto rows go through its canon, K, kid, transcript, proofs, lid, S
/// and enrolment, the acceptor's check of each refused proof, and the link a proof row's carrier and bind make; the parse rows through
/// its LINK line, link message and side-call parsers; the pure rows through its liveness, redial wait, clock and 13.12.3's table; the
/// machine rows through <see cref="PeerLinkConnector"/> on injected time, each at j = 0, ½ and just under 1 (a waitMs range is reached at
/// both ends), and the acceptor's row through <see cref="PeerReceipts"/>. Every number the core keeps is its §14 row and is read by the
/// core's code, its features are §14's, and every other row of §13's is named with what keeps it in PlayMap
/// (<see cref="NotInCore"/>). What no row reaches is held here too: a parse that succeeds, the side call a session admits; LINK end to
/// end over the proof rows — one proof, only to the id meant and over the certificate shown, verified only by an acceptor proof the
/// carrier checked, the record bound to the id it proved, another id accepted by a person; a changed certificate, a pin that names none,
/// the bearer only to a pinned one; a matched pong and the clock it makes; the level-0 proof's NONCE rule; the connect, LINK and
/// handshake deadlines; a public viewer proving its key again; a local <c>ws</c> link on plain; the final deadline and the commands
/// kept; the record's shape against 13.16.3's, its words; the receipt store's bounds; a hold across a restart on two clocks; the lights.
/// Socket scenarios against PlayMap's carriers are the links' own tests (PlayMap.Net.Tests).
/// </summary>
public sealed class PeerLinkVectorsTests
{
    /// <summary>The j each machine row runs at: 13.15.1's ends and its middle.</summary>
    static readonly double[] Js = [0, 0.5, 0.9999999];

    /// <summary>Every kind of row 13.20 names, each of which this class runs.</summary>
    static readonly string[] Cases = ["canon", "key", "proof", "refuse", "legacy", "parse", "liveness", "redial", "errms", "clock", "outcome", "machine"];

    const string Node = "Hall A";

    [Fact]
    public void EveryRowOfTheBlockHoldsOnTheCore() => Holds(160, (Check, text) =>
    {
        var block = Fenced(text, "peerlink");
        Check(block is not null, "peerlink vectors: the master holds a ```peerlink block");
        if (block is null) return;
        var rows = new List<JsonElement>();
        foreach (var line in block)
        {
            try { rows.Add(JsonDocument.Parse(line).RootElement.Clone()); }
            catch (JsonException e) { Check(false, $"peerlink vectors: a row parses ({e.Message}): {line}"); }
        }
        var proofs = rows.Where(r => Str(r, "case") == "proof").ToList();
        Check(proofs.Count > 0 && proofs.Exists(r => Str(r, "carrier") == "ws" && Str(r, "session") is not null),
            "peerlink vectors: the block holds proof rows, a ws one with its S among them");
        if (proofs.Count == 0) return;
        var wsProof = proofs.Find(r => Str(r, "carrier") == "ws");
        var ran = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var r in rows)
        {
            var c = Str(r, "case") ?? "";
            switch (c)
            {
                case "canon": Canon(Check, r); break;
                case "key": Key(Check, r); break;
                case "proof": Proof(Check, r); break;
                case "refuse": Refuse(Check, r, proofs[0]); break;
                case "legacy": Legacy(Check, r); break;
                case "parse": Parse(Check, r, wsProof); break;
                case "liveness":
                    Check(PeerLink.Liveness(Int(r, "silenceMs") ?? -1).Word() == Str(r, "state"), "peerlink vectors: liveness " + Short(r));
                    break;
                case "redial":
                    Check(Int(r, "n") is { } n && Num(r, "j") is { } j && Math.Abs(PeerLink.RedialWait((int)n, j) - (Int(r, "ms") ?? -1)) <= (Num(r, "tol") ?? 0),
                        "peerlink vectors: redial " + Short(r) + $" (core: {(Int(r, "n") is { } n2 && Num(r, "j") is { } j2 ? PeerLink.RedialWait((int)n2, j2) : -1)})");
                    break;
                case "errms":
                    Check(PeerClock.ErrMs(Int(r, "rttMs") ?? -1) == Int(r, "errMs"), "peerlink vectors: errMs " + Short(r));
                    break;
                case "clock": Clock(Check, r); break;
                case "outcome": Outcome(Check, r); break;
                case "machine": Machine(Check, r, Str(proofs[0], "bind")!["tls:sha256:".Length..]); break;
                default:
                    Check(false, "peerlink vectors: a row's case is one 13.20 names: " + c);
                    continue;
            }
            ran[c] = ran.GetValueOrDefault(c) + 1;
        }
        Check(Cases.All(ran.ContainsKey) && ran.Values.Sum() == rows.Count,
            $"peerlink vectors: every row ran, every kind among them ({ran.Values.Sum()} of {rows.Count}; none of {string.Join(", ", Cases.Where(c => !ran.ContainsKey(c)))})");
    });

    [Fact]
    public void TheCoresNumbersAreSection14sAndEverySection13RowIsPlaced() => Holds(30, (Check, text) =>
    {
        var numbers = Fenced(text, "showbus");
        Check(numbers is not null, "peerlink vectors: the master holds a ```showbus block");
        if (numbers is not null) Numbers(Check, numbers, text);
    });

    [Fact]
    public void TheCoreHoldsWhatNoRowReaches() => Holds(60, (Check, text) =>
    {
        var proofs = (Fenced(text, "peerlink") ?? []).Select(l => JsonDocument.Parse(l).RootElement.Clone()).Where(r => Str(r, "case") == "proof").ToList();
        Check(proofs.Count > 0 && proofs.Exists(r => Str(r, "carrier") == "ws"), "peerlink vectors: the block holds proof rows, a ws one among them");
        if (proofs.Count == 0 || !proofs.Exists(r => Str(r, "carrier") == "ws")) return;
        Beyond(Check, proofs[0], proofs.Find(r => Str(r, "carrier") == "ws"), text);
    });

    /// <summary>Runs <paramref name="body"/> over the copy's text with a check that keeps every failure, and holds that none failed and
    /// that at least <paramref name="least"/> checks ran.</summary>
    static void Holds(int least, Action<Action<bool, string>, string> body)
    {
        var path = Path.Combine(Root(), "docs", "SHOWBUS.md");
        var text = File.ReadAllText(path, new UTF8Encoding(false)).Replace("\r\n", "\n", StringComparison.Ordinal);
        var failed = new List<string>();
        var ran = 0;
        body((ok, what) =>
        {
            ran++;
            if (!ok) failed.Add(what);
        }, text);
        Assert.True(failed.Count == 0, string.Join("\n", failed));
        Assert.True(ran >= least, $"{ran} checks ran, fewer than {least}");
    }

    static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PlayMap.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("no PlayMap.sln above " + AppContext.BaseDirectory);
    }

    // ------------------------------------------------------------------ §14

    /// <summary>
    /// The §14 rows of §13's that the core does not keep, each with what keeps it in PlayMap — an acceptor, a carrier's socket, timer
    /// or writer lane, or another part — and the unit that brings it, or why it is not PlayMap's.
    /// </summary>
    static readonly (string Row, string Whose)[] NotInCore =
    [
        ("peerlink.key.bits.min", "the pmk_ key maker: PlayMap issues no app key yet (wp4-peerlink)"),
        ("peerlink.unpaired.per.address", "a LINK acceptor's unfinished connections: PlayMap's wire speaks no LINK yet (wp4-peerlink)"),
        ("peerlink.wrong.proofs.per.minute", "a LINK acceptor's wrong-proof table: PlayMap's wire speaks no LINK yet (wp4-peerlink)"),
        ("peerlink.hold.disconnect.ms", "StagewatchLinkOptions.DisconnectHold, the operator's disconnect: StagewatchLinkTests holds it"),
        ("peerlink.cert.probe.ms", "13.8.3's MAY probe of a changed certificate: StagewatchLink sends a CERTIFICATE CHANGED Stagewatch nothing, and the probe's own pace is the links' LINK unit's (wp4-peerlink)"),
        ("peerlink.peer.sha256", "the scripted peer's own hash, not a number the core keeps: ShowBusSpecTests holds the peer to it"),
        ("link.beat.ms", "ShowBusConstants.BeatMs, the beat PlayMap's links keep: ShowBusSpecTests holds it"),
        ("link.check.max.ms", "the step WireClient's and StagewatchLink's beat checks liveness on (StaleAfter / 8, at most 250 ms)"),
        ("link.command.bytes", "a LINK acceptor's reader: PlayMap's wire reads its own bound until it speaks LINK (wp4-peerlink)"),
        ("link.read.min.bytes", "the connectors' readers: WireClient reads lines to 4 MiB and StagewatchLink messages to 1 MiB"),
        ("link.replies.max", "a LINK acceptor's replies per connection: wp4-peerlink"),
        ("link.sidecall.inflight", "StagewatchLink's side-call lane, one call at a time: StagewatchLinkTests holds it"),
        ("link.sidecall.timeout.ms", "StagewatchLink.SideCallWithin, each side call's deadline: StagewatchLinkTests holds it"),
        ("link.write.deadline.max.ms", "WireClient's write deadline (10 s)"),
        ("link.ws.in.bytes", "Stagewatch's /ws reader; StagewatchLink sends it only its ping, far under it"),
        ("ping.min.ms", "the connectors' ping, every ShowBusConstants.BeatMs (2 s)"),
        ("ping.max.ms", "the connectors' ping, every ShowBusConstants.BeatMs (2 s)"),
        ("show.stuck.min.ms", "an acceptor that offers stuck: PlayMap offers none until its engine control lane's stall is measured (a lab line, not run)"),
        ("show.stuck.max.ms", "an acceptor that offers stuck: PlayMap offers none until its engine control lane's stall is measured (a lab line, not run)"),
        ("receipt.late.ms", "a LINK acceptor's lateMs: wp4-peerlink"),
        ("cmd.expire.max.ms", "an acceptor that offers feature expire: PlayMap offers none"),
        ("log.repeat.summary.ms", "the links' repeat summary: PlayMap's links log each change of state once; the summary is the links' LINK unit's (wp4-peerlink)"),
    ];

    /// <summary>The sections §14.4 gives the rows that start so; every other row is §3–§5's or §13's.</summary>
    static readonly string[] OtherSections = ["proto", "kind", "port", "cap", "reserved", "hello.", "peers.", "peer.", "context.", "cert.", "proposal.", "window.", "view."];

    /// <summary>
    /// Every number the core keeps is its §14 row and is read by the core's own code, not only declared; its features and label are
    /// §14's; and every row of §13's — named in §13 or by §14.4 as §13's — is one the core keeps or one <see cref="NotInCore"/> names.
    /// </summary>
    static void Numbers(Action<bool, string> Check, string[] lines, string text)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var w in lines.Select(l => l.Split(' ')).Where(w => w.Length == 2)) values.TryAdd(w[0], w[1]);
        foreach (var (name, value) in PeerLink.Rows)
            Check(values.TryGetValue(name, out var row) && long.TryParse(row, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n == value,
                $"peerlink vectors: the core's {name} is §14's ({value} against {values.GetValueOrDefault(name, "no row")})");
        var features = lines.Where(l => l.StartsWith("peerlink.feature ", StringComparison.Ordinal)).Select(l => l["peerlink.feature ".Length..]).ToList();
        Check(features.SequenceEqual(PeerLink.Features), "peerlink vectors: the core's features are §14's peerlink.feature rows, in order (" + string.Join(", ", features) + ")");
        Check(values.GetValueOrDefault("peerlink.label") == PeerLink.Label, "peerlink vectors: the core's label is §14's peerlink.label");

        // Each row the core keeps names a constant its code reads beyond its declaration and its entry in Rows.
        var source = File.ReadAllText(Path.Combine(Root(), "src", "PlayMap.Core", "Bus", "PeerLink.cs"));
        var named = Regex.Matches(source, @"\[""([a-z.]+)""\] = (\w+)").ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);
        var unread = PeerLink.Rows.Keys.Where(r => !named.TryGetValue(r, out var id) || Regex.Count(source, @"\b" + id + @"\b") < 3).ToList();
        Check(named.Count == PeerLink.Rows.Count && unread.Count == 0,
            "peerlink vectors: every row the core keeps is read by its code, not only declared (" + string.Join(", ", unread) + ")");

        var s13 = Between(text, "\n## 13. ", "\n## 14. ") ?? "";
        var whose = Between(text, "4. **Whose rows.**", "5. **Copies.**") ?? "";
        var thirteen = Regex.Matches(s13, "`([a-z][a-z0-9.]*)`").Select(m => m.Groups[1].Value)
            .Concat(Regex.Matches(whose, @"^   - `([a-z.]+)`:", RegexOptions.Multiline).Select(m => m.Groups[1].Value))
            .Where(r => values.ContainsKey(r) && !OtherSections.Any(o => r == o || (o.EndsWith('.') && r.StartsWith(o, StringComparison.Ordinal))))
            .ToHashSet(StringComparer.Ordinal);
        var kept = PeerLink.Rows.Keys.Append("peerlink.label").ToHashSet(StringComparer.Ordinal);
        var elsewhere = NotInCore.Select(x => x.Row).ToHashSet(StringComparer.Ordinal);
        var loose = thirteen.Where(r => !kept.Contains(r) && !elsewhere.Contains(r)).Order(StringComparer.Ordinal).ToList();
        var both = elsewhere.Where(kept.Contains).ToList();
        var strays = elsewhere.Where(r => !thirteen.Contains(r)).ToList();
        Check(thirteen.Count > 40 && loose.Count == 0 && both.Count == 0 && strays.Count == 0 && NotInCore.All(x => x.Whose.Length > 0),
            $"peerlink vectors: every row of §13's is kept by the core or named with what keeps it ({thirteen.Count}; loose: {string.Join(", ", loose)}; "
            + $"both: {string.Join(", ", both)}; not §13's: {string.Join(", ", strays)})");
    }

    // ------------------------------------------------------------------ crypto rows

    static void Canon(Action<bool, string> Check, JsonElement r)
    {
        var code = Str(r, "code") ?? "";
        var canon = PeerLink.Canon(code);
        Check(canon == Str(r, "canon"), $"peerlink vectors: canon {Short(r)} (core: {canon ?? "refused"})");
        if (Str(r, "K") is { } k)
            Check(canon is { } c && Hex(PeerLink.CodeK(c)) == k && Str(r, "kid") == "code", "peerlink vectors: a code's K, and its kid the word code: " + Short(r));
    }

    static void Key(Action<bool, string> Check, JsonElement r)
    {
        var k = PeerLink.KeyK(Str(r, "key") ?? "");
        Check(Hex(k) == Str(r, "K") && PeerLink.Kid(k) == Str(r, "kid"), "peerlink vectors: a key's K and kid: " + Str(r, "kid"));
    }

    /// <summary>T and what is made from it; the acceptor takes the client's proof and not the server's; the link a carrier and bind make has the row's flags.</summary>
    static void Proof(Action<bool, string> Check, JsonElement r)
    {
        var name = $"proof {Str(r, "carrier")} {Str(r, "kid")} {Str(r, "bind")?[..5]}";
        var t = Transcript(r);
        Check(t is not null, "peerlink vectors: the core builds the row's transcript: " + name);
        if (t is null || Str(r, "K") is not { } kh) return;
        var k = Convert.FromHexString(kh);
        if (Str(r, "Tsha256") is { } th) Check(Hex(SHA256.HashData(Encoding.UTF8.GetBytes(t))) == th, "peerlink vectors: the transcript hashes to Tsha256: " + name);
        var kid = Str(r, "kid")!;
        Check(kid == "code" ? Str(r, "code") is not { } code || (PeerLink.Canon(code) is { } c && Hex(PeerLink.CodeK(c)) == kh) : PeerLink.Kid(k) == kid,
            "peerlink vectors: the row's K is its kid's key, or its code's: " + name);
        var client = PeerLink.ClientProof(k, t);
        var server = PeerLink.ServerProof(k, t);
        Check(client == Str(r, "clientProof") && server == Str(r, "serverProof") && PeerLink.Lid(t) == Str(r, "lid"),
            "peerlink vectors: the client proof, the server proof and the lid: " + name);
        Check(PeerLink.Verify(client, Str(r, "clientProof")) && !PeerLink.Verify(client, Str(r, "serverProof")) && PeerLink.Verify(server, Str(r, "serverProof"))
              && !PeerLink.Verify(client, Str(r, "clientProof")!.ToUpperInvariant()),
            "peerlink vectors: each end takes the other's proof and no other, as lowercase hex: " + name);
        if (Str(r, "session") is { } s) Check(Str(r, "carrier") == "ws" && PeerLink.Session(k, t) == s, "peerlink vectors: S on ws: " + name);
        var bind = Str(r, "bind")!;
        if (Str(r, "enrolK") is { } ek)
        {
            var link = PeerLink.EnrolK(k, t);
            Check(PeerLink.MayEnrol(kid, bind) && Hex(link) == ek && PeerLink.Kid(link) == Str(r, "enrolKid"), "peerlink vectors: K_link and its kid: " + name);
        }
        if (r.TryGetProperty("enrol", out var enrol))
            Check(enrol.ValueKind == JsonValueKind.False && !PeerLink.MayEnrol(kid, bind), "peerlink vectors: a code over plain does not enrol: " + name);
        if (Str(r, "carrier") == "line" && Split(Str(r, "client")) is var (ct, cid))
        {
            var ask = PeerLink.ParseLinkLine($"@l1 LINK {Int(r, "offer")} {ct} {cid} {Str(r, "cnonce")} {kid}").Ask;
            Check(ask is { } a && a.Offer == Int(r, "offer") && a.T == ct && a.Id == cid && a.CNonce == Str(r, "cnonce") && a.Kid == kid,
                "peerlink vectors: the acceptor reads the LINK line the row's connector writes: " + name);
            Check(PeerLink.ParseLinkLine("@l2 LINK PROOF " + client).Proof == client, "peerlink vectors: the acceptor reads its LINK PROOF: " + name);
        }

        // The link this proof row's carrier and bind make, verified: its flags are the row's, plain and code-plain exactly.
        var m = new PeerLinkConnector(Settings(Str(r, "carrier") == "ws" ? PeerCarrier.Ws : PeerCarrier.Line, kid), null, 0, () => 0.5);
        if (bind == "plain") m.NoTls(0);
        else m.Certificate(0, bind["tls:sha256:".Length..]);
        m.Verified(0);
        var flags = m.Flags(0);
        var want = Strings(r, "flags");
        Check(want.All(flags.Contains) && flags.Contains("plain") == want.Contains("plain") && flags.Contains("code-plain") == want.Contains("code-plain")
              && m.State(0) == PeerLinkState.Live && m.Tls() == (bind == "plain" ? "none" : "pinned"),
            $"peerlink vectors: the link the row's carrier and bind make is live with the row's flags ({string.Join(", ", flags)}): " + name);
    }

    /// <summary>A refuse row's proof is the first proof row's with its one field changed; the acceptor, over the first row's T, refuses it.</summary>
    static void Refuse(Action<bool, string> Check, JsonElement r, JsonElement first)
    {
        var why = Str(r, "why") ?? "";
        var t = Transcript(first)!;
        var k = Convert.FromHexString(Str(first, "K")!);
        var given = Str(r, "clientProof");
        Check(Str(r, "expect") == "refused" && !PeerLink.Verify(PeerLink.ClientProof(k, t), given), "peerlink vectors: the acceptor refuses the proof of refuse " + why);
        if (why == "role")
        {
            Check(given == PeerLink.ServerProof(k, t), "peerlink vectors: refuse role offers the server's proof as the client's");
            return;
        }
        var changed = Transcript(first, why, r);
        Check(changed is not null && PeerLink.ClientProof(k, changed) == given, "peerlink vectors: refuse " + why + "'s proof is made over the changed transcript");
    }

    static void Legacy(Action<bool, string> Check, JsonElement r)
    {
        var nonce = Str(r, "nonce") ?? "";
        var code = Str(r, "code");
        if (Str(r, "proof") is { } proof)
        {
            Check(PeerLink.LegacyProof(code ?? "", nonce, Str(r, "name") ?? "") == proof, "peerlink vectors: the legacy proof: " + Short(r));
            return;
        }
        Check(Bool(r, "prove") == false
              && (code is not null
                  ? PeerLink.IsProvableNonce(nonce) && PeerLink.LegacyKey(code) is null && PeerLink.LegacyProof(code, nonce, "Stagewatch") is null
                  : !PeerLink.IsProvableNonce(nonce) && PeerLink.LegacyProof("k7qm-3xwd p9ra", nonce, "Stagewatch") is null),
            "peerlink vectors: no legacy proof for a token 13.6.8 does not prove, or a nonce it does not answer: " + Short(r));
    }

    // ------------------------------------------------------------------ parse rows

    static void Parse(Action<bool, string> Check, JsonElement r, JsonElement wsProof)
    {
        var expect = r.GetProperty("expect");
        switch (Str(r, "carrier"))
        {
            case "line":
                var p = PeerLink.ParseLinkLine(Str(r, "line") ?? "");
                Check(p.Ask is null && p.Proof is null && "ERR LINK " + p.Refused == expect.GetString(), $"peerlink vectors: parse {Short(r)} (core: {p.Refused ?? "taken"})");
                break;
            case "ws":
                var m = PeerLink.ParseLinkMessage(Str(r, "query") ?? "", r.GetProperty("message"));
                Check(Bool(expect, "ok") == false && m.Ask is null && m.Refused == Str(expect, "class"), $"peerlink vectors: parse {Short(r)} (core: {m.Refused ?? "taken"})");
                break;
            case "http":
                // A side call's Authorization against the first ws proof row's live link; `clear` is a call in clear from another
                // machine, and `ends` the lid whose link the acceptor ends. A row that names no `ends` is a header 13.7.1 does not take.
                var header = Str(r, "authorization");
                var c = PeerLink.CheckSession(header, lid => lid == Str(wsProof, "lid") ? Str(wsProof, "session") : null, Bool(r, "clear") == true);
                var names = expect.TryGetProperty("ends", out var ends);
                Check((names || PeerLink.ParseShowBus(header) is null) && c.Status == Int(expect, "status") && c.Relink == Bool(expect, "relink")
                      && c.Ends == (names && ends.ValueKind == JsonValueKind.String ? ends.GetString() : null),
                    $"peerlink vectors: parse {Short(r)} (core: {c.Status}{(c.Relink ? " relink" : "")}{(c.Ends is { } e ? " ends " + e : "")})");
                break;
            default:
                Check(false, "peerlink vectors: a parse row's carrier is line, ws or http: " + Short(r));
                break;
        }
    }

    // ------------------------------------------------------------------ pure rows

    static void Clock(Action<bool, string> Check, JsonElement r)
    {
        var tol = Num(r, "tol") ?? 0;
        if (r.TryGetProperty("samples", out var samples))
        {
            var clock = new PeerClock();
            var refused = samples.EnumerateArray().Count(s => !clock.Add(s[0].GetInt64(), s[1].GetInt64(), s[2].GetInt64(), s[3].GetInt64()));
            Check(refused == (Int(r, "refused") ?? 0) && clock.Samples == samples.GetArrayLength() - refused, "peerlink vectors: clock admission " + Short(r));
            var b = clock.Believed;
            if (Num(r, "offsetMs") is { } offset) Check(b is { } o && Math.Abs(o.OffsetMs - offset) <= tol, "peerlink vectors: clock offsetMs " + Short(r));
            if (Int(r, "rttMs") is { } rtt) Check(b?.RttMs == rtt, "peerlink vectors: clock rttMs " + Short(r));
            if (Int(r, "errMs") is { } err) Check(b is { } e && Math.Abs(e.ErrMs - err) <= tol, "peerlink vectors: clock errMs " + Short(r));
            if (Bool(r, "apart") is { } apart) Check(clock.Apart == apart, "peerlink vectors: clock apart " + Short(r));
            return;
        }
        var best = r.GetProperty("best");
        var off = Num(best, "offsetMs") ?? 0;
        var errMs = Int(best, "errMs") ?? 0;
        if (Bool(r, "apart") is { } a) Check(PeerClock.IsApart(off, errMs) == a, "peerlink vectors: clock apart " + Short(r));
        if (Int(best, "ageMs") is { } age)
        {
            if (Bool(r, "fresh") is { } fresh) Check(PeerClock.IsFresh(age) == fresh, "peerlink vectors: clock fresh " + Short(r));
            foreach (var fit in r.EnumerateObject().Where(p => p.Name.StartsWith("fitFor", StringComparison.Ordinal)))
                Check(PeerClock.IsFitFor(age, errMs, long.Parse(fit.Name["fitFor".Length..], CultureInfo.InvariantCulture)) == (fit.Value.ValueKind == JsonValueKind.True),
                    "peerlink vectors: clock " + fit.Name + " " + Short(r));
        }
    }

    static void Outcome(Action<bool, string> Check, JsonElement r)
    {
        var reading = Reading(r.GetProperty("answer"));
        Check(reading is { } x && x.Outcome.Word() == Str(r, "outcome") && (Str(r, "words") is not { } words || x.Words == words),
            $"peerlink vectors: outcome {Short(r)} (core: {reading?.Outcome.Word()} '{reading?.Words}')");
    }

    /// <summary>An outcome row's answer, read by the core's 13.12.3.</summary>
    static PeerReading? Reading(JsonElement a)
    {
        if (Str(a, "line") is { } line) return PeerLink.ReadLine(line)?.Reading;
        if (a.TryGetProperty("ws", out var ws)) return PeerLink.ReadReceipt(ws);
        if (Int(a, "http") is { } status) return PeerLink.ReadHttp((int)status, a.TryGetProperty("body", out var body) ? body.GetRawText() : null);
        if (Bool(a, "written") is { } written)
            return PeerLink.ReadUnanswered(written, Int(a, "silenceMs") ?? 0, written && Str(a, "link") == "down", Node,
                Str(a, "link") is { } link ? "the link is " + link : "not sent");
        return null;
    }

    // ------------------------------------------------------------------ machine rows

    static PeerLinkSettings Settings(PeerCarrier carrier, string? key, IReadOnlyList<string>? can = null)
        => new() { Carrier = carrier, Key = key, Can = can ?? [], Name = Node, Address = "10.0.0.5", Inst = "11aa22bb" };

    /// <summary>A machine row; <paramref name="pinned"/> is the certificate a <c>pin</c> config pins, the first proof row's (13.20).</summary>
    static void Machine(Action<bool, string> Check, JsonElement r, string pinned)
    {
        var name = "peerlink vectors: machine " + Str(r, "name");
        if (Str(r, "role") == "acceptor")
        {
            Acceptor(Check, r, name);
            return;
        }
        var waits = new List<(long At, long Wait)>();
        foreach (var j in Js) Connector(Check, r, name, j, waits, pinned);
        foreach (var x in Items(r, "expect").Where(e => e[1].TryGetProperty("waitMs", out _)))
        {
            var range = x[1].GetProperty("waitMs");
            var at = x[0].GetInt64();
            var seen = waits.Where(w => w.At == at).Select(w => w.Wait).ToList();
            Check(seen.Contains(range[0].GetInt64()) && seen.Contains(range[1].GetInt64()), $"{name}: j at 0 and near 1 reach both ends of the waitMs range at {at}");
        }
    }

    static void Connector(Action<bool, string> Check, JsonElement r, string name, double j, List<(long At, long Wait)> waits, string pinned)
    {
        var config = r.GetProperty("config");
        var settings = Settings(Str(config, "carrier") == "ws" ? PeerCarrier.Ws : PeerCarrier.Line, Str(config, "key"), Strings(config, "can"))
            with { ShowNeeds = Bool(config, "needs") == true };
        PeerPin? pin = Str(config, "pin") is { } p && p.StartsWith("node:", StringComparison.Ordinal) ? new PeerPin(p[5..], pinned, PinTrust.Proof) : null;
        PeerFloor? floor = config.TryGetProperty("floor", out var f)
            ? new PeerFloor(f.GetProperty("tls").GetBoolean(), f.GetProperty("level").GetInt32(), f.GetProperty("proof").GetString()!) : null;
        var m = new PeerLinkConnector(settings, new PeerKept(pin, floor, null, null), 0, () => j);
        var log = new List<PeerWrite>();
        var from = 0;
        var sends = new Dictionary<string, string>(StringComparer.Ordinal);
        var events = Items(r, "events");
        var next = 0;
        void Apply(JsonElement e)
        {
            var t = e[0].GetInt64();
            var a = e.GetArrayLength() > 2 ? e[2] : default;
            switch (e[1].GetString())
            {
                case "dial": m.Dial(t); break;
                case "connected": from = log.Count; m.Connected(t); break;
                case "answer":
                    if (Bool(a, "tls") == false) m.NoTls(t);
                    if (Str(a, "cert") is { } cert) m.Certificate(t, cert);
                    if (Str(a, "line") is { } line) m.LineAnswer(t, line);
                    if (Str(a, "first") == "frame") m.FirstFrame(t);
                    break;
                case "verified": from = log.Count; m.Verified(t, Str(a, "inst")); break;
                case "refused": m.Refused(t, Str(a, "class")!, (int)(Int(a, "retry") ?? 0)); break;
                case "closed": m.Closed(t); break;
                case "heard": m.Heard(t); break;
                case "frame": m.Frame(t); break;
                case "beat": m.Beat(t, true); break;
                case "beat-stuck": m.Beat(t, false); break;
                case "ping": m.Ping(t, Int(a, "t1")!.Value); break;
                case "pong" or "pong-stuck": m.Pong(t, Int(a, "t1"), Int(a, "t2"), Int(a, "t3"), e[1].GetString() == "pong-stuck"); break;
                case "evaluate": m.Evaluate(t); break;
                case "hello": m.Hello(t, Str(a, "id")!, Str(a, "tls"), Str(a, "inst"), Str(a, "host")); break;
                case "send":
                    for (var i = 0; i < Int(a, "count"); i++)
                    {
                        var id = m.NextId();
                        sends[(sends.Count + 1).ToString(CultureInfo.InvariantCulture)] = id;
                        m.Send(t, id);
                    }
                    break;
                case "written": m.Written(t, Str(a, "id")!); break;
                case "receipts":
                    m.Receipts(t, a.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.ValueKind == JsonValueKind.Object ? PeerLink.ReadReceipt(x.Value) : (PeerReading?)null));
                    break;
                case "side-call": m.SideCall(t, Reading(a)!.Value); break;
                case "restart": m = new PeerLinkConnector(settings, m.Kept, t, () => j); break;
                default: Check(false, $"{name}: an event the kit runs ({e[1].GetString()})"); break;
            }
            log.AddRange(m.Drain());
        }
        foreach (var x in Items(r, "expect"))
        {
            var at = x[0].GetInt64();
            while (next < events.Count && events[next][0].GetInt64() <= at) Apply(events[next++]);
            var where = $"{name} at {at} (j {j.ToString(CultureInfo.InvariantCulture)})";
            foreach (var field in x[1].EnumerateObject())
            {
                var v = field.Value;
                var rec = m.Record(at);
                var (ok, got) = field.Name switch
                {
                    "state" => (rec.State == v.GetString(), rec.State),
                    "light" => (rec.Light == v.GetString(), rec.Light),
                    "show" => (rec.Show == v.GetString(), rec.Show),
                    "refusal" => (rec.Refusal == v.GetString(), rec.Refusal),
                    "tls" => (rec.Tls == v.GetString(), rec.Tls),
                    "flags" => (v.EnumerateArray().All(s => rec.Flags.Contains(s.GetString()!)), string.Join(",", rec.Flags)),
                    "samples" => (rec.Samples == v.GetInt32(), rec.Samples.ToString(CultureInfo.InvariantCulture)),
                    "counts" => (v.EnumerateObject().All(c => Count(rec.Counts, c.Name) == c.Value.GetInt32()), rec.Counts.ToString()),
                    "waitMs" => (m.WaitMs >= v[0].GetInt64() && m.WaitMs <= v[1].GetInt64(), m.WaitMs.ToString(CultureInfo.InvariantCulture)),
                    "heldUntil" => (m.HeldUntilWall(at) == v.GetInt64(), m.HeldUntilWall(at)?.ToString(CultureInfo.InvariantCulture) ?? "none"),
                    "dial" => (m.DialDue(at) == (v.ValueKind == JsonValueKind.Null ? null : v.GetString()), m.DialDue(at) ?? "null"),
                    "pinged" => ((m.PingedAt == at) == v.GetBoolean(), m.PingedAt?.ToString(CultureInfo.InvariantCulture) ?? "never"),
                    "follows" => (m.Follows(at) == v.GetBoolean(), m.Follows(at).ToString()),
                    "sent" => (log.Skip(from).Where(w => w.Verb is not null).Select(w => w.Verb!).SequenceEqual(v.EnumerateArray().Select(s => s.GetString()!)),
                        string.Join(",", log.Skip(from).Select(w => w.Verb))),
                    "credentials" => (log.Skip(from).Where(w => w.Credential is not null).Select(w => w.Credential!).ToHashSet().SetEquals(v.EnumerateArray().Select(s => s.GetString()!)),
                        string.Join(",", log.Skip(from).Select(w => w.Credential))),
                    "inFlight" => (m.InFlight == v.GetInt32(), m.InFlight.ToString(CultureInfo.InvariantCulture)),
                    "outcome" => (v.EnumerateObject().All(o => m.OutcomeOf(sends.GetValueOrDefault(o.Name, o.Name))?.Word() == o.Value.GetString()),
                        string.Join(",", v.EnumerateObject().Select(o => m.OutcomeOf(sends.GetValueOrDefault(o.Name, o.Name))?.Word() ?? "waiting"))),
                    _ => (false, "a field the kit does not read"),
                };
                Check(ok, $"{where}: {field.Name} {v.GetRawText()} (core: {got})");
            }
            if (x[1].TryGetProperty("waitMs", out _)) waits.Add((at, m.WaitMs));
        }
        while (next < events.Count) Apply(events[next++]);
    }

    static int Count(PeerCounts c, string name) => name switch
    {
        "unconfirmed" => c.Unconfirmed,
        "late" => c.Late,
        "replayed" => c.Replayed,
        "oversize" => c.Oversize,
        "malformed" => c.Malformed,
        "unmatched" => c.Unmatched,
        _ => -1,
    };

    /// <summary>
    /// The acceptor's row: commands read on a verified link — run to their end, or still running — each run once at most while the
    /// store holds its id, and RECEIPTS asks answered from it (feature receipts).
    /// </summary>
    static void Acceptor(Action<bool, string> Check, JsonElement r, string name)
    {
        var config = r.GetProperty("config");
        var keeps = Strings(config, "can").Contains("receipts");
        var store = new PeerReceipts();
        string? connector = null, last = null;
        var runs = 0;
        var events = Items(r, "events");
        var next = 0;
        foreach (var x in Items(r, "expect"))
        {
            var at = x[0].GetInt64();
            for (; next < events.Count && events[next][0].GetInt64() <= at; next++)
            {
                var e = events[next];
                var t = e[0].GetInt64();
                switch (e[1].GetString())
                {
                    case "verified": connector = "main:3f9c2a1b7d0e"; break;
                    case "command" or "running" when connector is not null:
                        var id = Str(e[2], "id")!;
                        var held = keeps ? store.Begin(connector, id, t) : null;
                        if (held is not null)
                        {
                            last = held;
                            break;
                        }
                        runs++;
                        if (e[1].GetString() == "running") break;
                        last = "{\"ok\":true,\"stage\":\"applied\",\"effect\":\"\"}";
                        if (keeps) store.Keep(connector, id, last, t);
                        break;
                    case "ask" when connector is not null && keeps:
                        last = store.Answer(connector, Strings(e[2], "ids"), "a41f09c2", t);
                        break;
                    default: Check(false, $"{name}: an acceptor event the kit runs ({e[1].GetString()})"); break;
                }
            }
            if (Int(x[1], "runs") is { } want) Check(runs == want, $"{name} at {at}: runs {want} (core: {runs})");
            if (x[1].TryGetProperty("answer", out var answer))
            {
                using var doc = JsonDocument.Parse(last ?? "{}");
                Check(answer.EnumerateObject().All(p => doc.RootElement.TryGetProperty(p.Name, out var v) && v.GetRawText() == p.Value.GetRawText()),
                    $"{name} at {at}: the last receipt holds {answer.GetRawText()} (core: {last})");
            }
        }
    }

    // ------------------------------------------------------------------ beyond the rows

    static void Beyond(Action<bool, string> Check, JsonElement first, JsonElement wsProof, string text)
    {
        // The acceptor's parsers take what is well formed.
        var ask = PeerLink.ParseLinkLine("@l1 LINK 2 main 3f9c2a1b7d0e ffeeddccbbaa99887766554433221100 code");
        Check(ask.Ask is { Offer: 2, T: "main", Id: "3f9c2a1b7d0e", Kid: "code" } && PeerLink.Speaks(2) == 1 && PeerLink.Speaks(0) is null,
            "peerlink core: a LINK that offers a higher version is taken, and spoken at this one");
        Check(PeerLink.ParseLinkLine("LINK PROOF " + new string('a', 64) + " extra").Refused == "malformed" && PeerLink.ParseLinkLine("@l 1 LINK 1 main 3f9c2a1b7d0e ffeeddccbbaa99887766554433221100 code").Refused == "malformed"
              && PeerLink.ParseLinkLine("@l1  LINK 1 main 3f9c2a1b7d0e ffeeddccbbaa99887766554433221100 code").Refused == "malformed",
            "peerlink core: a word a LINK line does not name refuses it");
        var message = JsonDocument.Parse("{\"type\":\"link\",\"client\":{\"t\":\"patterns\",\"id\":\"9f8e7d6c\"},\"cnonce\":\"" + Str(wsProof, "cnonce") + "\",\"kid\":\""
            + Str(wsProof, "kid") + "\",\"proof\":\"" + Str(wsProof, "clientProof") + "\"}").RootElement;
        var taken = PeerLink.ParseLinkMessage("node=9f8e7d6c&t=patterns&link=1", message);
        Check(taken.Ask is { T: "patterns", Id: "9f8e7d6c", Offer: 1 } && taken.Proof == Str(wsProof, "clientProof")
              && PeerLink.ParseLinkMessage("node=9f8e7d6c&t=patterns&link=0", message).Refused == "version"
              && PeerLink.ParseLinkMessage("node=9f8e7d6c&t=patterns&link=1&link=1", message).Refused == "malformed"
              && PeerLink.ParseLinkMessage("node=9f8e7d6c&t=main&link=1", message).Refused == "malformed",
            "peerlink core: a link message whose client is its query's is taken with its proof; another offer, a doubled word or another client is not");
        var lid = Str(wsProof, "lid")!;
        var session = Str(wsProof, "session")!;
        string? Of(string l) => l == lid ? session : null;
        Check(PeerLink.CheckSession($"ShowBus lid={lid}, s={session}", Of) is { Status: 200, Relink: false }
              && PeerLink.CheckSession($"ShowBus lid={lid}, s={new string('0', 64)}", Of) is { Status: 401, Relink: true }
              && PeerLink.CheckSession($"ShowBus lid={new string('0', 16)}, s={session}", Of) is { Status: 401, Relink: true }
              && PeerLink.CheckSession(null, Of) is { Status: 401, Relink: true },
            "peerlink core: a side call is taken with its link's S, and a wrong S, a lid with no live link or none at all is 401 relink");
        Check(PeerLink.CheckSession($"ShowBus lid={lid}, s={session}", Of, inClearFromAnother: true) is { Status: 401, Relink: true, Ends: var ends } && ends == lid
              && PeerLink.CheckSession($"ShowBus lid={lid}, s={new string('0', 64)}", Of, inClearFromAnother: true) is { Status: 401, Ends: null }
              && PeerLink.CheckSession($"ShowBus lid={lid}, s={session}", Of) is { Ends: null },
            "peerlink core: a right S in clear from another machine is 401 relink and its link ended (13.7.1); a wrong one in clear ends nothing — a lid is no secret");

        // 13.12.3 beyond the rows.
        Check(PeerLink.ReadLine("OK @s1-11aa22bb final {\"stage\":\"applied\",\"effect\":\"cut\"}") is { Id: "s1-11aa22bb", Reading: { Outcome: PeerOutcome.Applied, Final: true, Words: "cut" } }
              && PeerLink.ReadLine("ERR @s1-11aa22bb final standby moved") is { Reading: { Outcome: PeerOutcome.Refused, Final: true, Words: "standby moved" } }
              && PeerLink.ReadLine("OK") is { Id: null, Reading.Outcome: PeerOutcome.Unconfirmed }
              && PeerLink.ReadLine("PONG 1 2 3") is null
              && PeerLink.ReadLine("OK @s1-11aa22bb {\"outcome\":\"Confirm\",\"confirm\":\"Cue 4 is a stinger\"}") is { Reading: { Outcome: PeerOutcome.Accepted } c }
              && c.Words.Contains("a second GO", StringComparison.Ordinal),
            "peerlink core: a final line, an id-less OK, a line that is no answer, and a Confirm's second GO");
        Check(PeerLink.ReadHttp(429, null, 7) is { Outcome: PeerOutcome.Refused, RetryAfter: 7, Words: "rate limited — again in 7 s" }
              && PeerLink.ReadHttp(404, "{\"ok\":false,\"error\":\"no such clip\"}") is { Outcome: PeerOutcome.Refused, Words: "no such clip" }
              && PeerLink.ReadHttp(null, null) is { Outcome: PeerOutcome.Unconfirmed }
              && PeerLink.ReadHttp(200, "{\"ok\":true,\"stage\":\"accepted\",\"effect\":\"queued\"}") is { Outcome: PeerOutcome.Accepted, Words: "queued" }
              && PeerLink.ReadHttp(200, "{\"ok\":true,\"stage\":\"applied\",\"lateMs\":1200}") is { Outcome: PeerOutcome.Applied, LateMs: 1200 }
              && PeerLink.ReadUnanswered(true, PeerLink.ReceiptFinalMs, false, Node) is null,
            "peerlink core: 429 waits its Retry-After, a 404 says its words, no response is unconfirmed, a staged body is a receipt, and an answer at receipt.final.ms still counts");

        // CERTIFICATE CHANGED: nothing of the connector's is sent until the operator types at least 8 hex of the new one.
        var pinned = Str(first, "bind")!["tls:sha256:".Length..];
        const string other = "8e8bc45bb14f9c7413604ef78eb1c42b465437cf700ae04a8f9553ffb0286edc";
        var changed = new PeerLinkConnector(Settings(PeerCarrier.Line, "code"), new PeerKept(new PeerPin("9f8e7d6c", pinned, PinTrust.Proof), null, null, null), 0);
        changed.Certificate(0, other);
        Check(changed.State(0) == PeerLinkState.Refused && changed.Refusal(0) == "certificate" && changed.Drain().Count == 0 && changed.DialDue(0) is null
              && changed.Record(0).Words.StartsWith("CERTIFICATE CHANGED", StringComparison.Ordinal),
            "peerlink core: a pinned node showing another certificate is refused, red words, nothing sent and nothing dialled");
        Check(!changed.AcceptCertificate(other[..7]) && !changed.AcceptCertificate(pinned[..8]) && changed.AcceptCertificate(other[..8])
              && changed.State(1) != PeerLinkState.Refused && changed.Kept.Pin is { Fingerprint: other, Trust: PinTrust.Person },
            "peerlink core: the new certificate is accepted only by typing at least 8 hex of it, and then pinned by a person");

        // A matched pong is a sample; the clock schedules only over TLS or a local link.
        PeerLinkConnector Clocked(bool tls)
        {
            var c = new PeerLinkConnector(Settings(PeerCarrier.Line, "bb3f97d01c274e15") with { TimedToleranceMs = 300 }, null, 0);
            if (tls) c.Certificate(1000, pinned); else c.NoTls(1000);
            c.Verified(1000, "a41f09c2");
            c.Ping(1000, 1000);
            c.Pong(1012, 1000, 1505, 1506, false);
            return c;
        }
        var overTls = Clocked(true);
        var overPlain = Clocked(false);
        Check(overTls.Record(1012) is { Samples: 1, OffsetMs: 499.5, ErrMs: 7, AgeMs: 0 } && overTls.CanSchedule(1012, 300) && !overTls.Flags(1012).Contains("unfit")
              && !overTls.CanSchedule(11013, 300) && overTls.Flags(11013).Contains("unfit"),
            "peerlink core: a matched pong is a sample, fit while fresh over TLS, unfit past clock.fresh.ms");
        Check(overPlain.Clock.Samples == 1 && !overPlain.CanSchedule(1012, 300) && overPlain.Flags(1012).Contains("unfit") && overPlain.Flags(1012).Contains("plain"),
            "peerlink core: a clock measured over a plain link off this machine is shown and never schedules");
        overTls.Verified(11014, "b7c3d9e1");
        Check(overTls.Clock.Samples == 0, "peerlink core: a new inst empties the clock");

        // Level 0 on line: a NONCE of exactly 32 lowercase hex is proved, nothing else; the link is legacy, amber.
        var legacy = new PeerLinkConnector(Settings(PeerCarrier.Line, "code"), null, 0);
        legacy.LineAnswer(0, "ERR @l1 unknown command 'LINK'");
        legacy.Nonce(1, "3F9A0C5E7B2D4F6A8C1E3B5D7F9A0C2E");
        legacy.Nonce(1, "3f9a0c5e7b2d4f6a8c1e3b5d7f9a0c2");
        var unproved = legacy.Drain();
        legacy.Nonce(2, "3f9a0c5e7b2d4f6a8c1e3b5d7f9a0c2e");
        var proved = legacy.Drain();
        Check(unproved.Select(w => w.Verb).SequenceEqual(["HELLO", "LINK"]) && unproved.All(w => w.Credential is null)
              && proved is [{ Verb: "AUTH-PROOF", Credential: "auth-proof" }] && !legacy.Follows(2),
            "peerlink core: at level 0 only a 32-lowercase-hex NONCE is proved, and nothing is taken before OK paired");
        legacy.LineAnswer(3, "OK paired");
        Check(legacy.State(3) == PeerLinkState.Live && legacy.Flags(3).Contains("legacy") && legacy.Light(3) == PeerLight.Amber && legacy.Follows(3),
            "peerlink core: a level-0 link that paired is live, legacy and amber");

        // A line connection greets once, whether its certificate is told before or after it is up.
        var greeted = new PeerLinkConnector(Settings(PeerCarrier.Line, "code"), null, 0);
        greeted.Connected(0);
        greeted.Certificate(0, pinned);
        Check(greeted.Drain().Select(w => w.Verb).SequenceEqual(["HELLO", "LINK"]) && greeted.Tls() == "none",
            "peerlink core: a line connection greets once, HELLO then LINK, and pins nothing before a proof");

        // The final answer: 30000 ms still waits, 30001 is unconfirmed; an accepted command waits for its final only with feature final.
        var final = new PeerLinkConnector(Settings(PeerCarrier.Line, "code", ["final"]), null, 0);
        final.Verified(0);
        var a = final.NextId();
        var b = final.NextId();
        final.Send(0, a);
        final.Send(0, b);
        final.Answer(10, b, PeerLink.ReadLine("OK @" + b + " {\"stage\":\"accepted\",\"effect\":\"queued\"}")!.Value.Reading);
        Check(a == "s1-11aa22bb" && final.InFlight == 2 && final.OutcomeOf(b) == PeerOutcome.Accepted, "peerlink core: ids are s<n>-<inst>, and an accepted command waits for its final answer");
        final.Answer(20, b, PeerLink.ReadLine("OK @" + b + " final {\"stage\":\"applied\"}")!.Value.Reading);
        Check(final.OutcomeOf(b) == PeerOutcome.Applied && final.InFlight == 1, "peerlink core: the final answer settles it");
        Check(final.OutcomeOf(a) is null && final.Record(PeerLink.ReceiptFinalMs).Counts.Unconfirmed == 0 && final.OutcomeOf(a) is null,
            "peerlink core: an answer may still come at receipt.final.ms");
        Check(final.Record(PeerLink.ReceiptFinalMs + 1).Counts.Unconfirmed == 1 && final.OutcomeOf(a) == PeerOutcome.Unconfirmed && final.InFlight == 0
              && final.WordsOf(a).StartsWith("no answer in 30 s", StringComparison.Ordinal),
            "peerlink core: past receipt.final.ms a written command with no final answer is unconfirmed");
        var plainAccepted = new PeerLinkConnector(Settings(PeerCarrier.Line, "code"), null, 0);
        plainAccepted.Verified(0);
        plainAccepted.Send(0, "s1-11aa22bb");
        plainAccepted.Answer(5, "s1-11aa22bb", PeerLink.ReadHttp(200, "{\"ok\":true}"));
        Check(plainAccepted.InFlight == 0 && plainAccepted.OutcomeOf("s1-11aa22bb") == PeerOutcome.Accepted, "peerlink core: without feature final, accepted is its last word");

        // 13.16.3: the record's fields, and their order, are the master's.
        var states = Between(text, "\n### 13.16 ", "\n### ") ?? "";
        var example = Between(states, "\n```\n{\"peer\"", "\n```") ?? "";
        var top = Regex.Matches(example, "\"([A-Za-z]+)\":").Select(m => m.Groups[1].Value).ToList();
        var nested = Regex.Matches(example, "\"([A-Za-z]+)\":\\{([^}]*)\\}").ToDictionary(m => m.Groups[1].Value,
            m => Regex.Matches(m.Groups[2].Value, "\"([A-Za-z]+)\"").Select(x => x.Groups[1].Value).ToList());
        var record = new PeerLinkConnector(Settings(PeerCarrier.Ws, "e63c317763b14546") with { Kind = "main", NodeId = "3f9c2a1b7d0e" }, null, 0);
        record.Certificate(0, pinned);
        record.Verified(0, "a41f09c2", "post");
        using (var doc = JsonDocument.Parse(record.Record(10).ToJson()))
        {
            var root = doc.RootElement;
            Check(top.Count > 10 && root.EnumerateObject().Select(p => p.Name).SequenceEqual(top)
                  && nested.All(n => n.Key is "floor" || root.GetProperty(n.Key).EnumerateObject().Select(p => p.Name).SequenceEqual(n.Value))
                  && root.GetProperty("floor").EnumerateObject().Select(p => p.Name).SequenceEqual(nested.GetValueOrDefault("floor") ?? []),
                "peerlink core: the record's fields, nested ones too, are 13.16.3's in its order (" + string.Join(",", root.EnumerateObject().Select(p => p.Name)) + ")");
            Check(Str(root, "state") == "live" && Str(root, "light") == "green" && Str(root, "mark") == "✓" && Str(root, "tls") == "pinned" && Str(root, "pin") == pinned[..16]
                  && Str(root, "kid") == "e63c317763b14546" && Str(root, "scope") == "post" && Int(root, "heardMs") == 10 && Int(root, "since") == 0,
                "peerlink core: a verified ws link's record: live, green ✓, pinned, its kid and scope, heard 10 ms ago");
            var json = root.GetRawText();
            Check(new[] { "K", "clientProof", "serverProof", "session", "snonce", "cnonce" }.All(f => Str(wsProof, f) is not { } secret || !json.Contains(secret, StringComparison.Ordinal)),
                "peerlink core: the record carries no K, proof, S or nonce");
        }

        // 13.12.6's store.
        var store = new PeerReceipts();
        const string conn = "patterns:9f8e7d6c";
        Check(store.Begin(conn, "s17-11aa22bb", 0) is null && store.Begin(conn, "s17-11aa22bb", 1) == "{\"ok\":true,\"stage\":\"accepted\",\"effect\":\"\",\"repeat\":true}",
            "peerlink core: a command whose id is held while it runs is answered accepted, with repeat — the receipt's fields alone, which the carrier wraps");
        using (var doc = JsonDocument.Parse(store.Answer(conn, ["s17-11aa22bb"], "a41f09c2", 1)))
            Check(Str(doc.RootElement.GetProperty("receipts").GetProperty("s17-11aa22bb"), "stage") == "accepted",
                "peerlink core: a RECEIPTS ask for an id that still runs is answered accepted, never unknown (13.12.6)");
        store.Keep(conn, "s17-11aa22bb", "{\"ok\":true,\"stage\":\"applied\",\"effect\":\"cut\"}", 2);
        var answer = store.Answer(conn, ["s17-11aa22bb", "s18-11aa22bb"], "a41f09c2", 3);
        using (var doc = JsonDocument.Parse(answer))
            Check(Str(doc.RootElement, "inst") == "a41f09c2" && doc.RootElement.GetProperty("receipts") is var rs && Str(rs.GetProperty("s17-11aa22bb"), "stage") == "applied"
                  && rs.GetProperty("s18-11aa22bb").GetString() == "unknown" && doc.RootElement.EnumerateObject().Select(p => p.Name).SequenceEqual(["inst", "receipts"]),
                "peerlink core: a RECEIPTS answer is 13.12.6's: the acceptor's inst, each receipt or unknown");
        Check(store.Begin("main:3f9c2a1b7d0e", "s17-11aa22bb", 4) is null, "peerlink core: receipts are kept per connector id");
        for (var i = 0; i < PeerLink.ReceiptsKept; i++) store.Keep(conn, "x" + i.ToString(CultureInfo.InvariantCulture), "{\"ok\":true,\"stage\":\"applied\"}", 5);
        Check(store.Begin(conn, "s17-11aa22bb", 6) is null && store.Begin(conn, "x255", 7) is not null, "peerlink core: the store keeps a connector's last receipts.kept ids");
        Check(store.Begin(conn, "x255", 5 + PeerLink.ReceiptsKeptMs + 1) is null, "peerlink core: the store keeps a receipt for receipts.kept.ms");

        // Request ids.
        Check(PeerLink.RequestId("rq", 1, "11aa22bb") == "rq1-11aa22bb" && Throws(() => PeerLink.RequestId("abcde", 1, "11aa22bb"))
              && Throws(() => PeerLink.RequestId("s", 1, new string('a', 30))) && Throws(() => PeerLink.RequestId("s", 0, "11aa22bb")),
            "peerlink core: a request id is 1-4 letters, a counter from 1 and the inst, within request.id.chars");

        // A hold persisted in wall time outlasts a restart whose monotonic clock started again.
        var held = new PeerLinkConnector(Settings(PeerCarrier.Ws, "e63c317763b14546"), null, new PeerNow(500, 1_000_000));
        held.Refused(new PeerNow(500, 1_000_000), "locked", 0);
        var restarted = new PeerLinkConnector(Settings(PeerCarrier.Ws, "e63c317763b14546"), held.Kept, new PeerNow(0, 1_030_000));
        Check(held.Kept.HeldUntilWall == 1_060_000 && restarted.HeldUntilWall(new PeerNow(0, 1_030_000)) == 1_060_000 && restarted.Refusal(new PeerNow(0, 1_030_000)) == "locked"
              && restarted.DialDue(new PeerNow(29_999, 1_059_999)) is null && restarted.DialDue(new PeerNow(30_000, 1_060_000)) == "tls",
            "peerlink core: a hold kept with its wall end holds after a restart until that end, on the new monotonic clock");
        restarted.LinkAgainNow(new PeerNow(30_001, 1_060_001));
        Check(restarted.DialDue(new PeerNow(30_001, 1_060_001)) == "tls" && restarted.State(new PeerNow(30_001, 1_060_001)) == PeerLinkState.Connecting,
            "peerlink core: Link again now dials at once");

        // Lights: connecting turns amber once its dial is older than peerlink.handshake.ms; down and held are red only when the show
        // needs the link.
        var dialling = new PeerLinkConnector(Settings(PeerCarrier.Ws, null), null, 0);
        dialling.Dial(0);
        dialling.Connected(4000);
        Check(dialling.Light(PeerLink.HandshakeMs) == PeerLight.Grey && dialling.Light(PeerLink.HandshakeMs + 1) == PeerLight.Amber
              && dialling.State(4000 + PeerLink.HandshakeMs) == PeerLinkState.Connecting,
            "peerlink core: a connecting light is grey, amber once its dial is older than peerlink.handshake.ms");
        PeerLight DownLight(bool needs)
        {
            var c = new PeerLinkConnector(Settings(PeerCarrier.Line, null) with { ShowNeeds = needs }, null, 0);
            c.Verified(0);
            c.Closed(10);
            return c.Light(11);
        }
        Check(DownLight(true) == PeerLight.Red && DownLight(false) == PeerLight.Amber, "peerlink core: a link down is red only when the show needs it");

        // The evaluator closes a link gone down and counts it as a failed dial; a self-gap holds it stale.
        var evaluated = new PeerLinkConnector(Settings(PeerCarrier.Line, null), null, 0);
        evaluated.Verified(0);
        var quiet = evaluated.Evaluate(PeerLink.DropMs);
        var gone = evaluated.Evaluate(PeerLink.DropMs + 1);
        Check(!quiet.Closed && gone.Closed && evaluated.State(PeerLink.DropMs + 1) == PeerLinkState.Down && evaluated.Attempt == 1,
            "peerlink core: the evaluator closes a link silent past link.drop.ms, a failed dial");

        Handshakes(Check, first, wsProof);
    }

    /// <summary>
    /// LINK end to end over the proof rows, and what the review of the core found: a verdict is taken only after this connection's one
    /// proof and only when the carrier's check of the acceptor's proof says so; the proof goes only to the id meant, over the
    /// certificate shown, at the version offered; the record binds to the id that proved, and another id waits for a person; a pin
    /// always names its certificate; the deadlines end what never finishes; a public viewer proves its key again; a local ws link runs
    /// plain; what came before the proof is counted; the commands kept are bounded; the words say why a link is keyless.
    /// </summary>
    static void Handshakes(Action<bool, string> Check, JsonElement first, JsonElement wsProof)
    {
        var pinned = Str(first, "bind")!["tls:sha256:".Length..];
        const string other = "8e8bc45bb14f9c7413604ef78eb1c42b465437cf700ae04a8f9553ffb0286edc";
        var k = Convert.FromHexString(Str(first, "K")!);
        var t = Transcript(first)!;
        var serverProof = Str(first, "serverProof")!;
        bool Checks(string proof) => PeerLink.Verify(PeerLink.ServerProof(k, t), proof);
        var okLink = $"OK @l1 LINK {Int(first, "speaks")} patterns 9f8e7d6c {Str(first, "snonce")} {Str(first, "bind")}";
        string Paired(string proof) => "OK @l2 paired {\"v\":1,\"verified\":\"code\",\"scope\":\"operate\",\"proof\":\"" + proof + "\",\"lid\":\"" + Str(first, "lid")
            + "\",\"level\":1,\"can\":[\"final\",\"receipts\"],\"inst\":\"a41f09c2\"}";
        PeerLinkConnector Linking(string? node, string? kind = null, string cert = "")
        {
            var c = new PeerLinkConnector(Settings(PeerCarrier.Line, "code") with { NodeId = node, Kind = kind }, null, 0, () => 0.5);
            c.Certificate(0, cert.Length > 0 ? cert : pinned);
            return c;
        }
        static string Credentials(IEnumerable<PeerWrite> writes) => string.Join(",", writes.Select(w => w.Verb + "/" + w.Credential));

        // The first proof row's link: one proof, then a verdict whose proof the carrier checked.
        var honest = Linking("9f8e7d6c");
        honest.LineAnswer(1, okLink);
        var wrote = honest.Drain();
        honest.LineAnswer(2, Paired(serverProof), Checks);
        Check(Credentials(wrote) == "HELLO/,LINK/,LINK/proof" && honest.State(2) == PeerLinkState.Live && honest.Record(2) is { Proof: "mutual", Scope: "operate", Tls: "pinned" }
              && honest.Kept is { Pin: { NodeId: "9f8e7d6c", Trust: PinTrust.Proof } p, Floor: { Tls: true, Level: 1 }, NodeId: "9f8e7d6c", NodeKind: "patterns" }
              && p.Fingerprint == pinned && honest.Follows(2) && honest.Drain().Count == 0,
            "peerlink core: the first proof row's link proves once and is verified by the acceptor's proof, pinned and floored for the id it proved (" + Credentials(wrote) + ")");

        // A verdict before the proof, or whose proof the carrier did not check or found wrong, ends the connection with nothing taken.
        var forged = new PeerLinkConnector(Settings(PeerCarrier.Line, "code") with { NodeId = "9f8e7d6c" }, null, 0, () => 0.5);
        forged.Certificate(0, other);
        forged.LineAnswer(1, Paired(new string('0', 64)), _ => true);
        Check(forged.State(1) == PeerLinkState.Down && !forged.Follows(1) && forged.Kept is { Pin: null, Floor: null } && forged.Tls() == "none"
              && !forged.KeepsConnection(1) && forged.Attempt == 1 && forged.Record(1).Words.Contains(PeerLink.ProofWords, StringComparison.Ordinal)
              && forged.Drain().All(w => w.Credential is null),
            "peerlink core: an OK paired before this connection's proof is no verdict: down, nothing pinned, floored or followed, in 13.6.3's words");
        foreach (var (proof, verifier, why) in new (string, Func<string, bool>?, string)[]
                 {
                     (Str(first, "clientProof")!, Checks, "the connector's own proof turned back"), (serverProof, null, "no check by the carrier"),
                     (new string('0', 64), _ => false, "a proof the carrier's check refuses"), ("not hex", _ => true, "a proof that is not 64 hex"),
                 })
        {
            var c = Linking("9f8e7d6c");
            c.LineAnswer(1, okLink);
            c.LineAnswer(2, Paired(proof), verifier);
            Check(c.State(2) == PeerLinkState.Down && !c.Follows(2) && c.Kept is { Pin: null, Floor: null } && !c.KeepsConnection(2)
                  && c.Record(2).Words.Contains(PeerLink.ProofWords, StringComparison.Ordinal),
                "peerlink core: a verdict is not taken on " + why + ": down, nothing pinned, floored or followed");
        }
        var twice = Linking("9f8e7d6c");
        twice.LineAnswer(1, okLink);
        twice.LineAnswer(2, okLink);
        Check(twice.Drain().Count(w => w.Credential == "proof") == 1 && twice.State(2) == PeerLinkState.Down && twice.Refusal(2) == "malformed"
              && twice.Record(2).Counts.Malformed == 1,
            "peerlink core: one proof a connection — a second answer to LINK is malformed and ends it");
        var again = Linking("9f8e7d6c");
        again.LineAnswer(1, okLink);
        again.LineAnswer(2, Paired(serverProof), Checks);
        again.LineAnswer(3, Paired(serverProof), Checks);
        Check(again.State(3) == PeerLinkState.Down && again.Refusal(3) == "malformed", "peerlink core: one verdict a connection — a second is malformed and ends it");

        // Only to the id meant, over the certificate shown, at the version offered.
        var moved = Linking("9f8e7d6c", "patterns");
        moved.LineAnswer(1, okLink.Replace("9f8e7d6c", "7a7a7a7a", StringComparison.Ordinal));
        var movedWords = moved.Record(1).Words;
        Check(moved.State(1) == PeerLinkState.Refused && moved.Refusal(1) == "other-id" && moved.Drain().All(w => w.Credential is null) && !moved.Follows(1)
              && movedWords.Contains("patterns 7a7a…", StringComparison.Ordinal) && movedWords.Contains("9f8e…", StringComparison.Ordinal),
            "peerlink core: an acceptor that answers LINK as another id is refused, naming both, with nothing proved (" + movedWords + ")");
        Check(moved.AcceptNewId() && !moved.AcceptNewId() && moved.NodeId == "7a7a7a7a" && moved.Kept is { NodeId: "7a7a7a7a", Pin: null, Floor: null }
              && moved.State(2) != PeerLinkState.Refused,
            "peerlink core: Accept new id binds the record to the id that answered, with nothing of the old node's");
        moved.Certificate(3, pinned);
        moved.LineAnswer(4, okLink.Replace("9f8e7d6c", "7a7a7a7a", StringComparison.Ordinal));
        Check(moved.Drain().Count(w => w.Credential == "proof") == 1, "peerlink core: once accepted, the new id is proved to");
        var kindless = Linking("9f8e7d6c", "patterns");
        kindless.LineAnswer(1, okLink.Replace(" patterns ", " playmap ", StringComparison.Ordinal));
        Check(kindless.Refusal(1) == "other-id", "peerlink core: the same id of another kind is another node");
        var typed = Linking(null);
        typed.LineAnswer(1, okLink);
        typed.LineAnswer(2, Paired(serverProof), Checks);
        typed.Closed(3);
        typed.Certificate(1000, pinned);
        typed.LineAnswer(1001, okLink.Replace("9f8e7d6c", "7a7a7a7a", StringComparison.Ordinal));
        Check(typed.NodeId == "9f8e7d6c" && typed.Kept is { NodeId: "9f8e7d6c", Pin.NodeId: "9f8e7d6c" } && typed.Refusal(1001) == "other-id"
              && typed.Drain().Count(w => w.Credential == "proof") == 1,
            "peerlink core: a typed address becomes the node its first verified link names, and its key is proved to no other");
        foreach (var (answer, cert, why) in new[]
                 {
                     (okLink, other, "a certificate other than the one shown"), (okLink.Replace("LINK 1 ", "LINK 2 ", StringComparison.Ordinal), pinned, "a version not offered"),
                     (okLink.Replace("LINK 1 ", "LINK 0 ", StringComparison.Ordinal), pinned, "a version below the one offered"), ("OK @l1 LINK 1 patterns 9f8e7d6c", pinned, "an answer it cannot read"),
                 })
        {
            var c = Linking("9f8e7d6c", cert: cert);
            c.LineAnswer(1, answer);
            Check(c.Drain().All(w => w.Credential is null) && c.State(1) == PeerLinkState.Down && !c.Follows(1),
                "peerlink core: no proof for " + why + "; the connection ends");
        }
        var plainBind = new PeerLinkConnector(Settings(PeerCarrier.Line, "code"), null, 0, () => 0.5);
        plainBind.NoTls(0);
        plainBind.LineAnswer(1, okLink);
        var floored = new PeerLinkConnector(Settings(PeerCarrier.Line, "code"), new PeerKept(null, new PeerFloor(true, 1, "mutual"), null, null), 0, () => 0.5);
        floored.Certificate(0, pinned);
        floored.LineAnswer(1, okLink.Replace("LINK 1 ", "LINK 0 ", StringComparison.Ordinal));
        Check(plainBind.Drain().All(w => w.Credential is null) && plainBind.Record(1).Words.Contains(PeerLink.ProofWords, StringComparison.Ordinal)
              && floored.Refusal(1) == "floor",
            "peerlink core: a plain connection answered with a certificate gets no proof; a lower version where a floor stands is refused");

        // On ws: the acceptor's link message, one proof, the verdict by the carrier's check.
        var wk = Convert.FromHexString(Str(wsProof, "K")!);
        var wt = Transcript(wsProof)!;
        var wsBind = Str(wsProof, "bind")!;
        PeerOffer? Offer(string bind) => PeerLink.ParseLinkOffer(JsonDocument.Parse("{\"type\":\"link\",\"v\":1,\"speaks\":1,\"server\":{\"t\":\"main\",\"id\":\"3f9c2a1b7d0e\"},\"snonce\":\""
            + Str(wsProof, "snonce") + "\",\"bind\":\"" + bind + "\"}").RootElement);
        JsonElement Auth(string proof) => JsonDocument.Parse("{\"type\":\"auth\",\"ok\":true,\"scope\":\"post\",\"node\":{\"t\":\"patterns\",\"id\":\"9f8e7d6c\",\"verified\":\"key\"},\"proof\":\""
            + proof + "\",\"lid\":\"" + Str(wsProof, "lid") + "\",\"level\":1,\"can\":[\"final\"],\"inst\":\"a41f09c2\",\"beatMs\":2000}").RootElement.Clone();
        bool WsChecks(string proof) => PeerLink.Verify(PeerLink.ServerProof(wk, wt), proof);
        PeerLinkConnector WsLinking()
        {
            var c = new PeerLinkConnector(Settings(PeerCarrier.Ws, Str(wsProof, "kid")) with { NodeId = "3f9c2a1b7d0e" }, null, 0, () => 0.5);
            c.Certificate(0, wsBind["tls:sha256:".Length..]);
            c.LinkOffered(1, Offer(wsBind));
            return c;
        }
        var good = WsLinking();
        var wsWrote = good.Drain();
        good.Verdict(2, Auth(Str(wsProof, "serverProof")!), WsChecks);
        Check(Offer(wsBind) is { Speaks: 1, T: "main", Id: "3f9c2a1b7d0e" } && Credentials(wsWrote) == "link/proof" && good.State(2) == PeerLinkState.Live
              && good.Record(2) is { Tls: "pinned", Scope: "post", Proof: "mutual" } && good.Follows(2),
            "peerlink core: a ws link proves once to the link message and is verified by the acceptor's proof (" + Credentials(wsWrote) + ")");
        var early = WsLinking();
        early.Frame(2);
        early.Verdict(3, Auth(new string('0', 64)), WsChecks);
        Check(early.Record(3).Counts.Malformed == 1 && early.State(3) == PeerLinkState.Down && early.Kept.Pin is null && !early.Follows(3),
            "peerlink core: a frame before the ws verdict is discarded and counted; a verdict whose proof does not verify ends the socket");
        var refusedWs = WsLinking();
        refusedWs.Verdict(2, JsonDocument.Parse("{\"type\":\"auth\",\"ok\":false,\"error\":\"unknown key\",\"class\":\"refused\",\"retry\":90}").RootElement);
        Check(refusedWs.State(2) == PeerLinkState.Held && refusedWs.Refusal(2) == "key" && refusedWs.HeldUntilWall(2) == 90_002 && refusedWs.Follows(2)
              && refusedWs.Flags(2).Contains("keyless"),
            "peerlink core: a ws verdict that refuses the key holds it for its retry and keeps the socket a public viewer");
        var before = new PeerLinkConnector(Settings(PeerCarrier.Line, "code"), null, 0);
        before.Connected(0);
        before.Frame(3);
        before.Heard(4);
        Check(before.Record(4).Counts.Malformed == 2 && !before.Follows(4) && before.Flags(4).Contains("malformed"),
            "peerlink core: what arrives before the acceptor's proof verifies is discarded and counted");

        // A pin always names its certificate: none is made from a certificate never told, none is kept without one, and the bearer goes
        // only to the certificate a proof or a person pinned.
        var unseen = new PeerLinkConnector(Settings(PeerCarrier.Line, "code"), null, 0);
        unseen.Connected(0);
        unseen.Verified(1);
        var blank = new PeerLinkConnector(Settings(PeerCarrier.Ws, "e63c317763b14546"), new PeerKept(new PeerPin("3f9c2a1b7d0e", "", PinTrust.Proof), null, null, null), 0);
        blank.Certificate(0, other);
        blank.FirstFrame(0);
        Check(unseen.Kept is { Pin: null, Floor.Tls: true } && blank.Drain().All(w => w.Credential is null) && blank.Tls() == "first" && blank.State(0) != PeerLinkState.Refused,
            "peerlink core: no pin without a certificate — a link whose certificate was never told pins none, and a kept pin naming none is no pin");
        PeerLinkConnector Bearer(PinTrust trust, string shown)
        {
            var c = new PeerLinkConnector(Settings(PeerCarrier.Ws, "e63c317763b14546"), new PeerKept(new PeerPin("3f9c2a1b7d0e", pinned, trust), null, null, null), 0);
            c.Certificate(0, shown);
            c.FirstFrame(0);
            return c;
        }
        var byPerson = Bearer(PinTrust.Person, pinned);
        var byProof = Bearer(PinTrust.Proof, pinned);
        var changedWs = Bearer(PinTrust.Proof, other);
        var firstWs = Bearer(PinTrust.First, other);
        Check(Credentials(byPerson.Drain()) == "/bearer" && Credentials(byProof.Drain()) == "/bearer" && changedWs.Drain().Count == 0
              && changedWs.Refusal(0) == "certificate" && firstWs.Drain().Count == 0 && firstWs.Flags(0).Contains("note"),
            "peerlink core: a level-0 bearer goes only to the certificate a proof or a person pinned, never to another or to a first pin");
        var trusting = new PeerLinkConnector(Settings(PeerCarrier.Ws, "e63c317763b14546") with { Name = "Hall A" }, null, 0);
        trusting.Certificate(0, other);
        trusting.FirstFrame(0);
        var trustWords = trusting.Record(0).Words;
        Check(trustWords.Contains("8e8b c45b b14f 9c74", StringComparison.Ordinal) && trustWords.Contains("press Trust to send the key", StringComparison.Ordinal),
            "peerlink core: a keyless level-0 ws link says how to trust it: " + trustWords);

        // The deadlines: no connection, no answer to LINK (level 0, its remembered NONCE proved), no link within the handshake's time.
        var unanswered = new PeerLinkConnector(Settings(PeerCarrier.Line, "code"), null, 0, () => 0.5);
        unanswered.Dial(0);
        Check(unanswered.State(PeerLink.ConnectTimeoutMs) == PeerLinkState.Connecting && unanswered.State(PeerLink.ConnectTimeoutMs + 1) == PeerLinkState.Down
              && unanswered.Attempt == 1 && !unanswered.KeepsConnection(PeerLink.ConnectTimeoutMs + 1)
              && unanswered.DialDue(PeerLink.ConnectTimeoutMs + 500) is null && unanswered.DialDue(PeerLink.ConnectTimeoutMs + 501) == "tls",
            "peerlink core: a dial with no connection within link.connect.timeout.ms is a failed dial, its redial waiting from its deadline");
        var silent = new PeerLinkConnector(Settings(PeerCarrier.Line, "code"), null, 0, () => 0.5);
        silent.Connected(0);
        silent.Nonce(0, "3f9a0c5e7b2d4f6a8c1e3b5d7f9a0c2e");
        var greet = silent.Drain();
        var atDeadline = !silent.Flags(PeerLink.ReceiptDeadlineMs).Contains("legacy") && silent.Drain().Count == 0;
        var pastDeadline = silent.Flags(PeerLink.ReceiptDeadlineMs + 1).Contains("legacy") && Credentials(silent.Drain()) == "AUTH-PROOF/auth-proof";
        Check(Credentials(greet) == "HELLO/,LINK/" && atDeadline && pastDeadline && silent.State(PeerLink.HandshakeMs) == PeerLinkState.Connecting
              && silent.State(PeerLink.HandshakeMs + 1) == PeerLinkState.Down && silent.Attempt == 1
              && silent.Record(PeerLink.HandshakeMs + 1).Words.Contains("no link in 10 s", StringComparison.Ordinal),
            "peerlink core: a line acceptor silent to LINK past receipt.deadline.ms is level 0, its NONCE answered then; unfinished past peerlink.handshake.ms, closed");

        // A public viewer proves its key again: at once on Link again now, or when a refusal's 13.15.1 wait ends.
        var viewer = new PeerLinkConnector(Settings(PeerCarrier.Ws, "e63c317763b14546"), null, 0, () => 0.5);
        viewer.Refused(0, "refused", 0);
        viewer.LinkAgainNow(1000);
        var busy = new PeerLinkConnector(Settings(PeerCarrier.Ws, "e63c317763b14546"), null, 0, () => 0.5);
        busy.Refused(0, "busy", 0);
        var busyWords = busy.Record(1).Words;
        Check(!viewer.KeepsConnection(1000) && viewer.DialDue(1000) == "tls" && busy.KeepsConnection(499) && busy.Refusal(499) == "busy"
              && busyWords.Contains("did not take this desk's key (busy)", StringComparison.Ordinal) && !busy.KeepsConnection(500) && busy.DialDue(500) == "tls",
            "peerlink core: a ws socket kept as a public viewer ends for a fresh link= socket on Link again now or at its refusal's wait (" + busyWords + ")");

        // A local ws link runs plain and proves; one to another machine with no TLS reads keyless and says why.
        var local = new PeerLinkConnector(Settings(PeerCarrier.Ws, "e63c317763b14546") with { Local = true }, null, 0);
        local.NoTls(0);
        local.LinkOffered(0, Offer("plain"));
        var away = new PeerLinkConnector(Settings(PeerCarrier.Ws, "e63c317763b14546"), null, 0);
        away.NoTls(0);
        Check(Credentials(local.Drain()) == "link/proof" && local.Tls() == "local" && !local.Flags(0).Contains("keyless") && !local.Flags(0).Contains("plain")
              && away.Drain().Count == 0 && away.Flags(0).Contains("keyless") && away.Record(0).Words == Node + " offers no TLS — the key stays on this machine",
            "peerlink core: a local ws link may be plain and proves on it; off this machine with no TLS the key stays here, and the words say so");

        // Accept certificate takes the hex as the desk shows it.
        foreach (var typedHex in new[] { "8E8BC45B", "8e8b c45b" })
        {
            var c = new PeerLinkConnector(Settings(PeerCarrier.Line, "code"), new PeerKept(new PeerPin("9f8e7d6c", pinned, PinTrust.Proof), null, null, null), 0);
            c.Certificate(0, other);
            Check(c.AcceptCertificate(typedHex) && c.Kept.Pin is { Fingerprint: other, Trust: PinTrust.Person }, "peerlink core: Accept certificate takes '" + typedHex + "'");
        }

        // The commands kept: every one in flight, and the last receipts.kept to settle; an accepted one settled late leaves in its turn.
        var many = new PeerLinkConnector(Settings(PeerCarrier.Line, "code", ["final"]), null, 0);
        many.Verified(0);
        var held = many.NextId();
        many.Send(0, held);
        var lateFinal = many.NextId();
        many.Send(0, lateFinal);
        many.Answer(1, lateFinal, new PeerReading(PeerOutcome.Accepted, "queued"));
        var ids = new List<string>();
        void Settled(long at, int count)
        {
            for (var i = 0; i < count; i++)
            {
                var id = many.NextId();
                ids.Add(id);
                many.Send(at, id);
                many.Answer(at, id, new PeerReading(PeerOutcome.Applied, ""));
            }
        }
        Settled(2, 400);
        many.Answer(3, lateFinal, new PeerReading(PeerOutcome.Applied, "", Final: true));
        Settled(4, 400);
        Check(many.Knows(held) && many.OutcomeOf(held) is null && many.InFlight == 1 && !many.Knows(lateFinal) && !many.Knows(ids[0])
              && ids.TakeLast(PeerLink.ReceiptsKept).All(many.Knows) && ids.Count(many.Knows) <= PeerLink.ReceiptsKept + PeerLink.InflightMax,
            "peerlink core: the commands kept are bounded — none waiting evicted, the last receipts.kept settled known, an accepted one let go once final");
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>The lines of the one fenced block <c>```word</c>.</summary>
    static string[]? Fenced(string text, string word)
        => Between(text, "\n```" + word + "\n", "\n```") is { } inner ? inner[("\n```" + word + "\n").Length..].Split('\n') : null;

    static string? Between(string text, string from, string to)
    {
        var i = text.IndexOf(from, StringComparison.Ordinal);
        if (i < 0) return null;
        var j = text.IndexOf(to, i + from.Length, StringComparison.Ordinal);
        return j < 0 ? text[i..] : text[i..j];
    }

    /// <summary>A proof row's transcript, by the core; <paramref name="field"/> taken from <paramref name="from"/> when named.</summary>
    static string? Transcript(JsonElement r, string? field = null, JsonElement from = default)
    {
        JsonElement V(string f) => f == field ? from.GetProperty(f) : r.GetProperty(f);
        string S(string f) => V(f).GetString() ?? "";
        return PeerLink.Transcript(S("carrier"), V("offer").GetInt64(), V("speaks").GetInt64(), S("server"), S("client"), S("kid"), S("snonce"), S("cnonce"), S("bind"));
    }

    static (string T, string Id)? Split(string? tid) => tid?.IndexOf(':') is { } i and > 0 ? (tid[..i], tid[(i + 1)..]) : null;

    static bool Throws(Action a)
    {
        try { a(); }
        catch (ArgumentException) { return true; }
        return false;
    }

    static string Short(JsonElement r) => r.GetRawText() is var t && t.Length > 90 ? t[..90] : r.GetRawText();

    static string Hex(byte[] b) => Convert.ToHexStringLower(b);

    static string? Str(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static long? Int(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;

    static double? Num(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    static bool? Bool(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null } : null;

    static List<JsonElement> Items(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().ToList() : [];

    static List<string> Strings(JsonElement e, string name) => Items(e, name).Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToList();
}
