

using Common.Recipe;
using Controller._01.ControlModule;
using Controller.Common;
using Controller.EventLogger;
using Controller.gRPC;
using Controller.Hardware;
using Controller.S88;

namespace Controller._02.EquipmentModule;

public class EM_GasSystem : S88EquipmentModuleBase
{
    private readonly GasSystemCfg _cfg;
    private readonly IRetainDataService _retainData;
    private GasSystemAction _selectedAction = GasSystemAction.None;
    private IRecipeStep? _currentRecipeStep;
    private long _actionStartTimestampMs = 0;
    private string _rejectedActionName = string.Empty;
    private uint _pumpDownTimeoutMs, _ventTimeoutMs, _purgeLinePurgeTimeMs, _purgeLinePumpTimeMs, _currentPurgeCycle;
    private float _pumpDownTargetPressurePa, _pumpDownRoughingPressureThresholdPa, _ventTargetPressurePa, _purgeLineTargetPressurePa, _purgeLineTimesSP;
    private bool? _operatorConfirmResult = null; // 交互标志位：null=等待中, true=OK, false=Cancel
    private bool _isWaitingForOperator = false;  // 标记当前是否处于“挂起死等”状态
    private float _leakCheckTargetPressurePa, _leakCheckDropThresholdPa, _leakCheckInitialPressurePa, _depressurizeTargetChamberPressurePa, _depressurizeThresholdPa;
    private uint _leakCheckHoldTimeMs, _depressurizeTimesSP, _currentDepressurizeCycle;


    public EM_GasSystem(GasSystemCfg cfg, ILogger<S88EquipmentModuleBase> logger, IEventProducer eventProducer,
        IONodes iONodes,
        IRetainDataService retainData,
        IMfcFactory mfcFactory,
        IScaleAIFactory scaleAIFactory,
        ICheckSensorFactory checkSensorFactory,
        ITempControllerFactory tempControllerFactory,
        IValveFactory valveFactory) : base(cfg.Name, eventProducer, logger)
    {
        _cfg = cfg;
        _retainData = retainData;
        RegisterMfcs(iONodes, mfcFactory);
        RegisterValvers(iONodes, valveFactory);
        RegisterScaleAIs(iONodes, scaleAIFactory);
        RegisterCheckSensors(iONodes, checkSensorFactory);
        RegisterTempControllers(iONodes, tempControllerFactory);

        RegisterCommandHandlers();

        _pumpDownTimeoutMs = _retainData.GetValue($"{Name}.PumpDownTimeoutMs", 180000u);
        _ventTimeoutMs = _retainData.GetValue($"{Name}.VentTimeoutMs", 300000u);
    }

    public GasSystemAlarmState AlarmState { get; } = new();

    protected override void OnExecute()
    {
        if (State != EMState.Busy) return;

        switch (_selectedAction)
        {
            case GasSystemAction.SetupReactionZone:
                ExecuteReactionZoneSetup();
                break;
            case GasSystemAction.PumpDown:
                ExecutePumpDownSetup();
                break;
            case GasSystemAction.Vent:
                ExecuteVentSetup();
                break;
            case GasSystemAction.PurgeLineSn:
                ExecutePurgeLineSn();
                break;
            case GasSystemAction.LeakCheckSn:
                ExecuteLeakCheckSn();
                break;
            case GasSystemAction.DepressurizeSn:
                ExecuteDepressurizeSn();
                break;
            default:
                Step = 0;
                break;
        }
    }

    protected override void OnAbort()
    {
        ToSafe();
        Step = 0;
        _selectedAction = GasSystemAction.None;
    }

    // 供 ProcessChamber 直接调用的极速接口, 绕过字符串 Command 队列
    public void StartRecipeAction(GasSystemAction action, IRecipeStep stepParams)
    {
        if (State == EMState.Error || State == EMState.Busy)
        {
            AlarmState.ActionRejectedError = true;
            _rejectedActionName = action.ToString();
            return;
        }
        Step = 0;
        _selectedAction = action;
        _currentRecipeStep = stepParams;
        _actionStartTimestampMs = CurrentTimestampMs;
        RaiseInfo(GasSystemEvents.InfoActionStarted, _selectedAction.ToString());
        ChangeState(EMState.Busy);
    }

    private void ExecuteReactionZoneSetup()
    {
        if (_currentRecipeStep is not ReactionZoneStep step) return;

        PV102.Open();
        PV103.Close(); PV104.Open(); PV105.Open(); PV106.Close();
        PV121.Open(); PV122.Open(); PV123.Open(); PV303.Open(); //H2O
        PV111.Open(); PV112.Open(); PV113.Open(); PV114.Close(); PV115.Open(); PV116.Open(); PV117.Close(); PV118.Close(); PV301.Open(); //Sn
        PV119.Open(); PV302.Open();

        MFC111.SetFlow(step.CarrierGasAFlowSccm); //Sn
        MFC112.SetFlow(step.DilutionGasAFlowSccm);
        MFC121.SetFlow(step.CarrierGasBFlowSccm); //H2O
        MFC122.SetFlow(step.DilutionGasBFlowSccm);
        MFC131.SetFlow(step.IsolationGasFlowSccm);
        MFC132.SetFlow(step.IsolationGasFlowSccm);

        ChangeState(EMState.Idle);
    }

    // ==========================================
    // 抽真空流程（PumpDown）
    // ==========================================
    private void ExecutePumpDownSetup()
    {
        if (_currentRecipeStep is not PumpDownStep) return;

        if (CurrentTimestampMs - _actionStartTimestampMs > _pumpDownTimeoutMs)
        {
            AlarmState.PumpDownTimeoutError = true;
            return;
        }

        switch (Step)
        {
            case 0:
                if (!_cfg.CheckLidClosed()) { AlarmState.LidNotClosedError = true; return; }
                Step = 10;
                break;
            case 10:
                if (!_cfg.ChecGateValveClosed()) { AlarmState.GateValveNotClosedError = true; return; }
                Step = 20;
                break;
            case 20:
                if (!_cfg.CheckPumpRunning()) { AlarmState.PumpNotRunningError = true; return; }
                Step = 30;
                break;
            case 30:
                if (_retainData.TryGetValue($"{Name}.PumpDownTimeoutMs", out _pumpDownTimeoutMs) &&
                    _retainData.TryGetValue($"{Name}.PumpDownTargetPressurePa", out _pumpDownTargetPressurePa) &&
                    _retainData.TryGetValue($"{Name}.PumpDownRoughingPressureThresholdPa", out _pumpDownRoughingPressureThresholdPa))
                {
                    Step = 40;
                }
                else
                {
                    AlarmState.MissingPumpDownParameterError = true;
                }
                break;
            case 40:
                if (IsNewStep) CloseAllMFCsAndProcessValves();
                if (StepTimeout(500)) Step = 50;
                break;
            case 50:
                if (IsNewStep)
                {
                    _cfg.ApplyLidSealTorque?.Invoke();
                    PV103.Open(); // 开启初抽阀
                }
                if (_cfg.ReadChamberPressure() <= _pumpDownRoughingPressureThresholdPa)
                {
                    Step = 60;
                }
                break;
            case 60:
                if (StepTimeout(500))
                {
                    PV104.Open(); // 开启主抽阀
                    PV103.Close();
                    // 检查极高真空度要求
                    if (_cfg.ReadChamberPressure() <= _pumpDownTargetPressurePa)
                    {
                        Step = 70;
                    }
                }
                break;
            case 70:
                _cfg.ReleaseLidSealTorque?.Invoke();
                _selectedAction = GasSystemAction.None;
                ChangeState(EMState.Idle);
                RaiseInfo(GasSystemEvents.InfoActionDone, _selectedAction.ToString());
                break;
        }
    }

