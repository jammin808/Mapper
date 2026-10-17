using System.Collections.Immutable;
using System.Collections.ObjectModel;
using PlayMap.App.Infrastructure;
using PlayMap.App.Link;
using PlayMap.App.State;

namespace PlayMap.App.ViewModels.Cards;

/// <summary>One field on a card: its label and its value now.</summary>
public sealed class CardFieldViewModel(string label) : ObservableObject
{
    private string _value = "";

    public string Label { get; } = label;

    public string Value
    {
        get => _value;
        set => Set(ref _value, value);
    }
}

/// <summary>A generic STATE card: a package's block of STATE with its light, its words, its fields (six on its face, the rest
/// under "more") and its actions, each a catalogue verb line (the card's toolbar and its right-click menu are the same list).
/// Updated in place on every push, so a value that moves repaints only itself.</summary>
public sealed class CardViewModel : ObservableObject
{
    private CardInstance _instance;
    private string _title = "";
    private string _light = "grey";
    private string _words = "";

    public CardViewModel(DeskSession session, CardInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        _instance = instance;
        Key = KeyOf(instance);
        Package = instance.Descriptor.Package;
        var fields = instance.Fields.Select(f => new CardFieldViewModel(f.Label)).ToList();
        _all = fields;
        Fields = [.. fields.Take(CardDescriptor.MaxFields)];
        More = [.. fields.Skip(CardDescriptor.MaxFields)];
        var actions = new List<DeskAction>();
        for (var i = 0; i < instance.Actions.Length; i++)
        {
            var at = i;
            actions.Add(DeskAction.Send(session, Key + "#" + at.ToString(System.Globalization.CultureInfo.InvariantCulture), instance.Actions[at].Label,
                () => _instance.Actions[at].Line, isEnabled: () => session.IsLive && _instance.Actions[at].Line is not null,
                whyNot: () => _instance.Actions[at].Missing));
        }
        Actions = actions;
        Update(instance);
    }

    public static string KeyOf(CardInstance c)
    {
        ArgumentNullException.ThrowIfNull(c);
        return c.Descriptor.Package + "/" + c.Descriptor.Index.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/" + c.Key;
    }

    public string Key { get; }

    public string Package { get; }

    /// <summary>The entity the card is about (an output, a media item…), or null.</summary>
    public string? EntityId => _instance.EntityId;

    public CardInstance Instance => _instance;

    public string Title
    {
        get => _title;
        private set => Set(ref _title, value);
    }

    public string Light
    {
        get => _light;
        private set => Set(ref _light, value);
    }

    public string Words
    {
        get => _words;
        private set
        {
            if (Set(ref _words, value))
                Raise(nameof(HasWords));
        }
    }

    public bool HasWords => _words.Length > 0;

    private readonly List<CardFieldViewModel> _all;

    /// <summary>The fields on the card's face (the descriptor's first six).</summary>
    public IReadOnlyList<CardFieldViewModel> Fields { get; }

    /// <summary>The descriptor's fields after the sixth, under the card's "more".</summary>
    public IReadOnlyList<CardFieldViewModel> More { get; }

    public bool HasMore => More.Count > 0;

    /// <summary>"4 more".</summary>
    public string MoreText => More.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + " more";

    /// <summary>The card's actions (its toolbar and its right-click menu).</summary>
    public IReadOnlyList<DeskAction> Actions { get; }

    public IReadOnlyList<DeskAction> Toolbar => Actions;

    public void Update(CardInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        _instance = instance;
        Title = instance.Title;
        Light = instance.Light;
        Words = instance.Words;
        for (var i = 0; i < _all.Count && i < instance.Fields.Length; i++)
            _all[i].Value = instance.Fields[i].Value;
    }
}

/// <summary>A drawer tab of cards: every card of the packages it takes, in the descriptors' order.</summary>
public sealed class CardsPanelViewModel(string title, Func<string, bool> takes) : ObservableObject
{
    public string Title { get; } = title;

    public ObservableCollection<CardViewModel> Cards { get; } = [];

    public bool IsEmpty => Cards.Count == 0;

    /// <summary>"No package on this node publishes …" while empty.</summary>
    public string EmptyWords => "Nothing here publishes STATE on this node yet.";

    /// <summary>Brings the tab in line with the cards shown: a card that stays keeps its view (an open "more" stays open), one
    /// that comes is inserted at its place, one that goes is removed.</summary>
    internal void Sync(IReadOnlyList<CardViewModel> all)
    {
        var want = all.Where(c => takes(c.Package)).ToList();
        if (want.SequenceEqual(Cards))
            return;
        var wasEmpty = Cards.Count == 0;
        Cards.SyncById(want, c => c.Key, c => c.Key, c => c, (_, _) => { });
        if (wasEmpty != (Cards.Count == 0))
            Raise(nameof(IsEmpty));
    }
}

/// <summary>The generic cards: every descriptor built into the desk, filled from each STATE push. They show under Health
/// (all of them), under Inputs (the controllers', the sound's and the live video's), and in the inspector of the object a
/// card is about, so no package's STATE stays invisible. A card whose block is absent is not shown.</summary>
public sealed class CardsModule : DeskModule
{
    private static readonly HashSet<string> InputPackages = new(StringComparer.Ordinal) { "control", "audio", "videoio", "phonescan", "companion" };

    private readonly DeskSession _session;
    private readonly ImmutableArray<CardDescriptor> _descriptors;
    private readonly Dictionary<string, CardViewModel> _cards = new(StringComparer.Ordinal);
    private List<CardViewModel> _shown = [];

    public CardsModule(DeskSession session, ImmutableArray<CardDescriptor>? descriptors = null)
    {
        _session = session;
        _descriptors = descriptors ?? CardDescriptor.BuiltIn;
        Health = new CardsPanelViewModel("Cards", _ => true);
        Inputs = new CardsPanelViewModel("Inputs", InputPackages.Contains);
    }

    public CardsPanelViewModel Health { get; }

    public CardsPanelViewModel Inputs { get; }

    /// <summary>The cards STATE fills now, in the descriptors' order.</summary>
    public IReadOnlyList<CardViewModel> Shown => _shown;

    public ImmutableArray<CardDescriptor> Descriptors => _descriptors;

    public override IEnumerable<DrawerTab> DrawerTabs() =>
    [
        new DrawerTab("health", "cards", "Cards", Health, 50),
        new DrawerTab("inputs", "cards", "Inputs", Inputs, 50),
    ];

    public override void OnState(StateSnapshot state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var shown = new List<CardViewModel>();
        foreach (var instance in CardReader.ReadAll(_descriptors, state.Root))
        {
            var key = CardViewModel.KeyOf(instance);
            if (_cards.TryGetValue(key, out var card))
                card.Update(instance);
            else
            {
                card = new CardViewModel(_session, instance);
                _cards[key] = card;
            }
            shown.Add(card);
        }
        _shown = shown;
        // Only cards STATE still fills keep their view models.
        if (_cards.Count > shown.Count)
        {
            var keep = shown.Select(c => c.Key).ToHashSet(StringComparer.Ordinal);
            foreach (var k in _cards.Keys.Where(k => !keep.Contains(k)).ToList())
                _cards.Remove(k);
        }
        Health.Sync(shown);
        Inputs.Sync(shown);
    }

    public override void RefreshActions()
    {
        foreach (var card in _shown)
            card.Actions.RefreshAll();
    }

    public override IEnumerable<object> InspectorSections(IMenuTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return _shown.Where(c => c.EntityId == target.Id);
    }
}
