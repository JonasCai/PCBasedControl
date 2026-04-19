using Controller.Common;
using Controller.EventLogger;
using Controller.gRPC;
using Controller.S88;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Controller._01.ControlModule;

public class CM_MFC : S88ControlModuleBase
{
    public CM_MFC(MfcCfg cfg, IEventProducer eventProducer, ILogger<CM_MFC> logger): base(cfg.Name,eventProducer,logger)
    {
        _cfg = cfg;
        RegisterCommandHandlers();

        if (!_cfg.Validate())
            throw new ArgumentException($"CM_MFC[{_cfg.Name}]配置不完整", nameof(_cfg));
    }

    // ==========================================
    // IControlModule 接口方法
    // ==========================================
    public override bool HasAnyWarning => AlarmState.HasAnyWarning;
    public override bool HasAnyError => State == MfcState.Error;
    public override void Refresh(long currentTimestampMs)
    {
        _currentTimestampMs = currentTimestampMs;

        // 读取原生流量信号并进行硬防呆校验 (防止底层总线断开导致 NaN/Infinity)
        _rawPv = _cfg.ReadPV();
        if (float.IsNaN(_rawPv) || float.IsInfinity(_rawPv) || _rawPv < -_cfg.Capacity * 0.1f)
        {
            // 传感器严重异常，跳过滤波，直接用异常值触发报警
            _filteredPv = _rawPv;
        }
        else
        {
            // 正常信号，进行低通滤波消除气流波动噪声
            _filteredPv = _filter.Filter(_rawPv, _cfg.FilterAlpha);
        }

        // 处理排队的外部指令
        ProcessCommandQueue();

        // 评估所有物理状态并触发/解除报警
        EvaluateAlarms();

        // 故障态直接切断输出，跳过后续逻辑
        if (State == MfcState.Error)
        {
            _cfg.WriteSP(0f); // 故障时强制输出 0
            return;
        }

        // 状态机逻辑
        switch (State)
        {
            case MfcState.Unknown:
                _cfg.WriteSP(0f);
                ChangeState(MfcState.Off);
                break;

            case MfcState.Off:
                _cfg.WriteSP(0f);
                break;

            case MfcState.Controlling:
                _cfg.WriteSP(_targetSp);
                break;
        }
    }
    public override void ToSafe()
    {
        PurgeCommands();
        _cfg.WriteSP(0f);
        ChangeState(MfcState.Unknown);
    }

    // ==========================================
    // 外部控制接口
    // ==========================================
    public void SetFlow(float sp)
    {
        if (State == MfcState.Error) return;

        // 限制设定值在量程范围内
        float clampedSp = Math.Clamp(sp, 0f, _cfg.Capacity);

        if (!_cfg.CanOperate() && clampedSp > 0)
        {
            AlarmState.InterlockConditionsNotMet = true;
            RaiseAlarm(MfcEvents.ErrInterlockLost);
            return;
        }

        _targetSp = clampedSp;

        if (_targetSp > 0)
        {
            if (State != MfcState.Controlling)
            {
                ChangeState(MfcState.Controlling);
                RaiseInfo(MfcEvents.InfoFlowStarted, _targetSp);
            }
            else
            {
                RaiseInfo(MfcEvents.InfoSpChanged, _targetSp);
            }
        }
        else
        {
            Stop(); // 如果设定的流量为0，直接按停止处理
        }
    }
    public void Stop()
    {
        if (State == MfcState.Error || State == MfcState.Off) return;

        _targetSp = 0f;
        ChangeState(MfcState.Off);
        RaiseInfo(MfcEvents.InfoFlowStopped);
    }
    public MfcState State { get; private set; } = MfcState.Unknown;
    public MfcAlarmState AlarmState { get; } = new();
    public float PV => _filteredPv;
    public float SP => State == MfcState.Controlling ? _targetSp : 0f;
    public MfcSnapshot GetSnapshot() => new()
    {
        Name = _cfg.Name,
        State = State,
        AlarmState = AlarmState,
        Capacity = _cfg.Capacity,
        TargetSP = _targetSp,
        CurrentSP = SP,
        RawPV = _rawPv,
        FilteredPV = _filteredPv
    };

