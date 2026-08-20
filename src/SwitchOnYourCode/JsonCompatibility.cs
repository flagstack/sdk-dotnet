using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SwitchOnYourCode;

internal static partial class JsonCompatibility
{
    private static readonly JsonSerializerOptions RelaxedJson = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    [GeneratedRegex(@"\\u([0-9A-Fa-f]{4})", RegexOptions.CultureInvariant)]
    private static partial Regex UnicodeEscapeRegex();

    internal static string ScalarBucketValue(object? value)
    {
        if (value is JsonElement element)
        {
            return element.ValueKind switch
            {
                JsonValueKind.String => FormatString(element.GetString()!),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Number => FormatJsonNumber(element.GetRawText()),
                _ => throw new EvaluationFailure(EvaluationErrorCode.InvalidContext, "bucket attribute must be a scalar string, boolean or number"),
            };
        }

        return value switch
        {
            string stringValue => FormatString(stringValue),
            bool booleanValue => booleanValue ? "true" : "false",
            byte number => number.ToString(CultureInfo.InvariantCulture),
            sbyte number => number.ToString(CultureInfo.InvariantCulture),
            short number => number.ToString(CultureInfo.InvariantCulture),
            ushort number => number.ToString(CultureInfo.InvariantCulture),
            int number => number.ToString(CultureInfo.InvariantCulture),
            uint number => number.ToString(CultureInfo.InvariantCulture),
            long number => number.ToString(CultureInfo.InvariantCulture),
            ulong number => number.ToString(CultureInfo.InvariantCulture),
            float number when float.IsFinite(number) => FormatFloating(number.ToString("R", CultureInfo.InvariantCulture), Math.Abs(number)),
            double number when double.IsFinite(number) => FormatFloating(number.ToString("R", CultureInfo.InvariantCulture), Math.Abs(number)),
            decimal number => FormatDecimal(number),
            _ => throw new EvaluationFailure(EvaluationErrorCode.InvalidContext, "bucket attribute must be a scalar string, boolean or number"),
        };
    }

    private static string FormatString(string value)
    {
        var encoded = JsonSerializer.Serialize(value, RelaxedJson)
            .Replace("<", "\\u003c", StringComparison.Ordinal)
            .Replace(">", "\\u003e", StringComparison.Ordinal)
            .Replace("&", "\\u0026", StringComparison.Ordinal)
            .Replace("\u2028", "\\u2028", StringComparison.Ordinal)
            .Replace("\u2029", "\\u2029", StringComparison.Ordinal);
        return UnicodeEscapeRegex().Replace(encoded, match => "\\u" + match.Groups[1].Value.ToLowerInvariant());
    }

    private static string FormatJsonNumber(string raw)
    {
        if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)) return raw;
        if (ulong.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)) return raw;
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number))
        {
            throw new EvaluationFailure(EvaluationErrorCode.InvalidContext, "bucket number is invalid");
        }
        return FormatFloating(number.ToString("R", CultureInfo.InvariantCulture), Math.Abs(number));
    }

    private static string FormatDecimal(decimal value)
    {
        if (value == 0m) return "0";
        var raw = value.ToString("G29", CultureInfo.InvariantCulture);
        return FormatFloating(raw, (double)Math.Abs(value));
    }

    private static string FormatFloating(string raw, double absolute)
    {
        if (absolute == 0d) return "0";
        raw = raw.Replace('E', 'e');
        var exponentIndex = raw.IndexOf('e');

        if (absolute < 1e-6 || absolute >= 1e21)
        {
            if (exponentIndex < 0) return raw.EndsWith(".0", StringComparison.Ordinal) ? raw[..^2] : raw;
            var coefficient = raw[..exponentIndex];
            var exponent = int.Parse(raw[(exponentIndex + 1)..], CultureInfo.InvariantCulture);
            return $"{coefficient}e{(exponent >= 0 ? "+" : string.Empty)}{exponent.ToString(CultureInfo.InvariantCulture)}";
        }

        if (exponentIndex < 0) return raw.EndsWith(".0", StringComparison.Ordinal) ? raw[..^2] : raw;
        return ExpandExponent(raw);
    }

    private static string ExpandExponent(string raw)
    {
        var exponentIndex = raw.IndexOf('e');
        var coefficient = raw[..exponentIndex];
        var exponent = int.Parse(raw[(exponentIndex + 1)..], CultureInfo.InvariantCulture);
        var negative = coefficient.StartsWith("-", StringComparison.Ordinal);
        if (negative) coefficient = coefficient[1..];
        var decimalPoint = coefficient.IndexOf('.');
        var integerDigits = decimalPoint < 0 ? coefficient.Length : decimalPoint;
        var digits = coefficient.Replace(".", string.Empty, StringComparison.Ordinal);
        var targetPosition = integerDigits + exponent;
        string result;
        if (targetPosition <= 0)
        {
            result = "0." + new string('0', -targetPosition) + digits;
        }
        else if (targetPosition >= digits.Length)
        {
            result = digits + new string('0', targetPosition - digits.Length);
        }
        else
        {
            result = digits[..targetPosition] + "." + digits[targetPosition..];
        }
        return negative ? "-" + result : result;
    }
}
