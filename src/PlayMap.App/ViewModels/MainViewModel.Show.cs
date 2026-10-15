using PlayMap.App.Infrastructure;
using PlayMap.App.ViewModels.Show;
using PlayMap.App.Views.Show;

namespace PlayMap.App.ViewModels;

public sealed partial class MainViewModel
{
    partial void ComposeShowDesk(List<DeskModule> modules)
    {
        ModuleViews.Register<ShowStripViewModel>(() => new ShowStripView());
        modules.Add(new ShowStripModule(Session));
    }
}
