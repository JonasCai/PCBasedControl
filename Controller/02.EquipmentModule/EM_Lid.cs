using Controller._01.ControlModule;
using Controller.Common;
using Controller.EventLogger;
using Controller.gRPC;
using Controller.Hardware;
using Controller.S88;

namespace Controller._02.EquipmentModule;

public class EM_Lid : S88EquipmentModuleBase
{
    private readonly LidCfg _cfg;
    private readonly IRetainDataService _retainData;
    private bool _lastBtnPressed, btnPressed;
    private float _chamberPressure = 0f;
    private LidAction _selectedAction = LidAction.None;

    public EM_Lid(LidCfg cfg, ILogger<S88EquipmentModuleBase> logger,
        IONodes iONodes,
        IRetainDataService retainData,
        IEventProducer eventProducer,
        IServoFactory servoFactory,
        ICylinderFactory cylinderFactory,
        ICheckSensorFactory checkSensorFactory) : base(cfg.Name, logger, eventProducer)
    {
        _cfg = cfg;
        _retainData = retainData;
        RegisterServos(iONodes, servoFactory);
        RegisterCylinders(iONodes, cylinderFactory);
        RegisterCheckSensors(iONodes, checkSensorFactory);
        RegisterCommandHandlers();
    }

    public LidStatus CurrentStatus
    {
        get
        {
            // 设备处于报警状态
            if (State == EMState.Error)
                return LidStatus.Faulted;

            // 运动中状态
            if (State == EMState.Busy)
            {
                if (_selectedAction == LidAction.Open) return LidStatus.Opening;
                if (_selectedAction == LidAction.Close) return LidStatus.Closing;
            }

            // 空闲态下的物理位置判定
            if (Closed1_Detect.FilteredSignal && Closed2_Detect.FilteredSignal)
                return LidStatus.Closed;

            if (Open1_Detect.FilteredSignal && Open2_Detect.FilteredSignal)
                return LidStatus.Opened;

            // 既不报错，也没在运动，两头传感器也都没亮
            return LidStatus.Unknown;
        }
    }

    public LidAlarmState AlarmState { get; } = new();

    protected override void OnExecute()
    {
        btnPressed = _cfg.IsOperationBtnPressed();

        // 全局监控开盖后的安全插销
        MonitorPinInsertion();

        // 在 HMI 已选择动作的情况下，按下物理按钮开始流程
        if (State == EMState.Idle && _selectedAction != LidAction.None && btnPressed && !_lastBtnPressed)
        {
            Step = 0;
            RaiseInfo(LidEvents.InfoActionStarted, _selectedAction.ToString());
            ChangeState(EMState.Busy);
        }

        // 松开按钮立即停止并报警！
        if (State == EMState.Busy && !btnPressed)
        {
            AlarmState.ButtonReleasedError = true;
        }

        // 底层 CM 发生 Error 必须拉停 EM
        if (HasAnyChildError())
        {
            AlarmState.ChildModuleFault = true;
        }

        _lastBtnPressed = btnPressed;

        // 执行灯光刷新
        UpdateButtonLight();

        if (State != EMState.Busy) return;

        switch (_selectedAction)
        {
            case LidAction.Open:
                ExecuteOpenSequence();
                break;
            case LidAction.Close:
                ExecuteCloseSequence();
                break;
            default:
                Step = 0;
                return;
        }
    }

    protected override void OnAbort()
    {
        // 伺服立刻急停
        LidAxis.Stop(emergency: true);

        // 停止所有传感器的超时监控，防止急停后级联报警刷屏
        Closed1_Detect.DisableMonitoring();
        Closed2_Detect.DisableMonitoring();
        Open1_Detect.DisableMonitoring();
        Open2_Detect.DisableMonitoring();
        Locked1_Detect.DisableMonitoring();
        Locked2_Detect.DisableMonitoring();
        Unlocked1_Detect.DisableMonitoring();
        Unlocked2_Detect.DisableMonitoring();

        // 重置动作选择，强制要求 HMI 重新下发
        _selectedAction = LidAction.None;

        // 发生异常时立刻熄灭指示灯
        _cfg.SetBtnLight(false);
    }

