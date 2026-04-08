
namespace Controller.Common;

public sealed class LowPassFilter
{
    private bool _initialized;
    private float _value;
    public float LastValue => _value;
    public bool IsInitialized => _initialized;
    public void Reset() { _initialized = false; _value = 0; }
    public float Filter(float input, float alpha)
    {
        if (alpha <= 0 || alpha > 1)
            throw new ArgumentOutOfRangeException(nameof(alpha), "alpha ±ØÐëÔÚ (0, 1] ·¶Î§ÄÚ¡£");
        if (!_initialized)
        {
            _value = input;
            _initialized = true;
            return _value;
        }
        _value = alpha * input + (1.0f - alpha) * _value;
        return _value;
    }
}
