using Common.Recipe;
using System.Windows;
using System.Windows.Controls;

namespace UI.Recipe;

public class StepEditorTemplateSelector : DataTemplateSelector
{
    public DataTemplate? PulseTemplate { get; set; }
    public DataTemplate? PurgeTemplate { get; set; }
    public DataTemplate? ReactionZoneTemplate { get; set; }
    public DataTemplate? MoveAxisTemplate { get; set; }
    public DataTemplate? WaitTemplate { get; set; }
    public DataTemplate? CycleTemplate { get; set; }
    public DataTemplate? PumpDownTemplate { get; set; }
    public DataTemplate? VentTemplate { get; set; }
    public DataTemplate? SetPressureTemplate { get; set; }
    public DataTemplate? WaitPressureStableTemplate { get; set; }
    public DataTemplate? SetTemperatureTemplate { get; set; }
    public DataTemplate? WaitTemperatureStableTemplate { get; set; }
    public DataTemplate? EmptyTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container)
    {
        if (item is not RecipeStepNodeViewModel vm)
            return EmptyTemplate;

        return vm.StepType switch
        {
            StepType.Pulse => PulseTemplate,
            StepType.Purge => PurgeTemplate,
            StepType.ReactionZone => ReactionZoneTemplate,
            StepType.MoveAxis => MoveAxisTemplate,
            StepType.Wait => WaitTemplate,
            StepType.Cycle => CycleTemplate,
            StepType.PumpDown => PumpDownTemplate,
            StepType.Vent => VentTemplate,
            StepType.SetPressure => SetPressureTemplate,
            StepType.WaitPressureStable => WaitPressureStableTemplate,
            StepType.SetTemperature => SetTemperatureTemplate,
            StepType.WaitTemperatureStable => WaitTemperatureStableTemplate,
            _ => EmptyTemplate
        };
    }
}
