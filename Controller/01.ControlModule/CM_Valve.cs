using Controller.Common;
using Controller.EventLogger;
using Controller.gRPC;
using Controller.S88;
using System.Collections.Concurrent;
using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Controller._01.ControlModule;

public class CM_Valve : S88ControlModuleBase
{
    public CM_Valve(IEventProducer eventProducer, ValveCfg cfg, ILogger<CM_Valve> logger) : base(cfg.Name, eventProducer, logger)
    {
        _cfg = cfg;
        RegisterCommandHandlers();

        //初始化防抖器
        long debounceTime = cfg.SensorDebounceTimeMs > 0 ? cfg.SensorDebounceTimeMs : 50;
        _openSensorFilter = new DigitalDebouncer(debounceTime);
        _closeSensorFilter = new DigitalDebouncer(debounceTime);

        if (!_cfg.Validate())
            throw new ArgumentException($"阀门[{_cfg.Name}]配置不完整", nameof(_cfg));
    }

    // ==========================================
    // S88ControlModuleBase重写接口
    // ==========================================
    public override bool HasAnyWarning => AlarmState.HasAnyWarning;
    public override bool HasAnyError => State == ValveState.Error;
    public override void Refresh(long currentTimestampMs)
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
                // 打开位传感器熄灭 + 处于关闭状态/或正在关闭且时间达标
                _isClosed = !_physicalOpen && (State == ValveState.Closed ||
                              (State == ValveState.ToCloseBusy && _toCloseElapsedTime >= _cfg.VirtualClosedDelayMs));
                break;

            case ValveSensorConfig.ClosedOnly:
                _isClosed = _physicalClosed;
                // 关闭位传感器熄灭 + 处于打开状态/或正在打开且时间达标
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

        // 硬件安全遮罩：如果处于 Error 态，拦截除 ToSafe 以外的所有新物理动作
        if (State == ValveState.Error && _targetCmd != ValveCmd.ToSafe)
        {
            // 处于故障态时，维持发生故障那一刻的物理输出，不响应新意图
        }
        else
        {
            switch (_targetCmd)
            {
                case ValveCmd.ToSafe:
                    _cfg.Actuate(ValveCmd.ToSafe);
                    break;
                case ValveCmd.ToClose:
                    _cfg.Actuate(ValveCmd.ToClose);
                    break;
                case ValveCmd.ToOpen:
                    _cfg.Actuate(ValveCmd.ToOpen);
                    break;
            }
        }

        // 状态机推演
        switch (State)
        {
            case ValveState.Unknown:
                if (_isOpen && !_isClosed) ChangeState(ValveState.Open);
                else if (!_isOpen && _isClosed) ChangeState(ValveState.Closed);
                // 如果意图是打开，或者意图是ToSafe且机械常态是常开
                else if (_targetCmd == ValveCmd.ToOpen ||
                        (_targetCmd == ValveCmd.ToSafe && _cfg.SafePhysicalState == ValveState.Open))
                {
                    ChangeState(ValveState.ToOpenBusy);
                    _toOpenStartTimestampMs = _currentTimestampMs;
                }
                // 如果意图是关闭，或者意图是ToSafe且机械常态是常闭
                else if (_targetCmd == ValveCmd.ToClose ||
                        (_targetCmd == ValveCmd.ToSafe && _cfg.SafePhysicalState == ValveState.Closed))
                {
                    ChangeState(ValveState.ToCloseBusy);
                    _toCloseStartTimestampMs = _currentTimestampMs;
                }
                // 如果 SafePhysicalState 是 Unknown (双电控阀掉电保持原位)，它就老老实实呆在 Unknown 等待传感器
                break;

            case ValveState.ToOpenBusy:
                _toOpenElapsedTime = _currentTimestampMs - _toOpenStartTimestampMs;
                if (_isOpen)
                {
                    _openCount++;
                    ChangeState(ValveState.Open);
                    RaiseInfo(ValveEvents.InfoOpenDone, _toOpenElapsedTime);
                }
                break;

            case ValveState.ToCloseBusy:
                _toCloseElapsedTime = _currentTimestampMs - _toCloseStartTimestampMs;
                if (_isClosed)
                {
                    _closeCount++;
                    ChangeState(ValveState.Closed);
                    RaiseInfo(ValveEvents.InfoClosedDone, _toCloseElapsedTime);
                }
                break;

            case ValveState.Open:
                // 如果信号丢失且没发生联锁错误，重新以此目标触发动作
                if (!_isOpen)
                {
                    RaiseInfo(ValveEvents.InfoOpenSensorLost);
                    ChangeState(ValveState.ToOpenBusy);
                    _toOpenStartTimestampMs = _currentTimestampMs;
                }
                break;

            case ValveState.Closed:
                // 如果信号丢失且没发生联锁错误，重新以此目标触发动作
                if (!_isClosed)
                {
                    RaiseInfo(ValveEvents.InfoCloseSensorLost);
                    ChangeState(ValveState.ToCloseBusy);
                    _toCloseStartTimestampMs = _currentTimestampMs;
                }
                break;

            case ValveState.Error:
                break;
        }

