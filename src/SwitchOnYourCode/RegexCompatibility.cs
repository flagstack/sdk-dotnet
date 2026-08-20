using System.Text.RegularExpressions;

namespace SwitchOnYourCode;

internal static class RegexCompatibility
{
    private static readonly string[] UnsupportedTokens =
    [
        "(?=", "(?!", "(?<=", "(?<!", "(?>", "(?(", "\\k<", "\\k'",
    ];

    internal static Regex Compile(string pattern)
    {
        if (pattern.Contains("[[:", StringComparison.Ordinal))
            throw new EvaluationFailure(EvaluationErrorCode.ParseError, "regular expression uses RE2 POSIX character classes that are not supported by the .NET SDK");
        if (UnsupportedTokens.Any(token => pattern.Contains(token, StringComparison.Ordinal)) || Regex.IsMatch(pattern, @"\\[1-9]", RegexOptions.CultureInvariant))
        {
            throw new EvaluationFailure(EvaluationErrorCode.ParseError, "regular expression uses syntax unsupported by RE2");
        }

        // Go's RE2 accepts Python-style named groups. Translate the spelling to the .NET equivalent.
        var translated = Regex.Replace(pattern, @"\(\?P<([A-Za-z][A-Za-z0-9_]*)>", "(?<$1>", RegexOptions.CultureInvariant);
        try
        {
            return new Regex(
                translated,
                RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
                TimeSpan.FromMilliseconds(100));
        }
        catch (ArgumentException exception)
        {
            throw new EvaluationFailure(EvaluationErrorCode.ParseError, exception.Message);
        }
    }
}
