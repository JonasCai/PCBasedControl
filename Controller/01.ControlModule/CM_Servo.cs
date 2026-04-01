using Controller.EventLogger;
using Controller.gRPC;
using Controller.S88;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Controller._01.ControlModule;

public class CM_Servo : IControlModule
{
    public CM_Servo(IEventProducer eventProducer, ServoCfg cfg, ILogger<CM_Servo> logger)
    {
        _eventProducer = eventProducer;
        _cfg = cfg;
        _logger = logger;
        RegisterCommandHandlers();

        if (!_cfg.Validate())
            throw new ArgumentException($"CM_Servo[{_cfg.Name}]配置不完整", nameof(_cfg));
    }

    // ==========================================
    // IControlModule 接口方法
    // ==========================================
    public bool HasAnyWarning => AlarmState.HasAnyWarning;
    public bool HasAnyError => State == ServoState.Error;
    public string Name => _cfg.Name;

    public void Refresh(long currentTimestampMs)
    {
        _currentTimestampMs = currentTimestampMs;

        // 1. 读取底层硬件状态 (雷赛 DMC_check_done / DMC_get_position 等)
        _actualPosition = _cfg.ReadActualPosition();
        _actualVelocity = _cfg.ReadActualVelocity();
        _isServoOn = _cfg.ReadServoStatus();
        _isMotionDone = _cfg.CheckMotionDone(); // 雷赛轴是否停止脉冲输出

        var ioStatus = _cfg.ReadAxisIoStatus(); // 读取 ALM, PEL, MEL 等硬件 IO

        // 2. 处理排队的外部指令
        ProcessCommandQueue();

        // 3. 评估报警及硬/软限位联锁
        EvaluateAlarms(ioStatus);

        // 4. 故障态立即切断运动，并跳过后续状态机
        if (State == ServoState.Error)
        {
            // 注意：根据工艺需求，发生 Error 时是否要下掉 Enable(断伺服电) 取决于配置。
            // 但无论如何，必须立刻发送急停指令停止发脉冲！
            if (!_isStopCommandSent)
            {
                _cfg.ActuateStop(true);
                _isStopCommandSent = true;
            }
            return;
        }

        // 5. 核心状态机逻辑
        switch (State)
        {
            case ServoState.Disabled:
                if (_isServoOn) ChangeState(ServoState.Standby);
                break;

            case ServoState.Standby:
                if (!_isServoOn) ChangeState(ServoState.Disabled);
                break;

            case ServoState.Homing:
                if (_isMotionDone)
                {
                    // 雷赛回零完成的标志
                    ChangeState(ServoState.Standby);
                    _eventProducer.SendInfo(_cfg.Name, ServoEvents.InfoHomeDone);
                }
                break;

            case ServoState.MovingAbs:
            case ServoState.MovingRel:
                if (_isMotionDone)
                {
                    // 到位后，根据是否配置了 INP (In-Position) 信号来决定是否精准到位
                    if (_cfg.RequireInpSignal && !ioStatus.INP)
                    {
                        // 脉冲发完了，但电机编码器还没追上，等待...
                        // (可在此处扩展到位超时报警逻辑)
                        break;
                    }
                    ChangeState(ServoState.Standby);
                    _eventProducer.SendInfo(_cfg.Name, ServoEvents.InfoMoveDone, _actualPosition);
                }
                break;

            case ServoState.Jogging:
            case ServoState.VelocityMode:
            case ServoState.TorqueMode:
                // 连续运动模式下，由外部主动发 Stop 指令来结束
                // 如果底层的驱动器因为某种原因自己停了（_isMotionDone == true），说明被硬限位截停或发生异常
                if (_isMotionDone && State != ServoState.TorqueMode)
                {
                    ChangeState(ServoState.Standby);
                }
                break;
        }
    }

    public void ToSafe()
    {
        PurgeCommands();
        Stop(emergency: true);
        // 安全态下，是否需要切断伺服使能（_cfg.ActuateEnable(false)），可根据实际机械评估
    }

