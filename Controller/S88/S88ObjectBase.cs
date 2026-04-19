using Controller._01.ControlModule;
using Controller.EventLogger;
using Controller.gRPC;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Controller.S88;

public abstract class S88ObjectBase(string name, IEventProducer eventProducer, ILogger logger)
{
    public string Name { get; } = name;
    public abstract bool HasAnyError { get; }
    public abstract bool HasAnyWarning { get; }
    public abstract void Refresh(long currentTimestampMs);
    public abstract void ToSafe();
    public virtual void ExecuteCommand(InternalCommand command) => _commandQueue.Enqueue(command);

    protected void ProcessCommandQueue()
    {
        while (_commandQueue.TryDequeue(out var cmd))
        {
            // 死亡确认
            if (cmd.CancelToken.IsCancellationRequested)
            {
                _logger.LogWarning("指令 {TargetUnit}.{TargetObject}.{CmdName} 在排队期间已被调用方取消或超时 (3s)，已作为僵尸指令安全丢弃", cmd.TargetUnit, cmd.TargetObject, cmd.CmdName);
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
    protected void PurgeCommands()
    {
        while (_commandQueue.TryDequeue(out var cmd))
        {
            if (cmd?.CallbackTcs != null)
            {
                cmd.CallbackTcs.TrySetResult(new CommandResult(
                    CommandResultType.Rejected,
                    "指令被系统强制清理，未执行"
                ));
                _logger.LogWarning("指令 {TargetUnit}.{TargetObject}.{CmdName} 被系统强制清理，未执行", cmd.TargetUnit, cmd.TargetObject, cmd.CmdName);
            }
        }
    }
    protected void RegisterCommandHandler(Command cmd, Action<InternalCommand> cmdHandler) => _commandHandlers[cmd] = cmdHandler;
    protected virtual void RaiseAlarm(EventBase eventbase, params object[] args)
    {
        if (eventbase.Severity == SeverityLevel.Info) return;

        if (!_activeAlarms.ContainsKey(eventbase.EventId))
        {
            var guid = Guid.NewGuid();
            _activeAlarms.Add(eventbase.EventId, (guid, eventbase, args));
            _eventProducer.RaiseAlarm(Name, guid, eventbase, args);
        }
    }
    protected void RaiseInfo(EventBase eventbase, params object[] args)
    {
        if (eventbase.Severity != SeverityLevel.Info) return;
        _eventProducer.SendInfo(Name, eventbase, args);
    }
    protected void TryClearAlarm(EventBase eventbase)
    {
        if (_activeAlarms.Remove(eventbase.EventId, out var alarm))
        {
            _eventProducer.ClearAlarm(Name, alarm.guid, alarm.eventBase, alarm.args);
        }
    }

    // 日志方法
    protected void LogInfo(string msg, params object?[] args) => _logger.LogInformation(msg, args);
    protected void LogWarning(string msg, params object?[] args) => _logger.LogWarning(msg, args);
    protected void LogError(Exception ex, string msg, params object?[] args) => _logger.LogError(ex,msg, args);

    private ILogger _logger = logger;
    private IEventProducer _eventProducer = eventProducer;
    private readonly Dictionary<Command, Action<InternalCommand>> _commandHandlers = new();
    private readonly Dictionary<int, (Guid guid, EventBase eventBase, object[] args)> _activeAlarms = new();
    private readonly ConcurrentQueue<InternalCommand> _commandQueue = new();
}


