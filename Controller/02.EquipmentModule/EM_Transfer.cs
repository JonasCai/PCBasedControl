using Controller._01.ControlModule;
using Controller.Common;
using Controller.EventLogger;
using Controller.gRPC;
using Controller.Hardware;
using Controller.S88;

namespace Controller._02.EquipmentModule;

public class EM_Transfer : S88EquipmentModuleBase
{
    private readonly TransferCfg _cfg;
    private readonly IRetainDataService _retainData;

    private TransferAction _selectedAction = TransferAction.None;
    private long _actionStartTimestampMs = 0;
    private string _rejectedActionName = string.Empty;

    private double _transferSpeedNormal, _transferSpeedSlow, _transferAccDec;
    private uint _transferTimeoutMs, _sendDelayMs;

    public EM_Transfer(TransferCfg cfg, IEventProducer eventProducer, ILogger<EM_Transfer> logger,
        IONodes iONodes,
        IRetainDataService retainData,
        IServoFactory servoFactory,
        ICheckSensorFactory checkSensorFactory) : base(cfg.Name, eventProducer, logger)
    {
        _cfg = cfg;
        _retainData = retainData;

        RegisterServos(iONodes, servoFactory);
        RegisterCheckSensors(iONodes, checkSensorFactory);
        RegisterCommandHandlers();

        // 初始化加载默认超时与延迟时间
        _transferTimeoutMs = _retainData.GetValue($"{Name}.TransferTimeoutMs", 60000u);
        _sendDelayMs = _retainData.GetValue($"{Name}.SendDelayMs", 2000u);
    }

    public TransferAlarmState AlarmState { get; } = new();

