using System.Text;

namespace CodexWinBar.Cli;

/// <summary>Entry point for the headless <c>codexbar</c> CLI.</summary>
public static class Program
{
    /// <summary>Runs the CLI.</summary>
    /// <param name="args">Command-line arguments excluding the executable name.</param>
    /// <returns>The process exit code.</returns>
    public static Task<int> Main(string[] args)
    {
        // Ensure non-ASCII glyphs (e.g. the "·" separator) render instead of becoming "?".
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch (System.IO.IOException)
        {
            // Output is redirected to a handle that rejects encoding changes; ignore.
        }

        return CommandRouter.RunAsync(args);
    }
}
