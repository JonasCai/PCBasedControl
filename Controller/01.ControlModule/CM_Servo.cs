using Controller.EventLogger;
using Controller.gRPC;
using Controller.S88;
using System.Collections.Concurrent;


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

        // 读取硬件状态
        _axisStatus = _cfg.ReadAxisStatus(_cfg.AxisId);

        // 处理排队的外部指令
        ProcessCommandQueue();

        // 评估报警及硬/软限位联锁 (正负软限位已分离)
        EvaluateAlarms(_axisStatus);

        // 故障态立即切断运动，并跳过后续状态机
        if (State == ServoState.Error)
        {
            if (!_isStopCommandSent)
            {
                _cfg.ActuateStop(_cfg.AxisId, true);
                _isStopCommandSent = true;
            }
            return;
        }

        // 核心状态机逻辑
        switch (State)
        {
            case ServoState.Disabled:
                if (_axisStatus.ServoOn) ChangeState(ServoState.Standby);
                break;

            case ServoState.Standby:
                if (!_axisStatus.ServoOn) ChangeState(ServoState.Disabled);
                break;

            case ServoState.Homing:
                if (!_axisStatus.Moving)
                {
                    ChangeState(ServoState.Standby);
                    _isStopCommandSent = false;
                    _eventProducer.SendInfo(_cfg.Name, ServoEvents.InfoHomeDone);
                }
                break;

            case ServoState.MovingAbs:
            case ServoState.MovingRel:
                if (!_axisStatus.Moving)
                {
                    ChangeState(ServoState.Standby);
                    _isStopCommandSent = false;
                    _eventProducer.SendInfo(_cfg.Name, ServoEvents.InfoMoveDone, _axisStatus.ActPos);
                }
                break;

            case ServoState.VelocityMode:
            case ServoState.TorqueMode:
                if (_isStopCommandSent || (!_axisStatus.Moving && State != ServoState.TorqueMode))
                {
                    ChangeState(ServoState.Standby);
                    _isStopCommandSent = false;
                }
                break;
        }
    }

    public void ToSafe()
    {
        PurgeCommands();
        Stop(emergency: true);
    }

    public void ExecuteCommand(InternalCommand command) => _commandQueue.Enqueue(command);

    // ==========================================
    // 外部运动控制接口
    // ==========================================
    public void EnableServo(bool enable)
    {
        if (State == ServoState.Error) return;
        _cfg.ActuateEnable(_cfg.AxisId, enable);
        _eventProducer.SendInfo(_cfg.Name, enable ? ServoEvents.InfoServoEnabled : ServoEvents.InfoServoDisabled);
    }

    public void Stop(bool emergency = false)
    {
        if (State == ServoState.Error || State == ServoState.Disabled || State == ServoState.Standby) return;

        if (!_isStopCommandSent)
        {
            _cfg.ActuateStop(_cfg.AxisId, emergency);
            _isStopCommandSent = true;
            _eventProducer.SendInfo(_cfg.Name, ServoEvents.InfoStopped, emergency);
        }
    }

    public void Home()
    {
        if (!CheckBeforeMove()) return;

        _isStopCommandSent = false;
        _cfg.ActuateHome(_cfg.AxisId, _cfg.HomeMode);
        _axisStatus = _cfg.ReadAxisStatus(_cfg.AxisId);
        ChangeState(ServoState.Homing);
        _eventProducer.SendInfo(_cfg.Name, ServoEvents.InfoHomingStarted);
    }

    public void MoveAbs(float targetPos, float speed)
    {
        if (!CheckBeforeMove(targetPos)) return;

        _isStopCommandSent = false;
        _cfg.ActuateMoveAbs(_cfg.AxisId, targetPos, speed);
        _axisStatus = _cfg.ReadAxisStatus(_cfg.AxisId);
        ChangeState(ServoState.MovingAbs);
        _eventProducer.SendInfo(_cfg.Name, ServoEvents.InfoMoveAbsStarted, targetPos, speed);
    }

    public void MoveRel(float distance, float speed)
    {
        double expectedTarget = _axisStatus.ActPos + distance;
        if (!CheckBeforeMove(expectedTarget)) return;

        _isStopCommandSent = false;
        _cfg.ActuateMoveRel(_cfg.AxisId, distance, speed);
        _axisStatus = _cfg.ReadAxisStatus(_cfg.AxisId);
        ChangeState(ServoState.MovingRel);
        _eventProducer.SendInfo(_cfg.Name, ServoEvents.InfoMoveRelStarted, distance, speed);
    }

    public void MoveVelocity(float speed)
    {
        if (!CheckBeforeMove()) return;

        _isStopCommandSent = false;
        _cfg.ActuateVelocity(_cfg.AxisId, speed);
        _axisStatus = _cfg.ReadAxisStatus(_cfg.AxisId);
        ChangeState(ServoState.VelocityMode);
        _eventProducer.SendInfo(_cfg.Name, ServoEvents.InfoMoveVelStarted, speed);
    }

    public void SetTorque(float torquePercent)
    {
        if (!CheckBeforeMove()) return;

        _isStopCommandSent = false;
        _cfg.ActuateTorque(_cfg.AxisId, torquePercent);
        _axisStatus = _cfg.ReadAxisStatus(_cfg.AxisId);
        ChangeState(ServoState.TorqueMode);
        _eventProducer.SendInfo(_cfg.Name, ServoEvents.InfoTorqueStarted, torquePercent);
    }

    // ==========================================
    // 状态读取属性
    // ==========================================
    public ServoState State { get; private set; } = ServoState.Disabled;
    public ServoAlarmState AlarmState { get; } = new();
    public double ActualPosition => _axisStatus.ActPos;
    public double ActualVelocity => _axisStatus.ActVel;

    public ServoSnapshot GetSnapshot() => new()
    {
        Name = _cfg.Name,
        State = State,
        AlarmState = AlarmState,
        ActualPosition = _axisStatus.ActPos,
        ActualVelocity = _axisStatus.ActVel,
        IsServoOn = _axisStatus.ServoOn
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
    private AxisStatus _axisStatus;
    private bool _isStopCommandSent = false;

    private void ChangeState(ServoState newState)
    {
        if (State == newState) return;
        State = newState;
    }

    private bool CheckBeforeMove(double? targetPos = null)
    {
        if (State == ServoState.Error) return false;

        if (!_axisStatus.ServoOn)
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

        if (targetPos.HasValue)
        {
            if (targetPos.Value > _cfg.SoftLimitPositive || targetPos.Value < _cfg.SoftLimitNegative)
            {
                AlarmState.TargetOutOfBoundsError = true;
                RaiseAlarm(ServoEvents.ErrTargetOutOfBounds, targetPos.Value, _cfg.SoftLimitNegative, _cfg.SoftLimitPositive);
                return false;
            }
        }
        return true;
    }

    private void EvaluateAlarms(AxisStatus status)
    {
        // 驱动器报警 (ALM)
        if (status.Alarm)
        {
            AlarmState.DriveAlarm = true;
            RaiseAlarm(ServoEvents.ErrDriveAlarm);
        }
        else AlarmState.DriveAlarm = false;

        // 硬件极限 (PEL / MEL)
        if (status.PosLimit_H)
        {
            AlarmState.HardwareLimitPositive = true;
            RaiseAlarm(ServoEvents.ErrPosLimit);
        }
        else AlarmState.HardwareLimitPositive = false;

        if (status.NegLimit_H)
        {
            AlarmState.HardwareLimitNegative = true;
            RaiseAlarm(ServoEvents.ErrNegLimit);
        }
        else AlarmState.HardwareLimitNegative = false;

        // 软件限位保护
        if (status.PosLimit_S || status.ActPos > _cfg.SoftLimitPositive)
        {
            AlarmState.SoftLimitPositiveError = true;
            if (State != ServoState.Error && !_activeAlarms.ContainsKey(ServoEvents.ErrSoftLimitPositive.EventId))
            {
                RaiseAlarm(ServoEvents.ErrSoftLimitPositive, status.ActPos, _cfg.SoftLimitPositive);
            }
        }
        else
        {
            AlarmState.SoftLimitPositiveError = false;
        }

        if (status.NegLimit_S || status.ActPos < _cfg.SoftLimitNegative)
        {
            AlarmState.SoftLimitNegativeError = true;
            if (State != ServoState.Error && !_activeAlarms.ContainsKey(ServoEvents.ErrSoftLimitNegative.EventId))
            {
                RaiseAlarm(ServoEvents.ErrSoftLimitNegative, status.ActPos, _cfg.SoftLimitNegative);
            }
        }
        else
        {
            AlarmState.SoftLimitNegativeError = false;
        }

        // 联锁检查
        if (!_cfg.CanMove())
        {
            if (State != ServoState.Disabled && State != ServoState.Standby && State != ServoState.Error)
            {
                AlarmState.InterlockLost = true;
                RaiseAlarm(ServoEvents.ErrInterlockLost);
            }
        }
        else AlarmState.InterlockLost = false;
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

        if (!AlarmState.DriveAlarm) TryClearAlarm(ServoEvents.ErrDriveAlarm);
        if (!AlarmState.HardwareLimitPositive) TryClearAlarm(ServoEvents.ErrPosLimit);
        if (!AlarmState.HardwareLimitNegative) TryClearAlarm(ServoEvents.ErrNegLimit);
        if (!AlarmState.SoftLimitPositiveError) TryClearAlarm(ServoEvents.ErrSoftLimitPositive);
        if (!AlarmState.SoftLimitNegativeError) TryClearAlarm(ServoEvents.ErrSoftLimitNegative);
        if (!AlarmState.InterlockLost) TryClearAlarm(ServoEvents.ErrInterlockLost);

        // 事件型故障无条件清除
        AlarmState.MoveWhileDisabledError = false;
        TryClearAlarm(ServoEvents.ErrMoveWhileDisabled);

        AlarmState.TargetOutOfBoundsError = false;
        TryClearAlarm(ServoEvents.ErrTargetOutOfBounds);

        if (!AlarmState.HasAnyError)
        {
            _isStopCommandSent = false;
            ChangeState(_axisStatus.ServoOn ? ServoState.Standby : ServoState.Disabled);
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
            cmd?.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Rejected, "系统强制清理"));
    }

    private void RegisterCommandHandlers()
    {
        _commandHandlers[Command.Enable] = cmd =>
        {
            bool enable = true;
            if (cmd.Params.TryGetValue("State", out var stateStr)) bool.TryParse(stateStr, out enable);
            EnableServo(enable);
            cmd.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Accepted, ""));
        };

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

        _commandHandlers[Command.Home] = cmd =>
        {
            Home();
            cmd.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Accepted, ""));
        };

        _commandHandlers[Command.MoveAbs] = cmd =>
        {
            if (cmd.Params.TryGetValue("Target", out var tStr) && float.TryParse(tStr, out var target) &&
                cmd.Params.TryGetValue("Speed", out var sStr) && float.TryParse(sStr, out var speed))
            {
                MoveAbs(target, speed);
                cmd.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Accepted, ""));
            }
            else cmd.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Rejected, "缺失 Target 或 Speed 参数"));
        };

        _commandHandlers[Command.MoveRel] = cmd =>
        {
            if (cmd.Params.TryGetValue("Distance", out var dStr) && float.TryParse(dStr, out var dist) &&
                cmd.Params.TryGetValue("Speed", out var sStr) && float.TryParse(sStr, out var speed))
            {
                MoveRel(dist, speed);
                cmd.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Accepted, ""));
            }
            else cmd.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Rejected, "缺失 Distance 或 Speed 参数"));
        };

        _commandHandlers[Command.MoveVelocity] = cmd =>
        {
            if (cmd.Params.TryGetValue("Speed", out var sStr) && float.TryParse(sStr, out var speed))
            {
                MoveVelocity(speed);
                cmd.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Accepted, ""));
            }
            else cmd.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Rejected, "缺失 Speed 参数"));
        };

        _commandHandlers[Command.SetTorque] = cmd =>
        {
            if (cmd.Params.TryGetValue("Torque", out var tqStr) && float.TryParse(tqStr, out var torque))
            {
                SetTorque(torque);
                cmd.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Accepted, ""));
            }
            else cmd.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Rejected, "缺失 Torque 参数"));
        };
    }
}

