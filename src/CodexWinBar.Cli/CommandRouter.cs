namespace CodexWinBar.Cli;

using System.Reflection;
using CodexWinBar.Cli.Commands;

/// <summary>Top-level command dispatch for the headless <c>codexbar</c> CLI.</summary>
internal static class CommandRouter
{
    /// <summary>Routes command-line arguments to the requested command.</summary>
    /// <param name="args">Command-line arguments excluding the executable name.</param>
    /// <returns>The process exit code.</returns>
    public static Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h")
        {
            HelpPrinter.PrintRootHelp(Console.Out);
            return Task.FromResult(0);
        }

        if (args[0] == "--version")
        {
            Console.WriteLine(typeof(CommandRouter).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? typeof(CommandRouter).Assembly.GetName().Version?.ToString()
                ?? "unknown");
            return Task.FromResult(0);
        }

        var tail = args[1..];
        return args[0] switch
        {
            "usage" => UsageCommand.RunAsync(tail),
            "config" => ConfigCommand.RunAsync(tail),
            "serve" => ServeCommand.RunAsync(tail),
            "diagnose" => DiagnoseCommand.RunAsync(tail),
            _ => UnknownCommandAsync(args[0]),
        };
    }

    private static Task<int> UnknownCommandAsync(string command)
    {
        Console.Error.WriteLine($"error: unknown command '{command}'");
        HelpPrinter.PrintRootHelp(Console.Error);
        return Task.FromResult(2);
    }
}