        // 统一的报警集中评估与映射
        AlarmHandler();
    }
    public override void ToSafe()
    {
        PurgeCommands();
        _targetCmd = ValveCmd.ToSafe;
        _cfg.Actuate(ValveCmd.ToSafe);

        if (State != ValveState.Error)
        {
            ChangeState(ValveState.Unknown);
        }
    }

    // ==========================================
    // 外部接口
    // ==========================================
    public void Close()
    {
        if (State == ValveState.Closed || State == ValveState.ToCloseBusy)
            return;

        if (!_cfg.CanClose())
        {
            AlarmState.CloseInterlockError = true;
            return;
        }

        _targetCmd = ValveCmd.ToClose; 

        if (State != ValveState.Error)
        {
            RaiseInfo(ValveEvents.InfoCmdClose);
            ChangeState(ValveState.ToCloseBusy);
            _toCloseStartTimestampMs = _currentTimestampMs;
        }
    }
    public void Open()
    {
        if (State == ValveState.Open || State == ValveState.ToOpenBusy)
            return;

        if (!_cfg.CanOpen())
        {
            AlarmState.OpenInterlockError = true;
            return;
        }

        _targetCmd = ValveCmd.ToOpen; 

        if (State != ValveState.Error)
        {
            RaiseInfo(ValveEvents.InfoCmdOpen);
            ChangeState(ValveState.ToOpenBusy);
            _toOpenStartTimestampMs = _currentTimestampMs;
        }
    }
    public ValveState State { get; private set; } = ValveState.Unknown;
    public ValveAlarmState AlarmState = new();
    public ValveSnapshot GetSnapshot() => new()
    {
        Name = _cfg.Name,
        TargetCmd = _targetCmd,
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
    private long _toOpenStartTimestampMs, _toCloseStartTimestampMs, _currentTimestampMs, _toCloseElapsedTime, _toOpenElapsedTime;
    private readonly ValveCfg _cfg;
    private DigitalDebouncer _openSensorFilter, _closeSensorFilter;
    private int _openCount, _closeCount;
    private bool _isOpen, _isClosed, _rawOpen, _rawClosed, _physicalOpen, _physicalClosed;
    private ValveCmd _targetCmd = ValveCmd.ToSafe;

    private void AlarmHandler()
    {
        AlarmState.LifeTimeReached = _openCount > _cfg.LifetimeSP;

        if (_isOpen && _isClosed) AlarmState.SensorConflict = true;

        if (State == ValveState.ToOpenBusy && _toOpenElapsedTime > _cfg.ToOpenToutMs)
            AlarmState.OpenTimeout = true;

        if (State == ValveState.ToCloseBusy && _toCloseElapsedTime > _cfg.ToCloseToutMs)
            AlarmState.CloseTimeout = true;

        // 动态联锁丢失错误锁存
        if (!_cfg.CanOpen() && _targetCmd != ValveCmd.ToSafe)
        {
            if (State == ValveState.ToOpenBusy) AlarmState.OpenInterlockLostError = true;
            else if (State == ValveState.Open) AlarmState.OpenKeepInterlockLostError = true;
        }

        if (!_cfg.CanClose() && _targetCmd != ValveCmd.ToSafe)
        {
            if (State == ValveState.ToCloseBusy) AlarmState.CloseInterlockLostError = true;
            else if (State == ValveState.Closed) AlarmState.CloseKeepInterlockLostError = true;
        }

        // 运行中联锁丢失，强制去安全位
        if (AlarmState.OpenInterlockLostError || AlarmState.OpenKeepInterlockLostError ||
            AlarmState.CloseInterlockLostError || AlarmState.CloseKeepInterlockLostError)
        {
            _targetCmd = ValveCmd.ToSafe;
        }

        if (AlarmState.LifeTimeReached) RaiseAlarm(ValveEvents.WarningLifetimeReached, _openCount, _cfg.LifetimeSP);
        else TryClearAlarm(ValveEvents.WarningLifetimeReached);

        if (AlarmState.SensorConflict) RaiseAlarm(ValveEvents.ErrSensorConflict);
        else TryClearAlarm(ValveEvents.ErrSensorConflict);

        if (AlarmState.OpenTimeout) RaiseAlarm(ValveEvents.ErrOpenTimeout, _cfg.ToOpenToutMs);
        else TryClearAlarm(ValveEvents.ErrOpenTimeout);

        if (AlarmState.CloseTimeout) RaiseAlarm(ValveEvents.ErrCloseTimeout, _cfg.ToCloseToutMs);
        else TryClearAlarm(ValveEvents.ErrCloseTimeout);

        if (AlarmState.OpenInterlockError) RaiseAlarm(ValveEvents.ErrOpenInterlock);
        else TryClearAlarm(ValveEvents.ErrOpenInterlock);

        if (AlarmState.OpenInterlockLostError) RaiseAlarm(ValveEvents.ErrOpenInterlockLost);
        else TryClearAlarm(ValveEvents.ErrOpenInterlockLost);

        if (AlarmState.OpenKeepInterlockLostError) RaiseAlarm(ValveEvents.ErrOpenKeepInterlockLost);
        else TryClearAlarm(ValveEvents.ErrOpenKeepInterlockLost);

        if (AlarmState.CloseInterlockError) RaiseAlarm(ValveEvents.ErrCloseInterlock);
        else TryClearAlarm(ValveEvents.ErrCloseInterlock);

        if (AlarmState.CloseInterlockLostError) RaiseAlarm(ValveEvents.ErrCloseInterlockLost);
        else TryClearAlarm(ValveEvents.ErrCloseInterlockLost);

        if (AlarmState.CloseKeepInterlockLostError) RaiseAlarm(ValveEvents.ErrCloseKeepInterlockLost);
        else TryClearAlarm(ValveEvents.ErrCloseKeepInterlockLost);

        if (AlarmState.HasAnyError && State != ValveState.Error)
        {
            ChangeState(ValveState.Error);
        }
    }

    private void Reset()
    {
        if (State != ValveState.Error) return;

        if (!(_isOpen && _isClosed))
            AlarmState.SensorConflict = false;

        // 打开超时清除条件：已物理打开 或 目标意图已改变
        if (_isOpen || _targetCmd != ValveCmd.ToOpen)
            AlarmState.OpenTimeout = false;

        // 关闭超时清除条件
        if (_isClosed || _targetCmd != ValveCmd.ToClose)
            AlarmState.CloseTimeout = false;

        // 外部联锁条件恢复时 或 目标意图已改变，允许清除对应的联锁报警
        if (_cfg.CanOpen() || _targetCmd != ValveCmd.ToOpen)
        {
            AlarmState.OpenInterlockLostError = false;
            AlarmState.OpenKeepInterlockLostError = false;
        }

        if (_cfg.CanClose() || _targetCmd != ValveCmd.ToClose)
        {
            AlarmState.CloseInterlockLostError = false;
            AlarmState.CloseKeepInterlockLostError = false;
        }

        AlarmState.CloseInterlockError = false;
        AlarmState.OpenInterlockError = false;

        // 如果所有的 Latch 都被成功清理，脱离 Error 态
        if (!AlarmState.HasAnyError)
        {
            ChangeState(ValveState.Unknown);
            RaiseInfo(ValveEvents.InfoReset);
        }
    }

    private void RegisterCommandHandlers()
    {
        RegisterCommandHandler(Command.Open, cmd => { Open(); cmd.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Accepted, string.Empty)); });
        RegisterCommandHandler(Command.Close, cmd => { Close(); cmd.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Accepted, string.Empty)); });
        RegisterCommandHandler(Command.Reset, cmd => { Reset(); cmd.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Accepted, string.Empty)); });
        RegisterCommandHandler(Command.ResetStatistics, cmd => { ResetStatistics(); cmd.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Accepted, string.Empty)); });
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
        RaiseInfo(ValveEvents.InfoClearStats);
    }

    protected override void RaiseAlarm(EventBase eventbase, params object[] args)
    {
        base.RaiseAlarm(eventbase, args);

        if (eventbase.Severity == SeverityLevel.Error)
            ChangeState(ValveState.Error);
    }
}