    protected override void Reset(InternalCommand cmd)
    {
        AlarmState.ButtonReleasedError = false;
        AlarmState.NotAtAtmosphereError = false;
        AlarmState.MissingOpenParameterError = false;
        AlarmState.MissingCloseParameterError = false;
        AlarmState.PinNotRetractedError = false;

        base.Reset(cmd);
    }

    protected override void AlarmHandler()
    {
        // 评估底层状态
        AlarmState.ChildModuleFault = HasAnyChildError();

        if (AlarmState.ButtonReleasedError) RaiseAlarm(LidEvents.ErrButtonReleased);
        else TryClearAlarm(LidEvents.ErrButtonReleased);

        if (AlarmState.ChildModuleFault) RaiseAlarm(LidEvents.ErrChildModuleFault);
        else TryClearAlarm(LidEvents.ErrChildModuleFault);

        if (AlarmState.MissingOpenParameterError) RaiseAlarm(LidEvents.ErrMissingOpenParameter, "EM_Lid.OpenPos 或 EM_Lid.OpenVel 或 EM_Lid.Taccdec");
        else TryClearAlarm(LidEvents.ErrMissingOpenParameter);

        if (AlarmState.MissingCloseParameterError) RaiseAlarm(LidEvents.ErrMissingCloseParameter, "EM_Lid.ClosePos 或 EM_Lid.CloseVel 或 EM_Lid.Taccdec");
        else TryClearAlarm(LidEvents.ErrMissingCloseParameter);

        if (AlarmState.NotAtAtmosphereError) RaiseAlarm(LidEvents.ErrNotAtAtmosphere, _chamberPressure);
        else TryClearAlarm(LidEvents.ErrNotAtAtmosphere);

        if (AlarmState.PinNotRetractedError) RaiseAlarm(LidEvents.ErrPinNotRetracted);
        else TryClearAlarm(LidEvents.ErrPinNotRetracted);

        // 所有的 AlarmState 都为 false，EM 会自动解开 Error 态回归 Idle！
        if (State == EMState.Error && !AlarmState.HasAnyError)
        {
            ChangeState(EMState.Idle);
        }
    }

    // 按钮指示灯控制 (Flash/Solid/Off)
    private void UpdateButtonLight()
    {
        if (State == EMState.Error)
        {
            _cfg.SetBtnLight(false); // 发生故障，灯熄灭
        }
        else if (State == EMState.Busy)
        {
            // 动作过程中按钮闪烁 (周期 500ms)
            bool flashOn = (CurrentTimestampMs / 500) % 2 == 0;
            _cfg.SetBtnLight(flashOn);
        }
        else if (State == EMState.Idle)
        {
            _cfg.SetBtnLight(true); // 动作完成 (待机空闲状态)，灯常亮
        }
    }

