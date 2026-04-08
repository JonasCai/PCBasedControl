using Controller.Common;
using Controller.EventLogger;
using Controller.gRPC;
using Controller.S88;
using System.Collections.Concurrent;

namespace Controller._01.ControlModule;

public class CM_TempController : IControlModule
{
    public CM_TempController(IEventProducer eventProducer, TempControllerCfg cfg, ILogger<CM_TempController> logger)
    {
        _eventProducer = eventProducer;
        _cfg = cfg;
        _logger = logger;
        _timeProportioning = new(TimeSpan.FromMilliseconds(_cfg.TimeProportioningCTMs), Environment.TickCount64);

        _pid.DeadBand = _cfg.PidDeadBand;
        _pid.IntegralSeparationBand = _cfg.PidIntegralSeparationBand;
        _pid.OutputRampRatePerSecond = _cfg.PidIntegralSeparationBand;
        _autoTuner.MaxSafeTemperature = _cfg.AbsoluteMaxTempLimit;

        if (!_cfg.Validate())
            throw new ArgumentException($"温控模块 [{_cfg.Name}] 配置不完整", nameof(_cfg));

        RegisterCommandHandlers();
    }

    // ==========================================
    // IControlModule 接口方法
    // ==========================================
    public string Name => _cfg.Name;
    public bool HasAnyError => State == TempControllerState.Error;
    public bool HasAnyWarning => AlarmState.HasAnyWarning;
    public void Refresh(long currentTimestampMs)
    {
        _currentTimestampMs = currentTimestampMs;

        // 读取传感器状态
        _monitorTemperature = _cfg.ReadMonitorTemp != null ? _cfg.ReadMonitorTemp() : null;
        _thisRawTemperature = _cfg.ReadControlTemp();
        _thisFilteredTemperature = _filter.Filter(_thisRawTemperature, _cfg.FilterAlpha);

        // 处理指令队列
        ProcessCommandQueue();

        // 安全及报警检查
        EvaluateAlarms();

        // 状态机逻辑
        switch (State)
        {
            case TempControllerState.Error:
            case TempControllerState.Disabled:
                _thisPidOutputPercent = 0f;
                _pid.Reset(_currentTimestampMs, _thisFilteredTemperature, 0);
                _timeProportioning.Reset(_currentTimestampMs);
                break;

            case TempControllerState.Manual:
                _thisPidOutputPercent = _pid.Compute(_thisFilteredTemperature, _currentTimestampMs);
                break;

            case TempControllerState.NormalPid:
                // 第一次进入，或者达到了设定的间隔
                if (!_lastPidComputeTimestamMs.HasValue || (_currentTimestampMs - _lastPidComputeTimestamMs.Value) >= _cfg.PidComputeIntervalMs)
                {
                    _thisPidOutputPercent = _pid.Compute(_thisFilteredTemperature, _currentTimestampMs);
                    _lastPidComputeTimestamMs = _currentTimestampMs;
                }
                else
                {
                    _thisPidOutputPercent = _lastPidOutputPercent;// 沿用上一个周期的输出值
                }
                break;

            case TempControllerState.AutoTune:
                // 第一次进入，或者达到了设定的间隔
                if (!_lastPidComputeTimestamMs.HasValue || (_currentTimestampMs - _lastPidComputeTimestamMs.Value) >= _cfg.PidComputeIntervalMs)
                {
                    _autoTuner.Update(_thisFilteredTemperature, _currentTimestampMs);
                    HandleAutoTuneStateAfterUpdate();

                    if (State != TempControllerState.AutoTune)// 自整定结束，直接打断当前帧的输出覆盖
                        break;

                    _thisPidOutputPercent = (float)_autoTuner.CurrentOutputPercent;
                    _lastPidComputeTimestamMs = _currentTimestampMs;
                }
                else
                {
                    _thisPidOutputPercent = _lastPidOutputPercent;// 沿用上一个周期的输出值
                }
                break;
        }

        // 周期更新温控逻辑
        _cfg.SetDutyRatio?.Invoke(_thisPidOutputPercent);
        if (_cfg.SetHeaterOn != null)
        {
            _timeProportioning.OutputPercent = _thisPidOutputPercent;
            _thisHeaterOn = _timeProportioning.GetOutputState(_currentTimestampMs);
            _cfg.SetHeaterOn(_thisHeaterOn);
        }

        _lastPidOutputPercent = _thisPidOutputPercent;

    }
    public void ToSafe()
    {
        Stop();
        PurgeCommands();
        _cfg.SetHeaterOn?.Invoke(false);
        _cfg.SetDutyRatio?.Invoke(0f);
    }
    public void ExecuteCommand(InternalCommand command) => _commandQueue.Enqueue(command);

