using System.Collections.Generic;

namespace ForexTradingBot.Cli;

/// <summary>
/// The set of commands and flags the CLI actually understands. Keep this in sync with
/// the <c>app.Configure</c> registration in <see cref="Program"/>: every flag declared
/// with <c>[CommandOption]</c> on a settings class must appear here, otherwise the
/// unknown-flag guard in <see cref="Program"/> starts rejecting valid input.
/// </summary>
internal static class CommandRegistry
{
    private static readonly IReadOnlySet<string> Empty = new HashSet<string>();
    private static readonly IReadOnlyDictionary<string, CommandSpec> NoChildren = new Dictionary<string, CommandSpec>();

    // Options declared with [CommandOption] across the command settings classes.
    private const string Build = "build";
    private const string Category = "category";
    private const string Connection = "connection";
    private const string Copy = "copy";
    private const string Description = "description";
    private const string Native = "native";
    private const string ShowValues = "show-values";
    private const string Volumes = "volumes";
    private const string Yes = "yes";

    public static IReadOnlyDictionary<string, CommandSpec> Commands { get; } = new Dictionary<string, CommandSpec>
    {
        // install: "[MODE]" positional plus its own flags.
        ["install"] = new(new HashSet<string> { Build }, NoChildren),

        ["start"] = new(new HashSet<string> { Native }, NoChildren),
        ["stop"] = new(new HashSet<string> { Volumes }, NoChildren),
        ["status"] = new(Empty, NoChildren),
        ["doctor"] = new(Empty, NoChildren),
        ["logs"] = new(Empty, NoChildren),
        ["migrate"] = new(new HashSet<string> { Connection }, NoChildren),
        ["migrations"] = new(Empty, NoChildren),
        ["backup"] = new(new HashSet<string>(), NoChildren),
        ["restore"] = new(Empty, NoChildren),
        ["config"] = new(Empty, NoChildren),

        ["secrets"] = new(Empty, new Dictionary<string, CommandSpec>
        {
            ["list"] = new(new HashSet<string> { ShowValues }, NoChildren),
            ["get"] = new(new HashSet<string> { Copy }, NoChildren),
            ["set"] = new(new HashSet<string> { Category, Description }, NoChildren),
            ["delete"] = new(new HashSet<string> { Yes }, NoChildren),
            ["rotate"] = new(Empty, NoChildren),
        }),
    };
}
