using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Common.Licensing;

public static class DeviceRequestService
{
    public static DeviceRequest CreateRequest(string productCode, string customerName = "")
    {
        return new DeviceRequest
        {
            ProductCode = productCode,
            CustomerName = customerName ?? string.Empty,
            RequestTimeUtc = DateTime.UtcNow,
            DeviceBinding = DeviceFingerprint.Capture()
        };
    }

    public static string Serialize(DeviceRequest request)
    {
        return JsonSerializer.Serialize(request, JsonOptions.Indented);
    }

    public static DeviceRequest Deserialize(string json)
    {
        return JsonSerializer.Deserialize<DeviceRequest>(json, JsonOptions.Default)
               ?? throw new InvalidDataException("设备请求文件解析失败");
    }
}