    // ==========================================
    // 外部接口 / 状态查询
    // ==========================================
    public TempControllerState State { get; private set; } = TempControllerState.Disabled;
    public TempControllerAlarmState AlarmState { get; } = new();
    public bool PidTargetReached { get; private set; }
    public bool PidDevCheck { get; set; }
    public float PidSetpoint
    {
        get => _pid.Setpoint;
        set
        {
            if (_pid.Setpoint != value)
            {
                var oldValue = _pid.Setpoint;
                _pid.Setpoint = value;
                _eventProducer.SendInfo(_cfg.Name, TempControllerEvents.InfoPidSPChanged, oldValue, value);
            }
        }
    }
    public float ManualOutputPercent
    {
        get => _manualOutputPercent;
        set
        {
            var newValue = Math.Clamp(value, 0f, 100f);
            if (_manualOutputPercent != newValue)
            {
                var oldValue = _manualOutputPercent;
                _manualOutputPercent = value;
                _eventProducer.SendInfo(_cfg.Name, TempControllerEvents.InfoPidManualOutputChanged, oldValue, newValue);
            }
        }
    }
    public void Start(float? sp = null)
    {
        if (State != TempControllerState.Disabled)
            return;

        if (sp.HasValue)
            PidSetpoint = sp.Value;

        _eventProducer.SendInfo(_cfg.Name, TempControllerEvents.InfoPidStart, PidSetpoint);
        ChangeState(TempControllerState.NormalPid);
    }
    public void Stop()
    {
        if (State == TempControllerState.Disabled || State == TempControllerState.Error)
            return;
        _eventProducer.SendInfo(_cfg.Name, TempControllerEvents.InfoPidStop);
        ChangeState(TempControllerState.Disabled);
    }
    public void SwitchToNormalPid(float? sp = null)
    {
        if (State == TempControllerState.Disabled || State == TempControllerState.NormalPid || State == TempControllerState.Error)
            return;
        if (sp.HasValue)
            PidSetpoint = sp.Value;
        ChangeState(TempControllerState.NormalPid);
    }
    public void SwitchToManual(float? output = null)
    {
        if (State == TempControllerState.Disabled || State == TempControllerState.Manual || State == TempControllerState.Error)
            return;
        if (output.HasValue)
            ManualOutputPercent = output.Value;
        ChangeState(TempControllerState.Manual);
    }
    public TempControllerSnapshot GetSnapshot() => new()
    {
        Name = _cfg.Name,
        State = State,
        AlarmState = AlarmState,
        DutyRatio = _thisPidOutputPercent,
        RawTemperature = _thisRawTemperature,
        FilteredTemperature = _thisFilteredTemperature,
        Setpoint = PidSetpoint,
        Kp = _pid.Kp,
        Ki = _pid.Ki,
        Kd = _pid.Kd,
        HeaterOn = _thisHeaterOn
    };

