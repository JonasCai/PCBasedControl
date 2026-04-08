using Controller.Common;
using Controller.EventLogger;
using Controller.gRPC;
using Controller.S88;
using System.Collections.Concurrent;

namespace Controller._01.ControlModule;

public class CM_Cylinder : IControlModule
{
    public CM_Cylinder(IEventProducer eventProducer, CylinderCfg cfg, ILogger<CM_Cylinder> logger)
    {
        _eventProducer = eventProducer;
        _cfg = cfg;
        _logger = logger;
        RegisterCommandHandlers();

        //初始化防抖器
        long debounceTime = cfg.SensorDebounceTimeMs > 0 ? cfg.SensorDebounceTimeMs : 50;
        _extSensorFilter = new DigitalDebouncer(debounceTime);
        _retSensorFilter = new DigitalDebouncer(debounceTime);

        if (!_cfg.Validate())
            throw new ArgumentException($"气缸[{_cfg.Name}]配置不完整", nameof(_cfg));
    }

    // ==========================================
    // IControlModule 接口方法
    // ==========================================
    public bool HasAnyWarning => AlarmState.HasAnyWarning;
    public bool HasAnyError => State == CylinderState.Error;
    public string Name => _cfg.Name;
    public void Refresh(long currentTimestampMs)
    {
        _currentTimestampMs = currentTimestampMs;

        // 读取原始信号
        _rawExt = (_cfg.SensorConfig is CylinderSensorConfig.DualSensors or CylinderSensorConfig.ExtendOnly)
                  && _cfg.ReadExtendedSensor != null ? _cfg.ReadExtendedSensor() : false;

        _rawRet = (_cfg.SensorConfig is CylinderSensorConfig.DualSensors or CylinderSensorConfig.RetractOnly)
                  && _cfg.ReadRetractedSensor != null ? _cfg.ReadRetractedSensor() : false;

        // 防抖过滤
        _physicalExt = _extSensorFilter.Filter(_rawExt, currentTimestampMs);
        _physicalRet = _retSensorFilter.Filter(_rawRet, currentTimestampMs);

        // 传感器状态推算
        switch (_cfg.SensorConfig)
        {
            case CylinderSensorConfig.DualSensors:
                _isExtended = _physicalExt;
                _isRetracted = _physicalRet;
                break;

            case CylinderSensorConfig.ExtendOnly:
                _isExtended = _physicalExt;
                // 伸出位传感器熄灭 + 处于缩回状态/或正在缩回且时间达标
                _isRetracted = !_physicalExt && (State == CylinderState.Retracted ||
                              (State == CylinderState.ToRetractBusy && _toRetractElapsedTime >= _cfg.VirtualRetractDelayMs));
                break;

            case CylinderSensorConfig.RetractOnly:
                _isRetracted = _physicalRet;
                // 缩回位传感器熄灭 + 处于伸出状态/或正在伸出且时间达标
                _isExtended = !_physicalRet && (State == CylinderState.Extended ||
                              (State == CylinderState.ToExtendBusy && _toExtendElapsedTime >= _cfg.VirtualExtendDelayMs));
                break;

            case CylinderSensorConfig.TimeBased:
                // 纯时间估算
                _isExtended = State == CylinderState.Extended || (State == CylinderState.ToExtendBusy && _toExtendElapsedTime >= _cfg.VirtualExtendDelayMs);
                _isRetracted = State == CylinderState.Retracted || (State == CylinderState.ToRetractBusy && _toRetractElapsedTime >= _cfg.VirtualRetractDelayMs);
                break;
        }

        // 处理指令队列
        ProcessCommandQueue();

        // 评估所有物理状态并触发/解除报警
        EvaluateAlarms(_isExtended, _isRetracted);

        // 状态机
        switch (State)
        {
            case CylinderState.Unknown:
                _cfg.Actuate(CylinderCmd.ToSafe);
                if (_isExtended) ChangeState(CylinderState.Extended);
                else if (_isRetracted) ChangeState(CylinderState.Retracted);
                break;

            case CylinderState.ToExtendBusy:
                // 动作保持
                _cfg.Actuate(CylinderCmd.Extend);
                _toExtendElapsedTime = _currentTimestampMs - _toExtendStartTimestampMs;
                if (_isExtended)
                {
                    _extendCount++;
                    ChangeState(CylinderState.Extended);
                    _eventProducer.SendInfo(_cfg.Name, CylinderEvents.InfoExtendedDone, _toExtendElapsedTime); //伸出到位 (耗时 {ToExtendElapsedTime} ms
                }
                break;

            case CylinderState.ToRetractBusy:
                // 动作保持
                _cfg.Actuate(CylinderCmd.Retract);
                _toRetractElapsedTime = _currentTimestampMs - _toRetractStartTimestampMs;
                if (_isRetracted)
                {
                    _retractCount++;
                    ChangeState(CylinderState.Retracted);
                    _eventProducer.SendInfo(_cfg.Name, CylinderEvents.InfoRetractedDone, _toRetractElapsedTime); //缩回到位 (耗时 {ToRetractElapsedTime} ms
                }
                break;

            case CylinderState.Extended:
                // 动作保持 (尤其是单电控气缸需要持续给电，双电控也建议保持)
                _cfg.Actuate(CylinderCmd.Extend);
                // 如果信号丢失且没发生联锁错误，重新以此目标触发动作
                if (!_isExtended)
                {
                    _eventProducer.SendInfo(_cfg.Name, CylinderEvents.InfoExtSensorLost);
                    ChangeState(CylinderState.ToExtendBusy);
                    _toExtendStartTimestampMs = _currentTimestampMs;
                }
                break;

            case CylinderState.Retracted:
                // 动作保持
                _cfg.Actuate(CylinderCmd.Retract);
                // 如果信号丢失且没发生联锁错误，重新以此目标触发动作
                if (!_isRetracted)
                {
                    _eventProducer.SendInfo(_cfg.Name, CylinderEvents.InfoRetSensorLost); //缩回位信号丢失，尝试重新检测
                    ChangeState(CylinderState.ToRetractBusy);
                    _toRetractStartTimestampMs = _currentTimestampMs;
                }
                break;

            case CylinderState.Error:
                break;
        }
    }
    public void ToSafe()
    {
        PurgeCommands();
        _cfg.Actuate(CylinderCmd.ToSafe);
        ChangeState(CylinderState.Unknown);
    }
    public void ExecuteCommand(InternalCommand command) => _commandQueue.Enqueue(command);


