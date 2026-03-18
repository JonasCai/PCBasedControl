using Controller.EventLogger;
using Controller.gRPC;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Controller.S88;

public abstract class S88UnitBase(UnitCfg cfg, IEventProducer eventProducer, ILogger<S88UnitBase> logger) : IUnit
{
    // ==========================================
    // IUnit 接口方法
    // ==========================================
    public bool IsActive { get; private set; } = true;
    public string Name => _cfg.Name;
    public void Refresh(long currentTimestampMs) //周期刷新(Cycle Logic)
    {
        _currentTimestampMs = currentTimestampMs;
        IsNewStep = _stepChangedPending;
        _stepChangedPending = false;

        try
        {
            // 1.Unit 进入系统故障状态, 不运行
            if (State == S88State.SystemFault)
            {
                ToSafe();// 持续保持安全状态
                return;// 直接返回，不再跑后续逻辑
            }

            // 2. 处理指令
            ProcessCommandQueue();

            // 3. 全局安全检查
            var guard = GlobalGuardCheck();

            // 手动模式下，不运行自动状态机逻辑
            if (Mode != S88Mode.Manual)
            {
                if (guard == GuardResult.Abort)
                    TryTransition(S88Command.Abort);

                // 4. 执行当前状态对应的逻辑
                switch (State)
                {
                    case S88State.Starting:
                        ExecuteLogic(OnStarting, S88State.Execute);
                        break;
                    case S88State.Execute:
                        ExecuteLogic(OnExecute, S88State.Completing);
                        break;
                    case S88State.Completing:
                        ExecuteLogic(OnCompleting, S88State.Completed);
                        break;
                    case S88State.Resetting:
                        ExecuteLogic(OnResetting, S88State.Idle);
                        break;
                    case S88State.Holding:
                        ExecuteLogic(OnHolding, S88State.Held);
                        break;
                    case S88State.Suspending:
                        ExecuteLogic(OnSuspending, S88State.Suspended);
                        break;
                    case S88State.Unholding:
                        ExecuteLogic(OnUnholding, S88State.Execute);
                        break;
                    case S88State.Unsuspending:
                        ExecuteLogic(OnUnsuspending, S88State.Execute);
                        break;
                    case S88State.Aborting:
                        ExecuteLogic(OnAborting, S88State.Aborted);
                        break;
                    case S88State.Clearing:
                        ExecuteLogic(OnClearing, S88State.Stopped);
                        break;
                    case S88State.Stopping:
                        ExecuteLogic(OnStopping, S88State.Stopped);
                        break;
                }
            }

            // 5. 下级设备 (EM/CM)刷新
            var cache = _membersCache; // 读取 volatile 引用
            for (int i = 0; i < cache.Length; i++)
            {
                cache[i].Refresh(currentTimestampMs);
            }
        }
        catch (Exception ex)
        {
            if (State != S88State.SystemFault)
            {
                State = S88State.SystemFault;
                LogError(ex, "系统故障，请联系工程师。");
            }
            IsActive = false;
            ToSafe();
        }
    }
    public void ExecuteCommand(InternalCommand command)
    {
        if (string.IsNullOrEmpty(command.TargetObject))
        {
            _commandQueue.Enqueue(command);
            return;
        }

        if (Mode == S88Mode.Manual)//仅手动模式下，才处理EM/CM的指令
        {
            if (_members.TryGetValue(command.TargetObject, out var s88Object))
            {
                s88Object.ExecuteCommand(command);
                return;
            }

            var cache = _membersCache; // 读取 volatile 引用
            for (int i = 0; i < cache.Length; i++)
            {
                if (cache[i] is IEquipmentModule em && em.TryGetCm(command.TargetObject, out IControlModule? cm))
                {
                    cm!.ExecuteCommand(command);
                    return;
                }
            }

            command.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Rejected, $"指令目标未知：{command.TargetUnit}.{command.TargetObject}"));
            return;
        }

        command.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Rejected, $"当前 {command.TargetUnit} 处于 {Mode} 模式，无法执行：{command.TargetObject}.{command.CmdName}"));
    }
    public void ToSafe()
    {
        PurgeCommands();
        TryTransition(S88Command.Abort);

        var cache = _membersCache;
        for (int i = 0; i < cache.Length; i++)
            cache[i].ToSafe(); // 级联让各 EM、CM 立即切断物理输出 (例如阀门关闭，电机掉使能等)
    }
    public S88State State
    {
        get => _state;
        private set
        {
            if (_state != value)
            {
                _eventProducer.SendInfo(Name, UnitEvents.InfoStateSwitched, _state, value);
                _state = value;
                _step = 0;
                _stepChangedPending = true;
                _stepStartTimestamp = _currentTimestampMs;
            }
        }

    }// 当前状态
    public S88Mode Mode { get; private set; } = S88Mode.Manual;// 当前模式
    public virtual string GetActiveRecipeJson() => string.Empty;


    // ==========================================
    // 外部接口
    // ==========================================


    // ==========================================
    // 供子类重写的逻辑钩子 (Hooks)
    // ==========================================
    protected virtual bool OnResetting() => true; // 返回 true 表示该阶段工作完成，可以进入下一阶段；默认立即完成
    protected virtual bool OnStarting() => true;
    protected virtual bool OnExecute() => false;
    protected virtual bool OnCompleting() => true;
    protected virtual bool OnHolding() => true;
    protected virtual bool OnSuspending() => true;
    protected virtual bool OnUnholding() => true;
    protected virtual bool OnUnsuspending() => true;
    protected virtual bool OnAborting() => true;
    protected virtual bool OnClearing() => true;
    protected virtual bool OnStopping() => true;
    protected virtual void OnEnterManual() { } // 切手动时，通常需执行特定的初始化，由具体的 Unit 实现
    protected virtual void OnEnterAuto() { } // 切自动时，通常需执行特定的初始化，由具体的 Unit 实现
    protected virtual GuardResult GlobalGuardCheck() => GuardResult.Ok; // 安全/故障检测及动作，由具体的 Unit 实现
    protected virtual void CaptureContextSnapshot(Dictionary<string, string> snapshot) { }// 保存状态快照
    protected virtual bool VerifyContextSnapshot(Dictionary<string, string> snapshot) => false;// 校验状态快照
    protected virtual void RegisterCommandHandlers()
    {
        RegisterCommandHandler(Command.Start, cmd =>
        {
            cmd.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Accepted, string.Empty));
            TryTransition(S88Command.Start);
        });

        RegisterCommandHandler(Command.Stop, cmd =>
        {
            cmd.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Accepted, string.Empty));
            TryTransition(S88Command.Stop);
        });

        RegisterCommandHandler(Command.Hold, cmd =>
        {
            cmd.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Accepted, string.Empty));
            TryTransition(S88Command.Hold);
        });

        RegisterCommandHandler(Command.Unhold, cmd =>
        {
            cmd.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Accepted, string.Empty));
            TryTransition(S88Command.Unhold);
        });

        RegisterCommandHandler(Command.Suspend, cmd =>
        {
            cmd.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Accepted, string.Empty));
            TryTransition(S88Command.Suspend);
        });

        RegisterCommandHandler(Command.Unsuspend, cmd =>
        {
            cmd.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Accepted, string.Empty));
            TryTransition(S88Command.Unsuspend);
        });

        RegisterCommandHandler(Command.Abort, cmd =>
        {
            cmd.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Accepted, string.Empty));
            TryTransition(S88Command.Abort);
        });

        RegisterCommandHandler(Command.EStop, cmd =>
        {
            cmd.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Accepted, string.Empty));
            TryTransition(S88Command.Abort);
        });

        RegisterCommandHandler(Command.Reset, cmd =>
        {
            cmd.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Accepted, string.Empty));
            TryTransition(S88Command.Reset);
        });

        RegisterCommandHandler(Command.Complete, cmd =>
        {
            cmd.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Accepted, string.Empty));
            TryTransition(S88Command.Complete);
        });

        RegisterCommandHandler(Command.Clear, cmd =>
        {
            cmd.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Accepted, string.Empty));
            TryTransition(S88Command.Clear);
        });

        RegisterCommandHandler(Command.NextStep, cmd =>
        {
            cmd.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Accepted, string.Empty));
            CmdNextStep();
        });

        RegisterCommandHandler(Command.SetMode, cmd =>
        {
            if (cmd.Params.TryGetValue("NewMode", out var modeStr) &&
                    Enum.TryParse<S88Mode>(modeStr, true, out var newMode))
            {
                cmd.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Accepted, string.Empty));
                CmdChangeMode(newMode);
                return;
            }

            cmd.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Rejected, $"模式切换失败，参数 NewMode 不对"));
        });
    }

    // ==========================================
    // 供子类调用的辅助方法
    // ==========================================
    /// <summary>
    /// 供子类检查本步骤转换条件, 在 OnExecute 中调用此方法来判断是否可以走下一步 
    /// </summary>
    protected bool CheckStepCondition(bool physicalCondition)
    {
        // 物理条件没满足，永远不走
        if (!physicalCondition)
        {
            _stepConfirmationReceived = false;
            return false;
        }

        // 全自动：物理满足就走
        if (Mode == S88Mode.Automatic) return true;

        // 半自动：物理满足 + 人工确认 才能走
        if (Mode == S88Mode.SemiAuto)
        {
            if (_stepConfirmationReceived)
            {
                _stepConfirmationReceived = false; // 消耗掉信号
                return true;
            }
            return false;
        }

        return false;
    }
    /// <summary>
    /// 供子类判断本步骤持续时间是否超过 ms 毫秒
    /// </summary>
    protected bool StepTimeout(long ms) => StepTime > ms;
    /// <summary>
    /// 供子类判断当前是否是本步骤的第一个周期
    /// </summary>
    protected bool IsNewStep { get; private set; } = false;
    /// <summary>
    /// 供子类获取/设置当前步号
    /// </summary>
    protected int Step
    {
        get => _step;
        set
        {
            if (_step != value)
            {
                _step = value;
                _stepChangedPending = true; // 标记新的一步
                _stepStartTimestamp = _currentTimestampMs;
            }
        }
    }
    /// <summary>
    /// 供子类获取当前步骤已耗时 (毫秒)
    /// </summary>
    protected long StepTime => _currentTimestampMs - _stepStartTimestamp;
    /// <summary>
    /// 供子类注册下级设备 (EM/CM)
    /// </summary>
    protected void RegisterMember(IS88Object member)
    {
        if (_members.TryAdd(member.Name, member))
        {
            // 每次注册新设备时，更新一次缓存。
            _membersCache = _members.Values.ToArray();
        }
    }
    /// <summary>
    /// 存储切手动那一刻的物理快照，供 OnEnterAuto 比对
    /// </summary>
    protected Dictionary<string, string> _autoContextSnapshot = new();

    protected void RegisterCommandHandler(Command cmdName, Action<InternalCommand> handler)
    => _commandHandlers[cmdName] = handler;

    // 日志方法
    protected void LogInfo(string msg) => _logger.LogInformation("[{Name}] - {Msg}",Name,msg);
    protected void LogWarning(string msg) => _logger.LogWarning("[{Name}] - {Msg}", Name, msg);
    protected void LogError(Exception ex, string msg) => _logger.LogError(ex, "[{Name}] - ERR: {Msg}", Name, msg);

    // ==========================================
    // 私有成员
    // ==========================================
    private int _step = 0;
    private UnitCfg _cfg = cfg;
    private S88State _state = S88State.Idle;
    private long _stepStartTimestamp = 0;
    private bool _stepChangedPending = true;//是否发生了跳步
    private readonly IEventProducer _eventProducer = eventProducer;
    private readonly ILogger<S88UnitBase> _logger = logger;
    private bool _stepConfirmationReceived = false;
    private S88State _previousState = S88State.Idle;
    private readonly ConcurrentQueue<InternalCommand> _commandQueue = new();
    private long _currentTimestampMs = 0;
    private readonly Dictionary<Command, Action<InternalCommand>> _commandHandlers = new();
    private readonly ConcurrentDictionary<string, IS88Object> _members = new(StringComparer.OrdinalIgnoreCase);
    private volatile IS88Object[] _membersCache = Array.Empty<IS88Object>();
    // 执行子类逻辑，如果返回true，则自动流转到下一个状态
    private void ExecuteLogic(Func<bool> action, S88State nextState)
    {
        // 调用子类重写的具体逻辑
        bool isDone = action();
        if (isDone)
            State = nextState;
    }
    private void TryTransition(S88Command cmd)
    {
        if (Mode == S88Mode.Manual)
        {
            _eventProducer.SendInfo(Name, UnitEvents.InfoCmdIgnoredOnManual, cmd.ToString());
            return;
        }

        switch (cmd)
        {
            case S88Command.Abort:
                if (State != S88State.Aborted && State != S88State.Aborting)
                {
                    PurgeCommands();
                    State = S88State.Aborting;
                }
                return;

            case S88Command.Stop:
                if (State != S88State.Stopped && State != S88State.Stopping &&
                    State != S88State.Clearing && State != S88State.Aborted && State != S88State.Aborting)
                    State = S88State.Stopping;
                return;

            case S88Command.Start:
                if (State == S88State.Idle)
                    State = S88State.Starting;
                return;

            case S88Command.Clear:
                if (State == S88State.Aborted)
                    State = S88State.Clearing;
                return;

            case S88Command.Reset:
                if (State == S88State.Stopped || State == S88State.Completed)
                    State = S88State.Resetting;
                return;

            case S88Command.Hold:
                if (State == S88State.Execute || State == S88State.Suspended)
                    State = S88State.Holding;
                return;

            case S88Command.Complete:
                if (State == S88State.Execute || State == S88State.Suspended || State == S88State.Held)
                    State = S88State.Completing;
                return;

            case S88Command.Suspend:
                if (State == S88State.Execute)
                    State = S88State.Suspending;
                return;

            case S88Command.Unhold:
                if (State == S88State.Held)
                    State = S88State.Unholding;
                return;

            case S88Command.Unsuspend:
                if (State == S88State.Suspended)
                    State = S88State.Unsuspending;
                return;
        }
    }
    private void CmdChangeMode(S88Mode newMode)
    {
        if (Mode == newMode) return;

        if (newMode == S88Mode.Manual)
        {
            // 状态分类判断
            bool isResumable = (State == S88State.Held || State == S88State.Suspended);
            bool isSafeToSwitch = (State == S88State.Idle || State == S88State.Stopped ||
                                   State == S88State.Aborted || State == S88State.Completed);

            // 记录切入手动前的瞬间状态
            _previousState = State;

            if (isResumable)
            {
                // [可恢复态]：保存物理快照，供切回时比对
                CaptureContextSnapshot(_autoContextSnapshot);
            }
            else if (!isSafeToSwitch)
            {
                // [动作态]：运行中强切手动，立刻触发紧急放弃 (Abort)
                _eventProducer.SendInfo(Name, UnitEvents.InfoSwitch2ManualOnActing);

                // 触发状态转移，此时 State 会立刻变为 Aborting
                TryTransition(S88Command.Abort);
            }

            var oldMode = Mode;
            Mode = newMode;
            _eventProducer.SendInfo(Name, UnitEvents.InfoModeSwitched, oldMode, newMode);
            OnEnterManual();
        }
        else
        {
            // 切回 Auto / SemiAuto
            var oldMode = Mode;
            Mode = newMode;
            _eventProducer.SendInfo(Name, UnitEvents.InfoModeSwitched, oldMode, newMode);

            // 只有当之前是从 Held / Suspended 切走时，才尝试恢复原状态
            if (_previousState == S88State.Held || _previousState == S88State.Suspended)
            {
                // 验证手动期间是否有人挪动了关键设备（快照一致性校验）
                bool isConsistent = VerifyContextSnapshot(_autoContextSnapshot);
                if (!isConsistent)
                {
                    _eventProducer.SendInfo(Name, UnitEvents.WarnContextSnapshotInconsistent);
                    // 现场被破坏，无法安全恢复，强制拉入 Stop 状态
                    TryTransition(S88Command.Stop);
                }
                else
                {
                    // 现场完好，完美恢复到之前的挂起/暂停状态
                    State = _previousState;
                }
            }
            else
            {
                // 其他情况（包括由于强切手动引发的 Aborting，或原本就是 Idle）
                // 此时【什么都不做】，保留当前状态！
                // -> 如果是 Aborting，切回自动后，状态机继续执行 OnAborting() 直到 Aborted。
                // -> 如果是 Idle，切回自动后，依然是 Idle，等待按 Start。
            }

            OnEnterAuto();
        }
    }
    private void CmdNextStep()
    {
        if (Mode != S88Mode.SemiAuto) return;
        _stepConfirmationReceived = true;
    }
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
            if (cmd == null) continue;

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