    // ==========================================
    // 私有成员与内部类
    // ==========================================
    private readonly ILogger<CM_TempController> _logger;
    private readonly TempControllerCfg _cfg;
    private readonly IEventProducer _eventProducer;
    private readonly ConcurrentQueue<InternalCommand> _commandQueue = new();
    private readonly Dictionary<Command, Action<InternalCommand>> _commandHandlers = new();
    private readonly Dictionary<int, (Guid guid, EventBase eventBase, object[] args)> _activeAlarms = new();// 用于追踪报警状态以便能够清除报警
    private readonly TemperaturePidController _pid = new();
    private readonly RelayAutoTuner _autoTuner = new();
    private readonly TimeProportioningOutput _timeProportioning;
    private readonly LowPassFilter _filter = new();
    private float _lastPidOutputPercent, _thisFilteredTemperature, _thisPidOutputPercent, _thisRawTemperature, _thisError, _manualOutputPercent;
    private float? _monitorTemperature;
    private long _currentTimestampMs;
    private bool _thisHeaterOn = false;
    private long? _lastPidComputeTimestamMs = null;
    private void ProcessCommandQueue()
    {
        while (_commandQueue.TryDequeue(out var cmd))
        {
            if (cmd.CancelToken.IsCancellationRequested)
            {
                _logger.LogWarning("指令 {TargetUnit}.{TargetObject}.{CmdName} 已失效丢弃", cmd.TargetUnit, cmd.TargetObject, cmd.CmdName);
                continue;
            }

            if (_commandHandlers.TryGetValue(cmd.CmdName, out var handler))
            {
                handler(cmd);
            }
            else
            {
                cmd.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Rejected, $"不支持的指令: {cmd.CmdName}"));
            }
        }
    }
    private void PurgeCommands()
    {
        while (_commandQueue.TryDequeue(out var cmd))
        {
            cmd.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Rejected, "被系统强制清理"));
        }
    }
    private void RegisterCommandHandlers()
    {
        _commandHandlers[Command.Start] = cmd =>
        {
            if (cmd.Params.Count > 0)
            {
                if (float.TryParse(cmd.Params.Values.First(), out var sp))
                    Start(sp);
                else
                    Start();
            }
            else
            {
                Start();
            }
            cmd.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Accepted, string.Empty));
        };
        _commandHandlers[Command.ChangeSP] = cmd =>
        {
            if (cmd.Params.Count > 0 && float.TryParse(cmd.Params.Values.First(), out var sp))
            {
                PidSetpoint = sp;
                cmd.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Accepted, string.Empty));
                return;
            }
            cmd.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Rejected, "缺少设定值参数"));
        };

        _commandHandlers[Command.ChangeManualOutput] = cmd =>
        {
            if (cmd.Params.Count > 0 && float.TryParse(cmd.Params.Values.First(), out var output))
            {
                ManualOutputPercent = output;
                cmd.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Accepted, string.Empty));
                return;
            }
            cmd.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Rejected, "缺少手动输出参数"));
        };

        _commandHandlers[Command.SetPID] = cmd =>
        {
            if (cmd.Params.TryGetValue("P", out var p) && cmd.Params.TryGetValue("I", out var i) && cmd.Params.TryGetValue("D", out var d))
            {
                if (float.TryParse(p, out var kp) && float.TryParse(i, out var ki) && float.TryParse(d, out var kd))
                {
                    SetPid(kp, ki, kd);
                    cmd.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Accepted, string.Empty));
                    return;
                }
            }
            cmd.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Rejected, "P/I/D参数不完整或者格式错误"));
        };

        _commandHandlers[Command.Stop] = cmd =>
        {
            Stop();
            cmd.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Accepted, string.Empty));
        };

        _commandHandlers[Command.Reset] = cmd =>
        {
            Reset();
            cmd.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Accepted, string.Empty));
        };

        _commandHandlers[Command.SwitchToManual] = cmd =>
        {
            if (cmd.Params.Count > 0)
            {
                if (float.TryParse(cmd.Params.Values.First(), out var output))
                    SwitchToManual(output);
                else
                    SwitchToManual();
            }
            else
            {
                SwitchToManual();
            }
            cmd.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Accepted, string.Empty));
        };

        _commandHandlers[Command.SwitchToNormalPid] = cmd =>
        {
            if (cmd.Params.Count > 0)
            {
                if (float.TryParse(cmd.Params.Values.First(), out var sp))
                    SwitchToNormalPid(sp);
                else
                    SwitchToNormalPid();
            }
            else
            {
                SwitchToNormalPid();
            }
            cmd.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Accepted, string.Empty));
        };

        _commandHandlers[Command.SwitchToAutoTune] = cmd =>
        {
            if (cmd.Params.Count > 0)
            {
                if (float.TryParse(cmd.Params.Values.First(), out var sp))
                    SwitchToAutoTune(sp);
                else
                    SwitchToAutoTune();
            }
            else
            {
                SwitchToAutoTune();
            }
            cmd.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Accepted, string.Empty));
        };

        _commandHandlers[Command.StopAutoTune] = cmd =>
        {
            if (State == TempControllerState.AutoTune && _autoTuner.Status == AutoTuneStatus.Running)
                _autoTuner.Cancel();
            cmd.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Accepted, string.Empty));
        };
    }
    private void ChangeState(TempControllerState newState)
    {
        if (State == newState) return;
        switch (newState)
        {
            case TempControllerState.Disabled:
                _timeProportioning.OutputPercent = 0f;
                _pid.SwitchToManual(0f);
                if (State == TempControllerState.AutoTune)
                    _autoTuner.Reset();
                break;

            case TempControllerState.Manual:
                _pid.SwitchToManual(ManualOutputPercent);
                _timeProportioning.Reset(_currentTimestampMs);
                if (State == TempControllerState.AutoTune)
                    _autoTuner.Reset();
                break;

            case TempControllerState.NormalPid:
                _pid.SwitchToAuto(_currentTimestampMs, _lastPidOutputPercent, _thisFilteredTemperature);
                _timeProportioning.Reset(_currentTimestampMs);
                _lastPidComputeTimestamMs = null; // 确保立即执行一次 PID 计算
                if (State == TempControllerState.AutoTune)
                    _autoTuner.Reset();
                break;

            case TempControllerState.AutoTune:
                _pid.SwitchToManual(0f);
                _autoTuner.Setpoint = _pid.Setpoint;
                _autoTuner.Start(_cfg.RelayAutoTuneOptions, _currentTimestampMs);
                _timeProportioning.Reset(_currentTimestampMs);
                _lastPidComputeTimestamMs = null;// 确保立即执行一次 Update 计算
                break;
        }
        TempControllerState oldState = State;
        State = newState;
        _eventProducer.SendInfo(_cfg.Name, TempControllerEvents.InfoStateChanged, oldState, newState);
    }
    private void HandleAutoTuneStateAfterUpdate()
    {
        switch (_autoTuner.Status)
        {
            case AutoTuneStatus.Running:
                break;

            case AutoTuneStatus.Succeeded:
                if (_autoTuner.Result?.Success == true)
                {
                    _eventProducer.SendInfo(_cfg.Name, TempControllerEvents.InfoAutoTuneSucceed);
                    SetPid((float)_autoTuner.Result.Kp, (float)_autoTuner.Result.Ki, (float)_autoTuner.Result.Kd);
                }
                SwitchToNormalPid();
                break;

            case AutoTuneStatus.Failed:
            case AutoTuneStatus.Cancelled:
                if (_autoTuner.Result?.Success == false)
                {
                    _eventProducer.SendInfo(_cfg.Name, TempControllerEvents.InfoAutoTuneFailedOrCanceled, _autoTuner.Result.Message);
                }
                SwitchToNormalPid();
                break;
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

        ChangeState(TempControllerState.Error);
    }
    private void ClearAlarm(EventBase eventbase)
    {
        if (_activeAlarms.Remove(eventbase.EventId, out var alarm))
        {
            _eventProducer.ClearAlarm(_cfg.Name, alarm.guid, alarm.eventBase, alarm.args);
        }
    }
    private void Reset()
    {
        if (State != TempControllerState.Error) return;

        if (!AlarmState.PVOverLimitError)
        {
            ClearAlarm(TempControllerEvents.ErrPVOverLimit);
        }

        if (!AlarmState.HighHighDevError)
        {
            ClearAlarm(TempControllerEvents.ErrHighHighDev);
        }

        if (!AlarmState.HighDevWarning)
        {
            ClearAlarm(TempControllerEvents.WarningHighDev);
        }

        if (!AlarmState.ExecuteConditionsNotMetError)
        {
            ClearAlarm(TempControllerEvents.ErrExecuteConditionsNotMet);
        }

        if (!AlarmState.HasAnyError)
        {
            ChangeState(TempControllerState.Disabled);
        }

        _eventProducer.SendInfo(_cfg.Name, TempControllerEvents.InfoPidReset);
    }
    private void SwitchToAutoTune(float? sp = null)
    {
        if (State == TempControllerState.Disabled || State == TempControllerState.AutoTune || State == TempControllerState.Error)
            return;
        if (sp.HasValue)
            PidSetpoint = sp.Value;
        ChangeState(TempControllerState.AutoTune);
    }
    private void SetPid(float Kp, float Ki, float Kd)
    {
        var oldKp = _pid.Kp;
        var oldKi = _pid.Ki;
        var oldKd = _pid.Kd;
        _pid.Kp = Kp;
        _pid.Ki = Ki;
        _pid.Kd = Kd;
        _eventProducer.SendInfo(_cfg.Name, TempControllerEvents.InfoPidParaChanged, oldKp, Kp, oldKi, Ki, oldKd, Kd);
    }
    private void EvaluateAlarms()
    {
        // 极值报警
        if (_thisRawTemperature > _cfg.AbsoluteMaxTempLimit || _thisRawTemperature < _cfg.AbsoluteMinTempLimit || (_monitorTemperature.HasValue && (_monitorTemperature > _cfg.AbsoluteMaxTempLimit || _monitorTemperature < _cfg.AbsoluteMinTempLimit)))
        {
            AlarmState.PVOverLimitError = true;
            RaiseAlarm(TempControllerEvents.ErrPVOverLimit, _thisRawTemperature, _monitorTemperature ?? float.NaN, _cfg.AbsoluteMinTempLimit, _cfg.AbsoluteMaxTempLimit);
        }
        else { AlarmState.PVOverLimitError = false; }

        // 偏差报警
        if (State == TempControllerState.NormalPid)
        {
            _thisError = Math.Abs(PidSetpoint - _thisRawTemperature);
            PidTargetReached = _thisError <= _cfg.PidTolerance;
            if (_thisError > _cfg.PidErrorDev && PidDevCheck)
            {
                AlarmState.HighHighDevError = true;
                RaiseAlarm(TempControllerEvents.ErrHighHighDev, _thisRawTemperature, PidSetpoint);
            }
            else { AlarmState.HighHighDevError = false; }

            if (_thisError > _cfg.PidWarningDev && _thisError <= _cfg.PidErrorDev && PidDevCheck)
            {
                AlarmState.HighDevWarning = true;
                RaiseAlarm(TempControllerEvents.WarningHighDev, _thisRawTemperature, PidSetpoint);
            }
            else
            {
                AlarmState.HighDevWarning = false;
                ClearAlarm(TempControllerEvents.WarningHighDev);//警告类型自动清除
            }
        }
        else { PidTargetReached = false; }

        // 联锁报警
        if (!_cfg.CanExecute())
        {
            AlarmState.ExecuteConditionsNotMetError = true;
            RaiseAlarm(TempControllerEvents.ErrExecuteConditionsNotMet);
        }
        else { AlarmState.ExecuteConditionsNotMetError = false; }
    }
}

