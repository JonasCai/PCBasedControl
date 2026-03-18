using Controller.EventLogger;
using Controller.gRPC;
using Controller.S88;
using MongoDB.Driver;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Controller._01.ControlModule;

public class CM_Cylinder : IControlModule
{
    public CM_Cylinder(IEventProducer eventProducer, CylinderCfg cfg, ILogger<CM_Cylinder> logger)
    {
        _eventProducer = eventProducer;
        _cfg = cfg;
        _logger = logger;
        RegisterCommandHandlers();

        if (!_cfg.Validate())
            throw new ArgumentException($"气缸[{_cfg.Name}]配置不完整", nameof(_cfg));
    }

    // ==========================================
    // IControlModule 接口方法
    // ==========================================
    public string Name => _cfg.Name;
    public void Refresh(long currentTimestampMs)
    {
        _currentTimestampMs = currentTimestampMs;

        // 读取传感器状态
        bool isExtended = _cfg.ReadExtendedSensor();
        bool isRetracted = _cfg.ReadRetractedSensor();

        // 处理指令队列
        ProcessCommandQueue();

        // 检测两个传感器是否同时亮
        if (_state != CylinderState.Error && isExtended && isRetracted)
        {
            TransitionToError(CylinderError.SensorConflict, CylinderEvents.ErrSensorConflict); //传感器信号冲突，原位和动位传感器同时亮
            return;
        }

        // 状态机逻辑
        switch (_state)
        {
            case CylinderState.Unknown:
                _cfg.Actuate(CylinderCmd.ToSafe);
                if (isExtended) _state = CylinderState.Extended;
                else if (isRetracted) _state = CylinderState.Retracted;
                break;

            case CylinderState.ToExtendBusy:
                // 动作保持
                _cfg.Actuate(CylinderCmd.Extend);
                ToExtendElapsedTime = _currentTimestampMs - _toExtendStartTimestampMs;
                // 伸出条件丢失
                if (!_cfg.CanExtend())
                {
                    _cfg.Actuate(CylinderCmd.ToSafe);
                    TransitionToError(CylinderError.ExtendConditionsNotMet, CylinderEvents.ErrExtendInterlockLost);//伸出动作中外部联锁条件丢失
                }
                // 成功伸出
                else if (isExtended)
                {
                    ExtendCount++;
                    _state = CylinderState.Extended;
                    _eventProducer.SendInfo(_cfg.Name, CylinderEvents.InfoExtendedDone, ToExtendElapsedTime); //伸出到位 (耗时 {ToExtendElapsedTime} ms
                }
                // 超时判定
                else if (ToExtendElapsedTime > _cfg.ToExtendTout)
                {
                    TransitionToError(CylinderError.ExtendTimeout, CylinderEvents.ErrExtendTimeout, _cfg.ToExtendTout);//伸出动作超时 (>{ToExtendTout} ms)
                }
                break;

            case CylinderState.ToRetractBusy:
                // 动作保持
                _cfg.Actuate(CylinderCmd.Retract);
                ToRetractElapsedTime = _currentTimestampMs - _toRetractStartTimestampMs;

                // 缩回条件丢失
                if (!_cfg.CanRetract())
                {
                    _cfg.Actuate(CylinderCmd.ToSafe);
                    TransitionToError(CylinderError.RetractConditionsNotMet, CylinderEvents.ErrRetractInterlockLost);//缩回动作中外部联锁条件丢失
                }
                // 成功缩回
                else if (isRetracted)
                {
                    RetractCount++;
                    _state = CylinderState.Retracted;
                    _eventProducer.SendInfo(_cfg.Name, CylinderEvents.InfoRetractedDone, ToRetractElapsedTime); //缩回到位 (耗时 {ToRetractElapsedTime} ms
                }
                // 超时判定
                else if (ToRetractElapsedTime > _cfg.ToRetractTout)
                {
                    TransitionToError(CylinderError.RetractTimeout, CylinderEvents.ErrRetractTimeout, _cfg.ToRetractTout); //缩回动作超时 (>{ToRetractTout} ms)
                }
                break;

            case CylinderState.Extended:
                // 动作保持 (尤其是单电控气缸需要持续给电，双电控也建议保持)
                _cfg.Actuate(CylinderCmd.Extend);

                // 伸出条件丢失
                if (!_cfg.CanExtend())
                {
                    //_cylinderCfg.Actuate(CylinderCmd.ToSafe);
                    TransitionToError(CylinderError.ExtendConditionsNotMet, CylinderEvents.ErrExtendKeepInterlockLost);//伸出状态保持中外部联锁条件丢失
                }
                // 持续监控：位置保持，如果信号丢失，重新以此目标触发动作
                else if (!isExtended)
                {
                    _eventProducer.SendInfo(_cfg.Name, CylinderEvents.InfoExtSensorLost); //伸出位信号丢失，尝试重新检测
                    _state = CylinderState.ToExtendBusy;
                    _toExtendStartTimestampMs = _currentTimestampMs;
                }
                break;

            case CylinderState.Retracted:
                // 动作保持
                _cfg.Actuate(CylinderCmd.Retract);

                // 缩回条件丢失
                if (!_cfg.CanRetract())
                {
                    //_cylinderCfg.Actuate(CylinderCmd.ToSafe);
                    TransitionToError(CylinderError.RetractConditionsNotMet, CylinderEvents.ErrRetractKeepInterlockLost);//缩回状态保持中外部联锁条件丢失
                }
                // 持续监控
                else if (!isRetracted)
                {
                    _eventProducer.SendInfo(_cfg.Name, CylinderEvents.InfoRetSensorLost); //缩回位信号丢失，尝试重新检测
                    _state = CylinderState.ToRetractBusy;
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
        _state = CylinderState.Unknown;
    }
    public void ExecuteCommand(InternalCommand command) => _commandQueue.Enqueue(command);


    // ==========================================
    // 外部接口
    // ==========================================
    public void MoveRetract()
    {
        if (_state == CylinderState.Retracted || _state == CylinderState.ToRetractBusy)
            return;

        if (_state == CylinderState.Error) return;

        if (!_cfg.CanRetract())
        {
            TransitionToError(CylinderError.RetractConditionsNotMet, CylinderEvents.ErrRetractInterlock);
            return;
        }

        _eventProducer.SendInfo(_cfg.Name, CylinderEvents.InfoCmdRetract);//收到缩回指令，开始执行...

        _state = CylinderState.ToRetractBusy;

        _toRetractStartTimestampMs = _currentTimestampMs;
    }
    public void MoveExtend()
    {
        if (_state == CylinderState.Extended || _state == CylinderState.ToExtendBusy)
            return;

        if (_state == CylinderState.Error) return;

        if (!_cfg.CanExtend())
        {
            TransitionToError(CylinderError.ExtendConditionsNotMet, CylinderEvents.ErrExtendInterlock);//无法执行[伸出]：外部联锁条件不满足
            return;
        }

        _eventProducer.SendInfo(_cfg.Name, CylinderEvents.InfoCmdExtend);//收到伸出指令，开始执行
        _state = CylinderState.ToExtendBusy;

        _toExtendStartTimestampMs = _currentTimestampMs;
    }
    public long ToRetractElapsedTime { get; private set; }
    public long ToExtendElapsedTime { get; private set; }
    public int ExtendCount { get; private set; }
    public int RetractCount { get; private set; }
    public CylinderState State => _state;
    public CylinderError ErrorId => _errorId;
    public void ResetStatistics()
    {
        ExtendCount = 0;
        RetractCount = 0;
        _eventProducer.SendInfo(_cfg.Name, CylinderEvents.InfoClearStats); //动作次数累计清零
    }
    public string GetDiagnostics()
    {
        var sb = new StringBuilder();
        sb.Append($"[{_cfg.Name}] State={_state}, Error={_errorId}, ");
        sb.Append($"ExtSensor={_cfg.ReadExtendedSensor()}, ");
        sb.Append($"RetSensor={_cfg.ReadRetractedSensor()}, ");
        sb.Append($"CanExtend={_cfg.CanExtend()}, ");
        sb.Append($"CanRetract={_cfg.CanRetract()}, ");
        sb.Append($"ExtendET={ToExtendElapsedTime} ms, RetractET={ToRetractElapsedTime} ms, ");
        sb.Append($"ExtendCnt={ExtendCount}, RetractCnt={RetractCount}");
        return sb.ToString();
    }


    // ==========================================
    // 私有成员
    // ==========================================
    private readonly ILogger<CM_Cylinder> _logger;
    private readonly ConcurrentDictionary<Guid, (EventBase eventBase, object[] args)> _activeAlarms = new();
    private long _toExtendStartTimestampMs, _toRetractStartTimestampMs, _currentTimestampMs;
    private readonly CylinderCfg _cfg;
    private readonly IEventProducer _eventProducer;
    private CylinderState _state = CylinderState.Unknown;
    private CylinderError _errorId = CylinderError.None;
    private readonly ConcurrentQueue<InternalCommand> _commandQueue = new();
    private readonly Dictionary<Command, Action<InternalCommand>> _commandHandlers = new();
    private void TransitionToError(CylinderError err, EventBase eventbase, params object[] args)
    {
        if (_state == CylinderState.Error) return;

        _state = CylinderState.Error;
        _errorId = err;
        var guid = Guid.NewGuid();
        _eventProducer.RaiseAlarm(_cfg.Name, guid, eventbase, args);
        _activeAlarms.TryAdd(guid, (eventbase, args));

        _cfg.ErrorHandler?.Invoke(err);
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
        if (_state != CylinderState.Error) return;

        _eventProducer.SendInfo(_cfg.Name, CylinderEvents.InfoReset);

        _errorId = CylinderError.None;
        _state = CylinderState.Unknown;

        foreach (var alarm in _activeAlarms)
            _eventProducer.ClearAlarm(_cfg.Name, alarm.Key, alarm.Value.eventBase, alarm.Value.args);

        _activeAlarms.Clear();
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
}

public class CylinderCfg
{
    public required string Name { get; init; }
    public int ToExtendTout { get; init; } = 10000; // unit:ms
    public int ToRetractTout { get; init; } = 10000; // unit:ms
    public required Action<CylinderCmd> Actuate { get; init; }
    public required Func<bool> ReadExtendedSensor { get; init; }
    public required Func<bool> ReadRetractedSensor { get; init; }
    public required Func<bool> CanExtend { get; init; }
    public required Func<bool> CanRetract { get; init; }
    public Action<CylinderError>? ErrorHandler { get; init; }

    public bool Validate()
    {
        return !string.IsNullOrEmpty(Name) &&
               Actuate != null &&
               ReadExtendedSensor != null &&
               ReadRetractedSensor != null &&
               CanExtend != null &&
               CanRetract != null;
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

public enum CylinderError : int
{
    None = 1000,
    ExtendConditionsNotMet, // 伸出条件不满足
    RetractConditionsNotMet, // 缩回条件不满足
    ExtendTimeout, // 伸出超时
    RetractTimeout, // 缩回超时
    SensorConflict // 信号冲突
}

public static class CylinderEvents
{
    public static readonly EventBase InfoClearStats = new()
    {
        EventId = 1,
        Severity = SeverityLevel.Info,
        MessageTemplate = "动作次数累计清零"
    };
    public static readonly EventBase InfoCmdRetract = new()
    {
        EventId = 2,
        Severity = SeverityLevel.Info,
        MessageTemplate = "指令:开始缩回"
    };
    public static readonly EventBase InfoCmdExtend = new()
    {
        EventId = 3,
        Severity = SeverityLevel.Info,
        MessageTemplate = "指令:开始伸出"
    };
    public static readonly EventBase InfoReset = new()
    {
        EventId = 4,
        Severity = SeverityLevel.Info,
        MessageTemplate = "故障复位"
    };
    public static readonly EventBase InfoExtendedDone = new()
    {
        EventId = 5,
        Severity = SeverityLevel.Info,
        MessageTemplate = "伸出到位 (耗时 {0} ms)"
    };
    public static readonly EventBase InfoRetractedDone = new()
    {
        EventId = 6,
        Severity = SeverityLevel.Info,
        MessageTemplate = "缩回到位 (耗时 {0} ms)"
    };
    public static readonly EventBase InfoExtSensorLost = new()
    {
        EventId = 7,
        Severity = SeverityLevel.Info,
        MessageTemplate = "伸出位信号丢失，尝试维持"
    };
    public static readonly EventBase InfoRetSensorLost = new()
    {
        EventId = 8,
        Severity = SeverityLevel.Info,
        MessageTemplate = "缩回位信号丢失，尝试维持"
    };

    public static readonly EventBase ErrRetractInterlock = new()
    {
        EventId = 9,
        Severity = SeverityLevel.Error,
        MessageTemplate = "无法缩回：外部联锁不满足"
    };
    public static readonly EventBase ErrExtendInterlock = new()
    {
        EventId = 10,
        Severity = SeverityLevel.Error,
        MessageTemplate = "无法伸出：外部联锁不满足"
    };
    public static readonly EventBase ErrSensorConflict = new()
    {
        EventId = 11,
        Severity = SeverityLevel.Error,
        MessageTemplate = "传感器异常：原位和动位传感器同时亮"
    };
    public static readonly EventBase ErrExtendInterlockLost = new()
    {
        EventId = 12,
        Severity = SeverityLevel.Error,
        MessageTemplate = "伸出动作中联锁丢失"
    };
    public static readonly EventBase ErrExtendTimeout = new()
    {
        EventId = 13,
        Severity = SeverityLevel.Error,
        MessageTemplate = "伸出动作超时 (> {0} ms)"
    };
    public static readonly EventBase ErrRetractInterlockLost = new()
    {
        EventId = 14,
        Severity = SeverityLevel.Error,
        MessageTemplate = "缩回动作中联锁丢失"
    };
    public static readonly EventBase ErrRetractTimeout = new()
    {
        EventId = 15,
        Severity = SeverityLevel.Error,
        MessageTemplate = "缩回动作超时 (> {0} ms)"
    };
    public static readonly EventBase ErrExtendKeepInterlockLost = new()
    {
        EventId = 16,
        Severity = SeverityLevel.Error,
        MessageTemplate = "伸出保持中联锁丢失"
    };
    public static readonly EventBase ErrRetractKeepInterlockLost = new()
    {
        EventId = 17,
        Severity = SeverityLevel.Error,
        MessageTemplate = "缩回保持中联锁丢失"
    };
}
