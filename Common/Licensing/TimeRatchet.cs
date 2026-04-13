using Microsoft.Win32;
using System;
using System.Security.Cryptography;
using System.Text;

namespace Common.Licensing;

public static class TimeRatchet
{
    private const string RegPath = @"SOFTWARE\LeadUI";
    private const string TimeValueName = "LastSeenUtcTicks";
    private const string SigValueName = "LastSeenUtcTicksSig";

    // 换成你自己的随机字符串
    private const string LocalSecret = "LeadUI_TimeRatchet_v1_Replace_With_Your_Own_Long_Random_String";
    private static readonly TimeSpan RollbackTolerance = TimeSpan.FromHours(24);

    public static bool IsTimeRollbackDetected(DateTime issuedAtUtc, out string message)
    {
        message = string.Empty;
        DateTime nowUtc = DateTime.UtcNow;

        try
        {
            if (nowUtc < issuedAtUtc - RollbackTolerance)
            {
                message =
                    $"系统时间异常。当前时间早于授权签发时间过多。\n" +
                    $"签发时间(UTC): {issuedAtUtc:yyyy-MM-dd HH:mm:ss}\n" +
                    $"当前时间(UTC): {nowUtc:yyyy-MM-dd HH:mm:ss}";
                return true;
            }

            long lastTicks = 0;
            string lastSig = string.Empty;

            using (var key = Registry.CurrentUser.OpenSubKey(RegPath))
            {
                if (key != null)
                {
                    var tickObj = key.GetValue(TimeValueName);
                    var sigObj = key.GetValue(SigValueName);

                    if (tickObj != null)
                        lastTicks = Convert.ToInt64(tickObj);

                    if (sigObj != null)
                        lastSig = sigObj.ToString() ?? string.Empty;
                }
            }

            if (lastTicks > 0)
            {
                string expectedSig = Protect(lastTicks);

                if (!string.Equals(expectedSig, lastSig, StringComparison.Ordinal))
                {
                    message = "本地授权时间记录已损坏或被修改。";
                    return true;
                }

                DateTime lastSeenUtc = new(lastTicks, DateTimeKind.Utc);

                if (nowUtc < lastSeenUtc - RollbackTolerance)
                {
                    message =
                        $"检测到系统时钟明显回退。\n" +
                        $"上次运行时间(UTC): {lastSeenUtc:yyyy-MM-dd HH:mm:ss}\n" +
                        $"当前系统时间(UTC): {nowUtc:yyyy-MM-dd HH:mm:ss}";
                    return true;
                }
            }

            if (lastTicks == 0 || nowUtc.Ticks > lastTicks)
            {
                SaveNow(nowUtc.Ticks);
            }

            return false;
        }
        catch (Exception ex)
        {
            message = $"本地时间安全检查失败: {ex.Message}";
            return true;
        }
    }

    private static void SaveNow(long utcTicks)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RegPath);
        key.SetValue(TimeValueName, utcTicks, RegistryValueKind.QWord);
        key.SetValue(SigValueName, Protect(utcTicks), RegistryValueKind.String);
    }

    private static string Protect(long value)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(LocalSecret));
        byte[] bytes = Encoding.UTF8.GetBytes(value.ToString());
        return Convert.ToBase64String(hmac.ComputeHash(bytes));
    }
}
