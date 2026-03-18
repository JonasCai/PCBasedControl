using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Controller.Hardware;

public class HwData
{
    private bool _isFaulted;
    private bool _isResetNeeded;
    private int _errorCode;
    private string _message = string.Empty;

    // 当状态发生任何变化时触发此事件
    public event Action<HwStatusChangedEventArgs>? OnStatusChanged;

    public bool IsFaulted
    {
        get => _isFaulted;
        set { if (_isFaulted != value) { _isFaulted = value; Notify(); } }
    }

    public bool IsResetNeeded
    {
        get => _isResetNeeded;
        set { if (_isResetNeeded != value) { _isResetNeeded = value; Notify(); } }
    }

    public int ErrorCode
    {
        get => _errorCode;
        set { if (_errorCode != value) { _errorCode = value; Notify(); } }
    }

    public string Message
    {
        get => _message;
        set { if (_message != value) { _message = value; Notify(); } }
    }

    private void Notify()
    {
        OnStatusChanged?.Invoke(new HwStatusChangedEventArgs
        {
            IsFaulted = IsFaulted,
            IsResetNeeded = IsResetNeeded,
            ErrorCode = ErrorCode,
            Message = Message
        });
    }

    // 复位请求逻辑（保持原子操作）
    private int _resetRequestSignal = 0;
    public void RequestReset() => Interlocked.Exchange(ref _resetRequestSignal, 1);
    public bool ConsumeResetRequest() => Interlocked.Exchange(ref _resetRequestSignal, 0) == 1;
}

public class HwStatusChangedEventArgs : EventArgs
{
    public bool IsFaulted { get; init; }
    public bool IsResetNeeded { get; init; }
    public int ErrorCode { get; init; }
    public string Message { get; init; } = string.Empty;
}
