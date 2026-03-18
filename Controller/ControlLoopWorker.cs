using Controller.Hardware;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Controller;

public class ControlLoopWorker(ILogger<ControlLoopWorker> logger, LeadshineDmcDriver driver, IONodes nodes, HwData hwData) : BackgroundService
{
    private readonly ILogger<ControlLoopWorker> _logger = logger;
    private readonly LeadshineDmcDriver _driver = driver;
    private readonly IONodes _nodes = nodes;
    private readonly HwData _hwData = hwData;

    // 引入系统时钟精度设置
    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint uMilliseconds);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint uMilliseconds);

    private const int TargetCycleTimeMs = 10; // 设定 10ms 周期

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("控制循环已启动，目标周期: {Cycle} ms", TargetCycleTimeMs);

        var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);

        var thread = new Thread(() =>
        {
            try
            {
                HardwareControlLoop(stoppingToken);
                tcs.TrySetResult(null);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        })
        {
            Name = "Control_Loop_Thread",
            Priority = ThreadPriority.AboveNormal,
            IsBackground = true
        };

        thread.Start();
        return tcs.Task;
    }

    private void HardwareControlLoop(CancellationToken stoppingToken)
    {
        TimeBeginPeriod(1);// 提升系统时钟分辨率 -》1ms

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (_driver.Initialize(out short cardCount))
                    break;

                var msg = cardCount == 0
                    ? "初始化失败，未找到控制卡或控制卡异常"
                    : $"初始化失败，存在重名卡或系统冲突, 代码={Math.Abs(cardCount) - 1}";

                TriggerHardwareFault(msg, -1, false);

                WaitWithCancel(TimeSpan.FromSeconds(10), stoppingToken);
            }

            if (stoppingToken.IsCancellationRequested)
                return;

            ResetHardwareFault();

            var stopwatch = new Stopwatch();
            int errorCode = 0;
            long targetTicks = TimeSpan.FromMilliseconds(TargetCycleTimeMs).Ticks;

            while (!stoppingToken.IsCancellationRequested)
            {
                stopwatch.Restart();

                try
                {
                    // 检查 UI 是否发起控制卡复位指令
                    if (_hwData.ConsumeResetRequest() && _hwData.IsFaulted && _hwData.IsResetNeeded)
                    {
                        _logger.LogInformation("正在重置控制卡...");
                        _driver.Reset();
                        WaitWithCancel(TimeSpan.FromSeconds(5), stoppingToken);
                        ResetHardwareFault();
                        continue;
                    }

                    // --- 控制卡状态检测 ---
                    if (!CheckHardwareSafe(out errorCode))
                    {
                        TriggerHardwareFault("检测到控制卡故障", errorCode, true);
                    }

                    // --- 读取输入 ---
                    _nodes.PullAll();

                    if (!_hwData.IsFaulted)
                    {
                        // --- 执行控制逻辑 ---
                        //ProcessCell.Refresh(Environment.TickCount64);//控制逻辑不抛出异常
                    }
                    else
                    {
                        // 硬件故障状态下：执行ProcessCell.ToSafe()
                    }

                    // --- 刷新输出 ---
                    _nodes.PushAll();

                }
                catch (DmcException ex)
                {
                    TriggerHardwareFault(ex.Message, errorCode, ex.ErrorCode != 0);
                    // 硬件故障状态下：执行ProcessCell.ToSafe()
                    try { _nodes.PushAll(); } catch { /* 尽最大努力安全输出 */ }
                }
                catch (Exception e)
                {
                    TriggerHardwareFault($"未知异常：{e.Message}", -1, true);
                    // 硬件故障状态下：执行ProcessCell.ToSafe()
                    try { _nodes.PushAll(); } catch { /* 尽最大努力安全输出 */ }
                }

                // --- 高精度周期对齐 ---
                long sleepTicks = targetTicks - stopwatch.Elapsed.Ticks;

                if (sleepTicks > 0)
                {
                    int sleepMs = (int)(sleepTicks / TimeSpan.TicksPerMillisecond);

                    if (sleepMs > 2)
                    {
                        Thread.Sleep(sleepMs - 2);
                    }

                    SpinWait.SpinUntil(() =>
                        stoppingToken.IsCancellationRequested ||
                        stopwatch.Elapsed.Ticks >= targetTicks);
                }
                else if (sleepTicks < 0) // 如果超时，记录警告
                {
                    _logger.LogWarning("周期超时! 耗时: {Elapsed} ms", stopwatch.ElapsedMilliseconds);
                }
            }
        }
        finally
        {
            _logger.LogWarning("控制服务正在关闭，执行安全停机序列...");
            try
            {
                // 执行ProcessCell.ToSafe()
                _nodes.PushAll(); // 退出前执行，确保物理硬件安全
            }
            catch { /* 忽略通讯错误 */ }
            _driver.Dispose();
            TimeEndPeriod(1);
        }
    }

    private static void WaitWithCancel(TimeSpan timeout, CancellationToken token)
    {
        var end = DateTime.UtcNow + timeout;
        while (!token.IsCancellationRequested && DateTime.UtcNow < end)
        {
            Thread.Sleep(100);
        }
    }

    private bool CheckHardwareSafe(out int errorCode)
    {
        errorCode = _driver.ErrorCode;
        return errorCode == 0;
    }

    private void TriggerHardwareFault(string msg, int code, bool needsReset)
    {
        if (!_hwData.IsFaulted)
        {
            _logger.LogError("系统进入故障安全状态: {Msg}", msg);
        }
        _hwData.IsFaulted = true;
        _hwData.Message = msg;
        _hwData.IsResetNeeded = needsReset;
        _hwData.ErrorCode = code;
    }

    private void ResetHardwareFault()
    {
        _hwData.IsFaulted = false;
        _hwData.Message = string.Empty;
        _hwData.IsResetNeeded = false;
        _hwData.ErrorCode = 0;
    }

}