public enum GuardResult
{
    Ok,
    Abort
}

public class UnitCfg
{
    public required string Name { get; init; }
}

public static partial class UnitEvents
{
    public static readonly EventBase InfoCmdQueuePurged = new()
    {
        EventId = 1,
        Severity = SeverityLevel.Info,
        MessageTemplate = "命令队列已清空"
    };

    public static readonly EventBase InfoCmdIgnoredOnManual = new()
    {
        EventId = 2,
        Severity = SeverityLevel.Info,
        MessageTemplate = "手动模式，指令无效: Cmd={0}"
    };

    public static readonly EventBase InfoStateSwitched = new()
    {
        EventId = 3,
        Severity = SeverityLevel.Info,
        MessageTemplate = "状态机切换完成：{0} -> {1}"
    };

    public static readonly EventBase InfoModeSwitched = new()
    {
        EventId = 4,
        Severity = SeverityLevel.Info,
        MessageTemplate = "模式切换完成：{0} -> {1}"
    };

    public static readonly EventBase InfoSwitch2ManualOnActing = new()
    {
        EventId = 5,
        Severity = SeverityLevel.Info,
        MessageTemplate = "ACTING状态下的请求切换至手动，触发Abort"
    };

    public static readonly EventBase WarnContextSnapshotInconsistent = new()
    {
        EventId = 6,
        Severity = SeverityLevel.Warning,
        MessageTemplate = "Manaul模式切换至Auto/Semi模式后状态快照比对不一致，触发Stop"
    };
}
