using Controller.EventLogger;
using Controller.gRPC;
using Controller.S88;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Controller._01.ControlModule;

public class CM_ScaleAI : IControlModule
{
    public CM_ScaleAI(IEventProducer eventProducer, ScaleAICfg cfg, ILogger<CM_ScaleAI> logger)
    {
        _eventProducer = eventProducer;
        _cfg = cfg;
        _logger = logger;

        RegisterCommandHandlers();

        if (!_cfg.Validate())
            throw new ArgumentException($"CM_ScaleAI[{_cfg.Name}]配置不完整", nameof(_cfg));

        // 初始状态设定
        State = _cfg.AutoEnableAlarmsOnStartup ? ScaleAIState.Active : ScaleAIState.Disabled;
    }

    // ==========================================
    // IControlModule 接口方法
    // ==========================================
    public bool HasAnyWarning => AlarmState.HasAnyWarning;
    public bool HasAnyError => State == ScaleAIState.Error;
    public string Name => _cfg.Name;
    public void Refresh(long currentTimestampMs)
    {
        _currentTimestampMs = currentTimestampMs;

        // 读取底层原始信号 (Raw Value)
        _rawVal = _cfg.ReadRawValue();

        // 传感器硬防呆：断线、短路或极度溢出检测
        if (float.IsNaN(_rawVal) || float.IsInfinity(_rawVal))
        {
            _scaledVal = _rawVal; // 维持异常值
        }
        else
        {
            // 先滤波，再缩放 (滤波滤的是电信号的毛刺)
            _filteredRawVal = _filter.Filter(_rawVal, _cfg.FilterAlpha);
            _scaledVal = CalculateScale(_filteredRawVal);
        }

        // 处理指令队列
        ProcessCommandQueue();

        // 评估所有超限报警及物理状态
        EvaluateAlarms();

        // 状态机逻辑
        switch (State)
        {
            case ScaleAIState.Disabled:
                // 仅转换数值，忽略超限报警
                break;

            case ScaleAIState.Active:
                // 正常监控中，遇到致命错误则由 EvaluateAlarms 触发进入 Error
                break;

            case ScaleAIState.Error:
                break;
        }
    }
    public void ToSafe()
    {
        PurgeCommands();
        // 对于模拟量读取模块，ToSafe 通常意味着停止超限报警监控，避免滋扰
        DisableAlarms();
    }
    public void ExecuteCommand(InternalCommand command) => _commandQueue.Enqueue(command);

    // ==========================================
    // 外部接口
    // ==========================================
    public float RawValue => _rawVal;
    public float ScaledValue => _scaledVal;
    public ScaleAIState State { get; private set; } = ScaleAIState.Disabled;
    public ScaleAIAlarmState AlarmState { get; } = new();
    public void EnableAlarms()
    {
        if (State == ScaleAIState.Error || State == ScaleAIState.Active) return;
        ChangeState(ScaleAIState.Active);
        _eventProducer.SendInfo(_cfg.Name, ScaleAIEvents.InfoAlarmsEnabled);
    }
    public void DisableAlarms()
    {
        if (State == ScaleAIState.Error || State == ScaleAIState.Disabled) return;
        ChangeState(ScaleAIState.Disabled);

        // 停止监控时，自动清理那些不需要手动确认的“警告”
        if (AlarmState.HighWarning) TryClearAlarm(ScaleAIEvents.WarningHigh);
        if (AlarmState.LowWarning) TryClearAlarm(ScaleAIEvents.WarningLow);

        AlarmState.HighWarning = false;
        AlarmState.LowWarning = false;

        _eventProducer.SendInfo(_cfg.Name, ScaleAIEvents.InfoAlarmsDisabled);
    }
    public void UpdateLimits(float? hh = null, float? h = null, float? l = null, float? ll = null)
    {
        if (hh.HasValue) _cfg.HighHighLimit = hh.Value;
        if (h.HasValue) _cfg.HighLimit = h.Value;
        if (l.HasValue) _cfg.LowLimit = l.Value;
        if (ll.HasValue) _cfg.LowLowLimit = ll.Value;

        _eventProducer.SendInfo(_cfg.Name, ScaleAIEvents.InfoLimitsUpdated,
            _cfg.HighHighLimit, _cfg.HighLimit, _cfg.LowLimit, _cfg.LowLowLimit);
    }
    public ScaleAISnapshot GetSnapshot() => new()
    {
        Name = _cfg.Name,
        State = State,
        AlarmState = AlarmState,
        RawValue = _rawVal,
        ScaledValue = _scaledVal,
        Unit = _cfg.EngineeringUnit,
        HH_Limit = _cfg.HighHighLimit,
        H_Limit = _cfg.HighLimit,
        L_Limit = _cfg.LowLimit,
        LL_Limit = _cfg.LowLowLimit
    };

