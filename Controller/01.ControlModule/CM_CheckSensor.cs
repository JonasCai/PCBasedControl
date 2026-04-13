using Controller._01.ControlModule;
using Controller.Common;
using Controller.EventLogger;
using Controller.gRPC;
using Controller.S88;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Controller._01.ControlModule;


public class CM_CheckSensor : IControlModule
{
    public CM_CheckSensor(IEventProducer eventProducer, CheckSensorCfg cfg, ILogger<CM_CheckSensor> logger)
    {
        _eventProducer = eventProducer;
        _cfg = cfg;
        _logger = logger;
        RegisterCommandHandlers();

        // 初始化防抖器
        _debouncer = new DigitalDebouncer(_cfg.DebounceTimeMs);

        if (!_cfg.Validate())
            throw new ArgumentException($"CM_CheckSensor[{_cfg.Name}]配置不完整", nameof(_cfg));

        // 初始化状态与监控目标
        _expectedState = _cfg.DefaultExpectedState;
        _currentTimeoutMs = _cfg.DefaultMismatchTimeoutMs;
        _currentSeverity = _cfg.DefaultMismatchSeverity;
        State = _cfg.AutoStartMonitoring ? CheckSensorState.Monitoring : CheckSensorState.Disabled;
    }

    // ==========================================
    // IControlModule 接口方法
    // ==========================================
    public bool HasAnyWarning => AlarmState.HasAnyWarning;
    public bool HasAnyError => State == CheckSensorState.Error;
    public string Name => _cfg.Name;

    public void Refresh(long currentTimestampMs)
    {
        _currentTimestampMs = currentTimestampMs;

        // 读取原生信号并进行防抖过滤
        _rawSignal = _cfg.ReadRawSignal();
        _filteredSignal = _debouncer.Filter(_rawSignal, currentTimestampMs);

        // 处理指令队列
        ProcessCommandQueue();

        // 评估报警逻辑
        EvaluateAlarms();

        // 状态机逻辑
        switch (State)
        {
            case CheckSensorState.Disabled:
                break;

            case CheckSensorState.Monitoring:
                break;

            case CheckSensorState.Error:
                break;
        }
    }

    public void ToSafe()
    {
        PurgeCommands();
        DisableMonitoring();
    }

    public void ExecuteCommand(InternalCommand command) => _commandQueue.Enqueue(command);

    // ==========================================
    // 外部控制接口
    // ==========================================
    public bool RawSignal => _rawSignal;
    public bool FilteredSignal => _filteredSignal;
    public CheckSensorState State { get; private set; } = CheckSensorState.Disabled;
    public CheckSensorAlarmState AlarmState { get; } = new();

    /// <summary>
    /// 动态改变期望状态并开启监控
    /// </summary>
    /// <param name="expected">期望传感器变成的状态 (ShouldBeOn / ShouldBeOff)</param>
    /// <param name="timeoutMs">可选：覆盖默认的超时时间</param>
    /// <param name="severity">可选：覆盖默认的报警严重程度 (Error/Warning)</param>
    public void SetExpectedState(ExpectedSignalState expected, long? timeoutMs = null, SeverityLevel? severity = null)
    {
        if (State == CheckSensorState.Error) return;

        _expectedState = expected;

        if(timeoutMs.HasValue)
            _currentTimeoutMs = timeoutMs.Value;

        if (severity.HasValue)
            _currentSeverity = severity.Value;

        // 每次重新设定目标时，清空之前的计时器
        _mismatchStartTimestampMs = null;

        if (_expectedState != ExpectedSignalState.Ignore)
        {
            ChangeState(CheckSensorState.Monitoring);
            _eventProducer.SendInfo(_cfg.Name, CheckSensorEvents.InfoMonitoringStarted,
                _expectedState.ToString(), _currentTimeoutMs, _currentSeverity.ToString());
        }
        else
        {
            DisableMonitoring();
        }
    }

    public void DisableMonitoring()
    {
        if (State == CheckSensorState.Error || State == CheckSensorState.Disabled) return;

        _expectedState = ExpectedSignalState.Ignore;
        _mismatchStartTimestampMs = null;
        ChangeState(CheckSensorState.Disabled);

        // 停止监控时，自动清理那些不需要手动确认的“警告”
        if (AlarmState.ShouldBeOnWarning) TryClearAlarm(CheckSensorEvents.WarningShouldBeOn);
        if (AlarmState.ShouldBeOffWarning) TryClearAlarm(CheckSensorEvents.WarningShouldBeOff);

        AlarmState.ShouldBeOnWarning = false;
        AlarmState.ShouldBeOffWarning = false;

        _eventProducer.SendInfo(_cfg.Name, CheckSensorEvents.InfoMonitoringDisabled);
    }

