using System.Text.Json;
using CodexWinBar.Core.Json;
using CodexWinBar.Core.Models;
using CodexWinBar.Core.Providers;

namespace CodexWinBar.Core.Statistics;

/// <summary>
/// Persists a bounded, hourly-coalesced history of provider rate-limit observations. The store records
/// percentages and reset boundaries only; it never reads or writes prompts, completions, or credentials.
/// </summary>
public sealed class PlanStatisticsStore : IPlanStatisticsStore
{
    private const int SchemaVersion = 1;
    private const int MaxSamplesPerSeries = 24 * 730;
    private static readonly TimeSpan ResetEquivalenceTolerance = TimeSpan.FromMinutes(2);
    private readonly object sync = new();
    private readonly object saveSync = new();
    private readonly string directory;
    private readonly Action<string> log;
    private readonly Dictionary<ProviderId, ProviderPlanStatistics> statistics = [];
    private bool disposed;

    /// <summary>Creates a history store rooted in local application data unless overridden for tests.</summary>
    public PlanStatisticsStore(Action<string> log, string? baseDirectory = null)
    {
        this.log = log;
        var root = baseDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        this.directory = Path.Combine(root, "CodexWinBar", "history");
        this.LoadExisting();
    }

    /// <inheritdoc />
    public event Action? StateChanged;

    /// <inheritdoc />
    public ProviderPlanStatistics Get(ProviderId provider)
    {
        lock (this.sync)
        {
            return this.statistics.TryGetValue(provider, out var value)
                ? Clone(value)
                : new ProviderPlanStatistics { Provider = provider };
        }
    }

