using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Controller.gRPC;

public enum Command
{
    Abort,
    Clear,
    Complete,
    DownloadRecipe,
    EStop,
    Extend,
    Hold,
    NextStep,
    Reset,
    ResetStatistics,
    Retract,
    SetMode,
    Start,
    Stop,
    Suspend,
    Unhold,
    Unsuspend
}

public static class CommandNames
{
    private static readonly Dictionary<string, Command> NameToEnum =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["CMDABORT"] = Command.Abort,
            ["CMDCLEAR"] = Command.Clear,
            ["CMDCOMPLETE"] = Command.Complete,
            ["CMDDOWNLOADRECIPE"] = Command.DownloadRecipe,
            ["CMDESTOP"] = Command.EStop,
            ["CMDEXTEND"] = Command.Extend,
            ["CMDHOLD"] = Command.Hold,
            ["CMDNEXTSTEP"] = Command.NextStep,
            ["CMDRESET"] = Command.Reset,
            ["CMDRESETSTATISTICS"] = Command.ResetStatistics,
            ["CMDRETRACT"] = Command.Retract,
            ["CMDSETMODE"] = Command.SetMode,
            ["CMDSTART"] = Command.Start,
            ["CMDSTOP"] = Command.Stop,
            ["CMDSUSPEND"] = Command.Suspend,
            ["CMDUNHOLD"] = Command.Unhold,
            ["CMDUNSUSPEND"] = Command.Unsuspend,
        };

    public static bool TryParse(string raw, out Command cmd)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            cmd = default;
            return false;
        }

        return NameToEnum.TryGetValue(raw.Trim(), out cmd);
    }
}

public enum CommandResultType
{
    Accepted,    // 指令已入队
    Rejected,    // 被拒绝（如：模式不对、联锁不满足）
}

public record CommandResult(CommandResultType Type, string Message);

public record InternalCommand(string TargetUnit, string TargetObject, Command CmdName, Dictionary<string, string> Params, string JsonPayload = "",
    TaskCompletionSource<CommandResult>? CallbackTcs = null,
    CancellationToken CancelToken = default);