    // ==========================================
    // 破真空流程（Vent）
    // ==========================================
    private void ExecuteVentSetup()
    {
        if (_currentRecipeStep is not VentStep) return;

        if (CurrentTimestampMs - _actionStartTimestampMs > _ventTimeoutMs)
        {
            AlarmState.VentTimeoutError = true;
            return;
        }

        switch (Step)
        {
            case 0:
                if (_retainData.TryGetValue($"{Name}.VentTimeoutMs", out _ventTimeoutMs) &&
                    _retainData.TryGetValue($"{Name}.VentTargetPressurePa", out _ventTargetPressurePa))
                {
                    Step = 10;
                }
                else
                {
                    AlarmState.MissingVentParameterError = true;
                }
                break;
            case 10:
                if (IsNewStep) CloseAllMFCsAndProcessValves();
                if (StepTimeout(1000) && PV103.State == ValveState.Closed && PV104.State == ValveState.Closed)
                    Step = 20;
                break;
            case 20:
                if (IsNewStep) PV106.Open(); // 打开破空阀
                Step = 30;
                break;
            case 30:
                if (_cfg.ReadChamberPressure() >= _ventTargetPressurePa)
                {
                    Step = 40;
                }
                break;
            case 40:
                if (IsNewStep) PV106.Close();
                Step = 50;
                break;
            case 50:
                _selectedAction = GasSystemAction.None;
                ChangeState(EMState.Idle);
                RaiseInfo(GasSystemEvents.InfoActionDone, _selectedAction.ToString());
                break;
        }
    }

    // ==========================================
    // Sn管路吹扫流程
    // ==========================================
    private void ExecutePurgeLineSn()
    {
        if (_currentRecipeStep is not PurgeLineStep) return;

        switch (Step)
        {
            case 0:
                if (!_cfg.CheckLidClosed()) { AlarmState.LidNotClosedError = true; return; }
                Step = 10;
                break;
            case 10:
                if (!_cfg.ChecGateValveClosed()) { AlarmState.GateValveNotClosedError = true; return; }
                Step = 20;
                break;
            case 20:
                if (!_cfg.CheckPumpRunning()) { AlarmState.PumpNotRunningError = true; return; }
                Step = 30;
                break;
            case 30:
                if (_retainData.TryGetValue($"{Name}.PurgeLineTargetPressurePa", out _purgeLineTargetPressurePa) &&
                    _retainData.TryGetValue($"{Name}.PurgeLineTimesSP", out _purgeLineTimesSP) &&
                    _retainData.TryGetValue($"{Name}.PurgeLinePurgeTimeMs", out _purgeLinePurgeTimeMs) &&
                    _retainData.TryGetValue($"{Name}.PurgeLinePumpTimeMs", out _purgeLinePumpTimeMs))
                {
                    Step = 40;
                }
                else
                {
                    AlarmState.MissingPurgeLineParaError = true;
                }
                break;
            case 40:
                if (IsNewStep) CloseAllMFCsAndProcessValves();
                if (StepTimeout(500)) Step = 50;
                break;
            case 50:
                if (_cfg.ReadChamberPressure() > _purgeLineTargetPressurePa)
                {
                    AlarmState.ChamberPressureHighError = true;
                    return;
                }
                Step = 60;
                break;
            case 60:
                if (IsNewStep)
                {
                    _isWaitingForOperator = true;
                    _operatorConfirmResult = null;
                    RaiseInfo(GasSystemEvents.PromptOperatorAction, "请确认 Sn 源瓶进出口手阀已完全关闭！确认无误后请点击 OK。");
                }

                if (_operatorConfirmResult.HasValue)
                {
                    _isWaitingForOperator = false;
                    if (_operatorConfirmResult.Value == true)
                    {
                        _currentPurgeCycle = 0;
                        Step = 70;
                    }
                    else
                    {
                        AlarmState.OperatorCancelledError = true; 
                        return;
                    }
                }
                else if (StepTimeout(60000))
                {
                    _isWaitingForOperator = false;
                    AlarmState.OperatorTimeoutError = true;
                    return;
                }
                break;
            case 70:
                if (IsNewStep)
                {
                    PV102.Open(); PV104.Open(); PV111.Open();
                    PV112.Open(); PV113.Open(); PV114.Open();
                    PV115.Open(); PV116.Open(); PV301.Open();
                    MFC111.SetFlow(0f);
                    MFC112.SetFlow(0f);
                }
                if (StepTimeout((long)_purgeLinePumpTimeMs)) Step = 80;
                break;
            case 80:
                if (IsNewStep)
                {
                    MFC111.SetFlow(99999f);
                    MFC112.SetFlow(99999f);
                }
                if (StepTimeout((long)_purgeLinePurgeTimeMs)) Step = 90;
                break;
            case 90:
                _currentPurgeCycle++;
                if (_currentPurgeCycle < _purgeLineTimesSP) Step = 70;
                else Step = 100;
                break;
            case 100:
                if (IsNewStep)
                {
                    PV102.Close(); PV111.Close(); PV112.Close();
                    PV113.Close(); PV114.Close(); PV115.Close();
                    PV116.Close(); PV301.Close();
                    MFC111.SetFlow(0f);
                    MFC112.SetFlow(0f);
                }
                if (StepTimeout(1000)) Step = 110;
                break;
            case 110:
                _selectedAction = GasSystemAction.None;
                ChangeState(EMState.Idle);
                RaiseInfo(GasSystemEvents.InfoActionDone, "PurgeLineSn");
                break;
        }
    }

    // ==========================================
    // Sn管路检漏流程
    // ==========================================
    private void ExecuteLeakCheckSn()
    {
        switch (Step)
        {
            case 0:
                if (!_cfg.CheckLidClosed()) { AlarmState.LidNotClosedError = true; return; }
                Step = 10;
                break;
            case 10:
                if (!_cfg.ChecGateValveClosed()) { AlarmState.GateValveNotClosedError = true; return; }
                Step = 20;
                break;
            case 20:
                if (!_cfg.CheckPumpRunning()) { AlarmState.PumpNotRunningError = true; return; }
                Step = 30;
                break;
            case 30:
                if (_retainData.TryGetValue($"{Name}.LeakCheckHoldTimeMs", out _leakCheckHoldTimeMs) &&
                    _retainData.TryGetValue($"{Name}.LeakCheckDropThresholdPa", out _leakCheckDropThresholdPa) &&
                    _retainData.TryGetValue($"{Name}.LeakCheckTargetPressurePa", out _leakCheckTargetPressurePa))
                {
                    Step = 40;
                }
                else
                {
                    AlarmState.MissingLeakCheckParaError = true;
                }
                break;
            case 40:
                if (IsNewStep) CloseAllMFCsAndProcessValves(); 
                if (StepTimeout(500)) Step = 50;
                break;
            case 50:
                if (_cfg.ReadChamberPressure() > _leakCheckTargetPressurePa)
                {
                    AlarmState.ChamberPressureHighError = true;
                    return;
                }
                Step = 60;
                break;
            case 60:
                if (IsNewStep)
                {
                    _isWaitingForOperator = true;
                    _operatorConfirmResult = null;
                    RaiseInfo(GasSystemEvents.PromptOperatorAction, "检漏前准备：请确认 Sn 源瓶【进口】与【出口】手阀均已完全关闭！确认后点击 OK。");
                }

                if (_operatorConfirmResult.HasValue)
                {
                    _isWaitingForOperator = false;
                    if (_operatorConfirmResult.Value == true) Step = 70;
                    else
                    {
                        AlarmState.OperatorCancelledError = true;
                        return;
                    }
                }
                else if (StepTimeout(60000)) { _isWaitingForOperator = false; AlarmState.OperatorTimeoutError = true; return; }
                break;
            case 70:
                if (IsNewStep)
                {
                    PV102.Open(); PV104.Open(); PV111.Open(); PV112.Open();
                    PV113.Open(); PV114.Open(); PV115.Open(); PV116.Open(); PV301.Open();
                    MFC111.SetFlow(99999f); MFC112.SetFlow(99999f);
                }
                if (StepTimeout(60000)) Step = 80;
                break;
            case 80:
                if (IsNewStep)
                {
                    PV102.Close();
                    MFC111.SetFlow(0f); MFC112.SetFlow(0f);
                }
                if (StepTimeout(120000)) Step = 90;
                break;
            case 90:
                if (IsNewStep) PV301.Close();
                if (StepTimeout(10000))
                {
                    _leakCheckInitialPressurePa = PT111.ScaledValue;
                    Step = 100;
                }
                break;
            case 100:
                float deviation = PT111.ScaledValue - _leakCheckInitialPressurePa;
                if (deviation > _leakCheckDropThresholdPa)
                {
                    AlarmState.SnLeakCheckFailedError = true;
                    return;
                }
                if (StepTimeout((long)_leakCheckHoldTimeMs))
                {
                    Step = 110;
                }
                break;
            case 110:
                if (IsNewStep)
                {
                    PV111.Close(); PV112.Close(); PV113.Close();
                    PV114.Close(); PV115.Close(); PV116.Close();
                }
                if (StepTimeout(500))
                {
                    _selectedAction = GasSystemAction.None;
                    ChangeState(EMState.Idle);
                    RaiseInfo(GasSystemEvents.InfoActionDone, "LeakCheckSn");
                }
                break;
        }
    }

