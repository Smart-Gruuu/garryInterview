using System.Text.Json.Serialization;
using InterviewAssist.Core.Models;

namespace InterviewAssist.Core.Serialization;

/// <summary>Source-generated (trim/AOT-safe) JSON metadata for persisted types.</summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals)]
[JsonSerializable(typeof(AppSettings))]
public partial class AppJsonContext : JsonSerializerContext;
