namespace CodexWinBar.Core.Models;

/// <summary>Severity of a provider incident, port of upstream ProviderStatusIndicator.</summary>
public enum StatusIndicator
{
    None = 0,
    Minor,
    Major,
    Critical,
    Maintenance,
    Unknown,
}

/// <summary>Latest polled service status for a provider.</summary>
public sealed record ProviderStatus
{
    public required StatusIndicator Indicator { get; init; }
    public string? Description { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }

    /// <summary>Everything except <see cref="StatusIndicator.None"/> is user-visible.</summary>
    public bool IsIssue => this.Indicator != StatusIndicator.None;
}
