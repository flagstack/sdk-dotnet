using System.Text.Json;

namespace FlagStack;

internal static class ConfigurationValidator
{
    private static readonly HashSet<string> SupportedOperators =
    [
        "equals", "not_equals", "in", "not_in", "contains", "not_contains", "starts_with", "ends_with",
        "greater_than", "greater_than_or_equal", "less_than", "less_than_or_equal", "exists", "not_exists",
        "matches_regex", "semver_greater_than", "semver_greater_than_or_equal", "semver_less_than",
        "semver_less_than_or_equal", "in_segment", "not_in_segment",
    ];

    internal static void Validate(FlagStackConfiguration configuration)
    {
        if (configuration.SchemaVersion != FlagStackConstants.SchemaVersion)
            throw new FlagStackConfigurationException($"Unsupported FlagStack schema version {configuration.SchemaVersion}.");
        if (configuration.Environment is null || string.IsNullOrWhiteSpace(configuration.Environment.Id) || string.IsNullOrWhiteSpace(configuration.Environment.Key))
            throw new FlagStackConfigurationException("FlagStack environment id and key are required.");
        if (configuration.Segments is null || configuration.Flags is null)
            throw new FlagStackConfigurationException("FlagStack configuration flags and segments must be arrays.");

        var segmentKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var segment in configuration.Segments)
        {
            ValidateSegment(segment);
            if (!segmentKeys.Add(segment.Key))
                throw new FlagStackConfigurationException($"Duplicate segment key '{segment.Key}'.");
        }

