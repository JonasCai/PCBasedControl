using Controller.Common;
using Controller.EventLogger;
using Controller.gRPC;
using Controller.S88;
using System;
using System.Collections.Concurrent;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Controller._01.ControlModule;

public class CM_MFC : S88ControlModuleBase
{
    public CM_MFC(MfcCfg cfg, IEventProducer eventProducer, ILogger<CM_MFC> logger) : base(cfg.Name, eventProducer, logger)
    {
        _cfg = cfg;
        RegisterCommandHandlers();

        if (!_cfg.Validate())
            throw new ArgumentException($"CM_MFC[{_cfg.Name}]配置不完整", nameof(_cfg));
    }

    // ==========================================
    // S88ControlModuleBase重写接口
    // ==========================================
    public override bool HasAnyWarning => AlarmState.HasAnyWarning;
    public override bool HasAnyError => State == MfcState.Error;
    public override void Refresh(long currentTimestampMs)
    {
        _currentTimestampMs = currentTimestampMs;

        // 读取原生流量信号并进行硬防呆校验
        _rawPv = _cfg.ReadPV();
        _isSensorFault = float.IsNaN(_rawPv) || float.IsInfinity(_rawPv) || _rawPv < -_cfg.Capacity * 0.1f;

        if (_isSensorFault)
        {
            // 传感器严重异常，跳过滤波，直接使用原始值
            _filteredPv = _rawPv;
        }
        else
        {
            // 正常信号，进行低通滤波消除气流波动噪声
            _filteredPv = _filter.Filter(_rawPv, _cfg.FilterAlpha);
        }

        ProcessCommandQueue();

        if (State == MfcState.Error && _targetCmdSp > 0f)
        {
            // 处于故障态时，绝对禁止输出任何流量！强制切断物理输出！
            _cfg.WriteSP(0f);
        }
        else
        {
            // 正常态或意图为 0 时，将意图映射到硬件输出
            _cfg.WriteSP(_targetCmdSp);
        }

        // 逻辑状态机
        OnExecute();

        // 报警评估与映射
        AlarmHandler();
    }
    public override void ToSafe()
    {
        PurgeCommands();
        _targetCmdSp = 0f;
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

        // 启动前联锁不满足
        if (!_cfg.CanOperate() && clampedSp > 0)
        {
            AlarmState.InterlockError = true;
            return;
        }

        // 如果设定值发生改变，重置建流超时计时器
        if (Math.Abs(_targetCmdSp - clampedSp) > 0.01f)
        {
            _deviationStartTimestampMs = null;
        }

        _targetCmdSp = clampedSp;

        if (State != MfcState.Error)
        {
            if (_targetCmdSp > 0)
                RaiseInfo(MfcEvents.InfoFlowStarted, _targetCmdSp);
            else
                RaiseInfo(MfcEvents.InfoFlowStopped);
        }
    }
    public void Stop()
    {
        SetFlow(0f);
    }
    public MfcState State { get; private set; } = MfcState.Unknown;
    public MfcAlarmState AlarmState { get; } = new();
    public float PV => _filteredPv;
    public float SP => State == MfcState.Controlling ? _targetCmdSp : 0f;
    public MfcSnapshot GetSnapshot() => new()
    {
        Name = _cfg.Name,
        State = State,
        AlarmState = AlarmState,
        Capacity = _cfg.Capacity,
        TargetSP = _targetCmdSp,
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
    private float _rawPv, _filteredPv;
    private bool _isSensorFault;
    private long? _deviationStartTimestampMs = null;
    private float _targetCmdSp = 0f;
    private void OnExecute()
    {
        if (State == MfcState.Error) return;

        switch (State)
        {
            case MfcState.Unknown:
                ChangeState(_targetCmdSp > 0 ? MfcState.Controlling : MfcState.Off);
                break;

            case MfcState.Off:
                if (_targetCmdSp > 0) ChangeState(MfcState.Controlling);
                break;

            case MfcState.Controlling:
                if (_targetCmdSp <= 0) ChangeState(MfcState.Off);
                break;
        }
    }

    private void AlarmHandler()
    {
        // 传感器物理故障检查 (断线/短路/越界)
        if (_isSensorFault)
        {
            AlarmState.SensorError = true;
        }

        // 运行中联锁丢失检查
        if (!_cfg.CanOperate() && State == MfcState.Controlling)
        {
            AlarmState.InterlockLostError = true;
        }

        // 流量偏差/建流超时检查
        if (State == MfcState.Controlling && !AlarmState.SensorError)
        {
            float error = Math.Abs(_filteredPv - _targetCmdSp);

            if (error > _cfg.FlowTolerance)
            {
                if (_deviationStartTimestampMs == null) _deviationStartTimestampMs = _currentTimestampMs;

                if (_currentTimestampMs - _deviationStartTimestampMs.Value > _cfg.FlowDeviationTimeoutMs)
                {
                    AlarmState.FlowDeviationError = true;
                }
            }
            else
            {
                _deviationStartTimestampMs = null;
            }
        }
        else
        {
            _deviationStartTimestampMs = null;
        }

        // 一旦发生严重故障或联锁丢失，强制夺取意图切断供气
        if (AlarmState.SensorError || AlarmState.InterlockLostError || AlarmState.FlowDeviationError)
        {
            _targetCmdSp = 0f;
        }

        if (AlarmState.SensorError) RaiseAlarm(MfcEvents.ErrSensorError, _rawPv);
        else TryClearAlarm(MfcEvents.ErrSensorError);

        if (AlarmState.InterlockError) RaiseAlarm(MfcEvents.ErrInterlock);
        else TryClearAlarm(MfcEvents.ErrInterlock);

        if (AlarmState.InterlockLostError) RaiseAlarm(MfcEvents.ErrInterlockLost);
        else TryClearAlarm(MfcEvents.ErrInterlockLost);

        if (AlarmState.FlowDeviationError) RaiseAlarm(MfcEvents.ErrFlowDeviation, _targetCmdSp, _filteredPv);
        else TryClearAlarm(MfcEvents.ErrFlowDeviation);

        if (AlarmState.HasAnyError && State != MfcState.Error)
        {
            ChangeState(MfcState.Error);
        }
    }

    private void Reset()
    {
        if (State != MfcState.Error) return;

        // 只有物理信号读数恢复正常，才允许复位
        if (!_isSensorFault)
        {
            AlarmState.SensorError = false;
        }

        // 外部联锁条件恢复或者操作员主动将流量设为了 0，才允许复位
        if (_cfg.CanOperate() || _targetCmdSp == 0f)
        {
            AlarmState.InterlockLostError = false;
        }

        AlarmState.InterlockError = false;

        // 目标意图已被改为 0（停止供气），或者物理流量已经回落到了公差以内，才允许复位
        if (_targetCmdSp == 0f || Math.Abs(_filteredPv - _targetCmdSp) <= _cfg.FlowTolerance)
        {
            AlarmState.FlowDeviationError = false;
            _deviationStartTimestampMs = null;
        }

        if (!AlarmState.HasAnyError)
        {
            ChangeState(MfcState.Unknown);
            RaiseInfo(MfcEvents.InfoReset);
        }
    }

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

    protected override void RaiseAlarm(EventBase eventbase, params object[] args)
    {
        base.RaiseAlarm(eventbase, args);
        if (eventbase.Severity == SeverityLevel.Error)
            ChangeState(MfcState.Error);
    }
}

// ==========================================
// 配置类与专属报警状态类
// ==========================================
public sealed class MfcAlarmState
{
    public bool HasAnyWarning => false;

