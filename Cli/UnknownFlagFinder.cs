using System.Collections.Generic;
using System.Linq;

namespace ForexTradingBot.Cli;

/// <summary>
/// Spectre.Console silently discards flags that a command's settings class does not
/// declare, so <c>forexbot status --producton</c> runs as if nothing had been asked.
/// For a CLI that manages secrets, silently ignoring a mistyped flag is dangerous:
/// the user believes a setting was applied when it was not. This helper re-checks the
/// raw command line against the flags every command actually supports and rejects
/// anything unrecognized before <see cref="CommandApp"/> gets a chance to drop it.
/// </summary>
internal static class UnknownFlagFinder
{
    /// <summary>
    /// Walks the raw argument list, tracks which flags belong to the command under
    /// inspection (accounting for branches such as <c>secrets</c>), and returns the
    /// first flag that no command in the path recognizes. Returns <c>null</c> when
    /// everything checks out.
    /// </summary>
    public static string? Find(IReadOnlyDictionary<string, CommandSpec> commands, IReadOnlyList<string> args)
    {
        var index = 0;

        // Skip leading global flags (--help/--version belong to the app, not a command).
        while (index < args.Count && IsFlag(args[index]))
        {
            var name = FlagName(args[index]);
            if (name != "help" && name != "version")
            {
                return args[index];
            }

            index++;
        }

        if (index >= args.Count)
        {
            return null;
        }

        // The first positional token is the command name; subcommands follow for branches.
        var current = args[index];
        index++;

        if (!commands.TryGetValue(current, out var spec))
        {
            // Unknown commands are Spectre's job to report; do not interfere.
            return null;
        }

        // Descend into branches: "secrets get KEY --flag" means spec is first "secrets".
        while (index < args.Count && !IsFlag(args[index]) && spec.Subcommands.Count > 0)
        {
            var next = args[index];
            if (!spec.Subcommands.TryGetValue(next, out var child))
            {
                break;
            }

            spec = child;
            index++;
        }

        for (; index < args.Count; index++)
        {
            var arg = args[index];
            if (!IsFlag(arg))
            {
                // Positional argument: consumed by the command's declared arguments.
                continue;
            }

            var name = FlagName(arg);
            if (name.Length == 0)
            {
                // "--" alone is the stop-parsing sentinel.
                continue;
            }

            if (!spec.Flags.Contains(name) && name != "help" && name != "version")
            {
                return arg;
            }
        }

        return null;
    }

    private static bool IsFlag(string arg) => arg.StartsWith('-') && arg.Length > 1 && arg != "--";

    private static string FlagName(string arg)
    {
        var trimmed = arg.TrimStart('-');
        var eq = trimmed.IndexOf('=');
        return eq < 0 ? trimmed : trimmed[..eq];
    }
}

/// <summary>Static description of one CLI command: its flags and its subcommands.</summary>
internal sealed record CommandSpec(IReadOnlySet<string> Flags, IReadOnlyDictionary<string, CommandSpec> Subcommands);
