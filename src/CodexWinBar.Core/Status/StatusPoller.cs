using System.Text.Json;
using CodexWinBar.Core.Http;
using CodexWinBar.Core.Models;
using CodexWinBar.Core.Providers;

namespace CodexWinBar.Core.Status;

internal sealed class StatusPoller(Action<string> log)
{
    private static readonly TimeSpan PollTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MinimumPollInterval = TimeSpan.FromMinutes(5);
    private static readonly Uri GoogleWorkspaceIncidentsUrl = new("https://www.google.com/appsstatus/dashboard/incidents.json");

    private readonly Dictionary<ProviderId, DateTimeOffset> lastPolls = [];
    private readonly Dictionary<ProviderId, ProviderStatus> previous = [];

    public async Task<ProviderStatus?> PollAsync(
        ProviderDescriptor descriptor,
        ProviderStatus? current,
        DateTimeOffset now,
        CancellationToken ct)
    {
        if (descriptor.Metadata.StatusPageUrl is null && string.IsNullOrWhiteSpace(descriptor.Metadata.StatusWorkspaceProductId))
        {
            return current;
        }

        if (this.lastPolls.TryGetValue(descriptor.Id, out var lastPoll) && now - lastPoll < MinimumPollInterval)
        {
            return current;
        }

        this.lastPolls[descriptor.Id] = now;
        if (current is not null)
        {
            this.previous[descriptor.Id] = current;
        }

        try
        {
            var status = descriptor.Metadata.StatusPageUrl is not null
                ? await PollStatusPageAsync(descriptor.Metadata.StatusPageUrl, ct).ConfigureAwait(false)
                : await PollGoogleWorkspaceAsync(descriptor.Metadata.StatusWorkspaceProductId!, ct).ConfigureAwait(false);
            this.previous[descriptor.Id] = status;
            return status;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException or OperationCanceledException)
        {
            log($"Status poll failed for {descriptor.Id}; keeping previous status. {ex.GetType().Name}: {ex.Message}");
            return current ?? (this.previous.TryGetValue(descriptor.Id, out var kept) ? kept : null);
        }
    }

    private static async Task<ProviderStatus> PollStatusPageAsync(Uri baseUrl, CancellationToken ct)
    {
        using var timeout = ProviderHttpClient.TimeoutCts(ct, PollTimeout);
        var endpoint = new Uri(baseUrl, "api/v2/status.json");
        using var response = await ProviderHttpClient.Shared.GetAsync(endpoint, timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token).ConfigureAwait(false);

        var root = document.RootElement;
        var statusElement = root.TryGetProperty("status", out var status) ? status : default;
        var indicator = ReadString(statusElement, "indicator");
        var description = ReadString(statusElement, "description");
        var updatedAt = root.TryGetProperty("page", out var page)
            ? ParseDateTimeOffset(ReadString(page, "updated_at"))
            : null;

        return new ProviderStatus
        {
            Indicator = MapStatusPageIndicator(indicator),
            Description = description,
            UpdatedAt = updatedAt,
        };
    }

    private static async Task<ProviderStatus> PollGoogleWorkspaceAsync(string productId, CancellationToken ct)
    {
        using var timeout = ProviderHttpClient.TimeoutCts(ct, PollTimeout);
        using var response = await ProviderHttpClient.Shared.GetAsync(GoogleWorkspaceIncidentsUrl, timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token).ConfigureAwait(false);

        GoogleIncident? best = null;
        foreach (var incident in EnumerateIncidents(document.RootElement))
        {
            if (!incident.ProductIds.Contains(productId, StringComparer.OrdinalIgnoreCase) || !incident.IsActive)
            {
                continue;
            }

            if (best is null || Rank(incident.Indicator) > Rank(best.Indicator))
            {
                best = incident;
            }
        }

        return best is null
            ? new ProviderStatus { Indicator = StatusIndicator.None }
            : new ProviderStatus
            {
                Indicator = best.Indicator,
                Description = best.Description,
                UpdatedAt = best.UpdatedAt,
            };
    }