public class TempControllerCfg
{
    public required string Name { get; init; }
    public required Func<bool> CanExecute { get; init; }
    public required Func<float> ReadControlTemp { get; init; }
    public Func<float>? ReadMonitorTemp { get; init; }
    public Action<bool>? SetHeaterOn { get; init; }
    public Action<float>? SetDutyRatio { get; init; }
    public float PidTolerance { get; init; } = 1.0f;
    public float PidWarningDev { get; init; } = 2.0f;
    public float PidErrorDev { get; init; } = 5.0f;
    public float AbsoluteMaxTempLimit { get; init; } = 200.0f;  // 绝对最高安全温度上限 (°C)
    public float AbsoluteMinTempLimit { get; init; } = 0.0f;  // 绝对最高安全温度下限 (°C)
    public uint TimeProportioningCTMs { get; init; } = 2000;
    public uint PidComputeIntervalMs { get; init; } = 500;
    public float PidIntegralSeparationBand { get; set; } = 5.0f;
    public float PidDeadBand { get; set; } = 0.2f;
    public float PidOutputRampRatePerSecond { get; set; } = 20.0f;
    public float FilterAlpha { get; init; } = 0.2f;
    public RelayAutoTuneOptions RelayAutoTuneOptions { get; init; } = new();

    public bool Validate()
    {
        return !string.IsNullOrEmpty(Name) &&
               ReadControlTemp != null &&
               (SetHeaterOn != null ||
               SetDutyRatio != null);
    }
}
public static class TempControllerEvents
{
    public static readonly EventBase InfoStateChanged = new() { EventId = 200, Severity = SeverityLevel.Info, MessageTemplate = "状态切换 ({0} -> {1})" };
    public static readonly EventBase InfoPidSPChanged = new() { EventId = 201, Severity = SeverityLevel.Info, MessageTemplate = "Pid设定值变化 ({0:F1} -> {1:F1})" };
    public static readonly EventBase InfoPidManualOutputChanged = new() { EventId = 202, Severity = SeverityLevel.Info, MessageTemplate = "Pid手动输出值变化 ({0:F1} -> {1:F1})" };
    public static readonly EventBase InfoPidStart = new() { EventId = 203, Severity = SeverityLevel.Info, MessageTemplate = "Pid启动触发 (SP: {0:F1})" };
    public static readonly EventBase InfoPidStop = new() { EventId = 204, Severity = SeverityLevel.Info, MessageTemplate = "Pid停止触发" };
    public static readonly EventBase InfoPidParaChanged = new() { EventId = 205, Severity = SeverityLevel.Info, MessageTemplate = "Pid参数变化 (Kp: {0} -> {1}, Ki: {2} -> {3}, Kd: {4} -> {5})" };
    public static readonly EventBase InfoPidReset = new() { EventId = 206, Severity = SeverityLevel.Info, MessageTemplate = "Pid错误复位触发" };
    public static readonly EventBase InfoAutoTuneSucceed = new() { EventId = 207, Severity = SeverityLevel.Info, MessageTemplate = "Pid参数自整定成功" };
    public static readonly EventBase InfoAutoTuneFailedOrCanceled = new() { EventId = 208, Severity = SeverityLevel.Info, MessageTemplate = "Pid参数自整定失败或者被取消 ({0})" };

    public static readonly EventBase ErrPVOverLimit = new() { EventId = 220, Severity = SeverityLevel.Error, MessageTemplate = "当前温度超出最大限制 (ControlPV：{0:F1} ,MonitorlPV：{1:F1} ,MinLimit: {2:F1}, MaxLimit: {3:F1})" };
    public static readonly EventBase ErrHighHighDev = new() { EventId = 221, Severity = SeverityLevel.Error, MessageTemplate = "温度高高偏差错误 (PV: {0:F1} , SP: {1:F1})" };
    public static readonly EventBase ErrExecuteConditionsNotMet = new() { EventId = 222, Severity = SeverityLevel.Error, MessageTemplate = "安全联锁触发" };

