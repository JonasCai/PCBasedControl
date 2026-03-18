using Controller.EventLogger;
using Controller.gRPC;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static MongoDB.Bson.Serialization.Serializers.SerializerHelper;

namespace Controller.S88;

public abstract class S88ProcessCellBase(ProcessCellCfg cfg, EventLoggerService eventProducer, ILogger<S88ProcessCellBase> logger) : IProcessCell
{
    // ==========================================
    // IProcessCell 接口方法
    // ==========================================
    public string Name { get; } = cfg.Name;
    public void ExecuteCommand(InternalCommand command)
    {
        if (string.IsNullOrEmpty(command.TargetUnit))
            _commandQueue.Enqueue(command);

        if (_units.TryGetValue(command.TargetUnit, out var unit))
        {
            if (unit.State != S88State.SystemFault)
            {
                unit.ExecuteCommand(command);
                return;
            }
            command.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Rejected, $"{command.TargetUnit} 处于 SystemFault 状态"));
            return;
        }
        command.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Rejected, $"指令目标未知：{command.TargetUnit}"));
    }
    public void ToSafe()
    {
        PurgeCommands();
        var cache = _unitsCache;
        for (int i = 0; i < cache.Length; i++)
            cache[i].ToSafe(); // 让各 CM 立即切断物理输出 (例如阀门关闭，电机掉使能等)
    }
    public void Refresh(long currentTimestampMs) //周期刷新(Cycle Logic)
    {
        // 处理指令队列（确保在同一线程执行所有逻辑）
        ProcessCommandQueue();

        // 更新物理按钮状态
        UpdatePhysicalButtons();

        // 边沿检测与系统级逻辑
        HandleButtonLogic(currentTimestampMs);

        // 驱动所有 Unit 扫描
        var cache = _unitsCache; // 读取 volatile 引用
        for (int i = 0; i < cache.Length; i++)
        {
            cache[i].Refresh(currentTimestampMs);
        }

        // 更新状态位
        SaveOldButtonStates();
    }
    public bool TryGetUnit(string unitName, out S88UnitBase? unit)
        => _units.TryGetValue(unitName, out unit);


    // ==========================================
    // 外部接口
    // ==========================================


    // ==========================================
    // 供子类调用的辅助方法
    // ==========================================
    protected void RegisterMember(S88UnitBase unit)
    {
        if (_units.TryAdd(unit.Name, unit))
        {
            // 每次注册新设备时，更新一次缓存。
            _unitsCache = _units.Values.ToArray();
        }
    }
    protected readonly IEventProducer _eventProducer = eventProducer;
    protected readonly ILogger<S88ProcessCellBase> _logger = logger;
    protected virtual void RegisterCommandHandlers()
    {
        Action<InternalCommand> action = cmd =>
        {
            cmd.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Accepted, string.Empty));
            var cache = _unitsCache;
            for (int i = 0; i < cache.Length; i++)
                if (cache[i].IsActive)
                    cache[i].ExecuteCommand(cmd with { TargetUnit = cache[i].Name, CallbackTcs = null });
        };

        _commandHandlers[Command.Start] = action;
        _commandHandlers[Command.Stop] = action;
        _commandHandlers[Command.Reset] = action;
        _commandHandlers[Command.SetMode] = action;
    }


    // ==========================================
    // 私有成员
    // ==========================================
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
    
    private void HandleButtonLogic(long ts)
    {
        // 启动逻辑
        if (_startBtnState && !_startBtnStateOld)
        {
            _eventProducer.SendInfo(Name, ProcellCellEvents.InfoStartBtnTriggered);
            BroadcastToUnits(Command.Start);
        }

        // 停止逻辑 (NC 触发)
        if (!_stopBtnState && _stopBtnStateOld)
        {
            _eventProducer.SendInfo(Name, ProcellCellEvents.InfoStopBtnTriggered);
            BroadcastToUnits(Command.Stop);
        }

        // 急停逻辑 (NC 触发)
        if (!_eStopBtnState)
        {
            if(_eStopBtnStateOld)
            {
                _eStopGuid = Guid.NewGuid();
                _eventProducer.RaiseAlarm(Name, _eStopGuid, ProcellCellEvents.InfoEStopBtnTriggered);
            }
            BroadcastToUnits(Command.EStop);
        }

        // 急停取消 (NC 触发) 
        if (_eStopBtnState && !_eStopBtnStateOld)
        {
            _eventProducer.ClearAlarm(Name, _eStopGuid, ProcellCellEvents.InfoEStopBtnTriggered);
        }

        // 手自动模式同步
        if (_manualAutoState != _manualAutoStateOld)
        {
            string modeStr = _manualAutoState ? S88Mode.Automatic.ToString() : S88Mode.Manual.ToString();
            _eventProducer.SendInfo(Name, ProcellCellEvents.InfoManualAutoSwitchTriggered, modeStr);

            var para = new Dictionary<string, string> { { "NewMode", modeStr } };
            BroadcastToUnits(Command.SetMode, para);
        }
    }
    private void BroadcastToUnits(Command cmdName, Dictionary<string, string>? args = null)
    {
        var cache = _unitsCache;
        for (int i = 0; i < cache.Length; i++)
            if (cache[i].IsActive)
                cache[i].ExecuteCommand(new InternalCommand(cache[i].Name, cache[i].Name, cmdName, args ?? new()));
    }
    private void UpdatePhysicalButtons()
    {
        _startBtnState = _cfg.GetStartBtnState();
        _stopBtnState = _cfg.GetStopBtnState();
        _resetBtnState = _cfg.GetResetBtnState();
        _eStopBtnState = _cfg.GetEStopBtnState();
        _manualAutoState = _cfg.GetManualAutoState();
    }
    private void SaveOldButtonStates()
    {
        _startBtnStateOld = _startBtnState;
        _stopBtnStateOld = _stopBtnState;
        _resetBtnStateOld = _resetBtnState;
        _eStopBtnStateOld = _eStopBtnState;
        _manualAutoStateOld = _manualAutoState;
    }

    private Guid _eStopGuid;
    private bool _startBtnState, _stopBtnState, _resetBtnState, _eStopBtnState, _manualAutoState;
    private bool _startBtnStateOld, _stopBtnStateOld, _resetBtnStateOld, _eStopBtnStateOld, _manualAutoStateOld;
    private readonly ProcessCellCfg _cfg = cfg;
    private volatile S88UnitBase[] _unitsCache = Array.Empty<S88UnitBase>();
    private readonly Dictionary<Command, Action<InternalCommand>> _commandHandlers = new();
    private readonly ConcurrentDictionary<string, S88UnitBase> _units = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<InternalCommand> _commandQueue = new();
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

