using Controller.Common;
using Controller.EventLogger;
using Controller.gRPC;
using Controller.S88;
using System;
using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Controller._01.ControlModule;

public class CM_Servo : S88ControlModuleBase
{
    public CM_Servo(IEventProducer eventProducer, ServoCfg cfg, ILogger<CM_Servo> logger) : base(cfg.Name, eventProducer, logger)
    {
        _cfg = cfg;
        RegisterCommandHandlers();

        if (!_cfg.Validate())
            throw new ArgumentException($"CM_Servo[{_cfg.Name}]配置不完整", nameof(_cfg));
    }

    // ==========================================
    // S88ControlModuleBase重写接口
    // ==========================================
    public override bool HasAnyWarning => AlarmState.HasAnyWarning;
    public override bool HasAnyError => State == ServoState.Error;

    public override void Refresh(long currentTimestampMs)
    {
        _currentTimestampMs = currentTimestampMs;

        // 读取硬件底层状态
        _axisStatus = _cfg.ReadAxisStatus(_cfg.AxisId);

        ProcessCommandQueue();

        // 报警集中评估、映射与硬件安全遮罩
        AlarmHandler();

        // 逻辑状态机
        if (State == ServoState.Error) return;

        switch (State)
        {
            case ServoState.Disabled:
                if (_axisStatus.ServoOn) ChangeState(ServoState.Standby);
                break;

            case ServoState.Standby:
                if (!_axisStatus.ServoOn) ChangeState(ServoState.Disabled);
                break;

            case ServoState.Homing:
                if (_currentTimestampMs - _commandIssueTimestampMs < _cfg.BusLatencyBlindTimeMs)
                    break;

                if (!_axisStatus.Moving)
                {
                    ChangeState(ServoState.Standby);
                    _isStopCommandSent = false;
                    RaiseInfo(ServoEvents.InfoHomeDone);
                }
                break;

            case ServoState.MovingAbs:
            case ServoState.MovingRel:
                if (_currentTimestampMs - _commandIssueTimestampMs < _cfg.BusLatencyBlindTimeMs)
                    break;

                if (!_axisStatus.Moving)
                {
                    ChangeState(ServoState.Standby);
                    _isStopCommandSent = false;
                    RaiseInfo(ServoEvents.InfoMoveDone, _axisStatus.ActPos);
                }
                break;

            case ServoState.VelocityMode:
            case ServoState.TorqueMode:
                if (_isStopCommandSent)
                {
                    ChangeState(ServoState.Standby);
                    _isStopCommandSent = false;
                }
                break;
        }
    }

    public override void ToSafe()
    {
        PurgeCommands();
        Stop(emergency: true);
    }

    // ==========================================
    // 外部运动控制接口
    // ==========================================
    public void EnableServo(bool enable)
    {
        if (State == ServoState.Error) return;
        _cfg.ActuateEnable(_cfg.AxisId, enable);
        RaiseInfo(enable ? ServoEvents.InfoServoEnabled : ServoEvents.InfoServoDisabled);
    }

    public void Stop(bool emergency = false)
    {
        if (State == ServoState.Error || State == ServoState.Disabled || State == ServoState.Standby) return;

        if (!_isStopCommandSent)
        {
            _cfg.ActuateStop(_cfg.AxisId, emergency);
            _isStopCommandSent = true;
            RaiseInfo(ServoEvents.InfoStopped, emergency);
        }
    }

    public void Home()
    {
        if (!CheckBeforeMove()) return;

        if (State == ServoState.TorqueMode || State == ServoState.VelocityMode)
        {
            AlarmState.InvalidModeError = true;
            return;
        }

        _isStopCommandSent = false;
        _cfg.ActuateHome(_cfg.AxisId, _cfg.HomeMode);

        _commandIssueTimestampMs = _currentTimestampMs;
        ChangeState(ServoState.Homing);
        RaiseInfo(ServoEvents.InfoHomingStarted);
    }

