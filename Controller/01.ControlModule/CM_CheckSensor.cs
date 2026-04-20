using Controller.Common;
using Controller.EventLogger;
using Controller.gRPC;
using Controller.S88;
using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Controller._01.ControlModule;

public class CM_CheckSensor : S88ControlModuleBase
{
    public CM_CheckSensor(CheckSensorCfg cfg, IEventProducer eventProducer, ILogger<CM_CheckSensor> logger) : base(cfg.Name, eventProducer, logger)
    {
        _cfg = cfg;
        RegisterCommandHandlers();

        // 初始化防抖器
        _debouncer = new DigitalDebouncer(_cfg.DebounceTimeMs);

        if (!_cfg.Validate())
            throw new ArgumentException($"CM_CheckSensor[{_cfg.Name}]配置不完整", nameof(_cfg));

        // 初始化状态与监控目标
        _expectedSignalState = _cfg.DefaultExpectedState;
        _currentTimeoutMs = _cfg.DefaultMismatchTimeoutMs;
        _currentSeverity = _cfg.DefaultMismatchSeverity;
        State = _cfg.AutoStartMonitoring ? CheckSensorState.Monitoring : CheckSensorState.Disabled;
    }

    // ==========================================
    // S88ControlModuleBase重写接口
    // ==========================================
    public override bool HasAnyWarning => AlarmState.HasAnyWarning;
    public override bool HasAnyError => State == CheckSensorState.Error;
    public override void Refresh(long currentTimestampMs)
    {
        _currentTimestampMs = currentTimestampMs;

        // 读取原生信号并进行防抖过滤
        _rawSignal = _cfg.ReadRawSignal();
        _filteredSignal = _debouncer.Filter(_rawSignal, currentTimestampMs);

        // 处理指令队列 (基类自带，坚决不要在此类重新定义 _commandQueue)
        ProcessCommandQueue();

        // 统一的报警集中评估与映射
        AlarmHandler();
    }
    public override void ToSafe()
    {
        PurgeCommands();
        DisableMonitoring();
    }

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
    public void SetExpectedState(ExpectedSignalState expected, long? timeoutMs = null, SeverityLevel? severity = null)
    {
        if (State == CheckSensorState.Error) return; // 故障态下拒绝改变监控目标

        _expectedSignalState = expected;

        if (timeoutMs.HasValue) _currentTimeoutMs = timeoutMs.Value;
        if (severity.HasValue) _currentSeverity = severity.Value;

        // 每次重新设定目标时，清空之前的计时器
        _mismatchStartTimestampMs = null;

        if (_expectedSignalState != ExpectedSignalState.Ignore)
        {
            ChangeState(CheckSensorState.Monitoring);
            RaiseInfo(CheckSensorEvents.InfoMonitoringStarted,
                _expectedSignalState.ToString(), _currentTimeoutMs, _currentSeverity.ToString());
        }
        else
        {
            DisableMonitoring();
        }
    }
    public void DisableMonitoring()
    {
        _expectedSignalState = ExpectedSignalState.Ignore;
        _mismatchStartTimestampMs = null;

        // Error 必须手动 Reset
        if (State != CheckSensorState.Error && State != CheckSensorState.Disabled)
        {
            ChangeState(CheckSensorState.Disabled);
            RaiseInfo(CheckSensorEvents.InfoMonitoringDisabled);
        }
    }
    public CheckSensorSnapshot GetSnapshot() => new()
    {
        Name = _cfg.Name,
        State = State,
        AlarmState = AlarmState,
        RawSignal = _rawSignal,
        FilteredSignal = _filteredSignal,
        ExpectedState = _expectedSignalState,
        CurrentTimeoutMs = _currentTimeoutMs,
        CurrentSeverity = _currentSeverity,
        MismatchTimeElapsedMs = _mismatchStartTimestampMs.HasValue ? (_currentTimestampMs - _mismatchStartTimestampMs.Value) : 0
    };