    public static readonly EventBase WarningHighDev = new() { EventId = 240, Severity = SeverityLevel.Warning, MessageTemplate = "温度高偏差警告 (PV: {0:F1} , SP: {1:F1})" };
}
public sealed class TempControllerAlarmState
{
    public bool HighDevWarning { get; internal set; }
    public bool HasAnyWarning => HighDevWarning;
    public bool PVOverLimitError { get; internal set; }
    public bool HighHighDevError { get; internal set; }
    public bool ExecuteConditionsNotMetError { get; internal set; }
    public bool HasAnyError => PVOverLimitError || HighHighDevError || ExecuteConditionsNotMetError;
    public override string ToString() => $"PVOverLimitError={PVOverLimitError}, HighHighDevError={HighHighDevError}, ExecuteConditionsNotMetError={ExecuteConditionsNotMetError}, HighDevWarning={HighDevWarning}";
}
public interface ITempControllerFactory
{
    CM_TempController Create(TempControllerCfg cfg);
}
public class TempControllerFactory : ITempControllerFactory
{
    private readonly IServiceProvider _sp;
    public TempControllerFactory(IServiceProvider sp) => _sp = sp;
    public CM_TempController Create(TempControllerCfg cfg) => ActivatorUtilities.CreateInstance<CM_TempController>(_sp, cfg);
}

// ==========================================
// 底层 PID 与温控逻辑核心
// ==========================================
#region Common Enums
public enum TempControllerState { Disabled, Manual, NormalPid, AutoTune, Error }
public enum AutoTuneRule
{
    /// <summary>
    /// 控制风格：激进、快速、抗干扰强。
    /// 特点：它的数学目标是达到经典的 “1/4 衰减比”（即第一个超调波峰是第二个波峰的 4 倍）。这意味着它一定会产生超调，而且初始震荡比较明显，但系统会用最快的速度逼近设定值。
    /// 适用场景：
    /// 对超调不敏感的系统。
    /// 需要极速响应的系统（例如：伺服电机位置控制、无滞后的张力控制）。
    /// 外部干扰非常大，需要 PID 瞬间输出巨大力量拉回设定的场景。
    /// </summary>
    ZieglerNicholsPid,

    /// <summary>
    /// 控制风格：保守、平稳、高鲁棒性。
    /// 特点：计算出的比例系数 (K_p) 较小，积分时间更长。它的目标是尽量不产生超调，或者产生极小的超调，让系统平滑地到达目标值。
    /// 适用场景：
    /// 温度控制（强烈推荐）：因为大多数加热系统“升温容易降温难”，一旦超调，只能靠自然散热，恢复极慢。T-L 法能完美避免这种麻烦。
    /// 大纯滞后系统：从执行器动作到传感器有明显延迟的系统（比如大型加热炉）。
    /// 对安全性要求极高、不允许产生剧烈震荡的工业现场。
    /// </summary>
    TyreusLuybenPid,

    /// <summary>
    /// 控制风格：抗噪、温和。
    /// 特点：微分项 ($K_d$) 对信号的“变化率”极其敏感。如果你的传感器数据有细微的抖动或高频噪声（比如水波纹导致液位计跳动），D 项会把这些噪声放大无数倍，导致继电器或阀门疯狂开合。PI 规则彻底去掉了 D 项，只用 P 和 I 来控制。
    /// 适用场景：
    /// 传感器噪声极大的系统：如果你的 rawTemperature 跳动很厉害，即使滤波后也不太安分。
    /// 流量控制 / 液位控制：流体系统天生伴随湍流和波动，行业内通常只用 PI 控制。
    /// 当你发现 PID 控制器让执行器（如固态继电器 SSR、阀门）动作过于频繁且剧烈时，降级使用 PI 是最好的解法。
    /// </summary>
    ZieglerNicholsPi
}
public enum AutoTuneStatus { Idle, Running, Succeeded, Failed, Cancelled }
internal enum PeakType { Unknown, Max, Min }
#endregion

#region PID
public sealed class TemperaturePidController
{
    private float _integral;
    private float _lastInput;
    private bool _hasLastInput;
    private float _lastOutput;
    private long _lastTickMs;//记录上一次计算的 Tick
    public float Kp { get; set; } = 8;
    public float Ki { get; set; } = 0.4f;
    public float Kd { get; set; } = 10;
    public float Setpoint { get; set; }
    public bool AutoMode { get; private set; } = true;
    public float ManualOutput { get; private set; }
    public float OutputMin { get; set; } = 0.0f;
    public float OutputMax { get; set; } = 100.0f;
    public float IntegralMin { get; set; } = -100.0f;
    public float IntegralMax { get; set; } = 100.0f;
    public float IntegralSeparationBand { get; set; } = 5.0f;
    public float DeadBand { get; set; } = 0.2f;
    public float OutputRampRatePerSecond { get; set; } = 20.0f;

    public void Reset(long currentTickMs, float currentTemperature = 0, float initialOutput = 0)
    {
        _integral = 0;
        _lastInput = currentTemperature;
        _hasLastInput = true;
        _lastOutput = Math.Clamp(initialOutput, OutputMin, OutputMax);
        _lastTickMs = currentTickMs;
    }

    public float Compute(float currentTemperature, long currentTickMs)
    {
        float dtSeconds = (currentTickMs - _lastTickMs) / 1000f;

        // 如果时间没有推进（例如在同一毫秒内被调用了两次），直接返回上一次的值，防止除以 0
        if (dtSeconds <= 0)
            return _lastOutput;

        _lastTickMs = currentTickMs;
        if (!AutoMode)
        {
            _lastOutput = Math.Clamp(ManualOutput, OutputMin, OutputMax);
            _lastInput = currentTemperature;
            _hasLastInput = true;
            return _lastOutput;
        }

        float rawError = Setpoint - currentTemperature;
        float activeError = CalculateActiveError(rawError); // 避免死区跳变

        // 比例项
        float p = Kp * activeError;

        // 积分项
        if (Math.Abs(activeError) <= IntegralSeparationBand)
        {
            float iDelta = Ki * activeError * dtSeconds;

            // 抗积分饱和
            bool isSaturated = (_lastOutput >= OutputMax && iDelta > 0) ||
                (_lastOutput <= OutputMin && iDelta < 0);

            if (!isSaturated)
            {
                _integral += iDelta;
                _integral = Math.Clamp(_integral, IntegralMin, IntegralMax);
            }
        }

        // 微分项
        float d = 0;
        if (_hasLastInput)
        {
            // 微分先行，抗设定值跳变
            float dInput = (currentTemperature - _lastInput) / dtSeconds;
            d = -Kd * dInput;
        }

        float rawOutput = p + _integral + d;
        rawOutput = Math.Clamp(rawOutput, OutputMin, OutputMax);

        // 限制变化率
        float finalOutput = ApplyRampLimit(_lastOutput, rawOutput, dtSeconds);
        _lastOutput = Math.Clamp(finalOutput, OutputMin, OutputMax);
        _lastInput = currentTemperature;
        _hasLastInput = true;
        return _lastOutput;
    }

