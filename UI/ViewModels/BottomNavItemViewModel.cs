using CommunityToolkit.Mvvm.ComponentModel;

namespace UI.ViewModels;

public partial class BottomNavItemViewModel : ObservableObject
{
    public BottomNavItemViewModel(string title, string routeKey)
    {
        Title = title;
        RouteKey = routeKey;
    }

    public string Title { get; }

    /// <summary>
    /// 用于页面路由，例如：工艺/运行、自动化/上料
    /// </summary>
    public string RouteKey { get; }

    [ObservableProperty]
    private bool isSelected;
}
