using Controller.gRPC;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Controller.S88;

public interface IS88Object : IControllable
{
    string Name { get; } // 名称 (e.g., "UNIT01/EM02/V01")
    void ToSafe(); // 去安全位
    void Refresh(long currentTimestampMs); // 周期性逻辑 (Scan)
}

public interface IControllable
{
    void ExecuteCommand(InternalCommand command);
}

public interface IControlModule : IS88Object
{

}

public interface IEquipmentModule : IS88Object
{
    public EMState State { get; }
    bool TryGetCm(string name, out IControlModule? cm);
}

public interface IUnit : IS88Object
{
    bool IsActive { get; }
    S88State State { get; }
    S88Mode Mode { get; }
    string GetActiveRecipeJson();
}

public interface IProcessCell : IS88Object
{
    bool TryGetUnit(string unitName, out S88UnitBase? unit);
}