public class ProcessCellCfg
{
    public required string Name { get; init; }
    public required Func<bool> GetManualAutoState { get; init; }
    public required Func<bool> GetStartBtnState { get; init; }
    public required Func<bool> GetStopBtnState { get; init; }
    public required Func<bool> GetResetBtnState { get; init; }
    public required Func<bool> GetEStopBtnState { get; init; }
}

public static partial class ProcellCellEvents
{
    public static readonly EventBase InfoStartBtnTriggered = new()
    {
        EventId =1,
        Severity = SeverityLevel.Info,
        MessageTemplate = "启动按钮触发"
    };

    public static readonly EventBase InfoStopBtnTriggered = new()
    {
        EventId = 2,
        Severity = SeverityLevel.Info,
        MessageTemplate = "停止按钮触发"
    };

    public static readonly EventBase InfoResetBtnTriggered = new()
    {
        EventId = 3,
        Severity = SeverityLevel.Info,
        MessageTemplate = "复位按钮触发"
    };

    public static readonly EventBase InfoEStopBtnTriggered = new()
    {
        EventId = 4,
        Severity = SeverityLevel.Info,
        MessageTemplate = "急停按钮触发"
    };

    public static readonly EventBase InfoManualAutoSwitchTriggered = new()
    {
        EventId = 5,
        Severity = SeverityLevel.Info,
        MessageTemplate = "手自动切换旋钮触发：{0}"
    };
}
