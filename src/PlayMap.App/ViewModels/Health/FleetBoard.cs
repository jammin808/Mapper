using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using PlayMap.App.Infrastructure;
using PlayMap.App.Link;
using PlayMap.App.State;
using PlayMap.Core.Fleet;

namespace PlayMap.App.ViewModels.Health;

/// <summary>One machine on the fleet board: its light (a machine not heard for 3 s is amber "quiet", for 6 s red "gone" — never
/// silently removed), its name, its role chip, its headline, and its actions (the same list its right-click menu shows).</summary>
public sealed class MachineCardViewModel : ObservableObject, IMenuTarget
{
    private string _light = "grey";
    private string _name = "";
    private string _role = "";
    private string _heard = "";
    private string _headline = "";
    private string _detail = "";
    private IReadOnlyList<DeskAction> _actions = [];
    private IReadOnlyList<DeskAction> _toolbar = [];

    public MachineCardViewModel(string id, bool isSelf)
    {
        Id = id;
        IsSelf = isSelf;
    }

    /// <summary>The machine's node id ("" for this node before STATE names it).</summary>
    public string Id { get; }

    /// <summary>True for the card of the node the desk is attached to.</summary>
    public bool IsSelf { get; }

    public string Light
    {
        get => _light;
        private set => Set(ref _light, value);
    }

    public string Name
    {
        get => _name;
        private set => Set(ref _name, value);
    }

    /// <summary>"PRIMARY", "BACKUP HOT", "FENCED", "SOLO", "BACKUP", "" when STATE names none.</summary>
    public string Role
    {
        get => _role;
        private set
        {
            if (Set(ref _role, value))
                Raise(nameof(HasRole));
        }
    }

    public bool HasRole => _role.Length > 0;

    /// <summary>"quiet" (stale) or "gone" while the machine is not heard, "" while it is.</summary>
    public string Heard
    {
        get => _heard;
        private set
        {
            if (Set(ref _heard, value))
                Raise(nameof(IsQuiet));
        }
    }

    public bool IsQuiet => _heard.Length > 0;

    public string Headline
    {
        get => _headline;
        private set => Set(ref _headline, value);
    }

    /// <summary>The mirror's or the backups' line, the last takeover.</summary>
    public string Detail
    {
        get => _detail;
        private set
        {
            if (Set(ref _detail, value))
                Raise(nameof(HasDetail));
        }
    }

    public bool HasDetail => _detail.Length > 0;

    /// <summary>The card's actions, its right-click menu. The board hands the same list instance from push to push, so a press held
    /// on one of its buttons across a push keeps its button.</summary>
    public IReadOnlyList<DeskAction> Actions
    {
        get => _actions;
        set
        {
            if (!Set(ref _actions, value))
                return;
            _toolbar = value.Toolbar();
            Raise(nameof(Toolbar));
        }
    }

    /// <summary>The card's buttons: its actions less the menu-only ones.</summary>
    public IReadOnlyList<DeskAction> Toolbar => _toolbar;

    IReadOnlyList<DeskAction> IMenuTarget.MenuActions => _actions;

    public void Update(string light, string name, string role, string heard, string headline, string detail)
    {
        Light = light;
        Name = name;
        Role = role;
        Heard = heard;
        Headline = headline;
        Detail = detail;
        _actions.RefreshAll();
    }
}

/// <summary>Health ▸ Fleet: every PlayMap machine this node hears, as cards. This node's card carries its role in a hot-backup pair
/// (STATE.fleet.role, the mirror's or the backups' words, the last takeover) and the fleet verbs that act on this node:
/// FAILOVER on a hot backup, FLEET SOLO to stop backing up, FLEET BACKUP to mirror the node that has the show when fenced — each a
/// hold-to-confirm button, as the module's keys hold them, and the same instances from push to push. Each
/// other machine's card (STATE.fleet.nodes, a primary's backups) is lit by how recently it was heard — amber when quiet, red when
/// gone — and focuses the board (FLEET FOCUS). The lens chips, ‹ › and Reset are the board's view verbs, the words Companion's EYE
/// keys use.</summary>
public sealed class FleetBoardViewModel : ObservableObject
{
    private static readonly string[] Lenses = ["ALL", "OUTPUTS", "PROJECTORS", "SYNC", "BACKUP", "PROBLEMS"];
    private static readonly IReadOnlyList<DeskAction> NoActions = [];

    private readonly DeskSession _session;
    private readonly Dictionary<string, MachineCardViewModel> _cards = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<DeskAction>> _focus = new(StringComparer.Ordinal);
    private readonly IReadOnlyList<DeskAction> _onBackup;
    private readonly IReadOnlyList<DeskAction> _onFenced;
    private FleetFacts _facts = FleetFacts.None;
    private string _headline = "";
    private string _light = "grey";
    private string _lens = "all";

