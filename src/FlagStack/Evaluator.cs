using System.Collections;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FlagStack;

internal static class Evaluator
{
    internal static int Bucket(string environmentId, string flagId, string bucketValue)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"flagstack-v1\0{environmentId}\0{flagId}\0{bucketValue}"));
        var prefix = ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
        return (int)(prefix % FlagStackConstants.BucketScale);
    }

    internal static RawEvaluationDetails Evaluate(
        FeatureFlag flag,
        string environmentId,
        EvaluationContext? context,
        IReadOnlyList<Segment> segments)
    {
        var evaluationContext = context ?? EvaluationContext.Empty;
        try
        {
            ConfigurationValidator.ValidateFlag(flag, environmentId);
            var segmentIndex = new Dictionary<string, Segment>(StringComparer.Ordinal);
            foreach (var segment in segments)
            {
                ConfigurationValidator.ValidateSegment(segment);
                segmentIndex[segment.Key] = segment;
            }

            if (!flag.Enabled)
                return Result(flag.DefaultValue, "default", EvaluationReason.Disabled);

            foreach (var rule in flag.Policy.Rules)
            {
                if (!MatchConditions(rule.Match, rule.Conditions, evaluationContext, segmentIndex, new HashSet<string>(StringComparer.Ordinal)))
                    continue;
                var resolved = ResolveOutcome(flag, environmentId, rule.Outcome, evaluationContext);
                return resolved with
                {
                    RuleId = rule.Id,
                    Reason = rule.Outcome.Rollout.Count > 0 ? EvaluationReason.Split : EvaluationReason.TargetingMatch,
                };
            }

            var fallthrough = flag.Policy.Fallthrough;
            if (OutcomeEmpty(fallthrough))
            {
                if (flag.Kind == "boolean") return Result(JsonBoolean(true), "on", EvaluationReason.Static);
                return Result(flag.DefaultValue, "default", EvaluationReason.Default);
            }

            var result = ResolveOutcome(flag, environmentId, fallthrough, evaluationContext);
            return result with { Reason = fallthrough.Rollout.Count > 0 ? EvaluationReason.Split : EvaluationReason.Static };
        }
        catch (EvaluationFailure exception)
        {
            return Error(flag, exception.Code, exception.Message);
        }
        catch (Exception exception)
        {
            return Error(flag, EvaluationErrorCode.ParseError, exception.Message);
        }
    }

    private static RawEvaluationDetails ResolveOutcome(FeatureFlag flag, string environmentId, Outcome outcome, EvaluationContext context)
    {
        if (!string.IsNullOrWhiteSpace(outcome.Variant))
            return Result(VariantValue(flag, outcome.Variant), outcome.Variant, EvaluationReason.Static);

        var bucketValue = ResolveBucketValue(context, outcome.BucketBy);
        var selected = Bucket(environmentId, flag.Id, bucketValue);
        var cumulative = 0;
        foreach (var allocation in outcome.Rollout)
        {
            cumulative += allocation.Weight;
            if (selected < cumulative)
                return Result(VariantValue(flag, allocation.Variant), allocation.Variant, EvaluationReason.Split);
        }
        throw new EvaluationFailure(EvaluationErrorCode.ParseError, "rollout did not resolve a variant");
    }

    private static JsonElement VariantValue(FeatureFlag flag, string key)
    {
        if (key == "default") return flag.DefaultValue.Clone();
        if (flag.Kind == "boolean" && key == "on") return JsonBoolean(true);
        if (flag.Kind == "boolean" && key == "off") return JsonBoolean(false);
        foreach (var variant in flag.Variants)
            if (variant.Key == key) return variant.Value.Clone();
        throw new EvaluationFailure(EvaluationErrorCode.ParseError, $"unknown variant '{key}'");
    }

    private static string ResolveBucketValue(EvaluationContext context, string? bucketBy)
    {
        if (string.IsNullOrWhiteSpace(bucketBy) || bucketBy == "targetingKey")
        {
            if (string.IsNullOrEmpty(context.TargetingKey))
                throw new EvaluationFailure(EvaluationErrorCode.TargetingKeyMissing, "targeting key is required for percentage rollout");
            return context.TargetingKey;
        }

        if (!TryGetContextValue(context, bucketBy, out var value))
            throw new EvaluationFailure(EvaluationErrorCode.InvalidContext, $"bucket attribute '{bucketBy}' is missing");
        return JsonCompatibility.ScalarBucketValue(value);
    }

    private static bool MatchConditions(string match, IReadOnlyList<Condition> conditions, EvaluationContext context, IReadOnlyDictionary<string, Segment> segments, HashSet<string> visiting)
    {
        if (match == "any")
        {
            foreach (var condition in conditions)
                if (ConditionMatches(condition, context, segments, visiting)) return true;
            return false;
        }
        foreach (var condition in conditions)
            if (!ConditionMatches(condition, context, segments, visiting)) return false;
        return true;
    }

    private static bool ConditionMatches(Condition condition, EvaluationContext context, IReadOnlyDictionary<string, Segment> segments, HashSet<string> visiting)
    {
        if (condition.Operator is "in_segment" or "not_in_segment")
        {
            var segmentKey = condition.Value.GetString() ?? throw new EvaluationFailure(EvaluationErrorCode.ParseError, "segment condition must reference a string key");
            var matched = MatchSegment(segmentKey, context, segments, visiting);
            return condition.Operator == "not_in_segment" ? !matched : matched;
        }

        var exists = TryGetContextValue(context, condition.Attribute ?? string.Empty, out var actual);
        if (condition.Operator == "exists") return exists;
        if (condition.Operator == "not_exists") return !exists;
        if (!exists) return false;

        var expected = condition.Value;
        return condition.Operator switch
        {
            "equals" => EqualValues(actual, expected),
            "not_equals" => !EqualValues(actual, expected),
            "in" => expected.EnumerateArray().Any(candidate => EqualValues(actual, candidate)),
            "not_in" => !expected.EnumerateArray().Any(candidate => EqualValues(actual, candidate)),
            "contains" => ContainsValue(actual, expected),
            "not_contains" => !ContainsValue(actual, expected),
            "starts_with" => actual is string actualString && expected.ValueKind == JsonValueKind.String && actualString.StartsWith(expected.GetString()!, StringComparison.Ordinal),
            "ends_with" => actual is string actualString && expected.ValueKind == JsonValueKind.String && actualString.EndsWith(expected.GetString()!, StringComparison.Ordinal),
            "greater_than" => CompareNumbers(actual, expected, comparison => comparison > 0),
            "greater_than_or_equal" => CompareNumbers(actual, expected, comparison => comparison >= 0),
            "less_than" => CompareNumbers(actual, expected, comparison => comparison < 0),
            "less_than_or_equal" => CompareNumbers(actual, expected, comparison => comparison <= 0),
            "matches_regex" => actual is string regexValue && expected.ValueKind == JsonValueKind.String && RegexCompatibility.Compile(expected.GetString()!).IsMatch(regexValue),
            "semver_greater_than" => CompareSemver(actual, expected, comparison => comparison > 0),
            "semver_greater_than_or_equal" => CompareSemver(actual, expected, comparison => comparison >= 0),
            "semver_less_than" => CompareSemver(actual, expected, comparison => comparison < 0),
            "semver_less_than_or_equal" => CompareSemver(actual, expected, comparison => comparison <= 0),
            _ => throw new EvaluationFailure(EvaluationErrorCode.ParseError, $"unsupported operator '{condition.Operator}'"),
        };
    }

    private static bool MatchSegment(string key, EvaluationContext context, IReadOnlyDictionary<string, Segment> segments, HashSet<string> visiting)
    {
        if (!segments.TryGetValue(key, out var segment)) return false;
        if (!visiting.Add(key)) throw new EvaluationFailure(EvaluationErrorCode.ParseError, $"segment cycle detected at '{key}'");
        try { return MatchConditions(segment.Match, segment.Conditions, context, segments, visiting); }
        finally { visiting.Remove(key); }
    }

    internal static bool TryGetContextValue(EvaluationContext context, string path, out object? value)
    {
        if (path == "targetingKey")
        {
            value = context.TargetingKey;
            return !string.IsNullOrEmpty(context.TargetingKey);
        }
        if (path.Length == 0) { value = null; return false; }
        object? current = context.SafeAttributes;
        foreach (var part in path.Split('.'))
        {
            if (!TryGetMember(current, part, out current)) { value = null; return false; }
        }
        value = current;
        return true;
    }

    private static bool TryGetMember(object? current, string key, out object? value)
    {
        switch (current)
        {
            case IReadOnlyDictionary<string, object?> readOnly when readOnly.TryGetValue(key, out value): return true;
            case IDictionary<string, object?> dictionary when dictionary.TryGetValue(key, out value): return true;
            case JsonElement { ValueKind: JsonValueKind.Object } element when element.TryGetProperty(key, out var property): value = property.Clone(); return true;
            default: value = null; return false;
        }
    }

    private static bool EqualValues(object? actual, JsonElement expected)
    {
        if (TryNumber(actual, out var actualNumber) && expected.ValueKind == JsonValueKind.Number && expected.TryGetDouble(out var expectedNumber)) return actualNumber.Equals(expectedNumber);
        return expected.ValueKind switch
        {
            JsonValueKind.Null => actual is null || actual is JsonElement { ValueKind: JsonValueKind.Null },
            JsonValueKind.True => actual is true || actual is JsonElement { ValueKind: JsonValueKind.True },
            JsonValueKind.False => actual is false || actual is JsonElement { ValueKind: JsonValueKind.False },
            JsonValueKind.String => StringValue(actual) is { } stringValue && stringValue == expected.GetString(),
            JsonValueKind.Array => SequenceEquals(actual, expected),
            JsonValueKind.Object => ObjectEquals(actual, expected),
            _ => false,
        };
    }

    private static bool SequenceEquals(object? actual, JsonElement expected)
    {
        var values = EnumerateSequence(actual)?.ToList();
        if (values is null || values.Count != expected.GetArrayLength()) return false;
        var index = 0;
        foreach (var expectedValue in expected.EnumerateArray()) if (!EqualValues(values[index++], expectedValue)) return false;
        return true;
    }

    private static bool ObjectEquals(object? actual, JsonElement expected)
    {
        var dictionary = AsDictionary(actual);
        if (dictionary is null) return false;
        var expectedProperties = expected.EnumerateObject().ToList();
        if (dictionary.Count != expectedProperties.Count) return false;
        foreach (var property in expectedProperties)
            if (!dictionary.TryGetValue(property.Name, out var actualValue) || !EqualValues(actualValue, property.Value)) return false;
        return true;
    }

    private static bool ContainsValue(object? actual, JsonElement expected)
    {
        if (actual is string text && expected.ValueKind == JsonValueKind.String) return text.Contains(expected.GetString()!, StringComparison.Ordinal);
        var dictionary = AsDictionary(actual);
        if (dictionary is not null) return expected.ValueKind == JsonValueKind.String && dictionary.ContainsKey(expected.GetString()!);
        var sequence = EnumerateSequence(actual);
        return sequence is not null && sequence.Any(item => EqualValues(item, expected));
    }

    private static IEnumerable<object?>? EnumerateSequence(object? value)
    {
        if (value is string) return null;
        if (value is JsonElement { ValueKind: JsonValueKind.Array } element) return element.EnumerateArray().Select(item => (object?)item.Clone()).ToArray();
        if (value is IEnumerable enumerable)
        {
            var values = new List<object?>();
            foreach (var item in enumerable) values.Add(item);
            return values;
        }
        return null;
    }

    private static IReadOnlyDictionary<string, object?>? AsDictionary(object? value)
    {
        if (value is IReadOnlyDictionary<string, object?> readOnly) return readOnly;
        if (value is IDictionary<string, object?> dictionary) return new Dictionary<string, object?>(dictionary);
        if (value is JsonElement { ValueKind: JsonValueKind.Object } element) return element.EnumerateObject().ToDictionary(property => property.Name, property => (object?)property.Value.Clone(), StringComparer.Ordinal);
        return null;
    }

    private static bool CompareNumbers(object? actual, JsonElement expected, Func<int, bool> predicate)
    {
        if (!TryNumber(actual, out var left) || expected.ValueKind != JsonValueKind.Number || !expected.TryGetDouble(out var right)) return false;
        return predicate(left.CompareTo(right));
    }

    private static bool CompareSemver(object? actual, JsonElement expected, Func<int, bool> predicate)
    {
        var actualString = StringValue(actual);
        if (actualString is null || expected.ValueKind != JsonValueKind.String) return false;
        var comparison = Semver.Compare(actualString, expected.GetString()!);
        return comparison is not null && predicate(comparison.Value);
    }

    private static bool TryNumber(object? value, out double number)
    {
        try
        {
            switch (value)
            {
                case JsonElement { ValueKind: JsonValueKind.Number } element when element.TryGetDouble(out number): return true;
                case byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal:
                    number = Convert.ToDouble(value, CultureInfo.InvariantCulture); return double.IsFinite(number);
                default: number = default; return false;
            }
        }
        catch (OverflowException) { number = default; return false; }
    }

    private static string? StringValue(object? value) => value switch
    {
        string text => text,
        JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
        _ => null,
    };

    private static bool OutcomeEmpty(Outcome outcome) => string.IsNullOrWhiteSpace(outcome.Variant) && outcome.Rollout.Count == 0;
    private static RawEvaluationDetails Result(JsonElement value, string variant, EvaluationReason reason) => new(value.Clone(), variant, reason);
    private static RawEvaluationDetails Error(FeatureFlag flag, EvaluationErrorCode code, string message) => new(flag.DefaultValue.Clone(), "default", EvaluationReason.Error, ErrorCode: code, ErrorMessage: message);
    private static JsonElement JsonBoolean(bool value) => JsonDocument.Parse(value ? "true" : "false").RootElement.Clone();
}
