using Controller.Common;
using Controller.EventLogger;
using Controller.gRPC;
using Controller.S88;
using System.Collections.Concurrent;

namespace Controller._01.ControlModule;

public class CM_Cylinder : S88ControlModuleBase
{
    public CM_Cylinder(IEventProducer eventProducer, CylinderCfg cfg, ILogger<CM_Cylinder> logger):base(cfg.Name, eventProducer, logger)
    {
        _cfg = cfg;
        RegisterCommandHandlers();

        //初始化防抖器
        long debounceTime = cfg.SensorDebounceTimeMs > 0 ? cfg.SensorDebounceTimeMs : 50;
        _extSensorFilter = new DigitalDebouncer(debounceTime);
        _retSensorFilter = new DigitalDebouncer(debounceTime);

        if (!_cfg.Validate())
            throw new ArgumentException($"气缸[{_cfg.Name}]配置不完整", nameof(_cfg));
    }

    // ==========================================
    // S88ControlModuleBase重写接口
    // ==========================================
    public override bool HasAnyWarning => AlarmState.HasAnyWarning;
    public override bool HasAnyError => State == CylinderState.Error;
    public override void Refresh(long currentTimestampMs)
    {
        _currentTimestampMs = currentTimestampMs;

        // 读取原始信号
        _rawExt = (_cfg.SensorConfig is CylinderSensorConfig.DualSensors or CylinderSensorConfig.ExtendOnly)
                  && _cfg.ReadExtendedSensor != null ? _cfg.ReadExtendedSensor() : false;

        _rawRet = (_cfg.SensorConfig is CylinderSensorConfig.DualSensors or CylinderSensorConfig.RetractOnly)
                  && _cfg.ReadRetractedSensor != null ? _cfg.ReadRetractedSensor() : false;

        // 防抖过滤
        _physicalExt = _extSensorFilter.Filter(_rawExt, currentTimestampMs);
        _physicalRet = _retSensorFilter.Filter(_rawRet, currentTimestampMs);

        // 传感器状态推算
        switch (_cfg.SensorConfig)
        {
            case CylinderSensorConfig.DualSensors:
                _isExtended = _physicalExt;
                _isRetracted = _physicalRet;
                break;

            case CylinderSensorConfig.ExtendOnly:
                _isExtended = _physicalExt;
                // 伸出位传感器熄灭 + 处于缩回状态/或正在缩回且时间达标
                _isRetracted = !_physicalExt && (State == CylinderState.Retracted ||
                              (State == CylinderState.ToRetractBusy && _toRetractElapsedTime >= _cfg.VirtualRetractDelayMs));
                break;

            case CylinderSensorConfig.RetractOnly:
                _isRetracted = _physicalRet;
                // 缩回位传感器熄灭 + 处于伸出状态/或正在伸出且时间达标
                _isExtended = !_physicalRet && (State == CylinderState.Extended ||
                              (State == CylinderState.ToExtendBusy && _toExtendElapsedTime >= _cfg.VirtualExtendDelayMs));
                break;

            case CylinderSensorConfig.TimeBased:
                // 纯时间估算
                _isExtended = State == CylinderState.Extended || (State == CylinderState.ToExtendBusy && _toExtendElapsedTime >= _cfg.VirtualExtendDelayMs);
                _isRetracted = State == CylinderState.Retracted || (State == CylinderState.ToRetractBusy && _toRetractElapsedTime >= _cfg.VirtualRetractDelayMs);
                break;
        }

        // 处理指令队列
        ProcessCommandQueue();

        // 如果处于 Error 态，拦截除 ToSafe 以外的所有新物理动作！
        // 必须按 Reset 清除逻辑错误并切入 Unknown 后，才允许向硬件发新指令。
        if (State == CylinderState.Error && _targetCmd != CylinderCmd.ToSafe)
        {
            // 处于故障态时，维持发生故障那一刻的物理输出，不响应新意图
        }
        else
        {
            switch (_targetCmd)
            {
                case CylinderCmd.ToSafe:
                    _cfg.Actuate(CylinderCmd.ToSafe);
                    break;
                case CylinderCmd.Retract:
                    _cfg.Actuate(CylinderCmd.Retract);
                    break;
                case CylinderCmd.Extend:
                    _cfg.Actuate(CylinderCmd.Extend);
                    break;
            }
        }

        // 状态机
        switch (State)
        {
            case CylinderState.Unknown:
                if (_isExtended && !_isRetracted) ChangeState(CylinderState.Extended);
                else if (!_isExtended && _isRetracted) ChangeState(CylinderState.Retracted);
                else if (_targetCmd == CylinderCmd.Extend)
                {
                    ChangeState(CylinderState.ToExtendBusy);
                    _toExtendStartTimestampMs = _currentTimestampMs;
                }
                else if (_targetCmd == CylinderCmd.Retract)
                {
                    ChangeState(CylinderState.ToRetractBusy);
                    _toRetractStartTimestampMs = _currentTimestampMs;
                }
                break;

            case CylinderState.ToExtendBusy:
                _toExtendElapsedTime = _currentTimestampMs - _toExtendStartTimestampMs;
                if (_isExtended)
                {
                    _extendCount++;
                    ChangeState(CylinderState.Extended);
                    RaiseInfo(CylinderEvents.InfoExtendedDone, _toExtendElapsedTime); //伸出到位 (耗时 {ToExtendElapsedTime} ms
                }
                break;

            case CylinderState.ToRetractBusy:
                _toRetractElapsedTime = _currentTimestampMs - _toRetractStartTimestampMs;
                if (_isRetracted)
                {
                    _retractCount++;
                    ChangeState(CylinderState.Retracted);
                    RaiseInfo(CylinderEvents.InfoRetractedDone, _toRetractElapsedTime); //缩回到位 (耗时 {ToRetractElapsedTime} ms
                }
                break;

            case CylinderState.Extended:
                // 如果信号丢失且没发生联锁错误，重新以此目标触发动作
                if (!_isExtended)
                {
                    RaiseInfo(CylinderEvents.InfoExtSensorLost);
                    ChangeState(CylinderState.ToExtendBusy);
                    _toExtendStartTimestampMs = _currentTimestampMs;
                }
                break;

            case CylinderState.Retracted:
                // 如果信号丢失且没发生联锁错误，重新以此目标触发动作
                if (!_isRetracted)
                {
                    RaiseInfo(CylinderEvents.InfoRetSensorLost); //缩回位信号丢失，尝试重新检测
                    ChangeState(CylinderState.ToRetractBusy);
                    _toRetractStartTimestampMs = _currentTimestampMs;
                }
                break;

            case CylinderState.Error:
                break;
        }

        //统一的报警集中评估与映射
        AlarmHandler();
    }
    public override void ToSafe()
    {
        PurgeCommands();
        _targetCmd = CylinderCmd.ToSafe;
        _cfg.Actuate(CylinderCmd.ToSafe);
        ChangeState(CylinderState.Unknown);
    }

