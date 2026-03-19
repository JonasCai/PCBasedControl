
using Common.Recipe;

namespace Controller.Recipe;

public class RecipeExecutionContext
{
    // 当前层级的步骤列表
    public List<IRecipeStep> Steps { get; }

    // 循环信息 (用于 HMI 监控)
    public uint CurrentLoop { get; set; } = 1;

    public uint TotalLoops { get; }

    // 当前执行到了哪一步
    public int StepIndex { get; set; } = 0;

    // 记录是从哪个 Cycle 进来的（用于UI展示，如果是根节点则为 null）
    public CycleStep? SourceCycle { get; }

    public RecipeExecutionContext(List<IRecipeStep> steps, uint totalLoops, CycleStep? sourceCycle = null)
    {
        Steps = steps ?? new List<IRecipeStep>();
        TotalLoops = totalLoops;
        SourceCycle = sourceCycle;
    }
}
