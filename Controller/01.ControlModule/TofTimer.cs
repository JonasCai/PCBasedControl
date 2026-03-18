using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Controller._01.ControlModule;

public class TofTimer
{
    private long _startTime;
    private bool _isTiming;

    /// <summary>
    /// 定时器输出：信号接通时立刻为 true；信号断开后，延迟 PT 毫秒才会变为 false。
    /// </summary>
    public bool Q { get; private set; }

    /// <summary>
    /// 断开延时时间 (Preset Time, 单位：毫秒)
    /// </summary>
    public int PT { get; set; }

    public void Update(bool inSignal, long currentTimestampMs)
    {
        if (inSignal)
        {
            // 信号接通：输出立刻接通，并打断任何正在进行的倒计时
            Q = true;
            _isTiming = false;
        }
        else
        {
            // 信号断开：如果当前输出还是 true，且倒计时还没开始，则启动倒计时
            if (Q && !_isTiming)
            {
                _isTiming = true;
                _startTime = currentTimestampMs;
            }
            // 如果倒计时正在进行中，检查是否超时
            else if (_isTiming)
            {
                if (currentTimestampMs - _startTime >= PT)
                {
                    Q = false;          // 延时到达，输出断开
                    _isTiming = false;  // 结束计时状态
                }
            }
        }
    }
}
