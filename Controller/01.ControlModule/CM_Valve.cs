using Controller.Common;
using Controller.EventLogger;
using Controller.gRPC;
using Controller.S88;
using System.Collections.Concurrent;

namespace Controller._01.ControlModule;

public class CM_Valve : IControlModule
{
    public CM_Valve(IEventProducer eventProducer, ValveCfg cfg, ILogger<CM_Valve> logger)
    {
        _eventProducer = eventProducer;
        _cfg = cfg;
        _logger = logger;
        RegisterCommandHandlers();

        //初始化防抖器
        long debounceTime = cfg.SensorDebounceTimeMs > 0 ? cfg.SensorDebounceTimeMs : 50;
        _openSensorFilter = new DigitalDebouncer(debounceTime);
        _closeSensorFilter = new DigitalDebouncer(debounceTime);

        if (!_cfg.Validate())
            throw new ArgumentException($"阀[{_cfg.Name}]配置不完整", nameof(_cfg));
    }

    // ==========================================
    // IControlModule 接口方法
    // ==========================================
    public bool HasAnyWarning => AlarmState.HasAnyWarning;
    public bool HasAnyError => State == ValveState.Error;
    public string Name => _cfg.Name;
    public void Refresh(long currentTimestampMs)
    {
        _currentTimestampMs = currentTimestampMs;

        // 读取原始信号
        _rawOpen = (_cfg.SensorConfig is ValveSensorConfig.DualSensors or ValveSensorConfig.OpenOnly)
                  && _cfg.ReadOpenSensor != null ? _cfg.ReadOpenSensor() : false;

        _rawClosed = (_cfg.SensorConfig is ValveSensorConfig.DualSensors or ValveSensorConfig.ClosedOnly)
                  && _cfg.ReadClosedSensor != null ? _cfg.ReadClosedSensor() : false;

        // 防抖过滤
        _physicalOpen = _openSensorFilter.Filter(_rawOpen, currentTimestampMs);
        _physicalClosed = _closeSensorFilter.Filter(_rawClosed, currentTimestampMs);

        // 传感器状态推算
        switch (_cfg.SensorConfig)
        {
            case ValveSensorConfig.DualSensors:
                _isOpen = _physicalOpen;
                _isClosed = _physicalClosed;
                break;

            case ValveSensorConfig.OpenOnly:
                _isOpen = _physicalOpen;
                // 伸出位传感器熄灭 + 处于缩回状态/或正在缩回且时间达标
                _isClosed = !_physicalOpen && (State == ValveState.Closed ||
                              (State == ValveState.ToCloseBusy && _toCloseElapsedTime >= _cfg.VirtualClosedDelayMs));
                break;

            case ValveSensorConfig.ClosedOnly:
                _isClosed = _physicalClosed;
                // 缩回位传感器熄灭 + 处于伸出状态/或正在伸出且时间达标
                _isOpen = !_physicalClosed && (State == ValveState.Open ||
                              (State == ValveState.ToOpenBusy && _toOpenElapsedTime >= _cfg.VirtualOpenDelayMs));
                break;

            case ValveSensorConfig.TimeBased:
                // 纯时间估算
                _isOpen = State == ValveState.Open || (State == ValveState.ToOpenBusy && _toOpenElapsedTime >= _cfg.VirtualOpenDelayMs);
                _isClosed = State == ValveState.Closed || (State == ValveState.ToCloseBusy && _toCloseElapsedTime >= _cfg.VirtualClosedDelayMs);
                break;
        }

        // 处理指令队列
        ProcessCommandQueue();

        // 评估所有物理状态并触发/解除报警
        EvaluateAlarms(_isOpen, _isClosed);

        // 状态机
        switch (State)
        {
            case ValveState.Unknown:
                _cfg.Actuate(ValveCmd.ToSafe);
                if (_isOpen) ChangeState(ValveState.Open);
                else if (_isClosed) ChangeState(ValveState.Closed);
                break;

            case ValveState.ToOpenBusy:
                // 动作保持
                _cfg.Actuate(ValveCmd.ToOpen);
                _toOpenElapsedTime = _currentTimestampMs - _toOpenStartTimestampMs;
                if (_isOpen)
                {
                    _openCount++;
                    ChangeState(ValveState.Open);
                    _eventProducer.SendInfo(_cfg.Name, ValveEvents.InfoOpenDone, _toOpenElapsedTime); //伸出到位 (耗时 {ToOpenElapsedTime} ms
                }
                break;

            case ValveState.ToCloseBusy:
                // 动作保持
                _cfg.Actuate(ValveCmd.ToClose);
                _toCloseElapsedTime = _currentTimestampMs - _toCloseStartTimestampMs;
                if (_isClosed)
                {
                    _closeCount++;
                    ChangeState(ValveState.Closed);
                    _eventProducer.SendInfo(_cfg.Name, ValveEvents.InfoClosedDone, _toCloseElapsedTime); //缩回到位 (耗时 {ToCloseElapsedTime} ms
                }
                break;

            case ValveState.Open:
                // 动作保持 (尤其是单电控气缸需要持续给电，双电控也建议保持)
                _cfg.Actuate(ValveCmd.ToOpen);
                // 如果信号丢失且没发生联锁错误，重新以此目标触发动作
                if (!_isOpen)
                {
                    _eventProducer.SendInfo(_cfg.Name, ValveEvents.InfoOpenSensorLost);
                    ChangeState(ValveState.ToOpenBusy);
                    _toOpenStartTimestampMs = _currentTimestampMs;
                }
                break;

            case ValveState.Closed:
                // 动作保持
                _cfg.Actuate(ValveCmd.ToClose);
                // 如果信号丢失且没发生联锁错误，重新以此目标触发动作
                if (!_isClosed)
                {
                    _eventProducer.SendInfo(_cfg.Name, ValveEvents.InfoCloseSensorLost); //缩回位信号丢失，尝试重新检测
                    ChangeState(ValveState.ToCloseBusy);
                    _toCloseStartTimestampMs = _currentTimestampMs;
                }
                break;

            case ValveState.Error:
                break;
        }
    }
    public void ToSafe()
    {
        PurgeCommands();
        _cfg.Actuate(ValveCmd.ToSafe);
        ChangeState(ValveState.Unknown);
    }
    public void ExecuteCommand(InternalCommand command) => _commandQueue.Enqueue(command);


