using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace Controller.Hardware;

public class AINode4 : EtherCatNodeBase
{
    public AINode4(IEtherCatDriver etherCatDriver, ushort portNoStart) : base(etherCatDriver, portNoStart, 8, 0, 0) { }

    public short this[int index]
    {
        get
        {
            if (index < 0 || index > 3)
                throw new IndexOutOfRangeException("AINode4 通道索引必须在 0 到 3 之间。");

            ReadOnlySpan<byte> source = GetInputSpan().Slice(index * 2, 2);
            return MemoryMarshal.Read<short>(source);
        }
    }
}


public class AINode8 : EtherCatNodeBase
{
    public AINode8(IEtherCatDriver etherCatDriver, ushort portNoStart) : base(etherCatDriver, portNoStart, 16, 0, 0) { }

    public short this[int index]
    {
        get
        {
            if (index < 0 || index > 7)
                throw new IndexOutOfRangeException("AINode7 通道索引必须在 0 到 7 之间。");

            ReadOnlySpan<byte> source = GetInputSpan().Slice(index * 2, 2);
            return MemoryMarshal.Read<short>(source);
        }
    }
}
