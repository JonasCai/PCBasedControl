using Controller.EventLogger;
using Controller.gRPC;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Controller.S88;

public abstract class S88ProcessCellBase:S88ObjectBase
{
    public S88ProcessCellBase(ProcessCellCfg cfg, IEventProducer eventProducer, ILogger logger) : base(cfg.Name, eventProducer, logger) 
    {
        _cfg = cfg;
    }

    // ==========================================
    // ...
    // ==========================================
    public override bool HasAnyWarning => false;
    public override bool HasAnyError => false;
    public override void ExecuteCommand(InternalCommand command)
    {
        if (string.IsNullOrEmpty(command.TargetUnit))
        {
            base.ExecuteCommand(command);
            return;
        }
            
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
    public override void ToSafe()
    {
        PurgeCommands();
        var cache = _unitsCache;
        for (int i = 0; i < cache.Length; i++)
            cache[i].ToSafe(); // 让各 CM 立即切断物理输出 (例如阀门关闭，电机掉使能等)
    }
    public override void Refresh(long currentTimestampMs) //周期刷新(Cycle Logic)
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


    // ==========================================
    // 外部接口
    // ==========================================
    public bool TryGetUnit(string unitName, out S88UnitBase? unit)
        => _units.TryGetValue(unitName, out unit);

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

        RegisterCommandHandler(Command.Start, action);
        RegisterCommandHandler(Command.Stop, action);
        RegisterCommandHandler(Command.Reset, action);
        RegisterCommandHandler(Command.SetMode, action);
    }


    // ==========================================
    // 私有成员
    // ========================================== 
    private void HandleButtonLogic(long ts)
    {
        // 启动逻辑
        if (_startBtnState && !_startBtnStateOld)
        {
            RaiseInfo(ProcellCellEvents.InfoStartBtnTriggered);
            BroadcastToUnits(Command.Start);
        }

        // 停止逻辑 (NC 触发)
        if (!_stopBtnState && _stopBtnStateOld)
        {
            RaiseInfo(ProcellCellEvents.InfoStopBtnTriggered);
            BroadcastToUnits(Command.Stop);
        }

        // 急停逻辑 (NC 触发)
        if (!_eStopBtnState)
        {
            if(_eStopBtnStateOld)
            {
                RaiseAlarm(ProcellCellEvents.InfoEStopBtnTriggered);

                // 只有按下的瞬间发一次广播指令即可，底层的 Unit 和 EM 会 Latch 住这个急停状态
                BroadcastToUnits(Command.EStop);
            }
        }

        // 急停取消 (NC 触发) 
        if (_eStopBtnState && !_eStopBtnStateOld)
        {
            TryClearAlarm(ProcellCellEvents.InfoEStopBtnTriggered);
        }

        // 手自动模式同步
        if (_manualAutoState != _manualAutoStateOld)
        {
            string modeStr = _manualAutoState ? S88Mode.Automatic.ToString() : S88Mode.Manual.ToString();
            RaiseInfo(ProcellCellEvents.InfoManualAutoSwitchTriggered, modeStr);

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

    private bool _startBtnState, _stopBtnState, _resetBtnState, _eStopBtnState, _manualAutoState;
    private bool _startBtnStateOld, _stopBtnStateOld, _resetBtnStateOld, _eStopBtnStateOld, _manualAutoStateOld;
    private readonly ProcessCellCfg _cfg;
    private volatile S88UnitBase[] _unitsCache = Array.Empty<S88UnitBase>();
    private readonly Dictionary<string, S88UnitBase> _units = new(StringComparer.OrdinalIgnoreCase);
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
