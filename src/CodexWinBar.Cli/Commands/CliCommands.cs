using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using CodexWinBar.Core.Config;
using CodexWinBar.Core.Http;
using CodexWinBar.Core.Models;
using CodexWinBar.Core.Providers;
using CodexWinBar.Providers;
using static CodexWinBar.Cli.Commands.CliSupport;

namespace CodexWinBar.Cli.Commands;

internal static class UsageCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (ArgReader.HasHelp(args))
        {
            HelpPrinter.PrintUsageHelp(Console.Out);
            return 0;
        }

        var parsed = ArgReader.Parse(args);
        var json = parsed.Has("json") || string.Equals(parsed.Value("format"), "json", StringComparison.OrdinalIgnoreCase);
        var pretty = parsed.Has("pretty");
        var verbose = parsed.Has("verbose");
        var providerValue = parsed.Value("provider");
        var all = parsed.Has("all");
        if (providerValue is not null && all)
        {
            return Fail("--provider and --all cannot be used together");
        }

        using var cts = ConsoleLifetime.CreateCancellationSource();
        var runtime = CliRuntime.Create(verbose);
        var selection = SelectDescriptors(runtime, providerValue, all);
        if (selection.Error is { } error)
        {
            return Fail(error);
        }

        var now = DateTimeOffset.UtcNow;
        var results = await Task.WhenAll(selection.Value.Select(descriptor => runtime.FetchAsync(descriptor, cts.Token))).ConfigureAwait(false);
        if (json)
        {
            JsonWriter.Write(Console.Out, UsageMapper.ToJson(now, results), CliJsonContext.Default.UsageJsonOutput, pretty);
        }
        else
        {
            TextUsageWriter.Write(Console.Out, now, results);
        }

        return providerValue is not null && results.Any(result => !result.Outcome.Succeeded) ? 1 : 0;
    }

    private static Selection SelectDescriptors(CliRuntime runtime, string? providerValue, bool all)
    {
        if (providerValue is not null)
        {
            var provider = ProviderIds.TryParse(providerValue);
            if (provider is null)
            {
                return Selection.Failure($"unknown provider '{providerValue}'");
            }

            var descriptor = runtime.DescriptorFor(provider.Value);
            return descriptor is null
                ? Selection.Failure($"provider '{providerValue}' is not in the catalog")
                : Selection.Success([descriptor]);
        }

        var items = all ? runtime.Descriptors : runtime.Descriptors.Where(runtime.IsEnabled).ToArray();
        return Selection.Success(items);
    }

    private sealed record Selection(IReadOnlyList<ProviderDescriptor> Value, string? Error)
    {
        public static Selection Success(IReadOnlyList<ProviderDescriptor> value) => new(value, null);
        public static Selection Failure(string error) => new([], error);
    }
}

