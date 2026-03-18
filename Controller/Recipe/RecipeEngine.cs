using System.Text.Json;

namespace Controller.Recipe;

public class RecipeEngine
{
    private readonly Stack<RecipeExecutionContext> _stack = new();
    private bool _stepEntered = false;
    private long _stepStartMs = 0;
    private AldRecipe? _recipe;

    // 清理引擎状态
    public void Clear()
    {
        _stack.Clear();
        _stepEntered = false;
        _recipe = null;
    }

    /// <summary> 
    /// 在 Unit 循环中调用 
    /// </summary> 
    /// <param name="onEnterStep">委托：让外部 Unit 执行单次触发动作</param> 
    /// <param name="onCheckDone">委托：让外部 Unit 检查该步是否完成</param> 
    /// <returns>返回 true 表示整个配方已经全部执行完毕</returns> 
    public bool Tick(Action<IRecipeStep> onEnterStep, Func<IRecipeStep, long, bool> onCheckDone)
    {
        if (_recipe == null || _stack.Count == 0) return true;
        var currentContext = _stack.Peek();

        // 判断当前层是否走完
        if (currentContext.StepIndex >= currentContext.Steps.Count)
        {
            currentContext.CurrentLoop++;
            if (currentContext.CurrentLoop > currentContext.TotalLoops)
            {
                _stack.Pop();
                if (_stack.Count > 0)
                {
                    _stack.Peek().StepIndex++;
                    _stepEntered = false;
                }
            }
            else
            {
                currentContext.StepIndex = 0;
                _stepEntered = false;
            }
            return false;
        }

        // 拿到当前步
        var s = currentContext.Steps[currentContext.StepIndex];

        // 循环步处理

        if (s is CycleStep cycle)
        {
            _stack.Push(new RecipeExecutionContext(cycle.SubSteps, cycle.LoopCount, cycle));
            return false;
        }

        // 配方动作由外部的 Unit 执行
        if (!_stepEntered)
        {
            _stepEntered = true;
            _stepStartMs = Environment.TickCount64; onEnterStep(s);
        }

        // 询问外部的 Unit，配方动作是否执行完成
        if (onCheckDone(s, Environment.TickCount64 - _stepStartMs))
        {
            currentContext.StepIndex++;
            _stepEntered = false;
        }
        return false;
    }

    /// <summary> 
    /// 提供给外部获取当前加载的配方JSON 
    /// </summary> 
    public string GetActiveRecipeJson() => _recipe != null ? JsonSerializer.Serialize(_recipe) : string.Empty;

    /// <summary> 
    /// 处理来自 HMI 的配方( JSON 字符串) 
    /// </summary> 
    public bool TryLoadFromJson(string jsonPayload, out string errorMessage)
    {
        errorMessage = string.Empty;
        try
        {
            // 多态反序列化
            var parsedRecipe = JsonSerializer.Deserialize<AldRecipe>(jsonPayload);
            if (parsedRecipe == null || parsedRecipe.Steps == null || parsedRecipe.Steps.Count == 0)
            {
                errorMessage = "配方为空或格式无法解析。"; return false;
            }

            // 校验
            if (!ValidateRecipeRules(parsedRecipe, out errorMessage))
            {
                return false;
            }

            // 校验通过，加载到引擎中
            LoadRecipe(parsedRecipe);
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = $"配方解析异常: {ex.Message}";
            return false;
        }
    }

    /// <summary> 
    /// 获取配方实时进度推送给 HMI 
    /// </summary> 
    public List<string> GetCurrentProgress()
    {
        var progressList = new List<string>();
        if (_recipe == null || _stack.Count == 0)
            return progressList;

        // 从主配方开始向内层拼装字符串
        foreach (var context in _stack.Reverse())
        {
            if (context.SourceCycle != null)
                progressList.Add($"Cycle [{context.CurrentLoop}/{context.TotalLoops}]");
            else
                progressList.Add(_recipe.RecipeName);
        }

        // 当前正在执行的动作
        var currentContext = _stack.Peek();
        if (currentContext.StepIndex < currentContext.Steps.Count)
        {
            var currentStep = currentContext.Steps[currentContext.StepIndex];
            progressList.Add($"-> {currentStep.StepType}");
        }
        return progressList;// [ "Al2O3_Recipe", "Cycle [2/50]", "Cycle [5/10]", "-> Pulse" ]
    }

    private bool ValidateRecipeRules(AldRecipe recipe, out string error)
    {
        error = string.Empty;
        // TODO: 在这里遍历 recipe.Steps 进行各种逻辑和极值的检查
        return true;
    }

    private void LoadRecipe(AldRecipe recipe)
    {
        _recipe = recipe;
        _stack.Clear();
        _stepEntered = false;
        _stack.Push(new RecipeExecutionContext(_recipe.Steps, 1));
    }
}