    public void ExecuteCommand(InternalCommand command) => _commandQueue.Enqueue(command);

    // ==========================================
    // 外部运动控制接口
    // ==========================================
    public void EnableServo(bool enable)
    {
        if (State == ServoState.Error) return;
        _cfg.ActuateEnable(enable);
    }

    public void Stop(bool emergency = false)
    {
        if (State == ServoState.Error || State == ServoState.Disabled || State == ServoState.Standby) return;

        _cfg.ActuateStop(emergency);
        _isStopCommandSent = true; // 标记已发停止，防止重复发

        // 状态机将在下一帧检测到 _isMotionDone 为 true 后切回 Standby
    }

    public void Home()
    {
        if (!CheckBeforeMove()) return;

        _isStopCommandSent = false;
        _cfg.ActuateHome();
        ChangeState(ServoState.Homing);
        _eventProducer.SendInfo(_cfg.Name, ServoEvents.InfoHomingStarted);
    }

    public void MoveAbs(double targetPos, double speed)
    {
        if (!CheckBeforeMove(targetPos)) return;

        _isStopCommandSent = false;
        _cfg.ActuateMoveAbs(targetPos, speed);
        ChangeState(ServoState.MovingAbs);
        _eventProducer.SendInfo(_cfg.Name, ServoEvents.InfoMoveAbsStarted, targetPos, speed);
    }

    public void MoveRel(double distance, double speed)
    {
        double expectedTarget = _actualPosition + distance;
        if (!CheckBeforeMove(expectedTarget)) return;

        _isStopCommandSent = false;
        _cfg.ActuateMoveRel(distance, speed);
        ChangeState(ServoState.MovingRel);
        _eventProducer.SendInfo(_cfg.Name, ServoEvents.InfoMoveRelStarted, distance, speed);
    }

    public void Jog(bool positiveDir, double speed)
    {
        if (!CheckBeforeMove()) return;

        _isStopCommandSent = false;
        _cfg.ActuateJog(positiveDir, speed);
        ChangeState(ServoState.Jogging);
    }

    public void MoveVelocity(double speed)
    {
        if (!CheckBeforeMove()) return;

        _isStopCommandSent = false;
        _cfg.ActuateVelocity(speed);
        ChangeState(ServoState.VelocityMode);
    }

    public void SetTorque(double torquePercent)
    {
        if (State == ServoState.Error || !_isServoOn) return;

        _isStopCommandSent = false;
        _cfg.ActuateTorque(torquePercent);
        ChangeState(ServoState.TorqueMode);
    }

    // ==========================================
    // 状态读取属性
    // ==========================================
    public ServoState State { get; private set; } = ServoState.Disabled;
    public ServoAlarmState AlarmState { get; } = new();
    public double ActualPosition => _actualPosition;
    public double ActualVelocity => _actualVelocity;

    public ServoSnapshot GetSnapshot() => new()
    {
        Name = _cfg.Name,
        State = State,
        AlarmState = AlarmState,
        ActualPosition = _actualPosition,
        ActualVelocity = _actualVelocity,
        IsServoOn = _isServoOn
    };

    // ==========================================
    // 私有成员与报警逻辑
    // ==========================================
    private readonly ILogger<CM_Servo> _logger;
    private readonly ServoCfg _cfg;
    private readonly IEventProducer _eventProducer;
    private readonly Dictionary<int, (Guid guid, EventBase eventBase, object[] args)> _activeAlarms = new();
    private readonly ConcurrentQueue<InternalCommand> _commandQueue = new();
    private readonly Dictionary<Command, Action<InternalCommand>> _commandHandlers = new();

    private long _currentTimestampMs;
    private double _actualPosition, _actualVelocity;
    private bool _isServoOn, _isMotionDone;
    private bool _isStopCommandSent = false;

    private void ChangeState(ServoState newState)
    {
        if (State == newState) return;
        State = newState;
    }

