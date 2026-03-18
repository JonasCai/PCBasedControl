using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace Controller.Hardware;

public class AONode4 : EtherCatNodeBase
{
    public AONode4(IEtherCatDriver etherCatDriver, ushort portNo) : base(etherCatDriver, 0, 0, portNo, 8) { }

    public short this[int index]
    {
        get
        {
            if (index < 0 || index > 3)
                throw new IndexOutOfRangeException("AONode4 通道索引必须在 0 到 3 之间。");

            ReadOnlySpan<byte> source = GetOutputSpan().Slice(index * 2, 2);
            return MemoryMarshal.Read<short>(source);
        }

        set
        {
            if (index < 0 || index > 3)
                throw new IndexOutOfRangeException("AONode4 通道索引必须在 0 到 3 之间。");

            Span<byte> source = GetOutputSpan().Slice(index * 2, 2);
            MemoryMarshal.Write(source, value);
        }
    }
}

public class AONode8 : EtherCatNodeBase
{
    public AONode8(IEtherCatDriver etherCatDriver, ushort portNo) : base(etherCatDriver, 0, 0, portNo, 16) { }

    public short this[int index]
    {
        get
        {
            if (index < 0 || index > 7)
                throw new IndexOutOfRangeException("AONode8 通道索引必须在 0 到 7 之间。");

            ReadOnlySpan<byte> source = GetOutputSpan().Slice(index * 2, 2);
            return MemoryMarshal.Read<short>(source);
        }

        set
        {
            if (index < 0 || index > 7)
                throw new IndexOutOfRangeException("AONode8 通道索引必须在 0 到 7 之间。");

            Span<byte> source = GetOutputSpan().Slice(index * 2, 2);
            MemoryMarshal.Write(source, value);
        }
    }
}