    // ==========================================
    // 外部接口
    // ==========================================
    public void MoveRetract()
    {
        if (State == CylinderState.Retracted || State == CylinderState.ToRetractBusy || State == CylinderState.Error)
            return;

        if (!_cfg.CanRetract())
        {
            AlarmState.RetractConditionsNotMet = true;
            RaiseAlarm(CylinderEvents.ErrRetractInterlock);
            return;
        }

        _eventProducer.SendInfo(_cfg.Name, CylinderEvents.InfoCmdRetract);//收到缩回指令，开始执行...
        ChangeState(CylinderState.ToRetractBusy);
        _toRetractStartTimestampMs = _currentTimestampMs;
    }
    public void MoveExtend()
    {
        if (State == CylinderState.Extended || State == CylinderState.ToExtendBusy || State == CylinderState.Error)
            return;

        if (!_cfg.CanExtend())
        {
            AlarmState.ExtendConditionsNotMet = true;
            RaiseAlarm(CylinderEvents.ErrExtendInterlock);
            return;
        }

        _eventProducer.SendInfo(_cfg.Name, CylinderEvents.InfoCmdExtend);//收到伸出指令，开始执行
        ChangeState(CylinderState.ToExtendBusy);
        _toExtendStartTimestampMs = _currentTimestampMs;
    }
    public CylinderState State { get; private set; } = CylinderState.Unknown;
    public CylinderAlarmState AlarmState = new();
    public CylinderSnapshot GetSnapshot() => new()
    {
        Name = _cfg.Name,
        State = State,
        AlarmState = AlarmState,
        ExtSensor = _isExtended,
        RetSensor = _isRetracted,
        CanExtend = _cfg.CanExtend(),
        CanRetract = _cfg.CanRetract(),
        ExtendET = _toExtendElapsedTime,
        RetractET = _toRetractElapsedTime,
        ExtendCnt = _extendCount,
        RetractCnt = _retractCount
    };


