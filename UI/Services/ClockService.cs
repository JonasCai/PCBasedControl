using System;
using System.Windows.Threading;

namespace UI.Services;

public class ClockService
{
    private readonly DispatcherTimer _timer;

    public ClockService()
    {
        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };

        _timer.Tick += (_, _) => TimeChanged?.Invoke(this, DateTime.Now);
    }

    public event EventHandler<DateTime>? TimeChanged;

    public void Start()
    {
        if (!_timer.IsEnabled)
        {
            _timer.Start();
        }
    }

    public void Stop()
    {
        if (_timer.IsEnabled)
        {
            _timer.Stop();
        }
    }
}
