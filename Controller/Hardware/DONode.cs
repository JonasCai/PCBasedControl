using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace Controller.Hardware;

public class DONode8 : EtherCatNodeBase
{
    public DONode8(IEtherCatDriver etherCatDriver, ushort portNo) : base(etherCatDriver, 0, 0, portNo, 1) { }

    public bool this[int index]
    {
        get
        {
            if (index < 0 || index > 7)
                throw new IndexOutOfRangeException("DONode8 通道索引必须在 0 到 7 之间。");

            ReadOnlySpan<byte> source = GetOutputSpan();
            return (source[0] & (1 << index)) != 0;
        }

        set
        {
            if (index < 0 || index > 7)
                throw new IndexOutOfRangeException("DONode8 通道索引必须在 0 到 7 之间。");

            Span<byte> source = GetOutputSpan();
            if (value)
                source[0] = (byte)(source[0] | (1 << index));
            else
                source[0] = (byte)(source[0] & ~(1 << index));
        }
    }
}


public class DONode16 : EtherCatNodeBase
{
    public DONode16(IEtherCatDriver etherCatDriver, ushort portNo) : base(etherCatDriver, 0, 0, portNo, 2) { }

    public bool this[int index]
    {
        get
        {
            if (index < 0 || index > 15)
                throw new IndexOutOfRangeException("DONode16 通道索引必须在 0 到 15 之间。");

            ReadOnlySpan<byte> source = GetOutputSpan();
            ushort data = MemoryMarshal.Read<ushort>(source);
            return (data & (1 << index)) != 0;
        }

        set
        {
            if (index < 0 || index > 15)
                throw new IndexOutOfRangeException("DONode16 通道索引必须在 0 到 15 之间。");

            Span<byte> source = GetOutputSpan();
            ref ushort dataRef = ref MemoryMarshal.AsRef<ushort>(source);
            if (value)
                dataRef = (ushort)(dataRef | (1 << index));
            else
                dataRef = (ushort)(dataRef & ~(1 << index));
        }
    }
}