    protected override void OnExecute()
    {
        if (State != EMState.Busy) return;

        switch (_selectedAction)
        {
            case TransferAction.Receive:
                ExecuteReceiveSequence();
                break;
            case TransferAction.Send:
                ExecuteSendSequence();
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
        _selectedAction = TransferAction.None;
    }

    // ==========================================
    // 动作接口
    // ==========================================
    public void StartTransferAction(TransferAction action)
    {
        if (State == EMState.Error || State == EMState.Busy)
        {
            AlarmState.ActionRejectedError = true;
            _rejectedActionName = action.ToString();
            return;
        }

        Step = 0;
        _selectedAction = action;
        _actionStartTimestampMs = CurrentTimestampMs;
        RaiseInfo(TransferEvents.InfoActionStarted, _selectedAction.ToString());
        ChangeState(EMState.Busy);
    }

    // ==========================================
    // 接收流程 (Receive)
    // ==========================================
    private void ExecuteReceiveSequence()
    {
        if (CurrentTimestampMs - _actionStartTimestampMs > _transferTimeoutMs)
        {
            AlarmState.TransferTimeoutError = true;
            return;
        }

        switch (Step)
        {
            case 0:
                // 加载运动参数
                if (_retainData.TryGetValue($"{Name}.TransferSpeedNormal", out _transferSpeedNormal) &&
                    _retainData.TryGetValue($"{Name}.TransferSpeedSlow", out _transferSpeedSlow) &&
                    _retainData.TryGetValue($"{Name}.TransferAccDec", out _transferAccDec))
                {
                    Step = 10;
                }
                else
                {
                    AlarmState.MissingParameterError = true;
                }
                break;

            case 10:
                // 根据当前传感器状态决定起始动作
                if (IsNewStep)
                {
                    TransferAxis1.EnableServo(true);
                    TransferAxis2.EnableServo(true);

                    if (StopPos_Detect.FilteredSignal)
                    {
                        // 初始就压在停止位上 -> 执行【后退至消失】
                        TransferAxis1.MoveVelocity(-Math.Abs(_transferSpeedSlow), _transferAccDec);
                        TransferAxis2.MoveVelocity(-Math.Abs(_transferSpeedSlow), _transferAccDec);
                        Step = 20;
                        return;
                    }
                    else if (SlowDown_Detect.FilteredSignal)
                    {
                        // 场景 B：在减速区 -> 直接慢速前进
                        TransferAxis1.MoveVelocity(Math.Abs(_transferSpeedSlow), _transferAccDec);
                        TransferAxis2.MoveVelocity(Math.Abs(_transferSpeedSlow), _transferAccDec);
                        Step = 60;
                        return;
                    }
                    else
                    {
                        // 场景 C：正常起点 -> 常速前进
                        TransferAxis1.MoveVelocity(Math.Abs(_transferSpeedNormal), _transferAccDec);
                        TransferAxis2.MoveVelocity(Math.Abs(_transferSpeedNormal), _transferAccDec);
                        Step = 50;
                    }
                }
                break;

            case 20:
                // 持续后退，直到 StopPos 信号【消失】
                if (!StopPos_Detect.FilteredSignal)
                {
                    Step = 30;
                }
                break;

            case 30:
                // 刹车停顿：防止电机瞬间反转带来的机械冲击
                if (IsNewStep)
                {
                    TransferAxis1.Stop();
                    TransferAxis2.Stop();
                }

                if (StepTimeout(300)) // 等待 300ms 物理静止
                {
                    Step = 40;
                }
                break;

            case 40:
                // 慢速前进，重新寻找 StopPos 信号
                if (IsNewStep)
                {
                    TransferAxis1.MoveVelocity(Math.Abs(_transferSpeedSlow), _transferAccDec);
                    TransferAxis2.MoveVelocity(Math.Abs(_transferSpeedSlow), _transferAccDec);
                }
                Step = 60;
                break;

            case 50:
                // 在线变速：常速 -> 慢速
                if (SlowDown_Detect.FilteredSignal)
                {
                    TransferAxis1.MoveVelocity(Math.Abs(_transferSpeedSlow), _transferAccDec);
                    TransferAxis2.MoveVelocity(Math.Abs(_transferSpeedSlow), _transferAccDec);
                    Step = 60;
                }
                break;

            case 60:
                // 慢速寻点
                if (StopPos_Detect.FilteredSignal)
                {
                    Step = 70;
                }
                break;

            case 70:
                // 同步停止
                if (IsNewStep)
                {
                    TransferAxis1.Stop();
                    TransferAxis2.Stop();
                }

                if (StepTimeout(500))
                {
                    Step = 80;
                }
                break;

            case 80:
                // 防止载板尺寸异常或滑移
                bool finalPositionOk = SlowDown_Detect.FilteredSignal &&
                                       StopPos_Detect.FilteredSignal &&
                                       Presence_Detect.FilteredSignal;

                if (finalPositionOk)
                {
                    _selectedAction = TransferAction.None;
                    ChangeState(EMState.Idle);
                    RaiseInfo(TransferEvents.InfoActionDone, "Receive");
                }
                else
                {
                    AlarmState.CarrierPositionError = true;
                }
                break;
        }
    }

    // ==========================================
    // 发送流程 (Send)
    // ==========================================
    private void ExecuteSendSequence()
    {
        if (CurrentTimestampMs - _actionStartTimestampMs > _transferTimeoutMs)
        {
            AlarmState.TransferTimeoutError = true;
            return;
        }

        switch (Step)
        {
            case 0:
                // 初始状态检查
                bool anySensorDetects = Inlet_Detect.FilteredSignal || Outlet_Detect.FilteredSignal ||
                                        SlowDown_Detect.FilteredSignal || StopPos_Detect.FilteredSignal ||
                                        Presence_Detect.FilteredSignal;

                if (!anySensorDetects)
                {
                    _selectedAction = TransferAction.None;
                    ChangeState(EMState.Idle);
                    RaiseInfo(TransferEvents.InfoActionDone, "Send (腔内已空，跳过动作)");
                    return;
                }
                Step = 10;
                break;

            case 10:
                if (_retainData.TryGetValue($"{Name}.TransferSpeedNormal", out _transferSpeedNormal) &&
                    _retainData.TryGetValue($"{Name}.TransferAccDec", out _transferAccDec) &&
                    _retainData.TryGetValue($"{Name}.SendDelayMs", out _sendDelayMs))
                {
                    Step = 20;
                }
                else
                {
                    AlarmState.MissingParameterError = true;
                }
                break;

            case 20:
                // 无论载板在哪，一律反向（负向）常速退出
                if (IsNewStep)
                {
                    TransferAxis1.EnableServo(true);
                    TransferAxis2.EnableServo(true);
                    TransferAxis1.MoveVelocity(-Math.Abs(_transferSpeedNormal), _transferAccDec);
                    TransferAxis2.MoveVelocity(-Math.Abs(_transferSpeedNormal), _transferAccDec);
                }

                bool isCompletelyEmpty = !(Inlet_Detect.FilteredSignal || Outlet_Detect.FilteredSignal ||
                                           SlowDown_Detect.FilteredSignal || StopPos_Detect.FilteredSignal ||
                                           Presence_Detect.FilteredSignal);

                if (isCompletelyEmpty)
                {
                    Step = 30; // 彻底脱离腔室
                }
                break;

            case 30:
                // 额外延时设定时间
                if (StepTimeout((long)_sendDelayMs))
                {
                    Step = 40;
                }
                break;

            case 40:
                // 停止双轴
                if (IsNewStep)
                {
                    TransferAxis1.Stop();
                    TransferAxis2.Stop();
                }

                if (StepTimeout(500))
                {
                    _selectedAction = TransferAction.None;
                    ChangeState(EMState.Idle);
                    RaiseInfo(TransferEvents.InfoActionDone, "Send");
                }
                break;
        }
    }

    // ==========================================
    // 报警与复位映射
    // ==========================================
    protected override void AlarmHandler()
    {
        if (HasAnyChildError()) AlarmState.ChildModuleFault = true;

        if (AlarmState.ChildModuleFault) RaiseAlarm(TransferEvents.ErrChildModuleFault);
        else TryClearAlarm(TransferEvents.ErrChildModuleFault);

        if (AlarmState.ActionRejectedError) RaiseAlarm(TransferEvents.ErrActionRejected, State.ToString(), _rejectedActionName);
        else TryClearAlarm(TransferEvents.ErrActionRejected);

        if (AlarmState.TransferTimeoutError) RaiseAlarm(TransferEvents.ErrTransferTimeout, _transferTimeoutMs);
        else TryClearAlarm(TransferEvents.ErrTransferTimeout);

        if (AlarmState.MissingParameterError) RaiseAlarm(TransferEvents.ErrMissingParameter);
        else TryClearAlarm(TransferEvents.ErrMissingParameter);

        if (AlarmState.CarrierPositionError) RaiseAlarm(TransferEvents.ErrCarrierPosition);
        else TryClearAlarm(TransferEvents.ErrCarrierPosition);

        if (!AlarmState.HasAnyError)
        {
            ChangeState(EMState.Idle);
            RaiseInfo(TransferEvents.InfoResetDone);
        }
    }

    protected override void Reset(InternalCommand cmd)
    {
        if (State != EMState.Error) return;

        AlarmState.ChildModuleFault = false;
        AlarmState.ActionRejectedError = false;
        AlarmState.TransferTimeoutError = false;
        AlarmState.MissingParameterError = false;
        AlarmState.CarrierPositionError = false;

        base.Reset(cmd); // 让底层的伺服清除自身的报警
    }

    protected void RegisterCommandHandlers()
    {
        RegisterCommandHandler(Command.Reset, cmd =>
        {
            Reset(cmd);
            cmd.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Accepted, ""));
        });