    // ==========================================
    // 外部接口
    // ==========================================
    public void MoveRetract()
    {
        if (State == CylinderState.Retracted || State == CylinderState.ToRetractBusy)
            return;

        if (!_cfg.CanRetract())
        {
            AlarmState.RetractInterlockError = true;
            return;
        }

        _targetCmd = CylinderCmd.Retract;

        if (State != CylinderState.Error)
        {
            RaiseInfo(CylinderEvents.InfoCmdRetract);
            ChangeState(CylinderState.ToRetractBusy);
            _toRetractStartTimestampMs = _currentTimestampMs;
        }
    }
    public void MoveExtend()
    {
        if (State == CylinderState.Extended || State == CylinderState.ToExtendBusy)
            return;

        if (!_cfg.CanExtend())
        {
            AlarmState.ExtendInterlockError = true;
            return;
        }

        _targetCmd = CylinderCmd.Extend;
        if (State != CylinderState.Error)
        {
            RaiseInfo(CylinderEvents.InfoCmdExtend);//收到伸出指令，开始执行
            ChangeState(CylinderState.ToExtendBusy);
            _toExtendStartTimestampMs = _currentTimestampMs;
        }
    }
    public CylinderState State { get; private set; } = CylinderState.Unknown;
    public CylinderAlarmState AlarmState = new();
    public CylinderSnapshot GetSnapshot() => new()
    {
        Name = _cfg.Name,
        TargetCmd = _targetCmd,
        State = State,
        AlarmState = AlarmState,
        ExtSensor = _isExtended,
        RetSensor = _isRetracted,
        CanExtend = _cfg.CanExtend(),
        CanRetract = _cfg.CanRetract(),
        ExtendET = _toExtendElapsedTime,
        RetractET = _toRetractElapsedTime,
        ExtendCnt = _extendCount,
        RetractCnt = _retractCount
    };