    public CheckSensorSnapshot GetSnapshot() => new()
    {
        Name = _cfg.Name,
        State = State,
        AlarmState = AlarmState,
        RawSignal = _rawSignal,
        FilteredSignal = _filteredSignal,
        ExpectedState = _expectedState,
        CurrentTimeoutMs = _currentTimeoutMs,
        CurrentSeverity = _currentSeverity,
        MismatchTimeElapsedMs = _mismatchStartTimestampMs.HasValue ? (_currentTimestampMs - _mismatchStartTimestampMs.Value) : 0
    };

    // ==========================================
    // 私有成员与核心逻辑
    // ==========================================
    private readonly ILogger<CM_CheckSensor> _logger;
    private readonly CheckSensorCfg _cfg;
    private readonly IEventProducer _eventProducer;
    private readonly Dictionary<int, (Guid guid, EventBase eventBase, object[] args)> _activeAlarms = new();
    private readonly ConcurrentQueue<InternalCommand> _commandQueue = new();
    private readonly Dictionary<Command, Action<InternalCommand>> _commandHandlers = new();

    private DigitalDebouncer _debouncer;
    private long _currentTimestampMs;
    private bool _rawSignal, _filteredSignal;

    private ExpectedSignalState _expectedState;
    private long _currentTimeoutMs;
    private SeverityLevel _currentSeverity;
    private long? _mismatchStartTimestampMs = null;

    private void ChangeState(CheckSensorState newState)
    {
        if (State == newState) return;
        State = newState;
    }

    private void EvaluateAlarms()
    {
        if (State == CheckSensorState.Disabled) return;

        if (_expectedState == ExpectedSignalState.ShouldBeOn)
        {
            if (!_filteredSignal)
            {
                // 信号为 False (不符合期望)，开始计时
                if (_mismatchStartTimestampMs == null)
                    _mismatchStartTimestampMs = _currentTimestampMs;

                if (_currentTimestampMs - _mismatchStartTimestampMs.Value > _currentTimeoutMs)
                {
                    if (_currentSeverity == SeverityLevel.Error)
                    {
                        AlarmState.ShouldBeOnError = true;
                        RaiseAlarm(CheckSensorEvents.ErrShouldBeOn, _currentTimeoutMs);
                    }
                    else
                    {
                        if (!AlarmState.ShouldBeOnWarning)
                        {
                            AlarmState.ShouldBeOnWarning = true;
                            RaiseAlarm(CheckSensorEvents.WarningShouldBeOn, _currentTimeoutMs);
                        }
                    }
                }
            }
            else
            {
                // 信号为 True (符合期望)，清空计时器与物理报警标志
                _mismatchStartTimestampMs = null;
                AlarmState.ShouldBeOnError = false;

                // 警告自动消警
                if (AlarmState.ShouldBeOnWarning)
                {
                    AlarmState.ShouldBeOnWarning = false;
                    TryClearAlarm(CheckSensorEvents.WarningShouldBeOn);
                }
            }
        }
        else if (_expectedState == ExpectedSignalState.ShouldBeOff)
        {
            if (_filteredSignal)
            {
                if (_mismatchStartTimestampMs == null)
                    _mismatchStartTimestampMs = _currentTimestampMs;

                if (_currentTimestampMs - _mismatchStartTimestampMs.Value > _currentTimeoutMs)
                {
                    if (_currentSeverity == SeverityLevel.Error)
                    {
                        AlarmState.ShouldBeOffError = true;
                        RaiseAlarm(CheckSensorEvents.ErrShouldBeOff, _currentTimeoutMs);
                    }
                    else
                    {
                        if (!AlarmState.ShouldBeOffWarning)
                        {
                            AlarmState.ShouldBeOffWarning = true;
                            RaiseAlarm(CheckSensorEvents.WarningShouldBeOff, _currentTimeoutMs);
                        }
                    }
                }
            }
            else
            {
                _mismatchStartTimestampMs = null;
                AlarmState.ShouldBeOffError = false;

                if (AlarmState.ShouldBeOffWarning)
                {
                    AlarmState.ShouldBeOffWarning = false;
                    TryClearAlarm(CheckSensorEvents.WarningShouldBeOff);
                }
            }
        }
        else
        {
            _mismatchStartTimestampMs = null;
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
            ChangeState(CheckSensorState.Error);
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
        if (State != CheckSensorState.Error) return;

        if (!AlarmState.ShouldBeOnError) TryClearAlarm(CheckSensorEvents.ErrShouldBeOn);
        if (!AlarmState.ShouldBeOffError) TryClearAlarm(CheckSensorEvents.ErrShouldBeOff);

        if (!AlarmState.HasAnyError)
        {
            ChangeState(CheckSensorState.Monitoring);
            _eventProducer.SendInfo(_cfg.Name, CheckSensorEvents.InfoReset);
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
        _commandHandlers[Command.Reset] = cmd =>
        {
            Reset();
            cmd.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Accepted, ""));
        };
    }
}

