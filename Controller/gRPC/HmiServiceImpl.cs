using Controller.EventLogger;
using Controller.Hardware;
using Controller.S88;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Shared.HmiService;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace Controller.gRPC;

public class HmiServiceImpl(EventLoggerService eventLogger, S88ProcessCellBase processCell, ILogger<HmiServiceImpl> logger, HwData hwData) : HmiService.HmiServiceBase
{
    private readonly HwData _hwData = hwData;
    private readonly S88ProcessCellBase _processCell = processCell;
    private readonly EventLoggerService _eventLogger = eventLogger;
    private readonly ILogger<HmiServiceImpl> _logger = logger;

    public override async Task<ControlCommandResponse> SendCommand(ControlCommandRequest request, ServerCallContext context)
    {
        var tcs = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken, cts.Token);

        // 硬件故障时的拦截逻辑
        if (_hwData.IsFaulted)
        {
            return new ControlCommandResponse
            {
                Success = false,
                Message = $"操作拒绝：硬件处于故障状态 [{_hwData.ErrorCode}]，请先复位硬件。"
            };
        }

        Command cmdName;
        if (!System.Enum.TryParse<Command>(request.CommandName, true, out cmdName))
        {
            return new ControlCommandResponse { Success = false, Message = $"指令未定义：{request.CommandName}" };
        }

        // 将 Proto 消息转换为内部指令对象
        var internalCmd = new InternalCommand(request.TargetUnit,
            request.TargetObject,
            cmdName,
            request.Params.ToDictionary(k => k.Key, v => v.Value, StringComparer.OrdinalIgnoreCase),
            request.JsonPayload,
            tcs,
            linkedCts.Token);

        _processCell.ExecuteCommand(internalCmd);

