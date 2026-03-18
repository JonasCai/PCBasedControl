using Controller.gRPC;
using Controller.S88;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace Controller.S88;

public abstract class S88EquipmentModuleBase(EquipmentModuleCfg cfg, ILogger<S88EquipmentModuleBase> logger) : IEquipmentModule
{
    // ==========================================
    // IEquipmentModule 接口方法
    // ==========================================
    public string Name => _cfg.Name;
    public EMState State { get; private set; } = EMState.Idle;
    public void ExecuteCommand(InternalCommand command)
    {
        if (command.TargetObject == _cfg.Name)
        {
            _commandQueue.Enqueue(command);
            return;
        }

        if (_cMs.TryGetValue(command.TargetObject, out var cm))
        {
            cm.ExecuteCommand(command);
            return;
        }

        command.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Rejected, $"指令目标未知：{command.TargetUnit}.{command.TargetObject}"));
    }
    public void Refresh(long currentTimestampMs)
    {
        _currentTimestampMs = currentTimestampMs;

        IsNewStep = _stepChangedPending;
        _stepChangedPending = false;

        try
        {
            CheckHardwareInterlocks();

            ProcessCommandQueue();

            if (State == EMState.Busy)
                OnExecute();

            var cache = _cMsCache; // 读取 volatile 引用
            for (int i = 0; i < cache.Length; i++)
            {
                cache[i].Refresh(currentTimestampMs);
            }
        }
        catch (Exception ex)
        {
            if (State != EMState.Fault)
            {
                State = EMState.Fault;
                _logger.LogError(ex, "EM [{Name}] 发生内部异常，强制进入 Fault 状态",Name);
            }
            ToSafe();
        }
    }
    public void ToSafe()
    {
        PurgeCommands();
        State = EMState.Idle;
        var cache = _cMsCache; // 读取 volatile 引用
        for (int i = 0; i < cache.Length; i++)
            cache[i].ToSafe();
    }
    public bool TryGetCm(string name, out IControlModule? cm) => _cMs.TryGetValue(name, out cm);


    // ==========================================
    // 供子类重写的逻辑钩子 (Hooks)
    // ==========================================
    protected virtual void OnExecute() { }
    protected virtual void CheckHardwareInterlocks() { }


    // ==========================================
    // 供子类调用的接口
    // ==========================================
    protected bool IsNewStep { get; private set; }
    protected long StepTime => _currentTimestampMs - _stepStartTimestamp;
    protected int Step
    {
        get => _step;
        set
        {
            if (_step != value)
            {
                _step = value;
                _stepChangedPending = true;
                _stepStartTimestamp = _currentTimestampMs;
            }
        }
    }
    protected bool StepTimeout(long ms) => StepTime > ms;
    protected void RegisterCm(IControlModule cm)
    {
        if (_cMs.TryAdd(cm.Name, cm))
            _cMsCache = _cMs.Values.ToArray();
    }
    protected void RegisterCommandHandler(Command cmdName, Action<InternalCommand> handler) => _commandHandlers[cmdName] = handler;


    // ==========================================
    // 私有成员
    // ==========================================
    private int _step = 0;
    private long _currentTimestampMs;
    private long _stepStartTimestamp;
    private bool _stepChangedPending = true;
    private readonly EquipmentModuleCfg _cfg = cfg;
    private readonly Dictionary<Command, Action<InternalCommand>> _commandHandlers = new();
    private readonly ILogger<S88EquipmentModuleBase> _logger = logger;
    private volatile IControlModule[] _cMsCache = Array.Empty<IControlModule>();
    private readonly ConcurrentDictionary<string, IControlModule> _cMs = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<InternalCommand> _commandQueue = new();
    private void PurgeCommands()
    {
        while (_commandQueue.TryDequeue(out var cmd))
        {
            if (cmd?.CallbackTcs != null)
            {
                cmd.CallbackTcs.TrySetResult(new CommandResult(
                    CommandResultType.Rejected,
                    "指令被系统强制清理，未执行"
                ));
                _logger.LogWarning("指令 [{TargetUnit}.{TargetObject}.{CmdName}] 被系统强制清理，未执行", cmd.TargetUnit, cmd.TargetObject, cmd.CmdName);
            }
        }
    }
    private void ProcessCommandQueue()
    {
        while (_commandQueue.TryDequeue(out var cmd))
        {
            if (cmd.CancelToken.IsCancellationRequested)
            {
                _logger.LogWarning("指令 [{TargetUnit}.{TargetObject}.{CmdName}] 在排队期间已被调用方取消或超时 (3s)，已作为僵尸指令安全丢弃", cmd.TargetUnit, cmd.TargetObject, cmd.CmdName);
                continue;
            }

            // 查表执行
            if (_commandHandlers.TryGetValue(cmd.CmdName, out var handler))
            {
                handler(cmd); // 执行绑定的动作
            }
            else
            {
                cmd.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Rejected, $"指令处理未定义：{cmd.TargetUnit}.{cmd.TargetObject}.{cmd.CmdName}"));
            }
        }

    }
}

public class EquipmentModuleCfg
{
    public required string Name { get; init; }
    public required Func<uint> ReadSafetyDeviceState { get; init; }
}

public enum EMState
{
    Idle,
    Busy,
    Done,
    Fault
}