    private bool CheckBeforeMove(double? targetPos = null)
    {
        if (State == ServoState.Error) return false;

        // 未使能时直接抛出致命错误
        if (!_isServoOn)
        {
            AlarmState.MoveWhileDisabledError = true;
            RaiseAlarm(ServoEvents.ErrMoveWhileDisabled);
            return false;
        }

        if (!_cfg.CanMove())
        {
            AlarmState.InterlockLost = true;
            RaiseAlarm(ServoEvents.ErrInterlockLost);
            return false;
        }

        // 软件限位预测拦截
        if (targetPos.HasValue)
        {
            if (targetPos.Value > _cfg.SoftLimitPositive || targetPos.Value < _cfg.SoftLimitNegative)
            {
                AlarmState.SoftwareLimitError = true;
                RaiseAlarm(ServoEvents.ErrSoftwareLimit, targetPos.Value);
                return false;
            }
        }
        return true;
    }

    private void EvaluateAlarms(AxisIoStatus ioStatus)
    {
        // 1. 伺服驱动器硬件报警 (ALM)
        if (ioStatus.ALM)
        {
            AlarmState.DriveAlarm = true;
            RaiseAlarm(ServoEvents.ErrDriveAlarm);
        }
        else
        {
            AlarmState.DriveAlarm = false;
        }

        // 2. 硬件极限保护 (PEL / MEL)
        if (ioStatus.PEL)
        {
            AlarmState.HardwareLimitPositive = true;
            RaiseAlarm(ServoEvents.ErrPEL);
        }
        else
        {
            AlarmState.HardwareLimitPositive = false;
        }

        if (ioStatus.MEL)
        {
            AlarmState.HardwareLimitNegative = true;
            RaiseAlarm(ServoEvents.ErrMEL);
        }
        else
        {
            AlarmState.HardwareLimitNegative = false;
        }

        // 3. 软件限位保护 (实时监测)
        if (_actualPosition > _cfg.SoftLimitPositive || _actualPosition < _cfg.SoftLimitNegative)
        {
            AlarmState.SoftwareLimitError = true;
            if (State != ServoState.Error && !_activeAlarms.ContainsKey(ServoEvents.ErrSoftwareLimit.EventId))
            {
                RaiseAlarm(ServoEvents.ErrSoftwareLimit, _actualPosition);
            }
        }
        else
        {
            AlarmState.SoftwareLimitError = false;
        }

        // 4. 安全联锁检查 (运动中突然丢失联锁)
        if (!_cfg.CanMove())
        {
            if (State != ServoState.Disabled && State != ServoState.Standby && State != ServoState.Error)
            {
                AlarmState.InterlockLost = true;
                RaiseAlarm(ServoEvents.ErrInterlockLost);
            }
        }
        else
        {
            AlarmState.InterlockLost = false;
        }
    }

    private void RaiseAlarm(EventBase eventbase, params object[] args)
    {
        if (!_activeAlarms.ContainsKey(eventbase.EventId))
        {
            var guid = Guid.NewGuid();
            _activeAlarms.Add(eventbase.EventId, (guid, eventbase, args));
            _eventProducer.RaiseAlarm(_cfg.Name, guid, eventbase, args);
        }

        if (eventbase.Severity == SeverityLevel.Error)
            ChangeState(ServoState.Error);
    }

    private void TryClearAlarm(EventBase eventbase)
    {
        if (_activeAlarms.Remove(eventbase.EventId, out var alarm))
        {
            _eventProducer.ClearAlarm(_cfg.Name, alarm.guid, alarm.eventBase, alarm.args);
        }
    }

