namespace CodexWinBar.App;

/// <summary>Process entry point. Real composition happens in <c>AppShell</c> (wave WC1).</summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Placeholder until the app shell wave lands: keeps the skeleton building and runnable.
        var app = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
        _ = args;
        app.Shutdown();
        return app.Run();
    }
}
