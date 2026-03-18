using CommunityToolkit.Mvvm.ComponentModel;
using System;

namespace UI.ViewModels;

public partial class TopBarViewModel : ObservableObject
{
    [ObservableProperty]
    private string equipmentStatus = "运行中";

    [ObservableProperty]
    private string statusDetail = "自动生产";

    [ObservableProperty]
    private string recipeName = "SnOx_AB01_120C";

    [ObservableProperty]
    private string recipeDetail = "配方 / Step 04 / 12";

    [ObservableProperty]
    private string alarmSummary = "无活动报警";

    [ObservableProperty]
    private string alarmDetail = "设备状态正常";

    [ObservableProperty]
    private string operatorName = "Alex Admin";

    [ObservableProperty]
    private string languageText = "中文";

    [ObservableProperty]
    private string timeText = DateTime.Now.ToString("HH:mm:ss");

    [ObservableProperty]
    private string dateText = DateTime.Now.ToString("yyyy-MM-dd");

    [ObservableProperty]
    private string logoText = "YOUR LOGO";

    public void UpdateTime(DateTime now)
    {
        TimeText = now.ToString("HH:mm:ss");
        DateText = now.ToString("yyyy-MM-dd");
    }
}