    public FleetBoardViewModel(DeskSession session)
    {
        _session = session;
        Self = new MachineCardViewModel("", isSelf: true);
        Failover = DeskAction.Send(session, "fleet.failover", "Failover", () => "FAILOVER", isEnabled: () => session.CanSend).Look("play", danger: true, hold: true);
        Solo = DeskAction.Send(session, "fleet.solo", "Stop backing up", () => "FLEET SOLO", isEnabled: () => session.IsLive).Look("close", menuOnly: true, hold: true);
        Mirror = DeskAction.Send(session, "fleet.mirror", "Back up the node with the show", () => _facts.Of is { Length: > 0 } of ? "FLEET BACKUP " + of + " HOT" : null,
            isEnabled: () => session.IsLive && _facts.Of is { Length: > 0 }).Look("fleet", hold: true);
        _onBackup = [Failover, Solo];
        _onFenced = [Mirror];
        LensActions = [.. Lenses.Select(l => DeskAction.Send(session, "fleet.lens." + l, Title(l), () => "FLEET LENS " + l,
            isChecked: () => string.Equals(_lens, l, StringComparison.OrdinalIgnoreCase), isEnabled: () => session.IsLive))];
        Actions =
        [
            DeskAction.Send(session, "fleet.prev", "Previous problem", () => "FLEET PREV", isEnabled: () => session.IsLive).Look("prev"),
            DeskAction.Send(session, "fleet.next", "Next problem", () => "FLEET NEXT", isEnabled: () => session.IsLive).Look("next"),
            DeskAction.Send(session, "fleet.reset", "Reset", () => "FLEET RESET", isEnabled: () => session.IsLive).Look("undo"),
        ];
    }

    private static string Title(string lens) => char.ToUpperInvariant(lens[0]) + lens[1..].ToLowerInvariant();

    /// <summary>This node's card.</summary>
    public MachineCardViewModel Self { get; }

    /// <summary>FAILOVER: this hot backup takes the show from its primary (on its card while it is a backup).</summary>
    public DeskAction Failover { get; }

    /// <summary>FLEET SOLO: this node stops backing up.</summary>
    public DeskAction Solo { get; }

    /// <summary>FLEET BACKUP &lt;the node with the show&gt; HOT, on a fenced node.</summary>
    public DeskAction Mirror { get; }

    /// <summary>All · Outputs · Projectors · Sync · Backup · Problems (FLEET LENS).</summary>
    public IReadOnlyList<DeskAction> LensActions { get; }

    /// <summary>‹ › and Reset (the board's toolbar; its right-click menu is the same list).</summary>
    public IReadOnlyList<DeskAction> Actions { get; }

    public IReadOnlyList<DeskAction> Toolbar => Actions;

    /// <summary>Every machine's card: this node first, then the others in STATE's order.</summary>
    public ObservableCollection<MachineCardViewModel> Machines { get; } = [];

    public string Headline
    {
        get => _headline;
        private set => Set(ref _headline, value);
    }

    public string Light
    {
        get => _light;
        private set => Set(ref _light, value);
    }

    /// <summary>The facts the board last read.</summary>
    public FleetFacts Facts => _facts;

    public void OnState(StateSnapshot state)
    {
        ArgumentNullException.ThrowIfNull(state);
        _facts = state.Section("fleet", FleetFacts.Read);
        _lens = state.Root.ValueKind == JsonValueKind.Object && state.Root.TryGetProperty("eye", out var eye) ? eye.Str("lens", "all") : "all";
        Headline = _facts.Headline;
        Light = _facts.Light;
        var want = new List<MachineCardViewModel> { Self };
        if (_facts.Present)
        {
            Self.Actions = _facts.Role switch
            {
                FleetRoles.Backup => _onBackup,
                FleetRoles.Fenced => _onFenced,
                _ => NoActions,
            };
            Self.Update(_facts.Light, state.Name.Length > 0 ? state.Name + " (this machine)" : "This machine", RoleWord(_facts.Role), "",
                _facts.Headline, _facts.SelfDetail);
            foreach (var m in _facts.Machines)
            {
                if (!_cards.TryGetValue(m.Id, out var card))
                {
                    card = new MachineCardViewModel(m.Id, isSelf: false);
                    _cards[m.Id] = card;
                }
                card.Actions = FocusOf(m.Id);
                card.Update(m.Light, m.Name, RoleWord(m.Role), m.Heard, m.Headline, m.Detail);
                want.Add(card);
            }
        }
        else
        {
            Self.Actions = NoActions;
            Self.Update("grey", "This machine", "", "", "This node does not watch other machines' health (no fleet monitor).", "");
        }
        foreach (var id in _cards.Keys.Where(id => !want.Any(c => c.Id == id)).ToList())
        {
            _cards.Remove(id);
            _focus.Remove(id);
        }
        if (!Machines.SequenceEqual(want))
        {
            Machines.Clear();
            foreach (var c in want)
                Machines.Add(c);
        }
    }

