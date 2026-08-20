using System.Text.Json;
using System.Text.Json.Serialization;

namespace SwitchOnYourCode;

public static class SwitchOnYourCodeConstants
{
    public const int SchemaVersion = 1;
    public const int BucketScale = 100_000;
}

public enum EvaluationReason
{
    Static,
    Default,
    TargetingMatch,
    Split,
    Disabled,
    Error,
}

public enum EvaluationErrorCode
{
    None,
    ParseError,
    TargetingKeyMissing,
    InvalidContext,
    ProviderNotReady,
    FlagNotFound,
    TypeMismatch,
}

public sealed record EvaluationContext(
    string? TargetingKey = null,
    IReadOnlyDictionary<string, object?>? Attributes = null)
{
    public static EvaluationContext Empty { get; } = new();

    internal IReadOnlyDictionary<string, object?> SafeAttributes =>
        Attributes ?? EmptyAttributes.Instance;

    private sealed class EmptyAttributes : Dictionary<string, object?>
    {
        internal static EmptyAttributes Instance { get; } = new();
    }
}

public sealed record EvaluationDetails<T>(
    T Value,
    string Variant,
    EvaluationReason Reason,
    string? RuleId = null,
    EvaluationErrorCode ErrorCode = EvaluationErrorCode.None,
    string? ErrorMessage = null);

public sealed class SwitchOnYourCodeConfiguration
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; set; }

    [JsonPropertyName("environment")]
    public SwitchOnYourCodeEnvironment Environment { get; set; } = new();

    [JsonPropertyName("flags")]
    public List<FeatureFlag> Flags { get; set; } = [];

    [JsonPropertyName("segments")]
    public List<Segment> Segments { get; set; } = [];
}

public sealed class SwitchOnYourCodeEnvironment
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;
}

public sealed class FeatureFlag
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;

    [JsonPropertyName("default_value")]
    public JsonElement DefaultValue { get; set; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("variants")]
    public List<Variant> Variants { get; set; } = [];

    [JsonPropertyName("policy")]
    public Policy Policy { get; set; } = new();

    [JsonPropertyName("revision")]
    public long Revision { get; set; }
}

public sealed class Variant
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("value")]
    public JsonElement Value { get; set; }
}

public sealed class Condition
{
    [JsonPropertyName("attribute")]
    public string? Attribute { get; set; }

    [JsonPropertyName("operator")]
    public string Operator { get; set; } = string.Empty;

    [JsonPropertyName("value")]
    public JsonElement Value { get; set; }
}

public sealed class Allocation
{
    [JsonPropertyName("variant")]
    public string Variant { get; set; } = string.Empty;

    [JsonPropertyName("weight")]
    public int Weight { get; set; }
}

public sealed class Outcome
{
    [JsonPropertyName("variant")]
    public string? Variant { get; set; }

    [JsonPropertyName("rollout")]
    public List<Allocation> Rollout { get; set; } = [];

    [JsonPropertyName("bucket_by")]
    public string? BucketBy { get; set; }
}

public sealed class Rule
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("match")]
    public string Match { get; set; } = string.Empty;

    [JsonPropertyName("conditions")]
    public List<Condition> Conditions { get; set; } = [];

    [JsonPropertyName("outcome")]
    public Outcome Outcome { get; set; } = new();
}

public sealed class Policy
{
    [JsonPropertyName("rules")]
    public List<Rule> Rules { get; set; } = [];

    [JsonPropertyName("fallthrough")]
    public Outcome Fallthrough { get; set; } = new();
}

public sealed class Segment
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("match")]
    public string Match { get; set; } = string.Empty;

    [JsonPropertyName("conditions")]
    public List<Condition> Conditions { get; set; } = [];
}

public sealed record FeatureFlagInfo(
    string Id,
    string Key,
    string Kind,
    bool Enabled,
    long Revision,
    SwitchOnYourCodeEnvironment Environment);

public sealed record RefreshResult(bool Modified, SwitchOnYourCodeConfiguration Configuration);

internal sealed record RawEvaluationDetails(
    JsonElement Value,
    string Variant,
    EvaluationReason Reason,
    string? RuleId = null,
    EvaluationErrorCode ErrorCode = EvaluationErrorCode.None,
    string? ErrorMessage = null);
