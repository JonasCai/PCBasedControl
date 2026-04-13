using System;
using System.Collections.Generic;
using System.Management;

namespace Common.Licensing;

public static class DeviceFingerprint
{
    public static DeviceBinding Capture()
    {
        return new DeviceBinding
        {
            CpuId = TryGetWmiInfo("Win32_Processor", "ProcessorId"),
            BaseBoardSerial = TryGetWmiInfo("Win32_BaseBoard", "SerialNumber"),
            BiosSerial = TryGetWmiInfo("Win32_BIOS", "SerialNumber"),
            SystemUuid = TryGetWmiInfo("Win32_ComputerSystemProduct", "UUID"),
            DiskSerial = TryGetWmiInfo("Win32_DiskDrive", "SerialNumber"),
            RequiredMatchCount = 3
        };
    }

    public static DeviceMatchResult Match(DeviceBinding licenseBinding, DeviceBinding currentBinding)
    {
        var pairs = new List<(string Name, string Expected, string Actual)>
        {
            ("CPU", licenseBinding.CpuId, currentBinding.CpuId),
            ("BOARD", licenseBinding.BaseBoardSerial, currentBinding.BaseBoardSerial),
            ("BIOS", licenseBinding.BiosSerial, currentBinding.BiosSerial),
            ("UUID", licenseBinding.SystemUuid, currentBinding.SystemUuid),
            ("DISK", licenseBinding.DiskSerial, currentBinding.DiskSerial),
        };

        int available = 0;
        int matched = 0;
        List<string> matchedFields = new();
        List<string> mismatchedFields = new();

        foreach (var p in pairs)
        {
            string expected = Normalize(p.Expected);
            string actual = Normalize(p.Actual);

            if (string.IsNullOrWhiteSpace(expected))
                continue;

            available++;

            if (!string.IsNullOrWhiteSpace(actual) &&
                string.Equals(expected, actual, StringComparison.Ordinal))
            {
                matched++;
                matchedFields.Add(p.Name);
            }
            else
            {
                mismatchedFields.Add(p.Name);
            }
        }

        int required = Math.Max(1, Math.Min(licenseBinding.RequiredMatchCount, available));

        return new DeviceMatchResult
        {
            IsMatch = matched >= required,
            MatchedCount = matched,
            AvailableCount = available,
            RequiredCount = required,
            MatchedFields = matchedFields.ToArray(),
            MismatchedFields = mismatchedFields.ToArray()
        };
    }

    private static string TryGetWmiInfo(string table, string prop)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher($"SELECT {prop} FROM {table}");
            foreach (var obj in searcher.Get())
            {
                var raw = obj[prop]?.ToString();
                string value = Normalize(raw);
                if (IsUsable(value))
                    return value;
            }
        }
        catch
        {
        }

        return string.Empty;
    }

    private static bool IsUsable(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        string v = value.ToUpperInvariant();

        if (v.Contains("TO BE FILLED BY O.E.M."))
            return false;
        if (v == "DEFAULT STRING")
            return false;
        if (v == "SYSTEM SERIAL NUMBER")
            return false;
        if (v == "NONE")
            return false;
        if (v == "UNKNOWN")
            return false;

        return true;
    }

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        return value.Trim().ToUpperInvariant();
    }
}

public sealed class DeviceMatchResult
{
    public bool IsMatch { get; init; }
    public int MatchedCount { get; init; }
    public int AvailableCount { get; init; }
    public int RequiredCount { get; init; }
    public string[] MatchedFields { get; init; } = Array.Empty<string>();
    public string[] MismatchedFields { get; init; } = Array.Empty<string>();
}