    // ==========================================
    // 私有成员
    // ==========================================
    private readonly ILogger<CM_Cylinder> _logger;
    private readonly Dictionary<int, (Guid guid, EventBase eventBase, object[] args)> _activeAlarms = new();
    private long _toExtendStartTimestampMs, _toRetractStartTimestampMs, _currentTimestampMs, _toRetractElapsedTime, _toExtendElapsedTime;
    private readonly CylinderCfg _cfg;
    private readonly IEventProducer _eventProducer;
    private readonly ConcurrentQueue<InternalCommand> _commandQueue = new();
    private readonly Dictionary<Command, Action<InternalCommand>> _commandHandlers = new();
    private DigitalDebouncer _extSensorFilter, _retSensorFilter;
    private int _extendCount, _retractCount;
    private bool _isExtended, _isRetracted, _rawExt, _rawRet, _physicalExt, _physicalRet;
    private void RaiseAlarm(EventBase eventbase, params object[] args)
    {
        if (!_activeAlarms.ContainsKey(eventbase.EventId))
        {
            var guid = Guid.NewGuid();
            _activeAlarms.Add(eventbase.EventId, (guid, eventbase, args));
            _eventProducer.RaiseAlarm(_cfg.Name, guid, eventbase, args);
        }

        if (eventbase.Severity == SeverityLevel.Error)
            ChangeState(CylinderState.Error);

    }
    private void ProcessCommandQueue()
    {
        while (_commandQueue.TryDequeue(out var cmd))
        {
            // 死亡确认
            if (cmd.CancelToken.IsCancellationRequested)
            {
                _logger.LogWarning("指令 {TargetUnit}.{TargetObject}.{CmdName} 在排队期间已被调用方取消或超时 (3s)，已作为僵尸指令安全丢弃", cmd.TargetUnit, cmd.TargetObject, cmd.CmdName);
                continue;
            }

            // 查表执行
            if (_commandHandlers.TryGetValue(cmd.CmdName, out var handler))
            {
                handler(cmd); // 执行绑定的动作
            }
            else
            {
                cmd.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Rejected, $"指令处理未定义：{cmd.TargetUnit}.{cmd.TargetObject}.{cmd.CmdName}"));
            }
        }

    }
    private void PurgeCommands()
    {
        while (_commandQueue.TryDequeue(out var cmd))
        {
            if (cmd?.CallbackTcs != null)
            {
                cmd.CallbackTcs.TrySetResult(new CommandResult(
                    CommandResultType.Rejected,
                    "指令被系统强制清理，未执行"
                ));
                _logger.LogWarning("指令 {TargetUnit}.{TargetObject}.{CmdName} 被系统强制清理，未执行", cmd.TargetUnit, cmd.TargetObject, cmd.CmdName);
            }
        }
    }
    private void Reset()
    {
        if (State != CylinderState.Error) return;

        if (!AlarmState.RetractConditionsNotMet)
        {
            TryClearAlarm(CylinderEvents.ErrRetractInterlock);
            TryClearAlarm(CylinderEvents.ErrRetractInterlockLost);
            TryClearAlarm(CylinderEvents.ErrRetractKeepInterlockLost);
        }

        if (!AlarmState.ExtendConditionsNotMet)
        {
            TryClearAlarm(CylinderEvents.ErrExtendInterlock);
            TryClearAlarm(CylinderEvents.ErrExtendInterlockLost);
            TryClearAlarm(CylinderEvents.ErrExtendKeepInterlockLost);
        }

        if (!AlarmState.SensorConflict)
        {
            TryClearAlarm(CylinderEvents.ErrSensorConflict);
        }

        // 无条件清除超时标志与报警，给系统重新尝试动作的机会。
        AlarmState.ExtendTimeout = false;
        TryClearAlarm(CylinderEvents.ErrExtendTimeout);

        AlarmState.RetractTimeout = false;
        TryClearAlarm(CylinderEvents.ErrRetractTimeout);

        if (!AlarmState.HasAnyError)
        {
            ChangeState(CylinderState.Unknown);
            _eventProducer.SendInfo(_cfg.Name, CylinderEvents.InfoReset);
        }
    }
    private void RegisterCommandHandlers()
    {
        _commandHandlers[Command.Extend] = cmd =>
        {
            cmd.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Accepted, string.Empty));
            MoveExtend();
        };

        _commandHandlers[Command.Retract] = cmd =>
        {
            cmd.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Accepted, string.Empty));
            MoveRetract();
        };

        _commandHandlers[Command.Reset] = cmd =>
        {
            cmd.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Accepted, string.Empty));
            Reset();
        };

        _commandHandlers[Command.ResetStatistics] = cmd =>
        {
            cmd.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Accepted, string.Empty));
            ResetStatistics();
        };

    }
    private void TryClearAlarm(EventBase eventbase)
    {
        if (_activeAlarms.Remove(eventbase.EventId, out var alarm))
        {
            _eventProducer.ClearAlarm(_cfg.Name, alarm.guid, alarm.eventBase, alarm.args);
        }
    }
    private void EvaluateAlarms(bool isExtended, bool isRetracted)
    {
        //寿命检测
        if (_extendCount > _cfg.LifetimeSP)
        {
            AlarmState.LifeTimeReached = true;
            RaiseAlarm(CylinderEvents.WarningLifetimeReached, _extendCount, _cfg.LifetimeSP);
        }
        else
        {
            AlarmState.LifeTimeReached = false;
            TryClearAlarm(CylinderEvents.WarningLifetimeReached);
        }

        // 传感器冲突检查
        if (isExtended && isRetracted)
        {
            AlarmState.SensorConflict = true;
            RaiseAlarm(CylinderEvents.ErrSensorConflict);
        }
        else
        {
            AlarmState.SensorConflict = false;
        }

        // 伸出联锁检查
        if (!_cfg.CanExtend())
        {
            if (State == CylinderState.ToExtendBusy)
            {
                AlarmState.ExtendConditionsNotMet = true;
                RaiseAlarm(CylinderEvents.ErrExtendInterlockLost);
            }
            else if (State == CylinderState.Extended)
            {
                AlarmState.ExtendConditionsNotMet = true;
                RaiseAlarm(CylinderEvents.ErrExtendKeepInterlockLost);
            }
        }
        else
        {
            AlarmState.ExtendConditionsNotMet = false;
        }

        // 缩回联锁检查
        if (!_cfg.CanRetract())
        {
            if (State == CylinderState.ToRetractBusy)
            {
                AlarmState.RetractConditionsNotMet = true;
                RaiseAlarm(CylinderEvents.ErrRetractInterlockLost);
            }
            else if (State == CylinderState.Retracted)
            {
                AlarmState.RetractConditionsNotMet = true;
                RaiseAlarm(CylinderEvents.ErrRetractKeepInterlockLost);
            }
        }
        else
        {
            AlarmState.RetractConditionsNotMet = false;
        }

        // 超时检查
        if (State == CylinderState.ToExtendBusy)
        {
            if (_toExtendElapsedTime > _cfg.ToExtendToutMs)
            {
                AlarmState.ExtendTimeout = true;
                RaiseAlarm(CylinderEvents.ErrExtendTimeout, _cfg.ToExtendToutMs);
            }
        }
        else if (isExtended || State == CylinderState.ToRetractBusy || State == CylinderState.Unknown)
        {
            AlarmState.ExtendTimeout = false; // 物理条件恢复或目标改变
        }

        if (State == CylinderState.ToRetractBusy)
        {
            if (_toRetractElapsedTime > _cfg.ToRetractToutMs)
            {
                AlarmState.RetractTimeout = true;
                RaiseAlarm(CylinderEvents.ErrRetractTimeout, _cfg.ToRetractToutMs);
            }
        }
        else if (isRetracted || State == CylinderState.ToExtendBusy || State == CylinderState.Unknown)
        {
            AlarmState.RetractTimeout = false;
        }
    }
    private void ChangeState(CylinderState newState)
    {
        if (State == newState) return;
        State = newState;
    }
    private void ResetStatistics()
    {
        _extendCount = 0;
        _retractCount = 0;
        _eventProducer.SendInfo(_cfg.Name, CylinderEvents.InfoClearStats); //动作次数累计清零
    }
}

