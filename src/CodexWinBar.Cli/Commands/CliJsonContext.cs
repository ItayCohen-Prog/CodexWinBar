using System.Text.Json.Serialization;
using CodexWinBar.Core.Config;

namespace CodexWinBar.Cli.Commands;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = false,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(UsageJsonOutput))]
[JsonSerializable(typeof(ProviderUsageJson))]
[JsonSerializable(typeof(SnapshotJson))]
[JsonSerializable(typeof(RateWindowJson))]
[JsonSerializable(typeof(NamedRateWindowJson))]
[JsonSerializable(typeof(CreditsJson))]
[JsonSerializable(typeof(IdentityJson))]
[JsonSerializable(typeof(ConfigProvidersJsonOutput))]
[JsonSerializable(typeof(ConfigProviderJson))]
[JsonSerializable(typeof(DiagnoseJsonOutput))]
[JsonSerializable(typeof(DiagnosticAttemptJson))]
[JsonSerializable(typeof(DiagnosticSnapshotJson))]
[JsonSerializable(typeof(HealthJson))]
[JsonSerializable(typeof(CodexBarConfig))]
internal sealed partial class CliJsonContext : JsonSerializerContext;