    // ==========================================
    // Sn源瓶泄压流程
    // ==========================================
    private void ExecuteDepressurizeSn()
    {
        switch (Step)
        {
            case 0:
                if (!_cfg.CheckLidClosed()) { AlarmState.LidNotClosedError = true; return; }
                if (!_cfg.ChecGateValveClosed()) { AlarmState.GateValveNotClosedError = true; return; }
                if (!_cfg.CheckPumpRunning()) { AlarmState.PumpNotRunningError = true; return; }
                Step = 10;
                break;
            case 10:
                if (_retainData.TryGetValue($"{Name}.DepressurizeTimesSP", out _depressurizeTimesSP) &&
                    _retainData.TryGetValue($"{Name}.DepressurizeThresholdPa", out _depressurizeThresholdPa) &&
                    _retainData.TryGetValue($"{Name}.DepressurizeTargetChamberPressurePa", out _depressurizeTargetChamberPressurePa))
                {
                    Step = 20;
                }
                else
                {
                    AlarmState.MissingSnDepressurizeParaError = true;
                }
                break;
            case 20:
                if (IsNewStep) CloseAllMFCsAndProcessValves();
                if (StepTimeout(500)) Step = 30;
                break;
            case 30:
                if (_cfg.ReadChamberPressure() > _depressurizeTargetChamberPressurePa)
                {
                    AlarmState.ChamberPressureHighError = true;
                    return;
                }
                Step = 40;
                break;
            case 40:
                if (IsNewStep)
                {
                    _isWaitingForOperator = true;
                    _operatorConfirmResult = null;
                    RaiseInfo(GasSystemEvents.PromptOperatorAction, "Sn源瓶泄压：请确认 Sn 源瓶【出口】手阀已开启！【进口】手阀保持关闭！确认后点击 OK。");
                }

                if (_operatorConfirmResult.HasValue)
                {
                    _isWaitingForOperator = false;
                    if (_operatorConfirmResult.Value == true) Step = 50;
                    else
                    {
                        AlarmState.OperatorCancelledError = true;
                        return;
                    }
                }
                else if (StepTimeout(60000))
                {
                    _isWaitingForOperator = false;
                    AlarmState.OperatorTimeoutError = true;
                    return;
                }
                break;
            case 50:
                if (IsNewStep) { PV115.Open(); PV117.Open(); PV118.Open(); }
                if (StepTimeout(10000)) Step = 60;
                break;
            case 60:
                if (IsNewStep) { PV117.Close(); PV118.Close(); }
                if (StepTimeout(500))
                {
                    _currentDepressurizeCycle = 0;
                    Step = 70;
                }
                break;
            case 70:
                if (IsNewStep) PV113.Open();
                if (StepTimeout(2000)) Step = 80;
                break;
            case 80:
                if (IsNewStep) PV113.Close();
                if (StepTimeout(500)) Step = 90;
                break;
            case 90:
                if (IsNewStep) { PV117.Open(); PV118.Open(); }
                if (StepTimeout(2000)) Step = 100;
                break;
            case 100:
                if (IsNewStep) { PV117.Close(); PV118.Close(); }
                if (StepTimeout(500)) Step = 110;
                break;
            case 110:
                if (IsNewStep)
                {
                    _currentDepressurizeCycle++;
                    if (_currentDepressurizeCycle < _depressurizeTimesSP) Step = 70;
                    else Step = 120;
                }
                break;
            case 120:
                if (IsNewStep)
                {
                    PV102.Open(); PV111.Open(); PV112.Open();
                    MFC112.SetFlow(99999f);
                }
                if (StepTimeout(1000)) Step = 130;
                break;
            case 130:
                if (IsNewStep)
                {
                    PV102.Close(); PV111.Close(); PV112.Close();
                    MFC112.SetFlow(0f);
                }
                if (StepTimeout(500)) Step = 140;
                break;
            case 140:
                if (IsNewStep)
                {
                    _isWaitingForOperator = true;
                    _operatorConfirmResult = null;
                    RaiseInfo(GasSystemEvents.PromptOperatorAction, "Sn源瓶泄压：请前往气柜物理开启 Sn 源【进口】手阀！确认后点击 OK。");
                }

                if (_operatorConfirmResult.HasValue)
                {
                    _isWaitingForOperator = false;
                    if (_operatorConfirmResult.Value == true) Step = 150;
                    else
                    {
                        AlarmState.OperatorCancelledError = true;
                        return;
                    }
                }
                else if (StepTimeout(60000)) { _isWaitingForOperator = false; AlarmState.OperatorTimeoutError = true; return; }
                break;
            case 150:
                if (IsNewStep)
                {
                    float finalPressure = PT111.ScaledValue;
                    if (finalPressure <= _depressurizeThresholdPa)
                    {
                        _selectedAction = GasSystemAction.None;
                        ChangeState(EMState.Idle);
                        RaiseInfo(GasSystemEvents.InfoActionDone, "DepressurizeSn");
                    }
                    else
                    {
                        AlarmState.SnDepressurizeFailedError = true;
                        return;
                    }
                }
                break;
        }
    }

    private void CloseAllMFCsAndProcessValves()
    {
        MFC111.SetFlow(0f); MFC112.SetFlow(0f);
        MFC121.SetFlow(0f); MFC122.SetFlow(0f);
        MFC131.SetFlow(0f); MFC132.SetFlow(0f);

        PV102.Close(); PV111.Close(); PV112.Close();
        PV113.Close(); PV114.Close(); PV115.Close();
        PV116.Close(); PV117.Close(); PV118.Close();
        PV119.Close(); PV121.Close(); PV122.Close();
        PV123.Close(); PV301.Close(); PV302.Close();
        PV303.Close(); PV105.Close();

        PV103.Close(); PV104.Close(); PV106.Close();
    }