    private void Reset()
    {
        if (State != ServoState.Error) return;

        // 连续物理故障：仅当物理标志恢复正常后，才允许清空报警记录
        if (!AlarmState.DriveAlarm) TryClearAlarm(ServoEvents.ErrDriveAlarm);
        if (!AlarmState.HardwareLimitPositive) TryClearAlarm(ServoEvents.ErrPEL);
        if (!AlarmState.HardwareLimitNegative) TryClearAlarm(ServoEvents.ErrMEL);
        if (!AlarmState.SoftwareLimitError) TryClearAlarm(ServoEvents.ErrSoftwareLimit);
        if (!AlarmState.InterlockLost) TryClearAlarm(ServoEvents.ErrInterlockLost);

        // 事件型故障，操作员确认复位后无条件清除
        AlarmState.MoveWhileDisabledError = false;
        TryClearAlarm(ServoEvents.ErrMoveWhileDisabled);

        if (!AlarmState.HasAnyError)
        {
            _isStopCommandSent = false;
            ChangeState(_isServoOn ? ServoState.Standby : ServoState.Disabled);
            _eventProducer.SendInfo(_cfg.Name, ServoEvents.InfoReset);
        }
    }

    private void ProcessCommandQueue()
    {
        while (_commandQueue.TryDequeue(out var cmd))
        {
            if (cmd.CancelToken.IsCancellationRequested) continue;

            if (_commandHandlers.TryGetValue(cmd.CmdName, out var handler))
            {
                handler(cmd);
            }
            else
            {
                cmd.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Rejected, "指令未定义"));
            }
        }
    }

    private void PurgeCommands()
    {
        while (_commandQueue.TryDequeue(out var cmd))
            cmd?.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Rejected, "被系统强制清理"));
    }

    private void RegisterCommandHandlers()
    {
        _commandHandlers[Command.Stop] = cmd =>
        {
            Stop();
            cmd.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Accepted, ""));
        };

        _commandHandlers[Command.Reset] = cmd =>
        {
            Reset();
            cmd.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Accepted, ""));
        };

        // 扩展的运动指令映射 (可以通过 Params 解析 target, speed 等)
        // ... (省略了参数解析的代码，类似之前的实现)
    }
}

// ==========================================
// 配置类、IO结构与报警状态
// ==========================================
public struct AxisIoStatus
{
    public bool ALM; // 驱动器报警 (Alarm)
    public bool PEL; // 正限位 (Positive Limit)
    public bool MEL; // 负限位 (Minus Limit)
    public bool ORG; // 原点传感器 (Origin)
    public bool INP; // 到位信号 (In-Position)
}

public enum ServoState
{
    Disabled,      // 未使能
    Standby,       // 已使能，空闲
    Homing,        // 回零中
    MovingAbs,     // 绝对定位中
    MovingRel,     // 相对定位中
    Jogging,       // 点动中
    VelocityMode,  // 速度模式运行中
    TorqueMode,    // 力矩控制模式中
    Error          // 故障锁死
}

public sealed class ServoAlarmState
{
    public bool HasAnyWarning => false;

    public bool DriveAlarm { get; internal set; }
    public bool HardwareLimitPositive { get; internal set; }
    public bool HardwareLimitNegative { get; internal set; }
    public bool SoftwareLimitError { get; internal set; }
    public bool InterlockLost { get; internal set; }

    // 新增：未使能运动报错
    public bool MoveWhileDisabledError { get; internal set; }

    public bool HasAnyError => DriveAlarm || HardwareLimitPositive || HardwareLimitNegative ||
                               SoftwareLimitError || InterlockLost || MoveWhileDisabledError;

    public override string ToString() => $"ALM={DriveAlarm}, PEL={HardwareLimitPositive}, MEL={HardwareLimitNegative}, SoftLimit={SoftwareLimitError}, Interlock={InterlockLost}, MoveDisabled={MoveWhileDisabledError}";
}

public class ServoCfg
{
    public required string Name { get; init; }

    // 软限位设置 (毫米/度 等业务工程单位)
    public double SoftLimitPositive { get; init; } = 9999.0;
    public double SoftLimitNegative { get; init; } = -9999.0;

    // 是否要求雷赛底层 INP 信号亮起才算真正到位
    public bool RequireInpSignal { get; init; } = true;

