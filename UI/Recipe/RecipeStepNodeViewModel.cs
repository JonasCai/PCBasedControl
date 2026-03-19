using Common.Recipe;
using CommunityToolkit.Mvvm.ComponentModel;
using MaterialDesignThemes.Wpf;
using System.Collections.ObjectModel;
using System.Windows.Media;

namespace UI.Recipe;

public partial class RecipeStepNodeViewModel : ObservableObject
{
    public RecipeStepNodeViewModel(IRecipeStep step, RecipeStepNodeViewModel? parent = null)
    {
        Step = step;
        Parent = parent;

        if (step is CycleStep cycle)
        {
            foreach (var subStep in cycle.SubSteps)
                Children.Add(new RecipeStepNodeViewModel(subStep, this));

            IsExpanded = true;
        }
    }

    public IRecipeStep Step { get; }

    public int Level => Parent == null ? 0 : Parent.Level + 1;

    public RecipeStepNodeViewModel? Parent { get; }

    public ObservableCollection<RecipeStepNodeViewModel> Children { get; } = new();

    public bool IsCycle => Step is CycleStep;
    public bool HasChildren => Children.Count > 0;

    [ObservableProperty]
    private bool isExpanded = true;

    [ObservableProperty]
    private bool isSelected;

    [ObservableProperty]
    private string displayIndex = string.Empty;

    [ObservableProperty]
    private string notes = string.Empty;

    public StepType StepType => Step.StepType;

    public string Title => StepType switch
    {
        StepType.Pulse => "Pulse",
        StepType.Purge => "Purge",
        StepType.ReactionZone => "Reaction Zone",
        StepType.MoveAxis => "Move Axis",
        StepType.Wait => "Wait",
        StepType.Cycle => "Cycle",
        StepType.Vent => "Vent",
        StepType.PumpDown => "Pump Down",
        StepType.SetTemperature => "Set Temperature",
        StepType.WaitTemperatureStable => "Wait Temperature Stable",
        StepType.SetPressure => "Set Pressure",
        StepType.WaitPressureStable => "Wait Pressure Stable",
        _ => StepType.ToString()
    };

    public string Summary => Step switch
    {
        PulseStep s => $"{s.TargetReactant} · {s.PulseTime.TotalMilliseconds:0} ms",
        PurgeStep s => $"N₂ · {s.Duration.TotalSeconds:0.###} s",
        WaitStep s => $"{s.Duration.TotalSeconds:0.###} s",
        MoveAxisStep s => $"{s.TargetAxis} → {s.TargetPosition:0.###}",
        ReactionZoneStep s => $"A {s.CarrierGasAFlowSccm:0} / B {s.CarrierGasBFlowSccm:0}",
        CycleStep s => $"x{s.LoopCount} · {Children.Count} sub steps",
        PumpDownStep s => $"{s.TargetPressurePa:0.###} Pa",
        VentStep s => $"{s.TargetPressurePa:0.###} Pa",
        SetPressureStep s => $"{s.TargetPressurePa:0.###} Pa",
        WaitPressureStableStep s => $"Stable {s.StableDuration.TotalSeconds:0}s",
        SetTemperatureStep s => $"{s.TargetTemperatureCelsius:0.#} °C",
        WaitTemperatureStableStep s => $"Stable {s.StableDuration.TotalSeconds:0}s",
        _ => string.Empty
    };

    public PackIconKind IconKind => StepType switch
    {
        StepType.Pulse => PackIconKind.Flash,
        StepType.Purge => PackIconKind.WavesArrowRight,
        StepType.ReactionZone => PackIconKind.VectorPolygon,
        StepType.MoveAxis => PackIconKind.AxisArrow,
        StepType.Wait => PackIconKind.TimerSand,
        StepType.Cycle => PackIconKind.Autorenew,
        StepType.Vent => PackIconKind.Export,
        StepType.PumpDown => PackIconKind.ArrowCollapseDown,
        StepType.SetTemperature => PackIconKind.Thermometer,
        StepType.WaitTemperatureStable => PackIconKind.ThermometerCheck,
        StepType.SetPressure => PackIconKind.Gauge,
        StepType.WaitPressureStable => PackIconKind.GaugeEmpty,
        _ => PackIconKind.Cog
    };

    public Brush TypeColorBrush => StepType switch
    {
        StepType.Pulse => BrushFrom("#2D7FF9"),
        StepType.Purge => BrushFrom("#3B82F6"),
        StepType.Wait => BrushFrom("#64748B"),
        StepType.Cycle => BrushFrom("#8B5CF6"),
        StepType.ReactionZone => BrushFrom("#14B8A6"),
        StepType.MoveAxis => BrushFrom("#F59E0B"),
        StepType.PumpDown => BrushFrom("#EF4444"),
        StepType.Vent => BrushFrom("#22C55E"),
        StepType.SetPressure => BrushFrom("#10B981"),
        StepType.WaitPressureStable => BrushFrom("#06B6D4"),
        StepType.SetTemperature => BrushFrom("#F97316"),
        StepType.WaitTemperatureStable => BrushFrom("#84CC16"),
        _ => BrushFrom("#94A3B8")
    };