    // ==========================================
    // 宏指令：伴热系统群控 (Group Control)
    // ==========================================
    private void StartAllSnHeaters(float sp)
    {
        SnHeateringZoneG.Start(sp); SnHeateringZoneH.Start(sp); SnHeateringZoneI.Start(sp);
        SnHeateringZoneJ.Start(sp); SnHeateringZone1.Start(sp); SnHeateringZone2.Start(sp);
        SnHeateringZone3.Start(sp); SnHeateringZone4.Start(sp); SnHeateringZone5.Start(sp);
        SnHeateringZone6_1.Start(sp); SnHeateringZone6_2.Start(sp); SnHeateringZone7.Start(sp);
        SnHeateringZone8.Start(sp); SnHeateringZone9.Start(sp); SnHeateringZone10.Start(sp);
        SnHeateringZone11.Start(sp); SnHeateringZone12.Start(sp);
        RaiseInfo(GasSystemEvents.InfoHeatingStarted, sp);
    }

    private void StopAllSnHeaters()
    {
        SnHeateringZoneG.Stop(); SnHeateringZoneH.Stop(); SnHeateringZoneI.Stop();
        SnHeateringZoneJ.Stop(); SnHeateringZone1.Stop(); SnHeateringZone2.Stop();
        SnHeateringZone3.Stop(); SnHeateringZone4.Stop(); SnHeateringZone5.Stop();
        SnHeateringZone6_1.Stop(); SnHeateringZone6_2.Stop(); SnHeateringZone7.Stop();
        SnHeateringZone8.Stop(); SnHeateringZone9.Stop(); SnHeateringZone10.Stop();
        SnHeateringZone11.Stop(); SnHeateringZone12.Stop();
        RaiseInfo(GasSystemEvents.InfoHeatingStopped);
    }