// ==============================================
// 配置、枚举与事件定义
// ==============================================
public enum ValveSensorConfig { DualSensors, OpenOnly, ClosedOnly, TimeBased }

public class ValveCfg
{
    public required string Name { get; init; }

    public ValveSensorConfig SensorConfig { get; init; } = ValveSensorConfig.TimeBased;

    public int VirtualOpenDelayMs { get; init; } = 10;
    public int VirtualClosedDelayMs { get; init; } = 10;
    public int ToOpenToutMs { get; init; } = 10000;
    public int ToCloseToutMs { get; init; } = 10000;
    public int LifetimeSP { get; init; } = 4000000;
    public int SensorDebounceTimeMs { get; init; } = 50;
    public ValveState SafePhysicalState { get; init; } = ValveState.Closed;

    public required Action<ValveCmd> Actuate { get; init; }
    public required Func<bool> CanOpen { get; init; }
    public required Func<bool> CanClose { get; init; }
    public Func<bool>? ReadOpenSensor { get; init; }
    public Func<bool>? ReadClosedSensor { get; init; }

    public bool Validate()
    {
        bool valid = !string.IsNullOrEmpty(Name) && Actuate != null && CanOpen != null && CanClose != null;
        if (SensorConfig is ValveSensorConfig.DualSensors or ValveSensorConfig.OpenOnly) valid &= ReadOpenSensor != null;
        if (SensorConfig is ValveSensorConfig.DualSensors or ValveSensorConfig.ClosedOnly) valid &= ReadClosedSensor != null;
        return valid;
    }
}