    public bool InterlockError { get; internal set; }
    public bool InterlockLostError { get; internal set; }
    public bool FlowDeviationError { get; internal set; }
    public bool SensorError { get; internal set; }

    public bool HasAnyError => InterlockError || InterlockLostError || FlowDeviationError || SensorError;
}

public class MfcCfg
{
    public required string Name { get; init; }
    public required float Capacity { get; init; } // 满量程 (SCCM/SLM)

    public float FlowTolerance { get; init; } = 2.0f; // 流量允许的误差带绝对值
    public int FlowDeviationTimeoutMs { get; init; } = 3000; // 流量超差容忍时间(建流时间)
    public float FilterAlpha { get; init; } = 1f; // 低通滤波系数

    public required Func<float> ReadPV { get; init; }
    public required Action<float> WriteSP { get; init; }
    public required Func<bool> CanOperate { get; init; }

    public bool Validate()
    {
        return !string.IsNullOrEmpty(Name) && Capacity > 0 && ReadPV != null && WriteSP != null && CanOperate != null;
    }
}

public enum MfcState { Unknown, Off, Controlling, Error }

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
    public static readonly EventBase InfoFlowStarted = new() { EventId = 300, Severity = SeverityLevel.Info, MessageTemplate = "开启并设定流量为 (目标: {0:F1})" };
    public static readonly EventBase InfoFlowStopped = new() { EventId = 302, Severity = SeverityLevel.Info, MessageTemplate = "停止供气" };
    public static readonly EventBase InfoReset = new() { EventId = 303, Severity = SeverityLevel.Info, MessageTemplate = "故障复位" };

    public static readonly EventBase ErrInterlock = new() { EventId = 320, Severity = SeverityLevel.Error, MessageTemplate = "无法供气：外部联锁条件不满足" };
    public static readonly EventBase ErrInterlockLost = new() { EventId = 321, Severity = SeverityLevel.Error, MessageTemplate = "供气中外部联锁条件丢失" };
    public static readonly EventBase ErrFlowDeviation = new() { EventId = 322, Severity = SeverityLevel.Error, MessageTemplate = "流量建流失败或发生严重偏差 (SP: {0:F1}, PV: {1:F1})" };
    public static readonly EventBase ErrSensorError = new() { EventId = 323, Severity = SeverityLevel.Error, MessageTemplate = "流量传感器读数异常 (PV: {0:F1})" };
}

public interface IMfcFactory { CM_MFC Create(MfcCfg cfg); }

public class MfcFactory : IMfcFactory
{
    private readonly IServiceProvider _serviceProvider;
    public MfcFactory(IServiceProvider serviceProvider) => _serviceProvider = serviceProvider;
    public CM_MFC Create(MfcCfg cfg) => ActivatorUtilities.CreateInstance<CM_MFC>(_serviceProvider, cfg);
}