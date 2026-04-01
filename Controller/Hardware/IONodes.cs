using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Controller.Hardware;

public class IONodes 
{ 
    private readonly EtherCatNodeBase[] _allNodes;
    
    public IONodes(IEtherCatDriver dmcDriver) 
    { 
        _allNodes = new EtherCatNodeBase[] 
        { 
            A101 = new DINode16(dmcDriver, 0), 
            A102 = new DINode16(dmcDriver, 1), 
            A103 = new DINode16(dmcDriver, 2), 
            A104 = new DINode16(dmcDriver, 3), 
            A105 = new DINode16(dmcDriver, 4), 
            A106 = new DINode16(dmcDriver, 5), 
            A107 = new DONode16(dmcDriver, 0), 
            A108 = new DONode16(dmcDriver, 1), 
            //A109:NX - PD1000
            A110 = new DONode16(dmcDriver, 2), 
            A111 = new DONode16(dmcDriver, 3), 
            A112 = new DONode16(dmcDriver, 4), 
            A113 = new AINode8(dmcDriver, 6), 
            A114 = new TcNode4(dmcDriver, 10), 
            A115 = new TcNode4(dmcDriver, 12), 
            A116 = new TcNode4(dmcDriver, 14), 
            A117 = new TcNode4(dmcDriver, 16), 
            //A118:NX - PD1000
            A119 = new TcNode4(dmcDriver, 18), 
            A120 = new TcNode4(dmcDriver, 20), 
            A121 = new TcNode4(dmcDriver, 22), 
            A122 = new TcNode4(dmcDriver, 24), 
            A123 = new TcNode4(dmcDriver, 26), 
            A124 = new TcNode4(dmcDriver, 28), 
            A125 = new TcNode4(dmcDriver, 30), 
            A126 = new TcNode4(dmcDriver, 32), 
            //A127:NX - PD1000
            A128 = new TcNode4(dmcDriver, 34), 
            A129 = new TcNode4(dmcDriver, 36), 
            A130 = new TcNode4(dmcDriver, 38), 
            A131 = new TcNode4(dmcDriver, 40), 
            A132 = new TcNode4(dmcDriver, 42), 
            A133 = new TcNode4(dmcDriver, 44), 
            A134 = new TcNode4(dmcDriver, 46), 
            A135 = new TcNode4(dmcDriver, 48), 
            A201 = new DINode16(dmcDriver, 50), 
            A202 = new DINode16(dmcDriver, 51), 
            A203 = new DONode16(dmcDriver, 5), 
            A204 = new DONode16(dmcDriver, 6), 
            A205 = new AINode8(dmcDriver, 52), 
            A206 = new AINode4(dmcDriver, 56), 
            A207 = new TcNode4(dmcDriver, 58), 
            A208 = new TcNode4(dmcDriver, 60), 
            //A209:NX - PD1000
            A210 = new TcNode4(dmcDriver, 62), 
            A211 = new TcNode4(dmcDriver, 64), 
            A212 = new TcNode4(dmcDriver, 66), 
            A213 = new TcNode4(dmcDriver, 68), 
            A214 = new TcNode4(dmcDriver, 70), 
            A215 = new DONode16(dmcDriver, 7), 
            A216 = new TcNode4(dmcDriver, 72), 
            ESV01 = new ESVNode32(dmcDriver, 8), 
            MFC111 = new MFCNode(dmcDriver, 74, 9), 
            MFC112 = new MFCNode(dmcDriver, 75, 10), 
            MFC121 = new MFCNode(dmcDriver, 76, 11), 
            MFC122 = new MFCNode(dmcDriver, 77, 12), 
            MFC131 = new MFCNode(dmcDriver, 78, 13), 
            MFC132 = new MFCNode(dmcDriver, 79, 14) 
        }; 

    } 
    
    public DINode16 A101 { get; } 
    public DINode16 A102 { get; } 
    public DINode16 A103 { get; } 
    public DINode16 A104 { get; } 
    public DINode16 A105 { get; } 
    public DINode16 A106 { get; } 
    public DONode16 A107 { get; } 
    public DONode16 A108 { get; } 
    public DONode16 A110 { get; } 
    public DONode16 A111 { get; } 
    public DONode16 A112 { get; } 
    public AINode8 A113 { get; } 
    public TcNode4 A114 { get; } 
    public TcNode4 A115 { get; } 
    public TcNode4 A116 { get; } 
    public TcNode4 A117 { get; } 
    public TcNode4 A119 { get; } 
    public TcNode4 A120 { get; } 
    public TcNode4 A121 { get; } 
    public TcNode4 A122 { get; } 
    public TcNode4 A123 { get; } 
    public TcNode4 A124 { get; } 
    public TcNode4 A125 { get; } 
    public TcNode4 A126 { get; } 
    public TcNode4 A128 { get; } 
    public TcNode4 A129 { get; } 
    public TcNode4 A130 { get; } 
    public TcNode4 A131 { get; } 
    public TcNode4 A132 { get; } 
    public TcNode4 A133 { get; } 
    public TcNode4 A134 { get; } 
    public TcNode4 A135 { get; } 
    public DINode16 A201 { get; } 
    public DINode16 A202 { get; } 
    public DONode16 A203 { get; } 
    public DONode16 A204 { get; } 
    public AINode8 A205 { get; } 
    public AINode4 A206 { get; } 
    public TcNode4 A207 { get; } 
    public TcNode4 A208 { get; } 
    public TcNode4 A210 { get; } 
    public TcNode4 A211 { get; } 
    public TcNode4 A212 { get; } 
    public TcNode4 A213 { get; } 
    public TcNode4 A214 { get; } 
    public DONode16 A215 { get; } 
    public TcNode4 A216 { get; } 
    public ESVNode32 ESV01 { get; } 
    public MFCNode MFC111 { get; } 
    public MFCNode MFC112 { get; } 
    public MFCNode MFC121 { get; } 
    public MFCNode MFC122 { get; } 
    public MFCNode MFC131 { get; } 
    public MFCNode MFC132 { get; } 
    
    public void PullAll() 
    { 
        foreach (var node in _allNodes) 
            node.PullInputsFromHardware(); 
    } 
    
    public void PushAll() 
    { 
        foreach (var node in _allNodes) 
            node.PushOutputsToHardware(); 
    } 
}