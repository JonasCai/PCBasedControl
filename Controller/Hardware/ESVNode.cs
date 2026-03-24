

using Controller.Hardware;
using System.Runtime.InteropServices;

public class ESVNode32 : EtherCatNodeBase
{
    public ESVNode32(IEtherCatDriver etherCatDriver, ushort portNo) : base(etherCatDriver, 0, 0, portNo, 4) { }

    public bool this[int index]
    {
        get
        {
            if (index < 0 || index > 31)
                throw new IndexOutOfRangeException("ESVNode32 通道索引必须在 0 到 31 之间。");

            ReadOnlySpan<byte> source = GetOutputSpan();
            uint data = MemoryMarshal.Read<uint>(source);
            return (data & (1u << index)) != 0;
        }

        set
        {
            if (index < 0 || index > 31)
                throw new IndexOutOfRangeException("ESVNode32 通道索引必须在 0 到 31 之间。");

            Span<byte> source = GetOutputSpan();
            ref uint dataRef = ref MemoryMarshal.AsRef<uint>(source);
            if (value)
                dataRef |= (1u << index);
            else
                dataRef &= ~(1u << index);
        }
    }
}