    // 状态读取委托 (由雷赛卡驱动层实现)
    public required Func<double> ReadActualPosition { get; init; }
    public required Func<double> ReadActualVelocity { get; init; }
    public required Func<bool> ReadServoStatus { get; init; }
    public required Func<bool> CheckMotionDone { get; init; }
    public required Func<AxisIoStatus> ReadAxisIoStatus { get; init; }
    public required Func<bool> CanMove { get; init; }

    // 动作执行委托 (封装雷赛 dmc_pmove, dmc_vmove 等)
    public required Action<bool> ActuateEnable { get; init; }
    public required Action<bool> ActuateStop { get; init; } // bool 参数表示是否为急停 (Emergency)
    public required Action ActuateHome { get; init; }
    public required Action<double, double> ActuateMoveAbs { get; init; } // target, speed
    public required Action<double, double> ActuateMoveRel { get; init; } // distance, speed
    public required Action<bool, double> ActuateJog { get; init; } // isPositiveDir, speed
    public required Action<double> ActuateVelocity { get; init; } // speed
    public required Action<double> ActuateTorque { get; init; } // torque

    public bool Validate()
    {
        return !string.IsNullOrEmpty(Name) &&
               ReadActualPosition != null && CheckMotionDone != null &&
               ActuateMoveAbs != null && ActuateStop != null;
    }
}

public sealed class ServoSnapshot
{
    public required string Name { get; init; }
    public required ServoState State { get; init; }
    public required ServoAlarmState AlarmState { get; init; } = new();
    public required double ActualPosition { get; init; }
    public required double ActualVelocity { get; init; }
    public required bool IsServoOn { get; init; }
}

public static class ServoEvents
{
    public static readonly EventBase InfoHomingStarted = new() { EventId = 601, Severity = SeverityLevel.Info, MessageTemplate = "开始回原点" };
    public static readonly EventBase InfoHomeDone = new() { EventId = 602, Severity = SeverityLevel.Info, MessageTemplate = "回原点完成" };
    public static readonly EventBase InfoMoveAbsStarted = new() { EventId = 603, Severity = SeverityLevel.Info, MessageTemplate = "绝对定位开始 (目标: {0:F3}, 速度: {1:F2})" };
    public static readonly EventBase InfoMoveRelStarted = new() { EventId = 604, Severity = SeverityLevel.Info, MessageTemplate = "相对定位开始 (距离: {0:F3}, 速度: {1:F2})" };
    public static readonly EventBase InfoMoveDone = new() { EventId = 605, Severity = SeverityLevel.Info, MessageTemplate = "轴停止到位 (当前位置: {0:F3})" };
    public static readonly EventBase InfoReset = new() { EventId = 606, Severity = SeverityLevel.Info, MessageTemplate = "轴报警复位" };

    public static readonly EventBase ErrDriveAlarm = new() { EventId = 620, Severity = SeverityLevel.Error, MessageTemplate = "伺服驱动器发生致命报警 (ALM)" };
    public static readonly EventBase ErrPEL = new() { EventId = 621, Severity = SeverityLevel.Error, MessageTemplate = "触发正向硬限位 (PEL)" };
    public static readonly EventBase ErrMEL = new() { EventId = 622, Severity = SeverityLevel.Error, MessageTemplate = "触发负向硬限位 (MEL)" };
    public static readonly EventBase ErrSoftwareLimit = new() { EventId = 623, Severity = SeverityLevel.Error, MessageTemplate = "越过软件限位或目标越界 (位置: {0:F3})" };
    public static readonly EventBase ErrInterlockLost = new() { EventId = 624, Severity = SeverityLevel.Error, MessageTemplate = "轴运动联锁丢失" };
    public static readonly EventBase ErrMoveWhileDisabled = new()
    {
        EventId = 625,
        Severity = SeverityLevel.Error,
        MessageTemplate = "伺服未使能时收到运动指令，拒绝执行"
    };
}
