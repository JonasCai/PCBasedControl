using Microsoft.Extensions.Options;
using MongoDB.Driver;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Controller.EventLogger;

public class EventRepository : IEventRepository, IDisposable
{
    private readonly IMongoCollection<EventBase> _collection;
    private readonly ILogger<EventRepository> _logger;
    private readonly Channel<EventBase> _bufferChannel;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _processTask;

    public EventRepository(IOptions<MongoDbSettings> dbOptions, ILogger<EventRepository> logger)
    {
        _logger = logger;
        var settings = dbOptions.Value;

        // 初始化 MongoDB 连接
        var client = new MongoClient(settings.ConnectionString);
        var database = client.GetDatabase(settings.DatabaseName);
        _collection = database.GetCollection<EventBase>(settings.CollectionName);

        // 配置 1万 容量的防阻塞通道
        var options = new BoundedChannelOptions(10000)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false
        };
        _bufferChannel = Channel.CreateBounded<EventBase>(options);

        // 启动后台存盘线程
        _processTask = Task.Run(() => ProcessEventsAsync(_cts.Token));
    }

    public void Save(EventBase evt)
    {
        if (!_bufferChannel.Writer.TryWrite(evt))
        {
            if (evt is AlarmModel alarm)
                _logger.LogWarning("事件缓冲池写入失败:{InstanceId} - {SourceName} - {State} - {Message}", alarm.InstanceId, alarm.SourceName, alarm.State, alarm.Message);
        }
    }

    private async Task ProcessEventsAsync(CancellationToken cancellationToken)
    {
        var batch = new List<EventBase>();

        try
        {
            await foreach (var evt in _bufferChannel.Reader.ReadAllAsync(cancellationToken))
            {
                batch.Add(evt);

                // 凑满 10 条或者通道暂空时，执行一次数据库 IO
                if (batch.Count >= 10 || _bufferChannel.Reader.Count == 0)
                {
                    await WriteBatchToDbAsync(batch, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException) { /* 正常停机 */ }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "事件存盘后台任务发生致命异常！");
        }
    }

    private async Task WriteBatchToDbAsync(List<EventBase> batch, CancellationToken cancellationToken)
    {
        int retryCount = 0;
        while (batch.Count > 0 && !cancellationToken.IsCancellationRequested)
        {
            try
            {
                var bulkOps = new List<WriteModel<EventBase>>();

                foreach (var evt in batch)
                {
                    if (evt is AlarmModel alarm)
                    {
                        // 无论是 Arrived 还是 Left，直接覆盖数据库中对应 InstanceId 的记录。
                        var filter = Builders<EventBase>.Filter.Eq("_id", alarm.InstanceId);
                        bulkOps.Add(new ReplaceOneModel<EventBase>(filter, alarm) { IsUpsert = true });
                    }
                    else
                    {
                        // 普通 MessageModel，直接 Insert
                        bulkOps.Add(new InsertOneModel<EventBase>(evt));
                    }
                }

                if (bulkOps.Any())
                {
                    // 将 IsOrdered 设为 true，保证同一批次内的时间戳时序，避免同一报警的清除动作被到达动作逆向覆盖
                    await _collection.BulkWriteAsync(bulkOps, new BulkWriteOptions { IsOrdered = true }, cancellationToken);
                }

                batch.Clear();
            }
            catch (MongoBulkWriteException ex)
            {
                // 如果是由于重复键引起的报错，通常意味着数据已经落盘，直接清空放弃重试，防止死循环
                _logger.LogError(ex, "MongoDB 批量写入发生文档级异常，将忽略冲突记录。");
                batch.Clear();
                break;
            }
            catch (OperationCanceledException) { /* 正常停机 */ }
            catch (Exception ex)
            {
                retryCount++;
                if (retryCount > 3)
                {
                    _logger.LogCritical(ex, "MongoDB 写入连续失败超过3次，丢弃该批次日志以防内存溢出。");
                    batch.Clear();
                    break;
                }
                _logger.LogError(ex, "MongoDB 网络或执行失败。5秒后进行第 {RetryCount} 次重试...", retryCount);
                await Task.Delay(5000, cancellationToken);
            }
        }
    }

    public void Dispose()
    {
        // 标记通道不再接收新数据
        _bufferChannel.Writer.TryComplete();

        // 给予后台任务最多 2 秒的时间把剩余数据写入 MongoDB
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
}

public interface IEventRepository
{
    void Save(EventBase evt);
}

public class MongoDbSettings
{
    public string ConnectionString { get; set; } = string.Empty;
    public string DatabaseName { get; set; } = string.Empty;
    public string CollectionName { get; set; } = string.Empty;
}