    public void SwitchToAuto(long currentTickMs, float currentOutput, float currentTemperature)
    {
        if (AutoMode) return;
        AutoMode = true;
        _lastInput = currentTemperature;
        _hasLastInput = true;
        _lastOutput = Math.Clamp(currentOutput, OutputMin, OutputMax);
        _lastTickMs = currentTickMs;

        // 使用完全一致的有效误差进行反算，实现绝对的无扰切换
        float rawError = Setpoint - currentTemperature;
        float activeError = CalculateActiveError(rawError);
        float p = Kp * activeError;
        _integral = _lastOutput - p;
        _integral = Math.Clamp(_integral, IntegralMin, IntegralMax);
    }

    public void SwitchToManual(float manualOutput)
    {
        AutoMode = false;
        ManualOutput = Math.Clamp(manualOutput, OutputMin, OutputMax);
    }

    private float CalculateActiveError(float rawError)
    {
        // 连续死区算法：将坐标轴平移，避免跨越死区时 P 项突变
        if (rawError > DeadBand) return rawError - DeadBand;
        if (rawError < -DeadBand) return rawError + DeadBand;
        return 0;
    }

    private float ApplyRampLimit(float currentOutput, float targetOutput, float dtSeconds)
    {
        if (OutputRampRatePerSecond <= 0) return targetOutput;
        float maxDelta = OutputRampRatePerSecond * dtSeconds;
        return Math.Clamp(targetOutput, currentOutput - maxDelta, currentOutput + maxDelta);
    }
}
#endregion

#region Time Proportioning
public sealed class TimeProportioningOutput
{
    private double _cycleTimeMs;
    private long _cycleStartTick;
    private float _outputPercent;

    public TimeSpan CycleTime
    {
        get => TimeSpan.FromMilliseconds(_cycleTimeMs);
        set
        {
            if (value <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(value), "CycleTime 必须大于 0。");
            _cycleTimeMs = value.TotalMilliseconds;
        }
    }

    public float OutputPercent
    {
        get => _outputPercent;
        set => _outputPercent = Math.Clamp(value, 0f, 100f);
    }

    public TimeProportioningOutput(TimeSpan cycleTime, long currentTickMs)
    {
        CycleTime = cycleTime;
        _cycleStartTick = currentTickMs;
    }

    public void Reset(long currentTickMs)
    {
        _cycleStartTick = currentTickMs;
    }

    public bool GetOutputState(long currentTickMs)
    {
        // 边界极值硬短路
        if (_outputPercent <= 0f) return false;
        if (_outputPercent >= 100f) return true;
        long elapsedMs = currentTickMs - _cycleStartTick;

        // 计算当前周期内的精确位置
        double currentPositionMs = elapsedMs % _cycleTimeMs;

        // 推进周期起点，防止 elapsedMs 无限增大导致浮点精度受损
        if (elapsedMs >= _cycleTimeMs)
        {
            long cyclesPassed = (long)(elapsedMs / _cycleTimeMs);
            _cycleStartTick += (long)(cyclesPassed * _cycleTimeMs);
        }

        // 计算导通阈值
        double onMs = _cycleTimeMs * _outputPercent / 100.0;
        return currentPositionMs < onMs;
    }
}
#endregion

#region AutoTune Models
public sealed class RelayAutoTuneOptions
{
    public float BiasOutputPercent { get; set; } = 50.0f;
    public float RelayAmplitudePercent { get; set; } = 30.0f;
    public float SwitchHysteresis { get; set; } = 0.5f;
    public float PeakThreshold { get; set; } = 0.5f;
    public int RequiredPeakCount { get; set; } = 8;
    public TimeSpan MaxDuration { get; set; } = TimeSpan.FromMinutes(30);
    public double MinOscillationAmplitude { get; set; } = 0.2;
    public double MaxOscillationAmplitude { get; set; } = 20.0;
    public TimeSpan MinPeakInterval { get; set; } = TimeSpan.FromSeconds(2);
    public double ConvergenceDeviation { get; set; } = 0.25; //收敛偏离度阈值
    public AutoTuneRule Rule { get; set; } = AutoTuneRule.TyreusLuybenPid;
}
public sealed class PidTuneResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public double Ku { get; init; }
    public double Pu { get; init; }
    public double Kp { get; init; }
    public double Ki { get; init; }
    public double Kd { get; init; }
    public double OscillationAmplitude { get; init; }
    public int PeakCount { get; init; }
    public override string ToString() => $"Success={Success}, Msg={Message}, Ku={Ku:F4}, Pu={Pu:F4}, Kp={Kp:F4}, Ki={Ki:F4}, Kd={Kd:F4}, Amp={OscillationAmplitude:F4}, Peaks={PeakCount}";
}
internal sealed class PeakPoint
{
    public long TimestampMs { get; init; }
    public float Value { get; init; }
    public PeakType Type { get; init; }
    public override string ToString() => $"{Type} @ Tick {TimestampMs}, {Value:F3}";
}
#endregion

