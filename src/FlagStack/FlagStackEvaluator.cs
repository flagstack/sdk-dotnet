using System.Text.Json;

namespace FlagStack;

public static class FlagStackEvaluator
{
    public static int Bucket(string environmentId, string flagId, string bucketValue) =>
        Evaluator.Bucket(environmentId, flagId, bucketValue);

    public static EvaluationDetails<JsonElement> Evaluate(
        FeatureFlag flag,
        string environmentId,
        EvaluationContext? context = null,
        IReadOnlyList<Segment>? segments = null)
    {
        ArgumentNullException.ThrowIfNull(flag);
        var result = Evaluator.Evaluate(flag, environmentId, context, segments ?? []);
        return new EvaluationDetails<JsonElement>(
            result.Value.Clone(), result.Variant, result.Reason, result.RuleId, result.ErrorCode, result.ErrorMessage);
    }
}
