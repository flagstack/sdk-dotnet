using System.Globalization;
using System.Text.RegularExpressions;

namespace FlagStack;

internal static partial class Semver
{
    [GeneratedRegex(@"^v(0|[1-9]\d*)(?:\.(0|[1-9]\d*))?(?:\.(0|[1-9]\d*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?)?$", RegexOptions.CultureInvariant)]
    private static partial Regex SemverRegex();

    internal static int? Compare(string left, string right)
    {
        var leftVersion = Parse(left);
        var rightVersion = Parse(right);
        if (leftVersion is null || rightVersion is null) return null;

        var comparison = CompareInteger(leftVersion.Value.Major, rightVersion.Value.Major);
        if (comparison != 0) return comparison;
        comparison = CompareInteger(leftVersion.Value.Minor, rightVersion.Value.Minor);
        if (comparison != 0) return comparison;
        comparison = CompareInteger(leftVersion.Value.Patch, rightVersion.Value.Patch);
        if (comparison != 0) return comparison;
        return ComparePrerelease(leftVersion.Value.Prerelease, rightVersion.Value.Prerelease);
    }

    private static ParsedSemver? Parse(string value)
    {
        var normalized = value.Trim();
        if (!normalized.StartsWith('v')) normalized = "v" + normalized;
        var match = SemverRegex().Match(normalized);
        if (!match.Success) return null;

        var prerelease = match.Groups[4].Success
            ? match.Groups[4].Value.Split('.')
            : [];
        if (prerelease.Any(identifier => identifier.Length > 1 && identifier[0] == '0' && identifier.All(char.IsDigit)))
        {
            return null;
        }

        return new ParsedSemver(
            match.Groups[1].Value,
            match.Groups[2].Success ? match.Groups[2].Value : "0",
            match.Groups[3].Success ? match.Groups[3].Value : "0",
            prerelease);
    }

    private static int CompareInteger(string left, string right)
    {
        if (left.Length != right.Length) return left.Length.CompareTo(right.Length);
        return string.CompareOrdinal(left, right);
    }

    private static int ComparePrerelease(string[] left, string[] right)
    {
        if (left.Length == 0 && right.Length == 0) return 0;
        if (left.Length == 0) return 1;
        if (right.Length == 0) return -1;

        for (var index = 0; index < Math.Min(left.Length, right.Length); index++)
        {
            if (left[index] == right[index]) continue;
            var leftNumeric = left[index].All(char.IsDigit);
            var rightNumeric = right[index].All(char.IsDigit);
            if (leftNumeric != rightNumeric) return leftNumeric ? -1 : 1;
            if (leftNumeric) return CompareInteger(left[index], right[index]);
            return string.CompareOrdinal(left[index], right[index]);
        }
        return left.Length.CompareTo(right.Length);
    }

    private readonly record struct ParsedSemver(string Major, string Minor, string Patch, string[] Prerelease);
}