    // ==========================================
    // 私有成员与核心逻辑
    // ==========================================
    private readonly ILogger<CM_ScaleAI> _logger;
    private readonly ScaleAICfg _cfg;
    private readonly IEventProducer _eventProducer;
    private readonly Dictionary<int, (Guid guid, EventBase eventBase, object[] args)> _activeAlarms = new();
    private readonly ConcurrentQueue<InternalCommand> _commandQueue = new();
    private readonly Dictionary<Command, Action<InternalCommand>> _commandHandlers = new();
    private readonly LowPassFilter _filter = new();
    private long _currentTimestampMs;
    private float _rawVal, _filteredRawVal, _scaledVal;
    private void ChangeState(ScaleAIState newState)
    {
        if (State == newState) return;
        State = newState;
    }

    private float CalculateScale(float raw)
    {
        // 线性插值计算: Scaled = (Raw - RawMin) * (ScaledMax - ScaledMin) / (RawMax - RawMin) + ScaledMin
        float rangeRaw = _cfg.RawMax - _cfg.RawMin;
        if (Math.Abs(rangeRaw) < 1e-6) return _cfg.ScaledMin; // 防止除以 0

        float scaled = (raw - _cfg.RawMin) / rangeRaw * (_cfg.ScaledMax - _cfg.ScaledMin) + _cfg.ScaledMin;
        return scaled;
    }

    private void EvaluateAlarms()
    {
        // 传感器物理故障
        if (float.IsNaN(_rawVal) || float.IsInfinity(_rawVal))
        {
            AlarmState.SensorError = true;
            RaiseAlarm(ScaleAIEvents.ErrSensorError);
            return; // 传感器坏了，不再检查上下限
        }
        else
        {
            AlarmState.SensorError = false;
        }

        // 未开启超限监控，或者处于错误锁死态，停止计算报警
        if (State == ScaleAIState.Disabled) return;

        // 上上限 Error (HH)
        if (_scaledVal >= _cfg.HighHighLimit)
        {
            AlarmState.HighHighError = true;
            RaiseAlarm(ScaleAIEvents.ErrHighHigh, _scaledVal, _cfg.HighHighLimit, _cfg.EngineeringUnit);
        }
        // 加入死区(Deadband)判断，防止数值在阈值边缘疯狂跳动
        else if (_scaledVal <= _cfg.HighHighLimit - _cfg.AlarmDeadband)
        {
            AlarmState.HighHighError = false; // 物理条件恢复
        }

        // 下下限 Error (LL)
        if (_scaledVal <= _cfg.LowLowLimit)
        {
            AlarmState.LowLowError = true;
            RaiseAlarm(ScaleAIEvents.ErrLowLow, _scaledVal, _cfg.LowLowLimit, _cfg.EngineeringUnit);
        }
        else if (_scaledVal >= _cfg.LowLowLimit + _cfg.AlarmDeadband)
        {
            AlarmState.LowLowError = false;
        }

        // 上限 Warning (H) 
        if (_scaledVal >= _cfg.HighLimit && _scaledVal < _cfg.HighHighLimit)
        {
            if (!AlarmState.HighWarning)
            {
                AlarmState.HighWarning = true;
                RaiseAlarm(ScaleAIEvents.WarningHigh, _scaledVal, _cfg.HighLimit, _cfg.EngineeringUnit);
            }
        }
        else if (_scaledVal <= _cfg.HighLimit - _cfg.AlarmDeadband)
        {
            if (AlarmState.HighWarning)
            {
                AlarmState.HighWarning = false;
                TryClearAlarm(ScaleAIEvents.WarningHigh); // 警告自动复位
            }
        }

        // 6. 下限 Warning (L)
        if (_scaledVal <= _cfg.LowLimit && _scaledVal > _cfg.LowLowLimit)
        {
            if (!AlarmState.LowWarning)
            {
                AlarmState.LowWarning = true;
                RaiseAlarm(ScaleAIEvents.WarningLow, _scaledVal, _cfg.LowLimit, _cfg.EngineeringUnit);
            }
        }
        else if (_scaledVal >= _cfg.LowLimit + _cfg.AlarmDeadband)
        {
            if (AlarmState.LowWarning)
            {
                AlarmState.LowWarning = false;
                TryClearAlarm(ScaleAIEvents.WarningLow); // 警告自动复位
            }
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
            ChangeState(ScaleAIState.Error);
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
        if (State != ScaleAIState.Error) return;

        if (!AlarmState.SensorError) TryClearAlarm(ScaleAIEvents.ErrSensorError);
        if (!AlarmState.HighHighError) TryClearAlarm(ScaleAIEvents.ErrHighHigh);
        if (!AlarmState.LowLowError) TryClearAlarm(ScaleAIEvents.ErrLowLow);

        if (!AlarmState.HasAnyError)
        {
            // 复位成功，回到监控状态
            ChangeState(ScaleAIState.Active);
            _eventProducer.SendInfo(_cfg.Name, ScaleAIEvents.InfoReset);
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
        _commandHandlers[Command.Start] = cmd =>
        {
            EnableAlarms();
            cmd.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Accepted, ""));
        };

        _commandHandlers[Command.Stop] = cmd =>
        {
            DisableAlarms();
            cmd.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Accepted, ""));
        };

        _commandHandlers[Command.Reset] = cmd =>
        {
            Reset();
            cmd.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Accepted, ""));
        };
    }
}

