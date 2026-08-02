using System.IO;
using CodexWinBar.App.Flyout;
using CodexWinBar.Core.Models;
using Xunit;

namespace CodexWinBar.App.Tests;

public sealed class AppHardeningTests
{
    [Theory]
    [InlineData(typeof(OutOfMemoryException))]
    [InlineData(typeof(StackOverflowException))]
    [InlineData(typeof(AccessViolationException))]
    public void ExceptionPolicy_LeavesFatalExceptionsUnhandled(Type exceptionType)
    {
        var exception = (Exception)Activator.CreateInstance(exceptionType)!;

        Assert.True(AppExceptionPolicy.IsFatal(exception));
    }

    [Fact]
    public void ExceptionPolicy_AllowsNonfatalExceptionsToBeHandled()
    {
        Assert.False(AppExceptionPolicy.IsFatal(new InvalidOperationException("test")));
    }

    [Fact]
    public void ExceptionPolicy_LeavesNestedFatalExceptionUnhandled()
    {
        var exception = new InvalidOperationException(
            "dispatcher wrapper",
            new InvalidOperationException("operation wrapper", new AccessViolationException("fatal")));

        Assert.True(AppExceptionPolicy.IsFatal(exception));
    }

    [Fact]
    public void RollingLog_DoesNotThrowWhenLogDirectoryCannotBeCreated()
    {
        var path = Path.Combine(Path.GetTempPath(), $"codexwinbar-log-file-{Guid.NewGuid():N}");
        File.WriteAllText(path, "not a directory");
        try
        {
            var exception = Record.Exception(() =>
            {
                using var log = new RollingLog(path);
                log.Write("test");
            });

            Assert.Null(exception);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RollingLog_DoesNotThrowWhenLogPathBecomesUnwritable()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"codexwinbar-log-dir-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            using var log = new RollingLog(directory);
            Directory.CreateDirectory(Path.Combine(directory, "app.log"));

            var exception = Record.Exception(() => log.Write("test"));

            Assert.Null(exception);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void CreditDisplay_UsesExplicitSpendPresentationWithoutBalanceLanguage()
    {
        var presentation = CreditDisplay.For(new CreditsSnapshot
        {
            Remaining = 143.2,
            Unit = "USD",
            Kind = CreditsSnapshotKind.Spend,
        });

        Assert.Equal("Spend (30d)", presentation.Label);
        Assert.Equal("143.2 USD spent", presentation.Value);
        Assert.DoesNotContain("Credit", presentation.Label, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Remaining", presentation.Value, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CreditDisplay_PreservesCreditBalancePresentation()
    {
        var presentation = CreditDisplay.For(new CreditsSnapshot
        {
            Remaining = 12.5,
            Limit = 20,
            Unit = "credits",
        });

        Assert.Equal("Credits", presentation.Label);
        Assert.Equal("12.5 of 20 credits", presentation.Value);
    }
}