        RegisterCommandHandler(Command.TransferReceive, cmd =>
        {
            if (State != EMState.Idle)
            {
                cmd.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Rejected, "模块忙碌中"));
                return;
            }
            StartTransferAction(TransferAction.Receive);
            cmd.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Accepted, "开始接收载板"));
        });

        RegisterCommandHandler(Command.TransferSend, cmd =>
        {
            if (State != EMState.Idle)
            {
                cmd.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Rejected, "模块忙碌中"));
                return;
            }
            StartTransferAction(TransferAction.Send);
            cmd.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Accepted, "开始发送载板"));
        });
    }

    #region CheckSensor
    private CM_CheckSensor Inlet_Detect = null!;
    private CM_CheckSensor Outlet_Detect = null!;
    private CM_CheckSensor SlowDown_Detect = null!;
    private CM_CheckSensor StopPos_Detect = null!;
    private CM_CheckSensor Presence_Detect = null!;
    private void RegisterCheckSensors(IONodes iONodes, ICheckSensorFactory checkSensorFactory)
    {
        Inlet_Detect = RegisterCheckSensor("Inlet_Detect", checkSensorFactory, readSignal: () => iONodes.A105[8]);
        Outlet_Detect = RegisterCheckSensor("Outlet_Detect", checkSensorFactory, readSignal: () => iONodes.A106[0]);
        Presence_Detect = RegisterCheckSensor("Presence_Detect", checkSensorFactory, readSignal: () => iONodes.A105[12]);
        SlowDown_Detect = RegisterCheckSensor("SlowDown_Detect", checkSensorFactory, readSignal: () => iONodes.A105[13]);
        StopPos_Detect = RegisterCheckSensor("StopPos_Detect", checkSensorFactory, readSignal: () => iONodes.A105[14]);
    }
    #endregion

    #region Servo
    private CM_Servo TransferAxis1 = null!;
    private CM_Servo TransferAxis2 = null!;
    private void RegisterServos(IONodes iONodes, IServoFactory servoFactory)
    {
        TransferAxis1 = RegisterServo("TransferAxis1", servoFactory, axisId: 2, homeMode: 35, softLimitPos: double.MaxValue, softLimitNeg: double.MinValue, readAxisStatus: axisId => { return new(); }, clearAxisError: axisId => { }, actuateEnable: (axisId, enable) => { }, actuateStop: (axisId, estop) => { }, actuateHome: (axisId, homeMode) => { }, moveAbs: (axisId, dist, vel, taccdec) => { }, moveRel: (axisId, dist, vel, taccdec) => { }, moveVel: (axisId, vel, taccdec) => { }, changeVel: (axisId, vel, taccdec) => { }, setTorque: (axisId, t) => { }, changeTorque: (axisId, t) => { }, canMove: () => true);
        TransferAxis2 = RegisterServo("TransferAxis2", servoFactory, axisId: 3, homeMode: 35, softLimitPos: double.MaxValue, softLimitNeg: double.MinValue, readAxisStatus: axisId => { return new(); }, clearAxisError: axisId => { }, actuateEnable: (axisId, enable) => { }, actuateStop: (axisId, estop) => { }, actuateHome: (axisId, homeMode) => { }, moveAbs: (axisId, dist, vel, taccdec) => { }, moveRel: (axisId, dist, vel, taccdec) => { }, moveVel: (axisId, vel, taccdec) => { }, changeVel: (axisId, vel, taccdec) => { }, setTorque: (axisId, t) => { }, changeTorque: (axisId, t) => { }, canMove: () => true);
    }
    #endregion
}