    public void MoveAbs(double targetPos, double speed, double taccdec)
    {
        if (!CheckBeforeMove(targetPos)) return;

        if (State == ServoState.TorqueMode || State == ServoState.VelocityMode)
        {
            AlarmState.InvalidModeError = true;
            return;
        }

        _isStopCommandSent = false;
        _cfg.ActuateMoveAbs(_cfg.AxisId, targetPos, speed, taccdec);

        _commandIssueTimestampMs = _currentTimestampMs;
        ChangeState(ServoState.MovingAbs);
        RaiseInfo(ServoEvents.InfoMoveAbsStarted, targetPos, speed);
    }

    public void MoveRel(double distance, double speed, double taccdec)
    {
        double expectedTarget = _axisStatus.ActPos + distance;
        if (!CheckBeforeMove(expectedTarget)) return;

        if (State == ServoState.TorqueMode || State == ServoState.VelocityMode)
        {
            AlarmState.InvalidModeError = true;
            return;
        }

        _isStopCommandSent = false;
        _cfg.ActuateMoveRel(_cfg.AxisId, distance, speed, taccdec);

        _commandIssueTimestampMs = _currentTimestampMs;
        ChangeState(ServoState.MovingRel);
        RaiseInfo(ServoEvents.InfoMoveRelStarted, distance, speed);
    }

    public void MoveVelocity(double speed, double taccdec)
    {
        if (!CheckBeforeMove()) return;

        if (State == ServoState.TorqueMode)
        {
            AlarmState.InvalidModeError = true;
            return;
        }

        _isStopCommandSent = false;
        if (State == ServoState.VelocityMode)
        {
            _cfg.ChangeVelocity(_cfg.AxisId, speed, taccdec);
        }
        else
        {
            _cfg.ActuateVelocity(_cfg.AxisId, speed, taccdec);
            _commandIssueTimestampMs = _currentTimestampMs;
            ChangeState(ServoState.VelocityMode);
        }
        RaiseInfo(ServoEvents.InfoMoveVelStarted, speed);
    }

    public void SetTorque(double torquePercent)
    {
        if (!CheckBeforeMove()) return;

        if (State == ServoState.VelocityMode)
        {
            AlarmState.InvalidModeError = true;
            return;
        }

        _isStopCommandSent = false;
        if (State == ServoState.TorqueMode)
        {
            _cfg.ChangeTorque(_cfg.AxisId, torquePercent);
        }
        else
        {
            _cfg.ActuateTorque(_cfg.AxisId, torquePercent);
            _commandIssueTimestampMs = _currentTimestampMs;
            ChangeState(ServoState.TorqueMode);
        }
        RaiseInfo(ServoEvents.InfoTorqueStarted, torquePercent);
    }

    // ==========================================
    // 状态读取属性
    // ==========================================
    public ServoState State { get; private set; } = ServoState.Disabled;
    public ServoAlarmState AlarmState { get; } = new();
    public double ActualPosition => _axisStatus.ActPos;
    public double ActualVelocity => _axisStatus.ActVel;
    public double ActualTorque => _axisStatus.ActTrq;
    public ServoSnapshot GetSnapshot() => new()
    {
        Name = _cfg.Name,
        State = State,
        AlarmState = AlarmState,
        ActualTorque = _axisStatus.ActTrq,
        ActualPosition = _axisStatus.ActPos,
        ActualVelocity = _axisStatus.ActVel,
        IsServoOn = _axisStatus.ServoOn
    };

    // ==========================================
    // 私有成员与核心逻辑
    // ==========================================
    private readonly ServoCfg _cfg;
    private long _currentTimestampMs;
    private AxisStatus _axisStatus;
    private bool _isStopCommandSent = false;
    private long _commandIssueTimestampMs = 0;

    private void ChangeState(ServoState newState)
    {
        if (State == newState) return;
        State = newState;
    }

    private bool CheckBeforeMove(double? targetPos = null)
    {
        if (State == ServoState.Error) return false;
        bool ok = true;

        if (!_axisStatus.ServoOn)
        {
            AlarmState.MoveWhileDisabledError = true;
            ok = false;
        }

        if (!_cfg.CanMove())
        {
            AlarmState.InterlockLostError = true;
            ok = false;
        }

        // 软件限位指令预判
        if (targetPos.HasValue)
        {
            if (targetPos.Value > _cfg.SoftLimitPositive || targetPos.Value < _cfg.SoftLimitNegative)
            {
                AlarmState.TargetOutOfBoundsError = true;
                AlarmState.BadTargetPos = targetPos.Value;
                ok = false;
            }
        }

        return ok;
    }

