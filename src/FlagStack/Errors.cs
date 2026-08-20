namespace FlagStack;

public class FlagStackException : Exception
{
    public FlagStackException(string message) : base(message) { }
    public FlagStackException(string message, Exception innerException) : base(message, innerException) { }
}

public sealed class FlagStackAuthenticationException : FlagStackException
{
    public FlagStackAuthenticationException(string message) : base(message) { }
}

public sealed class FlagStackHttpException : FlagStackException
{
    public FlagStackHttpException(int statusCode, string message) : base(message) => StatusCode = statusCode;
    public int StatusCode { get; }
}

public sealed class FlagStackConfigurationException : FlagStackException
{
    public FlagStackConfigurationException(string message) : base(message) { }
    public FlagStackConfigurationException(string message, Exception innerException) : base(message, innerException) { }
}

internal sealed class EvaluationFailure : Exception
{
    internal EvaluationFailure(EvaluationErrorCode code, string message) : base(message) => Code = code;
    internal EvaluationErrorCode Code { get; }
}
