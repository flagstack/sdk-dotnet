using System.Text.Json;

namespace SwitchOnYourCode.Tests;

public sealed class EvaluatorTests
{
    [Fact]
    public void Bucket_matches_reference_vector() => Assert.Equal(3837, SwitchOnYourCodeEvaluator.Bucket("env-1", "flag-1", "user-123"));

    [Fact]
    public void Disabled_boolean_returns_default()
    {
        var configuration = TestConfiguration.Parse(TestConfiguration.BooleanConfiguration(enabled: false));
        var details = SwitchOnYourCodeEvaluator.Evaluate(configuration.Flags[0], "env-1");
        Assert.False(details.Value.GetBoolean());
        Assert.Equal("default", details.Variant);
        Assert.Equal(EvaluationReason.Disabled, details.Reason);
    }

    [Fact]
    public void Enabled_boolean_without_policy_returns_on()
    {
        var configuration = TestConfiguration.Parse(TestConfiguration.BooleanConfiguration());
        var details = SwitchOnYourCodeEvaluator.Evaluate(configuration.Flags[0], "env-1");
        Assert.True(details.Value.GetBoolean());
        Assert.Equal("on", details.Variant);
        Assert.Equal(EvaluationReason.Static, details.Reason);
    }

    [Fact]
    public void Ordered_rules_support_transitive_segments()
    {
        var configuration = TestConfiguration.Parse("""
        {"schema_version":1,"environment":{"id":"env-1","key":"production"},"flags":[{"id":"flag-1","key":"new-checkout","kind":"boolean","default_value":false,"enabled":true,"variants":[],"revision":1,"policy":{"rules":[{"id":"staff-rule","match":"all","conditions":[{"operator":"in_segment","value":"staff"}],"outcome":{"variant":"on"}}],"fallthrough":{"variant":"off"}}}],"segments":[{"key":"staff","name":"Staff","match":"all","conditions":[{"operator":"in_segment","value":"internal"}]},{"key":"internal","name":"Internal","match":"all","conditions":[{"attribute":"profile.email","operator":"ends_with","value":"@example.com"}]}]}
        """);
        var context = new EvaluationContext("user-1", new Dictionary<string, object?> { ["profile"] = new Dictionary<string, object?> { ["email"] = "adam@example.com" } });
        var details = SwitchOnYourCodeEvaluator.Evaluate(configuration.Flags[0], "env-1", context, configuration.Segments);
        Assert.True(details.Value.GetBoolean());
        Assert.Equal(EvaluationReason.TargetingMatch, details.Reason);
        Assert.Equal("staff-rule", details.RuleId);
    }

    [Fact]
    public void Percentage_rollout_is_stable()
    {
        var configuration = TestConfiguration.Parse("""
        {"schema_version":1,"environment":{"id":"env-1","key":"production"},"flags":[{"id":"flag-1","key":"new-checkout","kind":"boolean","default_value":false,"enabled":true,"variants":[],"revision":1,"policy":{"fallthrough":{"rollout":[{"variant":"on","weight":25000},{"variant":"off","weight":75000}]}}}],"segments":[]}
        """);
        var details = SwitchOnYourCodeEvaluator.Evaluate(configuration.Flags[0], "env-1", new EvaluationContext("user-123"));
        Assert.True(details.Value.GetBoolean());
        Assert.Equal(EvaluationReason.Split, details.Reason);
    }

    [Fact]
    public void Missing_targeting_key_fails_safely()
    {
        var configuration = TestConfiguration.Parse("""
        {"schema_version":1,"environment":{"id":"env-1","key":"production"},"flags":[{"id":"flag-1","key":"new-checkout","kind":"boolean","default_value":false,"enabled":true,"variants":[],"revision":1,"policy":{"fallthrough":{"rollout":[{"variant":"on","weight":50000},{"variant":"off","weight":50000}]}}}],"segments":[]}
        """);
        var details = SwitchOnYourCodeEvaluator.Evaluate(configuration.Flags[0], "env-1");
        Assert.Equal(EvaluationReason.Error, details.Reason);
        Assert.Equal(EvaluationErrorCode.TargetingKeyMissing, details.ErrorCode);
        Assert.False(details.Value.GetBoolean());
    }