// ==========================================
// 配置类、IO结构与报警状态
// ==========================================
public struct AxisStatus
{
    public bool Alarm; // 驱动器报警
    public bool PosLimit_H; // 硬正限位
    public bool NegLimit_H; // 硬负限位
    public bool Homed; // 回零标志
    public bool PosLimit_S; // 软正限位
    public bool NegLimit_S; // 软负限位
    public bool Moving; // 运动中
    public bool ServoOn; // 使能
    public double ActVel;//当前速度
    public double ActPos;//当前位置
    public double ActTrq;//当前扭矩
}

public enum ServoState
{
    Disabled,
    Standby,
    Homing,
    MovingAbs,
    MovingRel,
    VelocityMode,
    TorqueMode,
    Error
}

public sealed class ServoAlarmState
{
    public bool HasAnyWarning => false;

    public bool DriveAlarm { get; internal set; }
    public bool HardwareLimitPositive { get; internal set; }
    public bool HardwareLimitNegative { get; internal set; }

    public bool SoftLimitPositiveError { get; internal set; }
    public bool SoftLimitNegativeError { get; internal set; }

    public bool TargetOutOfBoundsError { get; internal set; }
    public bool InterlockLost { get; internal set; }
    public bool MoveWhileDisabledError { get; internal set; }