internal static class ConfigCommand
{
    private static readonly HashSet<ProviderId> ApiKeyProviders =
    [
        ProviderId.OpenRouter,
        ProviderId.OpenAIAdmin,
        ProviderId.Zai,
        ProviderId.Copilot,
    ];

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || ArgReader.HasHelp(args))
        {
            HelpPrinter.PrintConfigHelp(Console.Out);
            return args.Length == 0 ? 2 : 0;
        }

        var sub = args[0];
        var parsed = ArgReader.Parse(args[1..]);
        var runtime = CliRuntime.Create(verbose: false);
        return sub switch
        {
            "providers" => Providers(runtime, parsed),
            "enable" => SetEnabled(runtime, parsed, enabled: true),
            "disable" => SetEnabled(runtime, parsed, enabled: false),
            "set-api-key" => await SetApiKeyAsync(runtime, parsed).ConfigureAwait(false),
            "print" => Print(runtime, parsed),
            "path" => Path(runtime),
            "validate" => Validate(runtime),
            _ => Fail($"unknown config subcommand '{sub}'"),
        };
    }

    private static int Providers(CliRuntime runtime, ParsedArgs args)
    {
        var rows = runtime.Descriptors.Select(descriptor =>
        {
            var entry = runtime.Store.EntryFor(runtime.Config, descriptor.Id);
            return new ConfigProviderJson(
                descriptor.Id.ConfigId(),
                descriptor.Metadata.DisplayName,
                runtime.IsEnabled(descriptor),
                !string.IsNullOrWhiteSpace(entry.ApiKey) || !string.IsNullOrWhiteSpace(entry.Source),
                AuthHint(descriptor, entry));
        }).ToArray();

        if (args.Has("json"))
        {
            JsonWriter.Write(Console.Out, new ConfigProvidersJsonOutput(rows), CliJsonContext.Default.ConfigProvidersJsonOutput, args.Has("pretty"));
            return 0;
        }

        foreach (var row in rows)
        {
            Console.WriteLine($"{row.Id,-12} {row.DisplayName,-16} enabled: {(row.Enabled ? "yes" : "no"),-3} configured: {(row.Configured ? "yes" : "no"),-3} auth: {row.AuthHint}");
        }

        return 0;
    }

    private static int SetEnabled(CliRuntime runtime, ParsedArgs args, bool enabled)
    {
        var target = RequiredProvider(args);
        if (target.Error is { } error)
        {
            return Fail(error);
        }

        var entry = runtime.Store.EntryFor(runtime.Config, target.Value) with { Enabled = enabled };
        runtime.Store.Save(runtime.Store.WithEntry(runtime.Config, entry));
        Console.WriteLine($"{target.Value.ConfigId()} {(enabled ? "enabled" : "disabled")} in {runtime.Store.ResolvePath()}");
        return 0;
    }

    private static async Task<int> SetApiKeyAsync(CliRuntime runtime, ParsedArgs args)
    {
        var target = RequiredProvider(args);
        if (target.Error is { } error)
        {
            return Fail(error);
        }

        if (!ApiKeyProviders.Contains(target.Value))
        {
            return Fail($"provider '{target.Value.ConfigId()}' does not support storing API keys");
        }

        var inline = args.Value("api-key");
        var useStdin = args.Has("stdin") || inline is null;
        if (args.Has("stdin") && inline is not null)
        {
            return Fail("--stdin and --api-key cannot be used together");
        }

        var key = useStdin ? (await Console.In.ReadToEndAsync().ConfigureAwait(false)).TrimEnd('\r', '\n') : inline!;
        if (string.IsNullOrWhiteSpace(key))
        {
            return Fail("API key is empty");
        }

        var current = runtime.Store.EntryFor(runtime.Config, target.Value);
        var updated = current with
        {
            ApiKey = key.Trim(),
            Source = "api",
            Enabled = args.Has("no-enable") ? current.Enabled : true,
        };
        runtime.Store.Save(runtime.Store.WithEntry(runtime.Config, updated));
        Console.WriteLine($"{target.Value.ConfigId()} API key saved ({Mask(key)}) in {runtime.Store.ResolvePath()}");
        return 0;
    }

    private static int Print(CliRuntime runtime, ParsedArgs args)
    {
        var showSecrets = args.Has("show-secrets") || args.Has("raw");
        var config = showSecrets ? runtime.Config : RedactSecrets(runtime.Config);
        JsonWriter.Write(Console.Out, config, CliJsonContext.Default.CodexBarConfig, pretty: true);
        return 0;
    }

    /// <summary>
    /// Returns a copy of the config with secret-bearing fields masked for display.
    /// The config file itself is never modified — this only affects printed output.
    /// </summary>
    private static CodexBarConfig RedactSecrets(CodexBarConfig config)
    {
        using var mask = JsonDocument.Parse("\"***\"");
        var maskElement = mask.RootElement.Clone();
        return config with
        {
            Providers = config.Providers.Select(entry => entry with
            {
                ApiKey = string.IsNullOrEmpty(entry.ApiKey) ? entry.ApiKey : "***",
                CookieHeader = string.IsNullOrEmpty(entry.CookieHeader) ? entry.CookieHeader : "***",
                SecretKey = string.IsNullOrEmpty(entry.SecretKey) ? entry.SecretKey : "***",
                TokenAccounts = entry.TokenAccounts is null ? null : maskElement,
            }).ToArray(),
        };
    }

    private static int Path(CliRuntime runtime)
    {
        Console.WriteLine(runtime.Store.ResolvePath());
        return 0;
    }

    private static int Validate(CliRuntime runtime)
    {
        var warnings = new List<string>();
        if (runtime.Config.Version != 1)
        {
            warnings.Add($"version is {runtime.Config.Version}, expected 1");
        }

        foreach (var entry in runtime.Config.Providers)
        {
            var id = ProviderIds.TryParse(entry.Id);
            if (id is null)
            {
                warnings.Add($"unknown provider id preserved: {entry.Id}");
                continue;
            }

            if (ApiKeyProviders.Contains(id.Value) &&
                string.Equals(entry.Source, "api", StringComparison.OrdinalIgnoreCase) &&
                string.IsNullOrWhiteSpace(entry.ApiKey))
            {
                warnings.Add($"{entry.Id} uses source api but has no apiKey");
            }
        }

        if (warnings.Count == 0)
        {
            Console.WriteLine("OK");
            return 0;
        }

        foreach (var warning in warnings)
        {
            Console.WriteLine($"warning: {warning}");
        }

        return warnings.Any(warning => !warning.StartsWith("unknown provider id preserved:", StringComparison.Ordinal)) ? 1 : 0;
    }

    private static ProviderSelection RequiredProvider(ParsedArgs args)
    {
        var value = args.Value("provider");
        if (string.IsNullOrWhiteSpace(value))
        {
            return ProviderSelection.Failure("--provider <id> is required");
        }

        var provider = ProviderIds.TryParse(value);
        return provider is null ? ProviderSelection.Failure($"unknown provider '{value}'") : ProviderSelection.Success(provider.Value);
    }

    private static string AuthHint(ProviderDescriptor descriptor, ProviderConfigEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.ApiKey))
        {
            return "api key";
        }

        if (descriptor.Strategies.Any(strategy => strategy.Kind == FetchKind.ApiToken))
        {
            return "set API key or env var";
        }

        if (descriptor.Strategies.Any(strategy => strategy.Kind == FetchKind.Oauth))
        {
            return "sign in with provider CLI/app";
        }

        return descriptor.Strategies.Count == 0 ? "none" : string.Join("/", descriptor.Strategies.Select(strategy => strategy.Id));
    }

    private static string Mask(string key) => key.Length <= 4 ? "****" : $"****{key[^4..]}";

    private sealed record ProviderSelection(ProviderId Value, string? Error)
    {
        public static ProviderSelection Success(ProviderId value) => new(value, null);
        public static ProviderSelection Failure(string error) => new(default, error);
    }
}

