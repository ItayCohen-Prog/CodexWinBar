using CodexWinBar.Core.Providers;

namespace CodexWinBar.Core.Statistics;

/// <summary>How samples in one statistics series should be projected into calendar activity.</summary>
public enum PlanUsageMetricKind
{
    /// <summary>A cumulative 0-100 quota meter; activity is reconstructed from positive cycle deltas.</summary>
    QuotaPercent = 0,
    /// <summary>A provider-supplied activity value already assigned to its capture bucket.</summary>
    ActivityValue,
}

/// <summary>A locally observed point for one provider statistics series.</summary>
public sealed record PlanUsageSample
{
    public required DateTimeOffset CapturedAt { get; init; }
    /// <summary>
    /// Numeric sample value. The property name is retained for backward-compatible history files;
    /// for <see cref="PlanUsageMetricKind.ActivityValue"/> series it contains the raw activity value.
    /// </summary>
    public required double UsedPercent { get; init; }
    public DateTimeOffset? ResetsAt { get; init; }
}

/// <summary>Historical observations for one stable provider quota lane.</summary>
public sealed record PlanUsageSeries
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required int WindowMinutes { get; init; }
    public PlanUsageMetricKind MetricKind { get; init; } = PlanUsageMetricKind.QuotaPercent;
    public string? Unit { get; init; }
    /// <summary>Fixed calendar intensity ceiling for raw activity values.</summary>
    public double? ScaleMaximum { get; init; }
    public IReadOnlyList<PlanUsageSample> Samples { get; init; } = [];
}

/// <summary>All locally observed plan-utilization history for one provider.</summary>
public sealed record ProviderPlanStatistics
{
    public required ProviderId Provider { get; init; }
    public IReadOnlyList<PlanUsageSeries> Series { get; init; } = [];
}

/// <summary>Read/write facade for provider plan-utilization statistics.</summary>
public interface IPlanStatisticsStore : IDisposable
{
    /// <summary>Raised after an observed snapshot changes the available statistics.</summary>
    event Action? StateChanged;

    /// <summary>Returns a stable copy of one provider's statistics.</summary>
    ProviderPlanStatistics Get(ProviderId provider);

    /// <summary>Records the usable rate-limit lanes from a successful provider snapshot.</summary>
    void Record(Models.UsageSnapshot snapshot);
}

/// <summary>Versioned on-disk document used by the local plan-statistics history store.</summary>
public sealed record PlanStatisticsDocument
{
    public int Version { get; init; } = 1;
    public required ProviderId Provider { get; init; }
    public IReadOnlyList<PlanUsageSeries> Series { get; init; } = [];
}
