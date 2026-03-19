using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Controller.Recipe;
using MaterialDesignThemes.Wpf;
using System.Windows.Media;

namespace UI.Recipe;

public partial class StepInsertMenuItemViewModel : ObservableObject
{
    public required string Header { get; init; }
    public required StepType StepType { get; init; }
    public required InsertMode InsertMode { get; init; }
    public required PackIconKind IconKind { get; init; }
    public required Brush ColorBrush { get; init; }

    [ObservableProperty]
    private bool isEnabled = true;

    public required IRelayCommand<StepInsertMenuItemViewModel> ExecuteCommand { get; init; }
}
