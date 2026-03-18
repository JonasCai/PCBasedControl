using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace Controller.Hardware;

public class LeadshineDmcDriver : IEtherCatDriver, IDisposable
{
    private bool _disposed = false;
    private const ushort _cardNo = 0;
    //Func 和 Action 泛型委托不支持带有 ref 或 out 参数的函数指针
    public delegate short DmcReadPortDelegate(ushort cardNo, ushort portNo, ref uint data);

    public void ReadPortInputs(ushort portNoStart, Span<byte> dataDestination)
    {
        if (dataDestination.IsEmpty) return;
        ProcessPortIO(portNoStart, dataDestination, LTDMC.dmc_read_inport_ex);
    }

    public void ReadPortOutputs(ushort portNoStart, Span<byte> dataDestination)
    {
        if (dataDestination.IsEmpty) return;
        ProcessPortIO(portNoStart, dataDestination, LTDMC.dmc_read_outport_ex);
    }

    public void WritePortOutputs(ushort portNoStart, ReadOnlySpan<byte> dataSource)
    {
        if (dataSource.Length == 0) return;
        ushort currentPort = portNoStart;

        if (dataSource.Length % 4 != 0)
            throw new DmcException($"写入控制卡输出端口失败, 数据长度不是 4 的整数倍，端口号={currentPort},数据长度={dataSource.Length}");

        int currentOffset = 0;
        while (currentOffset < dataSource.Length)
        {
            uint dataToWrite = MemoryMarshal.Read<uint>(dataSource.Slice(currentOffset, 4));
            var errCode = LTDMC.dmc_write_outport(_cardNo, currentPort, dataToWrite);
            if (errCode != 0)
                throw new DmcException($"写入控制卡输出端口失败, 端口号={currentPort}, 故障码={errCode}", errCode);
            currentOffset += 4;
            currentPort++;
        }
    }

    public bool Initialize(out short cardCount)
    {
        cardCount = LTDMC.dmc_board_init();
        if (cardCount <= 0)
            return false;

        return true;
    }

    public ushort ErrorCode => GetHardwareErrorCode();

    public void Reset()
    {
        var errCode = LTDMC.dmc_soft_reset(_cardNo);
        if (errCode != 0)
            throw new DmcException($"控制卡热复位失败,故障码={errCode}", errCode);
    }

    public void Dispose()
    {
        Dispose(true);
        // 告诉 GC 不要再调用析构函数
        GC.SuppressFinalize(this);
    }

    // 终结器 (兜底防护：即使忘了写 using 或 Dispose，GC 回收时也会关停板卡)
    ~LeadshineDmcDriver()
    {
        Dispose(false);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                ;// 释放其他托管资源
            }

            // 释放非托管资源 (无论是否是主动 Dispose 还是 GC 回收，都必须断开板卡)
            LTDMC.dmc_board_close();
            _disposed = true;
        }
    }

    private ushort GetHardwareErrorCode()
    {
        ushort errorCode = 0;
        var rlt = LTDMC.nmc_get_errcode(_cardNo, 2, ref errorCode);
        if (rlt != 0)
            throw new DmcException($"获取控制卡内部错误码失败, 返回码={rlt}", rlt);

        return errorCode;
    }

    private void ProcessPortIO(ushort portNoStart, Span<byte> dataDestination, DmcReadPortDelegate readMethod)
    {
        if (dataDestination.Length == 0) return;
        int currentOffset = 0;
        ushort currentPort = portNoStart;
        Span<byte> tempBuffer = stackalloc byte[4];

        while (currentOffset < dataDestination.Length)
        {
            uint rawData = 0;
            var errCode = readMethod(_cardNo, currentPort, ref rawData);
            if (errCode != 0)
                throw new DmcException($"读取控制卡端口失败, 端口号={currentPort}, 故障码={errCode}", errCode);
            int remaining = dataDestination.Length - currentOffset;
            if (remaining >= 4)
            {
                // 剩余空间足够，写入完整的 uint
                MemoryMarshal.Write(dataDestination.Slice(currentOffset, 4), in rawData);
                currentOffset += 4;
            }
            else
            {
                // 剩余空间不足 4 字节，将 uint 的前 remaining 个字节部分写入剩余空间
                MemoryMarshal.Write(tempBuffer, in rawData);
                tempBuffer.Slice(0, remaining).CopyTo(dataDestination.Slice(currentOffset, remaining));
                currentOffset += remaining;
            }
            currentPort++;
        }
    }
}


public class DmcException : Exception
{
    public short ErrorCode { get; init; }

    public DmcException(string message) : base(message)
    {
        ErrorCode = 0;
    }

    public DmcException(string message, short errCode) : base(message)
    {
        ErrorCode = errCode;
    }
}