// 气缸的传感器配置类型
public enum CylinderSensorConfig
{
    DualSensors, // 标配：双传感器
    ExtendOnly,  // 只有伸出位传感器 (缩回靠信号消失+时间推算)
    RetractOnly, // 只有缩回位传感器 (伸出靠信号消失+时间推算)
    TimeBased    // 无传感器 (纯靠双向时间推算)
}

public class CylinderCfg
{
    public required string Name { get; init; }

    // 硬件形态配置
    public CylinderSensorConfig SensorConfig { get; init; } = CylinderSensorConfig.DualSensors;

    // 虚拟行程推算时间 (当没有对应传感器时，指令下发后多久认为到位)
    public int VirtualExtendDelayMs { get; init; } = 2000;
    public int VirtualRetractDelayMs { get; init; } = 2000;

    public int ToExtendToutMs { get; init; } = 10000;
    public int ToRetractToutMs { get; init; } = 10000;
    public int LifetimeSP { get; init; } = 1000000;
    public int SensorDebounceTimeMs { get; init; } = 50;

    public required Action<CylinderCmd> Actuate { get; init; }
    public required Func<bool> CanExtend { get; init; }
    public required Func<bool> CanRetract { get; init; }

    public Func<bool>? ReadExtendedSensor { get; init; }
    public Func<bool>? ReadRetractedSensor { get; init; }