    protected void RegisterCommandHandlers()
    {
        RegisterCommandHandler(Command.Reset, cmd =>
        {
            Reset(cmd);
            cmd.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Accepted, ""));
        });

        RegisterCommandHandler(Command.HeatingStart, cmd =>
        {
            if (cmd.Params.TryGetValue("SP", out string? spStr) && float.TryParse(spStr, out float sp))
            {
                StartAllSnHeaters(sp);
                cmd.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Accepted, "已启动所有伴热"));
                return;
            }
            cmd.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Rejected, "无效的伴热 SP 参数"));
        });

        RegisterCommandHandler(Command.HeatingStop, cmd =>
        {
            StopAllSnHeaters();
            cmd.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Accepted, "已停止所有伴热"));
        });

        RegisterCommandHandler(Command.PumpDown, cmd =>
        {
            if (State != EMState.Idle)
            {
                cmd.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Rejected, "气路正在执行其他动作，拒绝指令"));
                return;
            }

            if (cmd.Params.TryGetValue("RoughingPressureThresholdPa", out string? roughingPressureThresholdStr) && float.TryParse(roughingPressureThresholdStr, out float roughingPressureThresholdPa) &&
                cmd.Params.TryGetValue("TargetPressurePa", out string? targetPressurePaStr) && float.TryParse(targetPressurePaStr, out float targetPressurePa) &&
                cmd.Params.TryGetValue("Timeout", out string? timeoutMsStr) && uint.TryParse(timeoutMsStr, out uint timeoutMs))
            {
                StartRecipeAction(GasSystemAction.PumpDown, new PumpDownStep() { RoughingPressureThresholdPa = roughingPressureThresholdPa, TargetPressurePa = targetPressurePa, TimeoutMs = timeoutMs });
                cmd.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Accepted, string.Empty));
                return;
            }

            cmd.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Rejected, "无效的参数"));
        });

        RegisterCommandHandler(Command.Vent, cmd =>
        {
            if (State != EMState.Idle)
            {
                cmd.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Rejected, "气路正在执行其他动作，拒绝指令"));
                return;
            }

            if (cmd.Params.TryGetValue("TargetPressurePa", out string? targetPressurePaStr) && float.TryParse(targetPressurePaStr, out float targetPressurePa) &&
                cmd.Params.TryGetValue("Timeout", out string? timeoutMsStr) && uint.TryParse(timeoutMsStr, out uint timeoutMs))
            {
                StartRecipeAction(GasSystemAction.Vent, new VentStep() { TargetPressurePa = targetPressurePa, TimeoutMs = timeoutMs });
                cmd.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Accepted, string.Empty));
                return;
            }

            cmd.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Rejected, "无效的参数"));
        });

        // 专门处理弹窗回复
        RegisterCommandHandler(Command.OperatorConfirm, cmd =>
        {
            if (!_isWaitingForOperator)
            {
                cmd.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Rejected, "当前没有正在等待人工确认的流程"));
                return;
            }

            if (cmd.Params.TryGetValue("Result", out string? resultStr))
            {
                if (resultStr.Equals("OK", StringComparison.OrdinalIgnoreCase))
                {
                    _operatorConfirmResult = true;
                    cmd.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Accepted, "收到 OK"));
                    return;
                }
                else if (resultStr.Equals("Cancel", StringComparison.OrdinalIgnoreCase))
                {
                    _operatorConfirmResult = false;
                    cmd.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Accepted, "收到 Cancel"));
                    return;
                }
            }

            cmd.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Rejected, "缺失 Result 参数或格式不正确(需为 OK 或 Cancel)"));
        });

        RegisterCommandHandler(Command.PurgeLineSn, cmd =>
        {
            if (State != EMState.Idle)
            {
                cmd.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Rejected, "气路正在执行其他动作，拒绝指令"));
                return;
            }

            StartRecipeAction(GasSystemAction.PurgeLineSn, new PurgeLineStep());
            cmd.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Accepted, "已启动 Sn 管路吹扫流程"));
        });

        RegisterCommandHandler(Command.LeakCheckSn, cmd =>
        {
            if (State != EMState.Idle)
            {
                cmd.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Rejected, "气路正在执行其他动作，拒绝指令"));
                return;
            }

            StartRecipeAction(GasSystemAction.LeakCheckSn, null!);
            cmd.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Accepted, "已启动 Sn 管路检漏流程"));
        });

        RegisterCommandHandler(Command.DepressurizeSn, cmd =>
        {
            if (State != EMState.Idle)
            {
                cmd.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Rejected, "气路正在执行其他动作，拒绝指令"));
                return;
            }

            StartRecipeAction(GasSystemAction.DepressurizeSn, null!);
            cmd.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Accepted, "已启动 Sn 源瓶泄压流程"));
        });
    }

    protected override void AlarmHandler()
    {
        // 致命安全监控
        if (CH4Leak_Detect.HasAnyError || Flame_Detect.HasAnyError ||
            Smoke_Heat_Detect.HasAnyError || SnOverTemp_Detect.HasAnyError || H2OOverTemp_Detect.HasAnyError)
            AlarmState.SafetySensorTriggered = true;

        if (HasAnyChildError())
            AlarmState.ChildModuleFault = true;

        if (AlarmState.SafetySensorTriggered) RaiseAlarm(GasSystemEvents.ErrSafetySensorTriggered);
        else TryClearAlarm(GasSystemEvents.ErrSafetySensorTriggered);

        if (AlarmState.ChildModuleFault) RaiseAlarm(GasSystemEvents.ErrChildModuleFault);
        else TryClearAlarm(GasSystemEvents.ErrChildModuleFault);

        if (AlarmState.LidNotClosedError) RaiseAlarm(GasSystemEvents.ErrLidNotClosed, _selectedAction.ToString());
        else TryClearAlarm(GasSystemEvents.ErrLidNotClosed);

        if (AlarmState.GateValveNotClosedError) RaiseAlarm(GasSystemEvents.ErrGateValveNotClosed, _selectedAction.ToString());
        else TryClearAlarm(GasSystemEvents.ErrGateValveNotClosed);

        if (AlarmState.PumpNotRunningError) RaiseAlarm(GasSystemEvents.ErrPumpFault, _selectedAction.ToString());
        else TryClearAlarm(GasSystemEvents.ErrPumpFault);

        if (AlarmState.PumpDownTimeoutError) RaiseAlarm(GasSystemEvents.ErrPumpDownTimeout, _pumpDownTimeoutMs, _pumpDownTargetPressurePa);
        else TryClearAlarm(GasSystemEvents.ErrPumpDownTimeout);

        if (AlarmState.VentTimeoutError) RaiseAlarm(GasSystemEvents.ErrVentTimeout, _ventTimeoutMs, _ventTargetPressurePa);
        else TryClearAlarm(GasSystemEvents.ErrVentTimeout);

        if (AlarmState.ActionRejectedError) RaiseAlarm(GasSystemEvents.ErrActionRejected, State.ToString(), _rejectedActionName);
        else TryClearAlarm(GasSystemEvents.ErrActionRejected);

        if (AlarmState.MissingPumpDownParameterError) RaiseAlarm(GasSystemEvents.ErrMissingPumpDownParameter, "TimeoutMs/TargetPressurePa/RoughingPressureThresholdPa");
        else TryClearAlarm(GasSystemEvents.ErrMissingPumpDownParameter);

        if (AlarmState.MissingVentParameterError) RaiseAlarm(GasSystemEvents.ErrMissingVentParameter, "TimeoutMs/TargetPressurePa");
        else TryClearAlarm(GasSystemEvents.ErrMissingVentParameter);

        if (AlarmState.OperatorTimeoutError) RaiseAlarm(GasSystemEvents.ErrOperatorTimeout);
        else TryClearAlarm(GasSystemEvents.ErrOperatorTimeout);

        if (AlarmState.OperatorCancelledError) RaiseAlarm(GasSystemEvents.ErrOperatorCancelled);
        else TryClearAlarm(GasSystemEvents.ErrOperatorCancelled);

        if (AlarmState.MissingPurgeLineParaError) RaiseAlarm(GasSystemEvents.ErrMissingPurgeLinePara);
        else TryClearAlarm(GasSystemEvents.ErrMissingPurgeLinePara);

        if (AlarmState.ChamberPressureHighError) RaiseAlarm(GasSystemEvents.ErrChamberPressureHigh, _purgeLineTargetPressurePa);
        else TryClearAlarm(GasSystemEvents.ErrChamberPressureHigh);

        if (AlarmState.MissingLeakCheckParaError) RaiseAlarm(GasSystemEvents.ErrMissingLeakCheckPara);
        else TryClearAlarm(GasSystemEvents.ErrMissingLeakCheckPara);

        if (AlarmState.SnLeakCheckFailedError) RaiseAlarm(GasSystemEvents.ErrLeakCheckFailed, PT111.ScaledValue - _leakCheckInitialPressurePa);
        else TryClearAlarm(GasSystemEvents.ErrLeakCheckFailed);

        if (AlarmState.MissingSnDepressurizeParaError) RaiseAlarm(GasSystemEvents.ErrMissingDepressurizePara);
        else TryClearAlarm(GasSystemEvents.ErrMissingDepressurizePara);

        if (AlarmState.SnDepressurizeFailedError) RaiseAlarm(GasSystemEvents.ErrDepressurizeFailed, PT111.ScaledValue);
        else TryClearAlarm(GasSystemEvents.ErrDepressurizeFailed);

        if (!AlarmState.HasAnyError)
        {
            ChangeState(EMState.Idle);
            RaiseInfo(GasSystemEvents.InfoResetDone);
        }
    }

    protected override void Reset(InternalCommand cmd)
    {
        if (State != EMState.Error) return;

        AlarmState.SafetySensorTriggered = false;
        AlarmState.ChildModuleFault = false;
        AlarmState.MissingPumpDownParameterError = false;
        AlarmState.MissingPurgeLineParaError = false;
        AlarmState.MissingVentParameterError = false;
        AlarmState.GateValveNotClosedError = false;
        AlarmState.LidNotClosedError = false;
        AlarmState.PumpNotRunningError = false;
        AlarmState.PumpDownTimeoutError = false;
        AlarmState.VentTimeoutError = false;
        AlarmState.OperatorTimeoutError = false;
        AlarmState.OperatorCancelledError = false;
        AlarmState.ChamberPressureHighError = false;
        AlarmState.ActionRejectedError = false;
        AlarmState.MissingLeakCheckParaError = false;
        AlarmState.SnLeakCheckFailedError = false;
        AlarmState.MissingSnDepressurizeParaError = false;
        AlarmState.SnDepressurizeFailedError = false;

        base.Reset(cmd); // 让底层的CM也去执行复位
    }

    #region Valve
    private CM_Valve PV102 = null!;
    private CM_Valve PV103 = null!;
    private CM_Valve PV104 = null!;
    private CM_Valve PV105 = null!;
    private CM_Valve PV106 = null!;
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
    private void RegisterValvers(IONodes iONodes, IValveFactory valveFactory)
    {
        PV102 = RegisterValve("PV102", valveFactory, () => iONodes.ESV01[0] = true, () => iONodes.ESV01[0] = false, canOpen: () => true, canClose: () => true);
        PV103 = RegisterValve("PV103", valveFactory, () => iONodes.A110[13] = true, () => iONodes.A110[13] = false, canOpen: () => true, canClose: () => true);
        PV104 = RegisterValve("PV104", valveFactory, () => iONodes.A110[12] = true, () => iONodes.A110[12] = false, canOpen: () => true, canClose: () => true);
        PV105 = RegisterValve("PV105", valveFactory, () => iONodes.ESV01[19] = true, () => iONodes.ESV01[19] = false, canOpen: () => true, canClose: () => true);
        PV106 = RegisterValve("PV106", valveFactory, () => iONodes.A112[14] = true, () => iONodes.A112[14] = false, canOpen: () => true, canClose: () => true);
        PV111 = RegisterValve("PV111", valveFactory, () => iONodes.ESV01[2] = true, () => iONodes.ESV01[2] = false, canOpen: () => true, canClose: () => true);
        PV112 = RegisterValve("PV112", valveFactory, () => iONodes.ESV01[4] = true, () => iONodes.ESV01[4] = false, canOpen: () => true, canClose: () => true);
        PV113 = RegisterValve("PV113", valveFactory, () => iONodes.ESV01[6] = true, () => iONodes.ESV01[6] = false, canOpen: () => true, canClose: () => true);
        PV114 = RegisterValve("PV114", valveFactory, () => iONodes.ESV01[8] = true, () => iONodes.ESV01[8] = false, canOpen: () => true, canClose: () => true);
        PV115 = RegisterValve("PV115", valveFactory, () => iONodes.ESV01[10] = true, () => iONodes.ESV01[10] = false, canOpen: () => true, canClose: () => true);
        PV116 = RegisterValve("PV116", valveFactory, () => iONodes.ESV01[12] = true, () => iONodes.ESV01[12] = false, canOpen: () => true, canClose: () => true);
        PV117 = RegisterValve("PV117", valveFactory, () => iONodes.ESV01[14] = true, () => iONodes.ESV01[14] = false, canOpen: () => true, canClose: () => true);
        PV118 = RegisterValve("PV118", valveFactory, () => iONodes.ESV01[3] = true, () => iONodes.ESV01[3] = false, canOpen: () => true, canClose: () => true);
        PV119 = RegisterValve("PV119", valveFactory, () => iONodes.ESV01[16] = true, () => iONodes.ESV01[16] = false, canOpen: () => true, canClose: () => true);
        PV121 = RegisterValve("PV121", valveFactory, () => iONodes.ESV01[18] = true, () => iONodes.ESV01[18] = false, canOpen: () => true, canClose: () => true);
        PV122 = RegisterValve("PV122", valveFactory, () => iONodes.ESV01[15] = true, () => iONodes.ESV01[15] = false, canOpen: () => true, canClose: () => true);
        PV123 = RegisterValve("PV123", valveFactory, () => iONodes.ESV01[17] = true, () => iONodes.ESV01[17] = false, canOpen: () => true, canClose: () => true);
        PV301 = RegisterValve("PV301", valveFactory, () => iONodes.ESV01[9] = true, () => iONodes.ESV01[9] = false, canOpen: () => true, canClose: () => true);
        PV302 = RegisterValve("PV302", valveFactory, () => iONodes.ESV01[11] = true, () => iONodes.ESV01[11] = false, canOpen: () => true, canClose: () => true);
        PV303 = RegisterValve("PV303", valveFactory, () => iONodes.ESV01[13] = true, () => iONodes.ESV01[13] = false, canOpen: () => true, canClose: () => true);
    }
    #endregion

    #region MFC
    private CM_MFC MFC111 = null!;
    private CM_MFC MFC112 = null!;
    private CM_MFC MFC121 = null!;
    private CM_MFC MFC122 = null!;
    private CM_MFC MFC131 = null!;
    private CM_MFC MFC132 = null!;
    private void RegisterMfcs(IONodes iONodes, IMfcFactory mfcFactory)
    {
        MFC111 = RegisterMfc("MFC111", mfcFactory, iONodes.MFC111, 5000f);
        MFC112 = RegisterMfc("MFC112", mfcFactory, iONodes.MFC112, 5000f);
        MFC121 = RegisterMfc("MFC121", mfcFactory, iONodes.MFC121, 5000f);
        MFC122 = RegisterMfc("MFC122", mfcFactory, iONodes.MFC122, 1000f);
        MFC131 = RegisterMfc("MFC131", mfcFactory, iONodes.MFC131, 50000f);
        MFC132 = RegisterMfc("MFC132", mfcFactory, iONodes.MFC132, 50000f);
    }
    #endregion

    #region ScaleAI
    private CM_ScaleAI RegisterScaleAI(
        string name,
        IScaleAIFactory factory,
        Func<float> readRawValue,
        float rawMin, float rawMax,
        float scaledMin, float scaledMax,
        string unit,
        float filterAlpha = 1.0f)
    {
        var cfg = new ScaleAICfg
        {
            Name = name,
            ReadRawValue = readRawValue,
            RawMin = rawMin,
            RawMax = rawMax,
            ScaledMin = scaledMin,
            ScaledMax = scaledMax,
            EngineeringUnit = unit,
            FilterAlpha = filterAlpha
        };

        var ai = factory.Create(cfg);
        RegisterCm(ai);
        return ai;
    }
    private CM_ScaleAI PT111 = null!;
    private CM_ScaleAI PT121 = null!;
    private CM_ScaleAI H2OLI = null!;//Level Indicator
    private void RegisterScaleAIs(IONodes iONodes, IScaleAIFactory scaleAIFactory)
    {
        //Sn源出口
        PT111 = RegisterScaleAI("PT111", scaleAIFactory,
            readRawValue: () => iONodes.A205[0],
            rawMin: 0, rawMax: 4000,
            scaledMin: 0f, scaledMax: 133.322f,
            unit: "kPa");

        //H2O源出口
        PT121 = RegisterScaleAI("PT121", scaleAIFactory,
            readRawValue: () => iONodes.A205[1],
            rawMin: 0, rawMax: 4000,
            scaledMin: 0f, scaledMax: 133.322f,
            unit: "kPa");

        //H2O源出口
        H2OLI = RegisterScaleAI("H2OLI", scaleAIFactory,
            readRawValue: () => iONodes.A206[0],
            rawMin: 0, rawMax: 8000,
            scaledMin: 0f, scaledMax: 263f,
            unit: "kPa");
    }
    #endregion

    #region CheckSensor
    private CM_CheckSensor WaterBubbler_H = null!;
    private CM_CheckSensor WaterBubbler_L = null!;
    private CM_CheckSensor H2OSourceLevel_A = null!;
    private CM_CheckSensor H2OSourceLevel_B = null!;
    private CM_CheckSensor H2OSourceLevel_C = null!;
    private CM_CheckSensor H2OSourceLevel_D = null!;
    private CM_CheckSensor H2OSourceLevel_Fault = null!;
    private CM_CheckSensor SnOverTemp_Detect = null!;
    private CM_CheckSensor H2OOverTemp_Detect = null!;
    private CM_CheckSensor CH4Leak_Detect = null!;
    private CM_CheckSensor Flame_Detect = null!;
    private CM_CheckSensor Smoke_Heat_Detect = null!;
    private CM_CheckSensor SnCabinetDoorSwitch = null!;
    private void RegisterCheckSensors(IONodes iONodes, ICheckSensorFactory checkSensorFactory)
    {
        //贮水罐液位高
        WaterBubbler_H = RegisterCheckSensor("WaterBubbler_H", checkSensorFactory,
            readSignal: () => iONodes.A201[0]);

        //贮水罐液位低
        WaterBubbler_L = RegisterCheckSensor("WaterBubbler_L", checkSensorFactory,
            readSignal: () => iONodes.A201[1]);

        //水源瓶液位
        H2OSourceLevel_A = RegisterCheckSensor("H2OSourceLevel_A", checkSensorFactory,
            readSignal: () => iONodes.A201[2]);

        //水源瓶液位
        H2OSourceLevel_B = RegisterCheckSensor("H2OSourceLevel_B", checkSensorFactory,
            readSignal: () => iONodes.A201[3]);

        //水源瓶液位
        H2OSourceLevel_C = RegisterCheckSensor("H2OSourceLevel_C", checkSensorFactory,
            readSignal: () => iONodes.A201[4]);

        //水源瓶液位
        H2OSourceLevel_D = RegisterCheckSensor("H2OSourceLevel_D", checkSensorFactory,
            readSignal: () => iONodes.A201[5]);

        //水源瓶液位
        H2OSourceLevel_Fault = RegisterCheckSensor("H2OSourceLevel_Fault", checkSensorFactory,
            readSignal: () => iONodes.A201[6]);

        SnCabinetDoorSwitch = RegisterCheckSensor("SnCabinetDoorSwitch", checkSensorFactory,
            readSignal: () => iONodes.A201[7],
            defaultExpectedSignalState: ExpectedSignalState.ShouldBeOn,
            autoStart: true);

        CH4Leak_Detect = RegisterCheckSensor("CH4Leak_Detect", checkSensorFactory,
            readSignal: () => iONodes.A201[8],
            defaultExpectedSignalState: ExpectedSignalState.ShouldBeOn,
            autoStart: true);

        Flame_Detect = RegisterCheckSensor("Flame_Detect", checkSensorFactory,
            readSignal: () => iONodes.A201[9],
            defaultExpectedSignalState: ExpectedSignalState.ShouldBeOn,
            autoStart: true);

        Smoke_Heat_Detect = RegisterCheckSensor("Smoke_Heat_Detect", checkSensorFactory,
            readSignal: () => iONodes.A201[10],
            defaultExpectedSignalState: ExpectedSignalState.ShouldBeOn,
            autoStart: true);

        SnOverTemp_Detect = RegisterCheckSensor("SnOverTemp_Detect", checkSensorFactory,
            readSignal: () => iONodes.A201[11],
            defaultExpectedSignalState: ExpectedSignalState.ShouldBeOn,
            autoStart: true);

        H2OOverTemp_Detect = RegisterCheckSensor("H2OOverTemp_Detect", checkSensorFactory,
            readSignal: () => iONodes.A201[12],
            defaultExpectedSignalState: ExpectedSignalState.ShouldBeOn,
            autoStart: true);
    }
    #endregion

    #region TempController
    private CM_TempController SnHeateringZoneJ = null!;
    private CM_TempController SnHeateringZoneI = null!;
    private CM_TempController SnHeateringZoneH = null!;
    private CM_TempController SnHeateringZoneG = null!;
    private CM_TempController SnHeateringZone1 = null!;
    private CM_TempController SnHeateringZone2 = null!;
    private CM_TempController SnHeateringZone3 = null!;
    private CM_TempController SnHeateringZone4 = null!;
    private CM_TempController SnHeateringZone5 = null!;
    private CM_TempController SnHeateringZone6_1 = null!;
    private CM_TempController SnHeateringZone6_2 = null!;
    private CM_TempController SnHeateringZone7 = null!;
    private CM_TempController SnHeateringZone8 = null!;
    private CM_TempController SnHeateringZone9 = null!;
    private CM_TempController SnHeateringZone10 = null!;
    private CM_TempController SnHeateringZone11 = null!;
    private CM_TempController SnHeateringZone12 = null!;
    private void RegisterTempControllers(IONodes iONodes, ITempControllerFactory tempControllerFactory)
    {
        //Sn源 - G
        SnHeateringZoneG = RegisterTempController("SnHeateringZoneG", tempControllerFactory,
            readControlTemperature: () => iONodes.A207[0],
            setHeaterOn: (on) => iONodes.A203[0] = on,
            canExecute: () => true,
            maxSafeTemp: 150f);

        //Sn源 - H
        SnHeateringZoneH = RegisterTempController("SnHeateringZoneH", tempControllerFactory,
            readControlTemperature: () => iONodes.A207[1],
            setHeaterOn: (on) => iONodes.A203[1] = on,
            canExecute: () => true,
            maxSafeTemp: 150f);

        //Sn源 - I
        SnHeateringZoneI = RegisterTempController("SnHeateringZoneI", tempControllerFactory,
            readControlTemperature: () => iONodes.A207[2],
            setHeaterOn: (on) => iONodes.A203[2] = on,
            canExecute: () => true,
            maxSafeTemp: 150f);

        //Sn源 - J
        SnHeateringZoneJ = RegisterTempController("SnHeateringZoneJ", tempControllerFactory,
            readControlTemperature: () => iONodes.A207[3],
            setHeaterOn: (on) => iONodes.A203[3] = on,
            canExecute: () => true,
            maxSafeTemp: 150f);

        //Sn源 - 1
        SnHeateringZone1 = RegisterTempController("SnHeateringZone1", tempControllerFactory,
            readControlTemperature: () => iONodes.A208[0],
            setHeaterOn: (on) => iONodes.A203[4] = on,
            canExecute: () => true,
            maxSafeTemp: 150f);

        //Sn源 - 2
        SnHeateringZone2 = RegisterTempController("SnHeateringZone2", tempControllerFactory,
            readControlTemperature: () => iONodes.A208[1],
            setHeaterOn: (on) => iONodes.A203[5] = on,
            canExecute: () => true,
            maxSafeTemp: 150f);

        //Sn源 - 3
        SnHeateringZone3 = RegisterTempController("SnHeateringZone3", tempControllerFactory,
            readControlTemperature: () => iONodes.A208[2],
            setHeaterOn: (on) => iONodes.A203[6] = on,
            canExecute: () => true,
            maxSafeTemp: 150f);

        //Sn源 - 4
        SnHeateringZone4 = RegisterTempController("SnHeateringZone4", tempControllerFactory,
            readControlTemperature: () => iONodes.A208[3],
            setHeaterOn: (on) => iONodes.A203[7] = on,
            canExecute: () => true,
            maxSafeTemp: 150f);

        //Sn源 - 5
        SnHeateringZone5 = RegisterTempController("SnHeateringZone5", tempControllerFactory,
            readControlTemperature: () => iONodes.A210[0],
            setHeaterOn: (on) => iONodes.A203[8] = on,
            canExecute: () => true,
            maxSafeTemp: 150f);

        //Sn源 - 6-1
        SnHeateringZone6_1 = RegisterTempController("SnHeateringZone6_1", tempControllerFactory,
            readControlTemperature: () => iONodes.A210[1],
            setHeaterOn: (on) => iONodes.A203[9] = on,
            canExecute: () => true,
            maxSafeTemp: 150f);

        //Sn源 - 6-2
        SnHeateringZone6_2 = RegisterTempController("SnHeateringZone6_2", tempControllerFactory,
            readControlTemperature: () => iONodes.A210[2],
            setHeaterOn: (on) => iONodes.A203[10] = on,
            canExecute: () => true,
            maxSafeTemp: 150f);

        //Sn源 - 7
        SnHeateringZone7 = RegisterTempController("SnHeateringZone7", tempControllerFactory,
            readControlTemperature: () => iONodes.A210[3],
            setHeaterOn: (on) => iONodes.A203[11] = on,
            canExecute: () => true,
            maxSafeTemp: 150f);

        //Sn源 - 8
        SnHeateringZone8 = RegisterTempController("SnHeateringZone8", tempControllerFactory,
            readControlTemperature: () => iONodes.A211[0],
            setHeaterOn: (on) => iONodes.A203[12] = on,
            canExecute: () => true,
            maxSafeTemp: 150f);

        //Sn源 - 9
        SnHeateringZone9 = RegisterTempController("SnHeateringZone9", tempControllerFactory,
            readControlTemperature: () => iONodes.A211[1],
            setHeaterOn: (on) => iONodes.A203[13] = on,
            canExecute: () => true,
            maxSafeTemp: 150f);

        //Sn源 - 10
        SnHeateringZone10 = RegisterTempController("SnHeateringZone10", tempControllerFactory,
            readControlTemperature: () => iONodes.A216[0],
            setHeaterOn: (on) => iONodes.A204[0] = on,
            canExecute: () => true,
            maxSafeTemp: 150f);

        //Sn源 - 11 - 进口阀
        SnHeateringZone11 = RegisterTempController("SnHeateringZone11", tempControllerFactory,
            readControlTemperature: () => iONodes.A214[2],
            setHeaterOn: (on) => iONodes.A204[11] = on,
            canExecute: () => true,
            maxSafeTemp: 150f);

        //Sn源 - 12 - 出口阀
        SnHeateringZone12 = RegisterTempController("SnHeateringZone12", tempControllerFactory,
            readControlTemperature: () => iONodes.A214[3],
            setHeaterOn: (on) => iONodes.A204[12] = on,
            canExecute: () => true,
            maxSafeTemp: 150f);
    }
    #endregion
}

