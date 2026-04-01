using Common.Recipe;
using Controller._01.ControlModule;
using Controller.EventLogger;
using Controller.gRPC;
using Controller.Hardware;
using Controller.Recipe;
using Controller.S88;


namespace Controller._03.Unit;

public class ProcessChamber : S88UnitBase
{
    private readonly RecipeEngine _recipeEngine = new();

    public ProcessChamber(UnitCfg cfg, IEventProducer eventProducer, ILogger<S88UnitBase> logger, IONodes iONodes, IMfcFactory mfcFactory, IValveFactory valveFactory) : base(cfg, eventProducer, logger)
    {
        RegisterMembers(iONodes, mfcFactory, valveFactory);
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

    private CM_MFC RegisterMfc(
        string name,
        IMfcFactory factory,
        MFCNode mfcNode,
        float capacity,
        Func<bool>? canOperate = null)
    {
        var cfg = new MfcCfg()
        {
            Name = name,
            Capacity = capacity,
            CanOperate = canOperate ?? (() => true), // 传入联锁委托
            ReadPV = () => mfcNode.FlowReading,
            WriteSP = sp => mfcNode.FlowSetting = sp,
        };

        var mfc = factory.Create(cfg);
        RegisterMember(mfc);
        return mfc;
    }

    private CM_Valve RegisterValve2(
        string name,
        IValveFactory factory,
        DONode16 doNode, //DO模块
        int openChannel, // 绑定的打开通道
        int closeChannel = -1, // 绑定的关闭通道
        DINode16? diNode = null,      // 绑定的数字量输入模块
        int openSensorIndex = -1,     // 开启到位传感器通道号
        int closeSensorIndex = -1,     // 关闭到位传感器通道号
        Func<bool>? canOpen = null, // 开启(ToWork)的联锁条件
        Func<bool>? canClose = null) // 关闭(ToHome)的联锁条件 
    {
        var cfg = new ValveCfg
        {
            Name = name,
            Actuate = cmd =>
            {
                switch (cmd)
                {
                    case ValveCmd.ToOpen:
                        doNode[openChannel] = true;
                        if (closeChannel >= 0)
                            doNode[closeChannel] = false;
                        break;

                    case ValveCmd.ToClose:
                        doNode[openChannel] = false;
                        if (closeChannel >= 0)
                            doNode[closeChannel] = true;
                        break;

                    case ValveCmd.ToSafe:
                        doNode[openChannel] = false;
                        if (closeChannel >= 0)
                            doNode[closeChannel] = false;
                        break;
                }
            },

            ReadOpenSensor = (diNode != null && openSensorIndex >= 0) ? () => diNode[openSensorIndex] : null,
            ReadClosedSensor = (diNode != null && closeSensorIndex >= 0) ? () => diNode[closeSensorIndex] : null,
            CanOpen = canOpen ?? (() => true),
            CanClose = canClose ?? (() => true)
        };

        var valve = factory.Create(cfg);
        RegisterMember(valve);
        return valve;
    }

    private CM_Valve RegisterValve(
        string name,
        IValveFactory factory,
        ESVNode32 eSvNode, //阀岛模块
        int openChannel, // 绑定的打开通道
        int closeChannel = -1, // 绑定的关闭通道
        DINode16? diNode = null,      // 绑定的数字量输入模块
        int openSensorIndex = -1,     // 开启到位传感器通道号
        int closeSensorIndex = -1,     // 关闭到位传感器通道号
        Func<bool>? canOpen = null, // 开启(ToWork)的联锁条件
        Func<bool>? canClose = null) // 关闭(ToHome)的联锁条件 
    {
        var cfg = new ValveCfg
        {
            Name = name,
            Actuate = cmd =>
            {
                switch (cmd)
                {
                    case ValveCmd.ToOpen:
                        eSvNode[openChannel] = true;
                        if (closeChannel >= 0)
                            eSvNode[closeChannel] = false;
                        break;

                    case ValveCmd.ToClose:
                        eSvNode[openChannel] = false;
                        if (closeChannel >= 0)
                            eSvNode[closeChannel] = true;
                        break;

                    case ValveCmd.ToSafe:
                        eSvNode[openChannel] = false;
                        if (closeChannel >= 0)
                            eSvNode[closeChannel] = false;
                        break;
                }
            },

            ReadOpenSensor = (diNode != null && openSensorIndex >= 0) ? () => diNode[openSensorIndex] : null,
            ReadClosedSensor = (diNode != null && closeSensorIndex >= 0) ? () => diNode[closeSensorIndex] : null,
            CanOpen = canOpen ?? (() => true),
            CanClose = canClose ?? (() => true),
        };

        var valve = factory.Create(cfg);
        RegisterMember(valve);
        return valve;
    }

    private void RegisterMembers(IONodes iONodes, IMfcFactory mfcFactory, IValveFactory valveFactory)
    {
        //MFC
        MFC111 = RegisterMfc("MFC111", mfcFactory, iONodes.MFC111, 5000f);
        MFC112 = RegisterMfc("MFC112", mfcFactory, iONodes.MFC112, 5000f);
        MFC121 = RegisterMfc("MFC121", mfcFactory, iONodes.MFC121, 5000f);
        MFC122 = RegisterMfc("MFC122", mfcFactory, iONodes.MFC122, 1000f);
        MFC131 = RegisterMfc("MFC131", mfcFactory, iONodes.MFC131, 50000f);
        MFC132 = RegisterMfc("MFC132", mfcFactory, iONodes.MFC132, 50000f);

        // Valve
        PV102 = RegisterValve("PV102", valveFactory, iONodes.ESV01, 0, canOpen: () => true, canClose: () => true);
        PV103 = RegisterValve2("PV103", valveFactory, iONodes.A110, 13, canOpen: () => true, canClose: () => true);
        PV104 = RegisterValve2("PV104", valveFactory, iONodes.A110, 12, canOpen: () => true, canClose: () => true);
        PV105 = RegisterValve("PV105", valveFactory, iONodes.ESV01, 19, canOpen: () => true, canClose: () => true);
        PV111 = RegisterValve("PV111", valveFactory, iONodes.ESV01, 2, canOpen: () => true, canClose: () => true);
        PV112 = RegisterValve("PV112", valveFactory, iONodes.ESV01, 4, canOpen: () => true, canClose: () => true);
        PV113 = RegisterValve("PV113", valveFactory, iONodes.ESV01, 6, canOpen: () => true, canClose: () => true);
        PV114 = RegisterValve("PV114", valveFactory, iONodes.ESV01, 8, canOpen: () => true, canClose: () => true);
        PV115 = RegisterValve("PV115", valveFactory, iONodes.ESV01, 10, canOpen: () => true, canClose: () => true);
        PV116 = RegisterValve("PV116", valveFactory, iONodes.ESV01, 12, canOpen: () => true, canClose: () => true);
        PV117 = RegisterValve("PV117", valveFactory, iONodes.ESV01, 14, canOpen: () => true, canClose: () => true);
        PV118 = RegisterValve("PV118", valveFactory, iONodes.ESV01, 3, canOpen: () => true, canClose: () => true);
        PV119 = RegisterValve("PV119", valveFactory, iONodes.ESV01, 16, canOpen: () => true, canClose: () => true);
        PV121 = RegisterValve("PV121", valveFactory, iONodes.ESV01, 18, canOpen: () => true, canClose: () => true);
        PV122 = RegisterValve("PV122", valveFactory, iONodes.ESV01, 15, canOpen: () => true, canClose: () => true);
        PV123 = RegisterValve("PV123", valveFactory, iONodes.ESV01, 17, canOpen: () => true, canClose: () => true);
        PV301 = RegisterValve("PV301", valveFactory, iONodes.ESV01, 9, canOpen: () => true, canClose: () => true);
        PV302 = RegisterValve("PV302", valveFactory, iONodes.ESV01, 11, canOpen: () => true, canClose: () => true);
        PV303 = RegisterValve("PV303", valveFactory, iONodes.ESV01, 13, canOpen: () => true, canClose: () => true);

        // Heaters

        //...
    }

    #region MFC
    private CM_MFC MFC111 = null!;
    private CM_MFC MFC112 = null!;
    private CM_MFC MFC121 = null!;
    private CM_MFC MFC122 = null!;
    private CM_MFC MFC131 = null!;
    private CM_MFC MFC132 = null!;
    #endregion

    #region Valve
    private CM_Valve PV102 = null!;
    private CM_Valve PV103 = null!;
    private CM_Valve PV104 = null!;
    private CM_Valve PV105 = null!;
    private CM_Valve PV111 = null!;
    private CM_Valve PV112 = null!;
    private CM_Valve PV113 = null!;
    private CM_Valve PV114 = null!;
    private CM_Valve PV115 = null!;
    private CM_Valve PV116 = null!;
    private CM_Valve PV117 = null!;
    private CM_Valve PV118 = null!;
    private CM_Valve PV119 = null!;
    private CM_Valve PV121 = null!;
    private CM_Valve PV122 = null!;
    private CM_Valve PV123 = null!;
    private CM_Valve PV301 = null!;
    private CM_Valve PV302 = null!;
    private CM_Valve PV303 = null!;
    #endregion
}