    private void ExecuteOpenSequence()
    {
        switch (Step)
        {
            case 0: // 1. 检查腔体是否处于常压
                _chamberPressure = _cfg.ChamberPressure();
                if (_chamberPressure < 95f)
                {
                    AlarmState.NotAtAtmosphereError = true;
                    break;
                }

                if (_retainData.TryGetValue("EM_Lid.OpenPos", out double openPos) &&
                    _retainData.TryGetValue("EM_Lid.OpenVel", out double openVel) &&
                    _retainData.TryGetValue("EM_Lid.Taccdec", out double taccdec))
                {
                    LidAxis.MoveAbs(openPos, openVel, taccdec);
                    Step = 10;
                }
                else
                {
                    AlarmState.MissingOpenParameterError = true;
                }
                break;

            case 10: // 2. 等待伺服运行至开门位
                if (LidAxis.State == ServoState.Standby)
                {
                    Open1_Detect.SetExpectedState(ExpectedSignalState.ShouldBeOn, 2000, SeverityLevel.Error);
                    Open2_Detect.SetExpectedState(ExpectedSignalState.ShouldBeOn, 2000, SeverityLevel.Error);
                    Step = 20;
                }
                break;

            case 20: // 3. 开门传感器闭合
                if (Open1_Detect.FilteredSignal && Open2_Detect.FilteredSignal)
                {
                    Open1_Detect.DisableMonitoring();
                    Open2_Detect.DisableMonitoring();
                    LidClamper.MoveExtend();
                    Step = 30;
                }
                break;

            case 30: // 4. 等待气缸动作完成
                if (LidClamper.State == CylinderState.Extended)
                {
                    Locked1_Detect.SetExpectedState(ExpectedSignalState.ShouldBeOn, 2000, SeverityLevel.Error);
                    Locked2_Detect.SetExpectedState(ExpectedSignalState.ShouldBeOn, 2000, SeverityLevel.Error);
                    Step = 40;
                }
                break;

            case 40: // 5. 锁紧传感器闭合
                if (Locked1_Detect.FilteredSignal && Locked2_Detect.FilteredSignal)
                {
                    Locked1_Detect.DisableMonitoring();
                    Locked2_Detect.DisableMonitoring();
                    Step = 50;
                }
                break;

            case 50: // 6. 结束动作，交出控制权 (插销监控将由后台自动接管)
                RaiseInfo(LidEvents.InfoOpenDone);
                _selectedAction = LidAction.None;
                ChangeState(EMState.Idle);
                break;
        }
    }

    private void ExecuteCloseSequence()
    {
        switch (Step)
        {
            case 0:
                // 关闭后台监控插入的报警器，防止冲突
                Pin1Inserted_Detect.DisableMonitoring();
                Pin2Inserted_Detect.DisableMonitoring();

                // 立刻校验插销是否拔出，状态不对直接报警！
                if (Pin1Inserted_Detect.FilteredSignal || Pin2Inserted_Detect.FilteredSignal ||
                    !Pin1Retracted_Detect.FilteredSignal || !Pin2Retracted_Detect.FilteredSignal)
                {
                    AlarmState.PinNotRetractedError = true;
                    break;
                }
                Step = 10;
                break;

            case 10: // 2. 锁紧气缸解除锁定
                LidClamper.MoveRetract();
                Step = 20;
                break;

            case 20: // 等待气缸缩回完成
                if (LidClamper.State == CylinderState.Retracted)
                {
                    Unlocked1_Detect.SetExpectedState(ExpectedSignalState.ShouldBeOn, 2000, SeverityLevel.Error);
                    Unlocked2_Detect.SetExpectedState(ExpectedSignalState.ShouldBeOn, 2000, SeverityLevel.Error);
                    Step = 30;
                }
                break;

            case 30: // 等待解锁传感器闭合
                if (Unlocked1_Detect.FilteredSignal && Unlocked2_Detect.FilteredSignal)
                {
                    Unlocked1_Detect.DisableMonitoring();
                    Unlocked2_Detect.DisableMonitoring();

                    if (_retainData.TryGetValue("EM_Lid.ClosePos", out double closePos) &&
                        _retainData.TryGetValue("EM_Lid.CloseVel", out double closeVel) &&
                        _retainData.TryGetValue("EM_Lid.Taccdec", out double taccdec))
                    {
                        LidAxis.MoveAbs(closePos, closeVel, taccdec);
                        Step = 40;
                    }
                    else
                    {
                        AlarmState.MissingCloseParameterError = true;
                    }
                }
                break;

            case 40: // 等待伺服运行至关门位
                if (LidAxis.State == ServoState.Standby)
                {
                    Closed1_Detect.SetExpectedState(ExpectedSignalState.ShouldBeOn, 2000, SeverityLevel.Error);
                    Closed2_Detect.SetExpectedState(ExpectedSignalState.ShouldBeOn, 2000, SeverityLevel.Error);
                    Step = 50;
                }
                break;

            case 50: // 等待关门传感器闭合
                if (Closed1_Detect.FilteredSignal && Closed2_Detect.FilteredSignal)
                {
                    Closed1_Detect.DisableMonitoring();
                    Closed2_Detect.DisableMonitoring();
                    RaiseInfo(LidEvents.InfoCloseDone);
                    _selectedAction = LidAction.None;
                    ChangeState(EMState.Idle);
                }
                break;
        }
    }

