using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Controller._01.ControlModule;

public class TonTimer
{
    private long _startTime;
    private bool _isTiming;

    /// <summary>
    /// 定时器输出：当输入信号持续时间 >= PT 时，Q 为 true
    /// </summary>
    public bool Q { get; private set; }

    /// <summary>
    /// 预设时间 (Preset Time, 单位：毫秒)
    /// </summary>
    public int PT { get; set; }

    public void Refresh(bool inSignal, long currentTimestampMs)
    {
        if (inSignal)
        {
            // 信号刚刚从 false 变成 true 的瞬间，记录起点
            if (!_isTiming)
            {
                _isTiming = true;
                _startTime = currentTimestampMs;
                Q = false;
            }
            // 信号持续为 true，检查是否达到预设时间
            else if (!Q)
            {
                if (currentTimestampMs - _startTime >= PT)
                {
                    Q = true;
                }
            }
        }
        else
        {
            // 信号一旦断开，定时器立刻复位
            _isTiming = false;
            Q = false;
        }
    }
}