internal static class ServeCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (ArgReader.HasHelp(args))
        {
            HelpPrinter.PrintServeHelp(Console.Out);
            return 0;
        }

        var parsed = ArgReader.Parse(args);
        var host = parsed.Value("host") ?? "127.0.0.1";
        var port = ParseInt(parsed.Value("port"), 8787, "port");
        var interval = ParseInt(parsed.Value("interval"), 30, "interval");
        if (port.Error is { } portError)
        {
            return Fail(portError);
        }

        if (interval.Error is { } intervalError)
        {
            return Fail(intervalError);
        }

        if (!IsLoopback(host) && !parsed.Has("host"))
        {
            return Fail("non-loopback host requires explicit --host");
        }

        var token = parsed.Value("token");
        if (!IsLoopback(host) && string.IsNullOrWhiteSpace(token))
        {
            return Fail("binding to a non-loopback host exposes usage and account data; supply --token <value> to require bearer-token auth");
        }

        // Loopback binds stay token-free unless a token was explicitly supplied.
        var requiredToken = string.IsNullOrWhiteSpace(token) ? null : token;

        var runtime = CliRuntime.Create(verbose: false);
        using var cts = ConsoleLifetime.CreateCancellationSource();
        using var listener = new HttpListener();
        var prefix = $"http://{host}:{port.Value}/";
        listener.Prefixes.Add(prefix);
        listener.Start();
        Console.Error.WriteLine($"codexbar serve listening on {prefix}");

        var cache = new UsageCache(TimeSpan.FromSeconds(interval.Value));
        try
        {
            while (!cts.IsCancellationRequested)
            {
                var contextTask = listener.GetContextAsync();
                var completed = await Task.WhenAny(contextTask, Task.Delay(Timeout.InfiniteTimeSpan, cts.Token)).ConfigureAwait(false);
                if (completed != contextTask)
                {
                    break;
                }

                await HandleAsync(runtime, cache, contextTask.Result, requiredToken, cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            listener.Stop();
        }

        return 0;
    }

    private static async Task HandleAsync(CliRuntime runtime, UsageCache cache, HttpListenerContext context, string? requiredToken, CancellationToken ct)
    {
        Console.Error.WriteLine($"{DateTimeOffset.UtcNow:O} {context.Request.HttpMethod} {context.Request.RawUrl}");
        if (context.Request.HttpMethod != "GET")
        {
            await WriteTextAsync(context, 405, "method not allowed").ConfigureAwait(false);
            return;
        }

        if (requiredToken is not null && !IsAuthorized(context.Request, requiredToken))
        {
            // WWW-Authenticate is a restricted header on HttpListenerResponse, so the 401 body carries the hint.
            await WriteTextAsync(context, 401, "unauthorized: pass Authorization: Bearer <token> or ?token=<token>").ConfigureAwait(false);
            return;
        }

        var requestPath = context.Request.Url?.AbsolutePath.Trim('/') ?? string.Empty;
        if (requestPath == "healthz")
        {
            await WriteJsonAsync(context, new HealthJson(true), CliJsonContext.Default.HealthJson).ConfigureAwait(false);
            return;
        }

        if (requestPath == "usage")
        {
            var output = await cache.GetOrRefreshAsync("enabled", () => FetchUsageJsonAsync(runtime, null, ct)).ConfigureAwait(false);
            await WriteJsonAsync(context, output, CliJsonContext.Default.UsageJsonOutput).ConfigureAwait(false);
            return;
        }

        const string usagePrefix = "usage/";
        if (requestPath.StartsWith(usagePrefix, StringComparison.OrdinalIgnoreCase))
        {
            var id = requestPath[usagePrefix.Length..];
            var provider = ProviderIds.TryParse(id);
            if (provider is null || runtime.DescriptorFor(provider.Value) is null)
            {
                await WriteTextAsync(context, 404, "unknown provider").ConfigureAwait(false);
                return;
            }

            var output = await cache.GetOrRefreshAsync($"provider:{provider.Value.ConfigId()}", () => FetchUsageJsonAsync(runtime, provider.Value, ct)).ConfigureAwait(false);
            await WriteJsonAsync(context, output, CliJsonContext.Default.UsageJsonOutput).ConfigureAwait(false);
            return;
        }

        await WriteTextAsync(context, 404, "not found").ConfigureAwait(false);
    }

    private static async Task<UsageJsonOutput> FetchUsageJsonAsync(CliRuntime runtime, ProviderId? provider, CancellationToken ct)
    {
        IReadOnlyList<ProviderDescriptor> descriptors = provider is { } id
            ? [runtime.DescriptorFor(id)!]
            : runtime.Descriptors.Where(runtime.IsEnabled).ToArray();
        var now = DateTimeOffset.UtcNow;
        var results = await Task.WhenAll(descriptors.Select(descriptor => runtime.FetchAsync(descriptor, ct))).ConfigureAwait(false);
        return UsageMapper.ToJson(now, results);
    }

    private static async Task WriteJsonAsync<T>(HttpListenerContext context, T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    {
        context.Response.StatusCode = 200;
        context.Response.ContentType = "application/json; charset=utf-8";
        await JsonSerializer.SerializeAsync(context.Response.OutputStream, value, typeInfo).ConfigureAwait(false);
        context.Response.Close();
    }

    private static async Task WriteTextAsync(HttpListenerContext context, int status, string text)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "text/plain; charset=utf-8";
        var bytes = Encoding.UTF8.GetBytes(text);
        await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        context.Response.Close();
    }

    private static bool IsAuthorized(HttpListenerRequest request, string requiredToken)
    {
        var header = request.Headers["Authorization"];
        if (header is not null &&
            header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) &&
            TokensEqual(header["Bearer ".Length..].Trim(), requiredToken))
        {
            return true;
        }

        var query = request.QueryString["token"];
        return query is not null && TokensEqual(query, requiredToken);
    }

    private static bool TokensEqual(string presented, string expected) =>
        System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(presented),
            Encoding.UTF8.GetBytes(expected));

    private static bool IsLoopback(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
        IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);

    private static IntResult ParseInt(string? value, int fallback, string name)
    {
        if (value is null)
        {
            return new(fallback, null);
        }

        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? new(parsed, null)
            : new(0, $"--{name} must be a positive integer");
    }

    private sealed record IntResult(int Value, string? Error);
}