    /// <inheritdoc />
    public void Record(UsageSnapshot snapshot)
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);
        var samples = EnumerateSeries(snapshot).ToArray();
        if (samples.Length == 0)
        {
            return;
        }

        ProviderPlanStatistics? changed = null;
        lock (this.sync)
        {
            var current = this.statistics.TryGetValue(snapshot.Provider, out var existing)
                ? existing
                : new ProviderPlanStatistics { Provider = snapshot.Provider };
            var seriesById = current.Series.ToDictionary(item => item.Id, StringComparer.Ordinal);
            var didChange = false;

            foreach (var incoming in samples)
            {
                if (seriesById.TryGetValue(incoming.Series.Id, out var prior))
                {
                    var updatedSamples = AddSample(prior.Samples, incoming.Sample);
                    if (updatedSamples is null)
                    {
                        continue;
                    }

                    seriesById[incoming.Series.Id] = prior with
                    {
                        Title = incoming.Series.Title,
                        WindowMinutes = incoming.Series.WindowMinutes,
                        Samples = updatedSamples,
                    };
                }
                else
                {
                    seriesById[incoming.Series.Id] = incoming.Series with { Samples = [incoming.Sample] };
                }

                didChange = true;
            }

            if (didChange)
            {
                changed = new ProviderPlanStatistics
                {
                    Provider = snapshot.Provider,
                    Series = seriesById.Values
                        .OrderBy(item => SeriesOrder(item.Id))
                        .ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
                        .ToArray(),
                };
                this.statistics[snapshot.Provider] = changed;
            }
        }

        if (changed is null)
        {
            return;
        }

        this.PersistLatest(changed.Provider);
        this.RaiseStateChanged();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        this.disposed = true;
    }

    private static ProviderPlanStatistics Clone(ProviderPlanStatistics value) => value with
    {
        Series = value.Series.Select(series => series with { Samples = series.Samples.ToArray() }).ToArray(),
    };

    private static IEnumerable<IncomingSeries> EnumerateSeries(UsageSnapshot snapshot)
    {
        foreach (var item in CandidateWindows(snapshot))
        {
            var window = item.Window;
            if (window.IsSyntheticPlaceholder || window.WindowMinutes is not > 0 || window.ResetsAt is null ||
                double.IsNaN(window.UsedPercent) || double.IsInfinity(window.UsedPercent))
            {
                continue;
            }

            var sample = new PlanUsageSample
            {
                CapturedAt = snapshot.UpdatedAt.ToUniversalTime(),
                UsedPercent = Math.Clamp(window.UsedPercent, 0, 100),
                ResetsAt = window.ResetsAt?.ToUniversalTime(),
            };
            yield return new IncomingSeries(
                new PlanUsageSeries
                {
                    Id = item.Id,
                    Title = item.Title,
                    WindowMinutes = CanonicalWindowMinutes(window.WindowMinutes.Value),
                },
                sample);
        }
    }

    private static IEnumerable<(string Id, string Title, RateWindow Window)> CandidateWindows(UsageSnapshot snapshot)
    {
        if (snapshot.Primary is { } primary)
        {
            yield return ("session", "Session", primary);
        }

        if (snapshot.Secondary is { } secondary)
        {
            yield return ("weekly", "Weekly", secondary);
        }

        if (snapshot.Tertiary is { } tertiary)
        {
            yield return ("tertiary", snapshot.Provider == ProviderId.Claude ? "Opus weekly" : "Model limit", tertiary);
        }

        foreach (var extra in snapshot.ExtraWindows.Where(item => item.UsageKnown))
        {
            yield return ($"extra:{extra.Id}", extra.Title, extra.Window);
        }
    }

    private static int CanonicalWindowMinutes(int value) => value switch
    {
        >= 295 and <= 305 => 300,
        >= 10070 and <= 10090 => 10080,
        _ => value,
    };

    private static IReadOnlyList<PlanUsageSample>? AddSample(
        IReadOnlyList<PlanUsageSample> existing,
        PlanUsageSample incoming)
    {
        var entries = existing.OrderBy(item => item.CapturedAt).ToList();
        var incomingHour = HourBucket(incoming.CapturedAt);
        var sameHour = entries
            .Select((sample, index) => (sample, index))
            .Where(item => HourBucket(item.sample.CapturedAt) == incomingHour)
            .ToArray();

        var equivalent = sameHour.FirstOrDefault(item => EquivalentReset(item.sample.ResetsAt, incoming.ResetsAt));
        if (equivalent.sample is not null)
        {
            var preferred = incoming.UsedPercent > equivalent.sample.UsedPercent ||
                (Math.Abs(incoming.UsedPercent - equivalent.sample.UsedPercent) < 0.001 &&
                    incoming.CapturedAt > equivalent.sample.CapturedAt)
                ? incoming
                : equivalent.sample;
            if (preferred == equivalent.sample)
            {
                return null;
            }

            entries[equivalent.index] = preferred;
        }
        else
        {
            entries.Add(incoming);
        }

        entries.Sort((left, right) => left.CapturedAt.CompareTo(right.CapturedAt));
        if (entries.Count > MaxSamplesPerSeries)
        {
            entries.RemoveRange(0, entries.Count - MaxSamplesPerSeries);
        }

        return entries;
    }

    private static long HourBucket(DateTimeOffset value) => value.ToUnixTimeSeconds() / 3600;

    private static bool EquivalentReset(DateTimeOffset? left, DateTimeOffset? right) => (left, right) switch
    {
        (null, null) => true,
        ({ } lhs, { } rhs) => Math.Abs((lhs - rhs).TotalSeconds) < ResetEquivalenceTolerance.TotalSeconds,
        _ => false,
    };

    private static int SeriesOrder(string id) => id switch
    {
        "session" => 0,
        "weekly" => 1,
        "tertiary" => 2,
        _ => 3,
    };

    private void LoadExisting()
    {
        foreach (var provider in ProviderIds.All)
        {
            var path = this.PathFor(provider);
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                using var stream = File.OpenRead(path);
                var document = JsonSerializer.Deserialize(stream, CoreJsonContext.Default.PlanStatisticsDocument);
                if (document is null || document.Version != SchemaVersion || document.Provider != provider)
                {
                    this.log($"Ignoring unsupported statistics history for {provider.ConfigId()}.");
                    continue;
                }

                this.statistics[provider] = new ProviderPlanStatistics
                {
                    Provider = provider,
                    Series = Sanitize(document.Series),
                };
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                this.log($"Failed to load statistics history for {provider.ConfigId()}. {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private static IReadOnlyList<PlanUsageSeries> Sanitize(IReadOnlyList<PlanUsageSeries>? series) =>
        (series ?? [])
            .Where(item => !string.IsNullOrWhiteSpace(item.Id) && item.WindowMinutes > 0)
            .Select(item => item with
            {
                Samples = (item.Samples ?? [])
                    .Where(sample => sample.UsedPercent is >= 0 and <= 100)
                    .OrderBy(sample => sample.CapturedAt)
                    .TakeLast(MaxSamplesPerSeries)
                    .ToArray(),
            })
            .Where(item => item.Samples.Count > 0)
            .OrderBy(item => SeriesOrder(item.Id))
            .ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private void Save(ProviderPlanStatistics value)
    {
        try
        {
            Directory.CreateDirectory(this.directory);
            var path = this.PathFor(value.Provider);
            var tempPath = Path.Combine(this.directory, $".{value.Provider.ConfigId()}.{Guid.NewGuid():N}.tmp");
            try
            {
                var document = new PlanStatisticsDocument
                {
                    Version = SchemaVersion,
                    Provider = value.Provider,
                    Series = value.Series,
                };
                using (var stream = File.Create(tempPath))
                {
                    JsonSerializer.Serialize(stream, document, CoreJsonContext.Default.PlanStatisticsDocument);
                }

                File.Move(tempPath, path, true);
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            this.log($"Failed to save statistics history for {value.Provider.ConfigId()}. {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void PersistLatest(ProviderId provider)
    {
        lock (this.saveSync)
        {
            ProviderPlanStatistics value;
            lock (this.sync)
            {
                value = Clone(this.statistics[provider]);
            }

            this.Save(value);
        }
    }

    private string PathFor(ProviderId provider) => Path.Combine(this.directory, $"{provider.ConfigId()}.json");

    private void RaiseStateChanged()
    {
        var subscribers = this.StateChanged;
        if (subscribers is null)
        {
            return;
        }

        foreach (Action subscriber in subscribers.GetInvocationList())
        {
            try
            {
                subscriber();
            }
            catch (Exception ex)
            {
                this.log($"Statistics subscriber failed. {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private sealed record IncomingSeries(PlanUsageSeries Series, PlanUsageSample Sample);
}
