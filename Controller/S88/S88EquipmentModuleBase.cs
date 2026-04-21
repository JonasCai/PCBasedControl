using Controller._01.ControlModule;
using Controller.EventLogger;
using Controller.gRPC;
using Controller.Hardware;
using Controller.S88;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace Controller.S88;

public abstract class S88EquipmentModuleBase : S88ObjectBase
{
    public S88EquipmentModuleBase(string name, IEventProducer eventProducer, ILogger logger) : base(name, eventProducer, logger) {}

    // ==========================================
    // ...
    // ==========================================
    public override bool HasAnyWarning => false;
    public override bool HasAnyError => State == EMState.Error;
    public EMState State { get; private set; } = EMState.Idle;
    public override void ExecuteCommand(InternalCommand command)
    {
        if (command.TargetObject == Name)
        {
            base.ExecuteCommand(command);
            return;
        }

        if (_cMs.TryGetValue(command.TargetObject, out var cm))
        {
            cm.ExecuteCommand(command);
            return;
        }

        command.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Rejected, $"指令目标未知：{command.TargetUnit}.{command.TargetObject}"));
    }
    public override void Refresh(long currentTimestampMs)
    {
        CurrentTimestampMs = currentTimestampMs;

        IsNewStep = _stepChangedPending;
        _stepChangedPending = false;

        try
        {
            ProcessCommandQueue();

            OnExecute();

            var cache = _cMsCache; // 读取 volatile 引用
            for (int i = 0; i < cache.Length; i++)
            {
                cache[i].Refresh(currentTimestampMs);
            }

            AlarmHandler();
        }
        catch (Exception ex)
        {
            if (State != EMState.Error)
            {
                OnAbort(); 
                ChangeState(EMState.Error);
                LogError(ex, "EM [{Name}] 发生内部未知异常，强制进入 Error 状态", Name);
            }
            ToSafe();
        }
    }
    public override void ToSafe()
    {
        PurgeCommands();
        ChangeState(EMState.Idle);
        var cache = _cMsCache;
        for (int i = 0; i < cache.Length; i++)
            cache[i].ToSafe();
    }
    public bool TryGetCm(string name, out S88ControlModuleBase? cm) => _cMs.TryGetValue(name, out cm);


    // ==========================================
    // 供子类重写的逻辑钩子 (Hooks)
    // ==========================================
    protected virtual void OnExecute() { }
    protected virtual void OnAbort() { }
    protected virtual void AlarmHandler() {}
    protected virtual void Reset(InternalCommand cmd)
    {
        // 将 Reset 指令透传给底层所有 CM
        var cache = _cMsCache;
        for (int i = 0; i < cache.Length; i++)
        {
            // 为每个 CM 创建独立的 Command 副本，避免引用冲突
            cache[i].ExecuteCommand(cmd with { CallbackTcs = new(), CancelToken = new() });
        }
    }

    // ==========================================
    // 供子类调用的接口
    // ==========================================
    protected bool IsNewStep { get; private set; }
    protected long CurrentTimestampMs { get; private set; }
    protected long StepTime => CurrentTimestampMs - _stepStartTimestamp;
    protected int Step
    {
        get => _step;
        set
        {
            if (_step != value)
            {
                _step = value;
                _stepChangedPending = true;
                _stepStartTimestamp = CurrentTimestampMs;
            }
        }
    }
    protected bool StepTimeout(long ms) => StepTime > ms;
    protected void RegisterCm(S88ControlModuleBase cm)
    {
        if (_cMs.TryAdd(cm.Name, cm))
            _cMsCache = _cMs.Values.ToArray();
    }
    protected bool HasAnyChildError()
    {
        var cache = _cMsCache;
        for (int i = 0; i < cache.Length; i++)
        {
            if (cache[i].HasAnyError)
                return true;
        }
        return false;
    }
    protected override void RaiseAlarm(EventBase eventbase, params object[] args)
    {
        base.RaiseAlarm(eventbase, args);

        if (eventbase.Severity == SeverityLevel.Error && State != EMState.Error)
        {
            OnAbort();
            ChangeState(EMState.Error);
        }
    }
    protected void ChangeState(EMState newState)
    {
        if (State == newState) return;
        State = newState;
    }
    protected CM_Cylinder RegisterCylinder(
        string name,
        ICylinderFactory factory,
        Action<CylinderCmd> actuate,
        CylinderSensorConfig sensorConfig = CylinderSensorConfig.DualSensors, // 虚拟传感器配置
        Func<bool>? readExtSensor = null,
        Func<bool>? readRetSensor = null,
        int virtualSensorDelayMs = 2000, // 虚拟传感器推算时间
        Func<bool>? canExtend = null,
        Func<bool>? canRetract = null)
    {
        var cfg = new CylinderCfg
        {
            Name = name,
            SensorConfig = sensorConfig,
            VirtualExtendDelayMs = virtualSensorDelayMs,
            Actuate = actuate,
            ReadExtendedSensor = readExtSensor,
            ReadRetractedSensor = readRetSensor,
            CanExtend = canExtend ?? (() => true),
            CanRetract = canRetract ?? (() => true)
        };
        var cylinder = factory.Create(cfg);
        RegisterCm(cylinder);
        return cylinder;
    }

    protected CM_Servo RegisterServo(
        string name,
        IServoFactory factory,
        ushort axisId, ushort homeMode,
        double softLimitPos, double softLimitNeg,
        Func<ushort, AxisStatus> readAxisStatus,
        Action<ushort> clearAxisError,
        Action<ushort, bool> actuateEnable,
        Action<ushort, bool> actuateStop,
        Action<ushort, ushort> actuateHome,
        Action<ushort, double, double, double> moveAbs,
        Action<ushort, double, double, double> moveRel,
        Action<ushort, double, double> moveVel,
        Action<ushort, double, double> changeVel,
        Action<ushort, double> setTorque,
        Action<ushort, double> changeTorque,
        Func<bool>? canMove = null)
    {
        var cfg = new ServoCfg
        {
            Name = name,
            AxisId = axisId,
            HomeMode = homeMode,
            SoftLimitPositive = softLimitPos,
            SoftLimitNegative = softLimitNeg,
            ReadAxisStatus = readAxisStatus,
            ClearAxisError = clearAxisError,
            ActuateEnable = actuateEnable,
            ActuateStop = actuateStop,
            ActuateHome = actuateHome,
            ActuateMoveAbs = moveAbs,
            ActuateMoveRel = moveRel,
            ActuateVelocity = moveVel,
            ChangeVelocity = changeVel,
            ActuateTorque = setTorque,
            ChangeTorque = changeTorque,
            CanMove = canMove ?? (() => true)
        };

        var servo = factory.Create(cfg);
        RegisterCm(servo);
        return servo;
    }

    protected CM_CheckSensor RegisterCheckSensor(
        string name,
        ICheckSensorFactory factory,
        Func<bool> readSignal,
        long debounceTimeMs = 100,
        long defaultTimeoutMs = 2000,
        bool autoStart = false,
        ExpectedSignalState defaultExpectedSignalState = ExpectedSignalState.Ignore)
    {
        var cfg = new CheckSensorCfg
        {
            Name = name,
            ReadRawSignal = readSignal,
            DebounceTimeMs = debounceTimeMs,
            DefaultExpectedSignalState = defaultExpectedSignalState,
            DefaultMismatchTimeoutMs = defaultTimeoutMs,
            AutoStartMonitoring = autoStart
        };
        var sensor = factory.Create(cfg);
        RegisterCm(sensor);
        return sensor;
    }

    protected CM_Valve RegisterValve(
        string name,
        IValveFactory factory,
        Action toOpen,
        Action toClose,
        DINode16? diNode = null,      // 绑定的数字量输入模块
        int openSensorIndex = -1,     // 开启到位传感器通道号
        int closeSensorIndex = -1,     // 关闭到位传感器通道号
        Func<bool>? canOpen = null, // 开启(ToWork)的联锁条件
        Func<bool>? canClose = null) // 关闭(ToHome)的联锁条件 
    {
        var cfg = new ValveCfg
        {
            Name = name,
            Actuate = cmd =>
            {
                switch (cmd)
                {
                    case ValveCmd.ToOpen:
                        toOpen();
                        break;
                    case ValveCmd.ToClose:
                    case ValveCmd.ToSafe:
                        toClose();
                        break;
                }
            },

            ReadOpenSensor = (diNode != null && openSensorIndex >= 0) ? () => diNode[openSensorIndex] : null,
            ReadClosedSensor = (diNode != null && closeSensorIndex >= 0) ? () => diNode[closeSensorIndex] : null,
            CanOpen = canOpen ?? (() => true),
            CanClose = canClose ?? (() => true)
        };

        var valve = factory.Create(cfg);
        RegisterCm(valve);
        return valve;
    }

    protected CM_MFC RegisterMfc(
        string name,
        IMfcFactory factory,
        MFCNode mfcNode,
        float capacity,
        Func<bool>? canOperate = null)
    {
        var cfg = new MfcCfg()
        {
            Name = name,
            Capacity = capacity,
            CanOperate = canOperate ?? (() => true), // 传入联锁委托
            ReadPV = () => mfcNode.FlowReading,
            WriteSP = sp => mfcNode.FlowSetting = sp,
        };

        var mfc = factory.Create(cfg);
        RegisterCm(mfc);
        return mfc;
    }

    protected CM_TempController RegisterTempController(
        string name,
        ITempControllerFactory factory,
        Func<float> readControlTemperature, // 读取温度的委托 (例如 () => iONodes.TC_01.Temperature )
        Func<float>? readMonitorTemperature = null,
        Action<bool>? setHeaterOn = null, // SSR固态继电器控制
        Action<float>? setDutyRatio = null, // 调功器模拟量控制
        Func<bool>? canExecute = null, // 温控启动的硬联锁
        float maxSafeTemp = 200.0f) // 绝对安全温度
    {
        var cfg = new TempControllerCfg
        {
            Name = name,
            ReadControlTemp = readControlTemperature,
            ReadMonitorTemp = readMonitorTemperature,
            SetHeaterOn = setHeaterOn,
            SetDutyRatio = setDutyRatio,
            CanExecute = canExecute ?? (() => true),
            AbsoluteMaxTempLimit = maxSafeTemp
        };

        var tc = factory.Create(cfg);
        RegisterCm(tc);
        return tc;
    }

    // ==========================================
    // 私有成员
    // ==========================================
    private int _step = 0;
    private long _stepStartTimestamp;
    private bool _stepChangedPending = true;
    private volatile S88ControlModuleBase[] _cMsCache = Array.Empty<S88ControlModuleBase>();
    private readonly Dictionary<string, S88ControlModuleBase> _cMs = new(StringComparer.OrdinalIgnoreCase);

}

public class EquipmentModuleCfg
{
    public required string Name { get; init; }
}

public enum EMState
{
    Idle,
    Busy,
    Error
}