internal static class DiagnoseCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (ArgReader.HasHelp(args))
        {
            HelpPrinter.PrintDiagnoseHelp(Console.Out);
            return 0;
        }

        var parsed = ArgReader.Parse(args);
        var providerValue = parsed.Value("provider");
        if (providerValue is null)
        {
            return Fail("--provider <id> is required");
        }

        var provider = ProviderIds.TryParse(providerValue);
        if (provider is null)
        {
            return Fail($"unknown provider '{providerValue}'");
        }

        var runtime = CliRuntime.Create(verbose: false);
        var descriptor = runtime.DescriptorFor(provider.Value);
        if (descriptor is null)
        {
            return Fail($"provider '{providerValue}' is not in the catalog");
        }

        using var cts = ConsoleLifetime.CreateCancellationSource();
        var result = await runtime.FetchAsync(descriptor, cts.Token).ConfigureAwait(false);
        var output = new DiagnoseJsonOutput(
            descriptor.Id.ConfigId(),
            runtime.Store.ResolvePath(),
            result.Outcome.Attempts.Select(attempt => new DiagnosticAttemptJson(
                attempt.StrategyId,
                attempt.Kind.ToString(),
                attempt.WasAvailable,
                Redact(attempt.Error))).ToArray(),
            result.Outcome.Succeeded,
            result.Outcome.Snapshot is null ? null : DiagnosticSnapshotJson.From(result.Outcome.Snapshot),
            Redact(ErrorMessage(result.Outcome.Error)));

        if (parsed.Has("json"))
        {
            JsonWriter.Write(Console.Out, output, CliJsonContext.Default.DiagnoseJsonOutput, parsed.Has("pretty"));
        }
        else
        {
            Console.WriteLine($"provider: {output.ProviderId}");
            Console.WriteLine($"config: {output.ConfigPath}");
            foreach (var attempt in output.Attempts)
            {
                Console.WriteLine($"attempt: {attempt.StrategyId} kind={attempt.Kind} available={(attempt.WasAvailable ? "yes" : "no")} error={attempt.Error ?? "-"}");
            }

            Console.WriteLine($"snapshot: {(output.SnapshotProduced ? "yes" : "no")}");
            if (output.Snapshot is not null)
            {
                Console.WriteLine($"confidence: {output.Snapshot.Confidence}");
                Console.WriteLine($"windows: {string.Join(", ", output.Snapshot.WindowsPresent)}");
            }

            if (output.TerminalError is not null)
            {
                Console.WriteLine($"error: {output.TerminalError}");
            }
        }

        return result.Outcome.Succeeded ? 0 : 1;
    }

    private static string? Redact(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        foreach (var marker in new[] { "sk-", "Bearer ", "apiKey", "cookie", "token" })
        {
            var index = value.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index >= 0)
            {
                return value[..index] + "[redacted]";
            }
        }

        return value;
    }
}

