using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Controller.Hardware;

public abstract class EtherCatNodeBase
{
    private readonly ushort _inPortNoStart;
    private readonly ushort _outPortNoStart;
    private readonly int _inSize;
    private readonly int _outSize;
    private readonly Memory<byte> _inputImage;
    private readonly Memory<byte> _outputImage;
    private IEtherCatDriver _etherCatDriver;

    protected EtherCatNodeBase(IEtherCatDriver etherCatDriver, ushort inPortNoStart, int inSize, ushort outPortNoStart, int outSize)
    {
        _inPortNoStart = inPortNoStart;
        _outPortNoStart = outPortNoStart;
        _inSize = inSize;
        _outSize = outSize;
        _etherCatDriver = etherCatDriver;
        _inputImage = inSize > 0 ? new byte[inSize] : Array.Empty<byte>();
        _outputImage = outSize > 0 ? new byte[outSize] : Array.Empty<byte>();
    }

    /// <summary>
    /// 将硬件数据拉取到本地 _inputImage 和 _outputImage 中
    /// </summary>
    public void PullInputsFromHardware()
    {
        if (_inSize > 0)
            _etherCatDriver.ReadPortInputs(_inPortNoStart, _inputImage.Span);

        if (_outSize > 0)
            _etherCatDriver.ReadPortOutputs(_outPortNoStart, _outputImage.Span);
    }

    /// <summary>
    /// 将本地 _outputImage 刷写到硬件
    /// </summary>
    public void PushOutputsToHardware()
    {
        if (_outSize == 0) return;
        _etherCatDriver.WritePortOutputs(_outPortNoStart, _outputImage.Span);
    }

    /// <summary>
    /// 获取当前输入快照
    /// </summary>
    protected ReadOnlySpan<byte> GetInputSpan() => _inputImage.Span;

    /// <summary>
    /// 获取当前输出缓冲区
    /// </summary>
    protected Span<byte> GetOutputSpan() => _outputImage.Span;
}

public interface IEtherCatDriver
{
    // 从硬件读取数据填充到 span 中
    void ReadPortInputs(ushort portNoStart, Span<byte> dataDestination);
    void ReadPortOutputs(ushort portNoStart, Span<byte> dataDestination);

    // 将 span 中的数据写入到硬件
    void WritePortOutputs(ushort portNoStart, ReadOnlySpan<byte> dataSource);
}