        var flagKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var flag in configuration.Flags)
        {
            ValidateFlag(flag, configuration.Environment.Id);
            if (string.IsNullOrWhiteSpace(flag.Key))
                throw new FlagStackConfigurationException("Flag key is required.");
            if (!flagKeys.Add(flag.Key))
                throw new FlagStackConfigurationException($"Duplicate flag key '{flag.Key}'.");
        }
    }

    internal static void ValidateFlag(FeatureFlag flag, string environmentId)
    {
        if (string.IsNullOrWhiteSpace(flag.Id) || string.IsNullOrWhiteSpace(environmentId))
            throw new EvaluationFailure(EvaluationErrorCode.ParseError, "flag and environment IDs are required");
        if (flag.Kind is not ("boolean" or "string" or "number" or "json"))
            throw new EvaluationFailure(EvaluationErrorCode.ParseError, $"unsupported flag kind '{flag.Kind}'");
        if (flag.Revision < 0)
            throw new EvaluationFailure(EvaluationErrorCode.ParseError, "flag revision must not be negative");

        if (flag.Variants is null || flag.Policy is null)
            throw new EvaluationFailure(EvaluationErrorCode.ParseError, "flag variants and policy are required");
        if (flag.Policy.Rules is null || flag.Policy.Fallthrough is null)
            throw new EvaluationFailure(EvaluationErrorCode.ParseError, "policy rules and fallthrough are required");

        ValidateValueKind(flag.Kind, flag.DefaultValue);
        var variants = new HashSet<string>(StringComparer.Ordinal) { "default" };
        if (flag.Kind == "boolean")
        {
            variants.Add("on");
            variants.Add("off");
        }

        foreach (var variant in flag.Variants)
        {
            var key = variant.Key.Trim();
            if (key.Length == 0) throw new EvaluationFailure(EvaluationErrorCode.ParseError, "variant key is required");
            if (!variants.Add(key))
                throw new EvaluationFailure(EvaluationErrorCode.ParseError, $"variant key '{key}' is reserved or duplicated");
            ValidateValueKind(flag.Kind, variant.Value);
        }

        var ruleIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in flag.Policy.Rules)
        {
            if (string.IsNullOrWhiteSpace(rule.Id))
                throw new EvaluationFailure(EvaluationErrorCode.ParseError, "rule ID is required");
            if (!ruleIds.Add(rule.Id))
                throw new EvaluationFailure(EvaluationErrorCode.ParseError, $"duplicate rule ID '{rule.Id}'");
            if (rule.Conditions is null || rule.Outcome is null)
                throw new EvaluationFailure(EvaluationErrorCode.ParseError, $"rule '{rule.Id}' conditions and outcome are required");
            ValidateMatchMode(rule.Match);
            if (rule.Conditions.Count == 0)
                throw new EvaluationFailure(EvaluationErrorCode.ParseError, $"rule '{rule.Id}' must contain at least one condition");
            foreach (var condition in rule.Conditions) ValidateCondition(condition);
            ValidateOutcome(rule.Outcome, variants, required: true);
        }
        ValidateOutcome(flag.Policy.Fallthrough, variants, required: false);
    }

    internal static void ValidateSegment(Segment segment)
    {
        if (string.IsNullOrWhiteSpace(segment.Key))
            throw new EvaluationFailure(EvaluationErrorCode.ParseError, "segment key is required");
        if (segment.Conditions is null)
            throw new EvaluationFailure(EvaluationErrorCode.ParseError, $"segment '{segment.Key}' conditions are required");
        ValidateMatchMode(segment.Match);
        if (segment.Conditions.Count == 0)
            throw new EvaluationFailure(EvaluationErrorCode.ParseError, $"segment '{segment.Key}' must contain at least one condition");
        foreach (var condition in segment.Conditions) ValidateCondition(condition);
    }

    private static void ValidateCondition(Condition condition)
    {
        if (!SupportedOperators.Contains(condition.Operator))
            throw new EvaluationFailure(EvaluationErrorCode.ParseError, $"unsupported operator '{condition.Operator}'");

        if (condition.Operator is "in_segment" or "not_in_segment")
        {
            if (condition.Value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(condition.Value.GetString()))
                throw new EvaluationFailure(EvaluationErrorCode.ParseError, "segment reference must be a non-empty string");
            return;
        }

        if (condition.Operator is "exists" or "not_exists")
        {
            if (string.IsNullOrWhiteSpace(condition.Attribute))
                throw new EvaluationFailure(EvaluationErrorCode.ParseError, "condition attribute is required");
            return;
        }

        if (string.IsNullOrWhiteSpace(condition.Attribute))
            throw new EvaluationFailure(EvaluationErrorCode.ParseError, "condition attribute is required");
        if (condition.Value.ValueKind == JsonValueKind.Undefined)
            throw new EvaluationFailure(EvaluationErrorCode.ParseError, "condition value is required");

        if ((condition.Operator is "in" or "not_in") && condition.Value.ValueKind != JsonValueKind.Array)
            throw new EvaluationFailure(EvaluationErrorCode.ParseError, $"{condition.Operator} condition value must be an array");
        if (condition.Operator == "matches_regex")
        {
            if (condition.Value.ValueKind != JsonValueKind.String)
                throw new EvaluationFailure(EvaluationErrorCode.ParseError, "regex condition value must be a string");
            _ = RegexCompatibility.Compile(condition.Value.GetString()!);
        }
        if (condition.Operator.StartsWith("semver_", StringComparison.Ordinal))
        {
            if (condition.Value.ValueKind != JsonValueKind.String || Semver.Compare(condition.Value.GetString()!, condition.Value.GetString()!) is null)
                throw new EvaluationFailure(EvaluationErrorCode.ParseError, "semantic-version condition value must be a valid semantic version");
        }
    }

    private static void ValidateOutcome(Outcome outcome, HashSet<string> allowed, bool required)
    {
        if (outcome.Rollout is null)
            throw new EvaluationFailure(EvaluationErrorCode.ParseError, "outcome rollout must be an array");
        var hasVariant = !string.IsNullOrWhiteSpace(outcome.Variant);
        var hasRollout = outcome.Rollout.Count > 0;
        if (hasVariant && hasRollout)
            throw new EvaluationFailure(EvaluationErrorCode.ParseError, "outcome cannot contain both a variant and a rollout");
        if (!hasVariant && !hasRollout)
        {
            if (required) throw new EvaluationFailure(EvaluationErrorCode.ParseError, "outcome must contain a variant or rollout");
            return;
        }

        if (hasVariant)
        {
            if (!allowed.Contains(outcome.Variant!))
                throw new EvaluationFailure(EvaluationErrorCode.ParseError, $"unknown variant '{outcome.Variant}'");
            return;
        }

        long total = 0;
        foreach (var allocation in outcome.Rollout)
        {
            if (!allowed.Contains(allocation.Variant))
                throw new EvaluationFailure(EvaluationErrorCode.ParseError, $"unknown rollout variant '{allocation.Variant}'");
            if (allocation.Weight <= 0)
                throw new EvaluationFailure(EvaluationErrorCode.ParseError, "rollout weights must be positive integers");
            total += allocation.Weight;
        }
        if (total != FlagStackConstants.BucketScale)
            throw new EvaluationFailure(EvaluationErrorCode.ParseError, $"rollout weights must total {FlagStackConstants.BucketScale}");
    }

    private static void ValidateMatchMode(string match)
    {
        if (match is not ("all" or "any"))
            throw new EvaluationFailure(EvaluationErrorCode.ParseError, "match mode must be 'all' or 'any'");
    }

    private static void ValidateValueKind(string kind, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Undefined)
            throw new EvaluationFailure(EvaluationErrorCode.ParseError, "JSON value is required");
        if (kind == "boolean" && value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new EvaluationFailure(EvaluationErrorCode.ParseError, "value must be a boolean");
        if (kind == "string" && value.ValueKind != JsonValueKind.String)
            throw new EvaluationFailure(EvaluationErrorCode.ParseError, "value must be a string");
        if (kind == "number" && value.ValueKind != JsonValueKind.Number)
            throw new EvaluationFailure(EvaluationErrorCode.ParseError, "value must be a number");
    }
}