    private void AlarmHandler()
    {
        // 驱动器报警
        if (_axisStatus.Alarm)
        {
            AlarmState.DriveAlarm = true;
            AlarmState.DriveAlarmId = _axisStatus.ErrorCode;
        }

        // 软件限位逻辑 
        if (_axisStatus.ActPos > _cfg.SoftLimitPositive) AlarmState.SoftLimitPositiveError = true;
        if (_axisStatus.ActPos < _cfg.SoftLimitNegative) AlarmState.SoftLimitNegativeError = true;

        if (!_cfg.CanMove() && State is not ServoState.Disabled and not ServoState.Standby and not ServoState.Error)
        {
            AlarmState.InterlockLostError = true;
        }

        if (AlarmState.HasAnyError && !_isStopCommandSent)
        {
            _cfg.ActuateStop(_cfg.AxisId, true);
            _isStopCommandSent = true;
        }

        if (AlarmState.DriveAlarm) RaiseAlarm(ServoEvents.ErrDriveAlarm, AlarmState.DriveAlarmId);
        else TryClearAlarm(ServoEvents.ErrDriveAlarm);

        if (AlarmState.SoftLimitPositiveError) RaiseAlarm(ServoEvents.ErrSoftLimitPositive, _axisStatus.ActPos, _cfg.SoftLimitPositive);
        else TryClearAlarm(ServoEvents.ErrSoftLimitPositive);

        if (AlarmState.SoftLimitNegativeError) RaiseAlarm(ServoEvents.ErrSoftLimitNegative, _axisStatus.ActPos, _cfg.SoftLimitNegative);
        else TryClearAlarm(ServoEvents.ErrSoftLimitNegative);

        if (AlarmState.InterlockLostError) RaiseAlarm(ServoEvents.ErrInterlockLost);
        else TryClearAlarm(ServoEvents.ErrInterlockLost);

        if (AlarmState.MoveWhileDisabledError) RaiseAlarm(ServoEvents.ErrMoveWhileDisabled);
        else TryClearAlarm(ServoEvents.ErrMoveWhileDisabled);

        if (AlarmState.TargetOutOfBoundsError) RaiseAlarm(ServoEvents.ErrTargetOutOfBounds, AlarmState.BadTargetPos, _cfg.SoftLimitNegative, _cfg.SoftLimitPositive);
        else TryClearAlarm(ServoEvents.ErrTargetOutOfBounds);

        if (AlarmState.InvalidModeError) RaiseAlarm(ServoEvents.ErrInvalidMode, State.ToString());
        else TryClearAlarm(ServoEvents.ErrInvalidMode);

        if (AlarmState.HasAnyError && State != ServoState.Error)
        {
            ChangeState(ServoState.Error);
        }
    }

    private void Reset()
    {
        if (State != ServoState.Error) return;

        // 向驱动器下发硬件复位指令
        _cfg.ClearAxisError(_cfg.AxisId);

        // 驱动器报警必须在底层消除后才允许软件复位
        if (!_axisStatus.Alarm) AlarmState.DriveAlarm = false;

        // 必须物理上回到了软限位以内，才允许清除
        if (_axisStatus.ActPos <= _cfg.SoftLimitPositive)
            AlarmState.SoftLimitPositiveError = false;
        if (_axisStatus.ActPos >= _cfg.SoftLimitNegative)
            AlarmState.SoftLimitNegativeError = false;

        // 外部联锁恢复才允许清除
        if (_cfg.CanMove()) AlarmState.InterlockLostError = false;

        AlarmState.MoveWhileDisabledError = false;
        AlarmState.TargetOutOfBoundsError = false;
        AlarmState.InvalidModeError = false;

        if (!AlarmState.HasAnyError)
        {
            _isStopCommandSent = false;
            ChangeState(_axisStatus.ServoOn ? ServoState.Standby : ServoState.Disabled);
            RaiseInfo(ServoEvents.InfoReset);
        }
    }

