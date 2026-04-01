using System;

namespace Controller._01.ControlModule;

/// <summary>
/// 轻量级源耗量追踪器（基于流量积分法）
/// 供 Unit 周期性调用 Refresh 进行推算，Unit 负责读取警告标志位并触发实际报警
/// </summary>
public class SourceTracker
{
    private readonly SourceTrackerCfg _cfg;
    private long _lastIntegrationTimestampMs;

    public SourceTracker(SourceTrackerCfg cfg)
    {
        _cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));

        if (!_cfg.Validate())
            throw new ArgumentException($"SourceTracker[{_cfg.Name}] 配置不完整", nameof(cfg));

        // 初始化剩余量 (实际项目中通常在构造后从数据库恢复此值)
        RemainingAmount = _cfg.Capacity;
    }

    // ==========================================
    // 核心对外属性 (供 Unit 轮询读取)
    // ==========================================
    public string Name => _cfg.Name;
    public double RemainingAmount { get; private set; }
    public double ConsumedAmount => _cfg.Capacity - RemainingAmount;

    /// <summary>
    /// 触及 Low 阈值 (例如剩余 20%)
    /// </summary>
    public bool IsLowWarning { get; private set; }

    /// <summary>
    /// 触及 LowLow 阈值 (例如剩余 5%)
    /// </summary>
    public bool IsLowLowWarning { get; private set; }


    // ==========================================
    // 周期计算引擎
    // ==========================================
    public void Refresh(long currentTimestampMs)
    {
        // 处理开机第一帧，防止 dt 异常计算
        if (_lastIntegrationTimestampMs == 0)
        {
            _lastIntegrationTimestampMs = currentTimestampMs;
            return;
        }

        long dtMs = currentTimestampMs - _lastIntegrationTimestampMs;
        _lastIntegrationTimestampMs = currentTimestampMs;

        // 1. 流量积分推算
        float currentFlow = _cfg.ReadFlowRate();

        // 过滤极小漂移底噪和时间异常
        if (dtMs > 0 && currentFlow > _cfg.FlowDeadband)
        {
            // 积分: 消耗量 = 流量(单位/分钟) / 60000(毫秒) * 经过的毫秒数 * 转换系数
            double consumedThisTick = (currentFlow / 60000.0) * dtMs * _cfg.IntegrationConversionFactor;
            RemainingAmount -= consumedThisTick;

            if (RemainingAmount < 0) RemainingAmount = 0; // 触底保护
        }

        // 2. 评估警告标志位 (带回差死区，防止临界点闪烁)
        EvaluateWarnings();
    }


    // ==========================================
    // 外部操作接口
    // ==========================================

    /// <summary>
    /// 换瓶/加注
    /// </summary>
    /// <param name="newAmount">如果不传，默认加满至 Capacity</param>
    public void Refill(double? newAmount = null)
    {
        RemainingAmount = Math.Clamp(newAmount ?? _cfg.Capacity, 0, _cfg.Capacity);

        // 换瓶后立即重新评估警告状态，使其瞬间解除
        EvaluateWarnings();
    }

    /// <summary>
    /// 手动校准当前余量
    /// </summary>
    public void SetRemainingAmount(double amount)
    {
        RemainingAmount = Math.Clamp(amount, 0, _cfg.Capacity);
    }

    // ==========================================
    // 私有逻辑
    // ==========================================
    private void EvaluateWarnings()
    {
        // LowLow Warning (下下限 / 极低液位)
        if (RemainingAmount <= _cfg.LowLowThreshold)
        {
            IsLowLowWarning = true;
        }
        else if (RemainingAmount >= _cfg.LowLowThreshold + _cfg.WarningDeadband)
        {
            IsLowLowWarning = false;
        }

        // Low Warning (下限 / 低液位)
        // 注意：当跌破 LowLow 时，通常只报严重的 LowLow，这里做了互斥处理，避免上层 Unit 同时报两条警告
        if (RemainingAmount <= _cfg.LowThreshold && RemainingAmount > _cfg.LowLowThreshold)
        {
            IsLowWarning = true;
        }
        else if (RemainingAmount >= _cfg.LowThreshold + _cfg.WarningDeadband || IsLowLowWarning)
        {
            IsLowWarning = false;
        }
    }
}

// ==========================================
// 配置类
// ==========================================
public class SourceTrackerCfg
{
    public required string Name { get; init; }
    public required double Capacity { get; init; } // 源瓶满载容量

    // 积分法核心配置
    public required Func<float> ReadFlowRate { get; init; } // 读取实时流量委托
    public float FlowDeadband { get; init; } = 0.1f; // 流量底噪过滤阈值
    public double IntegrationConversionFactor { get; init; } = 1.0; // SCCM -> 质量/体积 的转换系数

    // 警告阈值
    public double LowThreshold { get; init; } = 20.0;
    public double LowLowThreshold { get; init; } = 5.0;
    public double WarningDeadband { get; init; } = 2.0; // 警告回差死区

    public bool Validate()
    {
        return !string.IsNullOrEmpty(Name) && Capacity > 0 && ReadFlowRate != null;
    }
}