    // ==========================================
    // 私有成员与状态机核心
    // ==========================================
    private long _currentTimestampMs;
    private readonly MfcCfg _cfg;
    private readonly LowPassFilter _filter = new();
    private float _rawPv, _filteredPv, _targetSp;
    private long? _deviationStartTimestampMs = null; // 用于流量偏差超时计时
    private void ChangeState(MfcState newState)
    {
        if (State == newState) return;

        // 状态切换时清理偏差计时器
        if (newState == MfcState.Off || newState == MfcState.Unknown || newState == MfcState.Error)
        {
            _deviationStartTimestampMs = null;
        }

        State = newState;
    }

    private void EvaluateAlarms()
    {
        // 传感器物理故障检查 (断线/短路/越界)
        if (float.IsNaN(_rawPv) || float.IsInfinity(_rawPv) || _rawPv < -_cfg.Capacity * 0.1f)
        {
            AlarmState.SensorError = true;
            RaiseAlarm(MfcEvents.ErrSensorError, _rawPv);
        }
        else
        {
            AlarmState.SensorError = false;
        }

        // 联锁丢失检查
        if (!_cfg.CanOperate())
        {
            if (State == MfcState.Controlling)
            {
                AlarmState.InterlockConditionsNotMet = true;
                RaiseAlarm(MfcEvents.ErrInterlockLost);
            }
        }
        else
        {
            AlarmState.InterlockConditionsNotMet = false;
        }

        // 流量偏差超时检查
        if (State == MfcState.Controlling && !AlarmState.SensorError)
        {
            float error = Math.Abs(_filteredPv - _targetSp);

            // 如果误差超出容忍带
            if (error > _cfg.FlowTolerance)
            {
                // 记录开始偏差的时间戳
                if (_deviationStartTimestampMs == null)
                    _deviationStartTimestampMs = _currentTimestampMs;

                // 检查是否超时
                if (_currentTimestampMs - _deviationStartTimestampMs.Value > _cfg.FlowDeviationTimeoutMs)
                {
                    AlarmState.FlowDeviationError = true;
                    RaiseAlarm(MfcEvents.ErrFlowDeviation, _targetSp, _filteredPv);
                }
            }
            else
            {
                // 误差回到容忍带以内，清空计时器 (物理条件恢复)
                _deviationStartTimestampMs = null;
                AlarmState.FlowDeviationError = false;
            }
        }
        else
        {
            _deviationStartTimestampMs = null;
            AlarmState.FlowDeviationError = false;
        }
    }

    protected override void RaiseAlarm(EventBase eventbase, params object[] args)
    {
        base.RaiseAlarm(eventbase, args);

        if (eventbase.Severity == SeverityLevel.Error)
            ChangeState(MfcState.Error);
    }

    private void Reset()
    {
        if (State != MfcState.Error) return;

        // 连续物理故障：仅当物理条件恢复时，才允许清除报警记录
        if (!AlarmState.SensorError)
            TryClearAlarm(MfcEvents.ErrSensorError);

        if (!AlarmState.InterlockConditionsNotMet)
            TryClearAlarm(MfcEvents.ErrInterlockLost);

        // 无条件清除，给设备重新尝试建立流量的机会
        AlarmState.FlowDeviationError = false;
        _deviationStartTimestampMs = null;
        TryClearAlarm(MfcEvents.ErrFlowDeviation);

        // 脱离错误状态
        if (!AlarmState.HasAnyError)
        {
            ChangeState(MfcState.Unknown);
            RaiseInfo(MfcEvents.InfoReset);
        }
    }

