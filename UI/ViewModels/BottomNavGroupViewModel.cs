using CommunityToolkit.Mvvm.ComponentModel;
using MaterialDesignThemes.Wpf;
using System.Collections.ObjectModel;

namespace UI.ViewModels;

public partial class BottomNavGroupViewModel : ObservableObject
{
    public BottomNavGroupViewModel(
        string title,
        PackIconKind iconKind,
        string routeKey,
        IEnumerable<BottomNavItemViewModel>? items = null)
    {
        Title = title;
        IconKind = iconKind;
        RouteKey = routeKey;

        Items = items is null
            ? new ObservableCollection<BottomNavItemViewModel>()
            : new ObservableCollection<BottomNavItemViewModel>(items);
    }

    public string Title { get; }

    public PackIconKind IconKind { get; }

    /// <summary>
    /// 一级组头默认路由，例如：工艺、自动化、数据
    /// </summary>
    public string RouteKey { get; }

    public ObservableCollection<BottomNavItemViewModel> Items { get; }

    public bool HasItems => Items.Count > 0;

    [ObservableProperty]
    private bool isSelected;
}
