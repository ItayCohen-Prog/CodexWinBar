using System.Text.Json.Serialization;
using CodexWinBar.Core.Config;
using CodexWinBar.Core.Statistics;

namespace CodexWinBar.Core.Json;

/// <summary>
/// Source-generated JSON metadata for core DTO serialization.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(CodexBarConfig))]
[JsonSerializable(typeof(ProviderConfigEntry))]
[JsonSerializable(typeof(QuotaWarnings))]
[JsonSerializable(typeof(QuotaWarningWindow))]
[JsonSerializable(typeof(UiSettings))]
[JsonSerializable(typeof(PlanStatisticsDocument))]
public sealed partial class CoreJsonContext : JsonSerializerContext;