// ==========================================
// 配置类与专属状态类
// ==========================================
public enum CheckSensorState { Disabled, Monitoring, Error }
public enum ExpectedSignalState { Ignore, ShouldBeOn, ShouldBeOff }

public sealed class CheckSensorAlarmState
{
    public bool ShouldBeOnWarning { get; internal set; }
    public bool ShouldBeOffWarning { get; internal set; }
    public bool HasAnyWarning => ShouldBeOnWarning || ShouldBeOffWarning;

    public bool ShouldBeOnError { get; internal set; }
    public bool ShouldBeOffError { get; internal set; }
    public bool HasAnyError => ShouldBeOnError || ShouldBeOffError;

    public override string ToString() => $"OnWarn={ShouldBeOnWarning}, OffWarn={ShouldBeOffWarning}, OnErr={ShouldBeOnError}, OffErr={ShouldBeOffError}";
}

public class CheckSensorCfg
{
    public required string Name { get; init; }
    public required Func<bool> ReadRawSignal { get; init; }

    public long DebounceTimeMs { get; init; } = 50;
    public long DefaultMismatchTimeoutMs { get; init; } = 2000;

    public SeverityLevel DefaultMismatchSeverity { get; init; } = SeverityLevel.Error;

    public bool AutoStartMonitoring { get; init; } = false;
    public ExpectedSignalState DefaultExpectedState { get; init; } = ExpectedSignalState.Ignore;

    public bool Validate()
    {
        return !string.IsNullOrEmpty(Name) && ReadRawSignal != null;
    }
}

public sealed class CheckSensorSnapshot
{
    public required string Name { get; init; }
    public required CheckSensorState State { get; init; }
    public required CheckSensorAlarmState AlarmState { get; init; } = new();
    public required bool RawSignal { get; init; }
    public required bool FilteredSignal { get; init; }
    public required ExpectedSignalState ExpectedState { get; init; }
    public required long CurrentTimeoutMs { get; init; }
    public required SeverityLevel CurrentSeverity { get; init; }
    public required long MismatchTimeElapsedMs { get; init; }
}

public static class CheckSensorEvents
{
    public static readonly EventBase InfoMonitoringStarted = new() { EventId = 501, Severity = SeverityLevel.Info, MessageTemplate = "开启传感器监控 (期望: {0}, 超时: {1}ms, 等级: {2})" };
    public static readonly EventBase InfoMonitoringDisabled = new() { EventId = 502, Severity = SeverityLevel.Info, MessageTemplate = "停止传感器监控" };
    public static readonly EventBase InfoReset = new() { EventId = 503, Severity = SeverityLevel.Info, MessageTemplate = "传感器报警复位成功" };

    public static readonly EventBase ErrShouldBeOn = new() { EventId = 520, Severity = SeverityLevel.Error, MessageTemplate = "传感器未能在规定时间内闭合 (超时: {0}ms)" };
    public static readonly EventBase ErrShouldBeOff = new() { EventId = 521, Severity = SeverityLevel.Error, MessageTemplate = "传感器未能在规定时间内断开 (超时: {0}ms)" };

    public static readonly EventBase WarningShouldBeOn = new() { EventId = 540, Severity = SeverityLevel.Warning, MessageTemplate = "传感器意外断开警告 (超时: {0}ms)" };
    public static readonly EventBase WarningShouldBeOff = new() { EventId = 541, Severity = SeverityLevel.Warning, MessageTemplate = "传感器意外闭合警告 (超时: {0}ms)" };
}