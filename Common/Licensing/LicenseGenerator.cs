using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Common.Licensing;

public static class LicenseGenerator
{
    public static string CreateLicenseFile(LicenseModel model, string privateKey)
    {
        ValidateModel(model);

        string json = JsonSerializer.Serialize(model, JsonOptions.Default);
        string payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
        string signature = SecurityGuard.SignData(payload, privateKey);

        var container = new LicenseContainer
        {
            Payload = payload,
            Signature = signature
        };

        return JsonSerializer.Serialize(container, JsonOptions.Indented);
    }

    private static void ValidateModel(LicenseModel model)
    {
        if (string.IsNullOrWhiteSpace(model.LicenseId))
            throw new ArgumentException("LicenseId 不能为空");

        if (string.IsNullOrWhiteSpace(model.ProductCode))
            throw new ArgumentException("ProductCode 不能为空");

        if (model.IssuedAtUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("IssuedAtUtc 必须是 UTC");

        if (model.ExpireDateUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("ExpireDateUtc 必须是 UTC");

        if (model.ExpireDateUtc <= model.IssuedAtUtc)
            throw new ArgumentException("ExpireDateUtc 必须晚于 IssuedAtUtc");
    }
}

public static class JsonOptions
{
    public static readonly JsonSerializerOptions Default = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public static readonly JsonSerializerOptions Indented = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };
}