    // ==========================================
    // 外部接口
    // ==========================================
    public void MoveClose()
    {
        if (State == ValveState.Closed || State == ValveState.ToCloseBusy || State == ValveState.Error)
            return;

        if (!_cfg.CanClose())
        {
            AlarmState.CloseConditionsNotMet = true;
            RaiseAlarm(ValveEvents.ErrCloseInterlock);
            return;
        }

        _eventProducer.SendInfo(_cfg.Name, ValveEvents.InfoCmdClose);//收到缩回指令，开始执行...
        ChangeState(ValveState.ToCloseBusy);
        _toCloseStartTimestampMs = _currentTimestampMs;
    }
    public void MoveOpen()
    {
        if (State == ValveState.Open || State == ValveState.ToOpenBusy || State == ValveState.Error)
            return;

        if (!_cfg.CanOpen())
        {
            AlarmState.OpenConditionsNotMet = true;
            RaiseAlarm(ValveEvents.ErrOpenInterlock);
            return;
        }

        _eventProducer.SendInfo(_cfg.Name, ValveEvents.InfoCmdOpen);//收到伸出指令，开始执行
        ChangeState(ValveState.ToOpenBusy);
        _toOpenStartTimestampMs = _currentTimestampMs;
    }
    public ValveState State { get; private set; } = ValveState.Unknown;
    public ValveAlarmState AlarmState = new();
    public ValveSnapshot GetSnapshot() => new()
    {
        Name = _cfg.Name,
        State = State,
        AlarmState = AlarmState,
        OpenSensor = _isOpen,
        ClosedSensor = _isClosed,
        CanOpen = _cfg.CanOpen(),
        CanClose = _cfg.CanClose(),
        OpenET = _toOpenElapsedTime,
        CloseET = _toCloseElapsedTime,
        OpenCnt = _openCount,
        CloseCnt = _closeCount
    };


