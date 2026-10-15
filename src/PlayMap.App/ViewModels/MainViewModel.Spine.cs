using PlayMap.App.Infrastructure;
using PlayMap.App.ViewModels.Cards;
using PlayMap.App.Views.Cards;

namespace PlayMap.App.ViewModels;

public sealed partial class MainViewModel
{
    /// <summary>The generic STATE cards (Health, Inputs, the inspectors).</summary>
    public CardsModule Cards { get; private set; } = null!;

    /// <summary>The entries generated for every object's menu and inspector toolbar.</summary>
    public Generated.ObjectMenusModule ObjectMenus { get; private set; } = null!;

    partial void ComposeSpine(List<DeskModule> modules)
    {
        ModuleViews.Register<CardViewModel>(() => new CardView());
        ModuleViews.Register<CardsPanelViewModel>(() => new CardsPanel());
        Cards = new CardsModule(Session);
        modules.Add(Cards);
        ObjectMenus = new Generated.ObjectMenusModule(Session);
        modules.Add(ObjectMenus);
    }
}
