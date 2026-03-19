using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Controller.Recipe;
using MaterialDesignThemes.Wpf;
using System.Collections.ObjectModel;
using System.Windows.Media;

namespace UI.Recipe;

public partial class RecipeEditorViewModel : ObservableObject
{
    public RecipeEditorViewModel()
    {
        LibrarySteps = new ObservableCollection<StepLibraryItemViewModel>(BuildLibrary());
        FilteredLibrarySteps = new ObservableCollection<StepLibraryItemViewModel>(LibrarySteps);

        BuildInsertMenus();

        Recipe = CreateDefaultRecipe();
        LoadRecipeToUi(Recipe);
    }

    [ObservableProperty]
    private string recipeName = "ALD_Process_Recipe";

    [ObservableProperty]
    private AldMode mode = AldMode.Temporal;

    [ObservableProperty]
    private string searchKeyword = string.Empty;

    [ObservableProperty]
    private RecipeStepNodeViewModel? selectedNode;

    [ObservableProperty]
    private AldRecipe recipe = new() { RecipeName = "ALD_Process_Recipe" };

    public ObservableCollection<RecipeStepNodeViewModel> RootSteps { get; } = new();
    public ObservableCollection<StepLibraryItemViewModel> LibrarySteps { get; }
    public ObservableCollection<StepLibraryItemViewModel> FilteredLibrarySteps { get; }

    public ObservableCollection<StepInsertMenuItemViewModel> InsertBeforeMenuItems { get; } = new();
    public ObservableCollection<StepInsertMenuItemViewModel> InsertAfterMenuItems { get; } = new();
    public ObservableCollection<StepInsertMenuItemViewModel> InsertChildMenuItems { get; } = new();

    public string StepCountText => $"Steps ({CountAllNodes(RootSteps)})";
    public bool CanInsertChild => SelectedNode?.IsCycle == true;
    public bool HasSelection => SelectedNode != null;
    public bool CanMoveSelectedUp => GetMoveUpAvailable(SelectedNode);
    public bool CanMoveSelectedDown => GetMoveDownAvailable(SelectedNode);

    partial void OnSelectedNodeChanged(RecipeStepNodeViewModel? oldValue, RecipeStepNodeViewModel? newValue)
    {
        if (oldValue != null) oldValue.IsSelected = false;
        if (newValue != null) newValue.IsSelected = true;

        OnPropertyChanged(nameof(CanInsertChild));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(CanMoveSelectedUp));
        OnPropertyChanged(nameof(CanMoveSelectedDown));

