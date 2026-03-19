using Common.Recipe;
using Controller.EventLogger;
using Controller.gRPC;
using Controller.Recipe;
using Controller.S88;
using MongoDB.Driver;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Controller._03.Unit;

public class ProcessChamber : S88UnitBase
{
    private readonly RecipeEngine _recipeEngine = new();

    public ProcessChamber(UnitCfg cfg, IEventProducer eventProducer, ILogger<S88UnitBase> logger) : base(cfg, eventProducer, logger)
    {
        RegisterCommandHandlers();
    }

    // 提供重写的接口给 HMI 推送数据
    public override string GetActiveRecipeJson() => _recipeEngine.GetActiveRecipeJson();

    protected override bool OnExecute()
    {
        switch (Step)
        {
            case 0:
                if (_recipeEngine.Tick(EnterRecipeStepOnce, IsRecipeStepDone))
                    Step++;
                return false;

            case 1:
                return true;

            default:
                return false;
        }

    }

    // 重写指令注册，把配方相关的扩展指令加进来
    protected override void RegisterCommandHandlers()
    {
        base.RegisterCommandHandlers(); // 注册基类的 Start, Stop 等

        // 扩展 DownloadRecipe 指令
        RegisterCommandHandler(Command.DownloadRecipe, CmdDownloadRecipe);
    }

    // 处理配方下载的指令
    private void CmdDownloadRecipe(InternalCommand cmd)
    {
        if (Mode != S88Mode.Manual)
        {
            // S88 状态校验：只有在非运行状态才允许下发新配方
            if (State != S88State.Idle && State != S88State.Stopped && State != S88State.Aborted)
            {
                cmd.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Rejected, $"当前状态 {State} 不允许下发配方。"));
                return;
            }
        }

        if (!_recipeEngine.TryLoadFromJson(cmd.JsonPayload, out string errorMsg))
        {
            cmd.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Rejected, $"配方校验失败: {errorMsg}"));
            return;
        }

        // 成功！
        LogInfo($"成功加载新配方。");
        cmd.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Accepted, "配方下载并校验成功！"));
    }

    private void EnterRecipeStepOnce(IRecipeStep step)
    {
        switch (step)
        {
            case PulseStep p:
                break;
            case PurgeStep p:
                break;
            case MoveAxisStep m:
                break;
        }
    }

    private bool IsRecipeStepDone(IRecipeStep step, long elapsedMs)
    {
        switch (step)
        {
            case PulseStep p:
                return true;
            case PurgeStep p:
                return true;
            case MoveAxisStep m:
                return true;

            default:
                return true;
        }
    }
}