    /// <summary>A card's own action: focus the board on that machine (the same instance from push to push).</summary>
    private IReadOnlyList<DeskAction> FocusOf(string id)
    {
        if (_focus.TryGetValue(id, out var had))
            return had;
        IReadOnlyList<DeskAction> made = [DeskAction.Send(_session, "fleet.focus", "Focus", () => "FLEET FOCUS " + id, isEnabled: () => _session.IsLive).Look("select")];
        _focus[id] = made;
        return made;
    }

    public void RefreshActions()
    {
        new[] { Failover, Solo, Mirror }.RefreshAll();
        LensActions.RefreshAll();
        Actions.RefreshAll();
        foreach (var c in Machines)
            c.Actions.RefreshAll();
    }

    /// <summary>The role chip's word: a hot backup says so.</summary>
    public static string RoleWord(string role) => role switch
    {
        FleetRoles.Backup => "BACKUP HOT",
        "" => "",
        _ => role.ToUpperInvariant(),
    };
}

/// <summary>One other machine as STATE.fleet names it.</summary>
public sealed record FleetMachine(string Id, string Name, string Role, string Light, string Heard, string Headline, string Detail);

/// <summary>STATE.fleet as the fleet board reads it: this node's role, epoch and light, the mirror's words on a hot backup, a
/// primary's backups and the last takeover, and every other machine with its light by how recently it was heard.</summary>
public sealed record FleetFacts(bool Present, string Role, long Epoch, string Light, string Headline, string? Of, string SelfDetail,
    ImmutableArray<FleetMachine> Machines)
{
    public static readonly FleetFacts None = new(false, FleetRoles.Solo, 0, "grey", "", null, "", []);

    public bool Equals(FleetFacts? other) =>
        other is not null && (Present, Role, Epoch, Light, Headline, Of, SelfDetail) ==
        (other.Present, other.Role, other.Epoch, other.Light, other.Headline, other.Of, other.SelfDetail)
        && Machines.AsSpan().SequenceEqual(other.Machines.AsSpan());

    public override int GetHashCode() => HashCode.Combine(Role, Epoch, Light, Machines.Length);

    /// <summary>A machine's light by how recently it was heard: its own light while live, amber while quiet, red once gone.</summary>
    public static (string Light, string Heard) HeardLight(string light, string heard) => heard switch
    {
        "stale" => ("amber", "quiet"),
        "gone" => ("red", "gone"),
        _ => (light.Length > 0 ? light : "grey", ""),
    };

    public static FleetFacts Read(JsonElement? e)
    {
        if (e is not { ValueKind: JsonValueKind.Object } f)
            return None;
        var role = f.Str("role", FleetRoles.Solo);
        var lines = new List<string>();
        if (f.Obj("mirror") is { } mirror && mirror.Str("words") is { Length: > 0 } mw)
            lines.Add(mw);
        foreach (var b in f.Arr("backups"))
            lines.Add(b.Str("words", b.Str("name") + ": " + b.Str("state")));
        if (f.Obj("last") is { } last && last.Str("words") is { Length: > 0 } lw)
            lines.Add(lw);
        var epoch = f.Long("epoch");
        if (epoch > 0)
            lines.Add("epoch " + epoch.ToString(CultureInfo.InvariantCulture));
        var machines = ImmutableArray.CreateBuilder<FleetMachine>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var n in f.Arr("nodes"))
        {
            var id = n.Str("id");
            if (id.Length == 0 || !seen.Add(id))
                continue;
            var (light, heard) = HeardLight(n.Str("light", "grey"), n.Str("heard", "live"));
            var build = n.Str("build");
            machines.Add(new FleetMachine(id, n.Str("name", id), n.Str("role"), light, heard, n.Str("headline"), build.Length > 0 ? "build " + build : ""));
        }
        // A primary's backups and a backup's primary are machines too, when the monitor does not list them.
        foreach (var b in f.Arr("backups"))
        {
            var id = b.Str("node");
            if (id.Length == 0 || !seen.Add(id))
                continue;
            machines.Add(new FleetMachine(id, b.Str("name", id), FleetRoles.Backup, b.Str("light", "grey"), "", b.Str("words"), b.Str("state")));
        }
        if (f.Obj("mirror") is { } m && m.Str("of") is { Length: > 0 } of && seen.Add(of))
        {
            var light = CardReader.LightWord(m.Str("state"));
            machines.Add(new FleetMachine(of, m.Str("name", of), FleetRoles.Primary, light, "", "has the show", m.Str("state")));
        }
        return new FleetFacts(true, role, epoch, f.Str("light", "grey"), f.Str("headline"), f.StrOrNull("of"), string.Join(" · ", lines),
            machines.ToImmutable());
    }
}
