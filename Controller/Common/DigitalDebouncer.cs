
namespace Controller.Common;

public struct DigitalDebouncer
{
    private bool _output;
    private bool _lastRawState;
    private long _stateChangeTimestamp;
    private readonly long _debounceTimeMs;

    public DigitalDebouncer(long debounceTimeMs = 50)
    {
        _debounceTimeMs = debounceTimeMs;
        _output = false;
        _lastRawState = false;
        _stateChangeTimestamp = 0;
    }

    // 核心滤波逻辑
    public bool Filter(bool rawSignal, long currentTimestampMs)
    {
        // 如果原始信号发生了翻转，记录当前的时间戳
        if (rawSignal != _lastRawState)
        {
            _stateChangeTimestamp = currentTimestampMs;
            _lastRawState = rawSignal;
        }

        // 如果原始信号和确认输出不一致，检查稳定时间是否达标
        if (rawSignal != _output)
        {
            if (currentTimestampMs - _stateChangeTimestamp >= _debounceTimeMs)
            {
                // 稳定时间足够，确认状态翻转
                _output = rawSignal;
            }
        }

        return _output;
    }
}