internal sealed class CliRuntime
{
    private readonly bool verbose;

    private CliRuntime(ConfigStore store, CodexBarConfig config, IReadOnlyList<ProviderDescriptor> descriptors, bool verbose)
    {
        this.Store = store;
        this.Config = config;
        this.Descriptors = descriptors;
        this.verbose = verbose;
    }

    public ConfigStore Store { get; }
    public CodexBarConfig Config { get; }
    public IReadOnlyList<ProviderDescriptor> Descriptors { get; }

    public static CliRuntime Create(bool verbose)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var store = new ConfigStore(Environment.GetEnvironmentVariable, profile, message =>
        {
            if (verbose)
            {
                Console.Error.WriteLine(message);
            }
        });
        return new CliRuntime(store, store.Load(), ProviderCatalog.CreateAll(), verbose);
    }

    public ProviderDescriptor? DescriptorFor(ProviderId id) => this.Descriptors.FirstOrDefault(descriptor => descriptor.Id == id);

    public bool IsEnabled(ProviderDescriptor descriptor) =>
        this.Store.EntryFor(this.Config, descriptor.Id).Enabled ?? descriptor.Metadata.DefaultEnabled;

    public async Task<ProviderFetchResult> FetchAsync(ProviderDescriptor descriptor, CancellationToken ct)
    {
        var ctx = new FetchContext
        {
            ProviderConfig = this.Store.EntryFor(this.Config, descriptor.Id),
            Http = ProviderHttpClient.Shared,
            Environment = Environment.GetEnvironmentVariable,
            UserProfileDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Now = () => DateTimeOffset.UtcNow,
            Log = message =>
            {
                if (this.verbose)
                {
                    Console.Error.WriteLine(message);
                }
            },
        };
        var outcome = await FetchPipeline.RunAsync(descriptor, ctx, ct).ConfigureAwait(false);
        return new ProviderFetchResult(descriptor, outcome);
    }
}