    [Fact]
    public void Regex_supports_RE2_inline_flags()
    {
        var configuration = TestConfiguration.Parse("""
        {"schema_version":1,"environment":{"id":"env-1","key":"production"},"flags":[{"id":"flag-1","key":"new-checkout","kind":"boolean","default_value":false,"enabled":true,"variants":[],"revision":1,"policy":{"rules":[{"id":"staff-email","match":"all","conditions":[{"attribute":"email","operator":"matches_regex","value":"(?i)@example\\.com$"}],"outcome":{"variant":"on"}}],"fallthrough":{"variant":"off"}}}],"segments":[]}
        """);
        var details = SwitchOnYourCodeEvaluator.Evaluate(configuration.Flags[0], "env-1", new EvaluationContext(Attributes: new Dictionary<string, object?> { ["email"] = "Adam@EXAMPLE.COM" }));
        Assert.True(details.Value.GetBoolean());
    }

    [Fact]
    public void Semver_supports_shorthand()
    {
        var configuration = TestConfiguration.Parse("""
        {"schema_version":1,"environment":{"id":"env-1","key":"production"},"flags":[{"id":"flag-1","key":"new-checkout","kind":"boolean","default_value":false,"enabled":true,"variants":[],"revision":1,"policy":{"rules":[{"id":"modern","match":"all","conditions":[{"attribute":"app_version","operator":"semver_greater_than_or_equal","value":"2.4"}],"outcome":{"variant":"on"}}],"fallthrough":{"variant":"off"}}}],"segments":[]}
        """);
        var details = SwitchOnYourCodeEvaluator.Evaluate(configuration.Flags[0], "env-1", new EvaluationContext(Attributes: new Dictionary<string, object?> { ["app_version"] = "v2.4.1" }));
        Assert.True(details.Value.GetBoolean());
    }

    [Theory]
    [InlineData(1.0, "1")]
    [InlineData(0.0000001, "1e-7")]
    [InlineData(0.000001, "0.000001")]
    [InlineData(1e20, "100000000000000000000")]
    [InlineData(1e21, "1e+21")]
    public void Custom_bucket_numbers_match_Go_JSON(double value, string expected) => Assert.Equal(expected, JsonCompatibility.ScalarBucketValue(value));

    [Fact]
    public void Custom_bucket_strings_match_Go_JSON_escaping()
    {
        Assert.Equal("\"\\u003c\\u0026\\u003e\"", JsonCompatibility.ScalarBucketValue("<&>"));
        Assert.Equal("\"café\"", JsonCompatibility.ScalarBucketValue("café"));
        Assert.Equal("\"\\u2028\\u2029\"", JsonCompatibility.ScalarBucketValue("\u2028\u2029"));
    }

    [Fact]
    public void Segment_cycle_fails_safely()
    {
        var configuration = TestConfiguration.Parse("""
        {"schema_version":1,"environment":{"id":"env-1","key":"production"},"flags":[{"id":"flag-1","key":"new-checkout","kind":"boolean","default_value":false,"enabled":true,"variants":[],"revision":1,"policy":{"rules":[{"id":"cycle","match":"all","conditions":[{"operator":"in_segment","value":"a"}],"outcome":{"variant":"on"}}]}}],"segments":[{"key":"a","name":"A","match":"all","conditions":[{"operator":"in_segment","value":"b"}]},{"key":"b","name":"B","match":"all","conditions":[{"operator":"in_segment","value":"a"}]}]}
        """);
        var details = SwitchOnYourCodeEvaluator.Evaluate(configuration.Flags[0], "env-1", segments: configuration.Segments);
        Assert.Equal(EvaluationReason.Error, details.Reason);
        Assert.Equal(EvaluationErrorCode.ParseError, details.ErrorCode);
    }
}