    // ==========================================
    // 私有成员
    // ==========================================
    private readonly ILogger<CM_Valve> _logger;
    private readonly Dictionary<int, (Guid guid, EventBase eventBase, object[] args)> _activeAlarms = new();
    private long _toOpenStartTimestampMs, _toCloseStartTimestampMs, _currentTimestampMs, _toCloseElapsedTime, _toOpenElapsedTime;
    private readonly ValveCfg _cfg;
    private readonly IEventProducer _eventProducer;
    private readonly ConcurrentQueue<InternalCommand> _commandQueue = new();
    private readonly Dictionary<Command, Action<InternalCommand>> _commandHandlers = new();
    private DigitalDebouncer _openSensorFilter, _closeSensorFilter;
    private int _openCount, _closeCount;
    private bool _isOpen, _isClosed, _rawOpen, _rawClosed, _physicalOpen, _physicalClosed;
    private void RaiseAlarm(EventBase eventbase, params object[] args)
    {
        if (!_activeAlarms.ContainsKey(eventbase.EventId))
        {
            var guid = Guid.NewGuid();
            _activeAlarms.Add(eventbase.EventId, (guid, eventbase, args));
            _eventProducer.RaiseAlarm(_cfg.Name, guid, eventbase, args);
        }

        if (eventbase.Severity == SeverityLevel.Error)
            ChangeState(ValveState.Error);

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
        if (State != ValveState.Error) return;

        if (!AlarmState.CloseConditionsNotMet)
        {
            TryClearAlarm(ValveEvents.ErrCloseInterlock);
            TryClearAlarm(ValveEvents.ErrCloseInterlockLost);
            TryClearAlarm(ValveEvents.ErrCloseKeepInterlockLost);
        }

        if (!AlarmState.OpenConditionsNotMet)
        {
            TryClearAlarm(ValveEvents.ErrOpenInterlock);
            TryClearAlarm(ValveEvents.ErrOpenInterlockLost);
            TryClearAlarm(ValveEvents.ErrOpenKeepInterlockLost);
        }

        if (!AlarmState.SensorConflict)
        {
            TryClearAlarm(ValveEvents.ErrSensorConflict);
        }

        // 无条件清除超时标志与报警，给系统重新尝试动作的机会。
        AlarmState.OpenTimeout = false;
        TryClearAlarm(ValveEvents.ErrOpenTimeout);

        AlarmState.CloseTimeout = false;
        TryClearAlarm(ValveEvents.ErrCloseTimeout);

        if (!AlarmState.HasAnyError)
        {
            ChangeState(ValveState.Unknown);
            _eventProducer.SendInfo(_cfg.Name, ValveEvents.InfoReset);
        }
    }
    private void RegisterCommandHandlers()
    {
        _commandHandlers[Command.Open] = cmd =>
        {
            cmd.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Accepted, string.Empty));
            MoveOpen();
        };

        _commandHandlers[Command.Close] = cmd =>
        {
            cmd.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Accepted, string.Empty));
            MoveClose();
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
    private void EvaluateAlarms(bool isOpen, bool isClosed)
    {
        //寿命检测
        if (_openCount > _cfg.LifetimeSP)
        {
            AlarmState.LifeTimeReached = true;
            RaiseAlarm(ValveEvents.WarningLifetimeReached, _openCount, _cfg.LifetimeSP);
        }
        else
        {
            AlarmState.LifeTimeReached = false;
            TryClearAlarm(ValveEvents.WarningLifetimeReached);
        }

        // 传感器冲突检查
        if (isOpen && isClosed)
        {
            AlarmState.SensorConflict = true;
            RaiseAlarm(ValveEvents.ErrSensorConflict);
        }
        else
        {
            AlarmState.SensorConflict = false;
        }

        // 伸出联锁检查
        if (!_cfg.CanOpen())
        {
            if (State == ValveState.ToOpenBusy)
            {
                AlarmState.OpenConditionsNotMet = true;
                RaiseAlarm(ValveEvents.ErrOpenInterlockLost);
            }
            else if (State == ValveState.Open)
            {
                AlarmState.OpenConditionsNotMet = true;
                RaiseAlarm(ValveEvents.ErrOpenKeepInterlockLost);
            }
        }
        else
        {
            AlarmState.OpenConditionsNotMet = false;
        }

        // 缩回联锁检查
        if (!_cfg.CanClose())
        {
            if (State == ValveState.ToCloseBusy)
            {
                AlarmState.CloseConditionsNotMet = true;
                RaiseAlarm(ValveEvents.ErrCloseInterlockLost);
            }
            else if (State == ValveState.Closed)
            {
                AlarmState.CloseConditionsNotMet = true;
                RaiseAlarm(ValveEvents.ErrCloseKeepInterlockLost);
            }
        }
        else
        {
            AlarmState.CloseConditionsNotMet = false;
        }

        // 超时检查
        if (State == ValveState.ToOpenBusy)
        {
            if (_toOpenElapsedTime > _cfg.ToOpenToutMs)
            {
                AlarmState.OpenTimeout = true;
                RaiseAlarm(ValveEvents.ErrOpenTimeout, _cfg.ToOpenToutMs);
            }
        }
        else if (isOpen || State == ValveState.ToCloseBusy || State == ValveState.Unknown)
        {
            AlarmState.OpenTimeout = false; // 物理条件恢复或目标改变
        }

        if (State == ValveState.ToCloseBusy)
        {
            if (_toCloseElapsedTime > _cfg.ToCloseToutMs)
            {
                AlarmState.CloseTimeout = true;
                RaiseAlarm(ValveEvents.ErrCloseTimeout, _cfg.ToCloseToutMs);
            }
        }
        else if (isClosed || State == ValveState.ToOpenBusy || State == ValveState.Unknown)
        {
            AlarmState.CloseTimeout = false;
        }
    }
    private void ChangeState(ValveState newState)
    {
        if (State == newState) return;
        State = newState;
    }
    private void ResetStatistics()
    {
        _openCount = 0;
        _closeCount = 0;
        _eventProducer.SendInfo(_cfg.Name, ValveEvents.InfoClearStats); //动作次数累计清零
    }
}