public enum ValveCmd { ToSafe, ToClose, ToOpen }
public enum ValveState { Unknown, ToOpenBusy, ToCloseBusy, Open, Closed, Error }

public sealed class ValveAlarmState
{
    public bool LifeTimeReached { get; internal set; }
    public bool HasAnyWarning => LifeTimeReached;

    public bool OpenInterlockError { get; internal set; }
    public bool CloseInterlockError { get; internal set; }
    public bool OpenInterlockLostError { get; internal set; }
    public bool CloseInterlockLostError { get; internal set; }
    public bool OpenKeepInterlockLostError { get; internal set; }
    public bool CloseKeepInterlockLostError { get; internal set; }

    public bool OpenTimeout { get; internal set; }
    public bool CloseTimeout { get; internal set; }
    public bool SensorConflict { get; internal set; }

    public bool HasAnyError => OpenInterlockError || CloseInterlockError ||
                               OpenInterlockLostError || CloseInterlockLostError ||
                               OpenKeepInterlockLostError || CloseKeepInterlockLostError ||
                               OpenTimeout || CloseTimeout || SensorConflict;
}

public static class ValveEvents
{
    public static readonly EventBase InfoClearStats = new() { EventId = 100, Severity = SeverityLevel.Info, MessageTemplate = "动作次数累计清零" };
    public static readonly EventBase InfoCmdClose = new() { EventId = 101, Severity = SeverityLevel.Info, MessageTemplate = "指令:开始关闭" };
    public static readonly EventBase InfoCmdOpen = new() { EventId = 102, Severity = SeverityLevel.Info, MessageTemplate = "指令:开始打开" };
    public static readonly EventBase InfoReset = new() { EventId = 103, Severity = SeverityLevel.Info, MessageTemplate = "故障复位完成" };
    public static readonly EventBase InfoOpenDone = new() { EventId = 104, Severity = SeverityLevel.Info, MessageTemplate = "打开到位 (耗时 {0} ms)" };
    public static readonly EventBase InfoClosedDone = new() { EventId = 105, Severity = SeverityLevel.Info, MessageTemplate = "关闭到位 (耗时 {0} ms)" };
    public static readonly EventBase InfoOpenSensorLost = new() { EventId = 106, Severity = SeverityLevel.Info, MessageTemplate = "打开位信号丢失，尝试维持" };
    public static readonly EventBase InfoCloseSensorLost = new() { EventId = 107, Severity = SeverityLevel.Info, MessageTemplate = "关闭位信号丢失，尝试维持" };