    private void RegisterCommandHandlers()
    {
        RegisterCommandHandler(Command.Enable, cmd =>
        {
            bool enable = true;
            if (cmd.Params.TryGetValue("State", out var stateStr)) bool.TryParse(stateStr, out enable);
            EnableServo(enable);
            cmd.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Accepted, ""));
        });

        RegisterCommandHandler(Command.Stop, cmd =>
        {
            Stop();
            cmd.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Accepted, ""));
        });

        RegisterCommandHandler(Command.Reset, cmd =>
        {
            Reset();
            cmd.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Accepted, ""));
        });

        RegisterCommandHandler(Command.Home, cmd =>
        {
            Home();
            cmd.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Accepted, ""));
        });

        RegisterCommandHandler(Command.MoveAbs, cmd =>
        {
            if (cmd.Params.TryGetValue("Target", out var tStr) && double.TryParse(tStr, out var target) &&
                cmd.Params.TryGetValue("Speed", out var sStr) && double.TryParse(sStr, out var speed) &&
                cmd.Params.TryGetValue("Taccdec", out var accdecStr) && double.TryParse(accdecStr, out var taccdec))
            {
                MoveAbs(target, speed, taccdec);
                cmd.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Accepted, ""));
            }
            else cmd.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Rejected, "缺失 Target/Speed/Taccdec 参数"));
        });

        RegisterCommandHandler(Command.MoveRel, cmd =>
        {
            if (cmd.Params.TryGetValue("Distance", out var dStr) && double.TryParse(dStr, out var dist) &&
                cmd.Params.TryGetValue("Speed", out var sStr) && double.TryParse(sStr, out var speed) &&
                cmd.Params.TryGetValue("Taccdec", out var accdecStr) && double.TryParse(accdecStr, out var taccdec))
            {
                MoveRel(dist, speed, taccdec);
                cmd.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Accepted, ""));
            }
            else cmd.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Rejected, "缺失 Distance/Speed/Taccdec 参数"));
        });

        RegisterCommandHandler(Command.MoveVelocity, cmd =>
        {
            if (cmd.Params.TryGetValue("Speed", out var sStr) && double.TryParse(sStr, out var speed) &&
                cmd.Params.TryGetValue("Taccdec", out var accdecStr) && double.TryParse(accdecStr, out var taccdec))
            {
                MoveVelocity(speed, taccdec);
                cmd.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Accepted, ""));
            }
            else cmd.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Rejected, "缺失 Speed/Taccdec 参数"));
        });

        RegisterCommandHandler(Command.SetTorque, cmd =>
        {
            if (cmd.Params.TryGetValue("Torque", out var tqStr) && double.TryParse(tqStr, out var torque))
            {
                SetTorque(torque);
                cmd.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Accepted, ""));
            }
            else cmd.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Rejected, "缺失 Torque 参数"));
        });
    }

    protected override void RaiseAlarm(EventBase eventbase, params object[] args)
    {
        base.RaiseAlarm(eventbase, args);
        if (eventbase.Severity == SeverityLevel.Error)
            ChangeState(ServoState.Error);
    }
}

// ==========================================
// 配置类、IO结构与报警状态
// ==========================================
public struct AxisStatus
{
    public bool Alarm;
    public ushort ErrorCode; // 驱动器原生报警码
    public bool Homed;
    public bool Moving;
    public bool ServoOn;
    public double ActVel;
    public double ActPos;
    public double ActTrq;
}

public enum ServoState { Disabled, Standby, Homing, MovingAbs, MovingRel, VelocityMode, TorqueMode, Error }

public sealed class ServoAlarmState
{
    public bool HasAnyWarning => false;

    public bool DriveAlarm { get; internal set; }
    public ushort DriveAlarmId { get; internal set; }
    public bool SoftLimitPositiveError { get; internal set; }
    public bool SoftLimitNegativeError { get; internal set; }
    public bool TargetOutOfBoundsError { get; internal set; }
    public double BadTargetPos { get; internal set; }
    public bool InterlockLostError { get; internal set; }
    public bool MoveWhileDisabledError { get; internal set; }
    public bool InvalidModeError { get; internal set; }

    public bool HasAnyError => DriveAlarm || SoftLimitPositiveError || SoftLimitNegativeError ||
                               TargetOutOfBoundsError || InterlockLostError || MoveWhileDisabledError || InvalidModeError;

