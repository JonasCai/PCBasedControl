using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MaterialDesignThemes.Wpf;
using System;
using System.Collections.ObjectModel;
using System.Linq;

namespace UI.ViewModels;

public partial class BottomNavViewModel : ObservableObject
{
    private readonly Action<string>? _onNavigate;

    public BottomNavViewModel(Action<string> onNavigate)
    {
        _onNavigate = onNavigate;

        Groups = new ObservableCollection<BottomNavGroupViewModel>
        {
            new BottomNavGroupViewModel(
                "总览",
                PackIconKind.ViewDashboardOutline,
                "总览"),

            new BottomNavGroupViewModel(
                "工艺",
                PackIconKind.FlaskOutline,
                "工艺/运行",
                new[]
                {
                    new BottomNavItemViewModel("运行", "工艺/运行"),
                    new BottomNavItemViewModel("配方", "工艺/配方"),
                    new BottomNavItemViewModel("维护", "工艺/维护"),
                }),

            new BottomNavGroupViewModel(
                "自动化",
                PackIconKind.RobotIndustrial,
                "自动化/上料",
                new[]
                {
                    new BottomNavItemViewModel("上料", "自动化/上料"),
                    new BottomNavItemViewModel("下料", "自动化/下料"),
                    new BottomNavItemViewModel("传输", "自动化/传输"),
                }),

            new BottomNavGroupViewModel(
                "报警",
                PackIconKind.AlertOutline,
                "报警"),

            new BottomNavGroupViewModel(
                "数据",
                PackIconKind.ChartLine,
                "数据/运行日志",
                new[]
                {
                    new BottomNavItemViewModel("运行日志", "数据/运行日志"),
                    new BottomNavItemViewModel("趋势", "数据/趋势"),
                }),

            new BottomNavGroupViewModel(
                "系统",
                PackIconKind.CogOutline,
                "系统"),
        };
    }

    public ObservableCollection<BottomNavGroupViewModel> Groups { get; }

    [ObservableProperty]
    private string? selectedRouteKey;

    [RelayCommand]
    private void NavigateGroup(BottomNavGroupViewModel? group)
    {
        if (group is null)
            return;

        SelectGroup(group);

        if (group.HasItems)
        {
            var defaultItem = group.Items.First();
            SelectItem(group, defaultItem);
            NavigateTo(defaultItem.RouteKey);
        }
        else
        {
            ClearAllItemsSelection();
            NavigateTo(group.RouteKey);
        }
    }

    [RelayCommand]
    private void NavigateItem(object? parameter)
    {
        if (parameter is not BottomNavItemClickParameter nav)
            return;

        if (nav.Group is null || nav.Item is null)
            return;

        SelectGroup(nav.Group);
        SelectItem(nav.Group, nav.Item);
        NavigateTo(nav.Item.RouteKey);
    }

    public void InitializeDefault()
    {
        var firstGroup = Groups.FirstOrDefault();
        if (firstGroup is not null)
        {
            NavigateGroup(firstGroup);
        }
    }

    private void NavigateTo(string routeKey)
    {
        SelectedRouteKey = routeKey;
        _onNavigate?.Invoke(routeKey);
    }

    private void SelectGroup(BottomNavGroupViewModel selectedGroup)
    {
        foreach (var group in Groups)
        {
            group.IsSelected = ReferenceEquals(group, selectedGroup);
        }
    }

    private void SelectItem(BottomNavGroupViewModel ownerGroup, BottomNavItemViewModel selectedItem)
    {
        foreach (var group in Groups)
        {
            foreach (var item in group.Items)
            {
                item.IsSelected = ReferenceEquals(group, ownerGroup) && ReferenceEquals(item, selectedItem);
            }
        }
    }

    private void ClearAllItemsSelection()
    {
        foreach (var group in Groups)
        {
            foreach (var item in group.Items)
            {
                item.IsSelected = false;
            }
        }
    }
}

/// <summary>
/// 用于 XAML 中把 Group 和 Item 一起传给命令
/// </summary>
public class BottomNavItemClickParameter
{
    public BottomNavGroupViewModel? Group { get; set; }

    public BottomNavItemViewModel? Item { get; set; }
}
