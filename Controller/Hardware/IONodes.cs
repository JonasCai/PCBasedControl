using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Controller.Hardware;

public class IONodes
{
    private readonly Dictionary<string, EtherCatNodeBase> _nodeMap = new();

    public IONodes(IEtherCatDriver dmcDriver)
    {
        _nodeMap.Add("A100", new TcNode4(dmcDriver, 1));
        _nodeMap.Add("A101", new TcNode4(dmcDriver, 3));
        _nodeMap.Add("A102", new DINode16(dmcDriver, 5));
        _nodeMap.Add("A103", new DONode16(dmcDriver, 6));
        _nodeMap.Add("MFC100", new MFCNode(dmcDriver, 7, 1));
        _nodeMap.Add("MFC200", new MFCNode(dmcDriver, 8, 2));
    }

    public TcNode4 A100 => (TcNode4)_nodeMap["A100"];
    public TcNode4 A101 => (TcNode4)_nodeMap["A101"];
    public DINode16 A102 => (DINode16)_nodeMap["A102"];
    public DONode16 A103 => (DONode16)_nodeMap["A103"];
    public MFCNode MFC100 => (MFCNode)_nodeMap["MFC100"];
    public MFCNode MFC200 => (MFCNode)_nodeMap["MFC200"];

    public void PullAll()
    {
        foreach (var node in _nodeMap.Values)
            node.PullInputsFromHardware();
    }

    public void PushAll()
    {
        foreach (var node in _nodeMap.Values)
            node.PushOutputsToHardware();
    }
}