    public bool Validate()
    {
        bool valid = !string.IsNullOrEmpty(Name) && Actuate != null && CanExtend != null && CanRetract != null;

        // 校验传感器配置是否与委托匹配
        if (SensorConfig is CylinderSensorConfig.DualSensors or CylinderSensorConfig.ExtendOnly)
            valid &= ReadExtendedSensor != null;

        if (SensorConfig is CylinderSensorConfig.DualSensors or CylinderSensorConfig.RetractOnly)
            valid &= ReadRetractedSensor != null;

        return valid;
    }
}

public enum CylinderCmd
{
    ToSafe, // 断电/泄压/中位
    Retract, // 缩回/回原位
    Extend // 伸出/去动位
}

public enum CylinderState
{
    Unknown, // 未知
    ToExtendBusy, // 伸出中
    ToRetractBusy, // 缩回中
    Extended, // 已伸出
    Retracted, // 已缩回
    Error // 故障
}

public sealed class CylinderAlarmState
{
    public bool LifeTimeReached { get; internal set; }
    public bool HasAnyWarning => LifeTimeReached;
    public bool ExtendConditionsNotMet { get; internal set; }
    public bool RetractConditionsNotMet { get; internal set; }
    public bool ExtendTimeout { get; internal set; }
    public bool RetractTimeout { get; internal set; }
    public bool SensorConflict { get; internal set; }
    public bool HasAnyError => ExtendConditionsNotMet || RetractConditionsNotMet || ExtendTimeout || RetractTimeout || SensorConflict;
    public override string ToString() => $"ExtendConditionsNotMet={ExtendConditionsNotMet}, RetractConditionsNotMet={RetractConditionsNotMet}, ExtendTimeout={ExtendTimeout}, RetractTimeout={RetractTimeout}, SensorConflict={SensorConflict}";
}

