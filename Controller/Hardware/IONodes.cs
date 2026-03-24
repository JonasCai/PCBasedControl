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
            A100 = new TcNode4(dmcDriver, 0),
            A101 = new TcNode4(dmcDriver, 2),
            A102 = new DINode16(dmcDriver, 4),
            A103 = new DONode16(dmcDriver, 0),
            MFC100 = new MFCNode(dmcDriver, 6,1),
            MFC200 = new MFCNode(dmcDriver, 7,2),

        };
    }

    public TcNode4 A100 { get; }
    public TcNode4 A101 { get; }
    public DINode16 A102 { get; }
    public DONode16 A103 { get; }
    public MFCNode MFC100 { get; }
    public MFCNode MFC200 { get; }

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