#region Relay Auto Tuner
/// <summary>
/// 温控继电自整定器
/// 使用方式：
/// 1. Start()
/// 2. 周期性调用 Update(rawTemp, filteredTemp, utcNow)
/// 3. 读取 CurrentOutputPercent 输出到执行器
/// 4. 当 Status 变为 Succeeded / Failed / Cancelled 时结束
/// </summary>
public sealed class RelayAutoTuner
{
    private readonly List<PeakPoint> _peaks = new();
    private RelayAutoTuneOptions? _options;
    private long _startTickMs;
    private long _lastSampleTickMs;
    private bool _relayHigh;
    private bool _started;
    private float _currentOutputPercent;
    private float _lastValue;
    private float _lastSlope;
    private bool _hasLastValue;
    private bool _hasLastSlope;
    private string _message = string.Empty;
    public float Setpoint { get; set; }
    public float MaxSafeTemperature { get; set; } = 120.0f;
    public AutoTuneStatus Status { get; private set; } = AutoTuneStatus.Idle;
    public PidTuneResult? Result { get; private set; }
    public RelayAutoTuneOptions? CurrentOptions => _options;
    public float CurrentOutputPercent => _currentOutputPercent;
    public int PeakCount => _peaks.Count;

    public void Start(RelayAutoTuneOptions options, long currentTickMs)
    {
        if (Status == AutoTuneStatus.Running) return;
        if (options == null) throw new ArgumentNullException(nameof(options));
        if (options.RelayAmplitudePercent <= 0) throw new ArgumentOutOfRangeException(nameof(options.RelayAmplitudePercent));
        if (options.RequiredPeakCount < 6) throw new ArgumentOutOfRangeException(nameof(options.RequiredPeakCount));

        _options = options;
        _peaks.Clear();
        _startTickMs = currentTickMs;
        _lastSampleTickMs = currentTickMs;
        _relayHigh = true;
        _currentOutputPercent = Math.Clamp(options.BiasOutputPercent + options.RelayAmplitudePercent, 0f, 100f);
        _started = true;
        _hasLastValue = false;
        _hasLastSlope = false;
        _lastValue = 0f;
        _lastSlope = 0f;
        _message = "Auto tuning started.";
        Result = null;
        Status = AutoTuneStatus.Running;
    }

    public void Cancel()
    {
        if (Status != AutoTuneStatus.Running)
            return;
        Status = AutoTuneStatus.Cancelled;
        _currentOutputPercent = 0f;
        _message = "Auto tuning cancelled.";
    }

    public void Reset()
    {
        _peaks.Clear();
        _options = null;
        _started = false;
        _relayHigh = false;
        _currentOutputPercent = 0f;
        _hasLastValue = false;
        _hasLastSlope = false;
        _lastValue = 0f;
        _lastSlope = 0f;
        _message = string.Empty;
        Result = null;
        Status = AutoTuneStatus.Idle;
    }

    public void Update(float filteredTemperature, long currentTickMs)
    {
        if (!_started || _options == null || Status != AutoTuneStatus.Running)
            return;

        if (filteredTemperature >= MaxSafeTemperature)
        {
            Fail($"Temperature exceeded safe limit: {filteredTemperature:F2} >= {MaxSafeTemperature:F2}");
            return;
        }

        if ((currentTickMs - _startTickMs) > _options.MaxDuration.TotalMilliseconds)
        {
            Fail("Auto tuning timeout.");
            return;
        }

        HandleRelaySwitch(filteredTemperature);
        DetectPeak(filteredTemperature, currentTickMs);

        // 先判断是否达到等幅收敛状态
        if (CheckConvergence(out float stableAmplitude, out float stablePu))
        {
            // 判断振幅是否在安全的物理范围内
            if (CheckAmplitudeLimits(stableAmplitude, stablePu))
            {
                Complete(stableAmplitude, stablePu);
            }
        }
        _lastSampleTickMs = currentTickMs;
    }

    private void HandleRelaySwitch(float pv)
    {
        if (_options == null) return;
        float upper = Setpoint + _options.SwitchHysteresis;
        float lower = Setpoint - _options.SwitchHysteresis;
        if (_relayHigh && pv >= upper)
        {
            _relayHigh = false;
            _currentOutputPercent = Math.Clamp(_options.BiasOutputPercent - _options.RelayAmplitudePercent, 0f, 100f);
            _message = $"Switched LOW at PV={pv:F3}";
            return;
        }

        if (!_relayHigh && pv <= lower)
        {
            _relayHigh = true;
            _currentOutputPercent = Math.Clamp(_options.BiasOutputPercent + _options.RelayAmplitudePercent, 0f, 100f);
            _message = $"Switched HIGH at PV={pv:F3}";
        }
    }

    private void DetectPeak(float pv, long currentTickMs)
    {
        if (_options == null) return;
        if (!_hasLastValue)
        {
            _lastValue = pv;
            _hasLastValue = true;
            return;
        }

        float dt = (currentTickMs - _lastSampleTickMs) / 1000.0f;
        if (dt <= 0) dt = 1e-3f;
        float slope = (pv - _lastValue) / dt;
        if (_hasLastSlope)
        {
            if (_lastSlope > 0 && slope <= 0)
                TryAddPeak(_lastSampleTickMs, _lastValue, PeakType.Max);
            else if (_lastSlope < 0 && slope >= 0)
                TryAddPeak(_lastSampleTickMs, _lastValue, PeakType.Min);
        }
        _lastSlope = slope;
        _hasLastSlope = true;
        _lastValue = pv;
    }

    private void TryAddPeak(long timestampMs, float value, PeakType type)
    {
        if (_options == null) return;
        if (_peaks.Count > 0)
        {
            PeakPoint last = _peaks[^1];
            if ((timestampMs - last.TimestampMs) < _options.MinPeakInterval.TotalMilliseconds)
                return;

            if (last.Type == type)
            {
                bool replace =
                    (type == PeakType.Max && value > last.Value) ||
                    (type == PeakType.Min && value < last.Value);

                if (replace)
                {
                    _peaks[^1] = new PeakPoint
                    {
                        TimestampMs = timestampMs,
                        Value = value,
                        Type = type
                    };
                }
                return;
            }

            if (Math.Abs(value - last.Value) < _options.PeakThreshold)
                return;
        }

        _peaks.Add(new PeakPoint
        {
            TimestampMs = timestampMs,
            Value = value,
            Type = type
        });
        _message = $"Peak detected: {type}, value={value:F3}, count={_peaks.Count}";
    }

