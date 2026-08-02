using System.Text.Json.Serialization;
using CodexWinBar.Core.Providers;

namespace CodexWinBar.Core.Models;

/// <summary>How trustworthy the numbers in a snapshot are.</summary>
public enum DataConfidence
{
    Unknown = 0,
    Exact,
    Estimated,
    PercentOnly,
}

/// <summary>Who is signed in for a provider, scoped per provider by design.</summary>
public sealed record ProviderIdentity
{
    public string? AccountEmail { get; init; }
    public string? AccountOrganization { get; init; }
    /// <summary>Human plan label, e.g. "Max", "Pro", "Plus".</summary>
    public string? Plan { get; init; }
    /// <summary>How the data was obtained, e.g. "OAuth", "API key", "Device flow".</summary>
    public string? LoginMethod { get; init; }
}

/// <summary>Whether a credit value is a remaining balance or an amount already spent.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CreditsSnapshotKind>))]
public enum CreditsSnapshotKind
{
    /// <summary>A balance that can be depleted. This is the legacy/default meaning.</summary>
    Credits = 0,
    /// <summary>An amount already spent, which must not be treated as a remaining balance.</summary>
    Spend,
}

/// <summary>Credit balance information for providers that expose one.</summary>
public sealed record CreditsSnapshot
{
    public required double Remaining { get; init; }
    /// <summary>Optional total grant/limit that <see cref="Remaining"/> is measured against.</summary>
    public double? Limit { get; init; }
    /// <summary>Display unit, e.g. "credits" or "USD".</summary>
    public string Unit { get; init; } = "credits";
    public DateTimeOffset UpdatedAt { get; init; }
    /// <summary>
    /// Whether <see cref="Remaining"/> contains remaining credits or spend. Missing serialized values retain
    /// the legacy meaning of <see cref="CreditsSnapshotKind.Credits"/>.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public CreditsSnapshotKind Kind { get; init; } = CreditsSnapshotKind.Credits;
    /// <summary>Whether depletion and remaining-percentage semantics are valid for this snapshot.</summary>
    [JsonIgnore]
    public bool SupportsDepletionSemantics => this.Kind == CreditsSnapshotKind.Credits;
}

/// <summary>The normalized usage envelope handed to the UI — the Windows port of upstream UsageSnapshot.</summary>
public sealed record UsageSnapshot
{
    public required ProviderId Provider { get; init; }
    /// <summary>Session window (typically 5h).</summary>
    public RateWindow? Primary { get; init; }
    /// <summary>Weekly window.</summary>
    public RateWindow? Secondary { get; init; }
    /// <summary>Model-specific weekly window (e.g. Opus) when reported.</summary>
    public RateWindow? Tertiary { get; init; }
    public IReadOnlyList<NamedRateWindow> ExtraWindows { get; init; } = [];
    public CreditsSnapshot? Credits { get; init; }
    public ProviderIdentity? Identity { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public DataConfidence Confidence { get; init; } = DataConfidence.Unknown;
}
