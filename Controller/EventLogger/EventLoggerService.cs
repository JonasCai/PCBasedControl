using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Controller.EventLogger;

public class EventLoggerService : IEventProducer, IDisposable
{
    private readonly Channel<RawEventEntry> _incomingChannel;
    private readonly IEventRepository _repository;
    private readonly ConcurrentDictionary<Guid, AlarmModel> _activeAlarms = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly ILogger<EventLoggerService> _logger;
    private readonly Task _processTask;

    // 使用事件向外层(gRPC)广播，这样 EventLoggerService 不需要知道 gRPC 的存在
    public event Action<EventBase>? OnEventProcessed;

    public EventLoggerService(IEventRepository repository, ILogger<EventLoggerService> logger)
    {
        _logger = logger;
        _repository = repository;

        var options = new BoundedChannelOptions(10000)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropWrite
        };
        _incomingChannel = Channel.CreateBounded<RawEventEntry>(options);

        // 启动后台处理循环
        _processTask = Task.Run(() => ProcessEventsLoop(_cts.Token));

    }

    public void Dispose()
    {
        // 阻止新事件进入
        _incomingChannel.Writer.TryComplete();

        // 给后台循环留出处理时间
        _cts.CancelAfter(TimeSpan.FromSeconds(2));

        try
        {
            // 阻塞等待后台任务安全结束
            _processTask.GetAwaiter().GetResult();
        }
        catch (TaskCanceledException) { }
        catch (Exception ex) { _logger.LogError(ex, "事件仓储停机时发生异常"); }
        _cts.Dispose();
    }

    /// <summary>
    /// 获取当前活跃报警快照 (供 gRPC 客户端刚连上来时使用)
    /// </summary>
    public IEnumerable<AlarmModel> GetActiveAlarmsSnapshot() => _activeAlarms.Values.ToList();

    // ==========================================
    // IEventProducer 实现
    // ==========================================
    public void RaiseAlarm(string sourceName, Guid instanceId, EventBase alarm, params object[] args)
    {
        var entry = new RawEventEntry(instanceId, EventOpType.Raise, sourceName, alarm, args, DateTime.UtcNow);
        if (!_incomingChannel.Writer.TryWrite(entry))
        {
            // 队列溢出时，记录直接的本地错误，以便工程师回溯
            _logger.LogError("[日志溢出丢失] 无法记录报警到达: {InstanceId} - {SourceName} - {MessageTemplate}", instanceId, sourceName, alarm.MessageTemplate);
        }
    }

    public void ClearAlarm(string sourceName, Guid instanceId, EventBase alarm, params object[] args)
    {
        var entry = new RawEventEntry(instanceId, EventOpType.Clear, sourceName, alarm, args, DateTime.UtcNow);
        if (!_incomingChannel.Writer.TryWrite(entry))
        {
            _logger.LogError("[日志溢出丢失] 无法记录报警离开: {InstanceId} - {SourceName} - {MessageTemplate}", instanceId, sourceName, alarm.MessageTemplate);
        }
    }

    public void SendInfo(string sourceName, EventBase info, params object[] args)
    {
        var entry = new RawEventEntry(Guid.NewGuid(), EventOpType.Info, sourceName, info, args, DateTime.UtcNow);
        if (!_incomingChannel.Writer.TryWrite(entry))
        {
            _logger.LogError("[日志溢出丢失] 无法记录事件: {InstanceId} - {SourceName} - {MessageTemplate}", entry.instanceId, sourceName, entry.Event.MessageTemplate);
        }
    }


    private async Task ProcessEventsLoop(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var entry in _incomingChannel.Reader.ReadAllAsync(cancellationToken))
            {
                EventBase? modelToProcess = entry.OpType switch
                {
                    EventOpType.Raise => HandleRaise(entry),
                    EventOpType.Clear => HandleClear(entry),
                    EventOpType.Info => HandleInfo(entry),
                    _ => null
                };

                if (modelToProcess != null)
                {
                    // 异步存库 (EventRepository 自带 Channel 缓冲)
                    _repository.Save(modelToProcess);

                    // 广播给 UI (安全的委托调用机制)
                    SafeBroadcast(modelToProcess);
                }
            }
        }
        catch (OperationCanceledException) { /* 正常停机忽略 */ }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "事件处理核心循环发生致命异常！");
        }


    }

    private MessageModel HandleInfo(RawEventEntry entry)
    {
        return new MessageModel
        {
            InstanceId = entry.instanceId,
            EventId = entry.Event.EventId,
            SourceName = entry.SourceName,
            Severity = entry.Event.Severity,
            MessageTemplate = entry.Event.MessageTemplate,
            TimeRaised = entry.Timestamp,
            Message = SafeFormat(entry.Event.MessageTemplate, entry.Args)
        };
    }

    private AlarmModel? HandleRaise(RawEventEntry entry)
    {
        var key = entry.instanceId;

        var model = new AlarmModel
        {
            InstanceId = entry.instanceId,
            EventId = entry.Event.EventId,
            SourceName = entry.SourceName,
            Severity = entry.Event.Severity,
            TimeRaised = entry.Timestamp, // 使用原始触发时间
            TimeCleared = DateTime.MinValue,
            MessageTemplate = entry.Event.MessageTemplate,
            State = AlarmState.Arrived,
            Message = SafeFormat(entry.Event.MessageTemplate, entry.Args) // 在后台拼字符串
        };
        _activeAlarms.TryAdd(key, model);
        return model;
    }

    private AlarmModel? HandleClear(RawEventEntry entry)
    {
        if (_activeAlarms.Remove(entry.instanceId, out var existing))
            return existing with { TimeCleared = entry.Timestamp, State = AlarmState.Left };

        return null;
    }

    private void SafeBroadcast(EventBase model)
    {
        if (OnEventProcessed == null) return;

        // 拆解多播委托，防止某一个 gRPC 客户端异常拖死其他人
        foreach (Action<EventBase> handler in OnEventProcessed.GetInvocationList())
        {
            try
            {
                handler(model);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "事件广播给订阅者时发生异常");
            }
        }
    }

    private static string SafeFormat(string template, object[] args)
    {
        if (args == null || args.Length == 0) return template;
        try { return string.Format(template, args); } catch { return template; }
    }

    private enum EventOpType { Raise, Clear, Info }

    private record RawEventEntry(
        Guid instanceId,
        EventOpType OpType,
        string SourceName,
        EventBase Event,
        object[] Args,
        DateTime Timestamp
    );
}

public interface IEventProducer
{
    void RaiseAlarm(string sourceName, Guid instanceId, EventBase alarm, params object[] args);
    void ClearAlarm(string sourceName, Guid instanceId, EventBase alarm, params object[] args);
    void SendInfo(string sourceName, EventBase info, params object[] args);
}