internal sealed class UsageCache(TimeSpan ttl)
{
    private readonly Dictionary<string, CacheEntry> entries = [];

    public async Task<UsageJsonOutput> GetOrRefreshAsync(string key, Func<Task<UsageJsonOutput>> refresh)
    {
        var now = DateTimeOffset.UtcNow;
        if (this.entries.TryGetValue(key, out var entry) && now - entry.FetchedAt < ttl)
        {
            return entry.Output;
        }

        var output = await refresh().ConfigureAwait(false);
        this.entries[key] = new CacheEntry(now, output);
        return output;
    }

    private sealed record CacheEntry(DateTimeOffset FetchedAt, UsageJsonOutput Output);
}

internal static class UsageMapper
{
    public static UsageJsonOutput ToJson(DateTimeOffset generatedAt, IReadOnlyList<ProviderFetchResult> results) =>
        new(generatedAt, results.Select(ToProviderJson).ToArray());

    private static ProviderUsageJson ToProviderJson(ProviderFetchResult result) =>
        new(
            result.Descriptor.Id.ConfigId(),
            result.Descriptor.Metadata.DisplayName,
            result.Outcome.Succeeded,
            result.Outcome.Succeeded ? null : ErrorMessage(result.Outcome.Error),
            result.Outcome.Snapshot is null ? null : SnapshotJson.From(result.Outcome.Snapshot));
}

internal static class TextUsageWriter
{
    public static void Write(TextWriter writer, DateTimeOffset now, IReadOnlyList<ProviderFetchResult> results)
    {
        foreach (var result in results)
        {
            writer.WriteLine(result.Descriptor.Metadata.DisplayName);
            if (result.Outcome.Snapshot is { } snapshot)
            {
                var identity = snapshot.Identity;
                var parts = new[] { identity?.Plan, identity?.AccountEmail, identity?.AccountOrganization }
                    .Where(part => !string.IsNullOrWhiteSpace(part))
                    .ToArray();
                if (parts.Length > 0)
                {
                    writer.WriteLine($"  {string.Join(" · ", parts)}");
                }

                WriteWindow(writer, result.Descriptor.Metadata.SessionLabel, snapshot.Primary, now);
                WriteWindow(writer, result.Descriptor.Metadata.WeeklyLabel, snapshot.Secondary, now);
                WriteWindow(writer, "Tertiary", snapshot.Tertiary, now);
                foreach (var extra in snapshot.ExtraWindows)
                {
                    WriteWindow(writer, extra.Title, extra.Window, now);
                }

                if (snapshot.Credits is { } credits)
                {
                    var limit = credits.Limit is null ? string.Empty : string.Create(CultureInfo.InvariantCulture, $" / {credits.Limit:0.##}");
                    writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  Credits {credits.Remaining:0.##}{limit} {credits.Unit}"));
                }
            }
            else
            {
                writer.WriteLine($"  error: {ErrorMessage(result.Outcome.Error)}");
            }

            writer.WriteLine();
        }
    }

    private static void WriteWindow(TextWriter writer, string label, RateWindow? window, DateTimeOffset now)
    {
        if (window is null)
        {
            return;
        }

        var reset = ResetText(window, now);
        writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {label} {window.UsedPercent:0.#}%{reset}"));
    }

    private static string ResetText(RateWindow window, DateTimeOffset now)
    {
        if (window.ResetsAt is { } reset)
        {
            var delta = reset.ToUniversalTime() - now.ToUniversalTime();
            if (delta <= TimeSpan.Zero)
            {
                return " · reset due";
            }

            return $" · resets in {FormatDuration(delta)}";
        }

        return string.IsNullOrWhiteSpace(window.ResetDescription) ? string.Empty : $" · {window.ResetDescription}";
    }

    private static string FormatDuration(TimeSpan value)
    {
        if (value.TotalDays >= 1)
        {
            return $"{(int)value.TotalDays}d {value.Hours}h";
        }

        if (value.TotalHours >= 1)
        {
            return $"{(int)value.TotalHours}h {value.Minutes}m";
        }

        return $"{Math.Max(0, value.Minutes)}m";
    }
}

internal static class ArgReader
{
    public static bool HasHelp(string[] args) => args.Any(arg => arg is "--help" or "-h");

    public static ParsedArgs Parse(string[] args)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var positionals = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                positionals.Add(arg);
                continue;
            }

            var key = arg[2..];
            string? value = null;
            var eq = key.IndexOf('=', StringComparison.Ordinal);
            if (eq >= 0)
            {
                value = key[(eq + 1)..];
                key = key[..eq];
            }
            else if (i + 1 < args.Length && !args[i + 1].StartsWith("-", StringComparison.Ordinal))
            {
                value = args[++i];
            }

            values[key] = value ?? "true";
        }