    // 全局插销监控
    private void MonitorPinInsertion()
    {
        // 判定条件：腔盖在物理上处于开启且锁紧的状态
        bool isLidPhysicallyOpen = Open1_Detect.FilteredSignal && Open2_Detect.FilteredSignal &&
                                   LidClamper.State == CylinderState.Extended;

        // 如果机器空闲，且盖子开着
        if (State == EMState.Idle && isLidPhysicallyOpen)
        {
            if (Pin1Inserted_Detect.State == CheckSensorState.Disabled)
            {
                Pin1Inserted_Detect.SetExpectedState(ExpectedSignalState.ShouldBeOn, 60000, SeverityLevel.Error);
                Pin2Inserted_Detect.SetExpectedState(ExpectedSignalState.ShouldBeOn, 60000, SeverityLevel.Error);
                RaiseInfo(LidEvents.InfoWaitingPinInsert);
            }
        }
    }

    // HMI 外部指令交互
    protected void RegisterCommandHandlers()
    {
        RegisterCommandHandler(Command.SetLidAction, cmd =>
        {
            if (State == EMState.Busy)
            {
                cmd.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Rejected, "腔盖动作中，不允许切换动作"));
                return;
            }

            if (cmd.Params.TryGetValue("Action", out string? actStr) && Enum.TryParse(actStr, out LidAction action))
            {
                _selectedAction = action;
                cmd.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Accepted, $"已选择动作: {action}，请按住物理按钮开始执行"));
            }
            else
            {
                cmd.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Rejected, "无效的 Action 参数"));
            }
        });

        RegisterCommandHandler(Command.Reset, cmd =>
        {
            Reset(cmd);
            cmd.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Accepted, ""));
        });
    }

    #region CheckSensor
    private CM_CheckSensor Closed1_Detect = null!;
    private CM_CheckSensor Closed2_Detect = null!;
    private CM_CheckSensor Open1_Detect = null!;//NC
    private CM_CheckSensor Open2_Detect = null!;//NC
    private CM_CheckSensor Locked1_Detect = null!;//气缸锁
    private CM_CheckSensor Unlocked1_Detect = null!;
    private CM_CheckSensor Locked2_Detect = null!;
    private CM_CheckSensor Unlocked2_Detect = null!;
    private CM_CheckSensor Pin1Inserted_Detect = null!;//插销
    private CM_CheckSensor Pin1Retracted_Detect = null!;
    private CM_CheckSensor Pin2Inserted_Detect = null!;
    private CM_CheckSensor Pin2Retracted_Detect = null!;
    private CM_CheckSensor RegisterCheckSensor(
        string name,
        ICheckSensorFactory factory,
        Func<bool> readSignal,
        long debounceTimeMs = 100,
        long defaultTimeoutMs = 2000,
        bool autoStart = false,
        ExpectedSignalState defaultExpectedSignalState = ExpectedSignalState.Ignore)
    {
        var cfg = new CheckSensorCfg
        {
            Name = name,
            ReadRawSignal = readSignal,
            DebounceTimeMs = debounceTimeMs,
            DefaultExpectedState = defaultExpectedSignalState,
            DefaultMismatchTimeoutMs = defaultTimeoutMs,
            AutoStartMonitoring = autoStart
        };
        var sensor = factory.Create(cfg);
        RegisterCm(sensor);
        return sensor;
    }

    private void RegisterCheckSensors(IONodes iONodes, ICheckSensorFactory checkSensorFactory)
    {
        Closed1_Detect = RegisterCheckSensor("Closed1_Detect", checkSensorFactory,
            readSignal: () => iONodes.A201[13]);

        Closed2_Detect = RegisterCheckSensor("Closed2_Detect", checkSensorFactory,
            readSignal: () => iONodes.A201[15]);

        Open1_Detect = RegisterCheckSensor("Open1_Detect", checkSensorFactory,
            readSignal: () => iONodes.A201[12]);

        Open2_Detect = RegisterCheckSensor("Open2_Detect", checkSensorFactory,
            readSignal: () => iONodes.A201[14]);

        Locked1_Detect = RegisterCheckSensor("Locked1_Detect", checkSensorFactory,
            readSignal: () => iONodes.A202[0]);

        Unlocked1_Detect = RegisterCheckSensor("Unlocked1_Detect", checkSensorFactory,
            readSignal: () => iONodes.A202[1]);

        Locked2_Detect = RegisterCheckSensor("Locked2_Detect", checkSensorFactory,
            readSignal: () => iONodes.A202[2]);

        Unlocked2_Detect = RegisterCheckSensor("Unlocked2_Detect", checkSensorFactory,
            readSignal: () => iONodes.A202[3]);

        Pin1Retracted_Detect = RegisterCheckSensor("Pin1Retracted_Detect", checkSensorFactory,
            readSignal: () => iONodes.A202[4]);

        Pin1Inserted_Detect = RegisterCheckSensor("Pin1Inserted_Detect", checkSensorFactory,
            readSignal: () => iONodes.A202[5]);

        Pin2Retracted_Detect = RegisterCheckSensor("Pin2Retracted_Detect", checkSensorFactory,
            readSignal: () => iONodes.A202[6]);

        Pin2Inserted_Detect = RegisterCheckSensor("Pin2Inserted_Detect", checkSensorFactory,
            readSignal: () => iONodes.A202[7]);
    }
    #endregion

    #region Servo
    private CM_Servo LidAxis = null!;
    private CM_Servo RegisterServo(
        string name,
        IServoFactory factory,
        ushort axisId, ushort homeMode,
        double softLimitPos, double softLimitNeg,
        Func<ushort, AxisStatus> readAxisStatus,
        Action<ushort> clearAxisError,
        Action<ushort, bool> actuateEnable,
        Action<ushort, bool> actuateStop,
        Action<ushort, ushort> actuateHome,
        Action<ushort, double, double, double> moveAbs,
        Action<ushort, double, double, double> moveRel,
        Action<ushort, double, double> moveVel,
        Action<ushort, double, double> changeVel,
        Action<ushort, double> setTorque,
        Action<ushort, double> changeTorque,
        Func<bool>? canMove = null)
    {
        var cfg = new ServoCfg
        {
            Name = name,
            AxisId = axisId,
            HomeMode = homeMode,
            SoftLimitPositive = softLimitPos,
            SoftLimitNegative = softLimitNeg,
            ReadAxisStatus = readAxisStatus,
            ClearAxisError = clearAxisError,
            ActuateEnable = actuateEnable,
            ActuateStop = actuateStop,
            ActuateHome = actuateHome,
            ActuateMoveAbs = moveAbs,
            ActuateMoveRel = moveRel,
            ActuateVelocity = moveVel,
            ChangeVelocity = changeVel,
            ActuateTorque = setTorque,
            ChangeTorque = changeTorque,
            CanMove = canMove ?? (() => true)
        };

        var servo = factory.Create(cfg);
        RegisterCm(servo);
        return servo;
    }

    private void RegisterServos(IONodes iONodes, IServoFactory servoFactory)
    {
        LidAxis = RegisterServo("LidAxis", servoFactory,
            axisId: 1, homeMode: 1,
            softLimitPos: 1000d, softLimitNeg: -2d,
            readAxisStatus: axisId =>
            {
                int actTorq = 0;
                ushort homeState = 0, stateMachine = 0, errorCode = 0;
                double actPos = 0, actVel = 0;
                LTDMC.dmc_get_home_result(0, axisId, ref homeState);
                LTDMC.nmc_get_axis_state_machine(0, axisId, ref stateMachine);
                LTDMC.nmc_get_axis_errcode(0, axisId, ref errorCode);
                LTDMC.dmc_get_position_unit(0, axisId, ref actPos);
                LTDMC.dmc_read_current_speed_unit(0, axisId, ref actVel);
                LTDMC.nmc_get_torque(0, axisId, ref actTorq);
                return new()
                {
                    Alarm = errorCode != 0,
                    ServoOn = stateMachine == 4,
                    Homed = homeState == 1,
                    Moving = LTDMC.dmc_check_done(0, axisId) == 0,
                    ActPos = actPos,
                    ActVel = actVel,
                    ActTrq = actTorq
                };
            },
            clearAxisError: axisId => LTDMC.nmc_clear_axis_errcode(0, axisId),
            actuateEnable: (axisId, enable) =>
            {
                if (enable)
                    LTDMC.nmc_set_axis_enable(0, axisId);
                else
                    LTDMC.nmc_set_axis_disable(0, axisId);
            },
            actuateStop: (axisId, estop) => LTDMC.dmc_stop(0, axisId, estop ? (ushort)1 : (ushort)0),
            actuateHome: (axisId, homeMode) =>
            {
                LTDMC.nmc_set_home_profile(0, axisId, homeMode, 10, 20, 1.0, 1.0, 0);
                LTDMC.nmc_home_move(0, axisId);
            },
            moveAbs: (axisId, dist, vel, taccdec) =>
            {
                LTDMC.dmc_set_profile_unit(0, axisId, 0, vel, taccdec, taccdec, 2000); //设置单轴运动速度曲线
                LTDMC.dmc_pmove_unit(0, axisId, dist, 1);
            },
            moveRel: (axisId, dist, vel, taccdec) =>
            {
                LTDMC.dmc_set_profile_unit(0, axisId, 0, vel, taccdec, taccdec, 2000); //设置单轴运动速度曲线
                LTDMC.dmc_pmove_unit(0, axisId, dist, 0);
            },
            moveVel: (axisId, vel, taccdec) =>
            {
                LTDMC.dmc_set_profile_unit(0, axisId, 0, Math.Abs(vel), taccdec, taccdec, 2000); //设置单轴运动速度曲线
                LTDMC.dmc_set_s_profile(0, axisId, 0, 0.05);//速度曲线为s 形
                LTDMC.dmc_vmove(0, axisId, vel < 0 ? (ushort)0 : (ushort)1); //执行连续运动
            },
            changeVel: (axisId, vel, taccdec) =>
            {
                LTDMC.dmc_change_speed_unit(0, axisId, vel, taccdec);
            },
            setTorque: (axisId, t) => LTDMC.nmc_torque_move(0, axisId, (int)(t * 10), 0, 50000, 1),
            changeTorque: (axisId, t) => LTDMC.nmc_change_torque(0, axisId, (int)(t * 10)),
            canMove: () => true);
    }
    #endregion

    #region Cylinder
    private CM_Cylinder LidClamper = null!;

    private CM_Cylinder RegisterCylinder(
        string name,
        ICylinderFactory factory,
        Action<CylinderCmd> actuate,
        CylinderSensorConfig sensorConfig = CylinderSensorConfig.DualSensors, // 虚拟传感器配置
        Func<bool>? readExtSensor = null,
        Func<bool>? readRetSensor = null,
        int virtualSensorDelayMs = 2000, // 虚拟传感器推算时间
        Func<bool>? canExtend = null,
        Func<bool>? canRetract = null)
    {
        var cfg = new CylinderCfg
        {
            Name = name,
            SensorConfig = sensorConfig,
            VirtualExtendDelayMs = virtualSensorDelayMs,
            Actuate = actuate,
            ReadExtendedSensor = readExtSensor,
            ReadRetractedSensor = readRetSensor,
            CanExtend = canExtend ?? (() => true),
            CanRetract = canRetract ?? (() => true)
        };
        var cylinder = factory.Create(cfg);
        RegisterCm(cylinder);
        return cylinder;
    }

    private void RegisterCylinders(IONodes iONodes, ICylinderFactory cylinderFactory)
    {
        LidClamper = RegisterCylinder("LidClamper", cylinderFactory,
            actuate: cmd =>
            {
                switch (cmd)
                {
                    case (CylinderCmd.Extend):
                        iONodes.A110[10] = true;
                        iONodes.A110[11] = false;
                        break;

                    case (CylinderCmd.Retract):
                        iONodes.A110[10] = false;
                        iONodes.A110[11] = true;
                        break;

                    case (CylinderCmd.ToSafe):
                        //Do nothing
                        break;
                }
            },
            readExtSensor: () => iONodes.A102[0] && iONodes.A102[2],
            readRetSensor: () => iONodes.A102[1] && iONodes.A102[3],
            canExtend: () => true,
            canRetract: () => true);
    }
    #endregion
}

