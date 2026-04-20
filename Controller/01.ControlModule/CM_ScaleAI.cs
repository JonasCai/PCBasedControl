using Controller.Common;
using Controller.EventLogger;
using Controller.gRPC;
using Controller.S88;
using System;
using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Controller._01.ControlModule;

public class CM_ScaleAI : S88ControlModuleBase
{
    public CM_ScaleAI(ScaleAICfg cfg, IEventProducer eventProducer, IRetainDataService retainDataService, ILogger<CM_ScaleAI> logger) : base(cfg.Name, eventProducer, logger)
    {
        _cfg = cfg;
        _retainDataService = retainDataService;

        RegisterCommandHandlers();

        _highHighLimit = _retainDataService.GetValue($"{Name}.HH", _cfg.ScaledMax);
        _highLimit = _retainDataService.GetValue($"{Name}.H", _cfg.ScaledMax);
        _lowLimit = _retainDataService.GetValue($"{Name}.L", _cfg.ScaledMin);
        _lowLowLimit = _retainDataService.GetValue($"{Name}.LL", _cfg.ScaledMin);

        if (!_cfg.Validate())
            throw new ArgumentException($"CM_ScaleAI[{_cfg.Name}]配置不完整", nameof(_cfg));
    }

    // ==========================================
    // S88ControlModuleBase重写接口
    // ==========================================
    public override bool HasAnyWarning => AlarmState.HasAnyWarning;
    public override bool HasAnyError => State == ScaleAIState.Error;
    public override void Refresh(long currentTimestampMs)
    {
        _currentTimestampMs = currentTimestampMs;

        // 读取底层原始信号
        _rawVal = _cfg.ReadRawValue();
        _isSensorFault = float.IsNaN(_rawVal) || float.IsInfinity(_rawVal);

        // 传感器硬防呆：断线、短路或极度溢出检测
        if (_isSensorFault)
        {
            _scaledVal = _rawVal; // 维持异常值
        }
        else
        {
            // 先滤波，再缩放
            _filteredRawVal = _filter.Filter(_rawVal, _cfg.FilterAlpha);
            _scaledVal = CalculateScale(_filteredRawVal);
        }

        // 处理指令队列
        ProcessCommandQueue();

        // 报警集中评估与映射
        AlarmHandler();
    }
    public override void ToSafe()
    {
        PurgeCommands();
        DisableAlarms();
    }

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
        RaiseInfo(ScaleAIEvents.InfoAlarmsEnabled);
    }
    public void DisableAlarms()
    {
        if (State == ScaleAIState.Error || State == ScaleAIState.Disabled) return;
        ChangeState(ScaleAIState.Disabled);
        RaiseInfo(ScaleAIEvents.InfoAlarmsDisabled);
    }
    public void UpdateLimits(float? hh = null, float? h = null, float? l = null, float? ll = null)
    {
        float oldHH = _highHighLimit, oldH = _highLimit, oldL = _lowLimit, oldLL = _lowLowLimit;
        bool isChanged = false;

        // 加入防抖判定，防止浮点数微小误差触发无效的写入
        if (hh.HasValue && Math.Abs(hh.Value - _highHighLimit) > 1e-6)
        {
            _highHighLimit = hh.Value;
            _retainDataService.SetValue($"{Name}.HH", hh.Value);
            isChanged = true;
        }
        if (h.HasValue && Math.Abs(h.Value - _highLimit) > 1e-6)
        {
            _highLimit = h.Value;
            _retainDataService.SetValue($"{Name}.H", h.Value);
            isChanged = true;
        }
        if (l.HasValue && Math.Abs(l.Value - _lowLimit) > 1e-6)
        {
            _lowLimit = l.Value;
            _retainDataService.SetValue($"{Name}.L", l.Value);
            isChanged = true;
        }
        if (ll.HasValue && Math.Abs(ll.Value - _lowLowLimit) > 1e-6)
        {
            _lowLowLimit = ll.Value;
            _retainDataService.SetValue($"{Name}.LL", ll.Value);
            isChanged = true;
        }

        if (isChanged)
        {
            RaiseInfo(ScaleAIEvents.InfoLimitsUpdated,
                oldHH, _highHighLimit,
                oldH, _highLimit,
                oldL, _lowLimit,
                oldLL, _lowLowLimit);
        }
    }
    public ScaleAISnapshot GetSnapshot() => new()
    {
        Name = _cfg.Name,
        State = State,
        AlarmState = AlarmState,
        RawValue = _rawVal,
        ScaledValue = _scaledVal,
        Unit = _cfg.EngineeringUnit,
        HH_Limit = _highHighLimit,
        H_Limit = _highLimit,
        L_Limit = _lowLimit,
        LL_Limit = _lowLowLimit
    };

    // ==========================================
    // 私有成员与核心逻辑
    // ==========================================
    private readonly ScaleAICfg _cfg;
    private readonly LowPassFilter _filter = new();
    private readonly IRetainDataService _retainDataService;
    private long _currentTimestampMs;
    private float _rawVal, _filteredRawVal, _scaledVal;
    private float _highHighLimit, _highLimit, _lowLowLimit, _lowLimit;
    private bool _isSensorFault;
    private void ChangeState(ScaleAIState newState)
    {
        if (State == newState) return;
        State = newState;
    }
    private float CalculateScale(float raw)
    {
        float rangeRaw = _cfg.RawMax - _cfg.RawMin;
        if (Math.Abs(rangeRaw) < 1e-6) return _cfg.ScaledMin;
        return (raw - _cfg.RawMin) / rangeRaw * (_cfg.ScaledMax - _cfg.ScaledMin) + _cfg.ScaledMin;
    }
    private void AlarmHandler()
    {
        // 传感器物理故障
        if (_isSensorFault) AlarmState.SensorError = true;

        if (State == ScaleAIState.Active && !_isSensorFault)
        {
            if (_scaledVal >= _highHighLimit) AlarmState.HighHighError = true;
            if (_scaledVal <= _lowLowLimit) AlarmState.LowLowError = true;

            if (AlarmState.HighHighError)
            {
                AlarmState.HighWarning = false; // HH 触发时，屏蔽 H
            }
            else
            {
                // Warning 级别允许动态恢复 (Level-Trigger)
                if (_scaledVal >= _highLimit) AlarmState.HighWarning = true;
                else if (_scaledVal <= _highLimit - _cfg.AlarmDeadband) AlarmState.HighWarning = false;
            }

            if (AlarmState.LowLowError)
            {
                AlarmState.LowWarning = false; // LL 触发时，屏蔽 L
            }
            else
            {
                // Warning 级别允许动态恢复 (Level-Trigger)
                if (_scaledVal <= _lowLimit) AlarmState.LowWarning = true;
                else if (_scaledVal >= _lowLimit + _cfg.AlarmDeadband) AlarmState.LowWarning = false;
            }
        }
        else if (State == ScaleAIState.Disabled)
        {
            // 停止监控时，清理所有无需人工干预的 Warning
            AlarmState.HighWarning = false;
            AlarmState.LowWarning = false;
        }

        if (AlarmState.SensorError) RaiseAlarm(ScaleAIEvents.ErrSensorError);
        else TryClearAlarm(ScaleAIEvents.ErrSensorError);

        if (AlarmState.HighHighError) RaiseAlarm(ScaleAIEvents.ErrHighHigh, _scaledVal, _highHighLimit, _cfg.EngineeringUnit);
        else TryClearAlarm(ScaleAIEvents.ErrHighHigh);

        if (AlarmState.LowLowError) RaiseAlarm(ScaleAIEvents.ErrLowLow, _scaledVal, _lowLowLimit, _cfg.EngineeringUnit);
        else TryClearAlarm(ScaleAIEvents.ErrLowLow);

        if (AlarmState.HighWarning) RaiseAlarm(ScaleAIEvents.WarningHigh, _scaledVal, _highLimit, _cfg.EngineeringUnit);
        else TryClearAlarm(ScaleAIEvents.WarningHigh);

        if (AlarmState.LowWarning) RaiseAlarm(ScaleAIEvents.WarningLow, _scaledVal, _lowLimit, _cfg.EngineeringUnit);
        else TryClearAlarm(ScaleAIEvents.WarningLow);

        if (AlarmState.HasAnyError && State != ScaleAIState.Error)
        {
            ChangeState(ScaleAIState.Error);
        }
    }
    private void Reset()
    {
        if (State != ScaleAIState.Error) return;

        // 传感器必须恢复正常才能清除
        if (!_isSensorFault)
            AlarmState.SensorError = false;

        // 必须跌出上上限减去“死区”后，才允许复位
        if (_scaledVal <= _highHighLimit - _cfg.AlarmDeadband)
            AlarmState.HighHighError = false;

        // 必须涨出下下限加上“死区”后，才允许复位
        if (_scaledVal >= _lowLowLimit + _cfg.AlarmDeadband)
            AlarmState.LowLowError = false;

        // 复位成功
        if (!AlarmState.HasAnyError)
        {
            ChangeState(ScaleAIState.Active);
            RaiseInfo(ScaleAIEvents.InfoReset);
        }
    }
    private void RegisterCommandHandlers()
    {
        RegisterCommandHandler(Command.Start, cmd =>
        {
            EnableAlarms();
            cmd.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Accepted, ""));
        });

        RegisterCommandHandler(Command.Stop, cmd =>
        {
            DisableAlarms();
            cmd.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Accepted, ""));
        });

        RegisterCommandHandler(Command.Reset, cmd =>
        {
            Reset();
            cmd.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Accepted, ""));
        });
    }
    protected override void RaiseAlarm(EventBase eventbase, params object[] args)
    {
        base.RaiseAlarm(eventbase, args);
        if (eventbase.Severity == SeverityLevel.Error)
            ChangeState(ScaleAIState.Error);
    }
}