    // ==========================================
    // 私有成员
    // ==========================================
    private long _toExtendStartTimestampMs, _toRetractStartTimestampMs, _currentTimestampMs, _toRetractElapsedTime, _toExtendElapsedTime;
    private readonly CylinderCfg _cfg;
    private DigitalDebouncer _extSensorFilter, _retSensorFilter;
    private int _extendCount, _retractCount;
    private bool _isExtended, _isRetracted, _rawExt, _rawRet, _physicalExt, _physicalRet;
    private CylinderCmd _targetCmd = CylinderCmd.ToSafe;
    private void AlarmHandler()
    {
        AlarmState.LifeTimeReached = _extendCount > _cfg.LifetimeSP;

        if (_isExtended && _isRetracted) AlarmState.SensorConflict = true;

        if (State == CylinderState.ToExtendBusy && _toExtendElapsedTime > _cfg.ToExtendToutMs)
            AlarmState.ExtendTimeout = true;

        if (State == CylinderState.ToRetractBusy && _toRetractElapsedTime > _cfg.ToRetractToutMs)
            AlarmState.RetractTimeout = true;

        if (!_cfg.CanExtend())
        {
            if (State == CylinderState.ToExtendBusy) AlarmState.ExtendInterlockLostError = true;
            else if (State == CylinderState.Extended) AlarmState.ExtendKeepInterlockLostError = true;
        }

        if (!_cfg.CanRetract())
        {
            if (State == CylinderState.ToRetractBusy) AlarmState.RetractInterlockLostError = true;
            else if (State == CylinderState.Retracted) AlarmState.RetractKeepInterlockLostError = true;
        }

        // 运行中联锁丢失，强制去安全位
        if (AlarmState.ExtendInterlockLostError || AlarmState.ExtendKeepInterlockLostError ||
            AlarmState.RetractInterlockLostError || AlarmState.RetractKeepInterlockLostError)
        {
            _targetCmd = CylinderCmd.ToSafe;
        }

        if (AlarmState.LifeTimeReached) RaiseAlarm(CylinderEvents.WarningLifetimeReached, _extendCount, _cfg.LifetimeSP);
        else TryClearAlarm(CylinderEvents.WarningLifetimeReached);

        if (AlarmState.SensorConflict) RaiseAlarm(CylinderEvents.ErrSensorConflict);
        else TryClearAlarm(CylinderEvents.ErrSensorConflict);

        if (AlarmState.ExtendTimeout) RaiseAlarm(CylinderEvents.ErrExtendTimeout, _cfg.ToExtendToutMs);
        else TryClearAlarm(CylinderEvents.ErrExtendTimeout);

        if (AlarmState.RetractTimeout) RaiseAlarm(CylinderEvents.ErrRetractTimeout, _cfg.ToRetractToutMs);
        else TryClearAlarm(CylinderEvents.ErrRetractTimeout);

        if (AlarmState.ExtendInterlockError) RaiseAlarm(CylinderEvents.ErrExtendInterlock);
        else TryClearAlarm(CylinderEvents.ErrExtendInterlock);

        if (AlarmState.ExtendInterlockLostError) RaiseAlarm(CylinderEvents.ErrExtendInterlockLost);
        else TryClearAlarm(CylinderEvents.ErrExtendInterlockLost);

        if (AlarmState.ExtendKeepInterlockLostError) RaiseAlarm(CylinderEvents.ErrExtendKeepInterlockLost);
        else TryClearAlarm(CylinderEvents.ErrExtendKeepInterlockLost);

        if (AlarmState.RetractInterlockError) RaiseAlarm(CylinderEvents.ErrRetractInterlock);
        else TryClearAlarm(CylinderEvents.ErrRetractInterlock);

        if (AlarmState.RetractInterlockLostError) RaiseAlarm(CylinderEvents.ErrRetractInterlockLost);
        else TryClearAlarm(CylinderEvents.ErrRetractInterlockLost);

        if (AlarmState.RetractKeepInterlockLostError) RaiseAlarm(CylinderEvents.ErrRetractKeepInterlockLost);
        else TryClearAlarm(CylinderEvents.ErrRetractKeepInterlockLost);

        if (AlarmState.HasAnyError && State != CylinderState.Error)
        {
            ChangeState(CylinderState.Error);
        }
    }
    private void Reset()
    {
        if (State != CylinderState.Error) return;

        if (!(_isExtended && _isRetracted))
            AlarmState.SensorConflict = false;

        // 伸出超时清除条件：已物理伸出 或 目标意图已改变
        if (_isExtended || _targetCmd != CylinderCmd.Extend)
            AlarmState.ExtendTimeout = false;

        if (_isRetracted || _targetCmd != CylinderCmd.Retract)
            AlarmState.RetractTimeout = false;

        // 外部联锁条件恢复时 或 目标意图已改变，允许清除对应的联锁报警
        if (_cfg.CanExtend() || _targetCmd != CylinderCmd.Extend)
        {
            AlarmState.ExtendInterlockLostError = false;
            AlarmState.ExtendKeepInterlockLostError = false;
        }

        if (_cfg.CanRetract() || _targetCmd != CylinderCmd.Retract)
        {
            AlarmState.RetractInterlockLostError = false;
            AlarmState.RetractKeepInterlockLostError = false;
        }

        AlarmState.RetractInterlockError = false;
        AlarmState.ExtendInterlockError = false;

        // 如果所有的 Latch 都被成功清理，脱离 Error 态
        if (!AlarmState.HasAnyError)
        {
            ChangeState(CylinderState.Unknown);
            RaiseInfo(CylinderEvents.InfoReset);
        }
    }
    private void RegisterCommandHandlers()
    {
        RegisterCommandHandler(Command.Extend, cmd => { MoveExtend(); cmd.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Accepted, string.Empty)); });
        RegisterCommandHandler(Command.Retract, cmd => { MoveRetract(); cmd.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Accepted, string.Empty)); });
        RegisterCommandHandler(Command.Reset, cmd => { Reset(); cmd.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Accepted, string.Empty)); });
        RegisterCommandHandler(Command.ResetStatistics, cmd => { ResetStatistics(); cmd.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Accepted, string.Empty)); });

    }
    private void ChangeState(CylinderState newState)
    {
        if (State == newState) return;
        State = newState;
    }
    private void ResetStatistics()
    {
        _extendCount = 0;
        _retractCount = 0;
        RaiseInfo(CylinderEvents.InfoClearStats); //动作次数累计清零
    }
    protected override void RaiseAlarm(EventBase eventbase, params object[] args)
    {
        base.RaiseAlarm(eventbase, args);

        if (eventbase.Severity == SeverityLevel.Error)
            ChangeState(CylinderState.Error);
    }

}

