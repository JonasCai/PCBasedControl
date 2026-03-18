using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace Controller.Hardware;

public class TcNode4 : EtherCatNodeBase
{
    public TcNode4(IEtherCatDriver etherCatDriver, ushort portNoStart) : base(etherCatDriver, portNoStart, 8, 0, 0) { }

    public float this[int index]
    {
        get
        {
            if (index < 0 || index > 3)
                throw new IndexOutOfRangeException("TcNode4 通道索引必须在 0 到 3 之间。");

            ReadOnlySpan<byte> source = GetInputSpan().Slice(index * 2, 2);
            short data = MemoryMarshal.Read<short>(source);
            return data / 10.0f;
        }
    }
}

public class TcNode8 : EtherCatNodeBase
{
    public TcNode8(IEtherCatDriver etherCatDriver, ushort portNoStart) : base(etherCatDriver, portNoStart, 16, 0, 0) { }

    public float this[int index]
    {
        get
        {
            if (index < 0 || index > 7)
                throw new IndexOutOfRangeException("TcNode4 通道索引必须在 0 到 7 之间。");

            ReadOnlySpan<byte> source = GetInputSpan().Slice(index * 2, 2);
            short data = MemoryMarshal.Read<short>(source);
            return data / 10.0f;
        }
    }
}
