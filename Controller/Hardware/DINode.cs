using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace Controller.Hardware;

public class DINode8 : EtherCatNodeBase
{
    public DINode8(IEtherCatDriver etherCatDriver, ushort portNo) : base(etherCatDriver, portNo, 1, 0, 0) { }

    public bool this[int index]
    {
        get
        {
            if (index < 0 || index > 7)
                throw new IndexOutOfRangeException("DINode8 通道索引必须在 0 到 7 之间。");

            ReadOnlySpan<byte> source = GetInputSpan();
            return (source[0] & (1 << index)) != 0;
        }
    }
}

public class DINode16 : EtherCatNodeBase
{
    public DINode16(IEtherCatDriver etherCatDriver, ushort portNo) : base(etherCatDriver, portNo, 2, 0, 0) { }

    public bool this[int index]
    {
        get
        {
            if (index < 0 || index > 15)
                throw new IndexOutOfRangeException("DINode16 通道索引必须在 0 到 15 之间。");

            ReadOnlySpan<byte> source = GetInputSpan();
            if (source.Length == 0) return false;
            ushort data = MemoryMarshal.Read<ushort>(source);
            return (data & (1 << index)) != 0;
        }
    }
}