// 气缸的传感器配置类型
public enum CylinderSensorConfig { DualSensors, ExtendOnly, RetractOnly, TimeBased }

public class CylinderCfg
{
    public required string Name { get; init; }

    // 硬件形态配置
    public CylinderSensorConfig SensorConfig { get; init; } = CylinderSensorConfig.DualSensors;

    // 虚拟行程推算时间 (当没有对应传感器时，指令下发后多久认为到位)
    public int VirtualExtendDelayMs { get; init; } = 2000;
    public int VirtualRetractDelayMs { get; init; } = 2000;

    public int ToExtendToutMs { get; init; } = 10000;
    public int ToRetractToutMs { get; init; } = 10000;
    public int LifetimeSP { get; init; } = 1000000;
    public int SensorDebounceTimeMs { get; init; } = 50;

    public required Action<CylinderCmd> Actuate { get; init; }
    public required Func<bool> CanExtend { get; init; }
    public required Func<bool> CanRetract { get; init; }

    public Func<bool>? ReadExtendedSensor { get; init; }
    public Func<bool>? ReadRetractedSensor { get; init; }

    public bool Validate()
    {
        bool valid = !string.IsNullOrEmpty(Name) && Actuate != null && CanExtend != null && CanRetract != null;
        if (SensorConfig is CylinderSensorConfig.DualSensors or CylinderSensorConfig.ExtendOnly) valid &= ReadExtendedSensor != null;
        if (SensorConfig is CylinderSensorConfig.DualSensors or CylinderSensorConfig.RetractOnly) valid &= ReadRetractedSensor != null;
        return valid;
    }
}

public enum CylinderCmd { ToSafe, Retract, Extend }
public enum CylinderState { Unknown, ToExtendBusy, ToRetractBusy, Extended, Retracted, Error }

public sealed class CylinderAlarmState
{
    public bool LifeTimeReached { get; internal set; }
    public bool HasAnyWarning => LifeTimeReached;

    public bool ExtendInterlockError { get; internal set; }
    public bool RetractInterlockError { get; internal set; }
    public bool ExtendInterlockLostError { get; internal set; }
    public bool RetractInterlockLostError { get; internal set; }
    public bool ExtendKeepInterlockLostError { get; internal set; }
    public bool RetractKeepInterlockLostError { get; internal set; }

    public bool ExtendTimeout { get; internal set; }
    public bool RetractTimeout { get; internal set; }
    public bool SensorConflict { get; internal set; }