    private void RegisterCommandHandlers()
    {
        RegisterCommandHandler(Command.SetFlow, cmd =>
        {
            if (cmd.Params.Count > 0 && float.TryParse(cmd.Params.Values.First(), out var sp))
            {
                SetFlow(sp);
                cmd.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Accepted, string.Empty));
            }
            else
            {
                cmd.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Rejected, "缺少流量参数"));
            }
        });

        RegisterCommandHandler(Command.Stop, cmd =>
        {
            Stop();
            cmd.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Accepted, string.Empty));
        });

        RegisterCommandHandler(Command.Reset, cmd =>
        {
            Reset();
            cmd.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Accepted, string.Empty));
        });
    }
}

// ==========================================
// 配置类与专属报警状态类
// ==========================================
public sealed class MfcAlarmState
{
    public bool HasAnyWarning => false;

    public bool InterlockConditionsNotMet { get; internal set; }
    public bool FlowDeviationError { get; internal set; }
    public bool SensorError { get; internal set; }

    public bool HasAnyError => InterlockConditionsNotMet || FlowDeviationError || SensorError;

    public override string ToString() => $"InterlockLost={InterlockConditionsNotMet}, DeviationErr={FlowDeviationError}, SensorErr={SensorError}";
}

public class MfcCfg
{
    public required string Name { get; init; }
    public required float Capacity { get; init; } // 满量程 (SCCM/SLM)

    public float FlowTolerance { get; init; } = 2.0f; // 流量允许的误差带绝对值
    public int FlowDeviationTimeoutMs { get; init; } = 3000; // 流量超差容忍时间(建流时间)
    public float FilterAlpha { get; init; } = 0.2f; // 低通滤波系数

    public required Func<float> ReadPV { get; init; }
    public required Action<float> WriteSP { get; init; }
    public required Func<bool> CanOperate { get; init; }

    public bool Validate()
    {
        return !string.IsNullOrEmpty(Name) &&
               Capacity > 0 &&
               ReadPV != null &&
               WriteSP != null &&
               CanOperate != null;
    }
}

public enum MfcState
{
    Unknown,     // 未知
    Off,         // 关闭 (SP=0)
    Controlling, // 供气中 (SP>0)
    Error        // 故障
}

public sealed class MfcSnapshot
{
    public required string Name { get; init; }
    public required MfcState State { get; init; }
    public required MfcAlarmState AlarmState { get; init; } = new();
    public required float Capacity { get; init; }
    public required float TargetSP { get; init; }
    public required float CurrentSP { get; init; }
    public required float RawPV { get; init; }
    public required float FilteredPV { get; init; }
}

public static class MfcEvents
{
    public static readonly EventBase InfoFlowStarted = new() { EventId = 300, Severity = SeverityLevel.Info, MessageTemplate = "开始供气 (目标: {0:F1})" };
    public static readonly EventBase InfoSpChanged = new() { EventId = 301, Severity = SeverityLevel.Info, MessageTemplate = "流量设定值更改为 {0:F1}" };
    public static readonly EventBase InfoFlowStopped = new() { EventId = 302, Severity = SeverityLevel.Info, MessageTemplate = "停止供气" };
    public static readonly EventBase InfoReset = new() { EventId = 303, Severity = SeverityLevel.Info, MessageTemplate = "故障复位" };

    public static readonly EventBase ErrInterlockLost = new() { EventId = 320, Severity = SeverityLevel.Error, MessageTemplate = "外部供气联锁条件丢失" };
    public static readonly EventBase ErrFlowDeviation = new() { EventId = 321, Severity = SeverityLevel.Error, MessageTemplate = "流量建流失败或发生严重偏差 (SP: {0:F1}, PV: {1:F1})" };
    public static readonly EventBase ErrSensorError = new() { EventId = 322, Severity = SeverityLevel.Error, MessageTemplate = "流量传感器读数异常 (PV: {0:F1})" };
}

public interface IMfcFactory
{
    CM_MFC Create(MfcCfg cfg);
}

public class MfcFactory : IMfcFactory
{
    private readonly IServiceProvider _serviceProvider;
    public MfcFactory(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public CM_MFC Create(MfcCfg cfg)
    {
        return ActivatorUtilities.CreateInstance<CM_MFC>(_serviceProvider, cfg);
    }
}