    private static Brush BrushFrom(string hex) =>
        (Brush)new BrushConverter().ConvertFromString(hex)!;

    public void Refresh()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(IconKind));
        OnPropertyChanged(nameof(TypeColorBrush));
        OnPropertyChanged(nameof(HasChildren));
        OnPropertyChanged(nameof(IsCycle));
    }

    public List<IRecipeStep> BuildChildrenSteps() =>
        Children.Select(x => x.BuildStep()).ToList();

    public IRecipeStep BuildStep()
    {
        if (Step is CycleStep cycle)
            cycle.SubSteps = BuildChildrenSteps();

        return Step;
    }

    public string PulseTargetReactant
    {
        get => Step is PulseStep s ? s.TargetReactant : string.Empty;
        set { if (Step is PulseStep s && s.TargetReactant != value) { s.TargetReactant = value; Refresh(); } }
    }

    public double PulseTimeMs
    {
        get => Step is PulseStep s ? s.PulseTime.TotalMilliseconds : 0;
        set { if (Step is PulseStep s) { s.PulseTime = TimeSpan.FromMilliseconds(Math.Max(0, value)); Refresh(); } }
    }

    public float PulseCarrierGasFlowSccm
    {
        get => Step is PulseStep s ? s.CarrierGasFlowSccm : 0;
        set { if (Step is PulseStep s) { s.CarrierGasFlowSccm = Math.Max(0, value); Refresh(); } }
    }

    public double PurgeDurationMs
    {
        get => Step is PurgeStep s ? s.Duration.TotalMilliseconds : 0;
        set { if (Step is PurgeStep s) { s.Duration = TimeSpan.FromMilliseconds(Math.Max(0, value)); Refresh(); } }
    }

    public float PurgeGasFlowSccm
    {
        get => Step is PurgeStep s ? s.PurgeGasFlowSccm : 0;
        set { if (Step is PurgeStep s) { s.PurgeGasFlowSccm = Math.Max(0, value); Refresh(); } }
    }

    public double WaitDurationMs
    {
        get => Step is WaitStep s ? s.Duration.TotalMilliseconds : 0;
        set { if (Step is WaitStep s) { s.Duration = TimeSpan.FromMilliseconds(Math.Max(0, value)); Refresh(); } }
    }

    public string MoveAxisTargetAxis
    {
        get => Step is MoveAxisStep s ? s.TargetAxis : string.Empty;
        set { if (Step is MoveAxisStep s && s.TargetAxis != value) { s.TargetAxis = value; Refresh(); } }
    }

    public float MoveAxisTargetPosition
    {
        get => Step is MoveAxisStep s ? s.TargetPosition : 0;
        set { if (Step is MoveAxisStep s) { s.TargetPosition = value; Refresh(); } }
    }

    public float MoveAxisTargetVelocity
    {
        get => Step is MoveAxisStep s ? s.TargetVelocity : 0;
        set { if (Step is MoveAxisStep s) { s.TargetVelocity = Math.Max(0, value); Refresh(); } }
    }

    public float ReactionCarrierGasA
    {
        get => Step is ReactionZoneStep s ? s.CarrierGasAFlowSccm : 0;
        set { if (Step is ReactionZoneStep s) { s.CarrierGasAFlowSccm = Math.Max(0, value); Refresh(); } }
    }

    public float ReactionDilutionGasA
    {
        get => Step is ReactionZoneStep s ? s.DilutionGasAFlowSccm : 0;
        set { if (Step is ReactionZoneStep s) { s.DilutionGasAFlowSccm = Math.Max(0, value); Refresh(); } }
    }

    public float ReactionCarrierGasB
    {
        get => Step is ReactionZoneStep s ? s.CarrierGasBFlowSccm : 0;
        set { if (Step is ReactionZoneStep s) { s.CarrierGasBFlowSccm = Math.Max(0, value); Refresh(); } }
    }

    public float ReactionDilutionGasB
    {
        get => Step is ReactionZoneStep s ? s.DilutionGasBFlowSccm : 0;
        set { if (Step is ReactionZoneStep s) { s.DilutionGasBFlowSccm = Math.Max(0, value); Refresh(); } }
    }

    public float ReactionIsolationGas
    {
        get => Step is ReactionZoneStep s ? s.IsolationGasFlowSccm : 0;
        set { if (Step is ReactionZoneStep s) { s.IsolationGasFlowSccm = Math.Max(0, value); Refresh(); } }
    }

    public uint CycleLoopCount
    {
        get => Step is CycleStep s ? s.LoopCount : 0;
        set { if (Step is CycleStep s) { s.LoopCount = value; Refresh(); } }
    }

    public float PumpDownTargetPressurePa
    {
        get => Step is PumpDownStep s ? s.TargetPressurePa : 0;
        set { if (Step is PumpDownStep s) { s.TargetPressurePa = Math.Max(0, value); Refresh(); } }
    }

    public double PumpDownTimeoutSec
    {
        get => Step is PumpDownStep s ? s.Timeout.TotalSeconds : 0;
        set { if (Step is PumpDownStep s) { s.Timeout = TimeSpan.FromSeconds(Math.Max(0, value)); Refresh(); } }
    }

    public float VentTargetPressurePa
    {
        get => Step is VentStep s ? s.TargetPressurePa : 0;
        set { if (Step is VentStep s) { s.TargetPressurePa = Math.Max(0, value); Refresh(); } }
    }

    public double VentTimeoutSec
    {
        get => Step is VentStep s ? s.Timeout.TotalSeconds : 0;
        set { if (Step is VentStep s) { s.Timeout = TimeSpan.FromSeconds(Math.Max(0, value)); Refresh(); } }
    }

    public double SetPressureTargetPa
    {
        get => Step is SetPressureStep s ? s.TargetPressurePa : 0;
        set { if (Step is SetPressureStep s) { s.TargetPressurePa = Math.Max(0, value); Refresh(); } }
    }

    public double SetPressureTolerancePa
    {
        get => Step is SetPressureStep s ? s.TolerancePa : 0;
        set { if (Step is SetPressureStep s) { s.TolerancePa = Math.Max(0, value); Refresh(); } }
    }

    public double WaitPressureTimeoutSec
    {
        get => Step is WaitPressureStableStep s ? s.Timeout.TotalSeconds : 0;
        set { if (Step is WaitPressureStableStep s) { s.Timeout = TimeSpan.FromSeconds(Math.Max(0, value)); Refresh(); } }
    }

    public double WaitPressureStableDurationSec
    {
        get => Step is WaitPressureStableStep s ? s.StableDuration.TotalSeconds : 0;
        set { if (Step is WaitPressureStableStep s) { s.StableDuration = TimeSpan.FromSeconds(Math.Max(0, value)); Refresh(); } }
    }

    public double WaitPressureTargetPa
    {
        get => Step is WaitPressureStableStep s ? s.TargetPressurePa : 0;
        set { if (Step is WaitPressureStableStep s) { s.TargetPressurePa = Math.Max(0, value); Refresh(); } }
    }

    public double WaitPressureTolerancePa
    {
        get => Step is WaitPressureStableStep s ? s.TolerancePa : 0;
        set { if (Step is WaitPressureStableStep s) { s.TolerancePa = Math.Max(0, value); Refresh(); } }
    }

    public double SetTemperatureTargetC
    {
        get => Step is SetTemperatureStep s ? s.TargetTemperatureCelsius : 0;
        set { if (Step is SetTemperatureStep s) { s.TargetTemperatureCelsius = value; Refresh(); } }
    }

    public double SetTemperatureToleranceC
    {
        get => Step is SetTemperatureStep s ? s.ToleranceCelsius : 0;
        set { if (Step is SetTemperatureStep s) { s.ToleranceCelsius = Math.Max(0, value); Refresh(); } }
    }

    public double WaitTemperatureTimeoutSec
    {
        get => Step is WaitTemperatureStableStep s ? s.Timeout.TotalSeconds : 0;
        set { if (Step is WaitTemperatureStableStep s) { s.Timeout = TimeSpan.FromSeconds(Math.Max(0, value)); Refresh(); } }
    }

    public double WaitTemperatureStableDurationSec
    {
        get => Step is WaitTemperatureStableStep s ? s.StableDuration.TotalSeconds : 0;
        set { if (Step is WaitTemperatureStableStep s) { s.StableDuration = TimeSpan.FromSeconds(Math.Max(0, value)); Refresh(); } }
    }

    public double WaitTemperatureTargetC
    {
        get => Step is WaitTemperatureStableStep s ? s.TargetTemperatureCelsius : 0;
        set { if (Step is WaitTemperatureStableStep s) { s.TargetTemperatureCelsius = value; Refresh(); } }
    }

    public double WaitTemperatureToleranceC
    {
        get => Step is WaitTemperatureStableStep s ? s.ToleranceCelsius : 0;
        set { if (Step is WaitTemperatureStableStep s) { s.ToleranceCelsius = Math.Max(0, value); Refresh(); } }
    }
}
