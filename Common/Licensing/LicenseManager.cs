using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Common.Licensing;

public static class LicenseManager
{
    public const string CurrentProductCode = "PCBasedControl";

    private const string PUBLIC_KEY =
        "把你生成的公钥Base64填到这里";

    public static LicenseValidationResult Validate(string licenseFileContent)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(licenseFileContent))
            {
                return LicenseValidationResult.Fail(
                    LicenseStatus.InvalidFormat,
                    "授权文件为空");
            }

            LicenseContainer? container;
            try
            {
                container = JsonSerializer.Deserialize<LicenseContainer>(
                    licenseFileContent,
                    JsonOptions.Default);
            }
            catch (Exception ex)
            {
                return LicenseValidationResult.Fail(
                    LicenseStatus.InvalidFormat,
                    $"授权文件不是合法 JSON: {ex.Message}");
            }

            if (container == null ||
                string.IsNullOrWhiteSpace(container.Payload) ||
                string.IsNullOrWhiteSpace(container.Signature))
            {
                return LicenseValidationResult.Fail(
                    LicenseStatus.InvalidFormat,
                    "授权文件缺少必要字段");
            }

            bool signatureOk = SecurityGuard.VerifyData(
                container.Payload,
                container.Signature,
                PUBLIC_KEY);

            if (!signatureOk)
            {
                return LicenseValidationResult.Fail(
                    LicenseStatus.InvalidSignature,
                    "授权文件签名校验失败，文件可能已被篡改");
            }

            LicenseModel? model;
            try
            {
                string payloadJson = Encoding.UTF8.GetString(
                    Convert.FromBase64String(container.Payload));

                model = JsonSerializer.Deserialize<LicenseModel>(
                    payloadJson,
                    JsonOptions.Default);
            }
            catch (Exception ex)
            {
                return LicenseValidationResult.Fail(
                    LicenseStatus.InvalidPayload,
                    $"授权内容解析失败: {ex.Message}");
            }

            if (model == null)
            {
                return LicenseValidationResult.Fail(
                    LicenseStatus.InvalidPayload,
                    "授权内容为空");
            }

            if (string.IsNullOrWhiteSpace(model.LicenseId) ||
                string.IsNullOrWhiteSpace(model.ProductCode))
            {
                return LicenseValidationResult.Fail(
                    LicenseStatus.InvalidPayload,
                    "授权内容缺少关键字段");
            }

            if (model.IssuedAtUtc.Kind != DateTimeKind.Utc ||
                model.ExpireDateUtc.Kind != DateTimeKind.Utc)
            {
                return LicenseValidationResult.Fail(
                    LicenseStatus.InvalidPayload,
                    "授权时间字段必须为 UTC");
            }

            if (!string.Equals(model.ProductCode, CurrentProductCode, StringComparison.Ordinal))
            {
                return LicenseValidationResult.Fail(
                    LicenseStatus.ProductMismatch,
                    $"授权产品不匹配。授权产品: {model.ProductCode}，当前产品: {CurrentProductCode}");
            }

            if (TimeRatchet.IsTimeRollbackDetected(model.IssuedAtUtc, out string timeMsg))
            {
                return LicenseValidationResult.Fail(
                    LicenseStatus.TimeRollbackDetected,
                    timeMsg);
            }

            DateTime nowUtc = DateTime.UtcNow;
            if (nowUtc > model.ExpireDateUtc)
            {
                return LicenseValidationResult.Fail(
                    LicenseStatus.Expired,
                    $"授权已于 {model.ExpireDateUtc:yyyy-MM-dd HH:mm:ss} UTC 过期");
            }

            DeviceBinding currentBinding = DeviceFingerprint.Capture();
            DeviceMatchResult match = DeviceFingerprint.Match(model.DeviceBinding, currentBinding);

            if (!match.IsMatch)
            {
                return LicenseValidationResult.Fail(
                    LicenseStatus.MachineMismatch,
                    $"硬件绑定不匹配。要求命中 {match.RequiredCount} 项，实际命中 {match.MatchedCount}/{match.AvailableCount} 项。");
            }

            return LicenseValidationResult.Ok(model);
        }
        catch (Exception ex)
        {
            return LicenseValidationResult.Fail(
                LicenseStatus.EnvironmentError,
                $"验证过程发生异常: {ex.Message}");
        }
    }
}