// ==========================================
// 配置类与专属状态类
// ==========================================
public class ScaleAICfg
{
    public required string Name { get; init; }
    public string EngineeringUnit { get; init; } = "";
    public float RawMin { get; init; } = 0f;
    public float RawMax { get; init; } = 4000f;
    public float ScaledMin { get; init; } = 0f;
    public float ScaledMax { get; init; } = 100f;
    public float AlarmDeadband { get; init; } = 1.0f;
    public float FilterAlpha { get; init; } = 0.2f;
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
}

public enum ScaleAIState { Disabled, Active, Error }

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
    public static readonly EventBase InfoLimitsUpdated = new() { EventId = 403, Severity = SeverityLevel.Info, MessageTemplate = "报警阈值已更新 (HH: {0:F2} -> {1:F2}, H: {2:F2} -> {3:F2}, L: {4:F2} -> {5:F2}, LL: {6:F2} -> {7:F2})" };
    public static readonly EventBase ErrSensorError = new() { EventId = 420, Severity = SeverityLevel.Error, MessageTemplate = "传感器连接断开或数据异常" };
    public static readonly EventBase ErrHighHigh = new() { EventId = 421, Severity = SeverityLevel.Error, MessageTemplate = "检测值超越上上限 (当前: {0:F2} >= 限制: {1:F2} {2})" };
    public static readonly EventBase ErrLowLow = new() { EventId = 422, Severity = SeverityLevel.Error, MessageTemplate = "检测值跌破下下限 (当前: {0:F2} <= 限制: {1:F2} {2})" };
    public static readonly EventBase WarningHigh = new() { EventId = 440, Severity = SeverityLevel.Warning, MessageTemplate = "检测值触及上限警告 (当前: {0:F2} >= 限制: {1:F2} {2})" };
    public static readonly EventBase WarningLow = new() { EventId = 441, Severity = SeverityLevel.Warning, MessageTemplate = "检测值触及下限警告 (当前: {0:F2} <= 限制: {1:F2} {2})" };
}

public interface IScaleAIFactory { CM_ScaleAI Create(ScaleAICfg cfg); }

public class ScaleAIFactory : IScaleAIFactory
{
    private readonly IServiceProvider _sp;
    public ScaleAIFactory(IServiceProvider sp) => _sp = sp;
    public CM_ScaleAI Create(ScaleAICfg cfg) => ActivatorUtilities.CreateInstance<CM_ScaleAI>(_sp, cfg);
}