public static class CylinderEvents
{
    public static readonly EventBase InfoClearStats = new()
    {
        EventId = 100,
        Severity = SeverityLevel.Info,
        MessageTemplate = "动作次数累计清零"
    };
    public static readonly EventBase InfoCmdRetract = new()
    {
        EventId = 101,
        Severity = SeverityLevel.Info,
        MessageTemplate = "指令:开始缩回"
    };
    public static readonly EventBase InfoCmdExtend = new()
    {
        EventId = 102,
        Severity = SeverityLevel.Info,
        MessageTemplate = "指令:开始伸出"
    };
    public static readonly EventBase InfoReset = new()
    {
        EventId = 103,
        Severity = SeverityLevel.Info,
        MessageTemplate = "故障复位"
    };
    public static readonly EventBase InfoExtendedDone = new()
    {
        EventId = 104,
        Severity = SeverityLevel.Info,
        MessageTemplate = "伸出到位 (耗时 {0} ms)"
    };
    public static readonly EventBase InfoRetractedDone = new()
    {
        EventId = 105,
        Severity = SeverityLevel.Info,
        MessageTemplate = "缩回到位 (耗时 {0} ms)"
    };
    public static readonly EventBase InfoExtSensorLost = new()
    {
        EventId = 106,
        Severity = SeverityLevel.Info,
        MessageTemplate = "伸出位信号丢失，尝试维持"
    };
    public static readonly EventBase InfoRetSensorLost = new()
    {
        EventId = 107,
        Severity = SeverityLevel.Info,
        MessageTemplate = "缩回位信号丢失，尝试维持"
    };

    public static readonly EventBase ErrRetractInterlock = new()
    {
        EventId = 120,
        Severity = SeverityLevel.Error,
        MessageTemplate = "无法缩回：外部联锁不满足"
    };
    public static readonly EventBase ErrExtendInterlock = new()
    {
        EventId = 121,
        Severity = SeverityLevel.Error,
        MessageTemplate = "无法伸出：外部联锁不满足"
    };
    public static readonly EventBase ErrSensorConflict = new()
    {
        EventId = 122,
        Severity = SeverityLevel.Error,
        MessageTemplate = "传感器异常：原位和动位传感器同时亮"
    };
    public static readonly EventBase ErrExtendInterlockLost = new()
    {
        EventId = 123,
        Severity = SeverityLevel.Error,
        MessageTemplate = "伸出动作中联锁丢失"
    };
    public static readonly EventBase ErrExtendTimeout = new()
    {
        EventId = 124,
        Severity = SeverityLevel.Error,
        MessageTemplate = "伸出动作超时 (> {0} ms)"
    };
    public static readonly EventBase ErrRetractInterlockLost = new()
    {
        EventId = 125,
        Severity = SeverityLevel.Error,
        MessageTemplate = "缩回动作中联锁丢失"
    };
    public static readonly EventBase ErrRetractTimeout = new()
    {
        EventId = 126,
        Severity = SeverityLevel.Error,
        MessageTemplate = "缩回动作超时 (> {0} ms)"
    };
    public static readonly EventBase ErrExtendKeepInterlockLost = new()
    {
        EventId = 127,
        Severity = SeverityLevel.Error,
        MessageTemplate = "伸出保持中联锁丢失"
    };
    public static readonly EventBase ErrRetractKeepInterlockLost = new() { EventId = 128, Severity = SeverityLevel.Error, MessageTemplate = "缩回保持中联锁丢失" };
    public static readonly EventBase WarningLifetimeReached = new() { EventId = 140, Severity = SeverityLevel.Warning, MessageTemplate = "寿命到达 (PV:{0} , SP:{1})" };
}

public sealed class CylinderSnapshot
{
    public required string Name { get; init; }
    public required CylinderState State { get; init; }
    public required CylinderAlarmState AlarmState { get; init; } = new();
    public required bool ExtSensor { get; init; }
    public required bool RetSensor { get; init; }
    public required bool CanExtend { get; init; }
    public required bool CanRetract { get; init; }
    public required long ExtendET { get; init; }
    public required long RetractET { get; init; }
    public required int ExtendCnt { get; init; }
    public required int RetractCnt { get; init; }
}

public interface ICylinderFactory
{
    CM_Cylinder Create(CylinderCfg cfg);
}

public class CylinderFactory : ICylinderFactory
{
    private readonly IServiceProvider _sp;
    public CylinderFactory(IServiceProvider sp) => _sp = sp;
    public CM_Cylinder Create(CylinderCfg cfg) => ActivatorUtilities.CreateInstance<CM_Cylinder>(_sp, cfg);
}