    public static readonly EventBase ErrCloseInterlock = new() { EventId = 120, Severity = SeverityLevel.Error, MessageTemplate = "无法关闭：外部联锁不满足" };
    public static readonly EventBase ErrOpenInterlock = new() { EventId = 121, Severity = SeverityLevel.Error, MessageTemplate = "无法打开：外部联锁不满足" };
    public static readonly EventBase ErrSensorConflict = new() { EventId = 122, Severity = SeverityLevel.Error, MessageTemplate = "传感器异常：打开位和关闭位传感器同时亮" };
    public static readonly EventBase ErrOpenInterlockLost = new() { EventId = 123, Severity = SeverityLevel.Error, MessageTemplate = "打开动作中联锁丢失" };
    public static readonly EventBase ErrOpenTimeout = new() { EventId = 124, Severity = SeverityLevel.Error, MessageTemplate = "打开动作超时 (> {0} ms)" };
    public static readonly EventBase ErrCloseInterlockLost = new() { EventId = 125, Severity = SeverityLevel.Error, MessageTemplate = "关闭动作中联锁丢失" };
    public static readonly EventBase ErrCloseTimeout = new() { EventId = 126, Severity = SeverityLevel.Error, MessageTemplate = "关闭动作超时 (> {0} ms)" };
    public static readonly EventBase ErrOpenKeepInterlockLost = new() { EventId = 127, Severity = SeverityLevel.Error, MessageTemplate = "打开保持中联锁丢失" };
    public static readonly EventBase ErrCloseKeepInterlockLost = new() { EventId = 128, Severity = SeverityLevel.Error, MessageTemplate = "关闭保持中联锁丢失" };
    public static readonly EventBase WarningLifetimeReached = new() { EventId = 140, Severity = SeverityLevel.Warning, MessageTemplate = "寿命到达 (PV:{0} , SP:{1})" };
}

public sealed class ValveSnapshot
{
    public required string Name { get; init; }
    public required ValveCmd TargetCmd { get; init; }
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