        return new ParsedArgs(values, positionals);
    }
}

internal sealed record ParsedArgs(IReadOnlyDictionary<string, string?> Values, IReadOnlyList<string> Positionals)
{
    public bool Has(string name) => this.Values.ContainsKey(name);
    public string? Value(string name) => this.Values.TryGetValue(name, out var value) && value != "true" ? value : null;
}

internal static class JsonWriter
{
    public static void Write<T>(TextWriter writer, T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo, bool pretty)
    {
        var options = new JsonSerializerOptions(typeInfo.Options) { WriteIndented = pretty };
        writer.WriteLine(JsonSerializer.Serialize(value, typeInfo.Type, options));
    }
}

internal static class ConsoleLifetime
{
    public static CancellationTokenSource CreateCancellationSource()
    {
        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };
        return cts;
    }
}

internal static class HelpPrinter
{
    public static void PrintRootHelp(TextWriter writer)
    {
        writer.WriteLine("codexbar - headless CodexWinBar CLI");
        writer.WriteLine();
        writer.WriteLine("Usage:");
        writer.WriteLine("  codexbar usage [--provider <id>] [--all] [--json] [--pretty] [--verbose]");
        writer.WriteLine("  codexbar config <providers|enable|disable|set-api-key|print|path|validate> [flags]");
        writer.WriteLine("  codexbar serve [--port <n>] [--host <host>] [--interval <seconds>] [--token <value>]");
        writer.WriteLine("  codexbar diagnose --provider <id> [--json] [--pretty]");
        writer.WriteLine();
        writer.WriteLine("Global:");
        writer.WriteLine("  --help, -h     Show help");
        writer.WriteLine("  --version      Show version");
    }

    public static void PrintUsageHelp(TextWriter writer) => writer.WriteLine("Usage: codexbar usage [--provider <id>] [--all] [--json] [--format json] [--pretty] [--verbose]");

    public static void PrintConfigHelp(TextWriter writer)
    {
        writer.WriteLine("Usage:");
        writer.WriteLine("  codexbar config providers [--json] [--pretty]");
        writer.WriteLine("  codexbar config enable --provider <id>");
        writer.WriteLine("  codexbar config disable --provider <id>");
        writer.WriteLine("  codexbar config set-api-key --provider <id> [--stdin | --api-key <key>] [--no-enable]");
        writer.WriteLine("  codexbar config print [--show-secrets]");
        writer.WriteLine("  codexbar config path");
        writer.WriteLine("  codexbar config validate");
    }

    public static void PrintServeHelp(TextWriter writer) => writer.WriteLine("Usage: codexbar serve [--port <n>=8787] [--host 127.0.0.1] [--interval <seconds>=30] [--token <value>] (--token is required for non-loopback hosts; clients send Authorization: Bearer <value> or ?token=<value>)");
    public static void PrintDiagnoseHelp(TextWriter writer) => writer.WriteLine("Usage: codexbar diagnose --provider <id> [--json] [--pretty]");
}

internal static class CliSupport
{
    public static string ErrorMessage(Exception? error) =>
        error switch
        {
            null => "unknown error",
            UnauthorizedProviderException => "not signed in / needs auth",
            NoAvailableStrategyException => "no available data source",
            _ => error.Message,
        };

