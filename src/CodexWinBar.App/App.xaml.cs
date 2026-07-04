using System.Windows;

namespace CodexWinBar.App;

/// <summary>WPF application object for the CodexWinBar shell.</summary>
public sealed class App : Application
{
    private AppShell? shell;

    internal void InitializeShell(AppShell appShell)
    {
        this.shell = appShell;
        this.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        this.Exit += (_, _) => this.shell?.Dispose();
        this.shell.Start();
    }
}