// 传感器配置类型
public enum ValveSensorConfig
{
    DualSensors, // 标配：双传感器
    OpenOnly,  // 只有打开位传感器
    ClosedOnly, // 只有关闭位传感器
    TimeBased    // 无传感器 (纯靠双向时间推算)
}

public class ValveCfg
{
    public required string Name { get; init; }

    // 硬件形态配置
    public ValveSensorConfig SensorConfig { get; init; } = ValveSensorConfig.TimeBased;

    // 虚拟行程推算时间 (当没有对应传感器时，指令下发后多久认为到位)
    public int VirtualOpenDelayMs { get; init; } = 2000;
    public int VirtualClosedDelayMs { get; init; } = 2000;

    public int ToOpenToutMs { get; init; } = 10000;
    public int ToCloseToutMs { get; init; } = 10000;
    public int LifetimeSP { get; init; } = 1000000;
    public int SensorDebounceTimeMs { get; init; } = 50;

    public required Action<ValveCmd> Actuate { get; init; }
    public required Func<bool> CanOpen { get; init; }
    public required Func<bool> CanClose { get; init; }

    public Func<bool>? ReadOpenSensor { get; init; }
    public Func<bool>? ReadClosedSensor { get; init; }

    public bool Validate()
    {
        bool valid = !string.IsNullOrEmpty(Name) && Actuate != null && CanOpen != null && CanClose != null;

        // 校验传感器配置是否与委托匹配
        if (SensorConfig is ValveSensorConfig.DualSensors or ValveSensorConfig.OpenOnly)
            valid &= ReadOpenSensor != null;

        if (SensorConfig is ValveSensorConfig.DualSensors or ValveSensorConfig.ClosedOnly)
            valid &= ReadClosedSensor != null;

        return valid;
    }
}

public enum ValveCmd
{
    ToSafe,
    ToClose,
    ToOpen
}

public enum ValveState
{
    Unknown, // 未知
    ToOpenBusy, // 伸出中
    ToCloseBusy, // 缩回中
    Open, // 已伸出
    Closed, // 已缩回
    Error // 故障
}

public sealed class ValveAlarmState
{
    public bool LifeTimeReached { get; internal set; }
    public bool HasAnyWarning => LifeTimeReached;
    public bool OpenConditionsNotMet { get; internal set; }
    public bool CloseConditionsNotMet { get; internal set; }
    public bool OpenTimeout { get; internal set; }
    public bool CloseTimeout { get; internal set; }
    public bool SensorConflict { get; internal set; }
    public bool HasAnyError => OpenConditionsNotMet || CloseConditionsNotMet || OpenTimeout || CloseTimeout || SensorConflict;
    public override string ToString() => $"OpenConditionsNotMet={OpenConditionsNotMet}, CloseConditionsNotMet={CloseConditionsNotMet}, OpenTimeout={OpenTimeout}, CloseTimeout={CloseTimeout}, SensorConflict={SensorConflict}";
}