public class TransferCfg : EquipmentModuleCfg { }

public enum TransferAction { None, Send, Receive }

public sealed class TransferAlarmState
{
    public bool HasAnyWarning => false;

    public bool ChildModuleFault { get; internal set; }
    public bool ActionRejectedError { get; internal set; }
    public bool TransferTimeoutError { get; internal set; }
    public bool MissingParameterError { get; internal set; }
    public bool CarrierPositionError { get; internal set; } 

    public bool HasAnyError => ChildModuleFault || ActionRejectedError ||
                               TransferTimeoutError || MissingParameterError || CarrierPositionError; 
}

public static class TransferEvents
{
    public static readonly EventBase InfoActionStarted = new() { EventId = 4000, Severity = SeverityLevel.Info, MessageTemplate = "传送系统开始执行动作：{0}" };
    public static readonly EventBase InfoActionDone = new() { EventId = 4001, Severity = SeverityLevel.Info, MessageTemplate = "传送系统动作完成：{0}" };
    public static readonly EventBase InfoResetDone = new() { EventId = 4002, Severity = SeverityLevel.Info, MessageTemplate = "传送系统复位成功" };

    public static readonly EventBase ErrChildModuleFault = new() { EventId = 4020, Severity = SeverityLevel.Error, MessageTemplate = "底层模块 (伺服/传感器) 发生致命故障，传送系统急停！" };
    public static readonly EventBase ErrActionRejected = new() { EventId = 4021, Severity = SeverityLevel.Error, MessageTemplate = "动作被拒绝：当前状态为 {0}，无法执行 {1}" };
    public static readonly EventBase ErrMissingParameter = new() { EventId = 4022, Severity = SeverityLevel.Error, MessageTemplate = "传送失败：读取不到必需的速度/加速度/延时等设定参数！" };
    public static readonly EventBase ErrTransferTimeout = new() { EventId = 4023, Severity = SeverityLevel.Error, MessageTemplate = "传送超时：操作耗时超过全局超时限制 ({0}ms)！" };
    public static readonly EventBase ErrCarrierPosition = new() { EventId = 4024, Severity = SeverityLevel.Error, MessageTemplate = "载板接收异常：停止后，基准传感器未能全部检测到载板，载板可能滑移或尺寸错误！" };
}