    // ==========================================
    // 私有成员与核心逻辑
    // ==========================================
    private readonly CheckSensorCfg _cfg;
    private DigitalDebouncer _debouncer;
    private long _currentTimestampMs;
    private bool _rawSignal, _filteredSignal;
    private ExpectedSignalState _expectedSignalState;
    private long _currentTimeoutMs;
    private SeverityLevel _currentSeverity;
    private long? _mismatchStartTimestampMs = null;
    private void AlarmHandler()
    {
        if (State == CheckSensorState.Monitoring)
        {
            if (_expectedSignalState == ExpectedSignalState.ShouldBeOn)
            {
                if (!_filteredSignal)
                {
                    if (_mismatchStartTimestampMs == null) _mismatchStartTimestampMs = _currentTimestampMs;

                    if (_currentTimestampMs - _mismatchStartTimestampMs.Value > _currentTimeoutMs)
                    {
                        if (_currentSeverity == SeverityLevel.Error)
                            AlarmState.ShouldBeOnError = true; 
                        else
                            AlarmState.ShouldBeOnWarning = true;
                    }
                }
                else
                {
                    _mismatchStartTimestampMs = null;
                    AlarmState.ShouldBeOnWarning = false;
                }
            }
            else if (_expectedSignalState == ExpectedSignalState.ShouldBeOff)
            {
                if (_filteredSignal)
                {
                    if (_mismatchStartTimestampMs == null) _mismatchStartTimestampMs = _currentTimestampMs;

                    if (_currentTimestampMs - _mismatchStartTimestampMs.Value > _currentTimeoutMs)
                    {
                        if (_currentSeverity == SeverityLevel.Error)
                            AlarmState.ShouldBeOffError = true;
                        else
                            AlarmState.ShouldBeOffWarning = true;
                    }
                }
                else
                {
                    _mismatchStartTimestampMs = null;
                    AlarmState.ShouldBeOffWarning = false;
                }
            }
        }
        else if (State == CheckSensorState.Disabled)
        {
            // 停止监控时，清理所有正在计时的状态和警告
            _mismatchStartTimestampMs = null;
            AlarmState.ShouldBeOnWarning = false;
            AlarmState.ShouldBeOffWarning = false;
        }

        if (AlarmState.ShouldBeOnError) RaiseAlarm(CheckSensorEvents.ErrShouldBeOn, _currentTimeoutMs);
        else TryClearAlarm(CheckSensorEvents.ErrShouldBeOn);

        if (AlarmState.ShouldBeOnWarning) RaiseAlarm(CheckSensorEvents.WarningShouldBeOn, _currentTimeoutMs);
        else TryClearAlarm(CheckSensorEvents.WarningShouldBeOn);

        if (AlarmState.ShouldBeOffError) RaiseAlarm(CheckSensorEvents.ErrShouldBeOff, _currentTimeoutMs);
        else TryClearAlarm(CheckSensorEvents.ErrShouldBeOff);

        if (AlarmState.ShouldBeOffWarning) RaiseAlarm(CheckSensorEvents.WarningShouldBeOff, _currentTimeoutMs);
        else TryClearAlarm(CheckSensorEvents.WarningShouldBeOff);

        if (AlarmState.HasAnyError && State != CheckSensorState.Error)
        {
            ChangeState(CheckSensorState.Error);
        }
    }
    private void Reset()
    {
        if (State != CheckSensorState.Error) return;

        // 只有当监控目标已改变，或者物理信号真的符合期望时，才允许清除报错
        if (_expectedSignalState != ExpectedSignalState.ShouldBeOn || _filteredSignal)
        {
            AlarmState.ShouldBeOnError = false;
        }

        if (_expectedSignalState != ExpectedSignalState.ShouldBeOff || !_filteredSignal)
        {
            AlarmState.ShouldBeOffError = false;
        }

        // 如果条件满足并清除了锁存
        if (!AlarmState.HasAnyError)
        {
            // 如果期望已经是 Ignore，则切回 Disabled，否则继续 Monitoring
            ChangeState(_expectedSignalState == ExpectedSignalState.Ignore ? CheckSensorState.Disabled : CheckSensorState.Monitoring);
            RaiseInfo(CheckSensorEvents.InfoReset);
        }
    }
    private void RegisterCommandHandlers()
    {
        RegisterCommandHandler(Command.Reset, cmd =>
        {
            Reset();
            cmd.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Accepted, ""));
        });
    }
    private void ChangeState(CheckSensorState newState)
    {
        if (State == newState) return;
        State = newState;
    }
    protected override void RaiseAlarm(EventBase eventbase, params object[] args)
    {
        base.RaiseAlarm(eventbase, args);
        if (eventbase.Severity == SeverityLevel.Error)
            ChangeState(CheckSensorState.Error);
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

public interface ICheckSensorFactory
{
    CM_CheckSensor Create(CheckSensorCfg cfg);
}

public class CheckSensorFactory : ICheckSensorFactory
{
    private readonly IServiceProvider _sp;
    public CheckSensorFactory(IServiceProvider sp) => _sp = sp;
    public CM_CheckSensor Create(CheckSensorCfg cfg) => ActivatorUtilities.CreateInstance<CM_CheckSensor>(_sp, cfg);
}