        try
        {
            using (linkedCts.Token.Register(() => tcs.TrySetCanceled()))
            {
                CommandResult result = await tcs.Task;
                return new ControlCommandResponse { Success = result.Type == CommandResultType.Accepted, Message = result.Message };
            }
        }
        catch (TaskCanceledException)
        {
            // 捕捉超时或断开异常
            if (cts.IsCancellationRequested)
            {
                return new ControlCommandResponse { Success = false, Message = "控制器响应超时（可能主循环已卡死或者指令被系统强制清理）" };
            }
            return new ControlCommandResponse { Success = false, Message = "客户端取消了请求" };
        }
    }

    public override async Task SubscribeEvent(Empty request, IServerStreamWriter<EventEnvelope> responseStream, ServerCallContext context)
    {
        // 本地缓冲通道
        var buffer = Channel.CreateBounded<EventEnvelope>(new BoundedChannelOptions(500)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });

        // 将外界事件写入本地 Channel
        void OnEventBroadcast(EventBase evt)
        {
            buffer.Writer.TryWrite(evt.ToProto());
        }

        try
        {
            // 新事件也会进入 Buffer
            _eventLogger.OnEventProcessed += OnEventBroadcast;

            // 把快照写入 Buffer，确保客户端连上来能看到现有的报警
            foreach (var alarm in _eventLogger.GetActiveAlarmsSnapshot())
            {
                buffer.Writer.TryWrite(alarm.ToProto());
            }

            // 从 Buffer 读取并写入 gRPC Stream
            await foreach (var env in buffer.Reader.ReadAllAsync(context.CancellationToken))
            {
                await responseStream.WriteAsync(env);
            }
        }
        catch (OperationCanceledException) { /* 客户端正常断开 */ }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SubscribeEvent 发生异常");
        }
        finally
        {
            _eventLogger.OnEventProcessed -= OnEventBroadcast;
            buffer.Writer.TryComplete();
            _logger.LogInformation("SubscribeEvent 连接断开");
        }
    }

    public override async Task SubscribeHwStatus(Empty request, IServerStreamWriter<HwStatus> responseStream, ServerCallContext context)
    {
        var buffer = Channel.CreateBounded<HwStatus>(new BoundedChannelOptions(10)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

        void OnHwStatusChanged(HwStatusChangedEventArgs e)
        {
            buffer.Writer.TryWrite(new HwStatus
            {
                IsFaulted = e.IsFaulted,
                IsResetNeeded = e.IsResetNeeded,
                ErrorCode = e.ErrorCode,
                Message = e.Message
            });
        }

        try
        {
            _hwData.OnStatusChanged += OnHwStatusChanged;

            // 确保客户端连上来第一眼看到的是当前真实状态
            await responseStream.WriteAsync(new HwStatus
            {
                IsFaulted = _hwData.IsFaulted,
                IsResetNeeded = _hwData.IsResetNeeded,
                ErrorCode = _hwData.ErrorCode,
                Message = _hwData.Message
            });

            // --- 启动心跳 ---
            _ = Task.Run(async () =>
            {
                // .NET 6+ 推荐的异步定时器，无回调乱序风险
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
                try
                {
                    while (await timer.WaitForNextTickAsync(context.CancellationToken))
                    {
                        buffer.Writer.TryWrite(new HwStatus
                        {
                            IsFaulted = _hwData.IsFaulted,
                            IsResetNeeded = _hwData.IsResetNeeded,
                            ErrorCode = _hwData.ErrorCode,
                            Message = _hwData.Message
                        });
                    }
                }
                catch (OperationCanceledException) { /* 正常取消跳出 */ }
            }, context.CancellationToken);

            // --- 消费缓冲区并推送到网络流 ---
            await foreach (var status in buffer.Reader.ReadAllAsync(context.CancellationToken))
            {
                await responseStream.WriteAsync(status);
            }
        }
        catch (OperationCanceledException) { /* 客户端正常断开 */ }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SubscribeHwStatus 发生异常");
        }
        finally
        {
            _hwData.OnStatusChanged -= OnHwStatusChanged;
            buffer.Writer.TryComplete();
            _logger.LogInformation("硬件状态订阅流已关闭");
        }
    }

    public override Task<ResetHwReply> ResetHw(Empty request, ServerCallContext context)
    {
        _hwData.RequestReset(); // 请求复位
        return Task.FromResult(new ResetHwReply { Success = true, Message = "复位指令已接收" });
    }

    public override Task<RecipeUploadResponse> UploadRecipe(RecipeUploadRequest request, ServerCallContext context)
    {
        if (_processCell.TryGetUnit(request.TargetUnit, out var unit))
        {
            var json = unit!.GetActiveRecipeJson();
            return Task.FromResult(new RecipeUploadResponse
            {
                Success = true,
                Message = "读取成功",
                JsonPayload = json
            });
        }

        return Task.FromResult(new RecipeUploadResponse { Success = false, Message = $"找不到目标Unit {request.TargetUnit}" });
    }
}

public static class EventEnvelopeExtensions
{
    public static EventEnvelope ToProto(this EventBase evt) => evt switch
    {
        AlarmModel a => new EventEnvelope
        {
            EventId = a.EventId,
            SourceName = a.SourceName,
            Severity = a.Severity.ToProtoSeverity(),
            Message = a.Message,
            TimeRaised = a.TimeRaised.ToTimestamp(),
            Alarm = new AlarmPayload()
            {
                IsActive = a.State == AlarmState.Arrived,
                TimeCleared = (a.State == AlarmState.Left && a.TimeCleared != DateTime.MinValue)
                    ? a.TimeCleared.ToTimestamp()
                    : DateTime.MinValue.ToTimestamp()
            }
        },
        MessageModel m => new EventEnvelope
        {
            EventId = m.EventId,
            SourceName = m.SourceName,
            Severity = m.Severity.ToProtoSeverity(),
            Message = m.Message,
            TimeRaised = m.TimeRaised.ToTimestamp(),
        },
        _ => throw new ArgumentException("未知的事件类型")
    };

    private static Severity ToProtoSeverity(this SeverityLevel level) => level switch
    {
        SeverityLevel.Info => Severity.Info,
        SeverityLevel.Warning => Severity.Warning,
        SeverityLevel.Error => Severity.Error,
        _ => Severity.Info
    };

    // DateTime -> google.protobuf.Timestamp 扩展
    public static Timestamp ToTimestamp(this DateTime dt) =>
        Timestamp.FromDateTime(dt.ToUniversalTime());
}
