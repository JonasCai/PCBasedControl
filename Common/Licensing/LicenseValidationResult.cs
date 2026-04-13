using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Common.Licensing;

public sealed class LicenseValidationResult
{
    public bool Success { get; init; }
    public LicenseStatus Status { get; init; }
    public string Message { get; init; } = string.Empty;
    public LicenseModel? License { get; init; }

    public static LicenseValidationResult Ok(LicenseModel license, string message = "授权有效")
        => new()
        {
            Success = true,
            Status = LicenseStatus.Valid,
            Message = message,
            License = license
        };

    public static LicenseValidationResult Fail(LicenseStatus status, string message)
        => new()
        {
            Success = false,
            Status = status,
            Message = message
        };
}

public enum LicenseStatus
{
    Valid,
    InvalidFormat,
    InvalidSignature,
    InvalidPayload,
    ProductMismatch,
    MachineMismatch,
    Expired,
    TimeRollbackDetected,
    EnvironmentError
}