    public bool HasAnyError => DriveAlarm || HardwareLimitPositive || HardwareLimitNegative ||
                               SoftLimitPositiveError || SoftLimitNegativeError || TargetOutOfBoundsError ||
                               InterlockLost || MoveWhileDisabledError;

    public override string ToString() => $"ALM={DriveAlarm}, PEL={HardwareLimitPositive}, MEL={HardwareLimitNegative}, SoftPos={SoftLimitPositiveError}, SoftNeg={SoftLimitNegativeError}, TargetErr={TargetOutOfBoundsError}, Interlock={InterlockLost}, MoveDisabled={MoveWhileDisabledError}";
}

public class ServoCfg
{
    public required string Name { get; init; }

    public ushort AxisId { get; set; } = 0;
    public ushort HomeMode { get; set; } = 1;
    public float SoftLimitPositive { get; init; } = 9999.0f;
    public float SoftLimitNegative { get; init; } = -9999.0f;

    public required Func<ushort,AxisStatus> ReadAxisStatus { get; init; }
    public required Func<bool> CanMove { get; init; }

    public required Action<ushort, bool> ActuateEnable { get; init; }
    public required Action<ushort, bool> ActuateStop { get; init; }
    public required Action<ushort, ushort> ActuateHome { get; init; }
    public required Action<ushort, float, float> ActuateMoveAbs { get; init; }
    public required Action<ushort, float, float> ActuateMoveRel { get; init; }
    public required Action<ushort, float> ActuateVelocity { get; init; }
    public required Action<ushort, float> ActuateTorque { get; init; }