// ==========================================
// 配置类与报警状态类
// ==========================================
public class ScaleAICfg
{
    public required string Name { get; init; }
    public string EngineeringUnit { get; init; } = ""; // 物理单位 (如 kPa, N, Torr)

    // 缩放参数 (Scaling)
    public float RawMin { get; init; } = 0f;
    public float RawMax { get; init; } = 4000f;
    public float ScaledMin { get; init; } = 0f;
    public float ScaledMax { get; init; } = 100f;

    // 报警参数 (Limits)
    public float HighHighLimit { get; set; } = 95f;
    public float HighLimit { get; set; } = 90f;
    public float LowLimit { get; set; } = 10f;
    public float LowLowLimit { get; set; } = 5f;

    // 报警死区/滞环，防止数值临界跳动引发报警洪泛
    public float AlarmDeadband { get; init; } = 1.0f;

    // 滤波系数 (0~1)
    public float FilterAlpha { get; init; } = 0.2f;

    // 启动时是否默认开启超限监控
    public bool AutoEnableAlarmsOnStartup { get; init; } = true;

    // 数据读取委托
    public required Func<float> ReadRawValue { get; init; }

    public bool Validate()
    {
        return !string.IsNullOrEmpty(Name) && ReadRawValue != null && RawMax > RawMin;
    }
}

public sealed class ScaleAIAlarmState
{
    public bool HighWarning { get; internal set; }
    public bool LowWarning { get; internal set; }
    public bool HasAnyWarning => HighWarning || LowWarning;

    public bool HighHighError { get; internal set; }
    public bool LowLowError { get; internal set; }
    public bool SensorError { get; internal set; }
    public bool HasAnyError => HighHighError || LowLowError || SensorError;

    public override string ToString() => $"HH={HighHighError}, H={HighWarning}, L={LowWarning}, LL={LowLowError}, SensorErr={SensorError}";
}

public enum ScaleAIState
{
    Disabled, // 缩放正常工作，但关闭上下限报警监控
    Active,   // 正常工作，监控中
    Error     // 超越极限界限或传感器故障，锁死等待人工复位
}

public sealed class ScaleAISnapshot
{
    public required string Name { get; init; }
    public required ScaleAIState State { get; init; }
    public required ScaleAIAlarmState AlarmState { get; init; } = new();
    public required float RawValue { get; init; }
    public required float ScaledValue { get; init; }
    public required string Unit { get; init; }
    public required float HH_Limit { get; init; }
    public required float H_Limit { get; init; }
    public required float L_Limit { get; init; }
    public required float LL_Limit { get; init; }
}

public static class ScaleAIEvents
{
    public static readonly EventBase InfoAlarmsEnabled = new() { EventId = 400, Severity = SeverityLevel.Info, MessageTemplate = "开启超限报警监控" };
    public static readonly EventBase InfoAlarmsDisabled = new() { EventId = 401, Severity = SeverityLevel.Info, MessageTemplate = "停止超限报警监控" };
    public static readonly EventBase InfoReset = new() { EventId = 402, Severity = SeverityLevel.Info, MessageTemplate = "报警复位成功" };
    public static readonly EventBase InfoLimitsUpdated = new() { EventId = 403, Severity = SeverityLevel.Info, MessageTemplate = "报警阈值已更新 (HH:{0}, H:{1}, L:{2}, LL:{3})" };

    public static readonly EventBase ErrSensorError = new() { EventId = 420, Severity = SeverityLevel.Error, MessageTemplate = "传感器连接断开或数据异常" };
    public static readonly EventBase ErrHighHigh = new() { EventId = 421, Severity = SeverityLevel.Error, MessageTemplate = "检测值超越上上限 (当前: {0:F2} >= 限制: {1:F2} {2})" };
    public static readonly EventBase ErrLowLow = new() { EventId = 422, Severity = SeverityLevel.Error, MessageTemplate = "检测值跌破下下限 (当前: {0:F2} <= 限制: {1:F2} {2})" };

    public static readonly EventBase WarningHigh = new() { EventId = 440, Severity = SeverityLevel.Warning, MessageTemplate = "检测值触及上限警告 (当前: {0:F2} >= 限制: {1:F2} {2})" };
    public static readonly EventBase WarningLow = new() { EventId = 441, Severity = SeverityLevel.Warning, MessageTemplate = "检测值触及下限警告 (当前: {0:F2} <= 限制: {1:F2} {2})" };
}