    public override string ToString() => $"ALM={DriveAlarm}({DriveAlarmId}), SoftPos={SoftLimitPositiveError}, SoftNeg={SoftLimitNegativeError}, TargetErr={TargetOutOfBoundsError}, Interlock={InterlockLostError}, MoveDisabled={MoveWhileDisabledError}, InvalidMode={InvalidModeError}";
}

public class ServoCfg
{
    public required string Name { get; init; }
    public required ushort AxisId { get; init; } = 1;
    public required ushort HomeMode { get; init; } = 1;

    public long BusLatencyBlindTimeMs { get; init; } = 50;

    public double SoftLimitPositive { get; init; } = 9999.0;
    public double SoftLimitNegative { get; init; } = -9999.0;

    public required Func<ushort, AxisStatus> ReadAxisStatus { get; init; }
    public required Func<bool> CanMove { get; init; }

    public required Action<ushort> ClearAxisError { get; init; }
    public required Action<ushort, bool> ActuateEnable { get; init; }
    public required Action<ushort, bool> ActuateStop { get; init; }
    public required Action<ushort, ushort> ActuateHome { get; init; }
    public required Action<ushort, double, double, double> ActuateMoveAbs { get; init; }
    public required Action<ushort, double, double, double> ActuateMoveRel { get; init; }
    public required Action<ushort, double, double> ActuateVelocity { get; init; }
    public required Action<ushort, double, double> ChangeVelocity { get; init; }
    public required Action<ushort, double> ActuateTorque { get; init; }
    public required Action<ushort, double> ChangeTorque { get; init; }

    public bool Validate()
    {
        return !string.IsNullOrEmpty(Name) &&
               ReadAxisStatus != null && CanMove != null &&
               ActuateEnable != null && ActuateStop != null &&
               ActuateHome != null && ActuateMoveAbs != null &&
               ActuateMoveRel != null && ActuateVelocity != null &&
               ActuateTorque != null && ClearAxisError != null &&
               ChangeVelocity != null && ChangeTorque != null;
    }
}

public sealed class ServoSnapshot
{
    public required string Name { get; init; }
    public required ServoState State { get; init; }
    public required ServoAlarmState AlarmState { get; init; } = new();
    public required double ActualTorque { get; init; }
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

    public static readonly EventBase ErrDriveAlarm = new() { EventId = 620, Severity = SeverityLevel.Error, MessageTemplate = "伺服驱动器发生致命报警 (AlarmId : {0})" };
    public static readonly EventBase ErrSoftLimitPositive = new() { EventId = 621, Severity = SeverityLevel.Error, MessageTemplate = "物理运行越过正向软件限位 (当前位置: {0:F3} > 限制: {1:F3})" };
    public static readonly EventBase ErrSoftLimitNegative = new() { EventId = 622, Severity = SeverityLevel.Error, MessageTemplate = "物理运行越过负向软件限位 (当前位置: {0:F3} < 限制: {1:F3})" };
    public static readonly EventBase ErrInterlockLost = new() { EventId = 623, Severity = SeverityLevel.Error, MessageTemplate = "轴运动联锁丢失" };
    public static readonly EventBase ErrMoveWhileDisabled = new() { EventId = 624, Severity = SeverityLevel.Error, MessageTemplate = "伺服未使能时收到运动指令，拒绝执行" };
    public static readonly EventBase ErrTargetOutOfBounds = new() { EventId = 625, Severity = SeverityLevel.Error, MessageTemplate = "目标位置越过软件极限，拒绝执行 (目标: {0:F3}, 限制: [{1:F3}, {2:F3}])" };
    public static readonly EventBase ErrInvalidMode = new() { EventId = 626, Severity = SeverityLevel.Error, MessageTemplate = "模式冲突：当前处于 {0} ，拒绝执行该运动指令" };
}

public interface IServoFactory { CM_Servo Create(ServoCfg cfg); }

public class ServoFactory : IServoFactory
{
    private readonly IServiceProvider _sp;
    public ServoFactory(IServiceProvider sp) => _sp = sp;
    public CM_Servo Create(ServoCfg cfg) => ActivatorUtilities.CreateInstance<CM_Servo>(_sp, cfg);
}