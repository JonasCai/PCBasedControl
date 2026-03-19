using Common.Recipe;
using MaterialDesignThemes.Wpf;
using System.Windows.Media;

namespace UI.Recipe;

public class StepLibraryItemViewModel
{
    public required StepType StepType { get; init; }
    public required string DisplayName { get; init; }
    public required PackIconKind IconKind { get; init; }
    public required Brush TypeColorBrush { get; init; }
}