        RefreshInsertMenuStates();
        NotifyCommands();
    }

    partial void OnSearchKeywordChanged(string value) => FilterLibrary();

    partial void OnModeChanged(AldMode value) => RefreshInsertMenuStates();

    [RelayCommand]
    private void NewRecipe()
    {
        Recipe = CreateDefaultRecipe();
        LoadRecipeToUi(Recipe);
    }



    [RelayCommand]
    private void AddStep()
    {
        var defaultType = Mode == AldMode.Temporal ? StepType.Pulse : StepType.ReactionZone;
        InsertNewNode(defaultType, SelectedNode, SelectedNode == null ? InsertMode.After : InsertMode.After);
    }

    [RelayCommand]
    private void AddCycle()
    {
        InsertNewNode(StepType.Cycle, SelectedNode, SelectedNode == null ? InsertMode.After : InsertMode.After);
    }

    [RelayCommand(CanExecute = nameof(CanInsertChild))]
    private void AddChildStep()
    {
        var defaultType = Mode == AldMode.Temporal ? StepType.Pulse : StepType.ReactionZone;
        InsertNewNode(defaultType, SelectedNode, InsertMode.Child);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void InsertBefore()
    {
        var defaultType = Mode == AldMode.Temporal ? StepType.Pulse : StepType.ReactionZone;
        InsertNewNode(defaultType, SelectedNode, InsertMode.Before);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void InsertAfter()
    {
        var defaultType = Mode == AldMode.Temporal ? StepType.Pulse : StepType.ReactionZone;
        InsertNewNode(defaultType, SelectedNode, InsertMode.After);
    }

    [RelayCommand(CanExecute = nameof(CanInsertChild))]
    private void InsertChild()
    {
        var defaultType = Mode == AldMode.Temporal ? StepType.Pulse : StepType.ReactionZone;
        InsertNewNode(defaultType, SelectedNode, InsertMode.Child);
    }

    [RelayCommand]
    private void InsertSpecificTypedStep(StepInsertMenuItemViewModel? menuItem)
    {
        if (menuItem == null)
            return;

        if (menuItem.InsertMode == InsertMode.Child && SelectedNode?.IsCycle != true)
            return;

        InsertNewNode(menuItem.StepType, SelectedNode, menuItem.InsertMode);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void DeleteStep()
    {
        if (SelectedNode is null)
            return;

        var target = SelectedNode;
        var parent = target.Parent;
        var collection = GetCollectionOf(target);
        var index = collection.IndexOf(target);
        if (index < 0) return;

        collection.RemoveAt(index);

        RefreshAllIndices();

        if (collection.Count > 0)
            SelectedNode = collection[Math.Clamp(index, 0, collection.Count - 1)];
        else
            SelectedNode = parent;
    }

    [RelayCommand(CanExecute = nameof(CanMoveSelectedUp))]
    private void MoveStepUp()
    {
        if (SelectedNode is null) return;

        var collection = GetCollectionOf(SelectedNode);
        var index = collection.IndexOf(SelectedNode);
        if (index <= 0) return;

        collection.Move(index, index - 1);
        RefreshAllIndices();
        SelectedNode = collection[index - 1];
    }

    [RelayCommand(CanExecute = nameof(CanMoveSelectedDown))]
    private void MoveStepDown()
    {
        if (SelectedNode is null) return;

        var collection = GetCollectionOf(SelectedNode);
        var index = collection.IndexOf(SelectedNode);
        if (index < 0 || index >= collection.Count - 1) return;

        collection.Move(index, index + 1);
        RefreshAllIndices();
        SelectedNode = collection[index + 1];
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void ExpandSelected()
    {
        if (SelectedNode != null)
            SelectedNode.IsExpanded = true;
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void CollapseSelected()
    {
        if (SelectedNode != null)
            SelectedNode.IsExpanded = false;
    }

    [RelayCommand]
    private void ExpandAll() => SetExpandStateRecursive(RootSteps, true);

    [RelayCommand]
    private void CollapseAll() => SetExpandStateRecursive(RootSteps, false);

    public void AddLibraryStep(StepType stepType, RecipeStepNodeViewModel? targetNode = null, InsertMode insertMode = InsertMode.After)
        => InsertNewNode(stepType, targetNode, insertMode);

    public void InsertNewNode(StepType stepType, RecipeStepNodeViewModel? targetNode, InsertMode insertMode)
    {
        var newNode = CreateNode(stepType, insertMode == InsertMode.Child ? targetNode : targetNode?.Parent);

        if (targetNode == null)
        {
            RootSteps.Add(newNode);
            RefreshAllIndices();
            SelectedNode = newNode;
            return;
        }

        switch (insertMode)
        {
            case InsertMode.Before:
            {
                var collection = GetCollectionOf(targetNode);
                var index = collection.IndexOf(targetNode);
                collection.Insert(Math.Max(0, index), newNode);
                break;
            }
            case InsertMode.After:
            {
                var collection = GetCollectionOf(targetNode);
                var index = collection.IndexOf(targetNode);
                collection.Insert(index + 1, newNode);
                break;
            }
            case InsertMode.Child:
            {
                if (!targetNode.IsCycle)
                {
                    var fallbackCollection = GetCollectionOf(targetNode);
                    var fallbackIndex = fallbackCollection.IndexOf(targetNode);
                    fallbackCollection.Insert(fallbackIndex + 1, newNode);
                }
                else
                {
                    targetNode.Children.Add(newNode);
                    targetNode.IsExpanded = true;
                    targetNode.Refresh();
                }
                break;
            }
        }

        RefreshAllIndices();
        SelectedNode = newNode;
    }

    private void LoadRecipeToUi(AldRecipe recipe)
    {
        RecipeName = recipe.RecipeName;
        Mode = recipe.Mode;

        RootSteps.Clear();
        foreach (var step in recipe.Steps)
            RootSteps.Add(new RecipeStepNodeViewModel(step));

        RefreshAllIndices();
        SelectedNode = RootSteps.FirstOrDefault();
        RefreshInsertMenuStates();
    }

    private void SyncUiToRecipe()
    {
        Recipe.RecipeName = RecipeName;
        Recipe.Mode = Mode;
        Recipe.Steps = RootSteps.Select(x => x.BuildStep()).ToList();
    }

    private void RefreshAllIndices()
    {
        RefreshIndicesRecursive(RootSteps, null);
        OnPropertyChanged(nameof(StepCountText));
        OnPropertyChanged(nameof(CanInsertChild));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(CanMoveSelectedUp));
        OnPropertyChanged(nameof(CanMoveSelectedDown));
        RefreshInsertMenuStates();
        NotifyCommands();
    }

    private void NotifyCommands()
    {
        InsertBeforeCommand.NotifyCanExecuteChanged();
        InsertAfterCommand.NotifyCanExecuteChanged();
        InsertChildCommand.NotifyCanExecuteChanged();
        DeleteStepCommand.NotifyCanExecuteChanged();
        MoveStepUpCommand.NotifyCanExecuteChanged();
        MoveStepDownCommand.NotifyCanExecuteChanged();
        ExpandSelectedCommand.NotifyCanExecuteChanged();
        CollapseSelectedCommand.NotifyCanExecuteChanged();
        AddChildStepCommand.NotifyCanExecuteChanged();
    }

    private void RefreshIndicesRecursive(ObservableCollection<RecipeStepNodeViewModel> nodes, string? parentIndexPrefix)
    {
        for (int i = 0; i < nodes.Count; i++)
        {
            var currentIndex = parentIndexPrefix is null ? $"{i + 1}" : $"{parentIndexPrefix}.{i + 1}";
            nodes[i].DisplayIndex = currentIndex;
            nodes[i].Refresh();

            if (nodes[i].Children.Count > 0)
                RefreshIndicesRecursive(nodes[i].Children, currentIndex);
        }
    }

    private int CountAllNodes(IEnumerable<RecipeStepNodeViewModel> nodes)
    {
        int count = 0;
        foreach (var node in nodes)
        {
            count++;
            count += CountAllNodes(node.Children);
        }
        return count;
    }

    private bool GetMoveUpAvailable(RecipeStepNodeViewModel? node)
    {
        if (node == null) return false;
        return GetCollectionOf(node).IndexOf(node) > 0;
    }

    private bool GetMoveDownAvailable(RecipeStepNodeViewModel? node)
    {
        if (node == null) return false;
        var collection = GetCollectionOf(node);
        var index = collection.IndexOf(node);
        return index >= 0 && index < collection.Count - 1;
    }

    private void SetExpandStateRecursive(IEnumerable<RecipeStepNodeViewModel> nodes, bool isExpanded)
    {
        foreach (var node in nodes)
        {
            node.IsExpanded = isExpanded;
            if (node.Children.Count > 0)
                SetExpandStateRecursive(node.Children, isExpanded);
        }
    }

    private ObservableCollection<RecipeStepNodeViewModel> GetCollectionOf(RecipeStepNodeViewModel node)
        => node.Parent == null ? RootSteps : node.Parent.Children;

    private RecipeStepNodeViewModel CreateNode(StepType stepType, RecipeStepNodeViewModel? parent)
        => new(CreateStep(stepType), parent);

    private void FilterLibrary()
    {
        var keyword = SearchKeyword?.Trim() ?? string.Empty;
        FilteredLibrarySteps.Clear();

        IEnumerable<StepLibraryItemViewModel> query = LibrarySteps;
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            query = query.Where(x =>
                x.DisplayName.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                x.StepType.ToString().Contains(keyword, StringComparison.OrdinalIgnoreCase));
        }

        foreach (var item in query)
            FilteredLibrarySteps.Add(item);
    }

    private void BuildInsertMenus()
    {
        InsertBeforeMenuItems.Clear();
        InsertAfterMenuItems.Clear();
        InsertChildMenuItems.Clear();

        foreach (var item in BuildInsertMenuItems(InsertMode.Before))
            InsertBeforeMenuItems.Add(item);

        foreach (var item in BuildInsertMenuItems(InsertMode.After))
            InsertAfterMenuItems.Add(item);

        foreach (var item in BuildInsertMenuItems(InsertMode.Child))
            InsertChildMenuItems.Add(item);
    }

    private IEnumerable<StepInsertMenuItemViewModel> BuildInsertMenuItems(InsertMode insertMode)
    {
        Brush brush(string hex) => (Brush)new BrushConverter().ConvertFromString(hex)!;
        var command = InsertSpecificTypedStepCommand;

        return new[]
        {
            new StepInsertMenuItemViewModel { Header = "Pulse", StepType = StepType.Pulse, InsertMode = insertMode, IconKind = PackIconKind.Flash, ColorBrush = brush("#2D7FF9"), ExecuteCommand = command },
            new StepInsertMenuItemViewModel { Header = "Purge", StepType = StepType.Purge, InsertMode = insertMode, IconKind = PackIconKind.WavesArrowRight, ColorBrush = brush("#3B82F6"), ExecuteCommand = command },
            new StepInsertMenuItemViewModel { Header = "Wait", StepType = StepType.Wait, InsertMode = insertMode, IconKind = PackIconKind.TimerSand, ColorBrush = brush("#64748B"), ExecuteCommand = command },
            new StepInsertMenuItemViewModel { Header = "Reaction Zone", StepType = StepType.ReactionZone, InsertMode = insertMode, IconKind = PackIconKind.VectorPolygon, ColorBrush = brush("#14B8A6"), ExecuteCommand = command },
            new StepInsertMenuItemViewModel { Header = "Move Axis", StepType = StepType.MoveAxis, InsertMode = insertMode, IconKind = PackIconKind.AxisArrow, ColorBrush = brush("#F59E0B"), ExecuteCommand = command },
            new StepInsertMenuItemViewModel { Header = "Pump Down", StepType = StepType.PumpDown, InsertMode = insertMode, IconKind = PackIconKind.ArrowCollapseDown, ColorBrush = brush("#EF4444"), ExecuteCommand = command },
            new StepInsertMenuItemViewModel { Header = "Vent", StepType = StepType.Vent, InsertMode = insertMode, IconKind = PackIconKind.Export, ColorBrush = brush("#22C55E"), ExecuteCommand = command },
            new StepInsertMenuItemViewModel { Header = "Set Pressure", StepType = StepType.SetPressure, InsertMode = insertMode, IconKind = PackIconKind.Gauge, ColorBrush = brush("#10B981"), ExecuteCommand = command },
            new StepInsertMenuItemViewModel { Header = "Wait Pressure Stable", StepType = StepType.WaitPressureStable, InsertMode = insertMode, IconKind = PackIconKind.GaugeEmpty, ColorBrush = brush("#06B6D4"), ExecuteCommand = command },
            new StepInsertMenuItemViewModel { Header = "Set Temperature", StepType = StepType.SetTemperature, InsertMode = insertMode, IconKind = PackIconKind.Thermometer, ColorBrush = brush("#F97316"), ExecuteCommand = command },
            new StepInsertMenuItemViewModel { Header = "Wait Temperature Stable", StepType = StepType.WaitTemperatureStable, InsertMode = insertMode, IconKind = PackIconKind.ThermometerCheck, ColorBrush = brush("#84CC16"), ExecuteCommand = command },
            new StepInsertMenuItemViewModel { Header = "Cycle", StepType = StepType.Cycle, InsertMode = insertMode, IconKind = PackIconKind.Autorenew, ColorBrush = brush("#8B5CF6"), ExecuteCommand = command }
        };
    }

    private void RefreshInsertMenuStates()
    {
        foreach (var item in InsertBeforeMenuItems)
            item.IsEnabled = IsStepVisibleForMode(item.StepType, Mode);

        foreach (var item in InsertAfterMenuItems)
            item.IsEnabled = IsStepVisibleForMode(item.StepType, Mode);

        foreach (var item in InsertChildMenuItems)
            item.IsEnabled = SelectedNode?.IsCycle == true && IsStepVisibleForMode(item.StepType, Mode);
    }

    private bool IsStepVisibleForMode(StepType stepType, AldMode mode)
    {
        return mode switch
        {
            AldMode.Temporal => stepType switch
            {
                StepType.ReactionZone => false,
                StepType.MoveAxis => false,
                _ => true
            },
            AldMode.Spatial => stepType switch
            {
                StepType.Pulse => false,
                StepType.Purge => false,
                _ => true
            },
            _ => true
        };
    }

    private static AldRecipe CreateDefaultRecipe()
    {
        return new AldRecipe
        {
            RecipeName = "ALD_Process_Recipe",
            Mode = AldMode.Temporal,
            Steps = new List<IRecipeStep>
            {
                new PulseStep { TargetReactant = "TMA", PulseTime = TimeSpan.FromMilliseconds(300), CarrierGasFlowSccm = 200 },
                new PurgeStep { Duration = TimeSpan.FromSeconds(2), PurgeGasFlowSccm = 200 },
                new CycleStep
                {
                    LoopCount = 50,
                    SubSteps = new List<IRecipeStep>
                    {
                        new PurgeStep { Duration = TimeSpan.FromSeconds(2), PurgeGasFlowSccm = 200 },
                        new PulseStep { TargetReactant = "TMA", PulseTime = TimeSpan.FromMilliseconds(300), CarrierGasFlowSccm = 200 },
                        new WaitStep { Duration = TimeSpan.FromSeconds(1) }
                    }
                }
            }
        };
    }

    private static IRecipeStep CreateStep(StepType stepType) => stepType switch
    {
        StepType.Pulse => new PulseStep { TargetReactant = "TMA", PulseTime = TimeSpan.FromMilliseconds(300), CarrierGasFlowSccm = 200 },
        StepType.Purge => new PurgeStep { Duration = TimeSpan.FromSeconds(2), PurgeGasFlowSccm = 200 },
        StepType.ReactionZone => new ReactionZoneStep(),
        StepType.MoveAxis => new MoveAxisStep { TargetAxis = "X", TargetPosition = 120, TargetVelocity = 30 },
        StepType.Wait => new WaitStep { Duration = TimeSpan.FromSeconds(1) },
        StepType.Cycle => new CycleStep { LoopCount = 10, SubSteps = new List<IRecipeStep>() },
        StepType.PumpDown => new PumpDownStep(),
        StepType.Vent => new VentStep(),
        StepType.SetPressure => new SetPressureStep { TargetPressurePa = 500, TolerancePa = 3 },
        StepType.WaitPressureStable => new WaitPressureStableStep(),
        StepType.SetTemperature => new SetTemperatureStep { TargetTemperatureCelsius = 200, ToleranceCelsius = 1 },
        StepType.WaitTemperatureStable => new WaitTemperatureStableStep(),
        _ => throw new NotSupportedException($"Unsupported step type: {stepType}")
    };

    private static IEnumerable<StepLibraryItemViewModel> BuildLibrary()
    {
        Brush brush(string hex) => (Brush)new BrushConverter().ConvertFromString(hex)!;

        return new[]
        {
            new StepLibraryItemViewModel { StepType = StepType.Pulse, DisplayName = "Pulse", IconKind = PackIconKind.Flash, TypeColorBrush = brush("#2D7FF9") },
            new StepLibraryItemViewModel { StepType = StepType.Purge, DisplayName = "Purge", IconKind = PackIconKind.WavesArrowRight, TypeColorBrush = brush("#3B82F6") },
            new StepLibraryItemViewModel { StepType = StepType.Wait, DisplayName = "Wait", IconKind = PackIconKind.TimerSand, TypeColorBrush = brush("#64748B") },
            new StepLibraryItemViewModel { StepType = StepType.ReactionZone, DisplayName = "Reaction Zone", IconKind = PackIconKind.VectorPolygon, TypeColorBrush = brush("#14B8A6") },
            new StepLibraryItemViewModel { StepType = StepType.MoveAxis, DisplayName = "Move Axis", IconKind = PackIconKind.AxisArrow, TypeColorBrush = brush("#F59E0B") },
            new StepLibraryItemViewModel { StepType = StepType.PumpDown, DisplayName = "Pump Down", IconKind = PackIconKind.ArrowCollapseDown, TypeColorBrush = brush("#EF4444") },
            new StepLibraryItemViewModel { StepType = StepType.Vent, DisplayName = "Vent", IconKind = PackIconKind.Export, TypeColorBrush = brush("#22C55E") },
            new StepLibraryItemViewModel { StepType = StepType.SetPressure, DisplayName = "Set Pressure", IconKind = PackIconKind.Gauge, TypeColorBrush = brush("#10B981") },
            new StepLibraryItemViewModel { StepType = StepType.WaitPressureStable, DisplayName = "Wait Pressure Stable", IconKind = PackIconKind.GaugeEmpty, TypeColorBrush = brush("#06B6D4") },
            new StepLibraryItemViewModel { StepType = StepType.SetTemperature, DisplayName = "Set Temperature", IconKind = PackIconKind.Thermometer, TypeColorBrush = brush("#F97316") },
            new StepLibraryItemViewModel { StepType = StepType.WaitTemperatureStable, DisplayName = "Wait Temperature Stable", IconKind = PackIconKind.ThermometerCheck, TypeColorBrush = brush("#84CC16") },
            new StepLibraryItemViewModel { StepType = StepType.Cycle, DisplayName = "Cycle", IconKind = PackIconKind.Autorenew, TypeColorBrush = brush("#8B5CF6") }
        };
    }
}