    private bool CheckConvergence(out float stableAmplitude, out float stablePu)
    {
        stableAmplitude = 0;
        stablePu = 0;
        if (_options == null || _peaks.Count < _options.RequiredPeakCount)
            return false;

        // 剔除最后一个未定型的波峰
        int endIndex = _peaks.Count - 2;
        int checkCount = Math.Min(6, endIndex);

        // 确保起点的索引至少为 2，彻底避开不可靠的 _peaks[0] 和由其计算出的第一个振幅
        int startIndex = Math.Max(2, endIndex - checkCount + 1);
        List<float> recentAmplitudes = new();
        for (int i = startIndex; i <= endIndex; i++)
        {
            recentAmplitudes.Add(Math.Abs(_peaks[i].Value - _peaks[i - 1].Value) / 2.0f);
        }
        if (recentAmplitudes.Count < 3)
            return false;
        float avgAmp = recentAmplitudes.Average();

        // 计算最大偏离度，并使用 Options 中的配置值进行判断
        float maxDevRatio = recentAmplitudes.Max(a => Math.Abs(a - avgAmp) / avgAmp);
        if (maxDevRatio > _options.ConvergenceDeviation)
            return false;

        // 提取稳态周期
        List<float> recentPeriods = new();
        for (int i = startIndex; i <= endIndex - 2; i++)
        {
            float sec = (_peaks[i + 2].TimestampMs - _peaks[i].TimestampMs) / 1000.0f;
            if (sec > 0)
                recentPeriods.Add(sec);
        }

        if (recentPeriods.Count == 0)
            return false;
        stableAmplitude = avgAmp;
        stablePu = recentPeriods.Average();
        return true;
    }

    private bool CheckAmplitudeLimits(float amplitude, float pu)
    {
        if (_options == null) return false;

        if (amplitude < _options.MinOscillationAmplitude)
        {
            Fail($"Oscillation amplitude too small: {amplitude:F3}", amplitude, pu);
            return false;
        }

        if (amplitude > _options.MaxOscillationAmplitude)
        {
            Fail($"Oscillation amplitude too large: {amplitude:F3}", amplitude, pu);
            return false;
        }
        return true;
    }

    private void Complete(float amplitude, float pu)
    {
        if (_options == null)
            return;

        float d = _options.RelayAmplitudePercent;
        double ku = 4.0 * d / (Math.PI * amplitude);
        (double kp, double ki, double kd) = ConvertKuPuToPid(ku, pu, _options.Rule);
        Result = new PidTuneResult
        {
            Success = true,
            Message = $"Auto tuning succeeded with {_options.Rule}.",
            Ku = ku,
            Pu = pu,
            Kp = kp,
            Ki = ki,
            Kd = kd,
            OscillationAmplitude = amplitude,
            PeakCount = _peaks.Count
        };
        Status = AutoTuneStatus.Succeeded;
        _currentOutputPercent = 0f;
        _message = Result.Message;
    }

    private void Fail(string message, float? oscillationAmplitude = null, float? pu = null)
    {
        Result = new PidTuneResult
        {
            Success = false,
            Message = message,
            PeakCount = _peaks.Count,
            OscillationAmplitude = oscillationAmplitude ?? 0,
            Pu = pu ?? 0
        };
        Status = AutoTuneStatus.Failed;
        _currentOutputPercent = 0f;
        _message = message;
    }

    private static (double Kp, double Ki, double Kd) ConvertKuPuToPid(double ku, double pu, AutoTuneRule rule)
    {
        if (ku <= 0) throw new ArgumentOutOfRangeException(nameof(ku));
        if (pu <= 0) throw new ArgumentOutOfRangeException(nameof(pu));
        return rule switch
        {
            AutoTuneRule.ZieglerNicholsPid => ComputeZieglerNicholsPid(ku, pu),
            AutoTuneRule.TyreusLuybenPid => ComputeTyreusLuybenPid(ku, pu),
            AutoTuneRule.ZieglerNicholsPi => ComputeZieglerNicholsPi(ku, pu),
            _ => throw new NotSupportedException($"Unsupported rule: {rule}")
        };
    }

    private static (double Kp, double Ki, double Kd) ComputeZieglerNicholsPid(double ku, double pu)
    {
        double kp = 0.60 * ku;
        double ti = pu / 2.0;
        double td = pu / 8.0;
        return (kp, kp / ti, kp * td);
    }

    private static (double Kp, double Ki, double Kd) ComputeTyreusLuybenPid(double ku, double pu)
    {
        double kp = 0.31 * ku;
        double ti = 2.2 * pu;
        double td = pu / 6.3;
        return (kp, kp / ti, kp * td);
    }

    private static (double Kp, double Ki, double Kd) ComputeZieglerNicholsPi(double ku, double pu)
    {
        double kp = 0.45 * ku;
        double ti = pu / 1.2;
        return (kp, kp / ti, 0.0);
    }
}
#endregion

#region Snapshots
public sealed class TempControllerSnapshot
{
    public required string Name { get; init; }
    public required TempControllerState State { get; init; }
    public required TempControllerAlarmState AlarmState { get; init; } = new();
    public required float RawTemperature { get; init; }
    public required float FilteredTemperature { get; init; }
    public required float Setpoint { get; init; }
    public required float Kp { get; init; }
    public required float Ki { get; init; }
    public required float Kd { get; init; }
    public required bool HeaterOn { get; init; }
    public required float DutyRatio { get; init; }
}
#endregion