    private static IEnumerable<GoogleIncident> EnumerateIncidents(JsonElement root)
    {
        var incidents = root.ValueKind == JsonValueKind.Array
            ? root.EnumerateArray()
            : root.TryGetProperty("incidents", out var incidentsElement) && incidentsElement.ValueKind == JsonValueKind.Array
                ? incidentsElement.EnumerateArray()
                : [];

        foreach (var incident in incidents)
        {
            var productIds = ReadProductIds(incident);
            var status = ReadString(incident, "status");
            var severity = ReadString(incident, "severity");
            var indicator = MapWorkspaceIndicator(status, severity);
            var description = ReadString(incident, "external_desc")
                ?? ReadString(incident, "description")
                ?? ReadLatestUpdateText(incident);
            var updatedAt = ParseDateTimeOffset(ReadString(incident, "modified"))
                ?? ParseDateTimeOffset(ReadString(incident, "begin"))
                ?? ReadLatestUpdateTime(incident);

            yield return new GoogleIncident(productIds, IsActiveWorkspaceIncident(incident, status), indicator, description, updatedAt);
        }
    }

    private static IReadOnlyList<string> ReadProductIds(JsonElement incident)
    {
        var values = new List<string>();
        foreach (var propertyName in new[] { "products", "affected_products", "product_ids" })
        {
            if (!incident.TryGetProperty(propertyName, out var element) || element.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var item in element.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    values.Add(item.GetString() ?? string.Empty);
                }
                else if (item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                {
                    values.Add(id.GetString() ?? string.Empty);
                }
            }
        }

        return values;
    }

    private static string? ReadLatestUpdateText(JsonElement incident)
    {
        if (!incident.TryGetProperty("updates", out var updates) || updates.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        return updates.EnumerateArray()
            .Select(update => ReadString(update, "text") ?? ReadString(update, "description"))
            .FirstOrDefault(text => !string.IsNullOrWhiteSpace(text));
    }

    private static DateTimeOffset? ReadLatestUpdateTime(JsonElement incident)
    {
        if (!incident.TryGetProperty("updates", out var updates) || updates.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        return updates.EnumerateArray()
            .Select(update => ParseDateTimeOffset(ReadString(update, "modified") ?? ReadString(update, "created")))
            .FirstOrDefault(value => value is not null);
    }

    private static bool IsActiveWorkspaceIncident(JsonElement incident, string? status)
    {
        if (incident.TryGetProperty("end", out var end) && end.ValueKind != JsonValueKind.Null)
        {
            return false;
        }

        return !string.Equals(status, "AVAILABLE", StringComparison.OrdinalIgnoreCase);
    }

    private static StatusIndicator MapStatusPageIndicator(string? indicator) => indicator?.ToLowerInvariant() switch
    {
        "none" => StatusIndicator.None,
        "operational" => StatusIndicator.None,
        "minor" => StatusIndicator.Minor,
        "major" => StatusIndicator.Major,
        "critical" => StatusIndicator.Critical,
        "maintenance" => StatusIndicator.Maintenance,
        _ => StatusIndicator.Unknown,
    };

    private static StatusIndicator MapWorkspaceIndicator(string? status, string? severity) => status?.ToUpperInvariant() switch
    {
        "AVAILABLE" => StatusIndicator.None,
        "SERVICE_INFORMATION" => StatusIndicator.Minor,
        "SERVICE_DISRUPTION" => StatusIndicator.Major,
        "SERVICE_OUTAGE" => StatusIndicator.Critical,
        "SERVICE_MAINTENANCE" => StatusIndicator.Maintenance,
        "SCHEDULED_MAINTENANCE" => StatusIndicator.Maintenance,
        _ => severity?.ToLowerInvariant() switch
        {
            "low" => StatusIndicator.Minor,
            "medium" => StatusIndicator.Major,
            "high" => StatusIndicator.Critical,
            _ => StatusIndicator.Minor,
        },
    };

    private static int Rank(StatusIndicator indicator) => indicator switch
    {
        StatusIndicator.Critical => 5,
        StatusIndicator.Maintenance => 4,
        StatusIndicator.Major => 3,
        StatusIndicator.Minor => 2,
        StatusIndicator.Unknown => 1,
        _ => 0,
    };

    private static string? ReadString(JsonElement element, string name)
    {
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : null;
    }

    private static DateTimeOffset? ParseDateTimeOffset(string? value)
    {
        return DateTimeOffset.TryParse(value, out var parsed) ? parsed.ToUniversalTime() : null;
    }

    private sealed record GoogleIncident(
        IReadOnlyList<string> ProductIds,
        bool IsActive,
        StatusIndicator Indicator,
        string? Description,
        DateTimeOffset? UpdatedAt);
}
