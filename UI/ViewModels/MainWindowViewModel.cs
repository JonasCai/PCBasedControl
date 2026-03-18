using CommunityToolkit.Mvvm.ComponentModel;
using UI.Services;

namespace UI.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private readonly ClockService _clockService;

    public MainWindowViewModel(
        TopBarViewModel topBar,
        ClockService clockService)
    {
        TopBar = topBar;
        _clockService = clockService;

        BottomNav = new BottomNavViewModel(OnNavigate);

        _clockService.TimeChanged += (_, now) => TopBar.UpdateTime(now);
        _clockService.Start();

        BottomNav.InitializeDefault();
    }

    public TopBarViewModel TopBar { get; }

    public BottomNavViewModel BottomNav { get; }

    [ObservableProperty]
    private object? currentPage;

    private void OnNavigate(string routeKey)
    {
        CurrentPage = routeKey switch
        {
            "总览" => new PagePlaceholderViewModel("总览", "显示整机运行摘要、核心参数、当前批次与关键上下文。"),

            "工艺/运行" => new PagePlaceholderViewModel("工艺 / 运行", "显示当前工艺运行状态、阶段、步骤上下文和运行摘要。"),
            "工艺/配方" => new PagePlaceholderViewModel("工艺 / 配方", "显示配方列表、配方编辑、步骤参数与版本管理。"),
            "工艺/维护" => new PagePlaceholderViewModel("工艺 / 维护", "显示工艺维护相关参数、校准与维护操作入口。"),

            "自动化/上料" => new PagePlaceholderViewModel("自动化 / 上料", "显示上料机构状态、上料流程、到位与联锁状态。"),
            "自动化/下料" => new PagePlaceholderViewModel("自动化 / 下料", "显示下料机构状态、动作流程与异常处理状态。"),
            "自动化/传输" => new PagePlaceholderViewModel("自动化 / 传输", "显示传输机构、路径状态、节拍与当前位置。"),

            "报警" => new PagePlaceholderViewModel("报警", "显示活动报警、未确认报警、历史报警与详情。"),

            "数据/运行日志" => new PagePlaceholderViewModel("数据 / 运行日志", "显示设备运行日志、事件记录与查询导出。"),
            "数据/趋势" => new PagePlaceholderViewModel("数据 / 趋势", "显示温度、压力、节拍等趋势曲线与历史数据。"),

            "系统" => new PagePlaceholderViewModel("系统", "显示权限、通讯、参数、语言和系统设置。"),

            _ => new PagePlaceholderViewModel("页面", "未定义页面。")
        };
    }
}