    public bool HasAnyError => ExtendInterlockError || RetractInterlockError ||
                               ExtendInterlockLostError || RetractInterlockLostError ||
                               ExtendKeepInterlockLostError || RetractKeepInterlockLostError ||
                               ExtendTimeout || RetractTimeout || SensorConflict;
}

public static class CylinderEvents
{
    public static readonly EventBase InfoClearStats = new() { EventId = 700, Severity = SeverityLevel.Info, MessageTemplate = "动作次数累计清零" };
    public static readonly EventBase InfoCmdRetract = new() { EventId = 701, Severity = SeverityLevel.Info, MessageTemplate = "指令:开始缩回" };
    public static readonly EventBase InfoCmdExtend = new() { EventId = 702, Severity = SeverityLevel.Info, MessageTemplate = "指令:开始伸出" };
    public static readonly EventBase InfoReset = new() { EventId = 703, Severity = SeverityLevel.Info, MessageTemplate = "故障复位完成" };
    public static readonly EventBase InfoExtendedDone = new() { EventId = 704, Severity = SeverityLevel.Info, MessageTemplate = "伸出到位 (耗时 {0} ms)" };
    public static readonly EventBase InfoRetractedDone = new() { EventId = 705, Severity = SeverityLevel.Info, MessageTemplate = "缩回到位 (耗时 {0} ms)" };
    public static readonly EventBase InfoExtSensorLost = new() { EventId = 706, Severity = SeverityLevel.Info, MessageTemplate = "伸出位信号丢失，尝试维持" };
    public static readonly EventBase InfoRetSensorLost = new() { EventId = 707, Severity = SeverityLevel.Info, MessageTemplate = "缩回位信号丢失，尝试维持" };

    public static readonly EventBase ErrRetractInterlock = new() { EventId = 720, Severity = SeverityLevel.Error, MessageTemplate = "无法缩回：外部联锁不满足" };
    public static readonly EventBase ErrExtendInterlock = new() { EventId = 721, Severity = SeverityLevel.Error, MessageTemplate = "无法伸出：外部联锁不满足" };
    public static readonly EventBase ErrSensorConflict = new() { EventId = 722, Severity = SeverityLevel.Error, MessageTemplate = "传感器异常：原位和动位传感器同时亮" };
    public static readonly EventBase ErrExtendInterlockLost = new() { EventId = 723, Severity = SeverityLevel.Error, MessageTemplate = "伸出动作中联锁丢失" };
    public static readonly EventBase ErrExtendTimeout = new() { EventId = 724, Severity = SeverityLevel.Error, MessageTemplate = "伸出动作超时 (> {0} ms)" };
    public static readonly EventBase ErrRetractInterlockLost = new() { EventId = 725, Severity = SeverityLevel.Error, MessageTemplate = "缩回动作中联锁丢失" };
    public static readonly EventBase ErrRetractTimeout = new() { EventId = 726, Severity = SeverityLevel.Error, MessageTemplate = "缩回动作超时 (> {0} ms)" };
    public static readonly EventBase ErrExtendKeepInterlockLost = new() { EventId = 727, Severity = SeverityLevel.Error, MessageTemplate = "伸出保持中联锁丢失" };
    public static readonly EventBase ErrRetractKeepInterlockLost = new() { EventId = 728, Severity = SeverityLevel.Error, MessageTemplate = "缩回保持中联锁丢失" };
    public static readonly EventBase WarningLifetimeReached = new() { EventId = 740, Severity = SeverityLevel.Warning, MessageTemplate = "寿命到达 (PV:{0} , SP:{1})" };
}

public sealed class CylinderSnapshot
{
    public required string Name { get; init; }
    public required CylinderCmd TargetCmd { get; init; }
    public required CylinderState State { get; init; }
    public required CylinderAlarmState AlarmState { get; init; } = new();
    public required bool ExtSensor { get; init; }
    public required bool RetSensor { get; init; }
    public required bool CanExtend { get; init; }
    public required bool CanRetract { get; init; }
    public required long ExtendET { get; init; }
    public required long RetractET { get; init; }
    public required int ExtendCnt { get; init; }
    public required int RetractCnt { get; init; }
}

public interface ICylinderFactory
{
    CM_Cylinder Create(CylinderCfg cfg);
}

public class CylinderFactory : ICylinderFactory
{
    private readonly IServiceProvider _sp;
    public CylinderFactory(IServiceProvider sp) => _sp = sp;
    public CM_Cylinder Create(CylinderCfg cfg) => ActivatorUtilities.CreateInstance<CM_Cylinder>(_sp, cfg);
}