    public bool Validate()
    {
        return !string.IsNullOrEmpty(Name) &&
               ReadAxisStatus != null && CanMove != null &&
               ActuateEnable != null && ActuateStop != null &&
               ActuateHome != null && ActuateMoveAbs != null &&
               ActuateMoveRel != null && ActuateVelocity != null &&
               ActuateTorque != null;
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
    public static readonly EventBase InfoServoEnabled = new() { EventId = 607, Severity = SeverityLevel.Info, MessageTemplate = "伺服使能打开" };
    public static readonly EventBase InfoServoDisabled = new() { EventId = 608, Severity = SeverityLevel.Info, MessageTemplate = "伺服使能关闭" };
    public static readonly EventBase InfoStopped = new() { EventId = 609, Severity = SeverityLevel.Info, MessageTemplate = "触发停止指令 (急停: {0})" };
    public static readonly EventBase InfoMoveVelStarted = new() { EventId = 610, Severity = SeverityLevel.Info, MessageTemplate = "速度模式运行开始 (设定速度: {0:F2})" };
    public static readonly EventBase InfoTorqueStarted = new() { EventId = 611, Severity = SeverityLevel.Info, MessageTemplate = "力矩模式控制开始 (设定力矩: {0:F2}%)" };

    public static readonly EventBase ErrDriveAlarm = new() { EventId = 620, Severity = SeverityLevel.Error, MessageTemplate = "伺服驱动器发生致命报警" };
    public static readonly EventBase ErrPosLimit = new() { EventId = 621, Severity = SeverityLevel.Error, MessageTemplate = "触发正向硬限位 (PEL)" };
    public static readonly EventBase ErrNegLimit = new() { EventId = 622, Severity = SeverityLevel.Error, MessageTemplate = "触发负向硬限位 (MEL)" };
    public static readonly EventBase ErrSoftLimitPositive = new() { EventId = 623, Severity = SeverityLevel.Error, MessageTemplate = "物理运行越过正向软件限位 (当前位置: {0:F3} > 限制: {1:F3})" };
    public static readonly EventBase ErrSoftLimitNegative = new() { EventId = 624, Severity = SeverityLevel.Error, MessageTemplate = "物理运行越过负向软件限位 (当前位置: {0:F3} < 限制: {1:F3})" };
    public static readonly EventBase ErrInterlockLost = new() { EventId = 625, Severity = SeverityLevel.Error, MessageTemplate = "轴运动联锁丢失" };
    public static readonly EventBase ErrMoveWhileDisabled = new() { EventId = 626, Severity = SeverityLevel.Error, MessageTemplate = "伺服未使能时收到运动指令，拒绝执行" };
    public static readonly EventBase ErrTargetOutOfBounds = new() { EventId = 627, Severity = SeverityLevel.Error, MessageTemplate = "目标位置越过软件极限，拒绝执行 (目标: {0:F3}, 限制: [{1:F3}, {2:F3}])" };
}

public interface IServoFactory
{
    CM_Servo Create(ServoCfg cfg);
}

public class ServoFactory : IServoFactory
{
    private readonly IServiceProvider _sp;
    public ServoFactory(IServiceProvider sp) => _sp = sp;
    public CM_Servo Create(ServoCfg cfg) => ActivatorUtilities.CreateInstance<CM_Servo>(_sp, cfg);
}