public static class ValveEvents
{
    public static readonly EventBase InfoClearStats = new()
    {
        EventId = 100,
        Severity = SeverityLevel.Info,
        MessageTemplate = "动作次数累计清零"
    };
    public static readonly EventBase InfoCmdClose = new()
    {
        EventId = 101,
        Severity = SeverityLevel.Info,
        MessageTemplate = "指令:开始关闭"
    };
    public static readonly EventBase InfoCmdOpen = new()
    {
        EventId = 102,
        Severity = SeverityLevel.Info,
        MessageTemplate = "指令:开始打开"
    };
    public static readonly EventBase InfoReset = new()
    {
        EventId = 103,
        Severity = SeverityLevel.Info,
        MessageTemplate = "故障复位"
    };
    public static readonly EventBase InfoOpenDone = new()
    {
        EventId = 104,
        Severity = SeverityLevel.Info,
        MessageTemplate = "打开到位 (耗时 {0} ms)"
    };
    public static readonly EventBase InfoClosedDone = new()
    {
        EventId = 105,
        Severity = SeverityLevel.Info,
        MessageTemplate = "关闭到位 (耗时 {0} ms)"
    };
    public static readonly EventBase InfoOpenSensorLost = new()
    {
        EventId = 106,
        Severity = SeverityLevel.Info,
        MessageTemplate = "打开位信号丢失，尝试维持"
    };
    public static readonly EventBase InfoCloseSensorLost = new()
    {
        EventId = 107,
        Severity = SeverityLevel.Info,
        MessageTemplate = "关闭位信号丢失，尝试维持"
    };

    public static readonly EventBase ErrCloseInterlock = new()
    {
        EventId = 120,
        Severity = SeverityLevel.Error,
        MessageTemplate = "无法关闭：外部联锁不满足"
    };
    public static readonly EventBase ErrOpenInterlock = new()
    {
        EventId = 121,
        Severity = SeverityLevel.Error,
        MessageTemplate = "无法打开：外部联锁不满足"
    };
    public static readonly EventBase ErrSensorConflict = new()
    {
        EventId = 122,
        Severity = SeverityLevel.Error,
        MessageTemplate = "传感器异常：关闭位和打开位传感器同时亮"
    };
    public static readonly EventBase ErrOpenInterlockLost = new()
    {
        EventId = 123,
        Severity = SeverityLevel.Error,
        MessageTemplate = "打开动作中联锁丢失"
    };
    public static readonly EventBase ErrOpenTimeout = new()
    {
        EventId = 124,
        Severity = SeverityLevel.Error,
        MessageTemplate = "打开动作超时 (> {0} ms)"
    };
    public static readonly EventBase ErrCloseInterlockLost = new()
    {
        EventId = 125,
        Severity = SeverityLevel.Error,
        MessageTemplate = "关闭动作中联锁丢失"
    };
    public static readonly EventBase ErrCloseTimeout = new()
    {
        EventId = 126,
        Severity = SeverityLevel.Error,
        MessageTemplate = "关闭动作超时 (> {0} ms)"
    };
    public static readonly EventBase ErrOpenKeepInterlockLost = new()
    {
        EventId = 127,
        Severity = SeverityLevel.Error,
        MessageTemplate = "打开保持中联锁丢失"
    };
    public static readonly EventBase ErrCloseKeepInterlockLost = new() { EventId = 128, Severity = SeverityLevel.Error, MessageTemplate = "关闭保持中联锁丢失" };
    public static readonly EventBase WarningLifetimeReached = new() { EventId = 140, Severity = SeverityLevel.Warning, MessageTemplate = "寿命到达 (PV:{0} , SP:{1})" };
}

public sealed class ValveSnapshot
{
    public required string Name { get; init; }
    public required ValveState State { get; init; }
    public required ValveAlarmState AlarmState { get; init; } = new();
    public required bool OpenSensor { get; init; }
    public required bool ClosedSensor { get; init; }
    public required bool CanOpen { get; init; }
    public required bool CanClose { get; init; }
    public required long OpenET { get; init; }
    public required long CloseET { get; init; }
    public required int OpenCnt { get; init; }
    public required int CloseCnt { get; init; }
}

public interface IValveFactory
{
    CM_Valve Create(ValveCfg cfg);
}

public class ValveFactory : IValveFactory
{
    private readonly IServiceProvider _sp;
    public ValveFactory(IServiceProvider sp) => _sp = sp;
    public CM_Valve Create(ValveCfg cfg) => ActivatorUtilities.CreateInstance<CM_Valve>(_sp, cfg);
}
