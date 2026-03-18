using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace Controller.Hardware;

public class MFCNode : EtherCatNodeBase
{
    public MFCNode(IEtherCatDriver etherCatDriver, ushort inPortNo, ushort outPortNo) : base(etherCatDriver, inPortNo, 4, outPortNo, 4) { }

    public float FlowReading => MemoryMarshal.Read<float>(GetInputSpan());

    public float FlowSetting
    {
        get => MemoryMarshal.Read<float>(GetOutputSpan());
        set => MemoryMarshal.Write(GetOutputSpan(), value);
    }
}

