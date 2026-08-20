using System.Text.Json;

namespace SwitchOnYourCode;

internal static class ConfigurationParser
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
    };

    internal static SwitchOnYourCodeConfiguration Parse(ReadOnlySpan<byte> json)
    {
        try
        {
            var configuration = JsonSerializer.Deserialize<SwitchOnYourCodeConfiguration>(json, JsonOptions)
                ?? throw new SwitchOnYourCodeConfigurationException("SwitchOnYourCode configuration must be an object.");
            ConfigurationValidator.Validate(configuration);
            return configuration;
        }
        catch (SwitchOnYourCodeConfigurationException)
        {
            throw;
        }
        catch (EvaluationFailure exception)
        {
            throw new SwitchOnYourCodeConfigurationException($"SwitchOnYourCode configuration is not compatible with the v1 evaluator: {exception.Message}", exception);
        }
        catch (JsonException exception)
        {
            throw new SwitchOnYourCodeConfigurationException($"SwitchOnYourCode configuration response was not valid JSON: {exception.Message}", exception);
        }
    }

    internal static SwitchOnYourCodeConfiguration Clone(SwitchOnYourCodeConfiguration configuration) =>
        Parse(JsonSerializer.SerializeToUtf8Bytes(configuration, JsonOptions));
}
