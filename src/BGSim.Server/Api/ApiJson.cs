using System.Text.Json;
using System.Text.Json.Serialization;

namespace BGSim.Server.Api;

/// <summary>
/// Source-generated JSON for the API (no reflection, Native AOT). Reading is strict: unknown members, comments,
/// trailing commas, duplicate members and numbers in strings are errors, so a typo in a request is a 400, not a
/// silently ignored field.
/// </summary>
[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    ReadCommentHandling = JsonCommentHandling.Disallow,
    AllowTrailingCommas = false,
    AllowDuplicateProperties = false,
    NumberHandling = JsonNumberHandling.Strict,
    MaxDepth = 16)]
[JsonSerializable(typeof(SimulateRequest))]
[JsonSerializable(typeof(FightRequest))]
[JsonSerializable(typeof(SimulateResponse))]
[JsonSerializable(typeof(FightResponse))]
[JsonSerializable(typeof(InfoResponse))]
[JsonSerializable(typeof(ErrorResponse))]
internal sealed partial class ApiJson : JsonSerializerContext;