    public static int Fail(string message)
    {
        Console.Error.WriteLine($"error: {message}");
        return 2;
    }
}

internal sealed record ProviderFetchResult(ProviderDescriptor Descriptor, FetchOutcome Outcome);
internal sealed record UsageJsonOutput(DateTimeOffset GeneratedAt, IReadOnlyList<ProviderUsageJson> Providers);
internal sealed record ProviderUsageJson(string Id, string DisplayName, bool Ok, string? Error, SnapshotJson? Snapshot);

internal sealed record SnapshotJson(
    string Provider,
    RateWindowJson? Primary,
    RateWindowJson? Secondary,
    RateWindowJson? Tertiary,
    IReadOnlyList<NamedRateWindowJson> ExtraWindows,
    CreditsJson? Credits,
    IdentityJson? Identity,
    DateTimeOffset UpdatedAt,
    string Confidence)
{
    public static SnapshotJson From(UsageSnapshot snapshot) =>
        new(
            snapshot.Provider.ConfigId(),
            RateWindowJson.From(snapshot.Primary),
            RateWindowJson.From(snapshot.Secondary),
            RateWindowJson.From(snapshot.Tertiary),
            snapshot.ExtraWindows.Select(window => new NamedRateWindowJson(window.Id, window.Title, RateWindowJson.From(window.Window)!, window.UsageKnown)).ToArray(),
            snapshot.Credits is null ? null : new CreditsJson(snapshot.Credits.Remaining, snapshot.Credits.Limit, snapshot.Credits.Unit, snapshot.Credits.UpdatedAt),
            snapshot.Identity is null ? null : new IdentityJson(snapshot.Identity.AccountEmail, snapshot.Identity.AccountOrganization, snapshot.Identity.Plan, snapshot.Identity.LoginMethod),
            snapshot.UpdatedAt,
            snapshot.Confidence.ToString());
}

internal sealed record RateWindowJson(
    double UsedPercent,
    int? WindowMinutes,
    DateTimeOffset? ResetsAt,
    string? ResetDescription,
    double? NextRegenPercent,
    bool IsSyntheticPlaceholder,
    double RemainingPercent)
{
    public static RateWindowJson? From(RateWindow? window) =>
        window is null
            ? null
            : new RateWindowJson(window.UsedPercent, window.WindowMinutes, window.ResetsAt, window.ResetDescription, window.NextRegenPercent, window.IsSyntheticPlaceholder, window.RemainingPercent);
}

internal sealed record NamedRateWindowJson(string Id, string Title, RateWindowJson Window, bool UsageKnown);
internal sealed record CreditsJson(double Remaining, double? Limit, string Unit, DateTimeOffset UpdatedAt);
internal sealed record IdentityJson(string? AccountEmail, string? AccountOrganization, string? Plan, string? LoginMethod);
internal sealed record ConfigProvidersJsonOutput(IReadOnlyList<ConfigProviderJson> Providers);
internal sealed record ConfigProviderJson(string Id, string DisplayName, bool Enabled, bool Configured, string AuthHint);
internal sealed record DiagnoseJsonOutput(string ProviderId, string ConfigPath, IReadOnlyList<DiagnosticAttemptJson> Attempts, bool SnapshotProduced, DiagnosticSnapshotJson? Snapshot, string? TerminalError);
internal sealed record DiagnosticAttemptJson(string StrategyId, string Kind, bool WasAvailable, string? Error);

internal sealed record DiagnosticSnapshotJson(string Confidence, IReadOnlyList<string> WindowsPresent)
{
    public static DiagnosticSnapshotJson From(UsageSnapshot snapshot)
    {
        var windows = new List<string>();
        if (snapshot.Primary is not null)
        {
            windows.Add("primary");
        }

        if (snapshot.Secondary is not null)
        {
            windows.Add("secondary");
        }

        if (snapshot.Tertiary is not null)
        {
            windows.Add("tertiary");
        }

        windows.AddRange(snapshot.ExtraWindows.Select(window => window.Id));
        return new DiagnosticSnapshotJson(snapshot.Confidence.ToString(), windows);
    }
}

internal sealed record HealthJson(bool Ok);
