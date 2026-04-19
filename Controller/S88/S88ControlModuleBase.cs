
using Controller._01.ControlModule;
using Controller.EventLogger;
using Controller.gRPC;

namespace Controller.S88;

public abstract class S88ControlModuleBase : S88ObjectBase
{
    public S88ControlModuleBase(string name, IEventProducer eventProducer, ILogger logger):base(name, eventProducer, logger)
    {
    }
}