public enum LidStatus
{
    Unknown,    // 未知/中间位置 
    Closed,     // 已完全关闭
    Opened,     // 已完全打开
    Opening,    // 正在打开...
    Closing,    // 正在关闭...
    Faulted     // 故障停机 
}

public sealed class LidAlarmState
{
    public bool ButtonReleasedError { get; internal set; }
    public bool NotAtAtmosphereError { get; internal set; }
    public bool ChildModuleFault { get; internal set; }
    public bool MissingOpenParameterError { get; internal set; }
    public bool MissingCloseParameterError { get; internal set; }
    public bool PinNotRetractedError { get; internal set; }
    public bool HasAnyError => ButtonReleasedError || NotAtAtmosphereError || ChildModuleFault ||
                               MissingOpenParameterError || MissingCloseParameterError || PinNotRetractedError;
}

public class LidCfg : EquipmentModuleCfg
{
    public required Func<bool> IsOperationBtnPressed;
    public required Func<float> ChamberPressure;
    public required Action<bool> SetBtnLight; 
}

public enum LidAction { None, Open, Close }

public static class LidEvents
{
    public static readonly EventBase InfoActionStarted = new()
    { EventId = 2000, Severity = SeverityLevel.Info, MessageTemplate = "操作按钮已按下，开始执行 {0} 流程" };