public enum GasSystemAction
{
    None,
    Pulse,              // 对应 PulseStep
    Purge,              // 对应 PurgeStep
    SetupReactionZone,  // 对应 ReactionZoneStep (空间型ALD)
    PumpDown,           // 对应 PumpDownStep 抽空
    Vent,                // 对应 VentStep 破空
    PurgeLineSn,
    LeakCheckSn,
    DepressurizeSn
}

public class GasSystemCfg : EquipmentModuleCfg
{
    public required Func<float> ReadChamberPressure { get; init; }
    public required Func<bool> CheckLidClosed { get; init; }      // 检查腔盖/门阀是否完全闭合
    public required Func<bool> CheckPumpRunning { get; init; }    // 检查泵是否正常运行且无报警
    public required Func<bool> ChecGateValveClosed { get; init; }    // 检查门阀是否完全闭合
    public Action? ApplyLidSealTorque { get; init; }              // 施加腔盖辅助密封正扭矩
    public Action? ReleaseLidSealTorque { get; init; }            // 解除辅助密封扭矩
}

public sealed class GasSystemAlarmState
{
    public bool HasAnyWarning => false;

    public bool SafetySensorTriggered { get; internal set; }
    public bool ChildModuleFault { get; internal set; }
    public bool MissingPumpDownParameterError { get; internal set; }
    public bool MissingPurgeLineParaError { get; internal set; }
    public bool ChamberPressureHighError { get; internal set; }
    public bool MissingVentParameterError { get; internal set; }
    public bool OperatorTimeoutError { get; internal set; }
    public bool OperatorCancelledError { get; internal set; } 
    public bool GateValveNotClosedError { get; internal set; }
    public bool LidNotClosedError { get; internal set; }
    public bool PumpNotRunningError { get; internal set; }
    public bool PumpDownTimeoutError { get; internal set; }
    public bool VentTimeoutError { get; internal set; }
    public bool ActionRejectedError { get; internal set; }
    public bool MissingLeakCheckParaError { get; internal set; }
    public bool SnLeakCheckFailedError { get; internal set; }
    public bool MissingSnDepressurizeParaError { get; internal set; }
    public bool SnDepressurizeFailedError { get; internal set; }

