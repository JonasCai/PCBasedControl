using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Common.Licensing;

public sealed class LicenseModel
{
    public string LicenseId { get; init; } = string.Empty;
    public string ProductCode { get; init; } = string.Empty;
    public string CustomerName { get; init; } = string.Empty;
    public DateTime IssuedAtUtc { get; init; }
    public DateTime ExpireDateUtc { get; init; }
    public string[] Features { get; init; } = Array.Empty<string>();
    public DeviceBinding DeviceBinding { get; init; } = new();
}

public sealed class DeviceBinding
{
    public string CpuId { get; init; } = string.Empty;
    public string BaseBoardSerial { get; init; } = string.Empty;
    public string BiosSerial { get; init; } = string.Empty;
    public string SystemUuid { get; init; } = string.Empty;
    public string DiskSerial { get; init; } = string.Empty;
    public int RequiredMatchCount { get; init; } = 3;
}

public sealed class LicenseContainer
{
    public string Payload { get; init; } = string.Empty;
    public string Signature { get; init; } = string.Empty;
}

public sealed class DeviceRequest
{
    public string ProductCode { get; init; } = string.Empty;
    public string CustomerName { get; init; } = string.Empty;
    public DateTime RequestTimeUtc { get; init; }
    public DeviceBinding DeviceBinding { get; init; } = new();
}