    public static readonly EventBase InfoWaitingPinInsert = new()
    { EventId = 2001, Severity = SeverityLevel.Info, MessageTemplate = "腔盖开启完成，后台接管安全监控，请在 60s 内插入安全插销！" };

    public static readonly EventBase InfoOpenDone = new()
    { EventId = 2002, Severity = SeverityLevel.Info, MessageTemplate = "腔盖开启并安全锁定完成" };

    public static readonly EventBase InfoCloseDone = new()
    { EventId = 2004, Severity = SeverityLevel.Info, MessageTemplate = "腔盖关闭完成" };

    public static readonly EventBase ErrButtonReleased = new()
    { EventId = 2020, Severity = SeverityLevel.Error, MessageTemplate = "安全防呆触发：危险动作中松开了操作按钮，伺服已紧急制动！" };

    public static readonly EventBase ErrNotAtAtmosphere = new()
    { EventId = 2021, Severity = SeverityLevel.Error, MessageTemplate = "安全保护触发：腔室未处于常压状态，禁止开启！(当前腔室压力 = {0:F2} kPa)" };

    public static readonly EventBase ErrChildModuleFault = new()
    { EventId = 2022, Severity = SeverityLevel.Error, MessageTemplate = "动作中断：底层控制模块 (气缸/伺服/传感器) 发生报警！" };

    public static readonly EventBase ErrMissingOpenParameter = new()
    { EventId = 2023, Severity = SeverityLevel.Error, MessageTemplate = "数据读取失败：缺少必须的掉电保持参数 '{0}'，流程终止" };

    public static readonly EventBase ErrMissingCloseParameter = new()
    { EventId = 2024, Severity = SeverityLevel.Error, MessageTemplate = "数据读取失败：缺少必须的掉电保持参数 '{0}'，流程终止" };

    public static readonly EventBase ErrPinNotRetracted = new()
    { EventId = 2025, Severity = SeverityLevel.Error, MessageTemplate = "防撞车保护：安全插销未完全拔出，拒绝执行关盖动作！" };
}