    public bool HasAnyError => SafetySensorTriggered || ChildModuleFault || GateValveNotClosedError ||
                               LidNotClosedError || PumpNotRunningError ||
                               PumpDownTimeoutError || VentTimeoutError ||
                               ActionRejectedError || MissingPumpDownParameterError ||
                               MissingVentParameterError || OperatorTimeoutError || OperatorCancelledError ||
                               MissingPurgeLineParaError || ChamberPressureHighError ||
                               MissingLeakCheckParaError || SnLeakCheckFailedError ||
                               MissingSnDepressurizeParaError || SnDepressurizeFailedError;
}

public static class GasSystemEvents
{
    public static readonly EventBase InfoActionStarted = new() { EventId = 3000, Severity = SeverityLevel.Info, MessageTemplate = "气路系统开始执行：{0}" };
    public static readonly EventBase InfoActionDone = new() { EventId = 3001, Severity = SeverityLevel.Info, MessageTemplate = "气路系统动作完成：{0}" };
    public static readonly EventBase InfoHeatingStarted = new() { EventId = 3002, Severity = SeverityLevel.Info, MessageTemplate = "气路伴热系统启动 (设定温度: {0:F1}℃)" };
    public static readonly EventBase InfoHeatingStopped = new() { EventId = 3003, Severity = SeverityLevel.Info, MessageTemplate = "气路伴热系统停止" };
    public static readonly EventBase InfoResetDone = new() { EventId = 3004, Severity = SeverityLevel.Info, MessageTemplate = "气路系统复位完成" };
    public static readonly EventBase PromptOperatorAction = new() { EventId = 3005, Severity = SeverityLevel.Info, MessageTemplate = "等待人工确认：{0}" };

