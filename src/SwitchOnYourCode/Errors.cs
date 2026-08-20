namespace SwitchOnYourCode;

public class SwitchOnYourCodeException : Exception
{
    public SwitchOnYourCodeException(string message) : base(message) { }
    public SwitchOnYourCodeException(string message, Exception innerException) : base(message, innerException) { }
}

public sealed class SwitchOnYourCodeAuthenticationException : SwitchOnYourCodeException
{
    public SwitchOnYourCodeAuthenticationException(string message) : base(message) { }
}

public sealed class SwitchOnYourCodeHttpException : SwitchOnYourCodeException
{
    public SwitchOnYourCodeHttpException(int statusCode, string message) : base(message) => StatusCode = statusCode;
    public int StatusCode { get; }
}

public sealed class SwitchOnYourCodeConfigurationException : SwitchOnYourCodeException
{
    public SwitchOnYourCodeConfigurationException(string message) : base(message) { }
    public SwitchOnYourCodeConfigurationException(string message, Exception innerException) : base(message, innerException) { }
}

internal sealed class EvaluationFailure : Exception
{
    internal EvaluationFailure(EvaluationErrorCode code, string message) : base(message) => Code = code;
    internal EvaluationErrorCode Code { get; }
}