    public static readonly EventBase ErrChildModuleFault = new() { EventId = 3020, Severity = SeverityLevel.Error, MessageTemplate = "气路底控设备 (MFC/Valve/TC) 发生故障，系统急停！" };
    public static readonly EventBase ErrSafetySensorTriggered = new() { EventId = 3021, Severity = SeverityLevel.Error, MessageTemplate = "特气柜安全传感器 (烟雾/火焰/泄漏) 触发，立刻切断所有气路！" };
    public static readonly EventBase ErrGateValveNotClosed = new() { EventId = 3022, Severity = SeverityLevel.Error, MessageTemplate = "流程({0})执行失败：门阀未完全关闭！" };
    public static readonly EventBase ErrLidNotClosed = new() { EventId = 3023, Severity = SeverityLevel.Error, MessageTemplate = "流程({0})执行失败：腔体门/腔盖未完全关闭！" };
    public static readonly EventBase ErrPumpFault = new() { EventId = 3024, Severity = SeverityLevel.Error, MessageTemplate = "流程({0})执行失败：真空泵未运行或处于报警状态！" };
    public static readonly EventBase ErrPumpDownTimeout = new() { EventId = 3025, Severity = SeverityLevel.Error, MessageTemplate = "抽真空超时：在设定时间 ({0}ms) 内未达到目标真空度 ({1} Pa)！" };
    public static readonly EventBase ErrVentTimeout = new() { EventId = 3026, Severity = SeverityLevel.Error, MessageTemplate = "破空超时：在设定时间 ({0}ms) 内腔压未达到设定破空阈值 ({1} Pa)！" };
    public static readonly EventBase ErrActionRejected = new() { EventId = 3027, Severity = SeverityLevel.Error, MessageTemplate = "气路动作被拒绝：系统当前状态为 {0}，无法执行 {1} ！" };
    public static readonly EventBase ErrMissingPumpDownParameter = new() { EventId = 3028, Severity = SeverityLevel.Error, MessageTemplate = "数据读取失败：缺少必须的掉电保持参数 '{0}'，抽空流程终止" };
    public static readonly EventBase ErrMissingVentParameter = new() { EventId = 3029, Severity = SeverityLevel.Error, MessageTemplate = "数据读取失败：缺少必须的掉电保持参数 '{0}'，破空流程终止" };
    public static readonly EventBase ErrOperatorTimeout = new() { EventId = 3030, Severity = SeverityLevel.Error, MessageTemplate = "流程被中断：操作员响应超时 (60s 内未确认)！" };
    public static readonly EventBase ErrMissingPurgeLinePara = new() { EventId = 3031, Severity = SeverityLevel.Error, MessageTemplate = "Sn管路吹扫失败：缺失吹扫参数配置 (目标压力/循环次数/时间)！" };
    public static readonly EventBase ErrChamberPressureHigh = new() { EventId = 3032, Severity = SeverityLevel.Error, MessageTemplate = "Sn管路吹扫失败：腔体当前压力过高 ( > {0} Pa)，请先执行抽真空！" };
    public static readonly EventBase ErrMissingLeakCheckPara = new() { EventId = 3033, Severity = SeverityLevel.Error, MessageTemplate = "检漏失败：缺失检漏参数配置 (保压时间/压降阈值/目标压力)！" };
    public static readonly EventBase ErrLeakCheckFailed = new() { EventId = 3034, Severity = SeverityLevel.Error, MessageTemplate = "检漏失败：保压测试未通过，压力漏率超标 (当前偏差: {0:F2} Pa)！" };
    public static readonly EventBase ErrMissingDepressurizePara = new() { EventId = 3035, Severity = SeverityLevel.Error, MessageTemplate = "泄压失败：缺失泄压参数配置 (泄压次数/压力阈值)！" };
    public static readonly EventBase ErrDepressurizeFailed = new() { EventId = 3036, Severity = SeverityLevel.Error, MessageTemplate = "泄压失败：泄压流程结束后，管路压力仍高于设定阈值 (当前压力: {0:F2} Pa)！" };
    public static readonly EventBase ErrOperatorCancelled = new() { EventId = 3037, Severity = SeverityLevel.Error, MessageTemplate = "流程被中断：操作员在弹窗中点击了取消！" };
}
